using System.IO;
using System.Diagnostics;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.App.Services.Monitor;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// Vault operations started by an AI (stage 4, SPEC.md section 10): the handler, the real VaultOperationsService and a real
/// library in temp folders, on an STA thread with a pumping dispatcher like Cabinette's UI thread. The progress dialog is only
/// a view model (no window exists), so nothing is shown.
/// </summary>
public sealed class AiCommandVaultTests : AiCommandTestBase
{
    // ================================================================== backup

    [Fact]
    public void Backup_starts_at_once_and_is_followed_to_its_end() => StaAsync.Run(async () =>
    {
        var docs = await BuiltInAsync("documents");
        await AddAsync(docs.Id, "a.txt", "hello");
        await AddAsync(docs.Id, "linked.txt", mode: ItemMode.Pointer);
        var zip = Path.Combine(Root, "backups", "library.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        var rig = new Rig(this, Library);

        var started = Ok<OperationStarted>(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(zip)));
        var status = await rig.WaitForEndAsync(started.OperationId);

        Assert.Equal(AiTools.CreateBackup, started.Kind);
        Assert.StartsWith("op-", started.OperationId);
        Assert.Equal("succeeded", status.State);
        Assert.Equal(zip, status.Path);
        Assert.NotNull(status.FinishedAt);
        Assert.Null(status.Error);
        Assert.Contains("1 linked file left out", status.Summary);
        Assert.True(File.Exists(zip));
        Assert.Equal(started.OperationId, Ok<OperationStatus>(await rig.CallAsync(AiTools.GetOperationStatus)).OperationId);

        // The progress dialog was shown with the window brought up, and closed at the end without a result dialog.
        Assert.True(rig.Lifetime.TakePendingActivation());
        Assert.Empty(rig.Dialogs.OpenDialogs);
        Assert.False(rig.Lifetime.IsOperationRunning);

        var change = Assert.Single(rig.Changes.Reported);
        Assert.Equal((Client, AiChangeKind.BackupCreated), (change.Client, change.Kind));
        Assert.Equal(status.FilesDone, change.Count);

        var logged = rig.Log.Entries.First(e => e.Tool == AiTools.CreateBackup).Details;
        Assert.True(logged.Ok);
        Assert.Equal(started.OperationId, logged.OperationId);
        Assert.Equal([zip], logged.Paths);
    });

    [Fact]
    public void Backup_refusals_change_nothing() => StaAsync.Run(async () =>
    {
        var rig = new Rig(this, Library);
        var existing = Path.Combine(Root, "existing.zip");
        await File.WriteAllTextAsync(existing, "keep me");

        Assert.Contains("already exists", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(existing)), AiErrorCodes.Refused));
        Assert.Contains("outside the vault", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(Path.Combine(Vault, "b.zip"))), AiErrorCodes.Refused));
        Assert.Contains("doesn't exist", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(Path.Combine(Root, "no such folder", "b.zip"))), AiErrorCodes.Refused));
        Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(Path.Combine(Root, "b.7z"))), AiErrorCodes.InvalidArgument);
        Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(@"backups\b.zip")), AiErrorCodes.InvalidArgument);

        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(Path.Combine(Vault, "b.zip")));
        Assert.False(rig.Lifetime.IsOperationRunning);
        Assert.Empty(rig.Dialogs.OpenDialogs);
        Assert.Empty(rig.Changes.Reported);
        Failed(await rig.CallAsync(AiTools.GetOperationStatus), AiErrorCodes.NotFound);
    });

    // ================================================================== move vault

    [Fact]
    public void Move_vault_switches_to_the_new_folder_and_keeps_the_old_one() => StaAsync.Run(async () =>
    {
        var docs = await BuiltInAsync("documents");
        var item = await AddAsync(docs.Id, "a.txt", "hello");
        var target = Path.Combine(Root, "new vault");
        var rig = new Rig(this, Library);
        var moved = 0;
        rig.Service.VaultMoved += (_, _) => moved++;

        var started = Ok<OperationStarted>(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs(target)));
        var status = await rig.WaitForEndAsync(started.OperationId);

        Assert.Equal("succeeded", status.State);
        Assert.Equal((Vault, target), (status.OldVaultPath, status.Path));
        Assert.Contains("is kept", status.Summary);
        Assert.Equal(target, Library.VaultPath);
        Assert.True(File.Exists(item.FullPath)); // the old vault is kept
        Assert.True(File.Exists((await Library.GetItemAsync(item.Id))!.FullPath));
        Assert.StartsWith(target, (await Library.GetItemAsync(item.Id))!.FullPath);
        Assert.Equal(1, moved);
        Assert.Empty(rig.Dialogs.OpenDialogs);
        var change = Assert.Single(rig.Changes.Reported);
        Assert.Equal(AiChangeKind.VaultMoved, change.Kind);
        Assert.Equal(status.FilesDone, change.Count);
    });

    [Fact]
    public void Move_vault_refusals_change_nothing() => StaAsync.Run(async () =>
    {
        var rig = new Rig(this, Library);
        var full = Path.Combine(Root, "full");
        Directory.CreateDirectory(full);
        await File.WriteAllTextAsync(Path.Combine(full, "x.txt"), "x");

        Assert.Contains("isn't empty", Failed(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs(full)), AiErrorCodes.Refused));
        Assert.Contains("inside", Failed(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs(Path.Combine(Vault, "Inner"))), AiErrorCodes.Refused));
        Assert.Contains("inside", Failed(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs(Vault)), AiErrorCodes.Refused));
        Failed(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs("elsewhere")), AiErrorCodes.InvalidArgument);

        Assert.Equal(Vault, Library.VaultPath);
        Assert.False(rig.Lifetime.IsOperationRunning);
        Assert.Empty(rig.Dialogs.OpenDialogs);
    });

    // ================================================================== migrate

    [Fact]
    public void Migrate_copies_linked_files_into_the_vault() => StaAsync.Run(async () =>
    {
        var docs = await BuiltInAsync("documents");
        var linked = await AddAsync(docs.Id, "linked.txt", "linked content", ItemMode.Pointer);
        var rig = new Rig(this, Library);

        var started = Ok<OperationStarted>(await rig.CallAsync(AiTools.MigratePointers, new MigratePointersArgs()));
        var status = await rig.WaitForEndAsync(started.OperationId);

        Assert.Equal("succeeded", status.State);
        Assert.Empty(status.Failures!);
        var item = (await Library.GetItemAsync(linked.Id))!;
        Assert.Equal(ItemMode.Copy, item.Mode);
        Assert.StartsWith(Vault, item.FullPath);
        Assert.True(File.Exists(linked.FullPath)); // the original stays
        var change = Assert.Single(rig.Changes.Reported);
        Assert.Equal((AiChangeKind.PointersMigrated, 1), (change.Kind, change.Count));

        // Nothing linked any more: refused, nothing starts.
        Assert.Contains("no linked", Failed(await rig.CallAsync(AiTools.MigratePointers), AiErrorCodes.Refused));
        Assert.False(rig.Lifetime.IsOperationRunning);
    });

    // ================================================================== busy, progress, cancel

    [Fact]
    public void A_running_operation_makes_changes_busy_reports_progress_and_can_be_cancelled_in_the_dialog() => StaAsync.Run(async () =>
    {
        var notes = await BuiltInAsync("notes");
        var reachedMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = InterceptingLibrary.Create(Library, new Dictionary<string, Func<object?[], object?>>
        {
            ["MoveVaultAsync"] = args => MoveUntilCancelledAsync((IProgress<VaultOperationProgress>?)args[1], (CancellationToken)args[2]!, reachedMove),
        });
        var rig = new Rig(this, library);

        var started = Ok<OperationStarted>(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs(Path.Combine(Root, "new vault"))));

        // Running as soon as it answered: a second operation and every change are busy, reading still works.
        Assert.True(rig.Lifetime.IsOperationRunning);
        Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(Path.Combine(Root, "b.zip"))), AiErrorCodes.Busy);
        Failed(await rig.CallAsync(AiTools.CreateNote, new CreateNoteArgs("x", notes.Id)), AiErrorCodes.Busy);
        Ok<ListTagsResult>(await rig.CallAsync(AiTools.ListTags, new ListTagsArgs()));
        Assert.Equal(AiErrorCodes.Busy, (await rig.Service.StartBackupForAiAsync(Path.Combine(Root, "c.zip"), new Progress<VaultOperationProgress>())).ErrorCode);

        // Progress reaches the status.
        await reachedMove.Task;
        await StaAsync.UntilAsync(async () => Ok<OperationStatus>(await rig.CallAsync(AiTools.GetOperationStatus)).FilesDone == 3, "progress in the status");
        var running = Ok<OperationStatus>(await rig.CallAsync(AiTools.GetOperationStatus, new GetOperationStatusArgs(started.OperationId)));
        Assert.Equal(("running", "copying", 3, 10, 300L, 1000L), (running.State, running.Stage, running.FilesDone, running.FilesTotal, running.BytesDone, running.BytesTotal));

        // The user cancels in the progress dialog.
        var progress = Assert.IsType<ProgressDialogViewModel>(Assert.Single(rig.Dialogs.OpenDialogs));
        await StaAsync.UntilAsync(() => Task.FromResult(progress.CountText is not null), "the dialog shows the progress");
        progress.RequestCancelCommand.Execute(null);
        var status = await rig.WaitForEndAsync(started.OperationId);

        Assert.Equal("cancelled", status.State);
        Assert.Contains("cancelled", status.Error);
        Assert.Equal(Vault, Library.VaultPath);
        Assert.False(rig.Lifetime.IsOperationRunning);
        Assert.Empty(rig.Dialogs.OpenDialogs);
        var change = Assert.Single(rig.Changes.Reported);
        Assert.Equal((AiChangeKind.OperationFailed, L.Get("Vault_MoveCancelled")), (change.Kind, change.Detail));

        // Changes work again.
        Ok<FileResult>(await rig.CallAsync(AiTools.CreateNote, new CreateNoteArgs("x", notes.Id)));
    });

    [Fact]
    public void An_operation_started_from_Settings_makes_ai_operations_busy() => StaAsync.Run(async () =>
    {
        var rig = new Rig(this, Library);
        using (rig.Lifetime.BeginOperation())
        {
            Failed(await rig.CallAsync(AiTools.MigratePointers), AiErrorCodes.Busy);
            var direct = await rig.Service.StartMoveVaultForAiAsync(Path.Combine(Root, "x"), new Progress<VaultOperationProgress>());
            Assert.Equal(AiErrorCodes.Busy, direct.ErrorCode);
            Assert.Null(direct.Completion);
        }

        Assert.Empty(rig.Dialogs.OpenDialogs);
    });

    [Fact]
    public void A_failed_operation_is_reported_with_the_librarys_message() => StaAsync.Run(async () =>
    {
        var library = InterceptingLibrary.Create(Library, new Dictionary<string, Func<object?[], object?>>
        {
            ["CreateBackupAsync"] = _ => Task.FromException<BackupResult>(new LibraryException(LibraryErrorCode.AccessDenied, "Access to the folder is denied.")),
        });
        var rig = new Rig(this, library);

        var started = Ok<OperationStarted>(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(Path.Combine(Root, "b.zip"))));
        var status = await rig.WaitForEndAsync(started.OperationId);

        Assert.Equal(("failed", "Access to the folder is denied."), (status.State, status.Error));
        var change = Assert.Single(rig.Changes.Reported);
        Assert.Equal((AiChangeKind.OperationFailed, "Access to the folder is denied."), (change.Kind, change.Detail));
        Assert.False(rig.Lifetime.IsOperationRunning);
    });

    // ================================================================== the registry

    [Fact]
    public void The_registry_finds_by_id_or_the_latest_and_keeps_the_end()
    {
        var operations = new AiCommandOperations();
        var first = operations.Create(AiTools.CreateBackup);
        var second = operations.Create(AiTools.MoveVault);
        operations.Add(first);
        operations.Add(second);

        second.Report(new VaultOperationProgress("copying", 12, 10, 50, 0, null));
        var running = operations.Find(null)!;
        Assert.Equal((second.Id, "running", 10, 10, (long?)null), (running.OperationId, running.State, running.FilesDone, running.FilesTotal, running.BytesTotal));

        first.Complete(new AiVaultOperationOutcome(AiVaultOperationState.Failed) { Error = "disk full" });
        first.Complete(new AiVaultOperationOutcome(AiVaultOperationState.Succeeded));
        first.Report(new VaultOperationProgress("zipping", 1, 1, 1, 1, null));
        var ended = operations.Find(first.Id.ToUpperInvariant())!;
        Assert.Equal(("failed", "disk full"), (ended.State, ended.Error));
        Assert.Null(ended.Stage);
        Assert.Null(operations.Find("op-unknown"));
    }

    [Fact]
    public void The_registry_forgets_the_oldest_finished_operations_first()
    {
        var operations = new AiCommandOperations();
        var running = operations.Create(AiTools.MoveVault);
        operations.Add(running);
        var finished = new List<AiCommandOperation>();
        for (var i = 0; i < AiCommandOperations.MaxKept + 5; i++)
        {
            var operation = operations.Create(AiTools.CreateBackup);
            operation.Complete(new AiVaultOperationOutcome(AiVaultOperationState.Succeeded));
            operations.Add(operation);
            finished.Add(operation);
        }

        Assert.NotNull(operations.Find(running.Id));
        Assert.Null(operations.Find(finished[0].Id));
        Assert.NotNull(operations.Find(finished[^1].Id));
    }

    // ================================================================== helpers

    private static async Task<MoveVaultResult> MoveUntilCancelledAsync(IProgress<VaultOperationProgress>? progress, CancellationToken ct, TaskCompletionSource reached)
    {
        progress?.Report(new VaultOperationProgress("copying", 3, 10, 300, 1000, @"C:\vault\a.txt"));
        reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        throw new UnreachableException();
    }

    /// <summary>The app's pieces for AI vault operations, created on the STA thread (the host and the dialogs keep its dispatcher).</summary>
    private sealed class Rig
    {
        public Rig(AiCommandVaultTests test, ILibraryService library)
        {
            // AppLifetime.ActivateMainWindow only touches a window through Application.Current. The test process has none,
            // so activation is merely recorded (TakePendingActivation); fail rather than ever bring a real window up.
            Assert.Null(System.Windows.Application.Current);
            var directory = new CategoryDirectory(library);
            Dialogs = new DialogService(library, new CategoryIconService(library, new FakeIconGenerator()), directory);
            Service = new VaultOperationsService(library, Dialogs, new SilentNotifications(), test.RecycleBin, new NoShell(),
                new AppPaths(Path.Combine(test.Root, "data")), new VaultMonitorHost(library), Lifetime);
            Handler = new AiCommandHandler(library, Log, Changes, new AiCommandAppHost(Lifetime, new RecordingNavigator()), Service, new AiCommandOperations());
        }

        public AppLifetime Lifetime { get; } = new();

        public DialogService Dialogs { get; }

        public VaultOperationsService Service { get; }

        public AiCommandHandler Handler { get; }

        public FakeActivityLog Log { get; } = new();

        public FakeChangeReporter Changes { get; } = new();

        public Task<InstanceReply> CallAsync<TArgs>(string tool, TArgs args) => Handler.HandleAsync(Wire(tool, args));

        public Task<InstanceReply> CallAsync(string tool) => CallAsync<object?>(tool, null);

        /// <summary>Polls get_operation_status (the dispatcher keeps pumping) until the operation ended.</summary>
        public async Task<OperationStatus> WaitForEndAsync(string operationId)
        {
            OperationStatus? status = null;
            await StaAsync.UntilAsync(async () =>
            {
                status = Ok<OperationStatus>(await CallAsync(AiTools.GetOperationStatus, new GetOperationStatusArgs(operationId)));
                return status.State != "running";
            }, "the operation to end");

            // The end is reported after the status is recorded: give the report a moment.
            await StaAsync.UntilAsync(() => Task.FromResult(Changes.Reported.Count > 0), "the end to be reported");
            await StaAsync.UntilAsync(() => Task.FromResult(!Lifetime.IsOperationRunning), "the operation to be over");
            return status!;
        }
    }

    private sealed class SilentNotifications : INotificationService
    {
        public void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Action? action = null)
        {
        }
    }

    private sealed class NoShell : IShellOperations
    {
        public void OpenWithDefaultApp(string path) => throw new InvalidOperationException("Tests never open files.");

        public void ShowInExplorer(string path) => throw new InvalidOperationException("Tests never open Explorer.");
    }
}
