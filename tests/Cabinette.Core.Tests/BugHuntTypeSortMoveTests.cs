using System.Collections.Concurrent;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>
/// Bug hunt 2026-10-22 for "Move to category…" and "Type categories sort by type" (commit 6abd784;
/// docs/review/bughunt-2026-10-22-sortmove). The findings' tests asserted the defects on the reviewed commit and now assert
/// the fixed behavior; the others pin down behavior the reviewer checked and found correct.
/// </summary>
public sealed class BugHuntTypeSortMoveTests
{
    // ------------------------------------------------------------------ rollback when a move fails part-way

    [Fact]
    public async Task Move_WhenTheThirdOfFiveFilesCannotBeMoved_PutsTheFirstTwoBackAndChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);

        // A 250-character stem keeps "name.png" (254) inside NTFS's 255-character limit, but the "name (1).png" the clash in Trips
        // forces on it (258) is over it: the third move fails on disk after two files were moved.
        var longStem = new string('n', 250);
        var items = new List<LibraryItem>
        {
            await t.AddOneAsync(images.Id, t.CreateFile("f1.png", "f1")),
            await t.AddOneAsync(images.Id, t.CreateFile("f2.png", "f2")),
            await t.AddOneAsync(images.Id, t.CreateFile(longStem + ".png", "f3")),
            await t.AddOneAsync(images.Id, t.CreateFile("f4.png", "f4")),
            await t.AddOneAsync(images.Id, t.CreateFile("f5.png", "f5")),
        };
        await t.Library.AddTagsAsync([items[0].Id], ["keep"]);
        File.WriteAllText(t.VaultPath(Path.Combine("Trips", longStem + ".png")), "not in the library");
        t.ClearEvents();

        var ex = await Assert.ThrowsAsync<LibraryException>(
            () => t.Library.MoveToCategoryAsync(items.Select(i => i.Id).ToList(), images.Id, trips.Id));

        // The 258-character name is refused by the file system (Win32 error 206, "The filename or extension is too long",
        // which FileOps maps to Unknown), after f1 and f2 were moved.
        Assert.NotEqual(LibraryErrorCode.FileInUse, ex.Code);
        Assert.NotEqual(LibraryErrorCode.NotFound, ex.Code);
        foreach (var item in items)
        {
            var current = await t.ItemAsync(item.Id);
            Assert.Equal(images.Id, current.HomeCategoryId);
            Assert.Equal([images.Id], current.CategoryIds);
            Assert.Equal(item.FullPath, current.FullPath);
            Assert.True(File.Exists(item.FullPath), item.FullPath);
        }

        Assert.Equal(["keep"], (await t.ItemAsync(items[0].Id)).Tags);
        var foreign = Assert.Single(Directory.EnumerateFiles(t.VaultPath("Trips")));
        Assert.Empty(t.Events);

        // Without the planted file (which a rescan would import as a new item) the vault and the library agree again.
        File.Delete(foreign);
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    // ------------------------------------------------------------------ names

    [Fact]
    public async Task Move_CaseOnlyNameClash_GetsANumberedName()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var existing = await t.AddOneAsync(trips.Id, t.CreateFile(@"a\PHOTO.PNG", "in Trips"));
        var moving = await t.AddOneAsync(images.Id, t.CreateFile(@"b\photo.png", "from Images"));

        await t.Library.MoveToCategoryAsync([moving.Id], images.Id, trips.Id);

        Assert.Equal(t.VaultPath(@"Trips\photo (1).png"), (await t.ItemAsync(moving.Id)).FullPath);
        Assert.Equal("from Images", await File.ReadAllTextAsync(t.VaultPath(@"Trips\photo (1).png")));
        Assert.Equal("in Trips", await File.ReadAllTextAsync(existing.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task Move_RenamedByAClash_IsFoundBySearchUnderItsNewName()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        await t.AddOneAsync(trips.Id, t.CreateFile(@"a\report.png", "a"));
        var moving = await t.AddOneAsync(images.Id, t.CreateFile(@"b\report.png", "b"));

        await t.Library.MoveToCategoryAsync([moving.Id], images.Id, trips.Id);

        var hits = (await t.Library.SearchAsync("report (1)")).Items;
        Assert.Equal(moving.Id, Assert.Single(hits).Id);
        Assert.Equal(moving.Id, Assert.Single((await t.Library.GetThreadItemsAsync(trips.Id, "(1)")).Select(i => i.Id)));
    }

    // ------------------------------------------------------------------ the target's folder

    [Fact]
    public async Task Move_IntoACategoryWhoseFolderIsGone_RecreatesTheFolder()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var item = await t.AddOneAsync(images.Id, t.CreateFile("x.png", "x"));
        Directory.Delete(t.VaultPath("Trips"));

        await t.Library.MoveToCategoryAsync([item.Id], images.Id, trips.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(t.VaultPath(@"Trips\x.png"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task Move_ToAnAncestorOfTheThread_LeavesTheSubcategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var item = await t.AddOneAsync(python.Id, t.CreateFile("tool.py", "print(1)"));

        await t.Library.MoveToCategoryAsync([item.Id], python.Id, code.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal([code.Id], moved.CategoryIds);
        Assert.Equal(t.VaultPath(@"Code\tool.py"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.Empty(await t.Library.GetThreadItemsAsync(python.Id));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    // ------------------------------------------------------------------ a file somebody is reading (the vault monitor hashing it, for example)

    /// <summary>
    /// Finding (fixed): the move's pre-check opened each vault file with no sharing at all, so a file another program only
    /// reads (the vault monitor hashing it after an external edit, a viewer, a backup tool) failed the whole move with
    /// "in use", although a rename of it works: Windows lets a file move while handles opened with delete sharing are on it.
    /// </summary>
    [Fact]
    public async Task Move_OfAFileThatIsOnlyBeingRead_Succeeds_LikeRename()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var a = await t.AddOneAsync(images.Id, t.CreateFile("a.png", "a"));
        var b = await t.AddOneAsync(images.Id, t.CreateFile("b.png", "b"));

        // Exactly how the vault monitor's hasher (FileOps.HashCompleteFile) and Cabinette's own copy hold a file.
        using (new FileStream(a.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            await t.Library.MoveToCategoryAsync([a.Id, b.Id], images.Id, trips.Id);
        }

        Assert.Equal([trips.Id], (await t.ItemAsync(a.Id)).CategoryIds);
        Assert.Equal([trips.Id], (await t.ItemAsync(b.Id)).CategoryIds);
        Assert.True(File.Exists(t.VaultPath(@"Trips\a.png")));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    /// <summary>A program holding the file without delete sharing (an editor) still fails the move with "in use", and nothing changes.</summary>
    [Fact]
    public async Task Move_OfAFileHeldWithoutDeleteSharing_FailsAsInUse_AndPutsEverythingBack()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var a = await t.AddOneAsync(images.Id, t.CreateFile("a.png", "a"));
        var b = await t.AddOneAsync(images.Id, t.CreateFile("b.png", "b"));

        using (new FileStream(b.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveToCategoryAsync([a.Id, b.Id], images.Id, trips.Id));
            Assert.Equal(LibraryErrorCode.FileInUse, ex.Code);
        }

        Assert.Equal([images.Id], (await t.ItemAsync(a.Id)).CategoryIds);
        Assert.True(File.Exists(t.VaultPath(@"Images\a.png")));
        Assert.False(File.Exists(t.VaultPath(@"Trips\a.png")));
        Assert.True((await t.RescanAsync()).IsEmpty);
    }

    // ------------------------------------------------------------------ the vault monitor busy in the same folder

    [Fact]
    public async Task Move_WhileTheMonitorWaitsForALockedFileInTheSameFolder_LosesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var a = await t.AddOneAsync(images.Id, t.CreateFile("a.png", "a"));
        var busy = await t.AddOneAsync(images.Id, t.CreateFile("busy.png", "busy"));
        var monitor = t.FastMonitor();
        var summaries = new ConcurrentQueue<VaultChangeSummary>();
        monitor.ChangesApplied += (_, s) => summaries.Enqueue(s);
        await monitor.StartAsync();
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(15));

        // Another program edits busy.png and keeps it open for writing: the monitor sees the change, can't hash it and keeps
        // coming back to the Images folder.
        using (var writer = new FileStream(busy.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            writer.Seek(0, SeekOrigin.End);
            writer.Write("more"u8);
            writer.Flush(flushToDisk: true);

            // The batch for the edit runs within the quiet period (150 ms); the file can't be hashed while it is open for
            // writing, so the Images folder is put off and looked at again every 200 ms × attempts. (The library's clock is
            // frozen in tests, so LastSyncAt can't be watched; the Modified summary at the end proves the edit was seen.)
            await Task.Delay(600);
            Assert.Empty(summaries);

            await t.Library.MoveToCategoryAsync([a.Id], images.Id, trips.Id);
            await Task.Delay(700);
        }

        await VaultTestHelpers.WaitUntilAsync(() => summaries.Any(s => s.Modified > 0), "the edited file was hashed once it was closed");
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15));

        Assert.All(summaries, s => Assert.Equal(0, s.Removed + s.Moved + s.Renamed + s.AddedByCategory.Values.Sum()));
        var moved = await t.ItemAsync(a.Id);
        Assert.Equal(t.VaultPath(@"Trips\a.png"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.Equal([trips.Id], moved.CategoryIds);
        Assert.Equal(busy.Id, Assert.Single(await t.Library.GetThreadItemsAsync(images.Id)).Id);
        Assert.True((await t.RescanAsync()).IsEmpty);
        await monitor.DisposeAsync();
    }

    // ------------------------------------------------------------------ out of Deleted

    /// <summary>
    /// Finding (fixed): "Move to category…" out of Deleted failed when the file in the Deleted folder was gone, while
    /// "Add to category…" took the same item out anyway, pointing at a missing file. Both refuse now.
    /// </summary>
    [Fact]
    public async Task OutOfDeleted_WhenTheDeletedFileIsGone_MoveAndAddToCategoryBothRefuse()
    {
        await using var t = await TestLibrary.CreateAsync();
        var old = await t.Library.CreateCategoryAsync("Old", null);
        var images = await t.BuiltInAsync("images");
        var item = await t.AddOneAsync(old.Id, t.CreateFile("kept.png", "kept"));
        await t.Library.DeleteCategoryAsync(old.Id, CategoryDeletePolicy.MoveToDeleted);
        var deleted = await t.BuiltInAsync("deleted");
        File.Delete((await t.ItemAsync(item.Id)).FullPath);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveToCategoryAsync([item.Id], deleted.Id, images.Id));
        Assert.Equal(LibraryErrorCode.NotFound, ex.Code);
        Assert.Equal([deleted.Id], (await t.ItemAsync(item.Id)).CategoryIds);

        var addEx = await Assert.ThrowsAsync<LibraryException>(() => t.Library.AddToCategoryAsync([item.Id], images.Id));
        Assert.Equal(LibraryErrorCode.NotFound, addEx.Code);
        var still = await t.ItemAsync(item.Id);
        Assert.Equal([deleted.Id], still.CategoryIds);
        Assert.Equal(deleted.Id, still.HomeCategoryId);
    }

    // ------------------------------------------------------------------ sorting: a duplicate that already is in its type category

    /// <summary>Finding (fixed): a duplicate already in its type category was reported as "sorted" there, though nothing changed.</summary>
    [Fact]
    public async Task Sort_AFileAlreadyInItsTypeCategory_IsNotReportedAsSorted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var sheets = await t.BuiltInAsync("spreadsheets");
        var images = await t.BuiltInAsync("images");
        await t.AddOneAsync(images.Id, t.CreateFile(@"a\logo.png", "logo"));

        var result = await t.Library.AddPathsAsync(
            [t.CreateFile(@"b\logo.png", "logo")],
            new AddRequest { TargetCategoryId = sheets.Id, Mode = ItemMode.Copy, SortIntoTypeCategories = true });

        Assert.Empty(result.Added);
        var duplicate = Assert.Single(result.Duplicates);
        Assert.False(duplicate.AddedToTargetCategory);
        Assert.Empty(result.SortedByType);
    }
}
