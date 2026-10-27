using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class UnreadMissingAndEventTests
{
    [Fact]
    public async Task Unread_CountsExternalAddsSinceLastView_InTheSubtree()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var java = await t.BuiltInAsync("code.java");

        await t.AddOneAsync(python.Id, t.CreateFile("ui.py", "ui"), addedBy: AddedBy.AppUi);
        var explorer = await t.AddOneAsync(python.Id, t.CreateFile("explorer.py", "explorer"), addedBy: AddedBy.Explorer);
        await t.AddOneAsync(java.Id, t.CreateFile("ai.java", "ai"), addedBy: AddedBy.Ai);
        await t.Library.AddToCategoryAsync([explorer.Id], java.Id, AddedBy.VaultMonitor);
        await t.Library.AddPastedTextAsync(java.Id, "pasted");

        Assert.Equal(1, (await t.SummaryAsync(python.Id)).UnreadCount);
        Assert.Equal(2, (await t.SummaryAsync(java.Id)).UnreadCount);
        Assert.Equal(2, (await t.SummaryAsync(code.Id)).UnreadCount); // distinct items: explorer.py, ai.java

        t.Time.Advance(TimeSpan.FromMinutes(1));
        t.ClearEvents();
        await t.Library.MarkCategoryViewedAsync(code.Id);

        Assert.Equal(0, (await t.SummaryAsync(code.Id)).UnreadCount);
        Assert.Equal(0, (await t.SummaryAsync(python.Id)).UnreadCount);
        Assert.Equal(t.Time.GetUtcNow(), (await t.CategoryAsync(java.Id)).LastViewedAt);
        var evt = Assert.Single(t.Events);
        Assert.Contains(python.Id, evt.CategoryIds);
        Assert.Contains(code.Id, evt.CategoryIds);

        // Something new arrives after the view.
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(python.Id, t.CreateFile("later.py", "later"), addedBy: AddedBy.Explorer);
        Assert.Equal(1, (await t.SummaryAsync(code.Id)).UnreadCount);
        Assert.Equal(0, (await t.SummaryAsync(java.Id)).UnreadCount);

        // Viewing only a child clears only that child (and therefore the parent's count from it).
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.Library.MarkCategoryViewedAsync(python.Id);
        Assert.Equal(0, (await t.SummaryAsync(code.Id)).UnreadCount);
    }

    [Fact]
    public async Task VerifyExists_FlipsMissing_AndRaisesChangedOnlyOnChange()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("p.txt", "p"), ItemMode.Pointer);
        t.ClearEvents();

        Assert.True(await t.Library.VerifyExistsAsync(item.Id));
        Assert.Empty(t.Events);

        File.Move(item.FullPath, item.FullPath + ".moved");
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        Assert.True((await t.ItemAsync(item.Id)).IsMissing);
        var evt = Assert.Single(t.Events);
        Assert.Contains(item.Id, evt.ItemIds);
        Assert.Contains(docs.Id, evt.CategoryIds);

        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        Assert.Single(t.Events);

        File.Move(item.FullPath + ".moved", item.FullPath);
        Assert.True(await t.Library.VerifyExistsAsync(item.Id));
        Assert.False((await t.ItemAsync(item.Id)).IsMissing);
    }

    [Fact]
    public async Task Relocate_SameContent_Relinks()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("orig.txt", "content"), ItemMode.Pointer);
        var moved = Path.Combine(t.Source, "sub", "moved name.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(item.FullPath, moved);
        await t.Library.VerifyExistsAsync(item.Id);

        Assert.Equal(RelocateOutcome.Relocated, await t.Library.RelocateItemAsync(item.Id, moved, acceptHashMismatch: false));

        var relinked = await t.ItemAsync(item.Id);
        Assert.Equal(moved, relinked.FullPath);
        Assert.Equal("moved name.txt", relinked.FileName);
        Assert.False(relinked.IsMissing);
        Assert.Equal(item.Hash, relinked.Hash);
    }

    [Fact]
    public async Task Relocate_DifferentContent_NeedsConfirmation()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("orig.txt", "content"), ItemMode.Pointer);
        var other = t.CreateFile("other.txt", "different content");

        Assert.Equal(RelocateOutcome.HashMismatch, await t.Library.RelocateItemAsync(item.Id, other, acceptHashMismatch: false));
        Assert.Equal(item.FullPath, (await t.ItemAsync(item.Id)).FullPath);

        Assert.Equal(RelocateOutcome.Relocated, await t.Library.RelocateItemAsync(item.Id, other, acceptHashMismatch: true));
        var relinked = await t.ItemAsync(item.Id);
        Assert.Equal(other, relinked.FullPath);
        Assert.NotEqual(item.Hash, relinked.Hash);

        Assert.Equal(RelocateOutcome.NotFound, await t.Library.RelocateItemAsync(item.Id, Path.Combine(t.Source, "nope.txt"), true));
    }

    [Fact]
    public async Task ChangedEvents_IncludeAncestors_AndSource()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var nested = await t.Library.CreateCategoryAsync("Scripts", python.Id);
        t.ClearEvents();

        var item = await t.AddOneAsync(nested.Id, t.CreateFile("s.py", "s"), addedBy: AddedBy.Explorer);

        var evt = Assert.Single(t.Events);
        Assert.True(evt.Kind.HasFlag(LibraryChangeKind.Items));
        Assert.Contains(item.Id, evt.ItemIds);
        Assert.Equal(new[] { nested.Id, python.Id, code.Id }.Order(), evt.CategoryIds.Order());
        Assert.Equal(AddedBy.Explorer, evt.Source);

        t.ClearEvents();
        await t.Library.RenameItemAsync(item.Id, "t.py");
        Assert.Equal(new[] { nested.Id, python.Id, code.Id }.Order(), Assert.Single(t.Events).CategoryIds.Order());

        t.ClearEvents();
        await t.Library.SetCategoryPinnedAsync(nested.Id, true);
        var pinEvt = Assert.Single(t.Events);
        Assert.Equal(LibraryChangeKind.Categories, pinEvt.Kind);
        Assert.Equal(new[] { nested.Id, python.Id, code.Id }.Order(), pinEvt.CategoryIds.Order());
    }

    [Fact]
    public async Task FailedWrites_RaiseNoEvent_AndHandlersThatThrowDoNotBreakTheLibrary()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        t.ClearEvents();
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.CreateCategoryAsync("", docs.Id));
        Assert.Empty(t.Events);

        t.Library.Changed += (_, _) => throw new InvalidOperationException("bad subscriber");
        var cat = await t.Library.CreateCategoryAsync("Works anyway", docs.Id);
        Assert.NotNull(await t.Library.GetCategoryAsync(cat.Id));
    }

    [Fact]
    public async Task ConcurrentWritesAndReads_AreSafe()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var files = Enumerable.Range(0, 20).Select(i => t.CreateFile($"c{i}.txt", $"content {i}")).ToList();

        var writes = files.Select(f => t.Library.AddPathsAsync([f], new AddRequest { TargetCategoryId = text.Id, Mode = ItemMode.Copy }));
        var reads = Enumerable.Range(0, 20).Select(_ => t.Library.GetCategoriesAsync(null, CategorySort.LastUpload));
        await Task.WhenAll(writes.Cast<Task>().Concat(reads));

        Assert.Equal(20, (await t.Library.GetThreadItemsAsync(text.Id)).Count);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(t.Vault, "Text")).Length);
    }
}
