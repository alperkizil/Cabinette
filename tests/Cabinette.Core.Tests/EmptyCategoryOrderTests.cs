using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>
/// Chat list sorted by last upload: a category whose last file left joins the empty categories at the bottom (A–Z),
/// however the file left. One that still has files sorts by its newest remaining file, and a pinned one stays on top.
/// </summary>
public sealed class EmptyCategoryOrderTests
{
    public enum HowEmptied { Deleted, RemovedFromCategory, MovedAway, DeletedInExplorer }

    [Theory]
    [InlineData(HowEmptied.Deleted)]
    [InlineData(HowEmptied.RemovedFromCategory)]
    [InlineData(HowEmptied.MovedAway)]
    [InlineData(HowEmptied.DeletedInExplorer)]
    public async Task ACategoryWhoseLastFileLeft_JoinsTheEmptyCategoriesAtTheBottom(HowEmptied how)
    {
        await using var t = await TestLibrary.CreateAsync();
        var video = await t.BuiltInAsync("video");
        var audio = await t.BuiltInAsync("audio");
        var other = await t.BuiltInAsync("other");
        await t.AddOneAsync(video.Id, t.CreateFile("v.mp4", "v"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", "a"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        var item = await t.AddOneAsync(other.Id, t.CreateFile("x.bin", "x"));
        Assert.Equal(other.Id, (await TopLevelAsync(t))[0].Category.Id);
        t.Time.Advance(TimeSpan.FromMinutes(1));

        switch (how)
        {
            case HowEmptied.Deleted:
                await t.Library.DeleteItemsAsync([item.Id]);
                break;
            case HowEmptied.RemovedFromCategory:
                await t.Library.RemoveFromCategoryAsync([item.Id], other.Id);
                break;
            case HowEmptied.MovedAway:
                await t.Library.MoveToCategoryAsync([item.Id], other.Id, video.Id);
                break;
            case HowEmptied.DeletedInExplorer:
                File.Delete(item.FullPath);
                await t.RescanAsync();
                break;
        }

        var list = await TopLevelAsync(t);
        Assert.Equal(0, list.Single(s => s.Category.Id == other.Id).ItemCount);
        AssertEmptyCategoriesLast(list);
    }

    /// <summary>
    /// Reported by the user in 0.1.3: a file added to a category that already had older files and deleted right away left the
    /// category on top, among the recent ones. It now falls back to where its newest remaining file puts it, with that file's
    /// time, however the newest file left.
    /// </summary>
    [Theory]
    [InlineData(HowEmptied.Deleted)]
    [InlineData(HowEmptied.RemovedFromCategory)]
    [InlineData(HowEmptied.MovedAway)]
    [InlineData(HowEmptied.DeletedInExplorer)]
    public async Task ACategoryThatStillHasFiles_FallsBackToItsNewestRemainingFile_WhenItsNewestFileLeaves(HowEmptied how)
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var video = await t.BuiltInAsync("video");
        var other = await t.BuiltInAsync("other");
        await t.AddOneAsync(other.Id, t.CreateFile("old.bin", "old"));
        var oldAt = (await TopLevelAsync(t)).Single(s => s.Category.Id == other.Id).Category.LastUploadAt;
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", "a"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(video.Id, t.CreateFile("v.mp4", "v"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        var newest = await t.AddOneAsync(other.Id, t.CreateFile("new.bin", "new"));
        Assert.Equal(other.Id, (await TopLevelAsync(t))[0].Category.Id);
        t.Time.Advance(TimeSpan.FromMinutes(1));

        switch (how)
        {
            case HowEmptied.Deleted:
                await t.Library.DeleteItemsAsync([newest.Id]);
                break;
            case HowEmptied.RemovedFromCategory:
                // Removing needs another category to keep the file in; Audio gets it later than its own file.
                await t.Library.AddToCategoryAsync([newest.Id], audio.Id);
                await t.Library.RemoveFromCategoryAsync([newest.Id], other.Id);
                break;
            case HowEmptied.MovedAway:
                await t.Library.MoveToCategoryAsync([newest.Id], other.Id, audio.Id);
                break;
            case HowEmptied.DeletedInExplorer:
                File.Delete(newest.FullPath);
                await t.RescanAsync();
                break;
        }

        var list = await TopLevelAsync(t);
        var ids = list.Select(s => s.Category.Id).ToList();
        Assert.True(ids.IndexOf(other.Id) > ids.IndexOf(video.Id), $"{how}: Other's newest file now is older than Video's.");
        Assert.True(ids.IndexOf(other.Id) > ids.IndexOf(audio.Id), $"{how}: Other's newest file now is older than Audio's.");
        var row = list.Single(s => s.Category.Id == other.Id);
        Assert.Equal(1, row.ItemCount);
        Assert.Equal(oldAt, row.Category.LastUploadAt);
        AssertEmptyCategoriesLast(list);
    }

    [Fact]
    public async Task MovingOrAddingAnOldFileIntoACategory_StillPutsItOnTop()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var other = await t.BuiltInAsync("other");
        var old = await t.AddOneAsync(other.Id, t.CreateFile("old.bin", "old"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", "a"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        var notes = await t.BuiltInAsync("notes");
        var oldNote = await t.AddOneAsync(notes.Id, t.CreateFile("n.note", "n"));
        t.Time.Advance(TimeSpan.FromMinutes(1));

        // The file is old, but it arrives in Audio now.
        await t.Library.MoveToCategoryAsync([old.Id], other.Id, audio.Id);
        Assert.Equal(audio.Id, (await TopLevelAsync(t))[0].Category.Id);

        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.Library.AddToCategoryAsync([oldNote.Id], other.Id);
        Assert.Equal(other.Id, (await TopLevelAsync(t))[0].Category.Id);
    }

    [Fact]
    public async Task APinnedCategory_StaysOnTop_WhenEmptied()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var other = await t.BuiltInAsync("other");
        var item = await t.AddOneAsync(other.Id, t.CreateFile("x.bin", "x"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", "a"));
        await t.Library.SetCategoryPinnedAsync(other.Id, true);

        await t.Library.DeleteItemsAsync([item.Id]);

        Assert.Equal(other.Id, (await TopLevelAsync(t))[0].Category.Id);
    }

    [Fact]
    public async Task AParentWhoseSubtreeEmptied_JoinsTheEmptyCategories_AtBothLevels()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", "a"));
        await t.AddOneAsync(code.Id, t.CreateFile("b.go", "b"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        var script = await t.AddOneAsync(python.Id, t.CreateFile("s.py", "s"));
        var goFile = (await t.Library.GetThreadItemsAsync(code.Id)).Single(i => i.Id != script.Id);

        // The parent still holds a file of its own: it keeps its place, and only Python joins the empty ones.
        await t.Library.DeleteItemsAsync([script.Id]);
        var top = await TopLevelAsync(t);
        Assert.Equal(1, top.Single(s => s.Category.Id == code.Id).ItemCount);
        AssertEmptyCategoriesLast(top);
        var children = await t.Library.GetCategoriesAsync(code.Id, CategorySort.LastUpload);
        Assert.Equal(0, children.Single(s => s.Category.Id == python.Id).ItemCount);
        AssertEmptyCategoriesLast(children);

        await t.Library.DeleteItemsAsync([goFile.Id]);
        top = await TopLevelAsync(t);
        Assert.Equal(0, top.Single(s => s.Category.Id == code.Id).ItemCount);
        AssertEmptyCategoriesLast(top);
    }

    private static Task<IReadOnlyList<CategorySummary>> TopLevelAsync(TestLibrary t)
        => t.Library.GetCategoriesAsync(null, CategorySort.LastUpload);

    /// <summary>Categories with files first, newest upload first; then the empty ones, A–Z (nothing here is pinned).</summary>
    private static void AssertEmptyCategoriesLast(IReadOnlyList<CategorySummary> list)
    {
        var withFiles = list.TakeWhile(s => s.ItemCount > 0).ToList();
        var empty = list.Skip(withFiles.Count).ToList();
        Assert.NotEmpty(empty);
        Assert.All(empty, s => Assert.Equal(0, s.ItemCount));
        Assert.Equal(
            withFiles.OrderByDescending(s => s.Category.LastUploadAt).Select(s => s.Category.Id),
            withFiles.Select(s => s.Category.Id));
        var names = empty.Select(s => s.Category.Name).ToList();
        Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), names);
    }
}
