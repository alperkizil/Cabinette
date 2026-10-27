using System.IO;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.App.Services.Monitor;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests of the stage 4 "ai" command review (2026-10-24). They started as the reviewer's repro tests, which asserted
/// the behavior found (F1-F8); each now asserts the fixed behavior and keeps its finding's number. Temp folders and the test
/// Recycle Bin only; nothing is shown.
/// </summary>
public sealed class AiCommandReviewTests : AiCommandTestBase
{
    // ================================================================== F1: the system category can be renamed

    /// <summary>
    /// The UI offers no Rename for "Deleted" (CategoryMenuBuilder hides it for CategoryKind.System), so the handler refuses it
    /// too, as it refuses deleting it: the system category and its vault folder keep their names, nothing is reported, and the
    /// refusal is logged.
    /// </summary>
    [Fact]
    public async Task F1_Rename_category_refuses_the_Deleted_system_category()
    {
        var deleted = await BuiltInAsync("deleted");
        Assert.Equal(CategoryKind.System, deleted.Kind);
        Assert.True(Directory.Exists(Path.Combine(Vault, deleted.FolderName)));

        Failed(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(deleted.Id)), AiErrorCodes.Refused);
        var message = Failed(await CallAsync(AiTools.RenameCategory, new RenameCategoryArgs(deleted.Id, "Trash")), AiErrorCodes.Refused);

        Assert.Contains("can't be renamed", message);
        var stored = (await Library.GetCategoryAsync(deleted.Id))!;
        Assert.Equal(deleted.Name, stored.Name);
        Assert.Equal(CategoryKind.System, stored.Kind);
        Assert.False(Directory.Exists(Path.Combine(Vault, "Trash")));
        Assert.True(Directory.Exists(Path.Combine(Vault, deleted.FolderName)));
        Assert.Empty(Changes.Reported);
        Assert.Equal((AiTools.RenameCategory, false, AiErrorCodes.Refused), (Log.Last.Tool, Log.Last.Details.Ok, Log.Last.Details.ErrorCode));

        // Other categories, built-in ones included, can still be renamed.
        var docs = await BuiltInAsync("documents");
        Assert.Equal("Papers", Ok<CategoryResult>(await CallAsync(AiTools.RenameCategory, new RenameCategoryArgs(docs.Id, "Papers"))).Category.Name);
    }

    // ================================================================== F2: other spellings of a folder inside the vault

    /// <summary>
    /// CheckMoveTargetForAi compared paths after Path.GetFullPath, which leaves "\\?\" and "\\.\" device paths as they are, so a
    /// folder inside the vault spelled so was accepted as the new vault (Move vault would copy the vault into itself). The check
    /// now also compares the final paths Windows resolves (FinalPaths), which see through the prefix: the vault itself, a folder
    /// inside it and a folder containing it are all refused by the overlap rule again.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    [InlineData(@"//?/")]
    public void F2_Move_vault_check_refuses_a_device_path_spelling_of_a_folder_inside_the_vault(string prefix)
    {
        var inside = Path.Combine(Vault, "Inner");

        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(inside, Vault));
        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(Vault, Vault));
        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(prefix + inside, Vault));
        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(prefix + Vault, Vault));
        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(prefix + Root, Vault));

        // A new folder elsewhere, spelled plainly, is still fine.
        Assert.Null(VaultOperationsService.CheckMoveTargetForAi(Path.Combine(Root, "new vault"), Vault));
    }

    /// <summary>
    /// The same for create_backup: "\\?\&lt;vault&gt;\inside.zip" passed CheckBackupFileForAi and the backup was written into the
    /// vault. The check refuses it now, and the handler refuses device paths for move_vault and create_backup before that, as
    /// invalid_argument: nothing starts, nothing is written.
    /// </summary>
    [Fact]
    public void F2_Create_backup_refuses_a_device_path_spelling_inside_the_vault_and_writes_nothing() => StaAsync.Run(async () =>
    {
        var docs = await BuiltInAsync("documents");
        await AddAsync(docs.Id, "a.txt", "hello");
        var rig = new ReviewRig(this, Library);
        var plain = Path.Combine(Vault, "inside.zip");

        Assert.Contains("outside the vault", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(plain)), AiErrorCodes.Refused));
        foreach (var prefix in new[] { @"\\?\", @"\\.\" })
        {
            Assert.Contains("outside the vault", VaultOperationsService.CheckBackupFileForAi(prefix + plain, Vault));
            Assert.Contains("device path", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(prefix + plain)), AiErrorCodes.InvalidArgument));
        }

        Assert.False(File.Exists(plain));
        Assert.Empty(rig.Dialogs.OpenDialogs);
        Assert.Empty(rig.Changes.Reported);
        Assert.False(rig.Lifetime.IsOperationRunning);
    });

    /// <summary>move_vault and create_backup take plain full paths only: any device spelling is invalid_argument, wherever it points.</summary>
    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    [InlineData(@"//?/")]
    [InlineData(@"//./")]
    [InlineData(@"\??\")]
    public async Task F2_Vault_operations_refuse_device_paths_as_invalid_argument(string prefix)
    {
        var elsewhere = Path.Combine(Root, "elsewhere");

        Assert.Contains("device path", Failed(await CallAsync(AiTools.MoveVault, new MoveVaultArgs(prefix + elsewhere)), AiErrorCodes.InvalidArgument));
        Assert.Contains("device path", Failed(await CallAsync(AiTools.CreateBackup, new CreateBackupArgs(prefix + elsewhere + ".zip")), AiErrorCodes.InvalidArgument));

        Assert.Empty(VaultOperations.Calls);
        Assert.All(Log.Entries, e => Assert.Equal(AiErrorCodes.InvalidArgument, e.Details.ErrorCode));

        // Plain paths go on to the vault operations.
        Ok<OperationStarted>(await StartWithAsync(AiTools.MoveVault, new MoveVaultArgs(elsewhere)));
        Assert.Equal(["move:" + elsewhere], VaultOperations.Calls);
    }

    /// <summary>
    /// An 8.3 short name spells a folder inside the vault without the vault's own path in it (GetShortPathName shortens every
    /// part, the vault's parents included). Path.GetFullPath already expands short names for the parts that exist, and the final
    /// path does too: both checks refuse it, also below a folder that doesn't exist yet, and a backup there writes nothing.
    /// </summary>
    [Fact]
    public void F2_An_8_3_short_name_of_a_folder_inside_the_vault_is_refused() => StaAsync.Run(async () =>
    {
        var inside = Directory.CreateDirectory(Path.Combine(Vault, "A folder with a long name")).FullName;
        var shortInside = ShortPath(inside);
        if (shortInside is null || string.Equals(shortInside, inside, StringComparison.OrdinalIgnoreCase))
        {
            return; // This volume makes no 8.3 names: there is no other spelling to try.
        }

        Assert.DoesNotContain(Path.GetFileName(Root), shortInside, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(shortInside, Vault));
        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(Path.Combine(shortInside, "not yet", "new vault"), Vault));
        Assert.Contains("outside the vault", VaultOperationsService.CheckBackupFileForAi(Path.Combine(shortInside, "inside.zip"), Vault));

        var rig = new ReviewRig(this, Library);
        Assert.Contains("outside the vault", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(Path.Combine(shortInside, "inside.zip"))), AiErrorCodes.Refused));
        Assert.False(File.Exists(Path.Combine(inside, "inside.zip")));
        Assert.Empty(rig.Dialogs.OpenDialogs);
    });

    /// <summary>".." segments are resolved before the checks (as before): a path that climbs back into the vault is refused.</summary>
    [Fact]
    public void F2_Dot_dot_segments_back_into_the_vault_are_refused() => StaAsync.Run(async () =>
    {
        var climbing = Root + @"\elsewhere\..\vault";
        Directory.CreateDirectory(Path.Combine(Root, "elsewhere"));

        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(climbing + @"\Inner", Vault));
        Assert.Contains("inside", VaultOperationsService.CheckMoveTargetForAi(Root + @"\elsewhere\..", Vault));
        Assert.Contains("outside the vault", VaultOperationsService.CheckBackupFileForAi(climbing + @"\inside.zip", Vault));

        var rig = new ReviewRig(this, Library);
        Assert.Contains("outside the vault", Failed(await rig.CallAsync(AiTools.CreateBackup, new CreateBackupArgs(climbing + @"\inside.zip")), AiErrorCodes.Refused));
        Assert.Contains("inside", Failed(await rig.CallAsync(AiTools.MoveVault, new MoveVaultArgs(climbing + @"\Inner")), AiErrorCodes.Refused));
        Assert.False(File.Exists(Path.Combine(Vault, "inside.zip")));
        Assert.Empty(rig.Dialogs.OpenDialogs);
    });

    /// <summary>A call answered with the fake vault operations' started operation (as AiCommandVaultTests do).</summary>
    private Task<InstanceReply> StartWithAsync<TArgs>(string tool, TArgs args)
    {
        VaultOperations.Next = () => AiVaultOperationStart.Started(new TaskCompletionSource<AiVaultOperationOutcome>().Task);
        return CallAsync(tool, args);
    }

    /// <summary>The path with every part in its 8.3 short form, or null.</summary>
    private static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathName(path, buffer, buffer.Length);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, [System.Runtime.InteropServices.Out] char[] shortPath, int bufferLength);

    // ================================================================== F3: change reports that don't match what happened

    /// <summary>
    /// move_files names the category the files leave. The library (written for the UI, where the files always come from the
    /// thread you're in) would only add a file that isn't there to the target, so the handler checks first: every file must be in
    /// that category or one of its subcategories, else invalid_argument names the files and nothing changes, not even for the
    /// files that are there.
    /// </summary>
    [Fact]
    public async Task F3a_Move_files_from_a_category_the_files_are_not_in_is_refused_and_changes_nothing()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var archive = await Library.CreateCategoryAsync("Archive", null);
        var a = await AddAsync(docs.Id, "a.pdf");
        var b = await AddAsync(work.Id, "b.pdf");
        var c = await AddAsync(docs.Id, "c.pdf");

        var message = Failed(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([b.Id, a.Id, c.Id], work.Id, archive.Id)), AiErrorCodes.InvalidArgument);

        Assert.Contains($"The files with ids {a.Id}, {c.Id} aren't in \"Work\"", message);
        Assert.Contains("Nothing was changed", message);
        Assert.Equal([docs.Id], (await Library.GetItemAsync(a.Id))!.CategoryIds);
        Assert.Equal([work.Id], (await Library.GetItemAsync(b.Id))!.CategoryIds);
        Assert.Equal([docs.Id], (await Library.GetItemAsync(c.Id))!.CategoryIds);
        Assert.Empty(Changes.Reported);
        Assert.Equal((false, AiErrorCodes.InvalidArgument), (Log.Last.Details.Ok, Log.Last.Details.ErrorCode));

        // A file in a subcategory of the category it leaves is in it (its thread shows it): that moves.
        var python = await Library.CreateCategoryAsync("Python", work.Id);
        var d = await AddAsync(python.Id, "d.py");
        var moved = Ok<AiChangeResult>(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([b.Id, d.Id], work.Id, archive.Id)));
        Assert.Equal(2, moved.Count);
        Assert.All(moved.Files!, f => Assert.Equal([archive.Id], f.CategoryIds));
    }

    /// <summary>
    /// remove_from_category, add_to_category and untag_files (and tag, star, unstar, move and the renames) count what really
    /// changed, compared before and after: a call that changed nothing (the file wasn't in the category, already was, didn't have
    /// the tag...) answers 0 and makes no toast. The files are still returned as they are.
    /// </summary>
    [Fact]
    public async Task F3b_No_op_changes_report_zero_and_no_toast()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var a = await AddAsync(docs.Id, "a.pdf");
        await Library.SetItemTagsAsync(a.Id, ["Invoice"]);
        var before = (await Library.GetItemAsync(a.Id))!;

        var removed = Ok<AiChangeResult>(await CallAsync(AiTools.RemoveFromCategory, new FilesCategoryArgs([a.Id], work.Id)));
        var added = Ok<AiChangeResult>(await CallAsync(AiTools.AddToCategory, new FilesCategoryArgs([a.Id], docs.Id)));
        var untagged = Ok<AiChangeResult>(await CallAsync(AiTools.UntagFiles, new TagFilesArgs([a.Id], ["nothing"])));
        var tagged = Ok<AiChangeResult>(await CallAsync(AiTools.TagFiles, new TagFilesArgs([a.Id], ["invoice"])));
        var unstarred = Ok<AiChangeResult>(await CallAsync(AiTools.UnstarFiles, new FileIdsArgs([a.Id])));
        var moved = Ok<AiChangeResult>(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([a.Id], docs.Id, docs.Id)));
        Ok<FileResult>(await CallAsync(AiTools.RenameFile, new RenameFileArgs(a.Id, a.FileName)));
        Ok<CategoryResult>(await CallAsync(AiTools.RenameCategory, new RenameCategoryArgs(work.Id, "Work")));

        var after = (await Library.GetItemAsync(a.Id))!;
        Assert.Equal(before.CategoryIds, after.CategoryIds);
        Assert.Equal(before.Tags, after.Tags);
        Assert.Equal((0, 0, 0, 0, 0, 0), (removed.Count, added.Count, untagged.Count, tagged.Count, unstarred.Count, moved.Count));
        Assert.All(new[] { removed, added, untagged, tagged, unstarred, moved }, r => Assert.Equal(a.Id, Assert.Single(r.Files!).Id));
        Assert.Empty(Changes.Reported);
        Assert.All(Log.Entries, e => Assert.True(e.Details.Ok));

        // Of several files, only the ones that changed are counted.
        var b = await AddAsync(docs.Id, "b.pdf");
        var c = await AddAsync(docs.Id, "c.pdf");
        await Library.AddToCategoryAsync([b.Id], work.Id);
        var addedTwo = Ok<AiChangeResult>(await CallAsync(AiTools.AddToCategory, new FilesCategoryArgs([a.Id, b.Id, c.Id], work.Id)));
        var untaggedOne = Ok<AiChangeResult>(await CallAsync(AiTools.UntagFiles, new TagFilesArgs([a.Id, b.Id], ["INVOICE"])));
        var starredThree = Ok<AiChangeResult>(await CallAsync(AiTools.StarFiles, new FileIdsArgs([a.Id, b.Id, c.Id])));
        Assert.Equal((2, 1, 3), (addedTwo.Count, untaggedOne.Count, starredThree.Count));
        Assert.Equal(3, addedTwo.Files!.Count);
        Assert.Equal(
            [(AiChangeKind.FilesAddedToCategory, 2), (AiChangeKind.FilesUntagged, 1), (AiChangeKind.FilesStarred, 3)],
            Changes.Reported.Select(change => (change.Kind, change.Count)));
    }

    // ================================================================== F4: an AI delete forgets the user's pending "Delete permanently?"

    /// <summary>
    /// After a late Recycle Bin refusal the library remembers the refused files for the user's confirmed retry
    /// ("Delete permanently?"). Only a delete of the same ids takes that memory now, so an AI delete_files that lands while the
    /// dialog is up (here also refused late, a second refusal) leaves it alone: the user's confirmation deletes the file.
    /// </summary>
    [Fact]
    public async Task F4_An_ai_delete_keeps_the_users_pending_permanent_delete_confirmation()
    {
        var docs = await BuiltInAsync("documents");
        var control = await AddAsync(docs.Id, "control.bin");
        var huge = await AddAsync(docs.Id, "huge.bin");
        var other = await AddAsync(docs.Id, "other.pdf");
        var alsoHuge = await AddAsync(docs.Id, "also huge.bin");
        RecycleBin.Refused.Add(control.FullPath);
        RecycleBin.Refused.Add(huge.FullPath);
        var permanently = new DeleteItemsOptions { PermanentWhenNoRecycleBin = true };

        // Without an AI in between, the confirmed retry deletes exactly the refused file.
        var refused = await Assert.ThrowsAsync<LibraryException>(() => Library.DeleteItemsAsync([control.Id]));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, refused.Code);
        await Library.DeleteItemsAsync([control.Id], permanently);
        Assert.False(File.Exists(control.FullPath));

        // The user's delete is refused late; the "Delete permanently?" dialog is up...
        refused = await Assert.ThrowsAsync<LibraryException>(() => Library.DeleteItemsAsync([huge.Id]));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, refused.Code);
        Assert.Null(await Library.GetItemAsync(huge.Id));
        Assert.True(File.Exists(huge.FullPath));

        // ...an AI deletes another file meanwhile, and one the Recycle Bin also refuses late (never deleted permanently)...
        Ok<AiChangeResult>(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([other.Id])));
        Assert.Contains(other.FullPath, RecycleBin.Recycled);
        RecycleBin.Refused.Add(alsoHuge.FullPath);
        Failed(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([alsoHuge.Id])), AiErrorCodes.RecycleBinUnavailable);
        Assert.True(File.Exists(alsoHuge.FullPath));

        // ...and the user's confirmation still deletes exactly the refused file.
        await Library.DeleteItemsAsync([huge.Id], permanently);
        Assert.False(File.Exists(huge.FullPath));
        Assert.Null(await Library.GetItemAsync(huge.Id));
        Assert.True(File.Exists(alsoHuge.FullPath));
    }

    // ================================================================== F5: tag names are cleaned like the UI's

    /// <summary>
    /// The handler normalizes tag names exactly as the tag editor does and keeps Unicode format characters: a tag with a
    /// zero-width joiner (any ZWJ emoji sequence) given by the user is the same tag for an AI, which can remove it and add it back
    /// without creating a second one. Control characters are still dropped (white space becomes '-', as in the editor).
    /// </summary>
    [Fact]
    public async Task F5_Tags_with_format_characters_are_the_users_tags()
    {
        const string joined = "\U0001F468\u200D\U0001F4BB"; // man + ZWJ + laptop
        const string flag = "\U0001F3F3\uFE0F\u200D\U0001F308"; // white flag + variation selector + ZWJ + rainbow
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");
        await Library.SetItemTagsAsync(a.Id, [joined, flag]); // as the tag editor stores them
        Assert.Equal(new[] { joined, flag }.Order(StringComparer.Ordinal), (await Library.GetItemAsync(a.Id))!.Tags.Order(StringComparer.Ordinal));

        var untagged = Ok<AiChangeResult>(await CallAsync(AiTools.UntagFiles, new TagFilesArgs([a.Id], [joined])));
        Assert.Equal(1, untagged.Count);
        Assert.Equal([joined], Log.Entries[0].Details.Tags);
        Assert.Equal([flag], (await Library.GetItemAsync(a.Id))!.Tags);

        var tagged = Ok<AiChangeResult>(await CallAsync(AiTools.TagFiles, new TagFilesArgs([a.Id], ["#" + joined, flag])));
        Assert.Equal(1, tagged.Count);
        Assert.Equal(new[] { joined, flag }.Order(StringComparer.Ordinal), (await Library.GetItemAsync(a.Id))!.Tags.Order(StringComparer.Ordinal));
        Assert.Equal([AiChangeKind.FilesUntagged, AiChangeKind.FilesTagged], Changes.Reported.Select(c => c.Kind));

        // As the editor: trimmed, no leading '#', white space (control characters included) becomes one '-'; other control
        // characters are dropped.
        Assert.Equal(TagEditorViewModel.Normalize(" #paid  2026 "), AiCommandHandler.TagName(" #paid  2026 "));
        Assert.Equal("paid-2026", AiCommandHandler.TagName("paid\t\r\n2026"));
        Assert.Equal("paid-2026", AiCommandHandler.TagName("\u0007paid 2026\u001B"));
        Assert.Equal(joined, AiCommandHandler.TagName(joined));
        Assert.Equal("", AiCommandHandler.TagName("\u0001"));
    }

    // ================================================================== F6: the activity log keeps a bounded copy of what the AI sent

    /// <summary>
    /// The activity log is kept for a year and rendered by Settings › AI, so the handler cuts every text it stores (queries,
    /// paths, names, errors) to 2,000 characters ending in "…", and keeps at most 50 paths. The replies to the AI are as before.
    /// </summary>
    [Fact]
    public async Task F6_The_activity_log_caps_queries_paths_and_errors()
    {
        var longQuery = new string('q', 200_000);
        var longPath = @"C:\" + new string('p', 100_000) + ".txt";
        var longRelativePath = new string('r', 100_000);
        var max = AiCommandHandler.MaxLoggedTextLength;

        Ok<SearchResult>(await CallAsync(AiTools.Search, new SearchArgs(longQuery)));
        var search = Log.Last.Details;
        var added = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([longPath])));
        var add = Log.Last.Details;
        var reply = Failed(await CallAsync(AiTools.AddFiles, new AddFilesArgs([longRelativePath])), AiErrorCodes.InvalidArgument);
        var invalid = Log.Last.Details;
        var paths = Enumerable.Range(0, 60).Select(i => Path.Combine(Root, "missing", $"{i}.txt")).ToList();
        var many = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs(paths)));
        var manyPaths = Log.Last.Details;
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");
        Failed(await CallAsync(AiTools.RenameFile, new RenameFileArgs(a.Id, new string('n', 50_000) + ".pdf")), AiErrorCodes.Refused);
        var rename = Log.Last.Details;

        Assert.Equal(longQuery[..max] + "…", search.Query);
        Assert.True(search.ToJson().Length < max + 200);
        Assert.Single(added.Failures);
        Assert.Equal(longPath[..max] + "…", Assert.Single(add.Paths!));
        Assert.Contains(longRelativePath, reply); // the AI still hears the whole thing
        Assert.Equal(max + 1, invalid.Error!.Length);
        Assert.EndsWith("…", invalid.Error);
        Assert.Equal(longRelativePath[..max] + "…", Assert.Single(invalid.Paths!));
        Assert.Equal(60, many.Failures.Count);
        Assert.Equal(paths.Take(50), manyPaths.Paths);
        Assert.Equal(max + 1, rename.NewName!.Length);
        Assert.True(rename.ToJson().Length < 3 * max);

        // A cut never splits a surrogate pair.
        var emoji = new string('x', max - 1) + "\U0001F600" + "tail";
        var capped = AiCommandHandler.ForLog(new AiActivityDetails { Query = emoji }).Query!;
        Assert.Equal(new string('x', max - 1) + "…", capped);
    }

    // ================================================================== F7: replies don't wait for a running vault operation

    /// <summary>
    /// HandleAsync awaits the activity log entry before it answers ("stored before the AI hears back"). Move vault and Backup
    /// hold the library's write lock for their whole duration, but the real log (AiActivityLog over LibraryService.AddActivityAsync)
    /// appends in a short transaction of its own, without that lock. So while either operation is paused in the middle, holding
    /// the lock (an ordinary library write started then really waits), a read, a "busy" refusal and get_operation_status are
    /// answered at once, and each of them is already in the log.
    /// </summary>
    [Theory]
    [InlineData("move vault")]
    [InlineData("backup")]
    public async Task F7_Replies_and_their_log_entries_do_not_wait_for_a_running_vault_operation(string operation)
    {
        var docs = await BuiltInAsync("documents");
        await AddAsync(docs.Id, "a.txt", "hello");
        await AddAsync(docs.Id, "b.txt", "world");
        var pause = new Pause();
        var log = new AiActivityLog(Library);
        Host.IsOperationRunning = true; // as the app marks it while the operation runs
        var handler = new AiCommandHandler(Library, log, Changes, Host, VaultOperations, Operations);

        Task running;
        if (operation == "backup")
        {
            running = Library.CreateBackupAsync(Path.Combine(Root, "backup.zip"), new PauseAtStage(pause, "zipping"));
        }
        else
        {
            var hook = Library.GetType().GetProperty("AfterMoveVaultFileCopied", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Xunit.Sdk.XunitException("LibraryService.AfterMoveVaultFileCopied is gone; hold the write lock another way.");
            hook.SetValue(Library, (Action<string>)(_ => pause.HoldOnce()));
            running = Library.MoveVaultAsync(Path.Combine(Root, "new vault"));
        }

        Task write;
        try
        {
            await pause.Reached.WaitAsync(TimeSpan.FromSeconds(20));

            // The operation holds the write lock: an ordinary library write waits for it.
            write = Library.SetCategoryPinnedAsync(docs.Id, true);

            var read = await handler.HandleAsync(Wire(AiTools.ListTags, new ListTagsArgs())).WaitAsync(TimeSpan.FromSeconds(10));
            var busy = await handler.HandleAsync(Wire(AiTools.CreateNote, new CreateNoteArgs("x", docs.Id))).WaitAsync(TimeSpan.FromSeconds(10));
            var status = await handler.HandleAsync(Wire(AiTools.GetOperationStatus, new GetOperationStatusArgs())).WaitAsync(TimeSpan.FromSeconds(10));

            Ok<ListTagsResult>(read);
            Failed(busy, AiErrorCodes.Busy);
            Failed(status, AiErrorCodes.NotFound);
            var logged = await log.GetAsync(new ActivityQuery()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([AiTools.GetOperationStatus, AiTools.CreateNote, AiTools.ListTags], logged.Entries.Select(e => e.Action));
            Assert.False(running.IsCompleted, "the operation should still be paused");
            Assert.False(write.IsCompleted, "a library write should wait for the paused operation");
        }
        finally
        {
            pause.Release();
        }

        await running.WaitAsync(TimeSpan.FromSeconds(30));
        await write.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ================================================================== F8: remove_from_category is a way into Deleted

    /// <summary>
    /// The UI offers "Remove from this category" only for files that are also elsewhere, and asks before a file that is only here
    /// ends up in Deleted. remove_from_category can't ask, so it refuses such files, naming them and pointing to delete_files and
    /// move_files; nothing changes, not even for the files that are elsewhere too. A file only in the category and one of its
    /// subcategories counts as only here (removing takes it out of both), as in the UI.
    /// </summary>
    [Fact]
    public async Task F8_Remove_from_category_refuses_files_that_would_end_up_in_Deleted()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var python = await Library.CreateCategoryAsync("Python", work.Id);
        var deleted = await BuiltInAsync("deleted");
        var a = await AddAsync(docs.Id, "a.pdf");
        var both = await AddAsync(docs.Id, "both.pdf");
        await Library.AddToCategoryAsync([both.Id], work.Id);
        var nested = await AddAsync(work.Id, "nested.py");
        await Library.AddToCategoryAsync([nested.Id], python.Id);
        Failed(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([a.Id], docs.Id, deleted.Id)), AiErrorCodes.Refused);

        var message = Failed(await CallAsync(AiTools.RemoveFromCategory, new FilesCategoryArgs([both.Id, a.Id], docs.Id)), AiErrorCodes.Refused);
        Assert.Contains($"The file with id {a.Id} is only in \"Documents\"", message);
        Assert.Contains("delete_files", message);
        Assert.Contains("move_files", message);
        var nestedMessage = Failed(await CallAsync(AiTools.RemoveFromCategory, new FilesCategoryArgs([nested.Id], work.Id)), AiErrorCodes.Refused);
        Assert.Contains($"The file with id {nested.Id} is only in \"Work\" and its subcategories", nestedMessage);

        var file = (await Library.GetItemAsync(a.Id))!;
        Assert.Equal([docs.Id], file.CategoryIds);
        Assert.Equal(docs.Id, file.HomeCategoryId);
        Assert.Equal(a.FullPath, file.FullPath);
        Assert.Equal([docs.Id, work.Id], (await Library.GetItemAsync(both.Id))!.CategoryIds.Order());
        Assert.Equal([work.Id, python.Id], (await Library.GetItemAsync(nested.Id))!.CategoryIds.Order());
        Assert.Equal(0, (await Library.GetCategorySummaryAsync(deleted.Id))!.ItemCount);
        Assert.Empty(Changes.Reported);

        // A file that is in another category too is removed from this one.
        var removed = Ok<AiChangeResult>(await CallAsync(AiTools.RemoveFromCategory, new FilesCategoryArgs([both.Id], docs.Id)));
        Assert.Equal([work.Id], Assert.Single(removed.Files!).CategoryIds);
    }

    // ================================================================== helpers

    /// <summary>Holds a long library operation at a point (a test hook or a progress report) until the test releases it.</summary>
    private sealed class Pause
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new();
        private int _held;

        public Task Reached => _reached.Task;

        /// <summary>The first call blocks the calling thread (the operation's) until <see cref="Release"/>; later calls go on.</summary>
        public void HoldOnce()
        {
            if (Interlocked.Exchange(ref _held, 1) == 0)
            {
                _reached.TrySetResult();
                _released.Wait(TimeSpan.FromSeconds(30));
            }
        }

        public void Release() => _released.Set();
    }

    /// <summary>Pauses an operation at its first progress report of a stage (reports arrive synchronously, on its thread).</summary>
    private sealed class PauseAtStage(Pause pause, string stage) : IProgress<VaultOperationProgress>
    {
        public void Report(VaultOperationProgress value)
        {
            if (value.Stage == stage)
            {
                pause.HoldOnce();
            }
        }
    }

    /// <summary>The app's pieces for AI vault operations, created on the STA thread (like AiCommandVaultTests.Rig).</summary>
    private sealed class ReviewRig
    {
        public ReviewRig(AiCommandReviewTests test, ILibraryService library)
        {
            // No Application.Current: AppLifetime.ActivateMainWindow only records the request, nothing can appear.
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

        public async Task<OperationStatus> WaitForEndAsync(string operationId)
        {
            OperationStatus? status = null;
            await StaAsync.UntilAsync(async () =>
            {
                status = Ok<OperationStatus>(await CallAsync(AiTools.GetOperationStatus, new GetOperationStatusArgs(operationId)));
                return status.State != "running";
            }, "the operation to end");
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
