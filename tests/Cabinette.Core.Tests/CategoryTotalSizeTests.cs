using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// <see cref="CategorySummary.TotalSizeBytes"/>, shown in the thread banner (SPEC.md section 4): the sum of the known sizes of
/// the distinct files in a category and all its subcategories, the same set of files its <see cref="CategorySummary.ItemCount"/>
/// counts. Read through both the chat-list query and the single-summary query.
/// </summary>
public sealed class CategoryTotalSizeTests
{
    [Fact]
    public async Task TheTotalIsTheSumOfTheSizesOfTheCategorysFiles()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var video = await t.BuiltInAsync("video");
        await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", new string('a', 100)));
        await t.AddOneAsync(audio.Id, t.CreateFile("b.wav", new string('b', 250)));

        Assert.Equal(350, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);
        Assert.Equal(350, await TopLevelTotalAsync(t, audio.Id));
        Assert.Equal(0, (await t.SummaryAsync(video.Id)).TotalSizeBytes);
        Assert.Equal(0, await TopLevelTotalAsync(t, video.Id));
    }

    [Fact]
    public async Task AParentCountsItsSubcategories_AndAFileInSeveralOfThemOnce()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var java = await t.BuiltInAsync("code.java");
        await t.AddOneAsync(code.Id, t.CreateFile("main.go", new string('g', 40)));
        var script = await t.AddOneAsync(python.Id, t.CreateFile("s.py", new string('p', 7)));

        // The script is in Python and Java (and so twice in Code's subtree), and also filed in Code itself.
        await t.Library.AddToCategoryAsync([script.Id], java.Id);
        await t.Library.AddToCategoryAsync([script.Id], code.Id);

        Assert.Equal(47, (await t.SummaryAsync(code.Id)).TotalSizeBytes);
        Assert.Equal(47, await TopLevelTotalAsync(t, code.Id));
        Assert.Equal(7, (await t.SummaryAsync(python.Id)).TotalSizeBytes);
        Assert.Equal(7, (await t.SummaryAsync(java.Id)).TotalSizeBytes);
        var children = await t.Library.GetCategoriesAsync(code.Id, CategorySort.Alphabetical);
        Assert.Equal(7, children.Single(s => s.Category.Id == python.Id).TotalSizeBytes);
        Assert.Equal(7, children.Single(s => s.Category.Id == java.Id).TotalSizeBytes);
    }

    [Fact]
    public async Task TwoFilesOfTheSameSize_BothCount()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        await t.AddOneAsync(text.Id, t.CreateFile("one.txt", new string('1', 30)));
        await t.AddOneAsync(text.Id, t.CreateFile("two.txt", new string('2', 30)));

        Assert.Equal(60, (await t.SummaryAsync(text.Id)).TotalSizeBytes);
    }

    [Fact]
    public async Task UnknownSizesAreLeftOut()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var known = await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", new string('a', 100)));
        var unknown = await t.AddOneAsync(audio.Id, t.CreateFile("b.mp3", new string('b', 64)));

        SetSizeUnknown(t, unknown.Id);
        Assert.Equal(100, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);
        Assert.Equal(2, (await t.SummaryAsync(audio.Id)).ItemCount);

        SetSizeUnknown(t, known.Id);
        Assert.Equal(0, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);
        Assert.Equal(0, await TopLevelTotalAsync(t, audio.Id));
    }

    [Fact]
    public async Task TheTotalFollowsDeletesRemovalsAndMoves()
    {
        await using var t = await TestLibrary.CreateAsync();
        var audio = await t.BuiltInAsync("audio");
        var video = await t.BuiltInAsync("video");
        var a = await t.AddOneAsync(audio.Id, t.CreateFile("a.mp3", new string('a', 100)));
        var b = await t.AddOneAsync(audio.Id, t.CreateFile("b.mp3", new string('b', 20)));
        var c = await t.AddOneAsync(audio.Id, t.CreateFile("c.mp3", new string('c', 3)));
        Assert.Equal(123, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);

        await t.Library.DeleteItemsAsync([a.Id]);
        Assert.Equal(23, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);

        await t.Library.MoveToCategoryAsync([b.Id], audio.Id, video.Id);
        Assert.Equal(3, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);
        Assert.Equal(20, (await t.SummaryAsync(video.Id)).TotalSizeBytes);

        await t.Library.AddToCategoryAsync([c.Id], video.Id);
        await t.Library.RemoveFromCategoryAsync([c.Id], audio.Id);
        Assert.Equal(0, (await t.SummaryAsync(audio.Id)).TotalSizeBytes);
        Assert.Equal(23, (await t.SummaryAsync(video.Id)).TotalSizeBytes);
        Assert.Equal(23, await TopLevelTotalAsync(t, video.Id));
    }

    private static async Task<long> TopLevelTotalAsync(TestLibrary t, long categoryId)
        => (await t.Library.GetCategoriesAsync(null, CategorySort.LastUpload)).Single(s => s.Category.Id == categoryId).TotalSizeBytes;

    /// <summary>A size the library doesn't know: size_bytes is nullable in the schema.</summary>
    private static void SetSizeUnknown(TestLibrary t, long itemId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(t.DataDirectory, Database.FileName),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        Assert.Equal(1, connection.Execute("UPDATE items SET size_bytes = NULL WHERE id = @itemId", new { itemId }));
    }
}
