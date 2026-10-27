using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class CategoryTests
{
    [Fact]
    public async Task CreateCategory_CreatesItsFolder_AndRejectsSiblingClashes()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var icon = new byte[] { 1, 2, 3 };

        var cat = await t.Library.CreateCategoryAsync("Taxes: 2026?", docs.Id, icon);

        Assert.Equal("Taxes: 2026?", cat.Name);
        Assert.Equal("Taxes- 2026-", cat.FolderName);
        Assert.Equal(CategoryKind.User, cat.Kind);
        Assert.Equal(icon, cat.IconPng);
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Documents", "Taxes- 2026-")));

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.CreateCategoryAsync("taxes: 2026?", docs.Id));
        Assert.Equal(LibraryErrorCode.NameAlreadyExists, ex.Code);

        // Same sanitized folder name, different display name → numbered folder.
        var twin = await t.Library.CreateCategoryAsync("Taxes- 2026-", docs.Id);
        Assert.Equal("Taxes- 2026- (1)", twin.FolderName);

        var reserved = await t.Library.CreateCategoryAsync("con", null);
        Assert.Equal("con_", reserved.FolderName);

        var empty = await Assert.ThrowsAsync<LibraryException>(() => t.Library.CreateCategoryAsync("  ", null));
        Assert.Equal(LibraryErrorCode.InvalidName, empty.Code);
    }

    [Fact]
    public async Task RenameCategory_RenamesItsFolder_AndItemPathsStillResolve()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var item = await t.AddOneAsync(python.Id, t.CreateFile("app.py", "print(1)"));

        await t.Library.RenameCategoryAsync(code.Id, "Source/Code");

        var renamed = await t.CategoryAsync(code.Id);
        Assert.Equal("Source/Code", renamed.Name);
        Assert.Equal("Source-Code", renamed.FolderName);
        Assert.Equal("code", renamed.BuiltInKey);
        Assert.False(Directory.Exists(Path.Combine(t.Vault, "Code")));
        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(Path.Combine(t.Vault, "Source-Code", "Python", "app.py"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));

        // Case-only rename.
        await t.Library.RenameCategoryAsync(python.Id, "PYTHON");
        Assert.Equal("PYTHON", Path.GetFileName(Directory.GetDirectories(Path.Combine(t.Vault, "Source-Code")).Single(d =>
            string.Equals(Path.GetFileName(d), "python", StringComparison.OrdinalIgnoreCase))));
        Assert.True(File.Exists((await t.ItemAsync(item.Id)).FullPath));

        var clash = await Assert.ThrowsAsync<LibraryException>(() => t.Library.RenameCategoryAsync(code.Id, "audio"));
        Assert.Equal(LibraryErrorCode.NameAlreadyExists, clash.Code);
    }

    [Fact]
    public async Task DeleteCategory_MoveToDeleted_MovesOnlyFilesThatLiveNowhereElse()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var docs = await t.BuiltInAsync("documents");
        var deleted = await t.BuiltInAsync("deleted");
        var only = await t.AddOneAsync(python.Id, t.CreateFile("only.py", "only"));
        var shared = await t.AddOneAsync(python.Id, t.CreateFile("shared.py", "shared"));
        await t.Library.AddToCategoryAsync([shared.Id], docs.Id);
        var homeElsewhere = await t.AddOneAsync(docs.Id, t.CreateFile("doc.txt", "doc"));
        await t.Library.AddToCategoryAsync([homeElsewhere.Id], code.Id);
        t.CreateFile("unknown.bin", "x");
        File.Copy(Path.Combine(t.Source, "unknown.bin"), Path.Combine(t.Vault, "Code", "unknown.bin"));
        t.ClearEvents();

        await t.Library.DeleteCategoryAsync(code.Id, CategoryDeletePolicy.MoveToDeleted);

        Assert.Null(await t.Library.GetCategoryAsync(code.Id));
        Assert.Null(await t.Library.GetCategoryAsync(python.Id));

        var onlyNow = await t.ItemAsync(only.Id);
        Assert.Equal([deleted.Id], onlyNow.CategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "Deleted", "only.py"), onlyNow.FullPath);
        Assert.True(File.Exists(onlyNow.FullPath));

        var sharedNow = await t.ItemAsync(shared.Id);
        Assert.Equal([docs.Id], sharedNow.CategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "Documents", "shared.py"), sharedNow.FullPath);
        Assert.True(File.Exists(sharedNow.FullPath));

        var docNow = await t.ItemAsync(homeElsewhere.Id);
        Assert.Equal([docs.Id], docNow.CategoryIds);
        Assert.Equal(homeElsewhere.FullPath, docNow.FullPath);

        // The unknown file isn't a library item, so it stays (with its folder); the empty subfolder is gone. Nothing was recycled.
        Assert.True(File.Exists(Path.Combine(t.Vault, "Code", "unknown.bin")));
        Assert.False(Directory.Exists(Path.Combine(t.Vault, "Code", "Python")));
        Assert.Empty(t.RecycleBin.Recycled);

        var evt = Assert.Single(t.Events);
        Assert.Contains(code.Id, evt.CategoryIds);
        Assert.Contains(python.Id, evt.CategoryIds);
        Assert.Contains(deleted.Id, evt.CategoryIds);
        Assert.Contains(docs.Id, evt.CategoryIds);
        Assert.Contains(only.Id, evt.ItemIds);
    }

    [Fact]
    public async Task DeleteCategory_RecycleFiles_DeletesFilesUsingTheSettings()
    {
        await using var t = await TestLibrary.CreateAsync();
        var parent = await t.Library.CreateCategoryAsync("Trip", null);
        var child = await t.Library.CreateCategoryAsync("Day 1", parent.Id);
        var docs = await t.BuiltInAsync("documents");
        var vaultFile = await t.AddOneAsync(child.Id, t.CreateFile("photo.jpg", "jpg"));
        var pointer = await t.AddOneAsync(parent.Id, t.CreateFile("ticket.pdf", "pdf"), ItemMode.Pointer);
        var kept = await t.AddOneAsync(child.Id, t.CreateFile("kept.txt", "kept"));
        await t.Library.AddToCategoryAsync([kept.Id], docs.Id);

        await t.Library.DeleteCategoryAsync(parent.Id, CategoryDeletePolicy.RecycleFiles);

        Assert.Null(await t.Library.GetItemAsync(vaultFile.Id));
        Assert.Null(await t.Library.GetItemAsync(pointer.Id));
        Assert.True(t.RecycleBin.WasRecycled(vaultFile.FullPath));
        Assert.True(t.RecycleBin.WasRecycled(pointer.FullPath));

        var keptNow = await t.ItemAsync(kept.Id);
        Assert.Equal(Path.Combine(t.Vault, "Documents", "kept.txt"), keptNow.FullPath);
        Assert.True(File.Exists(keptNow.FullPath));

        // Only empty folders were left, so they were removed directly.
        Assert.False(Directory.Exists(Path.Combine(t.Vault, "Trip")));
        Assert.False(t.RecycleBin.WasRecycled(Path.Combine(t.Vault, "Trip")));
        Assert.DoesNotContain(await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical), s => s.Category.Kind == CategoryKind.System);
    }

    [Fact]
    public async Task DeleteCategory_WithALockedFile_FailsAndChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var docs = await t.BuiltInAsync("documents");
        var locked = await t.AddOneAsync(trip.Id, t.CreateFile("locked.jpg", "locked"));
        var shared = await t.AddOneAsync(trip.Id, t.CreateFile("shared.txt", "shared"));
        await t.Library.AddToCategoryAsync([shared.Id], docs.Id);

        using (new FileStream(locked.FullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.RecycleFiles));
            Assert.Equal(LibraryErrorCode.FileInUse, ex.Code);
        }

        // The shared file had already been moved to Documents inside the failed operation; it must be back.
        Assert.NotNull(await t.Library.GetCategoryAsync(trip.Id));
        var sharedNow = await t.ItemAsync(shared.Id);
        Assert.Equal(shared.FullPath, sharedNow.FullPath);
        Assert.True(File.Exists(shared.FullPath));
        Assert.True(File.Exists(locked.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    [Fact]
    public async Task RenameCategory_AroundAStrayFolder_PicksAFreeFolderName()
    {
        await using var t = await TestLibrary.CreateAsync();
        var cat = await t.Library.CreateCategoryAsync("Old", null);
        Directory.CreateDirectory(Path.Combine(t.Vault, "New"));
        File.WriteAllText(Path.Combine(t.Vault, "New", "stranger.txt"), "not ours");

        await t.Library.RenameCategoryAsync(cat.Id, "New");

        var renamed = await t.CategoryAsync(cat.Id);
        Assert.Equal("New", renamed.Name);
        Assert.Equal("New (1)", renamed.FolderName);
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "New (1)")));
        Assert.True(File.Exists(Path.Combine(t.Vault, "New", "stranger.txt")));
    }

    [Fact]
    public async Task DeletedCategory_CannotBeDeleted_ButCanBeRenamedAndEmptied()
    {
        await using var t = await TestLibrary.CreateAsync();
        var deleted = await t.BuiltInAsync("deleted");
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteCategoryAsync(deleted.Id, CategoryDeletePolicy.MoveToDeleted));
        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        var sub = await Assert.ThrowsAsync<LibraryException>(() => t.Library.CreateCategoryAsync("Sub", deleted.Id));
        Assert.Equal(LibraryErrorCode.NotAllowed, sub.Code);

        await t.Library.RenameCategoryAsync(deleted.Id, "Trash");
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Trash")));

        var text = await t.BuiltInAsync("text");
        var copy = await t.AddOneAsync(text.Id, t.CreateFile("c.txt", "c"));
        var pointer = await t.AddOneAsync(text.Id, t.CreateFile("p.txt", "p"), ItemMode.Pointer);
        await t.Library.RemoveFromCategoryAsync([copy.Id, pointer.Id], text.Id);
        Assert.Equal(2, (await t.SummaryAsync(deleted.Id)).ItemCount);
        var trashedPath = (await t.ItemAsync(copy.Id)).FullPath;
        Assert.Equal(Path.Combine(t.Vault, "Trash", "c.txt"), trashedPath);

        await t.Library.EmptyDeletedAsync();

        Assert.Null(await t.Library.GetItemAsync(copy.Id));
        Assert.Null(await t.Library.GetItemAsync(pointer.Id));
        Assert.True(t.RecycleBin.WasRecycled(trashedPath));
        Assert.True(File.Exists(pointer.FullPath), "Emptying Deleted only removes pointer items from the library.");
        Assert.Equal(0, (await t.SummaryAsync(deleted.Id)).ItemCount);
        Assert.NotNull(await t.Library.GetCategoryAsync(deleted.Id));
    }

    [Fact]
    public async Task ChatList_SortsPinnedFirst_ThenByLastUpload_ThenAToZ()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var video = await t.BuiltInAsync("video");
        var zebra = await t.Library.CreateCategoryAsync("zebra", null);
        await t.AddOneAsync(video.Id, t.CreateFile("v.mp4", "v"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", "a"));
        await t.Library.SetCategoryPinnedAsync(zebra.Id, true);

        var list = await t.Library.GetCategoriesAsync(null, CategorySort.LastUpload);
        var names = list.Select(s => s.Category.Name).ToList();
        Assert.Equal(["zebra", "Audio", "Video"], names.Take(3));

        // Never-used categories follow, A–Z; Deleted is hidden while empty.
        var rest = names.Skip(3).ToList();
        Assert.Equal(rest.Order(StringComparer.CurrentCultureIgnoreCase), rest);
        Assert.DoesNotContain("Deleted", names);

        var alpha = (await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical)).Select(s => s.Category.Name).ToList();
        Assert.Equal("zebra", alpha[0]);
        Assert.Equal(alpha.Skip(1).Order(StringComparer.CurrentCultureIgnoreCase), alpha.Skip(1));
    }

    [Fact]
    public async Task Summaries_CountDistinctItemsInTheSubtree_AndShowTheLastItem()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var java = await t.BuiltInAsync("code.java");
        var a = await t.AddOneAsync(python.Id, t.CreateFile("a.py", "a"));
        await t.Library.AddToCategoryAsync([a.Id], java.Id);
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(code.Id, t.CreateFile("b.go", "b"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.Library.CreateNoteAsync(java.Id, "latest note");

        var summary = await t.SummaryAsync(code.Id);
        Assert.Equal(3, summary.ItemCount);
        Assert.Equal(7, summary.SubcategoryCount);
        Assert.Equal(ItemKind.Note, summary.LastItemKind);
        Assert.StartsWith("Note ", summary.LastItemName);

        var thread = await t.Library.GetThreadItemsAsync(code.Id);
        Assert.Equal(["a.py", "b.go"], thread.Take(2).Select(i => i.FileName));
        Assert.Equal(3, thread.Count);
    }

    [Fact]
    public async Task CategoryPath_IconAndBuiltInRecreation()
    {
        await using var t = await TestLibrary.CreateAsync();
        var python = await t.BuiltInAsync("code.python");
        var path = await t.Library.GetCategoryPathAsync(python.Id);
        Assert.Equal(["Code", "Python"], path.Select(c => c.Name));

        await t.Library.SetCategoryIconAsync(python.Id, [9, 9]);
        Assert.Equal(new byte[] { 9, 9 }, (await t.CategoryAsync(python.Id)).IconPng);
        await t.Library.SetCategoryIconAsync(python.Id, null);
        Assert.Null((await t.CategoryAsync(python.Id)).IconPng);

        // A user category with the default name takes over the built-in role after the built-in was deleted.
        var images = await t.BuiltInAsync("images");
        await t.Library.DeleteCategoryAsync(images.Id, CategoryDeletePolicy.MoveToDeleted);
        var mine = await t.Library.CreateCategoryAsync("Images", null);
        var again = await t.BuiltInAsync("images");
        Assert.Equal(mine.Id, again.Id);
        Assert.Equal(CategoryKind.BuiltIn, again.Kind);
    }
}
