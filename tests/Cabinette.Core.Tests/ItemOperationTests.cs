using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class ItemOperationTests
{
    [Fact]
    public async Task RenameItem_RenamesTheVaultFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("old.pdf", "x"));

        await t.Library.RenameItemAsync(item.Id, "new name.docx");

        var renamed = await t.ItemAsync(item.Id);
        Assert.Equal("new name.docx", renamed.FileName);
        Assert.Equal("docx", renamed.Extension);
        Assert.Equal(Path.Combine(t.Vault, "Documents", "new name.docx"), renamed.FullPath);
        Assert.True(File.Exists(renamed.FullPath));
        Assert.False(File.Exists(item.FullPath));
        Assert.Equal(item.Id, Assert.Single((await t.Library.SearchAsync("name.doc")).Items).Id);
        Assert.Empty((await t.Library.SearchAsync("old.pdf")).Items);
    }

    [Fact]
    public async Task RenameItem_CaseOnlyChange_Works()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "x"));
        await t.Library.RenameItemAsync(item.Id, "Report.pdf");
        Assert.Equal("Report.pdf", Path.GetFileName(Directory.GetFiles(Path.Combine(t.Vault, "Documents")).Single()));
    }

    [Fact]
    public async Task RenameItem_RenamesThePointersRealFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("pointer.txt", "x");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);

        await t.Library.RenameItemAsync(item.Id, "renamed.txt");

        var renamed = await t.ItemAsync(item.Id);
        Assert.Equal(Path.Combine(t.Source, "renamed.txt"), renamed.FullPath);
        Assert.True(File.Exists(renamed.FullPath));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task RenameItem_ClashAndInvalidNames_AreRejected()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));

        var clash = await Assert.ThrowsAsync<LibraryException>(() => t.Library.RenameItemAsync(a.Id, "B.txt"));
        Assert.Equal(LibraryErrorCode.NameAlreadyExists, clash.Code);

        foreach (var bad in new[] { "", "  ", "a/b.txt", "what?.txt", "CON", "nul.txt", "dot." })
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.RenameItemAsync(a.Id, bad));
            Assert.Equal(LibraryErrorCode.InvalidName, ex.Code);
        }

        Assert.Equal("a.txt", (await t.ItemAsync(a.Id)).FileName);
        Assert.True(File.Exists(a.FullPath));

        var pointerSource = t.CreateFile("p1.txt", "p1");
        t.CreateFile("p2.txt", "p2");
        var pointer = await t.AddOneAsync(docs.Id, pointerSource, ItemMode.Pointer);
        var pointerClash = await Assert.ThrowsAsync<LibraryException>(() => t.Library.RenameItemAsync(pointer.Id, "p2.txt"));
        Assert.Equal(LibraryErrorCode.NameAlreadyExists, pointerClash.Code);
    }

    [Fact]
    public async Task RenameItem_LockedFile_ThrowsFileInUse_AndChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("locked.txt", "x"));
        using (new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.RenameItemAsync(item.Id, "free.txt"));
            Assert.Equal(LibraryErrorCode.FileInUse, ex.Code);
        }

        Assert.Equal("locked.txt", (await t.ItemAsync(item.Id)).FileName);
    }

    [Fact]
    public async Task DeleteItems_VaultFiles_GoToTheRecycleBin_OrArePermanentlyDeleted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var recycled = await t.AddOneAsync(docs.Id, t.CreateFile("r.txt", "recycle me"));
        var permanent = await t.AddOneAsync(docs.Id, t.CreateFile("p.txt", "delete me"));
        await t.Library.SetItemTagsAsync(recycled.Id, ["temp"]);

        await t.Library.DeleteItemsAsync([recycled.Id]);
        await t.Library.DeleteItemsAsync([permanent.Id], new DeleteItemsOptions { RecycleVaultFiles = false });

        Assert.True(t.RecycleBin.WasRecycled(recycled.FullPath));
        Assert.False(t.RecycleBin.WasRecycled(permanent.FullPath));
        Assert.False(File.Exists(recycled.FullPath));
        Assert.False(File.Exists(permanent.FullPath));
        Assert.Empty(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Empty(await t.Library.GetTagsAsync());
        Assert.Empty((await t.Library.SearchAsync("r.txt")).Items);
    }

    [Fact]
    public async Task DeleteItems_Pointers_RealFileToRecycleBin_OrLibraryOnly()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var withFile = await t.AddOneAsync(docs.Id, t.CreateFile("real1.txt", "1"), ItemMode.Pointer);
        var libraryOnly = await t.AddOneAsync(docs.Id, t.CreateFile("real2.txt", "2"), ItemMode.Pointer);

        await t.Library.DeleteItemsAsync([withFile.Id]);
        await t.Library.DeleteItemsAsync([libraryOnly.Id], new DeleteItemsOptions { DeleteRealPointerFiles = false });

        Assert.True(t.RecycleBin.WasRecycled(withFile.FullPath));
        Assert.True(File.Exists(libraryOnly.FullPath));
        Assert.Null(await t.Library.GetItemAsync(withFile.Id));
        Assert.Null(await t.Library.GetItemAsync(libraryOnly.Id));
    }

    [Fact]
    public async Task DeleteItems_WhenTheRecycleBinFailsAfterTheCommit_TheErrorIsReported_AndTheFileStays()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("keep.txt", "x"));
        t.RecycleBin.FailNext = true;

        // Files are deleted after the rows are committed; an unexpected shell failure leaves the file in the vault, where the
        // vault monitor picks it up again.
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([item.Id]));
        Assert.Contains("keep.txt", ex.Message);
        Assert.Null(await t.Library.GetItemAsync(item.Id));
        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task DeleteItems_WithALockedFile_FailsAndChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var free = await t.AddOneAsync(docs.Id, t.CreateFile("free.txt", "free"));
        var locked = await t.AddOneAsync(docs.Id, t.CreateFile("locked.txt", "locked"));
        t.ClearEvents();

        using (new FileStream(locked.FullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([free.Id, locked.Id]));
            Assert.Equal(LibraryErrorCode.FileInUse, ex.Code);
        }

        Assert.NotNull(await t.Library.GetItemAsync(free.Id));
        Assert.NotNull(await t.Library.GetItemAsync(locked.Id));
        Assert.True(File.Exists(free.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
        Assert.Empty(t.Events);
    }

    [Fact]
    public async Task RemoveFromCategory_HomeRemoved_MovesTheFileToTheNextCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("shared.txt", "shared"));
        await t.Library.AddToCategoryAsync([item.Id], docs.Id);

        await t.Library.RemoveFromCategoryAsync([item.Id], text.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(docs.Id, moved.HomeCategoryId);
        Assert.Equal([docs.Id], moved.CategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "Documents", "shared.txt"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.False(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task RemoveFromCategory_LastMembership_MovesToDeleted_AndAddingAgainRestores()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var deleted = await t.BuiltInAsync("deleted");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("alone.txt", "alone"));
        Assert.DoesNotContain((await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical)), s => s.Category.Id == deleted.Id);

        await t.Library.RemoveFromCategoryAsync([item.Id], text.Id);

        var inTrash = await t.ItemAsync(item.Id);
        Assert.Equal(deleted.Id, inTrash.HomeCategoryId);
        Assert.Equal([deleted.Id], inTrash.CategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "Deleted", "alone.txt"), inTrash.FullPath);
        Assert.True(File.Exists(inTrash.FullPath));
        Assert.Contains((await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical)), s => s.Category.Id == deleted.Id);

        // Removing from Deleted when there is nowhere else to go keeps it there.
        await t.Library.RemoveFromCategoryAsync([item.Id], deleted.Id);
        Assert.Equal([deleted.Id], (await t.ItemAsync(item.Id)).CategoryIds);

        // Adding it to a real category takes it out of Deleted.
        await t.Library.AddToCategoryAsync([item.Id], text.Id);
        var restored = await t.ItemAsync(item.Id);
        Assert.Equal([text.Id], restored.CategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "Text", "alone.txt"), restored.FullPath);
        Assert.True(File.Exists(restored.FullPath));
    }

    [Fact]
    public async Task RemoveFromParent_RemovesDescendantMemberships()
    {
        await using var t = await TestLibrary.CreateAsync();
        var python = await t.BuiltInAsync("code.python");
        var code = await t.BuiltInAsync("code");
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(python.Id, t.CreateFile("a.py", "a"));
        await t.Library.AddToCategoryAsync([item.Id], docs.Id);

        await t.Library.RemoveFromCategoryAsync([item.Id], code.Id);

        Assert.Equal([docs.Id], (await t.ItemAsync(item.Id)).CategoryIds);
        Assert.Empty(await t.Library.GetThreadItemsAsync(code.Id));
    }

    [Fact]
    public async Task AddToCategory_IsALink_NotACopy()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("a.txt", "a"));
        t.Time.Advance(TimeSpan.FromMinutes(3));

        await t.Library.AddToCategoryAsync([item.Id], docs.Id, AddedBy.Ai);

        var linked = await t.ItemAsync(item.Id);
        Assert.Equal(text.Id, linked.HomeCategoryId);
        Assert.Equal(2, linked.CategoryIds.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Vault, "Documents")));
        Assert.Equal(t.Time.GetUtcNow(), (await t.CategoryAsync(docs.Id)).LastUploadAt);
        Assert.Equal(1, (await t.SummaryAsync(docs.Id)).UnreadCount);
    }
}
