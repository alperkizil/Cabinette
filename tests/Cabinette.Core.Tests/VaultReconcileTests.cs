using System.IO.Hashing;
using System.Text;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Tests;

/// <summary>Reconcile logic of the vault monitor, driven directly (no watcher), so every case is deterministic.</summary>
public sealed class VaultReconcileTests
{
    [Fact]
    public async Task NewFileInCategoryFolder_IsAddedUnreadAndHashed_AndApplyingAgainChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var path = t.WriteVaultFile(@"Images\photo.jpg", "jpeg bytes");
        t.ClearEvents();

        var summary = await t.ApplyBatchAsync(["Images"]);

        Assert.Equal(1, summary.AddedByCategory["Images"]);
        var item = Assert.Single(await t.Library.GetThreadItemsAsync(images.Id));
        Assert.Equal("photo.jpg", item.FileName);
        Assert.Equal(path, item.FullPath);
        Assert.Equal(ItemMode.Copy, item.Mode);
        Assert.Equal(AddedBy.VaultMonitor, item.AddedBy);
        Assert.Equal(XxHash128.Hash(Encoding.UTF8.GetBytes("jpeg bytes")), item.Hash);
        Assert.Equal(10, item.SizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(path), item.FileModifiedAt!.Value.UtcDateTime);
        Assert.Equal(1, (await t.SummaryAsync(images.Id)).UnreadCount);

        var evt = Assert.Single(t.Events);
        Assert.Equal(AddedBy.VaultMonitor, evt.Source);
        Assert.Contains(item.Id, evt.ItemIds);
        Assert.Contains(images.Id, evt.CategoryIds);

        t.ClearEvents();
        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.True((await t.ApplyBatchAsync(["Images", ""])).IsEmpty);
        Assert.Empty(t.Events);
    }

    [Fact]
    public async Task NewFileInSubcategoryFolder_GoesToThatSubcategory_AndParentsAreNotified()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        t.WriteVaultFile(@"Code\Python\script.py", "print(1)");
        t.ClearEvents();

        var summary = await t.RescanAsync();

        Assert.Equal(1, summary.AddedByCategory["Python"]);
        var item = Assert.Single(await t.Library.GetThreadItemsAsync(python.Id));
        Assert.Equal(python.Id, item.HomeCategoryId);
        Assert.Contains(code.Id, Assert.Single(t.Events).CategoryIds);
    }

    [Fact]
    public async Task FilePlacedInVaultRoot_MovesIntoItsTypeCategory_WithAUniqueName()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents.pdf");
        var existing = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "first"));
        var loose = t.WriteVaultFile("report.pdf", "second");

        var summary = await t.ApplyBatchAsync([""]);

        Assert.Equal(1, summary.AddedByCategory["PDF"]);
        Assert.False(File.Exists(loose));
        var items = await t.Library.GetThreadItemsAsync(docs.Id);
        Assert.Equal(["report.pdf", "report (1).pdf"], items.Select(i => i.FileName).ToArray());
        var added = items.Single(i => i.Id != existing.Id);
        Assert.Equal(t.VaultPath(@"Documents\PDF\report (1).pdf"), added.FullPath);
        Assert.Equal("second", File.ReadAllText(added.FullPath));
        Assert.Equal(AddedBy.VaultMonitor, added.AddedBy);

        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task DeletedFile_IsRemovedFromTheLibrary_AndNothingElseIsTouched()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var notes = await t.BuiltInAsync("notes");
        var gone = await t.AddOneAsync(docs.Id, t.CreateFile("gone.txt", "gone"));
        var stays = await t.AddOneAsync(docs.Id, t.CreateFile("stays.txt", "stays"));
        await t.Library.AddToCategoryAsync([gone.Id], notes.Id);
        await t.Library.AddTagsAsync([gone.Id], ["only-on-gone"]);
        File.Delete(gone.FullPath);
        t.ClearEvents();

        var summary = await t.ApplyBatchAsync(["Documents"]);

        Assert.Equal(1, summary.Removed);
        Assert.Null(await t.Library.GetItemAsync(gone.Id));
        Assert.Empty(await t.Library.GetThreadItemsAsync(notes.Id));
        Assert.True(File.Exists(stays.FullPath));
        Assert.NotNull(await t.Library.GetItemAsync(stays.Id));
        Assert.Empty(await t.Library.GetTagsAsync());
        Assert.Empty(t.RecycleBin.Recycled);
        var evt = Assert.Single(t.Events);
        Assert.Contains(notes.Id, evt.CategoryIds);
    }

    [Fact]
    public async Task Batch_OnlyLooksAtTheFoldersThatChanged()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        File.Delete(item.FullPath);

        Assert.True((await t.ApplyBatchAsync(["Images"])).IsEmpty);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));

        Assert.Equal(1, (await t.ApplyBatchAsync(["Documents"])).Removed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenamedFile_KeepsItsIdTagsAndCategories(bool watcherReportedTheRename)
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var notes = await t.BuiltInAsync("notes");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("draft.txt", "the content"));
        await t.Library.AddToCategoryAsync([item.Id], notes.Id);
        await t.Library.AddTagsAsync([item.Id], ["keep"]);
        File.Move(item.FullPath, t.VaultPath(@"Documents\final.md"));

        var summary = watcherReportedTheRename
            ? await t.ApplyBatchAsync(["Documents"], (@"Documents\draft.txt", @"Documents\final.md"))
            : await t.RescanAsync();

        Assert.Equal(1, summary.Renamed);
        Assert.Equal(0, summary.Removed);
        Assert.Empty(summary.AddedByCategory);
        var after = await t.ItemAsync(item.Id);
        Assert.Equal("final.md", after.FileName);
        Assert.Equal("md", after.Extension);
        Assert.Equal(t.VaultPath(@"Documents\final.md"), after.FullPath);
        Assert.Equal(["keep"], after.Tags);
        Assert.Equal([docs.Id, notes.Id], after.CategoryIds.Order().ToArray());
        Assert.Equal(item.Hash, after.Hash);
        Assert.Single((await t.Library.SearchAsync("final")).Items);
    }

    [Fact]
    public async Task LetterCaseRename_UpdatesTheName()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("readme.txt", "r"));
        File.Move(item.FullPath, t.VaultPath(@"Documents\README.txt"));

        var summary = await t.ApplyBatchAsync(["Documents"], (@"Documents\readme.txt", @"Documents\README.txt"));

        Assert.Equal(1, summary.Renamed);
        Assert.Equal("README.txt", (await t.ItemAsync(item.Id)).FileName);
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileMovedToAnotherCategoryFolder_KeepsTags_AndMovesToThatCategory(bool watcherReportedTheMove)
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var notes = await t.BuiltInAsync("notes");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("plan.txt", "the plan"));
        await t.Library.AddToCategoryAsync([item.Id], notes.Id);
        await t.Library.AddTagsAsync([item.Id], ["work", "2026"]);
        File.Move(item.FullPath, t.VaultPath(@"Text\plan.txt"));
        t.ClearEvents();

        var summary = watcherReportedTheMove
            ? await t.ApplyBatchAsync(["Documents", "Text"], (@"Documents\plan.txt", @"Text\plan.txt"))
            : await t.RescanAsync();

        Assert.Equal(1, summary.Moved);
        Assert.Equal(0, summary.Removed);
        Assert.Empty(summary.AddedByCategory);
        var after = await t.ItemAsync(item.Id);
        Assert.Equal(text.Id, after.HomeCategoryId);
        Assert.Equal([text.Id, notes.Id], after.CategoryIds.Order().ToArray());
        Assert.Equal(["2026", "work"], after.Tags);
        Assert.Equal(t.VaultPath(@"Text\plan.txt"), after.FullPath);
        Assert.Empty(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Equal(1, (await t.SummaryAsync(text.Id)).UnreadCount);
        var evt = Assert.Single(t.Events);
        Assert.Contains(docs.Id, evt.CategoryIds);
        Assert.Contains(text.Id, evt.CategoryIds);
    }

    [Fact]
    public async Task ModifiedFile_UpdatesSizeTimeAndHash()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("data.csv", "a,b"));
        var later = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        t.WriteVaultFile(@"Documents\data.csv", "a,b\n1,2", later);
        t.ClearEvents();

        var summary = await t.ApplyBatchAsync(["Documents"]);

        Assert.Equal(1, summary.Modified);
        var after = await t.ItemAsync(item.Id);
        Assert.Equal(7, after.SizeBytes);
        Assert.Equal(later, after.FileModifiedAt!.Value.UtcDateTime);
        Assert.Equal(XxHash128.Hash(Encoding.UTF8.GetBytes("a,b\n1,2")), after.Hash);
        Assert.Contains(item.Id, Assert.Single(t.Events).ItemIds);
    }

    [Fact]
    public async Task TouchedFileWithSameContent_OnlyRecordsTheNewTime_Silently()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("same.txt", "same"));
        var later = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(item.FullPath, later);
        t.ClearEvents();

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Empty(t.Events);
        Assert.Equal(later, (await t.ItemAsync(item.Id)).FileModifiedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task EditedNote_RefreshesPreviewAndSearchText()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notes = await t.BuiltInAsync("notes");
        var note = await t.Library.CreateNoteAsync(notes.Id, "old words");
        File.WriteAllText(note.FullPath, "fresh thoughts about zebras");

        var summary = await t.ApplyBatchAsync(["Notes"]);

        Assert.Equal(1, summary.Modified);
        var after = await t.ItemAsync(note.Id);
        Assert.Equal("fresh thoughts about zebras", after.NotePreview);
        Assert.Equal(note.Id, Assert.Single(await t.Library.GetThreadItemsAsync(notes.Id, "zebras")).Id);
        Assert.Empty(await t.Library.GetThreadItemsAsync(notes.Id, "old words"));
    }

    [Fact]
    public async Task NewFolder_BecomesACategory_SubfoldersSubcategories_FilesImported()
    {
        await using var t = await TestLibrary.CreateAsync();
        t.WriteVaultFile(@"Trips\paris.jpg", "paris");
        t.WriteVaultFile(@"Trips\2025\rome.jpg", "rome");
        Directory.CreateDirectory(t.VaultPath(@"Trips\Empty"));
        var hidden = Directory.CreateDirectory(t.VaultPath(@"Trips\.git"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(hidden.FullName, "config"), "x");

        var summary = await t.ApplyBatchAsync([""]);

        Assert.Equal(["2025", "Empty", "Trips"], summary.CategoriesCreated.Order().ToArray());
        Assert.Equal(1, summary.AddedByCategory["Trips"]);
        Assert.Equal(1, summary.AddedByCategory["2025"]);
        var trips = (await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical)).Single(s => s.Category.Name == "Trips");
        Assert.Equal(CategoryKind.User, trips.Category.Kind);
        Assert.Equal("folder", trips.Category.IconKey);
        Assert.Equal("Trips", trips.Category.FolderName);
        Assert.Equal(2, trips.ItemCount);
        Assert.Equal(2, trips.UnreadCount);
        var subs = await t.Library.GetCategoriesAsync(trips.Category.Id, CategorySort.Alphabetical);
        Assert.Equal(["2025", "Empty"], subs.Select(s => s.Category.Name).ToArray());
        var rome = Assert.Single(await t.Library.GetThreadItemsAsync(subs[0].Category.Id));
        Assert.Equal(t.VaultPath(@"Trips\2025\rome.jpg"), rome.FullPath);

        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenamedFolder_RenamesTheCategory_KeepingIdIconPinAndFiles(bool watcherReportedTheRename)
    {
        await using var t = await TestLibrary.CreateAsync();
        var projects = await t.Library.CreateCategoryAsync("Projects", null, iconPng: [1, 2, 3]);
        await t.Library.SetCategoryPinnedAsync(projects.Id, true);
        var sub = await t.Library.CreateCategoryAsync("Sub", projects.Id);
        var a = await t.AddOneAsync(projects.Id, t.CreateFile("a.txt", "a"));
        var b = await t.AddOneAsync(sub.Id, t.CreateFile("b.txt", "b"));
        await t.Library.AddTagsAsync([a.Id], ["tagged"]);
        Directory.Move(t.VaultPath("Projects"), t.VaultPath("Work"));

        var summary = watcherReportedTheRename
            ? await t.ApplyBatchAsync([""], ("Projects", "Work"))
            : await t.RescanAsync();

        Assert.Empty(summary.CategoriesCreated);
        Assert.Empty(summary.CategoriesRemoved);
        Assert.Equal(0, summary.Removed);
        Assert.Empty(summary.AddedByCategory);
        var renamed = await t.CategoryAsync(projects.Id);
        Assert.Equal("Work", renamed.Name);
        Assert.Equal("Work", renamed.FolderName);
        Assert.True(renamed.IsPinned);
        Assert.Equal([1, 2, 3], renamed.IconPng);
        Assert.Equal(t.VaultPath(@"Work\a.txt"), (await t.ItemAsync(a.Id)).FullPath);
        Assert.Equal(["tagged"], (await t.ItemAsync(a.Id)).Tags);
        Assert.Equal(t.VaultPath(@"Work\Sub\b.txt"), (await t.ItemAsync(b.Id)).FullPath);
        Assert.Equal(projects.Id, (await t.CategoryAsync(sub.Id)).ParentId);
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task FolderMovedIntoAnotherCategoryFolder_BecomesItsSubcategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var projects = await t.Library.CreateCategoryAsync("Projects", null);
        var a = await t.AddOneAsync(projects.Id, t.CreateFile("a.txt", "a"));
        Directory.Move(t.VaultPath("Projects"), t.VaultPath(@"Documents\Projects"));

        var summary = await t.ApplyBatchAsync(["", "Documents"], ("Projects", @"Documents\Projects"));

        Assert.True(summary.IsEmpty);
        var moved = await t.CategoryAsync(projects.Id);
        Assert.Equal(docs.Id, moved.ParentId);
        Assert.Equal("Projects", moved.Name);
        Assert.Equal(t.VaultPath(@"Documents\Projects\a.txt"), (await t.ItemAsync(a.Id)).FullPath);
    }

    [Fact]
    public async Task DeletedFolder_RemovesItsCategorySubtree_AndTheFilesThatWereInside()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var old = await t.Library.CreateCategoryAsync("Old", null);
        var sub = await t.Library.CreateCategoryAsync("Sub", old.Id);
        var inside = await t.AddOneAsync(old.Id, t.CreateFile("inside.txt", "inside"));
        var deeper = await t.AddOneAsync(sub.Id, t.CreateFile("deeper.txt", "deeper"));
        var linked = await t.AddOneAsync(docs.Id, t.CreateFile("linked.txt", "linked"));
        await t.Library.AddToCategoryAsync([linked.Id], sub.Id);
        var pointer = await t.AddOneAsync(sub.Id, t.CreateFile("pointer.txt", "pointer"), ItemMode.Pointer);
        Directory.Delete(t.VaultPath("Old"), recursive: true);

        var summary = await t.ApplyBatchAsync([""]);

        Assert.Equal(["Old", "Sub"], summary.CategoriesRemoved.Order().ToArray());
        Assert.Equal(2, summary.Removed);
        Assert.Null(await t.Library.GetCategoryAsync(old.Id));
        Assert.Null(await t.Library.GetCategoryAsync(sub.Id));
        Assert.Null(await t.Library.GetItemAsync(inside.Id));
        Assert.Null(await t.Library.GetItemAsync(deeper.Id));

        // A vault file filed elsewhere only loses the membership; a pointer file is never touched and goes to Deleted.
        Assert.Equal([docs.Id], (await t.ItemAsync(linked.Id)).CategoryIds);
        Assert.True(File.Exists(linked.FullPath));
        var deletedId = (await t.BuiltInAsync("deleted")).Id;
        var pointerAfter = await t.ItemAsync(pointer.Id);
        Assert.Equal(deletedId, pointerAfter.HomeCategoryId);
        Assert.True(File.Exists(pointerAfter.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task DuplicateContent_IsKeptAsItsOwnItem_AndReported()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var original = await t.AddOneAsync(docs.Id, t.CreateFile("original.txt", "same content"));
        var copy = t.WriteVaultFile(@"Text\copy.txt", "same content");

        var summary = await t.ApplyBatchAsync(["Text"]);

        var duplicate = Assert.Single(summary.Duplicates);
        Assert.Equal(copy, duplicate.SourcePath);
        Assert.Equal(original.Id, duplicate.Existing.Id);
        Assert.False(duplicate.AddedToTargetCategory);
        Assert.Equal(1, summary.AddedByCategory["Text"]);
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(original.FullPath));
        Assert.Single(await t.Library.GetThreadItemsAsync(text.Id));
        Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));
    }

    [Fact]
    public async Task CabinettesOwnOperations_LeaveNothingToReconcile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var notes = await t.BuiltInAsync("notes");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var b = await t.AddOneAsync(null, t.CreateFile("b.png", "b"));
        await t.Library.RenameItemAsync(a.Id, "renamed.txt");
        await t.Library.CreateNoteAsync(notes.Id, "a note");
        await t.Library.AddPastedTextAsync(notes.Id, "https://example.com");
        await t.Library.AddPastedImageAsync(notes.Id, [1, 2, 3, 4]);
        var created = await t.Library.CreateCategoryAsync("Mine", null);
        await t.Library.RenameCategoryAsync(created.Id, "Ours");
        await t.AddOneAsync(created.Id, t.CreateFile("c.txt", "c"));
        await t.Library.AddToCategoryAsync([b.Id], notes.Id);
        await t.Library.RemoveFromCategoryAsync([b.Id], (await t.BuiltInAsync("images")).Id);
        await t.Library.DeleteItemsAsync([a.Id]);
        var folder = t.CreateFile(@"Project\x.py", "x");
        await t.Library.AddPathsAsync([Path.GetDirectoryName(folder)!], new AddRequest { Mode = ItemMode.Copy });
        await t.Library.DeleteCategoryAsync(created.Id, CategoryDeletePolicy.MoveToDeleted);
        t.ClearEvents();

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Empty(t.Events);
    }

    [Fact]
    public async Task TempAndSystemFiles_AreIgnored()
    {
        await using var t = await TestLibrary.CreateAsync();
        foreach (var name in (string[])["~$report.docx", "download.tmp", "movie.part", "file.crdownload", "desktop.ini", "Thumbs.db", ".cabinette-abc.tmp"])
        {
            t.WriteVaultFile(Path.Combine("Documents", name), "x");
            t.WriteVaultFile(name, "x");
        }

        var hidden = t.WriteVaultFile(@"Documents\hidden.txt", "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Empty(await t.AllItemsAsync());
        Assert.True(File.Exists(t.VaultPath("download.tmp")));
    }

    [Fact]
    public async Task PointerFileThatLivesInTheVault_IsLeftAlone()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var loose = t.WriteVaultFile("linked.pdf", "pointer target");
        var pointer = await t.AddOneAsync(docs.Id, loose, ItemMode.Pointer);

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.True(File.Exists(loose));
        Assert.Equal(loose, (await t.ItemAsync(pointer.Id)).FullPath);
    }

    [Fact]
    public async Task FileStillBeingWritten_IsPutOff_ThenAddedOnceComplete()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var path = t.VaultPath(@"Documents\download.bin");
        var request = new ReconcileRequest { DirtyFolders = ["Documents"], LockRetryDelays = [TimeSpan.FromMilliseconds(10)] };

        await using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write(new byte[1000]);
            writer.Flush();
            var outcome = await t.Service.ReconcileAsync(request, CancellationToken.None);
            Assert.True(outcome.Summary.IsEmpty);
            Assert.Equal([path], outcome.DeferredFiles);
            Assert.Equal(["Documents"], outcome.DeferredFolders);
        }

        var done = await t.Service.ReconcileAsync(request, CancellationToken.None);
        Assert.Empty(done.DeferredFiles);
        Assert.Equal(1, done.Summary.AddedByCategory["Documents"]);
        Assert.Equal(1000, Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id)).SizeBytes);
    }

    [Fact]
    public async Task MissingOrEmptiedVault_IsReportedUnavailable_AndNothingIsRemoved()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var away = t.Vault + "-away";
        Directory.Move(t.Vault, away);

        var missing = await t.Service.ReconcileAsync(ReconcileRequest.Rescan(), CancellationToken.None);
        Assert.True(missing.VaultUnavailable);
        Assert.Contains(t.Vault, missing.Message);

        // An empty folder where the vault should be (e.g. another drive mounted there) is not taken as "everything deleted".
        Directory.CreateDirectory(t.Vault);
        var empty = await t.Service.ReconcileAsync(ReconcileRequest.Rescan(), CancellationToken.None);
        Assert.True(empty.VaultUnavailable);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
        Assert.Equal(Cabinette.Core.Categorization.ExtensionMap.BuiltInCategories.Count, (await t.Library.GetAllCategoriesAsync()).Count);

        Directory.Delete(t.Vault);
        Directory.Move(away, t.Vault);
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task CategoryFolderLetterCaseChange_IsRecorded_WithoutRenamingTheCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        Directory.Move(t.VaultPath("Documents"), t.VaultPath("documents-tmp"));
        Directory.Move(t.VaultPath("documents-tmp"), t.VaultPath("documents"));

        Assert.True((await t.ApplyBatchAsync([""])).IsEmpty);
        var after = await t.CategoryAsync(docs.Id);
        Assert.Equal("Documents", after.Name);
        Assert.Equal("documents", after.FolderName);
        Assert.Equal(t.VaultPath(@"documents\a.txt"), (await t.ItemAsync(item.Id)).FullPath);
    }
}
