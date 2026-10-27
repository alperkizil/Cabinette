using System.Diagnostics;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Cabinette.Core.Tests;

public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int ItemCount = 50_000;

    [Fact]
    public async Task ChatListAndThread_StayFast_With50kItems()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var docs = await t.BuiltInAsync("documents");
        var codeSubtree = (await t.Library.GetAllCategoriesAsync())
            .Where(c => c.Id == code.Id || c.ParentId == code.Id)
            .Select(c => c.Id)
            .ToArray();

        Seed(t, codeSubtree, docs.Id);

        // Warm up (JIT, statement cache, page cache).
        await t.Library.GetCategoriesAsync(null, CategorySort.LastUpload);
        await t.Library.GetThreadItemsAsync(code.Id);

        var listTime = Measure(() => t.Library.GetCategoriesAsync(null, CategorySort.LastUpload), out var list);
        var threadTime = Measure(() => t.Library.GetThreadItemsAsync(code.Id), out var thread);
        var searchTime = Measure(() => t.Library.GetThreadItemsAsync(code.Id, "file_4242"), out var found);
        var globalTime = Measure(() => t.Library.SearchAsync("file_12 #tag3"), out _);
        var widestTime = Measure(() => t.Library.SearchAsync("file_1"), out var widest); // ~11k matches, the newest 500 listed
        var namedTime = Measure(() => t.Library.SearchNamePrefixAsync("file_1", 10), out var named); // the same ~11k start with it
        output.WriteLine($"GetCategoriesAsync: {listTime.TotalMilliseconds:F1} ms");
        output.WriteLine($"GetThreadItemsAsync ({thread.Count} items): {threadTime.TotalMilliseconds:F1} ms");
        output.WriteLine($"Thread search: {searchTime.TotalMilliseconds:F1} ms");
        output.WriteLine($"Global search: {globalTime.TotalMilliseconds:F1} ms");
        output.WriteLine($"Global search, 11k matches: {widestTime.TotalMilliseconds:F1} ms");
        output.WriteLine($"Name prefix search, 11k matches: {namedTime.TotalMilliseconds:F1} ms");

        Assert.Equal(ItemCount, list.Single(s => s.Category.Id == code.Id).ItemCount);
        Assert.Equal(ItemCount / 10, list.Single(s => s.Category.Id == docs.Id).ItemCount);
        Assert.Equal(ItemCount, thread.Count);
        Assert.All(thread.Take(100), i => Assert.NotEmpty(i.CategoryIds));
        Assert.NotEmpty(found);
        Assert.True(widest.HasMoreItems);
        Assert.Equal(10, named.Count);
        Assert.Equal("file_1.py", named[0].FileName); // the oldest file, but its name is the text without the extension

        // Targets are 50 ms / 300 ms on a release build; leave headroom for debug builds and busy machines.
        Assert.True(listTime < TimeSpan.FromMilliseconds(250), $"GetCategoriesAsync took {listTime.TotalMilliseconds:F0} ms");
        Assert.True(threadTime < TimeSpan.FromMilliseconds(1500), $"GetThreadItemsAsync took {threadTime.TotalMilliseconds:F0} ms");
        Assert.True(namedTime < TimeSpan.FromMilliseconds(1000), $"SearchNamePrefixAsync took {namedTime.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task PhotoThread_WithDatesTaken_StaysFast_With20kPhotos()
    {
        const int photos = 20_000;
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        SeedPhotos(t, images.Id, photos, withDates: false);
        await t.Library.GetThreadItemsAsync(images.Id);
        var withoutTime = Measure(() => t.Library.GetThreadItemsAsync(images.Id), out _);

        SeedPhotos(t, images.Id, photos, withDates: true);
        await t.Library.GetThreadItemsAsync(images.Id);
        var withTime = Measure(() => t.Library.GetThreadItemsAsync(images.Id), out var thread);
        output.WriteLine($"GetThreadItemsAsync ({thread.Count} photos): {withoutTime.TotalMilliseconds:F1} ms without dates taken, " +
                         $"{withTime.TotalMilliseconds:F1} ms with");

        Assert.Equal(photos, thread.Count);
        Assert.All(thread, i => Assert.Equal(new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i.Id), i.TakenAt));
        Assert.True(withTime < TimeSpan.FromMilliseconds(1500), $"GetThreadItemsAsync took {withTime.TotalMilliseconds:F0} ms");
    }

    /// <summary>Photos 1..<paramref name="count"/> in <paramref name="categoryId"/>; or, with <paramref name="withDates"/>, their dates taken.</summary>
    private static void SeedPhotos(TestLibrary t, long categoryId, int count, bool withDates)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(t.DataDirectory, "cabinette.db")};Pooling=False");
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = withDates
            ? "INSERT INTO item_dates(item_id, taken_at, file_size, file_modified_at) VALUES ($id, $taken, 1000, $at)"
            : """
              INSERT INTO items(id, mode, kind, file_name, extension, home_category_id, size_bytes, file_modified_at, added_at, added_by, batch_id)
              VALUES ($id, 0, 0, 'IMG_' || $id || '.jpg', 'jpg', $home, 1000, $at, $at, 0, 'b' || ($id / 10));
              INSERT INTO item_categories(item_id, category_id, added_at, added_by) VALUES ($id, $home, $at, 0);
              """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var taken = command.Parameters.Add("$taken", SqliteType.Text);
        command.Parameters.AddWithValue("$home", categoryId);
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var takenStart = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= count; i++)
        {
            id.Value = i;
            at.Value = start.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            taken.Value = takenStart.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static TimeSpan Measure<T>(Func<Task<T>> action, out T result)
    {
        // Best of three, to filter out GC pauses and scheduling noise.
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

    private static void Seed(TestLibrary t, long[] codeCategories, long docsId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(t.DataDirectory, "cabinette.db")};Pooling=False");
        connection.Open();
        using var tx = connection.BeginTransaction();

        using var tag = connection.CreateCommand();
        tag.Transaction = tx;
        tag.CommandText = "INSERT INTO tags(name) VALUES ('tag0'),('tag1'),('tag2'),('tag3'),('tag4')";
        tag.ExecuteNonQuery();

        using var item = connection.CreateCommand();
        item.Transaction = tx;
        item.CommandText = """
            INSERT INTO items(id, mode, kind, file_name, extension, home_category_id, size_bytes, hash, file_modified_at, added_at, added_by, batch_id)
            VALUES ($id, 0, 0, $name, 'py', $home, $size, $hash, $at, $at, $by, $batch)
            """;
        var pId = item.Parameters.Add("$id", SqliteType.Integer);
        var pName = item.Parameters.Add("$name", SqliteType.Text);
        var pHome = item.Parameters.Add("$home", SqliteType.Integer);
        var pSize = item.Parameters.Add("$size", SqliteType.Integer);
        var pHash = item.Parameters.Add("$hash", SqliteType.Blob);
        var pAt = item.Parameters.Add("$at", SqliteType.Text);
        var pBy = item.Parameters.Add("$by", SqliteType.Integer);
        var pBatch = item.Parameters.Add("$batch", SqliteType.Text);

        using var member = connection.CreateCommand();
        member.Transaction = tx;
        member.CommandText = "INSERT INTO item_categories(item_id, category_id, added_at, added_by) VALUES ($item, $cat, $at, $by)";
        var mItem = member.Parameters.Add("$item", SqliteType.Integer);
        var mCat = member.Parameters.Add("$cat", SqliteType.Integer);
        var mAt = member.Parameters.Add("$at", SqliteType.Text);
        var mBy = member.Parameters.Add("$by", SqliteType.Integer);

        using var itemTag = connection.CreateCommand();
        itemTag.Transaction = tx;
        itemTag.CommandText = "INSERT INTO item_tags(item_id, tag_id) VALUES ($item, (SELECT id FROM tags WHERE name = $tag))";
        var tItem = itemTag.Parameters.Add("$item", SqliteType.Integer);
        var tTag = itemTag.Parameters.Add("$tag", SqliteType.Text);

        var random = new Random(42);
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= ItemCount; i++)
        {
            var home = codeCategories[i % codeCategories.Length];
            var at = start.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            var by = i % 7 == 0 ? 1 : 0;
            var hash = new byte[16];
            random.NextBytes(hash);

            pId.Value = i;
            pName.Value = $"file_{i}.py";
            pHome.Value = home;
            pSize.Value = random.Next(1, 10_000_000);
            pHash.Value = hash;
            pAt.Value = at;
            pBy.Value = by;
            pBatch.Value = $"batch{i / 10}";
            item.ExecuteNonQuery();

            mItem.Value = i;
            mCat.Value = home;
            mAt.Value = at;
            mBy.Value = by;
            member.ExecuteNonQuery();

            if (i % 10 == 0)
            {
                mCat.Value = docsId;
                member.ExecuteNonQuery();
            }

            if (i % 20 == 0)
            {
                tItem.Value = i;
                tTag.Value = $"tag{i % 5}";
                itemTag.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }
}
