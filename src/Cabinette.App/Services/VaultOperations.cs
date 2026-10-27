using System.Globalization;
using System.IO;
using System.Windows;
using Cabinette.App.Localization;
using Cabinette.App.Services.Ai;
using Cabinette.App.Services.Monitor;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using Cabinette.Windows;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Cabinette.App.Services;

/// <summary>Files written to the old vault after a move: up to a few relative paths, and how many there are in all.</summary>
public sealed record OldVaultChanges(IReadOnlyList<string> RelativePaths, int Count);

/// <summary>
/// The user flows for the stage 2 vault operations (SPEC section 8): move the vault, copy linked files into it
/// (migrate), create a backup and restore one. Each flow picks paths, confirms, runs the Core operation off the UI
/// thread behind a cancellable progress dialog and shows the result. Stage 4 adds the same operations started by an AI
/// (<see cref="IAiVaultOperations"/>: no picker, no confirmation, no result dialog). Call on the UI thread.
/// </summary>
public sealed class VaultOperationsService : IAiVaultOperations
{
    private const int PointerQueryLimit = 500;

    /// <summary>How many changed files the warning before recycling the old vault lists by name.</summary>
    internal const int OldVaultChangesListed = 20;

    /// <summary>Prefix of Cabinette's own temp files and folders in a vault (Core's NameRules.CabinetteTempPrefix).</summary>
    private const string CabinetteTempPrefix = ".cabinette-";

    private static readonly TimeSpan CountBudget = TimeSpan.FromSeconds(3);

    private readonly ILibraryService _library;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IRecycleBin _recycleBin;
    private readonly IShellOperations _shell;
    private readonly AppPaths _paths;
    private readonly VaultMonitorHost _monitor;
    private readonly AppLifetime _lifetime;
    private readonly IDateTakenReader? _dateTakenReader;

    public VaultOperationsService(
        ILibraryService library,
        DialogService dialogs,
        INotificationService notifications,
        IRecycleBin recycleBin,
        IShellOperations shell,
        AppPaths paths,
        VaultMonitorHost monitor,
        AppLifetime lifetime,
        IDateTakenReader? dateTakenReader = null)
    {
        _library = library;
        _dialogs = dialogs;
        _notifications = notifications;
        _recycleBin = recycleBin;
        _shell = shell;
        _paths = paths;
        _monitor = monitor;
        _lifetime = lifetime;
        _dateTakenReader = dateTakenReader;
    }

    /// <summary>A vault operation is running (the buttons are disabled, quitting waits).</summary>
    public bool IsBusy => _lifetime.IsOperationRunning;

    /// <summary>Raised after the vault moved, so views showing the path can refresh.</summary>
    public event EventHandler? VaultMoved;

    // ================================================================== move

    public async Task MoveVaultAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_library.VaultPath is not { } current)
        {
            await ShowMessageAsync(L.Get("Vault_NoVaultTitle"), L.Get("Vault_NoVault"));
            return;
        }

        var target = await PickEmptyFolderAsync(L.Get("Vault_MovePickTitle"), Path.GetDirectoryName(current), current);
        if (target is null)
        {
            return;
        }

        var (confirm, contents) = BuildMoveConfirmDialog(current, target);
        using (var counting = new CancellationTokenSource())
        {
            _ = FillVaultContentsAsync(current, target, contents, confirm, counting.Token);
            var choice = await ShowAsync(confirm);
            await counting.CancelAsync();
            if (choice != OperationDialogChoice.Primary)
            {
                return;
            }
        }

        using var operation = _lifetime.BeginOperation();
        var progress = new ProgressDialogViewModel(L.Get("Vault_MoveProgressTitle"));
        MoveVaultResult result;
        var switchedAtUtc = DateTime.MinValue;
        try
        {
            result = await RunWithProgressAsync(progress, async () =>
            {
                var moved = await _library.MoveVaultAsync(target, progress, progress.Token);

                // Anything in the old vault written after this was not moved (checked before it is recycled).
                switchedAtUtc = DateTime.UtcNow;
                return moved;
            });
        }
        catch (OperationCanceledException)
        {
            _notifications.Show(L.Get("Vault_MoveCancelled"), NotificationKind.Info);
            return;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex, "Vault_MoveFailedTitle");
            return;
        }

        VaultMoved?.Invoke(this, EventArgs.Empty);
        var done = BuildMoveResultDialog(result);
        if (await ShowAsync(done) == OperationDialogChoice.Secondary && await ConfirmOldVaultUnchangedAsync(result.OldVaultPath, switchedAtUtc))
        {
            await RecycleOldVaultAsync(result);
        }
    }

    /// <summary>
    /// Before the old vault is recycled: files written to it after the switch are not in the new vault. If there are any,
    /// a warning lists them first; false keeps the old vault.
    /// </summary>
    private async Task<bool> ConfirmOldVaultUnchangedAsync(string oldVault, DateTime switchedAtUtc)
    {
        OldVaultChanges changes;
        try
        {
            changes = await Task.Run(() => FindFilesChangedSince(oldVault, switchedAtUtc, OldVaultChangesListed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Recycling reports what is wrong with the folder.
            AppLog.Warn($"Checking the old vault '{oldVault}' for changes failed.", ex);
            return true;
        }

        return changes.Count == 0 || await ShowAsync(BuildOldVaultChangedDialog(oldVault, changes)) == OperationDialogChoice.Primary;
    }

    /// <summary>
    /// Files below <paramref name="folder"/> written or created after <paramref name="sinceUtc"/>: their relative paths (at most
    /// <paramref name="maxListed"/>) and how many there are. Cabinette's own ".cabinette-*" files and folders are skipped and links
    /// are never followed. Call off the UI thread.
    /// </summary>
    internal static OldVaultChanges FindFilesChangedSince(string folder, DateTime sinceUtc, int maxListed, CancellationToken ct = default)
    {
        var listed = new List<string>();
        var count = 0;
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            return new OldVaultChanges(listed, 0);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                if (entry.Name.StartsWith(CabinetteTempPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry is DirectoryInfo dir)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(dir);
                    }
                }
                else if (entry.LastWriteTimeUtc > sinceUtc || entry.CreationTimeUtc > sinceUtc)
                {
                    count++;
                    if (listed.Count < maxListed)
                    {
                        listed.Add(Path.GetRelativePath(folder, entry.FullName));
                    }
                }
            }
        }

        listed.Sort(StringComparer.OrdinalIgnoreCase);
        return new OldVaultChanges(listed, count);
    }

    /// <summary>"Files changed in the old vault": lists them; "Recycle anyway" (destructive) or Cancel, which keeps the old vault.</summary>
    public static OperationDialogViewModel BuildOldVaultChangedDialog(string oldVault, OldVaultChanges changes)
    {
        var details = changes.RelativePaths.ToList();
        if (changes.Count > details.Count)
        {
            details.Add(L.Format("Vault_OldChangedMore", N(changes.Count - details.Count)));
        }

        var dialog = new OperationDialogViewModel(L.Get("Vault_OldChangedTitle"), L.Get("Vault_RecycleAnyway"))
        {
            Symbol = SymbolRegular.Warning24,
            Tone = DialogTone.Warning,
            Message = L.Get("Vault_OldChangedMessage"),
            DetailsTitle = L.Get("Vault_OldChangedDetailsTitle"),
            Details = details,
            IsPrimaryDestructive = true,
            CancelText = L.Get("Vault_KeepOldVault"),
        };
        dialog.AddFact(L.Get("Vault_FactOldVault"), oldVault);
        dialog.AddFact(L.Get("Vault_FactChanged"), N(changes.Count));
        return dialog;
    }

    /// <summary>"Move the vault?" with from/to and a contents row that is filled in once the files are counted.</summary>
    public static (OperationDialogViewModel Dialog, FactViewModel Contents) BuildMoveConfirmDialog(string current, string target)
    {
        var dialog = new OperationDialogViewModel(L.Get("Vault_MoveConfirmTitle"), L.Get("Vault_MoveConfirm"))
        {
            Symbol = SymbolRegular.FolderArrowRight24,
            Message = L.Get("Vault_MoveConfirmMessage"),
        };
        dialog.AddFact(L.Get("Vault_FactFrom"), current);
        dialog.AddFact(L.Get("Vault_FactTo"), target);
        var contents = dialog.AddFact(L.Get("Vault_FactContents"), L.Get("Vault_Counting"));
        return (dialog, contents);
    }

    /// <summary>"Vault moved" with "Keep it" (safe default) and "Send old vault to Recycle Bin".</summary>
    public static OperationDialogViewModel BuildMoveResultDialog(MoveVaultResult result)
    {
        var canRecycle = !FolderStats.IsSameOrInside(result.NewVaultPath, result.OldVaultPath) &&
                         !FolderStats.IsSameOrInside(result.OldVaultPath, result.NewVaultPath);
        var dialog = new OperationDialogViewModel(L.Get("Vault_MovedTitle"), L.Get("Vault_KeepOld"))
        {
            Symbol = SymbolRegular.CheckmarkCircle24,
            Tone = DialogTone.Success,
            Message = L.Get(canRecycle ? "Vault_MovedMessage" : "Vault_MovedMessageNoRecycle"),
            SecondaryText = canRecycle ? L.Get("Vault_RecycleOld") : null,
            ShowCancel = false,
        };
        dialog.AddFact(L.Get("Vault_FactNewVault"), result.NewVaultPath);
        dialog.AddFact(L.Get("Vault_FactCopied"), FilesAndSize(result.FilesCopied, result.BytesCopied));
        dialog.AddFact(L.Get("Vault_FactOldVault"), result.OldVaultPath);
        return dialog;
    }

    private async Task RecycleOldVaultAsync(MoveVaultResult result)
    {
        try
        {
            await Task.Run(() => _recycleBin.SendToRecycleBin([result.OldVaultPath]));
            _notifications.Show(L.Get("Vault_OldRecycled"), NotificationKind.Success);
        }
        catch (RecycleBinUnavailableException ex)
        {
            // Nothing was deleted. The old vault is on a drive without a Recycle Bin (or too big for it): ask first.
            AppLog.Info($"The old vault can't go to the Recycle Bin: {ex.Message}");
            if (await RecycleBinFallback.ConfirmPermanentDeleteAsync(_dialogs, result.OldVaultPath))
            {
                await DeleteOldVaultPermanentlyAsync(result.OldVaultPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("Recycling the old vault failed.", ex);
            _notifications.Show(L.Format("Vault_OldRecycleFailed", ex.Message), NotificationKind.Error);
        }
    }

    /// <summary>Deletes the old vault folder for good (after the user confirmed). Never the current vault.</summary>
    private async Task DeleteOldVaultPermanentlyAsync(string oldVault)
    {
        try
        {
            if (_library.VaultPath is { } current &&
                (FolderStats.IsSameOrInside(current, oldVault) || FolderStats.IsSameOrInside(oldVault, current)))
            {
                throw new InvalidOperationException("The old vault overlaps the current one.");
            }

            await Task.Run(() => DeleteTree(oldVault));
            _notifications.Show(L.Get("Vault_OldDeleted"), NotificationKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("Deleting the old vault failed.", ex);
            _notifications.Show(L.Format("Vault_OldDeleteFailed", ex.Message), NotificationKind.Error);
        }
    }

    /// <summary>Recursive delete that also removes read-only files. Links inside are removed, never followed.</summary>
    internal static void DeleteTree(string path)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists)
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        foreach (var entry in root.EnumerateFileSystemInfos("*", options))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        root.Delete(recursive: true);
    }

    private async Task FillVaultContentsAsync(string vault, string target, FactViewModel fact, OperationDialogViewModel dialog, CancellationToken ct)
    {
        var stats = await Task.Run(() => FolderStats.Measure(vault, CountBudget, ct), CancellationToken.None);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (stats is null)
        {
            fact.Value = L.Get("Vault_CountTooLong");
            return;
        }

        fact.Value = FilesAndSize(stats.Files, stats.Bytes);
        var free = await Task.Run(() => FolderStats.FreeSpace(target), CancellationToken.None);
        if (free is { } available && available < stats.Bytes && !ct.IsCancellationRequested)
        {
            dialog.Note = L.Format("Vault_NotEnoughSpace", Glyphs.FileSize(available), Glyphs.FileSize(stats.Bytes));
        }
    }

    // ================================================================== migrate

    public async Task MigrateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IReadOnlyList<LibraryItem> pointers;
        try
        {
            pointers = await LoadPointerItemsAsync();
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex, "Migrate_FailedTitle");
            return;
        }

        if (pointers.Count == 0)
        {
            await ShowMessageAsync(L.Get("Migrate_NoneTitle"), L.Get("Migrate_None"));
            return;
        }

        var missing = pointers.Count(p => p.IsMissing);
        var bytes = pointers.Where(p => !p.IsMissing).Sum(p => p.SizeBytes ?? 0);
        var confirm = new OperationDialogViewModel(L.Get("Migrate_ConfirmTitle"), L.Get("Migrate_Confirm"))
        {
            Symbol = SymbolRegular.ArrowDownload24,
            Message = L.Get("Migrate_ConfirmMessage"),
        };
        confirm.AddFact(L.Get("Migrate_FactLinked"), CountText(pointers.Count, pointers.Count >= PointerQueryLimit));
        confirm.AddFact(L.Get("Migrate_FactSize"), Glyphs.FileSize(bytes));
        if (missing > 0)
        {
            confirm.AddFact(L.Get("Migrate_FactMissing"), L.Format("Migrate_MissingValue", N(missing)));
        }

        if (_library.VaultPath is { } vault && await Task.Run(() => FolderStats.FreeSpace(vault)) is { } free && free < bytes)
        {
            confirm.Note = L.Format("Vault_NotEnoughSpace", Glyphs.FileSize(free), Glyphs.FileSize(bytes));
        }

        if (await ShowAsync(confirm) != OperationDialogChoice.Primary)
        {
            return;
        }

        using var operation = _lifetime.BeginOperation();
        var progress = new ProgressDialogViewModel(L.Get("Migrate_ProgressTitle"));
        MigrateResult result;
        try
        {
            result = await RunWithProgressAsync(progress, () => _library.MigratePointersAsync(progress, progress.Token));
        }
        catch (OperationCanceledException)
        {
            _notifications.Show(L.Get("Migrate_Cancelled"), NotificationKind.Info);
            return;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex, "Migrate_FailedTitle");
            return;
        }

        await ShowAsync(BuildMigrateResultDialog(result));
    }

    public static OperationDialogViewModel BuildMigrateResultDialog(MigrateResult result)
    {
        var details = result.SkippedMissing.Select(i => L.Format("Migrate_DetailMissing", i.FileName, i.FullPath))
            .Concat(result.Failures.Select(f => L.Format("Migrate_DetailFailed", Path.GetFileName(f.SourcePath), f.Message)))
            .ToList();
        var clean = result.Failures.Count == 0 && result.SkippedMissing.Count == 0;
        var dialog = new OperationDialogViewModel(L.Get("Migrate_DoneTitle"), L.Get("Common_Done"))
        {
            Symbol = clean ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24,
            Tone = clean ? DialogTone.Success : DialogTone.Warning,
            Message = L.Get(clean ? "Migrate_DoneMessage" : "Migrate_DonePartialMessage"),
            DetailsTitle = details.Count > 0 ? L.Get("Migrate_DetailsTitle") : null,
            Details = details,
            ShowCancel = false,
        };
        dialog.AddFact(L.Get("Migrate_FactCopied"), N(result.Migrated));
        if (result.SkippedMissing.Count > 0)
        {
            dialog.AddFact(L.Get("Migrate_FactSkipped"), N(result.SkippedMissing.Count));
        }

        if (result.Failures.Count > 0)
        {
            dialog.AddFact(L.Get("Migrate_FactFailed"), N(result.Failures.Count));
        }

        return dialog;
    }

    // ================================================================== backup

    public async Task CreateBackupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var vault = _library.VaultPath;
        int pointerCount;
        try
        {
            pointerCount = (await LoadPointerItemsAsync()).Count;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex, "Backup_FailedTitle");
            return;
        }

        var migrateFirst = false;
        if (pointerCount > 0)
        {
            var options = BuildBackupOptionsDialog(pointerCount);
            if (await ShowAsync(options) != OperationDialogChoice.Primary)
            {
                return;
            }

            migrateFirst = options.IsChecked;
        }

        var zip = await PickBackupFileAsync(vault);
        if (zip is null)
        {
            return;
        }

        using var operation = _lifetime.BeginOperation();
        var progress = new ProgressDialogViewModel(L.Get("Backup_ProgressTitle"));
        var shown = _dialogs.ShowAsync(progress);
        MigrateResult? migrated = null;
        BackupResult result;
        try
        {
            if (migrateFirst)
            {
                progress.BeginStep(1, 2, L.Get("Backup_StepMigrate"));
                migrated = await Task.Run(() => _library.MigratePointersAsync(progress, progress.Token));
                progress.BeginStep(2, 2, L.Get("Backup_StepZip"));
            }

            result = await Task.Run(() => _library.CreateBackupAsync(zip, progress, progress.Token));
        }
        catch (OperationCanceledException)
        {
            await CloseProgressAsync(progress, shown);
            _notifications.Show(L.Get("Backup_Cancelled"), NotificationKind.Info);
            return;
        }
        catch (Exception ex)
        {
            await CloseProgressAsync(progress, shown);
            await HandleFailureAsync(ex, "Backup_FailedTitle");
            return;
        }

        await CloseProgressAsync(progress, shown);
        var done = BuildBackupResultDialog(result, migrated);
        if (await ShowAsync(done) == OperationDialogChoice.Secondary)
        {
            try
            {
                _shell.ShowInExplorer(result.ZipPath);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Showing the backup in Explorer failed.", ex);
                ShellLauncher.OpenFolder(_shell, Path.GetDirectoryName(result.ZipPath) ?? result.ZipPath);
            }
        }
    }

    public static OperationDialogViewModel BuildBackupOptionsDialog(int pointerCount) => new(L.Get("Backup_OptionsTitle"), L.Get("Backup_Continue"))
    {
        Symbol = SymbolRegular.Archive24,
        Message = L.Get("Backup_OptionsMessage"),
        CheckText = L.Get("Backup_MigrateFirst"),
        CheckHint = pointerCount == 1
            ? L.Get("Backup_MigrateFirstHintOne")
            : L.Format("Backup_MigrateFirstHint", CountText(pointerCount, pointerCount >= PointerQueryLimit)),
        IsChecked = true,
    };

    public static OperationDialogViewModel BuildBackupResultDialog(BackupResult result, MigrateResult? migrated)
    {
        var dialog = new OperationDialogViewModel(L.Get("Backup_DoneTitle"), L.Get("Common_Done"))
        {
            Symbol = SymbolRegular.CheckmarkCircle24,
            Tone = DialogTone.Success,
            Message = L.Get("Backup_DoneMessage"),
            SecondaryText = L.Get("Backup_ShowInFolder"),
            ShowCancel = false,
        };
        dialog.AddFact(L.Get("Backup_FactFile"), Path.GetFileName(result.ZipPath));
        dialog.AddFact(L.Get("Backup_FactFolder"), Path.GetDirectoryName(result.ZipPath) ?? "");
        dialog.AddFact(L.Get("Backup_FactContents"), FilesAndSize(result.FilesIncluded, result.BytesIncluded));
        if (migrated is { Migrated: > 0 })
        {
            dialog.AddFact(L.Get("Backup_FactMigrated"), N(migrated.Migrated));
        }

        if (result.PointerItemsNotIncluded > 0)
        {
            dialog.Note = result.PointerItemsNotIncluded == 1
                ? L.Get("Backup_PointersNotIncludedOne")
                : L.Format("Backup_PointersNotIncluded", N(result.PointerItemsNotIncluded));
        }

        return dialog;
    }

    private async Task<string?> PickBackupFileAsync(string? vault)
    {
        while (true)
        {
            var dialog = new SaveFileDialog
            {
                Title = L.Get("Backup_PickTitle"),
                Filter = $"{L.Get("Backup_Filter")}|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = L.Format("Backup_FileName", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(Owner) != true)
            {
                return null;
            }

            if (vault is null || !FolderStats.IsSameOrInside(dialog.FileName, vault))
            {
                return dialog.FileName;
            }

            await ShowMessageAsync(L.Get("Vault_FolderNotUsableTitle"), L.Get("Backup_InsideVault"));
        }
    }

    // ================================================================== restore

    /// <summary>
    /// Restore from a backup .zip into a new, empty vault folder, then restart. <paramref name="replacesLibrary"/> is false
    /// on the first-run screen (there is no library to replace yet).
    /// </summary>
    public async Task RestoreAsync(bool replacesLibrary)
    {
        if (IsBusy)
        {
            return;
        }

        var zip = PickBackupToRestore();
        if (zip is null)
        {
            return;
        }

        BackupInfo info;
        try
        {
            info = await Task.Run(() => LibraryFactory.ReadBackupInfoAsync(zip));
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex, "Restore_NotABackupTitle");
            return;
        }

        if (await ShowAsync(BuildRestoreDetailsDialog(zip, info)) != OperationDialogChoice.Primary)
        {
            return;
        }

        var folder = await PickEmptyFolderAsync(L.Get("Restore_PickFolderTitle"), null, replacesLibrary ? _library.VaultPath : null);
        if (folder is null)
        {
            return;
        }

        if (await ShowAsync(BuildRestoreConfirmDialog(zip, folder, replacesLibrary)) != OperationDialogChoice.Primary)
        {
            return;
        }

        using var operation = _lifetime.BeginOperation();
        var progress = new ProgressDialogViewModel(L.Get("Restore_ProgressTitle"));
        var shown = _dialogs.ShowAsync(progress);
        Exception? failure = null;
        var cancelled = false;
        try
        {
            // Release the database and the vault watcher first; RestoreAsync requires the library to be closed.
            progress.StageText = L.Get("Restore_Closing");
            await _monitor.DisposeAsync();
            await _library.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            var restored = await Task.Run(() => LibraryFactory.RestoreAsync(
                zip,
                folder,
                new LibraryOptions { DataDirectory = _paths.DataDirectory, DateTakenReader = _dateTakenReader },
                _recycleBin,
                progress,
                progress.Token));
            await restored.DisposeAsync();
            AppLog.Info($"Restored the backup '{zip}' into '{folder}'.");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            AppLog.Error("Restoring a backup failed.", ex);
        }
        finally
        {
            await CloseProgressAsync(progress, shown);
        }

        // The library is closed either way: restart to open whatever is now in the data folder.
        await ShowAsync(BuildRestoreOutcomeDialog(failure, cancelled));
        await _lifetime.RestartAsync();
    }

    public static OperationDialogViewModel BuildRestoreDetailsDialog(string zip, BackupInfo info)
    {
        var dialog = new OperationDialogViewModel(L.Get("Restore_DetailsTitle"), L.Get("Restore_ChooseFolder"))
        {
            Symbol = SymbolRegular.ArchiveArrowBack24,
            Message = L.Get("Restore_DetailsMessage"),
        };
        var culture = CultureInfo.CurrentCulture;
        dialog.AddFact(L.Get("Restore_FactBackup"), Path.GetFileName(zip));
        dialog.AddFact(L.Get("Restore_FactCreated"), L.Format("Restore_CreatedValue", info.CreatedAt.ToLocalTime().ToString("f", culture), info.MachineName));
        dialog.AddFact(L.Get("Restore_FactFiles"), FilesAndSize(info.FileCount, info.TotalBytes));
        dialog.AddFact(L.Get("Restore_FactCategories"), N(info.CategoryCount));
        if (info.PointerItems > 0)
        {
            dialog.AddFact(L.Get("Restore_FactLinked"), L.Format("Restore_LinkedValue", N(info.PointerItems)));
        }

        return dialog;
    }

    public static OperationDialogViewModel BuildRestoreConfirmDialog(string zip, string folder, bool replacesLibrary)
    {
        var dialog = replacesLibrary
            ? new OperationDialogViewModel(L.Get("Restore_ReplaceTitle"), L.Get("Restore_ReplaceConfirm"))
            {
                Symbol = SymbolRegular.Warning24,
                Tone = DialogTone.Danger,
                Message = L.Get("Restore_ReplaceMessage"),
                IsPrimaryDestructive = true,
            }
            : new OperationDialogViewModel(L.Get("Restore_ConfirmTitle"), L.Get("Restore_Confirm"))
            {
                Symbol = SymbolRegular.ArchiveArrowBack24,
                Message = L.Get("Restore_ConfirmMessage"),
            };
        dialog.AddFact(L.Get("Restore_FactBackup"), Path.GetFileName(zip));
        dialog.AddFact(L.Get("Restore_FactNewVault"), folder);
        return dialog;
    }

    private static OperationDialogViewModel BuildRestoreOutcomeDialog(Exception? failure, bool cancelled)
    {
        if (failure is null && !cancelled)
        {
            return new OperationDialogViewModel(L.Get("Restore_DoneTitle"), L.Get("Restore_RestartNow"))
            {
                Symbol = SymbolRegular.CheckmarkCircle24,
                Tone = DialogTone.Success,
                Message = L.Get("Restore_DoneMessage"),
                ShowCancel = false,
            };
        }

        var reason = failure switch
        {
            null => L.Get("Restore_CancelledMessage"),
            LibraryException library => library.Message,
            NotImplementedException or NotSupportedException => L.Get("Vault_Unavailable"),
            _ => L.Format("Vault_UnexpectedError", failure.Message),
        };
        return new OperationDialogViewModel(L.Get(cancelled ? "Restore_CancelledTitle" : "Restore_FailedTitle"), L.Get("Restore_RestartNow"))
        {
            Symbol = cancelled ? SymbolRegular.Info24 : SymbolRegular.ErrorCircle24,
            Tone = cancelled ? DialogTone.Warning : DialogTone.Danger,
            Message = $"{reason}\n\n{L.Get("Restore_RestartAfterFailure")}",
            ShowCancel = false,
        };
    }

    private static string? PickBackupToRestore()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Get("Restore_PickTitle"),
            Filter = $"{L.Get("Backup_Filter")}|*.zip|{L.Get("Intake_AllFiles")}|*.*",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    // ================================================================== started by an AI (stage 4, SPEC section 10)

    // No folder picker, no confirmation and no result dialog: the same checks as the UI, then the usual progress dialog, which
    // brings the window up so the user sees what runs and can cancel it. The AI follows the operation with get_operation_status
    // and the end is reported as an AI notice. Texts in replies are English (only the AI reads them).

    internal const string AiNoVaultMessage = "Cabinette has no vault yet: the user must finish first-run setup in the Cabinette window.";

    internal const string AiCancelledMessage = "It was cancelled in Cabinette's progress dialog (by the user, or because Cabinette closed). Nothing changed.";

    /// <summary>Moves the vault to <paramref name="newVaultPath"/> (empty or new, not inside the vault). The old vault is kept.</summary>
    public Task<AiVaultOperationStart> StartMoveVaultForAiAsync(string newVaultPath, IProgress<VaultOperationProgress> status)
        => StartForAiAsync(
            "Vault_MoveProgressTitle",
            "Vault_MoveCancelled",
            status,
            vault => Task.Run(() => CheckMoveTargetForAi(newVaultPath, vault)),
            async (progress, ct) => new AiVaultOperationOutcome(AiVaultOperationState.Succeeded)
            {
                Moved = await _library.MoveVaultAsync(newVaultPath, progress, ct),
            },
            succeeded: () => VaultMoved?.Invoke(this, EventArgs.Empty));

    /// <summary>Copies every linked file into the vault. Refused when there is none.</summary>
    public Task<AiVaultOperationStart> StartMigrateForAiAsync(IProgress<VaultOperationProgress> status)
        => StartForAiAsync(
            "Migrate_ProgressTitle",
            "Migrate_Cancelled",
            status,
            async _ => (await LoadPointerItemsAsync()).Count == 0 ? "There are no linked (pointer) files to copy into the vault." : null,
            async (progress, ct) => new AiVaultOperationOutcome(AiVaultOperationState.Succeeded)
            {
                Migrated = await _library.MigratePointersAsync(progress, ct),
            });

    /// <summary>Writes a backup to a new .zip outside the vault, without migrating linked files first.</summary>
    public Task<AiVaultOperationStart> StartBackupForAiAsync(string zipPath, IProgress<VaultOperationProgress> status)
        => StartForAiAsync(
            "Backup_ProgressTitle",
            "Backup_Cancelled",
            status,
            vault => Task.Run(() => CheckBackupFileForAi(zipPath, vault)),
            async (progress, ct) => new AiVaultOperationOutcome(AiVaultOperationState.Succeeded)
            {
                Backup = await _library.CreateBackupAsync(zipPath, progress, ct),
            });

    private const string OverlapsForAiMessage = "The new vault folder can't be the current vault, be inside it or contain it.";

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> or inside it: as written, or once Windows resolved both to
    /// their final paths (<see cref="FinalPaths"/>: device prefixes, 8.3 short names, junctions and subst drives seen through, for
    /// the parts that exist), so another spelling of a folder inside the vault can't get past the AI's checks. Opens folders
    /// without reading them; the AI's checks run off the UI thread.
    /// </summary>
    internal static bool IsSameOrInsideAnySpelling(string path, string folder)
        => FolderStats.IsSameOrInside(path, folder)
           || (FinalPaths.TryResolve(path) is { } finalPath && FinalPaths.TryResolve(folder) is { } finalFolder
               && FolderStats.IsSameOrInside(finalPath, finalFolder));

    /// <summary>Null when the folder can become the vault (an AI's move), else why not, in English.</summary>
    internal static string? CheckMoveTargetForAi(string target, string currentVault)
    {
        try
        {
            if (File.Exists(target))
            {
                return $"\"{target}\" is a file. Choose an empty or new folder for the vault.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"The folder can't be used: {ex.Message}";
        }

        if (IsSameOrInsideAnySpelling(target, currentVault) || IsSameOrInsideAnySpelling(currentVault, target))
        {
            return OverlapsForAiMessage;
        }

        return InspectNewVaultFolder(target, currentVault, out var error) switch
        {
            NewVaultFolder.Overlaps => OverlapsForAiMessage,
            NewVaultFolder.NotEmpty => "The folder isn't empty. Choose an empty or new folder for the vault.",
            NewVaultFolder.Unreadable => $"The folder can't be used: {error}",
            _ => null,
        };
    }

    /// <summary>
    /// Null when an AI's backup may be written to <paramref name="zipPath"/>: outside the vault (as the UI requires), in a folder
    /// that exists, and not over an existing file (the UI asks before overwriting; an AI isn't asked). Else why not, in English.
    /// </summary>
    internal static string? CheckBackupFileForAi(string zipPath, string vault)
    {
        try
        {
            var full = Path.GetFullPath(zipPath);
            if (IsSameOrInsideAnySpelling(full, vault))
            {
                return "Save the backup outside the vault folder.";
            }

            var folder = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return $"The folder \"{folder}\" doesn't exist. Choose a .zip path in an existing folder.";
            }

            if (Directory.Exists(full))
            {
                return $"\"{full}\" is a folder. Choose a new .zip file name.";
            }

            if (File.Exists(full))
            {
                return $"\"{full}\" already exists. Choose a new file name: Cabinette doesn't overwrite files for an AI.";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"\"{zipPath}\" can't be used: {ex.Message}";
        }
    }

    /// <summary>
    /// Refuses while another operation runs, marks this one as running before anything else (so a second start is refused at
    /// once), runs <paramref name="check"/> and starts the work. UI thread.
    /// </summary>
    private async Task<AiVaultOperationStart> StartForAiAsync(
        string progressTitleKey,
        string cancelledKey,
        IProgress<VaultOperationProgress> status,
        Func<string, Task<string?>> check,
        Func<IProgress<VaultOperationProgress>, CancellationToken, Task<AiVaultOperationOutcome>> work,
        Action? succeeded = null)
    {
        if (IsBusy)
        {
            return AiVaultOperationStart.Refused(AiErrorCodes.Busy, AiCommandHandler.BusyMessage);
        }

        if (_library.VaultPath is not { } vault)
        {
            return AiVaultOperationStart.Refused(AiErrorCodes.NotReady, AiNoVaultMessage);
        }

        var operation = _lifetime.BeginOperation();
        string? problem;
        try
        {
            problem = await check(vault);
        }
        catch (Exception ex)
        {
            operation.Dispose();
            AppLog.Warn("Checking a vault operation an AI asked for failed.", ex);
            return AiVaultOperationStart.Refused(AiErrorCodes.Failed, $"Something went wrong: {ex.Message}");
        }

        if (problem is not null)
        {
            operation.Dispose();
            return AiVaultOperationStart.Refused(AiErrorCodes.Refused, problem);
        }

        return AiVaultOperationStart.Started(RunForAiAsync(operation, progressTitleKey, cancelledKey, status, work, succeeded));
    }

    /// <summary>The progress dialog, the work off the UI thread, and the outcome (never throws). Ends the operation when done.</summary>
    private async Task<AiVaultOperationOutcome> RunForAiAsync(
        IDisposable operation,
        string progressTitleKey,
        string cancelledKey,
        IProgress<VaultOperationProgress> status,
        Func<IProgress<VaultOperationProgress>, CancellationToken, Task<AiVaultOperationOutcome>> work,
        Action? succeeded)
    {
        using (operation)
        {
            var progress = new ProgressDialogViewModel(L.Get(progressTitleKey));
            var shown = _dialogs.ShowAsync(progress);
            _lifetime.ActivateMainWindow();

            AiVaultOperationOutcome outcome;
            try
            {
                var both = new ForwardingProgress(progress, status);
                outcome = await Task.Run(() => work(both, progress.Token));
            }
            catch (OperationCanceledException)
            {
                AppLog.Info($"A vault operation an AI started was cancelled ({progressTitleKey}).");
                outcome = new AiVaultOperationOutcome(AiVaultOperationState.Cancelled) { Error = AiCancelledMessage, UserMessage = L.Get(cancelledKey) };
            }
            catch (Exception ex)
            {
                outcome = FailedForAi(ex);
            }
            finally
            {
                await CloseProgressAsync(progress, shown);
            }

            if (outcome.State == AiVaultOperationState.Succeeded && succeeded is not null)
            {
                try
                {
                    succeeded();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Updating the app after a vault operation an AI started failed.", ex);
                }
            }

            return outcome;
        }
    }

    /// <summary>A failed operation, as <see cref="HandleFailureAsync"/> explains it to the user, plus an English text for the AI.</summary>
    private static AiVaultOperationOutcome FailedForAi(Exception ex)
    {
        switch (ex)
        {
            case NotImplementedException or NotSupportedException:
                AppLog.Info($"Vault operation not available: {ex.Message}");
                return new AiVaultOperationOutcome(AiVaultOperationState.Failed)
                {
                    Error = "This version of Cabinette can't do this yet.",
                    UserMessage = L.Get("Vault_Unavailable"),
                };
            case LibraryException library:
                AppLog.Info($"Vault operation refused: {library.Code}: {library.Message}");
                return new AiVaultOperationOutcome(AiVaultOperationState.Failed) { Error = library.Message, UserMessage = library.Message };
            default:
                AppLog.Error("Vault operation failed.", ex);
                return new AiVaultOperationOutcome(AiVaultOperationState.Failed)
                {
                    Error = $"Something went wrong: {ex.Message}",
                    UserMessage = L.Format("Vault_UnexpectedError", ex.Message),
                };
        }
    }

    /// <summary>Progress for the dialog and for the AI's status.</summary>
    private sealed class ForwardingProgress(IProgress<VaultOperationProgress> dialog, IProgress<VaultOperationProgress> status) : IProgress<VaultOperationProgress>
    {
        public void Report(VaultOperationProgress value)
        {
            dialog.Report(value);
            status.Report(value);
        }
    }

    // ================================================================== helpers

    /// <summary>Folder picker that insists on an empty (or new) folder outside <paramref name="mustNotBeInside"/>.</summary>
    private async Task<string?> PickEmptyFolderAsync(string title, string? initial, string? mustNotBeInside)
    {
        while (true)
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
            {
                dialog.InitialDirectory = initial;
            }

            if (dialog.ShowDialog(Owner) != true)
            {
                return null;
            }

            var path = dialog.FolderName;
            var error = await Task.Run(() => CheckEmptyFolder(path, mustNotBeInside));
            if (error is null)
            {
                return path;
            }

            await ShowMessageAsync(L.Get("Vault_FolderNotUsableTitle"), error);
            initial = Path.GetDirectoryName(path);
        }
    }

    /// <summary>Null when the folder can hold a new vault, else why not.</summary>
    public static string? CheckEmptyFolder(string path, string? mustNotBeInside) => InspectNewVaultFolder(path, mustNotBeInside, out var error) switch
    {
        NewVaultFolder.Overlaps => L.Get("Vault_FolderOverlaps"),
        NewVaultFolder.NotEmpty => L.Get("Vault_FolderNotEmpty"),
        NewVaultFolder.Unreadable => L.Format("FirstRun_Error", error),
        _ => null,
    };

    private enum NewVaultFolder
    {
        Usable,
        Overlaps,
        NotEmpty,
        Unreadable,
    }

    /// <summary>Whether the folder can hold a new vault: empty or new, and neither inside nor around <paramref name="mustNotBeInside"/>.</summary>
    private static NewVaultFolder InspectNewVaultFolder(string path, string? mustNotBeInside, out string error)
    {
        error = "";
        try
        {
            if (mustNotBeInside is not null &&
                (FolderStats.IsSameOrInside(path, mustNotBeInside) || FolderStats.IsSameOrInside(mustNotBeInside, path)))
            {
                return NewVaultFolder.Overlaps;
            }

            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                return NewVaultFolder.NotEmpty;
            }

            return NewVaultFolder.Usable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return NewVaultFolder.Unreadable;
        }
    }

    private async Task<IReadOnlyList<LibraryItem>> LoadPointerItemsAsync()
    {
        var results = await Task.Run(() => _library.SearchAsync("", new ItemFilter { Mode = ItemMode.Pointer }));
        return results.Items;
    }

    /// <summary>Shows the progress dialog, runs the work off the UI thread and always closes the dialog.</summary>
    private async Task<T> RunWithProgressAsync<T>(ProgressDialogViewModel progress, Func<Task<T>> work)
    {
        var shown = _dialogs.ShowAsync(progress);
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            await CloseProgressAsync(progress, shown);
        }
    }

    private static async Task CloseProgressAsync(ProgressDialogViewModel progress, Task shown)
    {
        progress.Complete();
        await shown;
    }

    /// <summary>Explains a failed operation: not available yet, a library message as is, or anything else (logged).</summary>
    private async Task HandleFailureAsync(Exception ex, string titleKey)
    {
        switch (ex)
        {
            case NotImplementedException or NotSupportedException:
                AppLog.Info($"Vault operation not available: {ex.Message}");
                await ShowMessageAsync(L.Get("Vault_UnavailableTitle"), L.Get("Vault_Unavailable"));
                break;
            case LibraryException library:
                AppLog.Info($"Vault operation refused: {library.Code}: {library.Message}");
                await ShowErrorAsync(L.Get(titleKey), library.Message);
                break;
            default:
                AppLog.Error("Vault operation failed.", ex);
                await ShowErrorAsync(L.Get(titleKey), L.Format("Vault_UnexpectedError", ex.Message));
                break;
        }
    }

    private async Task<OperationDialogChoice?> ShowAsync(OperationDialogViewModel dialog) => await _dialogs.ShowAsync(dialog) as OperationDialogChoice?;

    private Task ShowMessageAsync(string title, string message) => ShowAsync(new OperationDialogViewModel(title, L.Get("Common_Ok"))
    {
        Message = message,
        ShowCancel = false,
    });

    private Task ShowErrorAsync(string title, string message) => ShowAsync(new OperationDialogViewModel(title, L.Get("Common_Ok"))
    {
        Symbol = SymbolRegular.ErrorCircle24,
        Tone = DialogTone.Danger,
        Message = message,
        ShowCancel = false,
    });

    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } window ? window : null;

    public static string FilesAndSize(int files, long bytes)
        => files == 1
            ? L.Format("Vault_FilesAndSizeOne", Glyphs.FileSize(bytes))
            : L.Format("Vault_FilesAndSize", N(files), Glyphs.FileSize(bytes));

    private static string CountText(int count, bool capped) => capped ? L.Format("Vault_CountAtLeast", N(count)) : N(count);

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
