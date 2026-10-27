using System.Diagnostics;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Cabinette.Core.Tests;

/// <summary>
/// Bug-hunt review of the Starred files commits (974f86b, 8cca83e): the paths the author's tests leave out. Every test here
/// asserts the behavior SPEC.md section 4 asks for, so a failure is a finding.
/// </summary>
public sealed class StarredReviewTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeletingACategory_WithItsFiles_RemovesTheStars_AndSaysSo()
    {
        await using var t = await TestLibrary.CreateAsync();
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var docs = await t.BuiltInAsync("documents");
        var gone = await t.AddOneAsync(work.Id, t.CreateFile("plan.txt", "plan"));
        var kept = await t.AddOneAsync(docs.Id, t.CreateFile("kept.txt", "kept"));
        await t.Library.SetStarredAsync([gone.Id, kept.Id], true);
        t.ClearEvents();

        await t.Library.DeleteCategoryAsync(work.Id, CategoryDeletePolicy.RecycleFiles);

        Assert.Equal([kept.Id], (await t.Library.GetStarredItemsAsync()).Select(i => i.Id));
        Assert.Equal(1, StarRows(t));
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Starred));
    }

    [Fact]
    public async Task RelinkingAMissingPointer_KeepsTheStar()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var path = t.CreateFile("linked.pdf", "the same content");
        var item = await t.AddOneAsync(docs.Id, path, ItemMode.Pointer);
        await t.Library.SetStarredAsync([item.Id], true);
        var starredAt = t.Time.GetUtcNow();

        File.Delete(path);
        Assert.False(await t.Library.VerifyExistsAsync(item.Id));
        var newPath = t.CreateFile(@"elsewhere\linked.pdf", "the same content");

        Assert.Equal(RelocateOutcome.Relocated, await t.Library.RelocateItemAsync(item.Id, newPath, acceptHashMismatch: false));

        var relinked = await t.ItemAsync(item.Id);
        Assert.Equal(newPath, relinked.FullPath);
        Assert.False(relinked.IsMissing);
        Assert.Equal(starredAt, relinked.StarredAt);
        Assert.Equal([item.Id], (await t.Library.GetStarredItemsAsync()).Select(i => i.Id));
    }

    [Fact]
    public async Task VaultMonitor_MoveToAnotherCategoryFolder_KeepsTheStar()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("scan.png", "scan"));
        await t.Library.SetStarredAsync([item.Id], true);
        var starredAt = t.Time.GetUtcNow();
        Directory.CreateDirectory(t.VaultPath("Images"));
        File.Move(item.FullPath, t.VaultPath(@"Images\scan.png"));
        t.ClearEvents();

        var summary = await t.RescanAsync();

        Assert.Equal(1, summary.Moved);
        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(images.Id, moved.HomeCategoryId);
        Assert.Equal(starredAt, moved.StarredAt);
        var starred = Assert.Single(await t.Library.GetStarredItemsAsync());
        Assert.Equal(t.VaultPath(@"Images\scan.png"), starred.FullPath);

        // The Starred view reloads on this: the item's id is in the change.
        Assert.Contains(t.Events, e => e.ItemIds.Contains(item.Id));
    }

    [Fact]
    public async Task MoveVault_KeepsTheStars_AndTellsTheStarredView()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report"));
        await t.Library.SetStarredAsync([item.Id], true);
        var starredAt = t.Time.GetUtcNow();
        var newVault = Path.Combine(t.Root, "vault2");
        Directory.CreateDirectory(newVault);
        t.ClearEvents();

        await t.Library.MoveVaultAsync(newVault);

        var moved = Assert.Single(await t.Library.GetStarredItemsAsync());
        Assert.Equal(starredAt, moved.StarredAt);
        Assert.StartsWith(newVault, moved.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(moved.FullPath));

        // ThreadViewModel.ClassifyStarredChange reloads when a shown item's category is in a Categories change.
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Categories) && e.CategoryIds.Intersect(moved.CategoryIds).Any());
    }

    [Fact]
    public async Task RenamingAParentCategory_ReportsTheSubcategory_SoTheStarredViewReloadsPaths()
    {
        await using var t = await TestLibrary.CreateAsync();
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var invoices = await t.Library.CreateCategoryAsync("Invoices", work.Id);
        var item = await t.AddOneAsync(invoices.Id, t.CreateFile("march.pdf", "march"));
        await t.Library.SetStarredAsync([item.Id], true);
        t.ClearEvents();

        await t.Library.RenameCategoryAsync(work.Id, "Work 2026");

        // ThreadViewModel.ClassifyStarredChange only knows the shown items' own categories (Invoices), not their parents.
        var change = Assert.Single(t.Events);
        Assert.True(change.Kind.HasFlag(LibraryChangeKind.Categories));
        Assert.Contains(invoices.Id, change.CategoryIds);
        Assert.Equal(t.VaultPath(@"Work 2026\Invoices\march.pdf"), Assert.Single(await t.Library.GetStarredItemsAsync()).FullPath);
    }

    [Fact]
    public async Task MigratePointers_KeepsTheStars()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("linked.pdf", "linked"), ItemMode.Pointer);
        await t.Library.SetStarredAsync([item.Id], true);
        var starredAt = t.Time.GetUtcNow();

        await t.Library.MigratePointersAsync();

        var migrated = await t.ItemAsync(item.Id);
        Assert.Equal(ItemMode.Copy, migrated.Mode);
        Assert.Equal(starredAt, migrated.StarredAt);
        Assert.Equal([item.Id], (await t.Library.GetStarredItemsAsync()).Select(i => i.Id));
    }

    [Fact]
    public async Task IdReuse_AfterDeletingTheNewestStarredFile_TheNextFileIsNotStarred()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        _ = await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "a"));
        var newest = await t.AddOneAsync(docs.Id, t.CreateFile("b.pdf", "b"));
        await t.Library.SetStarredAsync([newest.Id], true);

        await t.Library.DeleteItemsAsync([newest.Id]);
        Assert.Equal(0, StarRows(t));

        // SQLite hands out max(id) + 1 again: the new file gets the deleted file's id.
        var next = await t.AddOneAsync(docs.Id, t.CreateFile("c.pdf", "c"));
        Assert.Equal(newest.Id, next.Id);
        Assert.False(next.IsStarred);
        Assert.Empty(await t.Library.GetStarredItemsAsync());
        Assert.Equal(0, (await t.Library.GetStarredSummaryAsync()).Count);
    }

    [Fact]
    public async Task StarringWhileDeleting_NeverLeavesAStarBehind()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        for (var round = 0; round < 12; round++)
        {
            var item = await t.AddOneAsync(docs.Id, t.CreateFile($"r{round}.txt", $"round {round}"));
            var star = Task.Run(() => t.Library.SetStarredAsync([item.Id], true));
            var delete = Task.Run(() => t.Library.DeleteItemsAsync([item.Id]));
            await Task.WhenAll(star, delete);
            Assert.Null(await t.Library.GetItemAsync(item.Id));
        }

        Assert.Equal(0, StarRows(t));
        Assert.Equal(new StarredSummary(0, null), await t.Library.GetStarredSummaryAsync());
    }

    [Fact]
    public async Task SameNameReappearingInTheVault_IsANewFile_WithoutTheOldStar()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("notes.txt", "first"));
        await t.Library.SetStarredAsync([item.Id], true);

        File.Delete(item.FullPath);
        Assert.Equal(1, (await t.RescanAsync()).Removed);
        Assert.Equal(0, StarRows(t));

        File.WriteAllText(t.VaultPath(@"Documents\notes.txt"), "second, different content");
        Assert.Equal(1, (await t.RescanAsync()).AddedByCategory.Values.Sum());

        var again = Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Equal("notes.txt", again.FileName);
        Assert.False(again.IsStarred);
        Assert.Empty(await t.Library.GetStarredItemsAsync());
    }

    [Fact]
    public async Task Performance_With50kStarredFiles()
    {
        const int count = 50_000;
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        Seed(t, code.Id, count);

        await t.Library.GetThreadItemsAsync(code.Id); // warm up
        var thread = Measure(() => t.Library.GetThreadItemsAsync(code.Id), out var threadItems);
        var starredList = Measure(() => t.Library.GetStarredItemsAsync(), out var starredItems);
        var summary = Measure(() => t.Library.GetStarredSummaryAsync(), out var starredSummary);
        var search = Measure(() => t.Library.GetStarredItemsAsync("file_4242"), out var found);

        var ids = threadItems.Select(i => i.Id).ToList();
        var unstarAll = Measure(() => t.Library.SetStarredAsync(ids, false));
        var starAll = Measure(() => t.Library.SetStarredAsync(ids, true));
        var reopen = Measure(t.ReopenAsync);

        output.WriteLine($"GetThreadItemsAsync (50k, all starred): {thread.TotalMilliseconds:F0} ms");
        output.WriteLine($"GetStarredItemsAsync (50k): {starredList.TotalMilliseconds:F0} ms");
        output.WriteLine($"GetStarredSummaryAsync: {summary.TotalMilliseconds:F1} ms");
        output.WriteLine($"Starred search: {search.TotalMilliseconds:F0} ms");
        output.WriteLine($"SetStarredAsync 50k off / on: {unstarAll.TotalMilliseconds:F0} / {starAll.TotalMilliseconds:F0} ms");
        output.WriteLine($"Reopen (orphan prune over 50k stars): {reopen.TotalMilliseconds:F0} ms");

        Assert.Equal(count, threadItems.Count);
        Assert.All(threadItems.Take(100), i => Assert.True(i.IsStarred));
        Assert.Equal(count, starredItems.Count);
        Assert.Equal(count, starredSummary.Count);
        Assert.NotEmpty(found); // file_4242 and file_4242x: a substring search, as in a thread
        Assert.All(found, i => Assert.Contains("file_4242", i.FileName));
        Assert.Equal(count, (await t.Library.GetStarredSummaryAsync()).Count);

        // Generous debug-build bounds; the existing thread budget is 1500 ms for 50k items.
        Assert.True(thread < TimeSpan.FromMilliseconds(1500), $"GetThreadItemsAsync took {thread.TotalMilliseconds:F0} ms");
        Assert.True(starredList < TimeSpan.FromMilliseconds(1500), $"GetStarredItemsAsync took {starredList.TotalMilliseconds:F0} ms");
        Assert.True(summary < TimeSpan.FromMilliseconds(200), $"GetStarredSummaryAsync took {summary.TotalMilliseconds:F0} ms");
        Assert.True(starAll < TimeSpan.FromSeconds(3), $"SetStarredAsync(50k) took {starAll.TotalMilliseconds:F0} ms");
        Assert.True(reopen < TimeSpan.FromSeconds(3), $"Reopen took {reopen.TotalMilliseconds:F0} ms");
    }

    private static TimeSpan Measure<T>(Func<Task<T>> action, out T result)
    {
        var best = TimeSpan.MaxValue;
        result = default!;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            result = action().GetAwaiter().GetResult();
            sw.Stop();
            if (sw.Elapsed < best)
            {
                best = sw.Elapsed;
            }
        }

        return best;
    }

    private static TimeSpan Measure(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        action().GetAwaiter().GetResult();
        return sw.Elapsed;
    }

    /// <summary>50k vault items in <paramref name="categoryId"/>, every one starred (star times increasing).</summary>
    private static void Seed(TestLibrary t, long categoryId, int count)
    {
        using var connection = OpenRaw(t);
        using var tx = connection.BeginTransaction();

        using var item = connection.CreateCommand();
        item.Transaction = tx;
        item.CommandText = """
            INSERT INTO items(id, mode, kind, file_name, extension, home_category_id, size_bytes, hash, file_modified_at, added_at, added_by, batch_id)
            VALUES ($id, 0, 0, $name, 'py', $home, $size, $hash, $at, $at, 0, $batch)
            """;
        var pId = item.Parameters.Add("$id", SqliteType.Integer);
        var pName = item.Parameters.Add("$name", SqliteType.Text);
        var pHome = item.Parameters.Add("$home", SqliteType.Integer);
        var pSize = item.Parameters.Add("$size", SqliteType.Integer);
        var pHash = item.Parameters.Add("$hash", SqliteType.Blob);
        var pAt = item.Parameters.Add("$at", SqliteType.Text);
        var pBatch = item.Parameters.Add("$batch", SqliteType.Text);

        using var member = connection.CreateCommand();
        member.Transaction = tx;
        member.CommandText = "INSERT INTO item_categories(item_id, category_id, added_at, added_by) VALUES ($item, $cat, $at, 0)";
        var mItem = member.Parameters.Add("$item", SqliteType.Integer);
        var mCat = member.Parameters.Add("$cat", SqliteType.Integer);
        var mAt = member.Parameters.Add("$at", SqliteType.Text);

        using var star = connection.CreateCommand();
        star.Transaction = tx;
        star.CommandText = "INSERT INTO item_stars(item_id, starred_at) VALUES ($item, $at)";
        var sItem = star.Parameters.Add("$item", SqliteType.Integer);
        var sAt = star.Parameters.Add("$at", SqliteType.Text);

        var random = new Random(42);
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= count; i++)
        {
            var at = start.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            var hash = new byte[16];
            random.NextBytes(hash);
            pId.Value = i;
            pName.Value = $"file_{i}.py";
            pHome.Value = categoryId;
            pSize.Value = random.Next(1, 10_000_000);
            pHash.Value = hash;
            pAt.Value = at;
            pBatch.Value = $"batch{i / 10}";
            item.ExecuteNonQuery();

            mItem.Value = i;
            mCat.Value = categoryId;
            mAt.Value = at;
            member.ExecuteNonQuery();

            sItem.Value = i;
            sAt.Value = start.AddDays(1).AddSeconds(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            star.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static SqliteConnection OpenRaw(TestLibrary t)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(t.DataDirectory, Database.FileName),
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long StarRows(TestLibrary t)
    {
        using var connection = OpenRaw(t);
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_stars");
    }
}
