using System.Collections.Concurrent;
using System.Text;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Tests;

/// <summary>
/// Regression tests for the stage 1 + 2 bug hunt (docs/review/bughunt-2026-10-15), group B: adding and the vault monitor.
/// The numbers are the README's finding numbers (#5, #6, #7, #9, #10, #12). The reviewers' and verifiers' repro tests are
/// kept as scenarios, with their assertions turned around to assert the fix.
/// </summary>
public sealed class BugHuntAddMonitorTests
{
    private static byte[] HashOf(string text) => FileOps.Hash(Encoding.UTF8.GetBytes(text));

    private static string StagingFolder(TestLibrary t) => NameRules.StagingFolder(t.Vault);

    private static string[] StagedFiles(TestLibrary t)
        => Directory.Exists(StagingFolder(t)) ? Directory.GetFiles(StagingFolder(t), "*", SearchOption.AllDirectories) : [];

    // ================================================================ #5: the "missing" mark on vault files

    /// <summary>Opus F4: a vault file marked missing while the drive was unplugged is cleared by the rescan; re-adding it is a duplicate.</summary>
    [Fact]
    public async Task F5_VaultFileMarkedMissing_IsClearedByTheRescan_AndReAddingItIsADuplicate()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report content"));

        // The drive with the vault is disconnected; the user double-clicks the bubble (OpenItemAsync → VerifyExistsAsync).
        var offline = t.Vault + ".offline";
        Directory.Move(t.Vault, offline);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        Directory.Move(offline, t.Vault);
        Assert.True((await t.ItemAsync(item.Id)).IsMissing);
        t.ClearEvents();

        // Drive back: the monitor's rescan finds the file in place and clears the mark.
        var summary = await t.RescanAsync();
        Assert.True(summary.IsEmpty);
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);
        Assert.Contains(t.Events, e => e.ItemIds.Contains(item.Id));

        // Adding the same content again is "Already in Cabinette": no second copy, nothing orphaned.
        var again = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"other\report.pdf", "report content"));
        Assert.Empty(again.Added);
        Assert.Equal(item.Id, Assert.Single(again.Duplicates).Existing.Id);
        Assert.Equal([item.FullPath], Directory.GetFiles(t.VaultPath("Documents")));

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));
    }

    /// <summary>Fable 2a: same, with tags, checked through the add result.</summary>
    [Fact]
    public async Task F5_VaultFileMarkedMissing_RescanClearsIt_AndTheNextAddOfItsContentIsADuplicate_NotARelink()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "the report"));
        await t.Library.SetItemTagsAsync(item.Id, ["keep"]);

        var away = t.Vault + "-away";
        Directory.Move(t.Vault, away);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        Directory.Move(away, t.Vault);

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);

        var again = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"copy\report.pdf", "the report"));
        Assert.Empty(again.Added);
        var duplicate = Assert.Single(again.Duplicates);
        Assert.Equal(item.Id, duplicate.Existing.Id);
        Assert.Equal("report.pdf", duplicate.Existing.FileName);

        Assert.True((await t.RescanAsync()).IsEmpty);
        var only = Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Equal(item.Id, only.Id);
        Assert.Equal(["keep"], only.Tags);
    }

    /// <summary>Without a rescan in between, the duplicate check itself finds the file and clears the mark.</summary>
    [Fact]
    public async Task F5_VaultFileMarkedMissing_ReAddedBeforeAnyRescan_IsADuplicate_AndTheMarkIsCleared()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report content"));
        var away = t.Vault + "-away";
        Directory.Move(t.Vault, away);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        Directory.Move(away, t.Vault);
        t.ClearEvents();

        var again = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"other\report.pdf", "report content"));

        Assert.Empty(again.Added);
        Assert.Equal(item.Id, Assert.Single(again.Duplicates).Existing.Id);
        var after = await t.ItemAsync(item.Id);
        Assert.False(after.IsMissing);
        Assert.Equal(item.FullPath, after.FullPath);
        Assert.Contains(t.Events, e => e.ItemIds.Contains(item.Id));
        Assert.Single(Directory.GetFiles(t.VaultPath("Documents")));
    }

    /// <summary>Fable 2b: a vault file deleted while the monitor wasn't looking is "found again" by an add of its content.</summary>
    [Fact]
    public async Task F5_VaultFileGoneWhileTheMonitorIsNotLooking_ReAddingItsContentFindsItAgain_AndTheRescanKeepsIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("notes.txt", "important notes"));
        await t.Library.SetItemTagsAsync(item.Id, ["keep"]);

        // Deleted in Explorer while the monitor is paused (or the app was closed): is_missing stays 0.
        File.Delete(item.FullPath);
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);

        var again = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"again\notes.txt", "important notes"));

        Assert.Empty(again.Duplicates);
        var relinked = Assert.Single(again.Added);
        Assert.Equal(item.Id, relinked.Id);
        Assert.Equal(item.FullPath, relinked.FullPath);
        Assert.Equal(["keep"], relinked.Tags);
        Assert.False(relinked.IsMissing);
        Assert.Equal("important notes", File.ReadAllText(relinked.FullPath));

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Equal(item.Id, Assert.Single(await t.AllItemsAsync()).Id);
    }

    /// <summary>GPT G6: the same with the real monitor paused; resuming (a full rescan) keeps the item.</summary>
    [Fact]
    public async Task F5_WhileMonitoringIsPaused_ReAddingADeletedVaultFile_FindsItAgain_AndResumingKeepsIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("report.txt", "report content");
        var item = await t.AddOneAsync(docs.Id, source);
        var monitor = t.FastMonitor();
        await monitor.StartAsync();
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        await monitor.PauseAsync();

        File.Delete(item.FullPath); // deleted in Explorer while paused
        var result = await t.AddAsync(docs.Id, ItemMode.Copy, source);

        Assert.Empty(result.Duplicates);
        Assert.Equal(item.Id, Assert.Single(result.Added).Id);
        Assert.True(File.Exists(item.FullPath));

        await monitor.ResumeAsync();
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        var kept = await t.ItemAsync(item.Id);
        Assert.False(kept.IsMissing);
        Assert.True(File.Exists(kept.FullPath));
        Assert.Equal(item.Id, Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id)).Id);
    }

    /// <summary>While the vault folder can't be reached, a vault item marked missing is never re-linked to a pointer's file.</summary>
    [Fact]
    public async Task F5_VaultUnreachable_AVaultItemMarkedMissing_IsNotRelinkedByAPointerAdd()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report content"));
        var away = t.Vault + "-away";
        Directory.Move(t.Vault, away);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));

        var result = await t.AddAsync(docs.Id, ItemMode.Pointer, t.CreateFile(@"elsewhere\report.pdf", "report content"));

        Assert.Empty(result.Duplicates);
        var pointer = Assert.Single(result.Added);
        Assert.NotEqual(item.Id, pointer.Id);
        Assert.Equal(ItemMode.Pointer, pointer.Mode);
        var stillVault = await t.ItemAsync(item.Id);
        Assert.Equal(ItemMode.Copy, stillVault.Mode);

        // The drive comes back: the vault file is where it was, and the rescan clears its mark.
        Directory.Move(away, t.Vault);
        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);
        Assert.True(File.Exists(stillVault.FullPath));
    }

    /// <summary>Background hashing of a large pointer must not turn a vault item into a pointer because the vault was unreachable.</summary>
    [Fact]
    public async Task F5_VaultUnreachable_BackgroundHashing_NeverTurnsAVaultItemIntoAPointer()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("big.dat", "large identical content"));
        var away = t.Vault + "-away";
        Directory.Move(t.Vault, away);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));

        t.Service.BackgroundHashThresholdBytes = 10;
        var pointer = await t.AddOneAsync(docs.Id, t.CreateFile(@"elsewhere\big.dat", "large identical content"), ItemMode.Pointer);
        Assert.Null(pointer.Hash);
        await t.Service.WhenBackgroundHashingIdleAsync();

        var hashed = await t.ItemAsync(pointer.Id);
        Assert.Equal(HashOf("large identical content"), hashed.Hash);
        var stillVault = await t.ItemAsync(item.Id);
        Assert.Equal(ItemMode.Copy, stillVault.Mode);
        Assert.True(stillVault.IsMissing);

        Directory.Move(away, t.Vault);
        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);
    }

    // ================================================================ #6: renaming a category while a copy into it is in flight

    /// <summary>Fable 3 and the verifier's F3: the rename succeeds and the item lands in the renamed folder.</summary>
    [Fact]
    public async Task F6_RenamingACategoryWhileACopyIntoItIsInFlight_Succeeds_AndTheItemLandsInTheRenamedFolder()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var big = t.CreateFile("big.bin", new byte[4 * 1024 * 1024]);
        Exception? renameError = null;
        var renamed = false;
        t.Service.DuringAddCopy = _ =>
        {
            try
            {
                t.Library.RenameCategoryAsync(trip.Id, "Journey").GetAwaiter().GetResult();
                renamed = true;
            }
            catch (Exception ex)
            {
                renameError = ex;
            }
        };

        var result = await t.Library.AddPathsAsync([big], new AddRequest { TargetCategoryId = trip.Id, Mode = ItemMode.Copy });

        Assert.Null(renameError);
        Assert.True(renamed);
        Assert.Empty(result.Failures);
        var item = Assert.Single(result.Added);
        Assert.Equal("Journey", (await t.CategoryAsync(trip.Id)).Name);
        Assert.Equal(t.VaultPath("Journey"), Path.GetDirectoryName(item.FullPath));
        Assert.True(File.Exists(item.FullPath));
        Assert.False(Directory.Exists(t.VaultPath("Trip")));
        Assert.Empty(StagedFiles(t));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task F6_RenamingTheParentCategory_DuringACopyIntoASubcategory_Succeeds()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var day = await t.Library.CreateCategoryAsync("Day 1", trip.Id);
        var big = t.CreateFile("big.bin", new byte[4 * 1024 * 1024]);
        Exception? error = null;
        t.Service.DuringAddCopy = _ =>
        {
            try
            {
                t.Library.RenameCategoryAsync(trip.Id, "Journey").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        };

        var result = await t.Library.AddPathsAsync([big], new AddRequest { TargetCategoryId = day.Id, Mode = ItemMode.Copy });

        Assert.Null(error);
        var item = Assert.Single(result.Added);
        Assert.Equal("Journey", (await t.CategoryAsync(trip.Id)).Name);
        Assert.Equal(t.VaultPath(@"Journey\Day 1"), Path.GetDirectoryName(item.FullPath));
        Assert.True(File.Exists(item.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>
    /// Kept from the verifier's fix probe as a plain OS fact: NTFS refuses to rename a folder while a file in it is open, even when
    /// the handle shares Delete. That is why copies go to the staging folder instead of the category folder.
    /// </summary>
    [Fact]
    public void F6_OsFact_AFolderCantBeRenamedWhileAFileInItIsOpen_EvenWithShareDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "cabinette-tests", "osfact-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(TryRenameFolderWhileAFileInItIsOpen(root, "pinned", FileShare.Read));
            Assert.False(TryRenameFolderWhileAFileInItIsOpen(root, "shared", FileShare.Read | FileShare.Delete));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool TryRenameFolderWhileAFileInItIsOpen(string root, string name, FileShare share)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        using var stream = new FileStream(Path.Combine(folder, ".cabinette-probe.tmp"), FileMode.CreateNew, FileAccess.Write, share);
        stream.Write(new byte[4096]);
        try
        {
            Directory.Move(folder, folder + "-renamed");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A copy is written into the hidden staging folder at the top of the vault; the category folder never holds a temp file.</summary>
    [Fact]
    public async Task F6_CopyGoesThroughTheHiddenStagingFolder_NotTheCategoryFolder()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        string? hookFolder = null;
        string[] stagedDuringCopy = [];
        string[] inCategoryDuringCopy = [];
        var stagingHidden = false;
        var calls = 0;
        t.Service.DuringAddCopy = folder =>
        {
            calls++;
            hookFolder = folder;
            stagedDuringCopy = StagedFiles(t);
            inCategoryDuringCopy = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
            stagingHidden = File.GetAttributes(StagingFolder(t)).HasFlag(FileAttributes.Hidden);
        };

        var item = await t.AddOneAsync(docs.Id, t.CreateFile("big.bin", new byte[3 * 1024 * 1024]));

        Assert.Equal(1, calls);
        Assert.Equal(t.VaultPath("Documents"), hookFolder);
        var staged = Assert.Single(stagedDuringCopy);
        Assert.Matches(@"^\.cabinette-[0-9a-f]{32}\.tmp$", Path.GetFileName(staged));
        Assert.Empty(inCategoryDuringCopy);
        Assert.True(stagingHidden);
        Assert.Equal(Path.Combine(t.VaultPath("Documents"), "big.bin"), item.FullPath);
        Assert.False(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.Hidden));
        Assert.Empty(StagedFiles(t));
    }

    /// <summary>A category deleted while a copy into it runs: the add fails, and neither a folder nor a temp file is left behind.</summary>
    [Fact]
    public async Task F6_DeletingTheCategoryDuringACopy_FailsTheAdd_AndCreatesNoFolder()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        t.Service.DuringAddCopy = _ => t.Library.DeleteCategoryAsync(trip.Id, CategoryDeletePolicy.MoveToDeleted).GetAwaiter().GetResult();

        var result = await t.Library.AddPathsAsync([t.CreateFile("big.bin", new byte[2 * 1024 * 1024])],
            new AddRequest { TargetCategoryId = trip.Id, Mode = ItemMode.Copy });

        Assert.Empty(result.Added);
        Assert.Single(result.Failures);
        Assert.False(Directory.Exists(t.VaultPath("Trip")));
        Assert.Empty(StagedFiles(t));
        Assert.Empty(Directory.GetFiles(t.Vault, NameRules.CabinetteTempPrefix + "*", SearchOption.AllDirectories));
    }

    // ================================================================ #7: an add in flight while the vault moves

    /// <summary>Opus F1: the add is copied again into the new vault, and the next rescan keeps it.</summary>
    [Fact]
    public async Task F7_MoveVaultDuringACopyAdd_TheFileIsCopiedAgainIntoTheNewVault_AndTheRescanKeepsTheItem()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("big.pdf", "the content of a big file that is still being copied");
        var oldVault = t.Vault;
        var newVault = Path.Combine(t.Root, "new vault");
        var moves = 0;

        // Runs while the add is copying (outside the write lock), like a user moving the vault from Settings while a large drop
        // is still in progress. If it ran again for the second copy, the vault would move again and the add would fail.
        t.Service.DuringAddCopy = _ =>
        {
            t.Library.MoveVaultAsync(newVault).GetAwaiter().GetResult();
            moves++;
        };

        var added = await t.AddOneAsync(docs.Id, source);

        Assert.Equal(1, moves);
        Assert.Equal(newVault, t.Library.VaultPath);
        Assert.Equal(Path.Combine(newVault, "Documents", "big.pdf"), added.FullPath);
        Assert.True(File.Exists(added.FullPath));
        Assert.Equal("the content of a big file that is still being copied", File.ReadAllText(added.FullPath));
        Assert.False(File.Exists(Path.Combine(oldVault, "Documents", "big.pdf")));
        Assert.False(Directory.Exists(NameRules.StagingFolder(oldVault)));
        Assert.True(File.Exists(source));

        var summary = await t.RescanAsync();
        Assert.True(summary.IsEmpty);
        Assert.NotNull(await t.Library.GetItemAsync(added.Id));
    }

    /// <summary>Fable 1: a 4 MB copy in flight while the vault moves ends up in the new vault, with no failure reported.</summary>
    [Fact]
    public async Task F7_CopyInFlightWhileTheVaultMoves_StoresTheFileInTheNewVault()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var oldVault = t.Vault;
        var newVault = Path.Combine(t.Root, "new vault");
        var big = t.CreateFile("big.bin", new byte[4 * 1024 * 1024]);
        t.Service.DuringAddCopy = _ => t.Library.MoveVaultAsync(newVault).GetAwaiter().GetResult();

        var result = await t.Library.AddPathsAsync([big], new AddRequest { TargetCategoryId = docs.Id, Mode = ItemMode.Copy });

        Assert.Equal(newVault, t.Library.VaultPath);
        Assert.Empty(result.Failures);
        var item = Assert.Single(result.Added);
        Assert.Equal(Path.Combine(newVault, "Documents", "big.bin"), item.FullPath);
        Assert.True(File.Exists(item.FullPath));
        Assert.Equal(4 * 1024 * 1024, new FileInfo(item.FullPath).Length);
        Assert.False(File.Exists(Path.Combine(oldVault, "Documents", "big.bin")));

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
    }

    /// <summary>GPT G3: with other items in the vault; the added item's file is where the library says.</summary>
    [Fact]
    public async Task F7_CopyAddInFlightDuringMoveVault_CommitsItsFileIntoTheNewVault()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var existing = await t.AddOneAsync(docs.Id, t.CreateFile("existing.txt", "existing"));
        var oldVault = t.Vault;
        var newVault = Path.Combine(t.Root, "new vault");
        MoveVaultResult? moved = null;
        t.Service.DuringAddCopy = _ => moved = t.Library.MoveVaultAsync(newVault).GetAwaiter().GetResult();

        var result = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile("late.txt", "added while the vault moved"));

        Assert.NotNull(moved);
        Assert.Equal(newVault, t.Library.VaultPath);
        var added = Assert.Single(result.Added);
        Assert.Empty(result.Failures);
        Assert.StartsWith(newVault + Path.DirectorySeparatorChar, added.FullPath);
        Assert.True(File.Exists(added.FullPath));
        Assert.False(File.Exists(Path.Combine(oldVault, "Documents", "late.txt")));
        Assert.True(await t.Library.VerifyExistsAsync(added.Id));
        Assert.True(await t.Library.VerifyExistsAsync(existing.Id));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task F7_VaultMovedDuringACopy_AndTheOriginalIsGoneByThen_FailsThatFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("gone.txt", "deleted while it was copied");
        var newVault = Path.Combine(t.Root, "new vault");
        t.Service.DuringAddCopy = _ =>
        {
            t.Library.MoveVaultAsync(newVault).GetAwaiter().GetResult();
            File.Delete(source); // the copy's handle shares Delete; the file goes once it is closed
        };

        var result = await t.AddAsync(docs.Id, ItemMode.Copy, source);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(source, failure.SourcePath);
        Assert.Equal("The vault moved while \"gone.txt\" was being copied. Add it again.", failure.Message);
        Assert.Empty(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Empty(Directory.GetFiles(newVault, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(t.Vault, "*", SearchOption.AllDirectories));
    }

    /// <summary>Migrate gets the same check: a file copied into the old vault is copied again into the new one.</summary>
    [Fact]
    public async Task F7_MigrateWhileTheVaultMoves_CopiesTheFileAgainIntoTheNewVault()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("linked.txt", "a pointer's file");
        var pointer = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        var oldVault = t.Vault;
        var newVault = Path.Combine(t.Root, "new vault");
        var moves = 0;
        t.Service.DuringMigrateCopy = _ =>
        {
            t.Library.MoveVaultAsync(newVault).GetAwaiter().GetResult();
            moves++;
        };

        var result = await t.Library.MigratePointersAsync();

        Assert.Equal(1, moves);
        Assert.Equal(1, result.Migrated);
        Assert.Empty(result.Failures);
        var migrated = await t.ItemAsync(pointer.Id);
        Assert.Equal(ItemMode.Copy, migrated.Mode);
        Assert.Equal(Path.Combine(newVault, "Documents", "linked.txt"), migrated.FullPath);
        Assert.Equal("a pointer's file", File.ReadAllText(migrated.FullPath));
        Assert.False(File.Exists(Path.Combine(oldVault, "Documents", "linked.txt")));
        Assert.True(File.Exists(source));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    // ================================================================ #9: folder and file renamed while Cabinette was closed

    /// <summary>GPT G8: the full rescan no longer fails with a foreign-key error.</summary>
    [Fact]
    public async Task F9_FolderAndFileRenamedWhileClosed_TheRescanKeepsTheItem_AndTheFolderBecomesItsCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var trip = await t.Library.CreateCategoryAsync("Trip", null);
        var item = await t.AddOneAsync(trip.Id, t.CreateFile("a.jpg", "jpeg bytes"));
        await t.Library.AddTagsAsync([item.Id], ["holiday"]);
        await t.Library.DisposeAsync();

        Directory.Move(t.VaultPath("Trip"), t.VaultPath("Holiday"));
        File.Move(t.VaultPath(@"Holiday\a.jpg"), t.VaultPath(@"Holiday\b.jpg"));
        await t.OpenAsync();

        var summary = await t.RescanAsync();

        Assert.Equal(0, summary.Removed);
        var kept = await t.ItemAsync(item.Id);
        Assert.Equal(["holiday"], kept.Tags);
        Assert.Equal("b.jpg", kept.FileName);
        var home = await t.CategoryAsync(kept.HomeCategoryId);
        Assert.Equal("Holiday", home.Name);
        Assert.Equal("Holiday", home.FolderName);
        Assert.Equal(t.VaultPath(@"Holiday\b.jpg"), kept.FullPath);
        Assert.True(File.Exists(kept.FullPath));
        Assert.Null(await t.Library.GetCategoryAsync(trip.Id));
        Assert.DoesNotContain(await t.Library.GetAllCategoriesAsync(), c => c.Name == "Trip");
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    // ================================================================ #10: stale pointer hash

    /// <summary>GPT G9: a pointer edited outside Cabinette is hashed again, so its old content is added as a new file.</summary>
    [Fact]
    public async Task F10_PointerEditedOutsideCabinette_AddingItsOldContentAddsANewCopy()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var pointerPath = t.CreateFile("template.txt", "version A");
        var pointer = await t.AddOneAsync(docs.Id, pointerPath, ItemMode.Pointer);
        File.WriteAllText(pointerPath, "version B, edited later"); // pointers are not watched
        t.ClearEvents();

        var oldCopy = t.CreateFile(@"mail\template-original.txt", "version A");
        var result = await t.AddAsync(docs.Id, ItemMode.Copy, oldCopy);

        Assert.Empty(result.Duplicates);
        var added = Assert.Single(result.Added);
        Assert.NotEqual(pointer.Id, added.Id);
        Assert.Equal(ItemMode.Copy, added.Mode);
        Assert.Equal("version A", File.ReadAllText(added.FullPath));
        Assert.Equal("version B, edited later", File.ReadAllText(pointerPath));

        var updated = await t.ItemAsync(pointer.Id);
        Assert.Equal(HashOf("version B, edited later"), updated.Hash);
        Assert.Equal(Encoding.UTF8.GetByteCount("version B, edited later"), updated.SizeBytes);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(pointerPath)), updated.FileModifiedAt);
        Assert.Contains(t.Events, e => e.ItemIds.Contains(pointer.Id));
    }

    /// <summary>A larger edited pointer is no duplicate either; it is hashed again in the background.</summary>
    [Fact]
    public async Task F10_LargePointerEdited_IsNoDuplicate_AndIsHashedAgainInTheBackground()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var pointerPath = t.CreateFile("template.txt", "version A");
        var pointer = await t.AddOneAsync(docs.Id, pointerPath, ItemMode.Pointer);
        Assert.Equal(HashOf("version A"), pointer.Hash);
        File.WriteAllText(pointerPath, "version B, edited later");
        t.Service.InlineRehashLimitBytes = 4;
        LibraryItem? beforeStoring = null;
        t.Service.AfterBackgroundHash = async id => beforeStoring = await t.Library.GetItemAsync(id);

        var result = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"mail\template-original.txt", "version A"));
        await t.Service.WhenBackgroundHashingIdleAsync();

        Assert.Empty(result.Duplicates);
        Assert.NotEqual(pointer.Id, Assert.Single(result.Added).Id);

        // The duplicate check stored the new size and time and dropped the stale hash; background hashing then stored the new one.
        Assert.NotNull(beforeStoring);
        Assert.Equal(pointer.Id, beforeStoring!.Id);
        Assert.Null(beforeStoring.Hash);
        Assert.Equal(Encoding.UTF8.GetByteCount("version B, edited later"), beforeStoring.SizeBytes);
        Assert.Equal(HashOf("version B, edited later"), (await t.ItemAsync(pointer.Id)).Hash);
    }

    /// <summary>A pointer whose file was only touched (same content, new time) is still a duplicate; its new time is stored.</summary>
    [Fact]
    public async Task F10_PointerTouchedButUnchanged_IsStillADuplicate_AndItsNewTimeIsStored()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var pointerPath = t.CreateFile("template.txt", "version A", new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var pointer = await t.AddOneAsync(docs.Id, pointerPath, ItemMode.Pointer);
        var touched = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(pointerPath, touched);

        var result = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"mail\copy.txt", "version A"));

        Assert.Empty(result.Added);
        Assert.Equal(pointer.Id, Assert.Single(result.Duplicates).Existing.Id);
        var after = await t.ItemAsync(pointer.Id);
        Assert.Equal(new DateTimeOffset(touched), after.FileModifiedAt);
        Assert.Equal(HashOf("version A"), after.Hash);
        Assert.Empty(Directory.GetFiles(t.VaultPath("Documents")));
    }

    /// <summary>An edited pointer whose file can't be read right now is no duplicate, and keeps what was stored.</summary>
    [Fact]
    public async Task F10_EditedPointerThatCantBeRead_IsNoDuplicate()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var pointerPath = t.CreateFile("template.txt", "version A");
        var pointer = await t.AddOneAsync(docs.Id, pointerPath, ItemMode.Pointer);
        File.WriteAllText(pointerPath, "version B, edited later");

        AddResult result;
        using (new FileStream(pointerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile(@"mail\template-original.txt", "version A"));
        }

        Assert.Empty(result.Duplicates);
        Assert.Single(result.Added);
        var after = await t.ItemAsync(pointer.Id);
        Assert.Equal(HashOf("version A"), after.Hash);
        Assert.Equal(pointer.SizeBytes, after.SizeBytes);
    }

    // ================================================================ #12: temp files left by an interrupted copy

    [Fact]
    public async Task F12_TempFilesLeftInTheStagingFolder_AreDeletedWhenTheLibraryOpens()
    {
        await using var t = await TestLibrary.CreateAsync();
        var staging = StagingFolder(t);
        Directory.CreateDirectory(staging);
        File.SetAttributes(staging, FileAttributes.Directory | FileAttributes.Hidden);
        var leftover = Path.Combine(staging, ".cabinette-0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllBytes(leftover, new byte[4096]);
        File.SetAttributes(leftover, FileAttributes.Hidden);
        var readOnly = Path.Combine(staging, ".cabinette-fedcba9876543210fedcba9876543210.tmp");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.Hidden | FileAttributes.ReadOnly);

        // Only Cabinette's own temp files go; anything else someone put there stays.
        var foreign = Path.Combine(staging, "mine.txt");
        File.WriteAllText(foreign, "not Cabinette's");

        await t.ReopenAsync();

        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(readOnly));
        Assert.True(File.Exists(foreign));
    }

    /// <summary>
    /// The sweep runs in the background after the library opened (a vault on a network drive that can't be reached must not hold
    /// up the start), so the temp file of a copy that started after the open stays.
    /// </summary>
    [Fact]
    public async Task F12_StagingSweep_LeavesTempFilesCreatedAfterTheLibraryOpened()
    {
        await using var t = await TestLibrary.CreateAsync();
        var staging = StagingFolder(t);
        Directory.CreateDirectory(staging);
        var opened = DateTime.UtcNow;
        var leftover = Path.Combine(staging, ".cabinette-0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllText(leftover, "left by an interrupted copy");
        File.SetCreationTimeUtc(leftover, opened.AddMinutes(-1));
        var copying = Path.Combine(staging, ".cabinette-fedcba9876543210fedcba9876543210.tmp");
        File.WriteAllText(copying, "a copy of this run");
        File.SetCreationTimeUtc(copying, opened.AddSeconds(1));

        LibraryService.SweepStagingFolder(t.Vault, opened);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(copying));
    }

    /// <summary>
    /// Restoring opens the backup's database, which still names the vault the backup was made from (possibly the vault in use
    /// before the restore, on this PC). That vault is not swept; the restored one is new, and the restart after a restore sweeps it.
    /// </summary>
    [Fact]
    public async Task F12_Restore_DoesNotSweepTheBackupsOldVault()
    {
        await using var source = await TestLibrary.CreateAsync();
        var staging = StagingFolder(source);
        Directory.CreateDirectory(staging);
        var leftover = Path.Combine(staging, ".cabinette-0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllText(leftover, "left in the old vault");
        var zip = Path.Combine(source.Root, "backups", "cabinette.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        var restored = (LibraryService)await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored vault"), target.Options, target.RecycleBin);
        target.Use(restored);
        await restored.WhenStagingSweptAsync();

        Assert.True(File.Exists(leftover));
    }

    /// <summary>
    /// Verifier O5: quitting disposes the library while a copy is still running, and the process then exits before the add's
    /// cleanup runs. The copy was in the staging folder (not in a category folder), and the next start empties it.
    /// </summary>
    [Fact]
    public async Task F12_LibraryDisposedDuringACopy_TheTempFileIsInTheStagingFolder_AndTheNextOpenEmptiesIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("big.pdf", new string('x', 64 * 1024));
        string[] stagedWhenDisposed = [];
        string[] inCategoryWhenDisposed = [];

        t.Service.DuringAddCopy = folder =>
        {
            t.Library.DisposeAsync().AsTask().GetAwaiter().GetResult();
            stagedWhenDisposed = StagedFiles(t);
            inCategoryWhenDisposed = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

            // Stand-in for the temp file a real quit leaves behind (in this process the add's own cleanup still runs).
            File.WriteAllBytes(Path.Combine(StagingFolder(t), NameRules.CabinetteTempPrefix + Guid.NewGuid().ToString("N") + ".tmp"), new byte[4096]);
        };

        try
        {
            await t.Library.AddPathsAsync([source], new AddRequest { TargetCategoryId = docs.Id, Mode = ItemMode.Copy });
        }
        catch (ObjectDisposedException)
        {
            // The add runs into the disposed library after the copy; irrelevant here.
        }

        Assert.Single(stagedWhenDisposed);
        Assert.Empty(inCategoryWhenDisposed);
        Assert.Single(StagedFiles(t));

        await t.OpenAsync();
        Assert.Empty(StagedFiles(t));
        Assert.Empty(Directory.GetFiles(t.Vault, NameRules.CabinetteTempPrefix + "*", SearchOption.AllDirectories));
    }

    /// <summary>Opus F5: an old-style temp file in a category folder (from a version before the staging folder) goes at the next full rescan.</summary>
    [Fact]
    public async Task F12_OldStyleTempFileInACategoryFolder_IsDeletedByTheStartupRescan()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.BuiltInAsync("video");

        var orphan = t.WriteVaultFile(@"Video\.cabinette-0123456789abcdef0123456789abcdef.tmp", new string('x', 4096));
        File.SetAttributes(orphan, FileAttributes.Hidden);

        await t.ReopenAsync();
        var summary = await t.RescanAsync();

        Assert.True(summary.IsEmpty);
        Assert.False(File.Exists(orphan));
        Assert.Empty(await t.Library.GetThreadItemsAsync((await t.BuiltInAsync("video")).Id));
    }

    [Fact]
    public async Task F12_OldStyleTempFiles_OnlyExactNamesGo_OnlyInAFullRescan_AndLockedOnesStay()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.BuiltInAsync("video");
        var orphan = t.WriteVaultFile(@"Video\.cabinette-ABCDEF0123456789abcdef0123456789.tmp", "old temp");
        var locked = t.WriteVaultFile(@"Video\.cabinette-11111111111111111111111111111111.tmp", "still open");
        var probe = t.WriteVaultFile(@"Video\.cabinette-probe-0123456789abcdef0123456789abcdef.tmp", "not an add's");
        var shortName = t.WriteVaultFile(@"Video\.cabinette-abc.tmp", "not an add's");
        var atTheTop = t.WriteVaultFile(".cabinette-0123456789abcdef0123456789abcdef.tmp", "not in a category folder");

        // A batch of the watcher leaves them alone.
        Assert.True((await t.ApplyBatchAsync(["Video", ""])).IsEmpty);
        Assert.True(File.Exists(orphan));

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True((await t.RescanAsync()).IsEmpty);
        }

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(locked));
        Assert.True(File.Exists(probe));
        Assert.True(File.Exists(shortName));
        Assert.True(File.Exists(atTheTop));

        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.False(File.Exists(locked));
    }

    [Fact]
    public void F12_LegacyTempNames_AreRecognizedExactly()
    {
        Assert.True(NameRules.IsLegacyCopyTempName(".cabinette-0123456789abcdef0123456789abcdef.tmp"));
        Assert.True(NameRules.IsLegacyCopyTempName(".CABINETTE-0123456789ABCDEF0123456789ABCDEF.TMP"));
        Assert.False(NameRules.IsLegacyCopyTempName(".cabinette-0123456789abcdef0123456789abcdeg.tmp"));
        Assert.False(NameRules.IsLegacyCopyTempName(".cabinette-0123456789abcdef0123456789abcde.tmp"));
        Assert.False(NameRules.IsLegacyCopyTempName(".cabinette-0123456789abcdef0123456789abcdef0.tmp"));
        Assert.False(NameRules.IsLegacyCopyTempName(".cabinette-0123456789abcdef0123456789abcdef.zip"));
        Assert.False(NameRules.IsLegacyCopyTempName("cabinette-0123456789abcdef0123456789abcdef.tmp"));
        Assert.False(NameRules.IsLegacyCopyTempName(".cabinette-tmp"));
    }

    /// <summary>The staging folder is ignored by the rescan, by Move vault and backup (VaultFiles), and by the watcher.</summary>
    [Fact]
    public async Task F12_FilesInTheStagingFolder_AreIgnoredByTheRescanAndByWholeVaultOperations()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var staging = StagingFolder(t);
        Assert.True(Directory.Exists(staging));
        File.WriteAllText(Path.Combine(staging, ".cabinette-0123456789abcdef0123456789abcdef.tmp"), "in flight");
        File.WriteAllText(Path.Combine(staging, "photo.jpg"), "someone put it here");
        Directory.CreateDirectory(Path.Combine(staging, "sub"));
        File.WriteAllText(Path.Combine(staging, "sub", "deep.txt"), "deep");

        var summary = await t.RescanAsync();

        Assert.True(summary.IsEmpty);
        Assert.DoesNotContain(await t.Library.GetAllCategoriesAsync(), c => c.FolderName.StartsWith(".cabinette", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, Directory.GetFileSystemEntries(staging, "*", SearchOption.AllDirectories).Length);

        var (directories, files) = VaultFiles.Enumerate(t.Vault, CancellationToken.None);
        Assert.DoesNotContain(directories, d => d.RelativePath.Contains(NameRules.StagingFolderName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.RelativePath.Contains(NameRules.StagingFolderName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.RelativePath == @"Documents\a.txt");
    }

    [Fact]
    public async Task F12_TheWatcher_IgnoresTheStagingFolder_AndCopiesThroughIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var monitor = t.FastMonitor();
        var summaries = new ConcurrentQueue<VaultChangeSummary>();
        monitor.ChangesApplied += (_, s) => summaries.Enqueue(s);
        await monitor.StartAsync();
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));

        await t.AddOneAsync(docs.Id, t.CreateFile("big.bin", new byte[2 * 1024 * 1024]));
        File.WriteAllText(Path.Combine(StagingFolder(t), ".cabinette-0123456789abcdef0123456789abcdef.tmp"), "in flight");

        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
        Assert.Empty(summaries);
        Assert.DoesNotContain(t.Events, e => e.Source == AddedBy.VaultMonitor);
    }

    /// <summary>A category can't get Cabinette's reserved names as its folder (the staging folder, or names the monitor ignores).</summary>
    [Fact]
    public async Task F12_ACategoryNamedLikeTheStagingFolder_GetsAnotherFolderName()
    {
        Assert.Equal("_.cabinette-tmp", NameRules.SanitizeFolderName(".cabinette-tmp"));
        Assert.Equal("_.Cabinette-x", NameRules.SanitizeFolderName(".Cabinette-x"));
        Assert.Equal(".cabinette", NameRules.SanitizeFolderName(".cabinette"));

        await using var t = await TestLibrary.CreateAsync();
        var odd = await t.Library.CreateCategoryAsync(".cabinette-tmp", null);
        Assert.Equal(".cabinette-tmp", odd.Name);
        Assert.Equal("_.cabinette-tmp", odd.FolderName);

        var item = await t.AddOneAsync(odd.Id, t.CreateFile("x.txt", "x"));
        Assert.Equal(t.VaultPath(@"_.cabinette-tmp\x.txt"), item.FullPath);
        await t.ReopenAsync();
        Assert.True(File.Exists(item.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }
}
