using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>
/// Regression tests for bug hunt 2026-10-15, group C (docs/review/bughunt-2026-10-15): #8 "Delete permanently" after a late
/// Recycle Bin refusal, and #13 a first-run vault that contains Cabinette's data folder. They started as the repro tests
/// OpusReviewer.Core F2/F2b, VerifyGpt.Core G5, FableReviewer.Core Finding4 and VerifyFableFindings.Core F4, with the
/// assertions turned around.
/// </summary>
public sealed class BugHuntDeleteFirstRunTests
{
    // ================================================================ #8 part 2: the Recycle Bin refuses after the commit

    /// <summary>OpusReviewer F2 / VerifyGpt G5: the confirmed retry deletes the refused vault file, and nothing comes back.</summary>
    [Fact]
    public async Task F8_LateRefusal_DeletePermanentlyDeletesExactlyTheRefusedFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        var other = await t.AddOneAsync(docs.Id, t.CreateFile("other.txt", "stays"));
        await t.Library.SetItemTagsAsync(big.Id, ["keep"]);
        t.RecycleBin.Refused.Add(big.FullPath); // CanRecycle says yes, the shell refuses (item too large)
        var ids = new[] { big.Id };

        // First attempt (ThreadViewModel.DeleteEverywhereAsync through RecycleBinFallback.RunAsync): refused after the commit.
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync(ids));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.Null(await t.Library.GetItemAsync(big.Id)); // the row is already deleted
        Assert.True(File.Exists(big.FullPath));

        // "Delete permanently": the same call again.
        await t.Library.DeleteItemsAsync(ids, new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(big.FullPath));
        Assert.False(t.RecycleBin.WasRecycled(big.FullPath)); // deleted permanently, not recycled
        Assert.True(File.Exists(other.FullPath));
        Assert.NotNull(await t.Library.GetItemAsync(other.Id));
        Assert.Empty(await t.Library.GetTagsAsync());

        // The next rescan has nothing to bring back.
        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Equal(other.Id, Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id)).Id);
    }

    /// <summary>Of a delete of several items, only the refused file is deleted by the retry; the recycled one stays recycled.</summary>
    [Fact]
    public async Task F8_LateRefusal_MixedDelete_RetryDeletesOnlyTheRefusedFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        var small = await t.AddOneAsync(docs.Id, t.CreateFile("small.txt", "fits"));
        t.RecycleBin.Refused.Add(big.FullPath);
        var ids = new[] { big.Id, small.Id };

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync(ids));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.Equal("\"big.iso\" can't be moved to the Recycle Bin: its drive has none, or it's too large for it.", ex.Message);
        Assert.True(t.RecycleBin.WasRecycled(small.FullPath));

        await t.Library.DeleteItemsAsync(ids, new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(big.FullPath));
        Assert.Equal([small.FullPath], t.RecycleBin.Recycled);
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>VerifyGpt G5, pointer variant: the retry deletes the pointer's real file (the user confirmed a permanent delete).</summary>
    [Fact]
    public async Task F8_LateRefusal_PointerItem_DeletePermanentlyDeletesTheRealFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var path = t.CreateFile("big-video.mp4", "pretend this is 40 GB");
        var item = await t.AddOneAsync(docs.Id, path, ItemMode.Pointer);
        t.RecycleBin.Refused.Add(path);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([item.Id]));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.True(File.Exists(path));

        await t.Library.DeleteItemsAsync([item.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.Null(await t.Library.GetItemAsync(item.Id));
        Assert.False(File.Exists(path));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    /// <summary>OpusReviewer F2b: the confirmed retry of a category delete succeeds, deletes the file and removes the folder.</summary>
    [Fact]
    public async Task F8b_LateRefusal_CategoryDelete_RetrySucceeds_AndTheFolderGoes()
    {
        await using var t = await TestLibrary.CreateAsync();
        var project = await t.Library.CreateCategoryAsync("Project", null);
        var big = await t.AddOneAsync(project.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        var photo = await t.AddOneAsync(project.Id, t.CreateFile("photo.jpg", "fits"));
        t.RecycleBin.Refused.Add(big.FullPath);

        var ex = await Assert.ThrowsAsync<LibraryException>(
            () => t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles));
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.Null(await t.Library.GetCategoryAsync(project.Id)); // already committed
        Assert.True(Directory.Exists(t.VaultPath("Project")));     // kept: big.iso is still in it

        await t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles,
            new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(big.FullPath));
        Assert.Equal([photo.FullPath], t.RecycleBin.Recycled);
        Assert.False(Directory.Exists(t.VaultPath("Project")));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>Empty Deleted: the confirmed retry deletes the refused file, plus whatever Deleted holds by then.</summary>
    [Fact]
    public async Task F8_LateRefusal_EmptyDeleted_DeletePermanentlyDeletesTheRefusedFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var deleted = await t.BuiltInAsync("deleted");
        var item = await t.AddOneAsync(text.Id, t.CreateFile("old.txt", "too large for the Recycle Bin"));
        await t.Library.RemoveFromCategoryAsync([item.Id], text.Id);
        var trashed = (await t.ItemAsync(item.Id)).FullPath;
        t.RecycleBin.Refused.Add(trashed);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.EmptyDeletedAsync());
        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.Null(await t.Library.GetItemAsync(item.Id));
        Assert.True(File.Exists(trashed));

        await t.Library.EmptyDeletedAsync(new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(trashed));
        Assert.Empty(t.RecycleBin.Recycled);
        Assert.Equal(0, (await t.SummaryAsync(deleted.Id)).ItemCount);
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>
    /// SQLite gives a new row the highest id + 1, so the id of the deleted item is handed out again right away. The retry must not
    /// look the id up again: the new item and its file stay.
    /// </summary>
    [Fact]
    public async Task F8_LateRefusal_IdReusedByANewItem_IsNotDeleted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id]));

        // While the "Delete permanently?" question is open, another file is added and gets the same id.
        var fresh = await t.AddOneAsync(docs.Id, t.CreateFile("fresh.txt", "a new file"));
        Assert.Equal(big.Id, fresh.Id);

        await t.Library.DeleteItemsAsync([big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(big.FullPath));
        Assert.Equal("fresh.txt", (await t.ItemAsync(fresh.Id)).FileName);
        Assert.True(File.Exists(fresh.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    /// <summary>A refused file that is a library item again by the time of the retry (here: taken back by a rescan) is left alone.</summary>
    [Fact]
    public async Task F8_LateRefusal_FileTakenBackAsANewItem_IsNotDeleted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id]));

        Assert.Equal(1, (await t.RescanAsync()).AddedByCategory["Documents"]);
        var back = Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));

        await t.Library.DeleteItemsAsync([big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.True(File.Exists(big.FullPath));
        Assert.NotNull(await t.Library.GetItemAsync(back.Id));
    }

    /// <summary>
    /// Changed by the AI command review (2026-10-24, F4): another delete in between (an AI's, say, while the user's "Delete
    /// permanently?" question is open) no longer ends the remembered refusal. Only a delete of the same ids takes it, so the
    /// user's confirmed retry still deletes exactly the refused file. (Until then, any delete in between ended it.)
    /// </summary>
    [Fact]
    public async Task F8_AnotherDeleteInBetween_KeepsTheRememberedRefusal_SoTheRetryStillDeletesIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        var other = await t.AddOneAsync(docs.Id, t.CreateFile("other.txt", "deleted in between"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id]));

        await t.Library.DeleteItemsAsync([other.Id]);
        Assert.True(t.RecycleBin.WasRecycled(other.FullPath));

        await t.Library.DeleteItemsAsync([big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(big.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>
    /// Changed by the AI command review (2026-10-24, F4): a different kind of delete in between (here Empty Deleted) leaves the
    /// remembered category delete alone too, so its confirmed retry deletes the refused file and removes the folder.
    /// </summary>
    [Fact]
    public async Task F8b_AnotherDeleteInBetween_TheCategoryRetryStillSucceeds()
    {
        await using var t = await TestLibrary.CreateAsync();
        var project = await t.Library.CreateCategoryAsync("Project", null);
        var big = await t.AddOneAsync(project.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles));

        await t.Library.EmptyDeletedAsync();

        await t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles, new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.False(File.Exists(big.FullPath));
        Assert.False(Directory.Exists(t.VaultPath("Project")));
    }

    /// <summary>
    /// AI command review F4: two late refusals in a row (the user's, then another delete's while the question is open) are both
    /// remembered; each confirmed retry deletes exactly its own refused file, in any order.
    /// </summary>
    [Fact]
    public async Task F4_TwoLateRefusals_EachRetryDeletesItsOwnFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var mine = await t.AddOneAsync(docs.Id, t.CreateFile("mine.iso", "the user's, too large"));
        var theirs = await t.AddOneAsync(docs.Id, t.CreateFile("theirs.iso", "the other delete's, too large"));
        t.RecycleBin.Refused.Add(mine.FullPath);
        t.RecycleBin.Refused.Add(theirs.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([mine.Id]));
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([theirs.Id]));

        await t.Library.DeleteItemsAsync([mine.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.False(File.Exists(mine.FullPath));
        Assert.True(File.Exists(theirs.FullPath));

        await t.Library.DeleteItemsAsync([theirs.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.False(File.Exists(theirs.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>
    /// AI command review F4: a delete of other ids, a category delete and Empty Deleted in between all leave an item delete's
    /// remembered refusal alone; a retry naming other ids (here: some of them) is not its retry either.
    /// </summary>
    [Fact]
    public async Task F4_OnlyADeleteOfTheSameIds_TakesTheRememberedRefusal()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        var small = await t.AddOneAsync(docs.Id, t.CreateFile("small.txt", "recycled fine"));
        var other = await t.AddOneAsync(docs.Id, t.CreateFile("other.txt", "deleted in between"));
        var project = await t.Library.CreateCategoryAsync("Project", null);
        await t.AddOneAsync(project.Id, t.CreateFile("in project.txt", "deleted with its category"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id, small.Id]));
        Assert.True(t.RecycleBin.WasRecycled(small.FullPath));

        await t.Library.DeleteItemsAsync([other.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        await t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles, new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        await t.Library.EmptyDeletedAsync(new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        await t.Library.DeleteItemsAsync([big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.True(File.Exists(big.FullPath));

        await t.Library.DeleteItemsAsync([small.Id, big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.False(File.Exists(big.FullPath));
    }

    /// <summary>
    /// AI command review F4: the same delete without "Delete permanently" drops the remembered refusal and starts over. That is
    /// how a refusal the user answered "No" to ends: once its id belongs to a new item, deleting that item (whose first attempt is
    /// never permanent) deletes the new item normally and leaves the old refused file alone.
    /// </summary>
    [Fact]
    public async Task F4_TheSameDeleteWithoutPermanent_DropsTheRememberedRefusal()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id]));

        // "No": nothing more is called. Later a new file gets the same id and the user deletes it.
        var fresh = await t.AddOneAsync(docs.Id, t.CreateFile("fresh.txt", "a new file"));
        Assert.Equal(big.Id, fresh.Id);
        await t.Library.DeleteItemsAsync([fresh.Id]);

        Assert.True(t.RecycleBin.WasRecycled(fresh.FullPath));
        Assert.Null(await t.Library.GetItemAsync(fresh.Id));
        Assert.True(File.Exists(big.FullPath));

        // The refusal is gone: a later permanent delete of that id deletes nothing it remembered.
        await t.Library.DeleteItemsAsync([big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.True(File.Exists(big.FullPath));
    }

    /// <summary>The retry of a category delete recognizes the remembered id, so a new category that got that id is not deleted.</summary>
    [Fact]
    public async Task F8b_LateRefusal_CategoryIdReusedByANewCategory_IsNotDeleted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var project = await t.Library.CreateCategoryAsync("Project", null);
        var big = await t.AddOneAsync(project.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles));

        var fresh = await t.Library.CreateCategoryAsync("Fresh", null);
        Assert.Equal(project.Id, fresh.Id);
        var kept = await t.AddOneAsync(fresh.Id, t.CreateFile("kept.txt", "kept"));

        await t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles,
            new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });

        Assert.False(File.Exists(big.FullPath));
        Assert.False(Directory.Exists(t.VaultPath("Project")));
        Assert.Equal("Fresh", (await t.CategoryAsync(fresh.Id)).Name);
        Assert.True(File.Exists(kept.FullPath));
        Assert.NotNull(await t.Library.GetItemAsync(kept.Id));
    }

    /// <summary>
    /// "No" after a late refusal (the [my default] of the fix plan): nothing more happens. The file stays and, as before the fix,
    /// comes back as a new, untagged item at the next rescan.
    /// </summary>
    [Fact]
    public async Task F8_LateRefusal_WithoutARetry_TheFileComesBackAsANewItem()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        await t.Library.SetItemTagsAsync(big.Id, ["keep"]);
        t.RecycleBin.Refused.Add(big.FullPath);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([big.Id]));

        Assert.Equal(1, (await t.RescanAsync()).AddedByCategory["Documents"]);
        var back = Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Empty(back.Tags);
        Assert.Equal(AddedBy.VaultMonitor, back.AddedBy);
        Assert.True(File.Exists(big.FullPath));
    }

    // ================================================================ #8 part 1: too large for the Recycle Bin, caught up front

    /// <summary>
    /// A file the Recycle Bin reports as too large before anything changes stops the whole delete: rows, tags and files stay. The
    /// check is per file: the other file in the same folder can be recycled, and it is asked about first.
    /// </summary>
    [Fact]
    public async Task F8_TooLargeUpFront_LeavesRowsAndTagsUntouched_UntilDeletePermanentlyIsConfirmed()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var small = await t.AddOneAsync(docs.Id, t.CreateFile("small.txt", "fits"));
        var big = await t.AddOneAsync(docs.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        await t.Library.SetItemTagsAsync(small.Id, ["mine"]);
        await t.Library.SetItemTagsAsync(big.Id, ["keep"]);
        t.RecycleBin.TooLarge.Add(big.FullPath);
        t.ClearEvents();

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.DeleteItemsAsync([small.Id, big.Id]));

        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.Equal("\"big.iso\" can't be moved to the Recycle Bin: its drive has none, or it's too large for it.", ex.Message);
        Assert.Equal(["mine"], (await t.ItemAsync(small.Id)).Tags);
        Assert.Equal(["keep"], (await t.ItemAsync(big.Id)).Tags);
        Assert.True(File.Exists(small.FullPath));
        Assert.True(File.Exists(big.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
        Assert.Empty(t.Events);

        // The confirmed retry: the small file goes to the Recycle Bin, the big one is deleted permanently.
        await t.Library.DeleteItemsAsync([small.Id, big.Id], new DeleteItemsOptions { PermanentWhenNoRecycleBin = true });
        Assert.Null(await t.Library.GetItemAsync(small.Id));
        Assert.Null(await t.Library.GetItemAsync(big.Id));
        Assert.Equal([small.FullPath], t.RecycleBin.Recycled);
        Assert.False(File.Exists(big.FullPath));
    }

    /// <summary>The same up-front check for a category delete: the category, its rows and its files stay.</summary>
    [Fact]
    public async Task F8b_TooLargeUpFront_CategoryDelete_ChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var project = await t.Library.CreateCategoryAsync("Project", null);
        var big = await t.AddOneAsync(project.Id, t.CreateFile("big.iso", "too large for the Recycle Bin"));
        t.RecycleBin.TooLarge.Add(big.FullPath);

        var ex = await Assert.ThrowsAsync<LibraryException>(
            () => t.Library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles));

        Assert.Equal(LibraryErrorCode.RecycleBinUnavailable, ex.Code);
        Assert.NotNull(await t.Library.GetCategoryAsync(project.Id));
        Assert.NotNull(await t.Library.GetItemAsync(big.Id));
        Assert.True(File.Exists(big.FullPath));
    }

    // ================================================================ #13: a vault that contains the data folder

    /// <summary>FableReviewer Finding4: first run refuses a vault folder that contains the data folder, and changes nothing.</summary>
    [Fact]
    public async Task F13_FirstRun_VaultThatContainsTheDataFolder_IsRefused()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);

        // t.Root holds the data folder (cabinette.db).
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.InitializeVaultAsync(t.Root));

        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Equal("The vault can't contain Cabinette's data folder.", ex.Message);
        Assert.Null(t.Library.VaultPath);
        Assert.Null((await t.Library.GetSettingsAsync()).VaultPath);
        Assert.False(Directory.Exists(Path.Combine(t.Root, "Documents")));

        // A sibling of the data folder is fine.
        await t.Library.InitializeVaultAsync(t.Vault);
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }

    /// <summary>The settings can also set the first vault (the app doesn't use that for it); the same check applies.</summary>
    [Fact]
    public async Task F13_SaveSettingsSettingTheFirstVault_RefusesOneThatContainsTheDataFolder()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var settings = await t.Library.GetSettingsAsync();

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.SaveSettingsAsync(settings with { VaultPath = t.Root }));

        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Null(t.Library.VaultPath);
        await t.Library.SaveSettingsAsync(settings with { VaultPath = t.Vault });
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }

    /// <summary>VerifyFableFindings F4: the data folder itself, %LocalAppData% and the whole profile are all refused.</summary>
    [Fact]
    public async Task F13_FirstRun_DataFolder_LocalAppData_AndProfile_AreRefused_ASiblingIsAccepted()
    {
        await using var p = await ProfileLayout.CreateAsync();

        foreach (var vault in new[] { p.DataDirectory, p.LocalAppData, p.AppData, p.Home, p.Home + Path.DirectorySeparatorChar })
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => p.Library.InitializeVaultAsync(vault));
            Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
            Assert.Equal("The vault can't contain Cabinette's data folder.", ex.Message);
            Assert.Null(p.Library.VaultPath);
        }

        Assert.False(Directory.Exists(Path.Combine(p.LocalAppData, "Documents")));
        Assert.False(Directory.Exists(Path.Combine(p.Home, "Documents")));

        // A folder next to the profile, or one inside it that doesn't hold the data folder, is accepted.
        await p.Library.InitializeVaultAsync(p.Vault);
        Assert.Equal(p.Vault, p.Library.VaultPath);
        Assert.True(Directory.Exists(Path.Combine(p.Vault, "Documents")));
    }

    [Fact]
    public async Task F13_FirstRun_AFolderInsideTheProfileThatDoesNotHoldTheDataFolder_IsAccepted()
    {
        await using var p = await ProfileLayout.CreateAsync();
        var inProfile = Path.Combine(p.Home, "Cabinette vault");

        await p.Library.InitializeVaultAsync(inProfile);

        Assert.Equal(inProfile, p.Library.VaultPath);
    }

    /// <summary>
    /// VerifyFableFindings F4: Move vault into any folder that contains the data folder stays refused (its target must be empty,
    /// and the data folder always holds the open database).
    /// </summary>
    [Fact]
    public async Task F13_MoveVault_IntoAnyFolderThatContainsTheDataFolder_IsStillRefused()
    {
        await using var p = await ProfileLayout.CreateAsync();
        await p.Library.InitializeVaultAsync(p.Vault);

        foreach (var target in new[] { p.DataDirectory, p.LocalAppData, p.AppData, p.Home })
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => p.Library.MoveVaultAsync(target));
            Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        }

        Assert.Equal(p.Vault, p.Library.VaultPath);
        Assert.True(File.Exists(Path.Combine(p.DataDirectory, Database.FileName)));
    }

    /// <summary>A fake user profile: home\AppData (hidden)\Local\Cabinette is the data folder; root\vault is a normal vault.</summary>
    private sealed class ProfileLayout : IAsyncDisposable
    {
        private ProfileLayout(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public string Home => Path.Combine(Root, "home");

        public string AppData => Path.Combine(Home, "AppData");

        public string LocalAppData => Path.Combine(AppData, "Local");

        public string DataDirectory => Path.Combine(LocalAppData, "Cabinette");

        public string Vault => Path.Combine(Root, "vault");

        public ILibraryService Library { get; private set; } = null!;

        public static async Task<ProfileLayout> CreateAsync()
        {
            var layout = new ProfileLayout(Path.Combine(Path.GetTempPath(), "cabinette-tests", "bughunt-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(layout.LocalAppData);
            File.SetAttributes(layout.AppData, File.GetAttributes(layout.AppData) | FileAttributes.Hidden); // as on Windows
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 14, 9, 26, 53, TimeSpan.Zero));
            layout.Library = await LibraryFactory.OpenAsync(
                new LibraryOptions { DataDirectory = layout.DataDirectory, TimeProvider = time },
                new FakeRecycleBin(Path.Combine(layout.Root, "recycle")));
            return layout;
        }

        public async ValueTask DisposeAsync()
        {
            await Library.DisposeAsync();
            new Database(DataDirectory).ClearPool();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
                        {
                            File.SetAttributes(entry, File.Exists(entry) ? FileAttributes.Normal : FileAttributes.Directory);
                        }

                        Directory.Delete(Root, recursive: true);
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
