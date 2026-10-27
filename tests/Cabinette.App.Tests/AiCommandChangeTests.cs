using System.IO;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// The changing tools against a real library (stage 4, SPEC.md section 10): marked as added by the AI (unread), nothing into
/// Deleted, deletes only to the (test) Recycle Bin, the user's pointer and category-delete settings followed, every change
/// logged and reported.
/// </summary>
public sealed class AiCommandChangeTests : AiCommandTestBase
{
    // ================================================================== add_files

    [Fact]
    public async Task Add_files_to_a_named_category_keeps_them_there_and_counts_them_as_unread()
    {
        var spreadsheets = await BuiltInAsync("spreadsheets");
        var image = SourceFile("photo.png", "png bytes");
        var sheet = SourceFile("budget.xlsx", "xlsx bytes");

        var result = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([image, sheet], spreadsheets.Id, "copy")));

        Assert.Equal(["photo.png", "budget.xlsx"], result.Added.Select(f => f.Name));
        Assert.All(result.Added, f => Assert.Equal([spreadsheets.Id], f.CategoryIds)); // no sorting by type
        Assert.All(result.Added, f => Assert.Equal("ai", f.AddedBy));
        Assert.All(result.Added, f => Assert.Equal("copy", f.Mode));
        Assert.All(result.Added, f => Assert.True(File.Exists(f.Path)));
        Assert.True(File.Exists(image)); // the original stays
        Assert.Equal(2, (await Library.GetCategorySummaryAsync(spreadsheets.Id))!.UnreadCount);

        var change = Assert.Single(Changes.Reported);
        Assert.Equal(new AiChange(Client, AiChangeKind.FilesAdded, 2) { CategoryId = spreadsheets.Id, CategoryName = "Spreadsheets" }, change with { AddedByCategory = null });
        Assert.Null(change.AddedByCategory);

        var logged = Log.Last.Details;
        Assert.True(logged.Ok);
        Assert.Equal([image, sheet], logged.Paths);
        Assert.Equal("copy", logged.Mode);
        Assert.Equal(2, logged.FileCount);
        Assert.Equal(["photo.png", "budget.xlsx"], logged.Files!.Select(f => f.Name));
        Assert.Equal([new AiActivityRef(spreadsheets.Id, "Spreadsheets")], logged.Categories);
    }

    [Fact]
    public async Task Add_files_without_a_category_sorts_them_by_type()
    {
        var image = SourceFile("photo.png");
        var script = SourceFile("tool.py");
        var images = await BuiltInAsync("images");
        var python = await BuiltInAsync("code.python");

        var result = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([image, script])));

        Assert.Equal([images.Id], result.Added.Single(f => f.Name == "photo.png").CategoryIds);
        Assert.Equal([python.Id], result.Added.Single(f => f.Name == "tool.py").CategoryIds);
        var change = Assert.Single(Changes.Reported);
        Assert.Null(change.CategoryId);
        Assert.Equal(new Dictionary<string, int> { ["Images"] = 1, ["Python"] = 1 }, change.AddedByCategory);
        Assert.Equal(1, (await Library.GetCategorySummaryAsync(images.Id))!.UnreadCount);
    }

    [Fact]
    public async Task Add_files_follows_the_mode_or_the_default_add_mode()
    {
        var docs = await BuiltInAsync("documents");
        await UpdateSettingsAsync(s => s with { DefaultAddMode = ItemMode.Pointer });
        var linked = SourceFile("linked.pdf");
        var copied = SourceFile("copied.pdf");

        var byDefault = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([linked], docs.Id)));
        Assert.Equal("pointer", Log.Last.Details.Mode);
        var explicitCopy = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([copied], docs.Id, "COPY")));

        Assert.Equal("pointer", Assert.Single(byDefault.Added).Mode);
        Assert.Equal(linked, byDefault.Added[0].Path);
        Assert.Equal("copy", Assert.Single(explicitCopy.Added).Mode);
        Assert.NotEqual(copied, explicitCopy.Added[0].Path);
    }

    [Fact]
    public async Task Add_files_reports_duplicates_and_failures()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var existing = await AddAsync(docs.Id, "report.pdf", "same content");
        var again = SourceFile("report copy.pdf", "same content");
        var missing = Path.Combine(Root, "source", "missing.pdf");

        var result = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([again, missing], work.Id)));

        Assert.Empty(result.Added);
        var duplicate = Assert.Single(result.Duplicates);
        Assert.Equal(again, duplicate.Path);
        Assert.Equal(existing.Id, duplicate.Existing.Id);
        Assert.True(duplicate.AddedToCategory);
        Assert.Equal(missing, Assert.Single(result.Failures).Path);
        var change = Assert.Single(Changes.Reported);
        Assert.Equal((0, 1, 1, work.Id), (change.Count, change.Duplicates, change.Failures, change.CategoryId));
    }

    [Fact]
    public async Task A_folder_becomes_a_category()
    {
        var folder = Path.Combine(Root, "source", "Holiday");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "beach.jpg"), "jpg");

        var result = Ok<AddFilesResult>(await CallAsync(AiTools.AddFiles, new AddFilesArgs([folder])));

        var created = Assert.Single(result.CreatedCategoryIds);
        Assert.Equal("Holiday", (await Library.GetCategoryAsync(created))!.Name);
        Assert.Equal([created], Assert.Single(result.Added).CategoryIds);
        Assert.Equal("Holiday", Changes.Reported.Single().CategoryName);
    }

    [Fact]
    public async Task Nothing_can_be_added_to_Deleted()
    {
        var docs = await BuiltInAsync("documents");
        var project = await Library.CreateCategoryAsync("Project", null);
        var item = await AddAsync(project.Id, "a.pdf");
        await Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.MoveToDeleted);
        var deleted = await BuiltInAsync("deleted");
        var other = await AddAsync(docs.Id, "b.pdf");

        Failed(await CallAsync(AiTools.AddFiles, new AddFilesArgs([SourceFile("c.pdf")], deleted.Id)), AiErrorCodes.Refused);
        Failed(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("x", deleted.Id)), AiErrorCodes.Refused);
        Failed(await CallAsync(AiTools.AddToCategory, new FilesCategoryArgs([other.Id], deleted.Id)), AiErrorCodes.Refused);
        Assert.Contains("can't be moved into", Failed(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([other.Id], docs.Id, deleted.Id)), AiErrorCodes.Refused));
        Failed(await CallAsync(AiTools.CreateCategory, new CreateCategoryArgs("Sub", deleted.Id)), AiErrorCodes.Refused);
        Failed(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(deleted.Id)), AiErrorCodes.Refused);

        Assert.Equal([deleted.Id], (await Library.GetItemAsync(item.Id))!.CategoryIds);
        Assert.Equal([docs.Id], (await Library.GetItemAsync(other.Id))!.CategoryIds);
        Assert.NotNull(await Library.GetCategoryAsync(deleted.Id));
        Assert.Empty(Changes.Reported);
    }

    // ================================================================== notes, tags, stars

    [Fact]
    public async Task Create_note_goes_to_Notes_by_default_or_to_the_category()
    {
        var work = await Library.CreateCategoryAsync("Work", null);
        var notes = await BuiltInAsync("notes");

        var inNotes = Ok<FileResult>(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("Remember the milk"))).File;
        var inWork = Ok<FileResult>(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("Meeting at 10", work.Id))).File;

        Assert.Equal([notes.Id], inNotes.CategoryIds);
        Assert.Equal([work.Id], inWork.CategoryIds);
        Assert.Matches(@"^Note \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(\d+\))?\.md$", inNotes.Name);
        Assert.Equal("Remember the milk", await File.ReadAllTextAsync(inNotes.Path));
        Assert.Equal(1, (await Library.GetCategorySummaryAsync(work.Id))!.UnreadCount);
        Assert.Equal(
            [(AiChangeKind.NoteCreated, (long?)notes.Id, "Notes"), (AiChangeKind.NoteCreated, work.Id, "Work")],
            Changes.Reported.Select(c => (c.Kind, c.CategoryId, c.CategoryName)));
    }

    [Fact]
    public async Task Create_note_recreates_a_deleted_Notes_category()
    {
        var notes = await BuiltInAsync("notes");
        await Library.DeleteCategoryAsync(notes.Id, CategoryDeletePolicy.MoveToDeleted);

        var note = Ok<FileResult>(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("back again"))).File;

        var recreated = await Library.GetCategoryAsync(Assert.Single(note.CategoryIds));
        Assert.Equal("notes", recreated!.BuiltInKey);
    }

    [Fact]
    public async Task Tag_and_untag_files()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");
        var b = await AddAsync(docs.Id, "b.pdf");

        var tagged = Ok<AiChangeResult>(await CallAsync(AiTools.TagFiles, new TagFilesArgs([a.Id, b.Id, a.Id], ["#Invoice", "paid 2026", "invoice"])));
        Assert.Equal(["Invoice", "paid-2026"], Log.Last.Details.Tags);
        var untagged = Ok<AiChangeResult>(await CallAsync(AiTools.UntagFiles, new TagFilesArgs([b.Id], ["invoice"])));

        Assert.Equal(2, tagged.Count);
        Assert.All(tagged.Files!, f => Assert.Equal(["Invoice", "paid-2026"], f.Tags.Order(StringComparer.Ordinal)));
        Assert.Equal(["paid-2026"], Assert.Single(untagged.Files!).Tags);
        Assert.Equal(["Invoice", "paid-2026"], (await Library.GetItemAsync(a.Id))!.Tags.Order(StringComparer.Ordinal));
        Assert.Equal([(AiChangeKind.FilesTagged, 2), (AiChangeKind.FilesUntagged, 1)], Changes.Reported.Select(c => (c.Kind, c.Count)));
    }

    [Fact]
    public async Task Star_and_unstar_files()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");

        var starred = Ok<AiChangeResult>(await CallAsync(AiTools.StarFiles, new FileIdsArgs([a.Id])));
        Assert.NotNull(Assert.Single(starred.Files!).StarredAt);
        var unstarred = Ok<AiChangeResult>(await CallAsync(AiTools.UnstarFiles, new FileIdsArgs([a.Id])));

        Assert.Null(Assert.Single(unstarred.Files!).StarredAt);
        Assert.Null((await Library.GetItemAsync(a.Id))!.StarredAt);
        Assert.Equal([AiChangeKind.FilesStarred, AiChangeKind.FilesUnstarred], Changes.Reported.Select(c => c.Kind));
    }

    // ================================================================== categories of files

    [Fact]
    public async Task Add_to_and_remove_from_a_category()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var a = await AddAsync(docs.Id, "a.pdf");

        var added = Ok<AiChangeResult>(await CallAsync(AiTools.AddToCategory, new FilesCategoryArgs([a.Id], work.Id)));
        Assert.Equal(1, (await Library.GetCategorySummaryAsync(work.Id))!.UnreadCount);
        var removed = Ok<AiChangeResult>(await CallAsync(AiTools.RemoveFromCategory, new FilesCategoryArgs([a.Id], docs.Id)));

        Assert.Equal([docs.Id, work.Id], Assert.Single(added.Files!).CategoryIds.Order());
        Assert.Equal([work.Id], Assert.Single(removed.Files!).CategoryIds);
        Assert.Equal(
            [(AiChangeKind.FilesAddedToCategory, (long?)work.Id, "Work"), (AiChangeKind.FilesRemovedFromCategory, docs.Id, "Documents")],
            Changes.Reported.Select(c => (c.Kind, c.CategoryId, c.CategoryName)));
    }

    [Fact]
    public async Task Move_files_like_move_to_category_and_counts_as_unread()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var a = await AddAsync(docs.Id, "a.pdf");

        var moved = Ok<AiChangeResult>(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([a.Id], docs.Id, work.Id)));

        var file = Assert.Single(moved.Files!);
        Assert.Equal([work.Id], file.CategoryIds);
        Assert.Equal(work.Id, file.HomeCategoryId);
        Assert.StartsWith(Path.Combine(Vault, "Work") + Path.DirectorySeparatorChar, file.Path);
        Assert.True(File.Exists(file.Path));
        Assert.False(File.Exists(a.FullPath));
        Assert.Equal(1, (await Library.GetCategorySummaryAsync(work.Id))!.UnreadCount);
        var change = Assert.Single(Changes.Reported);
        Assert.Equal((AiChangeKind.FilesMoved, 1, (long?)work.Id), (change.Kind, change.Count, change.CategoryId));
        Assert.Equal(["Documents", "Work"], Log.Last.Details.Categories!.Select(c => c.Name));
    }

    // ================================================================== rename

    [Fact]
    public async Task Rename_file_reports_the_name_it_got()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");

        var renamed = Ok<FileResult>(await CallAsync(AiTools.RenameFile, new RenameFileArgs(a.Id, " report 2026.pdf "))).File;

        Assert.Equal("report 2026.pdf", renamed.Name);
        Assert.True(File.Exists(Path.Combine(Vault, "Documents", "report 2026.pdf")));
        Assert.Equal("report 2026.pdf", Assert.Single(Changes.Reported).Detail);
        Assert.Equal([new AiActivityRef(a.Id, "a.pdf")], Log.Last.Details.Files); // the name before
        Assert.Equal("report 2026.pdf", Log.Last.Details.NewName);
    }

    [Fact]
    public async Task Rename_file_refuses_an_invalid_name()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");

        Failed(await CallAsync(AiTools.RenameFile, new RenameFileArgs(a.Id, @"..\outside.pdf")), AiErrorCodes.Refused);

        Assert.Equal("a.pdf", (await Library.GetItemAsync(a.Id))!.FileName);
        Assert.Empty(Changes.Reported);
    }

    // ================================================================== delete_files: Recycle Bin only

    [Fact]
    public async Task Delete_files_goes_to_the_Recycle_Bin_even_when_the_setting_says_permanent()
    {
        await UpdateSettingsAsync(s => s with { RecycleVaultFiles = false });
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");
        var b = await AddAsync(docs.Id, "b.pdf");

        var result = Ok<AiChangeResult>(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([a.Id, b.Id])));

        Assert.Equal(2, result.Count);
        Assert.Null(result.Files);
        Assert.Equal([a.FullPath, b.FullPath], RecycleBin.Recycled.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Null(await Library.GetItemAsync(a.Id));
        Assert.Equal((AiChangeKind.FilesDeleted, 2), (Changes.Reported.Single().Kind, Changes.Reported.Single().Count));
        Assert.Equal(["a.pdf", "b.pdf"], Log.Last.Details.Files!.Select(f => f.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_a_linked_file_follows_the_pointer_setting(bool deleteRealFile)
    {
        await UpdateSettingsAsync(s => s with { DeleteRealPointerFiles = deleteRealFile });
        var docs = await BuiltInAsync("documents");
        var linked = await AddAsync(docs.Id, "linked.pdf", mode: ItemMode.Pointer);

        Ok<AiChangeResult>(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([linked.Id])));

        Assert.Null(await Library.GetItemAsync(linked.Id));
        Assert.Equal(!deleteRealFile, File.Exists(linked.FullPath));
        Assert.Equal(deleteRealFile ? [linked.FullPath] : [], RecycleBin.Recycled);
    }

    [Fact]
    public async Task A_file_the_Recycle_Bin_cant_take_fails_the_delete_before_anything_changes()
    {
        var docs = await BuiltInAsync("documents");
        var small = await AddAsync(docs.Id, "small.pdf");
        var huge = await AddAsync(docs.Id, "huge.bin");
        RecycleBin.TooLarge.Add(huge.FullPath);

        var message = Failed(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([small.Id, huge.Id])), AiErrorCodes.RecycleBinUnavailable);

        Assert.Contains("huge.bin", message);
        Assert.Contains("still on disk", message);
        Assert.DoesNotContain("left the library", message);
        Assert.NotNull(await Library.GetItemAsync(small.Id));
        Assert.NotNull(await Library.GetItemAsync(huge.Id));
        Assert.True(File.Exists(small.FullPath));
        Assert.True(File.Exists(huge.FullPath));
        Assert.Empty(RecycleBin.Recycled);
        Assert.Empty(Changes.Reported);
        Assert.Equal(AiErrorCodes.RecycleBinUnavailable, Log.Last.Details.ErrorCode);
    }

    /// <summary>
    /// The late refusal (SPEC.md section 6): Windows refuses a file only after the delete went through. The AI gets
    /// recycle_bin_unavailable, the file stays on disk (never deleted permanently), and the message says the entry already left.
    /// </summary>
    [Fact]
    public async Task A_late_Recycle_Bin_refusal_keeps_the_file_on_disk_and_says_so()
    {
        var docs = await BuiltInAsync("documents");
        var huge = await AddAsync(docs.Id, "huge.bin", "too large for the Recycle Bin");
        RecycleBin.Refused.Add(huge.FullPath);

        var message = Failed(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([huge.Id])), AiErrorCodes.RecycleBinUnavailable);

        Assert.True(File.Exists(huge.FullPath));
        Assert.Equal("too large for the Recycle Bin", await File.ReadAllTextAsync(huge.FullPath));
        Assert.Contains("still on disk", message);
        Assert.Contains("1 of the 1 files left the library", message);
        Assert.Null(await Library.GetItemAsync(huge.Id));
        Assert.Empty(RecycleBin.Recycled);
        Assert.Empty(Changes.Reported);
    }

    // ================================================================== categories

    [Fact]
    public async Task Create_and_rename_a_category()
    {
        var docs = await BuiltInAsync("documents");

        var created = Ok<CategoryResult>(await CallAsync(AiTools.CreateCategory, new CreateCategoryArgs(" Taxes ", docs.Id))).Category;
        var renamed = Ok<CategoryResult>(await CallAsync(AiTools.RenameCategory, new RenameCategoryArgs(created.Id, "Taxes 2026"))).Category;

        Assert.Equal(("Taxes", "Documents/Taxes", "user", (long?)docs.Id), (created.Name, created.Path, created.Kind, created.ParentId));
        Assert.True(Directory.Exists(Path.Combine(Vault, "Documents", "Taxes 2026")));
        Assert.Equal(("Taxes 2026", "Documents/Taxes 2026"), (renamed.Name, renamed.Path));
        Assert.Equal(
            [(AiChangeKind.CategoryCreated, "Taxes", (string?)null), (AiChangeKind.CategoryRenamed, "Taxes 2026", "Taxes 2026")],
            Changes.Reported.Select(c => (c.Kind, c.CategoryName!, c.Detail)));
        Assert.Equal([new AiActivityRef(created.Id, "Taxes")], Log.Last.Details.Categories); // the name before
        Assert.Equal("Taxes 2026", Log.Last.Details.NewName);
    }

    [Theory]
    [InlineData(CategoryDeletePolicy.MoveToDeleted)]
    [InlineData(CategoryDeletePolicy.Ask)]
    public async Task Delete_category_moves_its_files_to_Deleted_when_the_setting_says_so_or_asks(CategoryDeletePolicy policy)
    {
        await UpdateSettingsAsync(s => s with { CategoryDeletePolicy = policy });
        var project = await Library.CreateCategoryAsync("Project", null);
        var drafts = await Library.CreateCategoryAsync("Drafts", project.Id);
        var a = await AddAsync(drafts.Id, "a.txt");

        var result = Ok<DeleteCategoryResult>(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(project.Id)));

        Assert.Equal(("moved_to_deleted", 2), (result.Files, result.Categories));
        Assert.Null(await Library.GetCategoryAsync(project.Id));
        Assert.Null(await Library.GetCategoryAsync(drafts.Id));
        var item = (await Library.GetItemAsync(a.Id))!;
        Assert.Equal([(await BuiltInAsync("deleted")).Id], item.CategoryIds);
        Assert.True(File.Exists(item.FullPath));
        Assert.Empty(RecycleBin.Recycled);
        var change = Assert.Single(Changes.Reported);
        Assert.Equal((AiChangeKind.CategoryDeleted, 2, (long?)project.Id, "Project"), (change.Kind, change.Count, change.CategoryId, change.CategoryName));
        Assert.Equal(1, Log.Last.Details.FileCount);
    }

    [Fact]
    public async Task Delete_category_recycles_its_files_when_the_setting_says_so_never_permanently()
    {
        await UpdateSettingsAsync(s => s with { CategoryDeletePolicy = CategoryDeletePolicy.RecycleFiles, RecycleVaultFiles = false });
        var project = await Library.CreateCategoryAsync("Project", null);
        var a = await AddAsync(project.Id, "a.txt");

        var result = Ok<DeleteCategoryResult>(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(project.Id)));

        Assert.Equal(("recycled", 1), (result.Files, result.Categories));
        Assert.Null(await Library.GetItemAsync(a.Id));
        Assert.Equal([a.FullPath], RecycleBin.Recycled);
    }

    [Fact]
    public async Task Delete_category_with_a_file_the_Recycle_Bin_cant_take_changes_nothing()
    {
        await UpdateSettingsAsync(s => s with { CategoryDeletePolicy = CategoryDeletePolicy.RecycleFiles });
        var project = await Library.CreateCategoryAsync("Project", null);
        var huge = await AddAsync(project.Id, "huge.bin");
        RecycleBin.TooLarge.Add(huge.FullPath);

        var message = Failed(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(project.Id)), AiErrorCodes.RecycleBinUnavailable);

        Assert.Contains("as they were", message);
        Assert.NotNull(await Library.GetCategoryAsync(project.Id));
        Assert.NotNull(await Library.GetItemAsync(huge.Id));
        Assert.True(File.Exists(huge.FullPath));
        Assert.Empty(Changes.Reported);
    }

    [Fact]
    public async Task Delete_category_with_a_late_Recycle_Bin_refusal_keeps_the_file()
    {
        await UpdateSettingsAsync(s => s with { CategoryDeletePolicy = CategoryDeletePolicy.RecycleFiles });
        var project = await Library.CreateCategoryAsync("Project", null);
        var huge = await AddAsync(project.Id, "huge.bin");
        RecycleBin.Refused.Add(huge.FullPath);

        var message = Failed(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(project.Id)), AiErrorCodes.RecycleBinUnavailable);

        Assert.Contains("still on disk", message);
        Assert.True(File.Exists(huge.FullPath));
        Assert.Empty(RecycleBin.Recycled);
        Assert.Empty(Changes.Reported);
    }

    // ================================================================== the log

    [Fact]
    public async Task Every_call_is_logged_with_the_client_and_tool_success_or_failure()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");

        await CallAsync(AiTools.StarFiles, new FileIdsArgs([a.Id]), client: "Codex");
        await CallAsync(AiTools.StarFiles, new FileIdsArgs([424242]), client: "Codex");
        await CallAsync(AiTools.ListTags, new ListTagsArgs(), client: "Codex");

        Assert.Equal(
            [("Codex", AiTools.StarFiles, true, (string?)null), ("Codex", AiTools.StarFiles, false, AiErrorCodes.NotFound), ("Codex", AiTools.ListTags, true, null)],
            Log.Entries.Select(e => (e.Client, e.Tool, e.Details.Ok, e.Details.ErrorCode)));
        Assert.Null(Log.Entries[0].Details.Error);
        Assert.Contains("424242", Log.Entries[1].Details.Error);

        // What Settings › AI reads back.
        var json = Log.Entries[0].Details.ToJson();
        Assert.Equal(Log.Entries[0].Details.Files, AiActivityDetails.TryParse(json)!.Files);
    }

    [Fact]
    public async Task The_log_lists_at_most_50_files_by_name_plus_the_count()
    {
        var docs = await BuiltInAsync("documents");
        var ids = new List<long>();
        for (var i = 0; i < AiActivityDetails.MaxRefs + 5; i++)
        {
            ids.Add((await AddAsync(docs.Id, $"f{i}.txt")).Id);
        }

        Ok<AiChangeResult>(await CallAsync(AiTools.StarFiles, new FileIdsArgs(ids)));

        Assert.Equal(AiActivityDetails.MaxRefs, Log.Last.Details.Files!.Count);
        Assert.Equal(ids.Count, Log.Last.Details.FileCount);
        Assert.Equal(ids.Count, Changes.Reported.Single().Count);
    }
}
