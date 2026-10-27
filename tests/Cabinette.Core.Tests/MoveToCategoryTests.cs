using System.Collections.Concurrent;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>"Move to category…" (SPEC.md section 6).</summary>
public sealed class MoveToCategoryTests
{
    [Fact]
    public async Task CopyModeFile_LeavingItsHome_MovesPhysically_AndKeepsTagsAndOtherCategories()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var item = await t.AddOneAsync(images.Id, t.CreateFile("beach.png", "beach"));
        await t.Library.AddToCategoryAsync([item.Id], work.Id);
        await t.Library.AddTagsAsync([item.Id], ["summer"]);
        t.ClearEvents();

        await t.Library.MoveToCategoryAsync([item.Id], images.Id, trips.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(trips.Id, moved.HomeCategoryId);
        Assert.Equal(t.VaultPath(@"Trips\beach.png"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.False(File.Exists(item.FullPath));
        Assert.Equal("beach", await File.ReadAllTextAsync(moved.FullPath));
        Assert.Equal(new[] { work.Id, trips.Id }.Order(), moved.CategoryIds.Order());
        Assert.Equal(["summer"], moved.Tags);
        Assert.Empty(await t.Library.GetThreadItemsAsync(images.Id));
        Assert.Equal(item.Id, Assert.Single(await t.Library.GetThreadItemsAsync(trips.Id)).Id);
        Assert.Equal(item.Id, Assert.Single(await t.Library.GetThreadItemsAsync(work.Id)).Id);

        // The user's own move: nothing turns unread, and open threads and the chat list hear about it.
        Assert.Equal(0, (await t.SummaryAsync(trips.Id)).UnreadCount);
        var change = Assert.Single(t.Events);
        Assert.Equal(AddedBy.AppUi, change.Source);
        Assert.True(change.Kind.HasFlag(LibraryChangeKind.Items));
        Assert.True(change.Kind.HasFlag(LibraryChangeKind.Categories));
        Assert.Contains(item.Id, change.ItemIds);
        Assert.Contains(images.Id, change.CategoryIds);
        Assert.Contains(trips.Id, change.CategoryIds);
        Assert.Contains(work.Id, change.CategoryIds);
    }

    [Fact]
    public async Task CopyModeFile_LinkedHere_OnlyChangesCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var item = await t.AddOneAsync(work.Id, t.CreateFile("plan.png", "plan"));
        await t.Library.AddToCategoryAsync([item.Id], images.Id);

        await t.Library.MoveToCategoryAsync([item.Id], images.Id, trips.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(work.Id, moved.HomeCategoryId);
        Assert.Equal(item.FullPath, moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.Equal(new[] { work.Id, trips.Id }.Order(), moved.CategoryIds.Order());
    }

    [Fact]
    public async Task PointerFile_OnlyChangesCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var invoices = await t.Library.CreateCategoryAsync("Invoices", null);
        var source = t.CreateFile("invoice.pdf", "invoice");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.Library.AddTagsAsync([item.Id], ["2026"]);

        await t.Library.MoveToCategoryAsync([item.Id], docs.Id, invoices.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(ItemMode.Pointer, moved.Mode);
        Assert.Equal(source, moved.FullPath);
        Assert.True(File.Exists(source));
        Assert.Equal([invoices.Id], moved.CategoryIds);
        Assert.Equal(invoices.Id, moved.HomeCategoryId);
        Assert.Equal(["2026"], moved.Tags);
        Assert.False(Directory.EnumerateFiles(t.VaultPath("Invoices")).Any());
    }

    [Fact]
    public async Task MissingPointerFile_StillMoves()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var invoices = await t.Library.CreateCategoryAsync("Invoices", null);
        var source = t.CreateFile("gone.pdf", "gone");
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        File.Delete(source);

        await t.Library.MoveToCategoryAsync([item.Id], docs.Id, invoices.Id);

        Assert.Equal([invoices.Id], (await t.ItemAsync(item.Id)).CategoryIds);
    }

    [Fact]
    public async Task FromAParentThread_LeavesTheParentAndEverySubcategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var java = await t.BuiltInAsync("code.java");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var archive = await t.Library.CreateCategoryAsync("Archive", null);
        var script = await t.AddOneAsync(python.Id, t.CreateFile("tool.py", "print(1)"));
        await t.Library.AddToCategoryAsync([script.Id], java.Id);
        await t.Library.AddToCategoryAsync([script.Id], work.Id);
        var config = await t.AddOneAsync(code.Id, t.CreateFile("config.json", "{}"));

        await t.Library.MoveToCategoryAsync([script.Id, config.Id], code.Id, archive.Id);

        var movedScript = await t.ItemAsync(script.Id);
        Assert.Equal(new[] { work.Id, archive.Id }.Order(), movedScript.CategoryIds.Order());
        Assert.Equal(archive.Id, movedScript.HomeCategoryId);
        Assert.Equal(t.VaultPath(@"Archive\tool.py"), movedScript.FullPath);
        Assert.True(File.Exists(movedScript.FullPath));
        Assert.False(File.Exists(script.FullPath));
        var movedConfig = await t.ItemAsync(config.Id);
        Assert.Equal([archive.Id], movedConfig.CategoryIds);
        Assert.Equal(t.VaultPath(@"Archive\config.json"), movedConfig.FullPath);
        Assert.Empty(await t.Library.GetThreadItemsAsync(code.Id));
        Assert.Equal(2, (await t.Library.GetThreadItemsAsync(archive.Id)).Count);
    }

    [Fact]
    public async Task WithinAParentThread_ToOneOfItsSubcategories_RefilesThere()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var item = await t.AddOneAsync(code.Id, t.CreateFile("notebook.json", "{\"cells\":[]}"));

        await t.Library.MoveToCategoryAsync([item.Id], code.Id, python.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal([python.Id], moved.CategoryIds);
        Assert.Equal(t.VaultPath(@"Code\Python\notebook.json"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.Single(await t.Library.GetThreadItemsAsync(code.Id));
    }

    [Fact]
    public async Task NameClash_GetsANumberedName()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        await t.AddOneAsync(trips.Id, t.CreateFile(@"a\notes.txt", "already in Trips"));
        File.WriteAllText(t.VaultPath(@"Trips\list.txt"), "a file that isn't in the library");
        var first = await t.AddOneAsync(docs.Id, t.CreateFile(@"b\notes.txt", "from Documents"));
        var second = await t.AddOneAsync(text.Id, t.CreateFile(@"b\list.txt", "from Text"));
        var third = await t.AddOneAsync(text.Id, t.CreateFile(@"c\notes.txt", "also from Text"));

        await t.Library.MoveToCategoryAsync([first.Id], docs.Id, trips.Id);
        await t.Library.MoveToCategoryAsync([second.Id, third.Id], text.Id, trips.Id);

        Assert.Equal(t.VaultPath(@"Trips\notes (1).txt"), (await t.ItemAsync(first.Id)).FullPath);
        Assert.Equal(t.VaultPath(@"Trips\list (1).txt"), (await t.ItemAsync(second.Id)).FullPath);
        Assert.Equal(t.VaultPath(@"Trips\notes (2).txt"), (await t.ItemAsync(third.Id)).FullPath);
        Assert.Equal("from Documents", await File.ReadAllTextAsync(t.VaultPath(@"Trips\notes (1).txt")));
        Assert.Equal("a file that isn't in the library", await File.ReadAllTextAsync(t.VaultPath(@"Trips\list.txt")));
        Assert.Equal("already in Trips", await File.ReadAllTextAsync(t.VaultPath(@"Trips\notes.txt")));
    }

    [Fact]
    public async Task MissingVaultFile_FailsTheWholeMove_AndChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var present = await t.AddOneAsync(images.Id, t.CreateFile("present.png", "present"));
        var gone = await t.AddOneAsync(images.Id, t.CreateFile("gone.png", "gone"));
        File.Delete(gone.FullPath);
        t.ClearEvents();

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveToCategoryAsync([present.Id, gone.Id], images.Id, trips.Id));

        Assert.Equal(LibraryErrorCode.NotFound, ex.Code);
        Assert.Contains("gone.png", ex.Message, StringComparison.Ordinal);
        Assert.Equal([images.Id], (await t.ItemAsync(present.Id)).CategoryIds);
        Assert.Equal([images.Id], (await t.ItemAsync(gone.Id)).CategoryIds);
        Assert.True(File.Exists(present.FullPath));
        Assert.Empty(Directory.EnumerateFiles(t.VaultPath("Trips")));
        Assert.Empty(t.Events);
    }

    [Fact]
    public async Task FileInUse_FailsTheWholeMove_AndChangesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var free = await t.AddOneAsync(images.Id, t.CreateFile("free.png", "free"));
        var locked = await t.AddOneAsync(images.Id, t.CreateFile("locked.png", "locked"));
        using (new FileStream(locked.FullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveToCategoryAsync([free.Id, locked.Id], images.Id, trips.Id));
            Assert.Equal(LibraryErrorCode.FileInUse, ex.Code);
            Assert.Contains("locked.png", ex.Message, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(free.FullPath));
        Assert.True(File.Exists(locked.FullPath));
        Assert.Equal([images.Id], (await t.ItemAsync(free.Id)).CategoryIds);
        Assert.Empty(Directory.EnumerateFiles(t.VaultPath("Trips")));
    }

    [Fact]
    public async Task OutOfDeleted_MovesTheFileOutOfTheDeletedFolder()
    {
        await using var t = await TestLibrary.CreateAsync();
        var old = await t.Library.CreateCategoryAsync("Old", null);
        var images = await t.BuiltInAsync("images");
        var item = await t.AddOneAsync(old.Id, t.CreateFile("kept.png", "kept"));
        await t.Library.AddTagsAsync([item.Id], ["keep"]);
        await t.Library.DeleteCategoryAsync(old.Id, CategoryDeletePolicy.MoveToDeleted);
        var deleted = await t.BuiltInAsync("deleted");
        var inDeleted = await t.ItemAsync(item.Id);
        Assert.Equal(deleted.Id, inDeleted.HomeCategoryId);
        Assert.True(File.Exists(inDeleted.FullPath));

        await t.Library.MoveToCategoryAsync([item.Id], deleted.Id, images.Id);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal([images.Id], moved.CategoryIds);
        Assert.Equal(images.Id, moved.HomeCategoryId);
        Assert.Equal(t.VaultPath(@"Images\kept.png"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.False(File.Exists(inDeleted.FullPath));
        Assert.Equal(["keep"], moved.Tags);
        Assert.Empty(await t.Library.GetThreadItemsAsync(deleted.Id));
    }

    [Fact]
    public async Task ToTheSameCategory_DoesNothing_AndToACategoryItIsAlreadyIn_JustLeavesThisOne()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var item = await t.AddOneAsync(images.Id, t.CreateFile("view.png", "view"));
        t.ClearEvents();

        await t.Library.MoveToCategoryAsync([item.Id], images.Id, images.Id);
        Assert.Equal([images.Id], (await t.ItemAsync(item.Id)).CategoryIds);
        Assert.True(File.Exists(item.FullPath));
        Assert.Empty(t.Events);

        // Already in Trips too: it only leaves Images, and its file follows its home.
        await t.Library.AddToCategoryAsync([item.Id], trips.Id);
        await t.Library.MoveToCategoryAsync([item.Id], images.Id, trips.Id);
        var moved = await t.ItemAsync(item.Id);
        Assert.Equal([trips.Id], moved.CategoryIds);
        Assert.Equal(t.VaultPath(@"Trips\view.png"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));

        // Once more from a thread it is no longer in: nothing changes.
        await t.Library.MoveToCategoryAsync([item.Id], images.Id, trips.Id);
        var again = await t.ItemAsync(item.Id);
        Assert.Equal([trips.Id], again.CategoryIds);
        Assert.Equal(moved.FullPath, again.FullPath);
    }

    [Fact]
    public async Task IntoDeleted_IsRefused()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var deleted = await t.BuiltInAsync("deleted");
        var item = await t.AddOneAsync(images.Id, t.CreateFile("x.png", "x"));

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveToCategoryAsync([item.Id], images.Id, deleted.Id));

        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Equal([images.Id], (await t.ItemAsync(item.Id)).CategoryIds);
    }

    [Fact]
    public async Task MovesByOthers_CountAsUnread()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var item = await t.AddOneAsync(images.Id, t.CreateFile("ai.png", "ai"));
        await t.Library.MarkCategoryViewedAsync(trips.Id);
        t.Time.Advance(TimeSpan.FromMinutes(1));
        t.ClearEvents();

        await t.Library.MoveToCategoryAsync([item.Id], images.Id, trips.Id, AddedBy.Ai);

        Assert.Equal(1, (await t.SummaryAsync(trips.Id)).UnreadCount);
        Assert.Equal(AddedBy.Ai, Assert.Single(t.Events).Source);
    }

    [Fact]
    public async Task WhileTheVaultMonitorRuns_NoItemIsLostOrDuplicated()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var a = await t.AddOneAsync(images.Id, t.CreateFile("a.png", "a"));
        var b = await t.AddOneAsync(images.Id, t.CreateFile("b.png", "b"));
        await t.AddOneAsync(trips.Id, t.CreateFile(@"x\b.png", "another b"));
        var c = await t.AddOneAsync(python.Id, t.CreateFile("c.py", "c"));
        var pointer = await t.AddOneAsync(images.Id, t.CreateFile("p.png", "p"), ItemMode.Pointer);
        await t.Library.AddTagsAsync([a.Id, c.Id], ["tagged"]);
        var monitor = t.FastMonitor();
        var summaries = new ConcurrentQueue<VaultChangeSummary>();
        monitor.ChangesApplied += (_, s) => summaries.Enqueue(s);
        await monitor.StartAsync();
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(15));
        var before = (await t.AllItemsAsync()).Select(i => i.Id).Order().ToList();

        await t.Library.MoveToCategoryAsync([a.Id, b.Id, pointer.Id], images.Id, trips.Id);
        await t.Library.MoveToCategoryAsync([c.Id], code.Id, images.Id);
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15));

        Assert.Empty(summaries);
        Assert.DoesNotContain(t.Events, e => e.Source == AddedBy.VaultMonitor);
        var after = await t.AllItemsAsync();
        Assert.Equal(before, after.Select(i => i.Id).Order().ToList());
        Assert.All(after, i => Assert.True(File.Exists(i.FullPath), i.FullPath));
        Assert.Equal(after.Count, after.Select(i => i.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(t.VaultPath(@"Trips\a.png"), (await t.ItemAsync(a.Id)).FullPath);
        Assert.Equal(t.VaultPath(@"Trips\b (1).png"), (await t.ItemAsync(b.Id)).FullPath);
        Assert.Equal(t.VaultPath(@"Images\c.py"), (await t.ItemAsync(c.Id)).FullPath);
        Assert.Equal(["tagged"], (await t.ItemAsync(a.Id)).Tags);
        Assert.Equal(["tagged"], (await t.ItemAsync(c.Id)).Tags);
        Assert.Equal(4, (await t.Library.GetThreadItemsAsync(trips.Id)).Count);
        Assert.Equal(c.Id, Assert.Single(await t.Library.GetThreadItemsAsync(images.Id)).Id);

        // A full rescan agrees with the library afterwards.
        Assert.True((await t.RescanAsync()).IsEmpty);
    }
}
