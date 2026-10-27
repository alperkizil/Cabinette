using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// Regression tests for the stage 1 review (docs/review/stage1-review.md). The numbers are the review's finding numbers.
/// The first tests of findings 1, 2, 3, 6, 7, 11, 12, 13 and 14 are the reviewer's repro tests, turned around to assert the fix.
/// </summary>
public sealed class ReviewRegressionTests
{
    // ================================================================ 1 and 2: missing items and new adds

    [Fact]
    public async Task F2_MissingPointer_ReAddedInCopyMode_IsFoundAgain()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("orig.txt", "the same content");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.Library.SetItemTagsAsync(item.Id, ["keep"]);
        var moved = Path.Combine(t.Source, "elsewhere", "orig.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(source, moved);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        t.ClearEvents();

        var result = await t.AddAsync(docs.Id, ItemMode.Copy, moved);

        Assert.Empty(result.Duplicates);
        var relinked = Assert.Single(result.Added);
        Assert.Equal(item.Id, relinked.Id);
        Assert.Equal(ItemMode.Copy, relinked.Mode);
        Assert.False(relinked.IsMissing);
        Assert.Equal(Path.Combine(t.Vault, "Documents", "orig.txt"), relinked.FullPath);
        Assert.Equal("the same content", File.ReadAllText(relinked.FullPath));
        Assert.Equal(moved, relinked.OriginalPath);
        Assert.Equal(["keep"], relinked.Tags);
        Assert.Equal([docs.Id], relinked.CategoryIds);
        Assert.True(File.Exists(moved), "The original stays where it is.");
        Assert.Single(Directory.GetFiles(Path.Combine(t.Vault, "Documents"), "*", SearchOption.AllDirectories));
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Items) && e.ItemIds.Contains(item.Id));
    }

    [Fact]
    public async Task F2_MissingPointer_ReAddedAsPointerIntoAnotherCategory_IsFoundAgain()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var source = t.CreateFile("orig.txt", "pointer content");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.Library.SetItemTagsAsync(item.Id, ["keep"]);
        var moved = Path.Combine(t.Source, "moved", "renamed.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(source, moved);

        // Not marked missing yet: nobody looked. The add checks the pointer's file itself.
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);
        var result = await t.AddAsync(text.Id, ItemMode.Pointer, moved);

        Assert.Empty(result.Duplicates);
        var relinked = Assert.Single(result.Added);
        Assert.Equal(item.Id, relinked.Id);
        Assert.Equal(ItemMode.Pointer, relinked.Mode);
        Assert.Equal(moved, relinked.FullPath);
        Assert.Equal("renamed.txt", relinked.FileName);
        Assert.False(relinked.IsMissing);
        Assert.Equal(["keep"], relinked.Tags);
        Assert.Equal(new[] { docs.Id, text.Id }.Order(), relinked.CategoryIds.Order());
        Assert.True(await t.Library.VerifyExistsAsync(item.Id));
    }

    [Fact]
    public async Task F2_MissingPointer_SamePathAddedAgain_ClearsTheMissingMark()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("back.txt", "first version");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        File.Delete(source);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        File.WriteAllText(source, "second version");

        var result = await t.AddAsync(docs.Id, ItemMode.Pointer, source);

        var relinked = Assert.Single(result.Added);
        Assert.Equal(item.Id, relinked.Id);
        Assert.False(relinked.IsMissing);
        Assert.Equal(14, relinked.SizeBytes);
        Assert.Equal(FileOps.Hash(System.Text.Encoding.UTF8.GetBytes("second version")), relinked.Hash);
    }

    [Fact]
    public async Task F2_MissingItemInDeleted_ReAddedInCopyMode_LeavesDeleted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var deleted = await t.BuiltInAsync("deleted");
        var source = t.CreateFile("lost.txt", "lost content");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.Library.RemoveFromCategoryAsync([item.Id], docs.Id);
        Assert.Equal([deleted.Id], (await t.ItemAsync(item.Id)).CategoryIds);
        var found = Path.Combine(t.Source, "found", "lost.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(found)!);
        File.Move(source, found);

        var relinked = Assert.Single((await t.AddAsync(text.Id, ItemMode.Copy, found)).Added);

        Assert.Equal(item.Id, relinked.Id);
        Assert.Equal([text.Id], relinked.CategoryIds);
        Assert.Equal(text.Id, relinked.HomeCategoryId);
        Assert.Equal(Path.Combine(t.Vault, "Text", "lost.txt"), relinked.FullPath);
        Assert.True(File.Exists(relinked.FullPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Vault, "Deleted")));
    }

    [Fact]
    public async Task F2_LiveItem_IsStillADuplicate()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "same"), ItemMode.Pointer);

        var copy = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile("b.txt", "same"));
        var pointer = await t.AddAsync(docs.Id, ItemMode.Pointer, t.CreateFile("c.txt", "same"));

        Assert.Empty(copy.Added);
        Assert.Equal(item.Id, Assert.Single(copy.Duplicates).Existing.Id);
        Assert.Empty(pointer.Added);
        Assert.Equal(item.Id, Assert.Single(pointer.Duplicates).Existing.Id);
    }

    [Fact]
    public async Task F1_BackgroundHash_MissingPointer_TakesOverTheNewFile_InsteadOfSwallowingIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var source = t.CreateFile("big.dat", "large identical content");
        var first = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        Assert.NotNull(first.Hash);
        await t.Library.SetItemTagsAsync(first.Id, ["keep"]);
        var moved = Path.Combine(t.Source, "moved", "big.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(source, moved);
        Assert.False(await t.Library.VerifyExistsAsync(first.Id));

        t.Service.BackgroundHashThresholdBytes = 10;
        var second = await t.AddOneAsync(text.Id, moved, ItemMode.Pointer, AddedBy.Explorer);
        Assert.Null(second.Hash);
        t.ClearEvents();
        await t.Service.WhenBackgroundHashingIdleAsync();

        // The new pointer folds into the old item, which now points at the live file.
        Assert.Null(await t.Library.GetItemAsync(second.Id));
        var survivor = await t.ItemAsync(first.Id);
        Assert.False(survivor.IsMissing);
        Assert.Equal(moved, survivor.FullPath);
        Assert.Equal(first.Hash, survivor.Hash);
        Assert.Equal(["keep"], survivor.Tags);
        Assert.Equal(new[] { docs.Id, text.Id }.Order(), survivor.CategoryIds.Order());
        Assert.Equal(1, (await t.SummaryAsync(text.Id)).UnreadCount);
        var evt = Assert.Single(t.Events);
        Assert.Contains(first.Id, evt.ItemIds);
        Assert.Contains(second.Id, evt.ItemIds);
        Assert.Empty(evt.MergedDuplicates);
    }

    [Fact]
    public async Task F1_BackgroundHash_LiveDuplicate_IsStillMerged()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var first = await t.AddOneAsync(docs.Id, t.CreateFile("one.dat", "large identical content"), ItemMode.Pointer);
        t.Service.BackgroundHashThresholdBytes = 10;
        var second = await t.AddOneAsync(docs.Id, t.CreateFile("two.dat", "large identical content"), ItemMode.Pointer);
        t.ClearEvents();
        await t.Service.WhenBackgroundHashingIdleAsync();

        Assert.Null(await t.Library.GetItemAsync(second.Id));
        Assert.Equal(first.FullPath, (await t.ItemAsync(first.Id)).FullPath);
        Assert.Single(Assert.Single(t.Events).MergedDuplicates);
    }

    // ================================================================ 3: vault folders that hold the user's own files

    [Fact]
    public async Task F3_VaultInit_AdoptsAnExistingFolder_AndCategoryDeleteLeavesItsForeignFiles()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var photos = Path.Combine(t.Vault, "Images");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "holiday.jpg"), "precious");
        await t.Library.InitializeVaultAsync(t.Vault);
        var images = await t.BuiltInAsync("images");
        Assert.Equal(0, (await t.SummaryAsync(images.Id)).ItemCount);

        await t.Library.DeleteCategoryAsync(images.Id, CategoryDeletePolicy.MoveToDeleted);

        Assert.Equal("precious", File.ReadAllText(Path.Combine(photos, "holiday.jpg")));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    [Fact]
    public async Task F3_CreateCategory_NumbersTheFolder_WhenAFolderWithForeignFilesExists()
    {
        await using var t = await TestLibrary.CreateAsync();
        Directory.CreateDirectory(Path.Combine(t.Vault, "Trip", "Day 1"));
        File.WriteAllText(Path.Combine(t.Vault, "Trip", "Day 1", "mine.txt"), "not Cabinette's");

        var trip = await t.Library.CreateCategoryAsync("Trip", null);

        Assert.Equal("Trip (1)", trip.FolderName);
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Trip (1)")));
        Assert.True(File.Exists(Path.Combine(t.Vault, "Trip", "Day 1", "mine.txt")));
        var item = await t.AddOneAsync(trip.Id, t.CreateFile("ticket.pdf", "ticket"));
        Assert.Equal(Path.Combine(t.Vault, "Trip (1)", "ticket.pdf"), item.FullPath);
    }

    [Fact]
    public async Task F3_CreateCategory_AdoptsAFolderWithoutForeignFiles()
    {
        await using var t = await TestLibrary.CreateAsync();
        Directory.CreateDirectory(Path.Combine(t.Vault, "Empty", "Sub"));
        Directory.CreateDirectory(Path.Combine(t.Vault, "Meta"));
        File.WriteAllText(Path.Combine(t.Vault, "Meta", "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(t.Vault, "Meta", NameRules.CabinetteTempPrefix + "old.tmp"), "leftover");
        var linked = t.WriteVaultFile(Path.Combine("Linked", "pointer.txt"), "a pointer's file");
        await t.AddOneAsync((await t.BuiltInAsync("text")).Id, linked, ItemMode.Pointer);

        Assert.Equal("Empty", (await t.Library.CreateCategoryAsync("Empty", null)).FolderName);
        Assert.Equal("Meta", (await t.Library.CreateCategoryAsync("Meta", null)).FolderName);
        Assert.Equal("Linked", (await t.Library.CreateCategoryAsync("Linked", null)).FolderName);
    }

    [Fact]
    public async Task F3_RecreatedBuiltIn_DoesNotTakeOverAFolderWithForeignFiles()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var mine = t.WriteVaultFile(Path.Combine("Images", "mine.jpg"), "mine");
        await t.Library.DeleteCategoryAsync(images.Id, CategoryDeletePolicy.MoveToDeleted);
        Assert.True(File.Exists(mine));

        var again = await t.BuiltInAsync("images");

        Assert.NotEqual(images.Id, again.Id);
        Assert.Equal("Images", again.Name);
        Assert.Equal("Images (1)", again.FolderName);
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public async Task F3_DeleteCategory_RecycleFiles_RecyclesOnlyLibraryFiles()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var day = await t.Library.CreateCategoryAsync("Day 1", trip.Id);
        var photo = await t.AddOneAsync(day.Id, t.CreateFile("photo.jpg", "photo"));
        var foreign = t.WriteVaultFile(Path.Combine("Trip", "Day 1", "mine.txt"), "mine");
        File.WriteAllText(t.VaultPath(Path.Combine("Trip", "Thumbs.db")), "cache");
        Directory.CreateDirectory(t.VaultPath(Path.Combine("Trip", "Empty")));

        await t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.RecycleFiles);

        Assert.Equal([photo.FullPath], t.RecycleBin.Recycled);
        Assert.Equal("mine", File.ReadAllText(foreign));
        Assert.False(Directory.Exists(t.VaultPath(Path.Combine("Trip", "Empty"))));
        Assert.True(File.Exists(t.VaultPath(Path.Combine("Trip", "Thumbs.db"))), "A folder that stays keeps its own files.");
    }

    [Fact]
    public async Task F3_DeleteCategory_RemovesAFolderHoldingOnlyLeftovers()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        await t.AddOneAsync(trip.Id, t.CreateFile("photo.jpg", "photo"));
        File.WriteAllText(t.VaultPath(Path.Combine("Trip", "desktop.ini")), "[.ShellClassInfo]");
        File.WriteAllText(t.VaultPath(Path.Combine("Trip", NameRules.CabinetteTempPrefix + "stale.tmp")), "stale");

        await t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.MoveToDeleted);

        Assert.False(Directory.Exists(t.VaultPath("Trip")));
    }

    // ================================================================ 5: drives without a Recycle Bin

    [Fact]
    public async Task F5_DeleteItems_WithoutARecycleBin_FailsWithNothingChanged_UnlessPermanentIsAllowed()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        t.RecycleBin.FoldersWithoutRecycleBin.Add(t.Vault);
        t.ClearEvents();

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([item.Id]));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
        Assert.True(File.Exists(item.FullPath));
        Assert.Empty(t.Events);

        await t.Library.DeleteItemsAsync([item.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.Null(await t.Library.GetItemAsync(item.Id));
        Assert.False(File.Exists(item.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    [Fact]
    public async Task F5_DeleteItems_PointerOnADriveWithoutARecycleBin_IsCheckedToo()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var vaultFile = await t.AddOneAsync(docs.Id, t.CreateFile("vault.txt", "v"));
        var pointer = await t.AddOneAsync(docs.Id, t.CreateFile("real.txt", "p"), ItemMode.Pointer);
        t.RecycleBin.FoldersWithoutRecycleBin.Add(t.Source);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([vaultFile.Id, pointer.Id]));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.True(File.Exists(vaultFile.FullPath));

        // Removing pointers from the library only doesn't need a Recycle Bin.
        await t.Library.DeleteItemsAsync([vaultFile.Id, pointer.Id], new DeleteItemsOptions { DeleteRealPointerFiles = false });
        Assert.True(t.RecycleBin.WasRecycled(vaultFile.FullPath));
        Assert.True(File.Exists(pointer.FullPath));
    }

    [Fact]
    public async Task F5_DeleteCategory_RecycleFiles_WithoutARecycleBin_FailsWithNothingChanged_UnlessPermanentIsAllowed()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var docs = await t.BuiltInAsync("documents");
        var photo = await t.AddOneAsync(trip.Id, t.CreateFile("photo.jpg", "photo"));
        var shared = await t.AddOneAsync(trip.Id, t.CreateFile("shared.txt", "shared"));
        await t.Library.AddToCategoryAsync([shared.Id], docs.Id);
        t.RecycleBin.FoldersWithoutRecycleBin.Add(t.Vault);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.RecycleFiles));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.NotNull(await t.Library.GetCategoryAsync(trip.Id));
        Assert.Equal(shared.FullPath, (await t.ItemAsync(shared.Id)).FullPath);
        Assert.True(File.Exists(photo.FullPath));

        await t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.RecycleFiles, new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.Null(await t.Library.GetCategoryAsync(trip.Id));
        Assert.Null(await t.Library.GetItemAsync(photo.Id));
        Assert.False(File.Exists(photo.FullPath));
        Assert.False(Directory.Exists(t.VaultPath("Trip")));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    [Fact]
    public async Task F5_DeleteCategory_UsesTheOptionsOverTheSettings()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var photo = await t.AddOneAsync(trip.Id, t.CreateFile("photo.jpg", "photo"));
        var pointer = await t.AddOneAsync(trip.Id, t.CreateFile("ticket.pdf", "ticket"), ItemMode.Pointer);

        await t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.RecycleFiles,
            new DeleteItemsOptions { RecycleVaultFiles = false, DeleteRealPointerFiles = false });

        Assert.False(File.Exists(photo.FullPath));
        Assert.True(File.Exists(pointer.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    [Fact]
    public async Task F5_EmptyDeleted_WithoutARecycleBin_FailsWithNothingChanged_UnlessPermanentIsAllowed()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var deleted = await t.BuiltInAsync("deleted");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("old.txt", "old"));
        await t.Library.RemoveFromCategoryAsync([item.Id], text.Id);
        var trashed = (await t.ItemAsync(item.Id)).FullPath;
        t.RecycleBin.FoldersWithoutRecycleBin.Add(t.Vault);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.EmptyDeletedAsync());
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.Equal(1, (await t.SummaryAsync(deleted.Id)).ItemCount);
        Assert.True(File.Exists(trashed));

        await t.Library.EmptyDeletedAsync(new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.Null(await t.Library.GetItemAsync(item.Id));
        Assert.False(File.Exists(trashed));
    }

    [Fact]
    public async Task F5_RecycleBinRefusingAnItemAfterTheCommit_IsReported_OrDeletedPermanentlyWhenAllowed()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        var small = await t.AddOneAsync(docs.Id, t.CreateFile("small.txt", "fits"));
        var huge = await t.AddOneAsync(docs.Id, t.CreateFile("huge.iso", "also too large"));
        t.RecycleBin.Refused.Add(big.FullPath);
        t.RecycleBin.Refused.Add(huge.FullPath);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id, small.Id]));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.True(File.Exists(big.FullPath), "Not deleted without permission.");
        Assert.True(t.RecycleBin.WasRecycled(small.FullPath));

        await t.Library.DeleteItemsAsync([huge.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.Null(await t.Library.GetItemAsync(huge.Id));
        Assert.False(File.Exists(huge.FullPath));
    }

    // ================================================================ 6: viewing a thread

    [Fact]
    public async Task F6_MarkViewed_RaisesOnlyAViewedChange()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        t.ClearEvents();

        await t.Library.MarkCategoryViewedAsync(python.Id);

        var evt = Assert.Single(t.Events);
        Assert.Equal(LibraryChangeKind.Viewed, evt.Kind);
        Assert.Equal(new[] { python.Id, code.Id }.Order(), evt.CategoryIds.Order());
        Assert.Empty(evt.ItemIds);
    }

    // ================================================================ 7: type filter for notes, links and pasted images

    [Fact]
    public async Task F7_NotesTypeFilter_MatchesNotesCreatedInCabinette()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notes = await t.BuiltInAsync("notes");
        var note = await t.Library.CreateNoteAsync(notes.Id, "hello");
        await t.Library.AddPastedTextAsync(notes.Id, "pasted text");
        var markdown = await t.AddOneAsync(notes.Id, t.CreateFile("readme.md", "# a markdown file"));

        var filtered = await t.Library.GetThreadItemsAsync(notes.Id, null, new ItemFilter { TypeKeys = ["notes"] });
        var asText = await t.Library.GetThreadItemsAsync(notes.Id, null, new ItemFilter { TypeKeys = ["text"] });
        var global = await t.Library.SearchAsync("", new ItemFilter { TypeKeys = ["notes"] });

        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, i => i.Id == markdown.Id);
        Assert.Equal([markdown.Id], asText.Select(i => i.Id));
        Assert.Equal(2, global.Items.Count);
        Assert.Equal(ExtensionMap.NotesKey, note.TypeKey);
        Assert.Equal("text", markdown.TypeKey);
    }

    [Fact]
    public async Task F7_LinksAndImagesTypeFilters_MatchPastedLinksAndImages()
    {
        await using var t = await TestLibrary.CreateAsync();
        var links = await t.BuiltInAsync("links");
        var link = Assert.Single((await t.Library.AddPastedTextAsync(links.Id, "https://example.com/a")).Added);
        var image = Assert.Single((await t.Library.AddPastedImageAsync(links.Id, [137, 80, 78, 71, 1, 2, 3])).Added);

        Assert.Equal([link.Id], (await t.Library.SearchAsync("", new ItemFilter { TypeKeys = ["links"] })).Items.Select(i => i.Id));
        Assert.Equal([image.Id], (await t.Library.SearchAsync("", new ItemFilter { TypeKeys = ["images"] })).Items.Select(i => i.Id));
        Assert.Empty((await t.Library.SearchAsync("", new ItemFilter { TypeKeys = ["other"] })).Items);
    }

    // ================================================================ 10: deleting items commits first

    [Fact]
    public async Task F10_DeleteItems_DeletesTheFilesOnlyAfterTheRowsAreCommitted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        bool? committedBeforeRecycling = null;
        t.RecycleBin.BeforeRecycle = _ => committedBeforeRecycling = t.Library.GetItemAsync(item.Id).GetAwaiter().GetResult() is null;

        await t.Library.DeleteItemsAsync([item.Id]);

        Assert.True(committedBeforeRecycling);
        Assert.True(t.RecycleBin.WasRecycled(item.FullPath));
    }

    // ================================================================ 11: renaming a pointer while it is hashed

    [Fact]
    public async Task F11_PointerRenamedBetweenHashingAndStoringTheHash_IsHashedAgain()
    {
        await using var t = await TestLibrary.CreateAsync();
        t.Service.BackgroundHashThresholdBytes = 10;
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("big.dat", "a large file that is hashed later");
        t.Service.AfterBackgroundHash = async id =>
        {
            t.Service.AfterBackgroundHash = null;
            await t.Library.RenameItemAsync(id, "renamed.dat");
        };

        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.Service.WhenBackgroundHashingIdleAsync();

        var after = await t.ItemAsync(item.Id);
        Assert.Equal(Path.Combine(t.Source, "renamed.dat"), after.FullPath);
        Assert.Equal(FileOps.Hash(System.Text.Encoding.UTF8.GetBytes("a large file that is hashed later")), after.Hash);
    }

    [Fact]
    public async Task F11_RenamingAPointerWhileItIsBeingHashed_StillEndsUpHashed()
    {
        await using var t = await TestLibrary.CreateAsync();
        t.Service.BackgroundHashThresholdBytes = 10;
        var docs = await t.BuiltInAsync("documents");
        var content = new byte[64 * 1024 * 1024];
        var source = t.CreateFile("big.dat", content);
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);

        await t.Library.RenameItemAsync(item.Id, "renamed.dat");
        await t.Service.WhenBackgroundHashingIdleAsync();

        var after = await t.ItemAsync(item.Id);
        Assert.Equal("renamed.dat", after.FileName);
        Assert.Equal(FileOps.Hash(content), after.Hash);
    }

    // ================================================================ 12: deleting a category while copying into it

    [Fact]
    public async Task F12_DeletingACategoryWhileACopyIntoItIsInFlight_LeavesNoStrayFolder()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var big = t.CreateFile("big.bin", new byte[4 * 1024 * 1024]);
        t.Service.DuringAddCopy = _ => t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.MoveToDeleted).GetAwaiter().GetResult();

        var result = await t.Library.AddPathsAsync([big], new AddRequest { TargetCategoryId = trip.Id, Mode = ItemMode.Copy });

        Assert.Empty(result.Added);
        Assert.Single(result.Failures);
        Assert.Null(await t.Library.GetCategoryAsync(trip.Id));
        Assert.False(Directory.Exists(t.VaultPath("Trip")));
        Assert.Empty(Directory.GetFiles(t.Vault, NameRules.CabinetteTempPrefix + "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task F12_DeletingACategoryDuringACopy_LeavesNoStrayFolder_WhateverTheTiming()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var big = t.CreateFile("big.bin", new byte[48 * 1024 * 1024]);
        var addTask = t.Library.AddPathsAsync([big], new AddRequest { TargetCategoryId = trip.Id, Mode = ItemMode.Copy });
        await Task.Delay(30);
        await t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.MoveToDeleted);

        var result = await addTask;

        Assert.Equal(1, result.Added.Count + result.Failures.Count);
        Assert.False(Directory.Exists(t.VaultPath("Trip")));
        Assert.Empty(Directory.GetFiles(t.Vault, NameRules.CabinetteTempPrefix + "*", SearchOption.AllDirectories));
    }

    // ================================================================ 13: nothing is added straight into "Deleted"

    [Fact]
    public async Task F13_FilesCannotBeAddedStraightIntoDeleted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var deleted = await t.BuiltInAsync("deleted");
        var text = await t.BuiltInAsync("text");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("x.txt", "x"));

        async Task Refused(Func<Task> add) => Assert.Equal(LibraryErrorCode.NotAllowed, (await Assert.ThrowsAsync<LibraryException>(add)).Code);
        await Refused(() => t.AddAsync(deleted.Id, ItemMode.Copy, t.CreateFile("y.txt", "y")));
        await Refused(() => t.AddAsync(deleted.Id, ItemMode.Pointer, t.CreateFile("z.txt", "z")));
        await Refused(() => t.Library.CreateNoteAsync(deleted.Id, "note"));
        await Refused(() => t.Library.AddPastedTextAsync(deleted.Id, "pasted"));
        await Refused(() => t.Library.AddPastedTextAsync(deleted.Id, "https://example.com"));
        await Refused(() => t.Library.AddPastedImageAsync(deleted.Id, [1, 2, 3]));
        await Refused(() => t.Library.AddToCategoryAsync([item.Id], deleted.Id));

        Assert.Empty(Directory.GetFiles(t.VaultPath("Deleted")));
        Assert.Equal([text.Id], (await t.ItemAsync(item.Id)).CategoryIds);

        // Files still reach "Deleted" when their last category goes.
        await t.Library.RemoveFromCategoryAsync([item.Id], text.Id);
        Assert.Equal([deleted.Id], (await t.ItemAsync(item.Id)).CategoryIds);
    }

    // ================================================================ 14: extension filter

    [Fact]
    public async Task F14_ExtensionFilter_StripsTheWildcard()
    {
        Assert.Equal("pdf", ExtensionMap.Normalize("*.pdf"));
        Assert.Equal("pdf", ExtensionMap.Normalize(" *.PDF "));
        Assert.Equal("pdf", ExtensionMap.Normalize(".pdf"));
        Assert.Equal("tar.gz", ExtensionMap.Normalize("*.tar.gz"));
        Assert.Equal("", ExtensionMap.Normalize("*"));

        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var pdf = await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "pdf"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.docx", "docx"));
        var found = await t.Library.GetThreadItemsAsync(docs.Id, null, new ItemFilter { Extensions = ["*.pdf"] });
        Assert.Equal([pdf.Id], found.Select(i => i.Id));
    }

    // ================================================================ 17: "more results" in the main-window search

    [Fact]
    public async Task F17_GlobalSearch_SaysWhenMoreItemsMatchedThanItReturns()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        SeedItems(t, docs.Id, "file_", LibraryService.GlobalSearchItemLimit);

        var exactly = await t.Library.SearchAsync("file_");
        Assert.Equal(LibraryService.GlobalSearchItemLimit, exactly.Items.Count);
        Assert.False(exactly.HasMoreItems);

        SeedItems(t, docs.Id, "more_file_", 1);
        var more = await t.Library.SearchAsync("file_");
        Assert.Equal(LibraryService.GlobalSearchItemLimit, more.Items.Count);
        Assert.True(more.HasMoreItems);
    }

    // ================================================================ 22: letter-case-only folder renames

    [Fact]
    public void F22_CaseOnlyFolderRename_FailingHalfway_IsUndone()
    {
        var root = Path.Combine(Path.GetTempPath(), "cabinette-tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Photos");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.jpg"), "a");
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var tx = connection.BeginTransaction();
            var ctx = new WriteContext(connection, tx, root, DateTimeOffset.UtcNow)
            {
                AfterCaseRenameFirstStep = _ => throw new IOException("Simulated failure of the second step."),
            };

            Assert.Throws<IOException>(() => ctx.MoveDirectory(folder, Path.Combine(root, "PHOTOS")));
            ctx.Undo();

            Assert.Equal(["Photos"], Directory.GetDirectories(root).Select(Path.GetFileName));
            Assert.True(File.Exists(Path.Combine(folder, "a.jpg")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task F22_CaseOnlyCategoryRename_StillWorks_AndIsUndoneWithTheWrite()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var item = await t.AddOneAsync(trip.Id, t.CreateFile("photo.jpg", "photo"));

        await t.Library.RenameCategoryAsync(trip.Id, "TRIP");

        Assert.Equal(["TRIP"], Directory.GetDirectories(t.Vault, "trip").Select(Path.GetFileName));
        Assert.True(File.Exists((await t.ItemAsync(item.Id)).FullPath));
        Assert.Empty(Directory.GetDirectories(t.Vault, "*.cabinette-rename-*"));
    }

    // ================================================================ checks from the review that already held

    [Fact]
    public async Task Check_UnicodeCaseVariants_DoNotOverwrite()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var a = await t.AddOneAsync(text.Id, t.CreateFile("one/şema.txt", "first"));
        var b = await t.AddOneAsync(text.Id, t.CreateFile("two/ŞEMA.txt", "second"));
        Assert.Equal("first", File.ReadAllText(a.FullPath));
        Assert.Equal("second", File.ReadAllText(b.FullPath));
        Assert.NotEqual(a.FullPath, b.FullPath, StringComparer.OrdinalIgnoreCase);

        // NTFS folds ş/Ş, so b was numbered "ŞEMA (1).txt"; renaming a to "ŞEMA.txt" is a case-only rename of its own file.
        Assert.Equal("ŞEMA (1).txt", b.FileName);
        await t.Library.RenameItemAsync(a.Id, "ŞEMA.txt");
        Assert.Equal("first", File.ReadAllText((await t.ItemAsync(a.Id)).FullPath));
        Assert.Equal("second", File.ReadAllText(b.FullPath));

        // Renaming b onto a's name (differs only by Turkish case) must be refused by the disk check.
        var clash = await Assert.ThrowsAsync<LibraryException>(() => t.Library.RenameItemAsync(b.Id, "şema.txt"));
        Assert.Equal(LibraryErrorCode.NameAlreadyExists, clash.Code);
    }

    [Fact]
    public async Task Check_SearchShortTerm_AndTagCombination()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("ab invoice.pdf", "a"));
        var b = await t.AddOneAsync(docs.Id, t.CreateFile("cd invoice.pdf", "b"));
        await t.Library.SetItemTagsAsync(a.Id, ["Invoice", "2026"]);
        await t.Library.SetItemTagsAsync(b.Id, ["invoice"]);

        Assert.Equal([a.Id], (await t.Library.SearchAsync("#INVOICE #2026")).Items.Select(i => i.Id));
        Assert.Equal([a.Id], (await t.Library.SearchAsync("ab #invoice")).Items.Select(i => i.Id));
        Assert.Equal(2, (await t.Library.GetThreadItemsAsync(docs.Id, "#invoice")).Count);
        Assert.Equal([a.Id], (await t.Library.GetThreadItemsAsync(docs.Id, "ab")).Select(i => i.Id));
    }

    [Fact]
    public async Task Check_RemoveFromDeletedThread_IsANoOp()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var deleted = await t.BuiltInAsync("deleted");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("a.txt", "a"));
        await t.Library.RemoveFromCategoryAsync([item.Id], text.Id);
        t.ClearEvents();
        await t.Library.RemoveFromCategoryAsync([item.Id], deleted.Id);
        Assert.Equal([deleted.Id], (await t.ItemAsync(item.Id)).CategoryIds);
    }

    /// <summary>Inserts pointer items straight into the database (fast; their files don't need to exist).</summary>
    private static void SeedItems(TestLibrary t, long categoryId, string prefix, int count)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(t.DataDirectory, Database.FileName)};Pooling=False");
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO items(mode, kind, file_name, extension, pointer_path, home_category_id, size_bytes, added_at, added_by, is_missing)
            VALUES (1, 0, $name, 'txt', $path, $cat, 1, $at, 0, 0);
            INSERT INTO item_categories(item_id, category_id, added_at, added_by) VALUES (last_insert_rowid(), $cat, $at, 0);
            """;
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var path = insert.Parameters.Add("$path", SqliteType.Text);
        insert.Parameters.AddWithValue("$cat", categoryId);
        insert.Parameters.AddWithValue("$at", DbValues.ToText(t.Time.GetUtcNow()));
        for (var i = 0; i < count; i++)
        {
            name.Value = $"{prefix}{i:D4}.txt";
            path.Value = Path.Combine(t.Source, "seeded", (string)name.Value);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
