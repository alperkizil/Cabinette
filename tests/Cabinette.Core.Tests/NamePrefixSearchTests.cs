using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// <see cref="ILibraryService.SearchNamePrefixAsync"/>: the files named like the search text, for the quick switcher. The main
/// search returns only the newest <see cref="LibraryService.GlobalSearchItemLimit"/> matches, so without it an older file named
/// exactly like the text is crowded out by newer files that merely contain the text (quick switcher bug hunt, finding 3).
/// </summary>
public sealed class NamePrefixSearchTests
{
    [Fact]
    public async Task An_old_exact_match_is_found_among_600_newer_files_that_contain_the_text()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var exact = await t.AddOneAsync(docs.Id, t.CreateFile("Report.pdf", "the one"));
        t.Time.Advance(TimeSpan.FromDays(1));
        Seed(t, docs.Id, Enumerable.Range(0, 600).Select(i => $"annual_report_{i:D4}.txt"), t.Time.GetUtcNow()); // contain "report"
        Seed(t, docs.Id, Enumerable.Range(0, 15).Select(i => $"Report draft {i:D2}.txt"), t.Time.GetUtcNow().AddHours(1)); // start with it

        // The main search lists the newest 500 only: the exact match is not among them.
        var search = await t.Library.SearchAsync("report");
        Assert.True(search.HasMoreItems);
        Assert.DoesNotContain(search.Items, i => i.Id == exact.Id);

        var named = await t.Library.SearchNamePrefixAsync("report", 10);

        Assert.Equal(10, named.Count);
        Assert.Equal(exact.Id, named[0].Id); // the exact name first, although it is the oldest
        Assert.All(named.Skip(1), i => Assert.StartsWith("Report draft ", i.FileName, StringComparison.Ordinal));
        Assert.Equal(["Report draft 14.txt", "Report draft 13.txt"], named.Skip(1).Take(2).Select(i => i.FileName)); // then newest first
        Assert.Equal(docs.Id, named[0].HomeCategoryId); // materialized like any search result
        Assert.Equal([docs.Id], named[0].CategoryIds);
    }

    [Fact]
    public async Task Exact_means_the_whole_name_or_the_name_without_its_extension_in_any_letter_case()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var start = t.Time.GetUtcNow();
        Seed(t, docs.Id, ["TAX RETURN.pdf"], start);
        Seed(t, docs.Id, ["tax return"], start.AddMinutes(1));
        Seed(t, docs.Id, ["Tax return.tar.gz"], start.AddMinutes(2)); // the name without its extension is "Tax return.tar"
        Seed(t, docs.Id, ["Tax returns 2025.pdf"], start.AddMinutes(3));
        Seed(t, docs.Id, ["My tax return.pdf"], start.AddMinutes(4)); // contains it, doesn't start with it

        var named = await t.Library.SearchNamePrefixAsync("  Tax   Return ", 10);

        Assert.Equal(["tax return", "TAX RETURN.pdf", "Tax returns 2025.pdf", "Tax return.tar.gz"], named.Select(i => i.FileName));
        Assert.Equal(["Tax return.tar.gz"], (await t.Library.SearchNamePrefixAsync("tax return.tar", 10)).Select(i => i.FileName));
    }

    [Fact]
    public async Task Short_text_tags_filters_and_the_limit_narrow_it_like_the_search()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");
        var ab = await t.AddOneAsync(docs.Id, t.CreateFile("ab.txt", "short"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        var abc = await t.AddOneAsync(docs.Id, t.CreateFile("Abc notes.txt", "longer"));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("abc.jpg", new byte[64]));
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.AddOneAsync(docs.Id, t.CreateFile("cab.txt", "contains ab"));
        await t.Library.SetItemTagsAsync(abc.Id, ["work"]);

        // Under three characters the trigram index can't help: LIKE, as in the search.
        Assert.Equal([ab.Id, photo.Id, abc.Id], (await t.Library.SearchNamePrefixAsync("ab", 10)).Select(i => i.Id));
        Assert.Equal([photo.Id, abc.Id], (await t.Library.SearchNamePrefixAsync("abc", 10)).Select(i => i.Id)); // "abc.jpg" is exact
        Assert.Equal([abc.Id], (await t.Library.SearchNamePrefixAsync("abc #work", 10)).Select(i => i.Id));
        Assert.Equal([photo.Id], (await t.Library.SearchNamePrefixAsync("abc", 10, new ItemFilter { TypeKeys = ["images"] })).Select(i => i.Id));
        Assert.Equal([photo.Id], (await t.Library.SearchNamePrefixAsync("abc", 1)).Select(i => i.Id));

        // No free text, no limit or FTS syntax: nothing, or the text taken literally.
        Assert.Empty(await t.Library.SearchNamePrefixAsync("#work", 10));
        Assert.Empty(await t.Library.SearchNamePrefixAsync("   ", 10));
        Assert.Empty(await t.Library.SearchNamePrefixAsync("abc", 0));
        Assert.Empty(await t.Library.SearchNamePrefixAsync("\"abc\" OR ab", 10));

        // Every file it returns also matches the search.
        foreach (var text in new[] { "ab", "abc", "abc #work" })
        {
            var searched = (await t.Library.SearchAsync(text)).Items.Select(i => i.Id).ToHashSet();
            Assert.All(await t.Library.SearchNamePrefixAsync(text, 10), i => Assert.Contains(i.Id, searched));
        }
    }

    [Fact]
    public async Task Letter_case_outside_ASCII_is_folded_by_the_trigram_index()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        Seed(t, docs.Id, ["ÇAY listesi.txt", "Şubat raporu.pdf"], t.Time.GetUtcNow());

        Assert.Equal(["ÇAY listesi.txt"], (await t.Library.SearchNamePrefixAsync("çay", 10)).Select(i => i.FileName));
        Assert.Equal(["Şubat raporu.pdf"], (await t.Library.SearchNamePrefixAsync("şubat raporu", 10)).Select(i => i.FileName));
    }

    /// <summary>Inserts pointer items straight into the database (much faster than adding hundreds of files).</summary>
    private static void Seed(TestLibrary t, long categoryId, IEnumerable<string> names, DateTimeOffset at)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(t.DataDirectory, Database.FileName)};Pooling=False");
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO items(mode, kind, file_name, extension, pointer_path, home_category_id, size_bytes, added_at, added_by, is_missing)
            VALUES (1, 0, $name, $ext, $path, $cat, 1, $at, 0, 0);
            INSERT INTO item_categories(item_id, category_id, added_at, added_by) VALUES (last_insert_rowid(), $cat, $at, 0);
            """;
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var ext = insert.Parameters.Add("$ext", SqliteType.Text);
        var path = insert.Parameters.Add("$path", SqliteType.Text);
        insert.Parameters.AddWithValue("$cat", categoryId);
        insert.Parameters.AddWithValue("$at", DbValues.ToText(at));
        foreach (var fileName in names)
        {
            name.Value = fileName;
            ext.Value = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            path.Value = Path.Combine(t.Source, "seeded", Guid.NewGuid().ToString("N"), fileName);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
