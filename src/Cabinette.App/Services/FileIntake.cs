using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services.Explorer;
using Cabinette.App.ViewModels.Shell;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using Microsoft.Win32;

namespace Cabinette.App.Services;

/// <summary>
/// Adding files: drops, the "+" file dialog, paste and the Explorer entries. Shows a cancellable progress card while
/// copying and result notices afterwards (added, duplicates, failures).
/// </summary>
public sealed class FileIntake : IFileIntake
{
    /// <summary>Progress cards only appear for operations slower than this, to avoid flicker.</summary>
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(250);
    private const int MaxIndividualNotices = 3;

    private readonly ILibraryService _library;
    private readonly IClipboardReader _clipboard;
    private readonly NotificationService _notifications;
    private readonly DialogService _dialogs;
    private readonly IShellNavigator _navigator;
    private readonly CategoryDirectory _directory;

    public FileIntake(
        ILibraryService library,
        IClipboardReader clipboard,
        NotificationService notifications,
        DialogService dialogs,
        IShellNavigator navigator,
        CategoryDirectory directory)
    {
        _library = library;
        _clipboard = clipboard;
        _notifications = notifications;
        _dialogs = dialogs;
        _navigator = navigator;
        _directory = directory;
    }

    /// <summary>
    /// Drops onto a thread or a chat-list row, and the paperclip (through <see cref="PickAndAddFilesAsync"/>). The user's own
    /// adds into a category follow "Sort files by type in type categories" (SPEC.md section 5); other sources keep the
    /// category they name.
    /// </summary>
    public Task AddPathsAsync(IReadOnlyList<string> paths, long? targetCategoryId, AddedBy addedBy = AddedBy.AppUi)
        => AddPathsCoreAsync(paths, targetCategoryId, addedBy, mode: null, sortByType: targetCategoryId is not null && addedBy == AddedBy.AppUi);

    /// <summary>
    /// One Explorer selection ("Copy to Cabinette" / "Add to Cabinette (Pointer)"): added like a drop onto the main window
    /// (auto-categorized, a folder becomes a category per the folder settings), in the entry's mode whatever the default
    /// add mode is, and recorded as added by Explorer, so it counts as unread. While the window isn't on screen the outcome
    /// goes to <paramref name="outside"/> (a toast), otherwise to the usual in-app notices. Never activates the window.
    /// </summary>
    internal Task AddFromExplorerAsync(ExplorerDrop drop, IExplorerAddReporter outside)
        => AddPathsCoreAsync(drop.Paths, targetCategoryId: null, AddedBy.Explorer, drop.Mode, outside);

    public async Task PickAndAddFilesAsync(long? targetCategoryId)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Get("Intake_PickTitle"),
            Multiselect = true,
            CheckFileExists = true,
            Filter = $"{L.Get("Intake_AllFiles")}|*.*",
        };
        var owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        if (dialog.ShowDialog(owner) == true && dialog.FileNames.Length > 0)
        {
            await AddPathsAsync(dialog.FileNames, targetCategoryId);
        }
    }

    public async Task PasteAsync(long? targetCategoryId)
    {
        ClipboardPayload payload;
        try
        {
            // Off the UI thread: reading may wait for another app to let go of the clipboard, and a large image is
            // decoded and re-encoded as PNG. A background STA thread, as clipboard access wants.
            payload = await StaWorker.RunAsync(_clipboard.Read);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the clipboard failed.", ex);
            payload = new ClipboardPayload(null, null, null);
        }

        if (payload.IsEmpty)
        {
            _notifications.Show(L.Get("Intake_NothingToPaste"));
            return;
        }

        try
        {
            if (payload.FilePaths is { Count: > 0 } files)
            {
                // Files copied in Explorer: vault copies, like a drop (auto-categorized in the main window).
                await AddPathsCoreAsync(files, targetCategoryId, AddedBy.Paste, ItemMode.Copy);
                return;
            }

            if (payload.ImagePng is { Length: > 0 } png)
            {
                var target = targetCategoryId ?? await AskPasteTargetAsync(ExtensionMap.ImagesKey, "Intake_PasteTitleImage");
                if (target is null)
                {
                    return;
                }

                var result = await _library.AddPastedImageAsync(target.Value, png);
                await ReportPasteAsync(result, target.Value, "Intake_PastedImage");
                return;
            }

            if (!string.IsNullOrEmpty(payload.Text))
            {
                var isLink = IsSingleUrl(payload.Text);
                var target = targetCategoryId ?? await AskPasteTargetAsync(
                    isLink ? ExtensionMap.LinksKey : ExtensionMap.NotesKey,
                    isLink ? "Intake_PasteTitleLink" : "Intake_PasteTitleText");
                if (target is null)
                {
                    return;
                }

                var result = await _library.AddPastedTextAsync(target.Value, payload.Text);
                await ReportPasteAsync(result, target.Value, isLink ? "Intake_PastedLink" : "Intake_PastedText");
            }
        }
        catch (LibraryException ex)
        {
            _notifications.Show(ex.Message, NotificationKind.Error);
        }
        catch (Exception ex)
        {
            ReportUnexpected("Paste failed.", ex);
        }
    }

    /// <summary>A single http(s) address with nothing else around it.</summary>
    public static bool IsSingleUrl(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 &&
               !trimmed.Any(char.IsWhiteSpace) &&
               Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Main-window paste: which category? Notes/Images/Links is preselected. If the user deleted it, it is still offered,
    /// but only re-created once the user confirms the picker with it (not when they cancel or pick another one).
    /// </summary>
    private async Task<long?> AskPasteTargetAsync(string builtInKey, string titleKey)
    {
        var result = await _dialogs.ShowCategoryPickerAsync(L.Get(titleKey), null, allowCreate: true, allowTopLevel: false, builtInKey: builtInKey);
        if (result?.BuiltInKeyToCreate is { } key)
        {
            return (await _library.GetOrCreateBuiltInCategoryAsync(key)).Id;
        }

        return result?.CategoryId;
    }

    /// <param name="outside">Explorer batches: reports the outcome as a toast while the window isn't on screen.</param>
    /// <param name="sortByType">Sort files into their type categories when the target is one and the setting is on.</param>
    private async Task AddPathsCoreAsync(IReadOnlyList<string> paths, long? targetCategoryId, AddedBy addedBy, ItemMode? mode,
        IExplorerAddReporter? outside = null, bool sortByType = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => AddPathsCoreAsync(paths, targetCategoryId, addedBy, mode, outside, sortByType)).Task.Unwrap();
            return;
        }

        if (paths.Count == 0)
        {
            return;
        }

        var title = paths.Count == 1
            ? L.Format("Intake_AddingNamed", Path.GetFileName(paths[0].TrimEnd('\\', '/')))
            : L.Format("Intake_Adding", paths.Count);
        var task = new ProgressTaskViewModel(title) { Detail = L.Get("Intake_Preparing") };

        // Only show the card if the operation takes a moment.
        var shown = false;
        var showTimer = new DispatcherTimer { Interval = ProgressDelay };
        showTimer.Tick += (_, _) =>
        {
            showTimer.Stop();
            shown = true;
            _notifications.Tasks.Add(task);
        };
        showTimer.Start();

        var progress = new Progress<AddProgress>(p =>
        {
            if (p.TotalFiles > 0)
            {
                task.IsIndeterminate = false;
                task.Progress = p.TotalBytes > 0
                    ? Math.Clamp((double)p.BytesProcessed / p.TotalBytes, 0, 1)
                    : Math.Clamp((double)p.ProcessedFiles / p.TotalFiles, 0, 1);
                task.Title = L.Format("Intake_Adding", p.TotalFiles);
                task.Detail = string.IsNullOrEmpty(p.CurrentFile)
                    ? L.Format("Intake_Progress", p.ProcessedFiles, p.TotalFiles)
                    : $"{L.Format("Intake_Progress", Math.Min(p.ProcessedFiles + 1, p.TotalFiles), p.TotalFiles)} · {Path.GetFileName(p.CurrentFile)}";
            }
        });

        try
        {
            var request = new AddRequest
            {
                TargetCategoryId = targetCategoryId,
                AddedBy = addedBy,
                Mode = mode,
                SortIntoTypeCategories = sortByType && await SortsByTypeAsync(),
            };
            var result = await _library.AddPathsAsync(paths, request, progress, task.Token);

            // Decided at the end: the window may have been opened (or hidden) while the files were copied.
            if (outside is { ReportsOutsideWindow: true })
            {
                await outside.ReportAsync(mode.GetValueOrDefault(), result);
            }
            else if (targetCategoryId is { } target && result.SortedByType.Count > 0)
            {
                await ReportSortedAsync(result, target);
            }
            else
            {
                await ReportAsync(result, targetCategoryId);
            }
        }
        catch (OperationCanceledException)
        {
            _notifications.Show(L.Get("Intake_Cancelled"));
        }
        catch (LibraryException ex)
        {
            if (outside is { ReportsOutsideWindow: true })
            {
                outside.ReportFailure(mode.GetValueOrDefault(), ex.Message);
            }
            else
            {
                _notifications.Show(ex.Message, NotificationKind.Error);
            }
        }
        catch (Exception ex)
        {
            if (outside is { ReportsOutsideWindow: true })
            {
                AppLog.Error("Adding files failed.", ex);
                outside.ReportFailure(mode.GetValueOrDefault(), L.Get("Notify_Error"));
            }
            else
            {
                ReportUnexpected("Adding files failed.", ex);
            }
        }
        finally
        {
            showTimer.Stop();
            if (shown)
            {
                _notifications.Tasks.Remove(task);
            }
        }
    }

    /// <summary>The "Sort files by type in type categories" setting (on when it can't be read, its default).</summary>
    private async Task<bool> SortsByTypeAsync()
    {
        try
        {
            return (await _library.GetSettingsAsync()).SortFilesByTypeInTypeCategories;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("Reading the settings failed; sorting by type as by default.", ex);
            return new AppSettings().SortFilesByTypeInTypeCategories;
        }
    }

    /// <summary>
    /// After an add that sorted files into their type categories (SPEC.md section 5): when all of them went to one category
    /// the thread doesn't show, that thread opens; otherwise one notice says where they went, with "Show". Duplicates and
    /// failures are reported as usual.
    /// </summary>
    private async Task ReportSortedAsync(AddResult result, long targetCategoryId)
    {
        await _directory.EnsureLoadedAsync();
        if (result.SortedByType.Any(s => _directory.Get(s.CategoryId) is null) || _directory.Get(targetCategoryId) is null)
        {
            // A type category was just re-created by this add.
            _directory.Invalidate();
            await _directory.EnsureLoadedAsync();
        }

        var outcome = TypeSortOutcome.From(result, _directory.GetSubtree(targetCategoryId));
        if (outcome.OpenCategoryId is { } openId)
        {
            var count = result.SortedByType.Count;
            var name = _directory.GetPath(openId);
            _navigator.OpenCategory(openId, result.Added.Count == 1 ? result.Added[0].Id : null);
            _notifications.Show(count == 1 ? L.Format("Intake_SortedIntoOne", name) : L.Format("Intake_SortedIntoMany", count, name), NotificationKind.Success);
        }
        else
        {
            var parts = string.Join(", ", outcome.Destinations.Select(d => L.Format("Intake_SortedPart", d.Count, _directory.GetPath(d.CategoryId))));
            // "Stayed here" only when the files were dropped on the thread you're in; a drop on another row names it.
            var message = outcome.Stayed == 0
                ? L.Format("Intake_SortedSplit", parts)
                : _navigator.CurrentCategoryId == targetCategoryId
                    ? L.Format("Intake_SortedSplitStayed", parts, outcome.Stayed)
                    : L.Format("Intake_SortedSplitStayedIn", parts, outcome.Stayed, _directory.GetPath(targetCategoryId));
            var showId = outcome.Destinations[0].CategoryId;
            _notifications.Show(
                message,
                NotificationKind.Success,
                _navigator.CurrentCategoryId == showId ? null : L.Get("Intake_Show"),
                () => _navigator.OpenCategory(showId));
        }

        ReportDuplicatesAndFailures(result, outcome.OpenCategoryId ?? _navigator.CurrentCategoryId);
    }

    private async Task ReportAsync(AddResult result, long? targetCategoryId)
    {
        await _directory.EnsureLoadedAsync();

        if (result.Added.Count > 0)
        {
            var first = result.Added[0];
            var openId = targetCategoryId ?? (result.Added.All(i => i.HomeCategoryId == first.HomeCategoryId) ? first.HomeCategoryId : (long?)null);
            var categoryName = openId is { } id ? _directory.GetName(id) : null;
            var message = (result.Added.Count, categoryName) switch
            {
                (1, { Length: > 0 } name) => L.Format("Intake_AddedOneTo", name),
                (1, _) => L.Get("Intake_AddedOne"),
                (var n, { Length: > 0 } name) => L.Format("Intake_AddedTo", n, name),
                (var n, _) => L.Format("Intake_Added", n),
            };

            var showId = openId ?? first.HomeCategoryId;
            var focusId = result.Added.Count == 1 ? first.Id : (long?)null;
            _notifications.Show(
                message,
                NotificationKind.Success,
                _navigator.CurrentCategoryId == showId ? null : L.Get("Intake_Show"),
                () => _navigator.OpenCategory(showId, focusId));
        }

        ReportDuplicatesAndFailures(result);
        if (result.Added.Count == 0 && result.Duplicates.Count == 0 && result.Failures.Count == 0)
        {
            _notifications.Show(L.Get("Intake_NothingAdded"));
        }
    }

    /// <param name="shownCategoryId">
    /// The thread on screen after a sort by type: a duplicate sorted into another category says where it went instead of "here".
    /// </param>
    private void ReportDuplicatesAndFailures(AddResult result, long? shownCategoryId = null)
    {
        if (result.Duplicates.Count is > 0 and <= MaxIndividualNotices)
        {
            var sortedInto = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var sorted in result.SortedByType)
            {
                sortedInto.TryAdd(sorted.SourcePath, sorted.CategoryId);
            }

            foreach (var duplicate in result.Duplicates)
            {
                var where = _directory.GetName(duplicate.Existing.HomeCategoryId);
                var name = duplicate.Existing.FileName;
                var key = duplicate.AddedToTargetCategory ? "Intake_DuplicateAddedHere" : "Intake_Duplicate";
                var existing = duplicate.Existing;
                var message = duplicate.AddedToTargetCategory && sortedInto.TryGetValue(duplicate.SourcePath, out var into) && into != shownCategoryId
                    ? L.Format("Intake_DuplicateAddedTo", name, where, _directory.GetPath(into))
                    : L.Format(key, name, where);
                _notifications.Show(
                    message,
                    NotificationKind.Info,
                    L.Get("Intake_Show"),
                    () => _navigator.OpenCategory(existing.HomeCategoryId, existing.Id));
            }
        }
        else if (result.Duplicates.Count > MaxIndividualNotices)
        {
            var lines = result.Duplicates
                .Select(d => $"{d.Existing.FileName}  ({_directory.GetPath(d.Existing.HomeCategoryId)})")
                .ToList();
            _notifications.Show(
                L.Format("Intake_DuplicatesMany", result.Duplicates.Count),
                NotificationKind.Info,
                L.Get("Intake_Details"),
                () => _ = _dialogs.ShowMessageAsync(L.Get("Intake_DuplicatesTitle"), L.Get("Intake_DuplicatesBody"), Limit(lines)));
        }

        if (result.Failures.Count == 1)
        {
            var failure = result.Failures[0];
            _notifications.Show(L.Format("Intake_Failed", Path.GetFileName(failure.SourcePath), failure.Message), NotificationKind.Error);
        }
        else if (result.Failures.Count > 1)
        {
            var lines = result.Failures.Select(f => $"{Path.GetFileName(f.SourcePath)} — {f.Message}").ToList();
            _notifications.Show(
                L.Format("Intake_FailedMany", result.Failures.Count),
                NotificationKind.Error,
                L.Get("Intake_Details"),
                () => _ = _dialogs.ShowMessageAsync(L.Get("Intake_FailuresTitle"), L.Get("Intake_FailuresBody"), Limit(lines)));
        }
    }

    private async Task ReportPasteAsync(AddResult result, long categoryId, string addedKey)
    {
        await _directory.EnsureLoadedAsync();
        var name = _directory.GetName(categoryId);
        if (result.Added.Count > 0)
        {
            var item = result.Added[0];
            _notifications.Show(
                L.Format(addedKey, name),
                NotificationKind.Success,
                _navigator.CurrentCategoryId == categoryId ? null : L.Get("Intake_Show"),
                () => _navigator.OpenCategory(categoryId, item.Id));
        }

        foreach (var duplicate in result.Duplicates.Take(MaxIndividualNotices))
        {
            var existing = duplicate.Existing;
            _notifications.Show(
                L.Format(duplicate.AddedToTargetCategory ? "Intake_DuplicateAddedHere" : "Intake_Duplicate", existing.FileName, _directory.GetName(existing.HomeCategoryId)),
                NotificationKind.Info,
                L.Get("Intake_Show"),
                () => _navigator.OpenCategory(existing.HomeCategoryId, existing.Id));
        }

        foreach (var failure in result.Failures.Take(MaxIndividualNotices))
        {
            _notifications.Show(L.Format("Intake_Failed", Path.GetFileName(failure.SourcePath), failure.Message), NotificationKind.Error);
        }
    }

    private static IReadOnlyList<string> Limit(List<string> lines)
    {
        const int max = 50;
        if (lines.Count <= max)
        {
            return lines;
        }

        var limited = lines.Take(max).ToList();
        limited.Add(L.Format("Intake_AndMore", lines.Count - max));
        return limited;
    }

    private void ReportUnexpected(string context, Exception ex)
    {
        AppLog.Error(context, ex);
        _notifications.Show(L.Get("Notify_Error"), NotificationKind.Error, L.Get("Notify_OpenLog"), OpenLogFolder);
    }

    public static void OpenLogFolder()
    {
        if (AppLog.Directory is { } directory && Directory.Exists(directory))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Opening the log folder failed.", ex);
            }
        }
    }
}
