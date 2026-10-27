using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class SearchAndTagTests
{
    private sealed record Fixture(TestLibrary T, Category Code, Category Python, Category Docs, LibraryItem Invoice, LibraryItem Script,
        LibraryItem Photo, LibraryItem Pointer);

    private static async Task<Fixture> CreateAsync()
    {
        var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code");
        var python = await t.BuiltInAsync("code.python");
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");

        var invoice = await t.AddOneAsync(docs.Id, t.CreateFile("Invoice March 2026.pdf", new byte[2000]));
        t.Time.Advance(TimeSpan.FromDays(1));
        var script = await t.AddOneAsync(python.Id, t.CreateFile("march_report.py", "print('report')"));
        t.Time.Advance(TimeSpan.FromDays(1));
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("IMG_0001.jpg", new byte[5000]));
        t.Time.Advance(TimeSpan.FromDays(1));
        var pointer = await t.AddOneAsync(docs.Id, t.CreateFile("ab.txt", "pointer text"), ItemMode.Pointer);

        await t.Library.SetItemTagsAsync(invoice.Id, ["#Invoice", "2026", " tax "]);
        await t.Library.SetItemTagsAsync(script.Id, ["invoice"]);
        return new Fixture(t, code, python, docs, invoice, script, photo, pointer);
    }

    [Fact]
    public async Task GlobalSearch_MatchesFileNameSubstrings_CaseInsensitive_NewestFirst()
    {
        var f = await CreateAsync();
        await using var t = f.T;

        var results = await t.Library.SearchAsync("MARCH");
        Assert.Equal([f.Script.Id, f.Invoice.Id], results.Items.Select(i => i.Id));

        Assert.Equal([f.Photo.Id], (await t.Library.SearchAsync("img_0")).Items.Select(i => i.Id));

        // Several words must all match.
        Assert.Equal([f.Invoice.Id], (await t.Library.SearchAsync("march invoice")).Items.Select(i => i.Id));

        // Quotes and FTS syntax are taken literally.
        Assert.Empty((await t.Library.SearchAsync("\"march\" OR NEAR(")).Items);
    }

    [Fact]
    public async Task ShortText_FallsBackToLike()
    {
        var f = await CreateAsync();
        await using var t = f.T;
        Assert.Equal([f.Pointer.Id], (await t.Library.SearchAsync("ab")).Items.Select(i => i.Id));
        Assert.Contains(f.Script.Id, (await t.Library.SearchAsync("_r")).Items.Select(i => i.Id));
        Assert.DoesNotContain(f.Invoice.Id, (await t.Library.SearchAsync("_r")).Items.Select(i => i.Id));
        Assert.Empty((await t.Library.SearchAsync("%")).Items);
    }

    [Fact]
    public async Task Tags_AreAndedAndCombineWithText()
    {
        var f = await CreateAsync();
        await using var t = f.T;
        Assert.Equal([f.Script.Id, f.Invoice.Id], (await t.Library.SearchAsync("#invoice")).Items.Select(i => i.Id));
        Assert.Equal([f.Invoice.Id], (await t.Library.SearchAsync("#INVOICE #2026")).Items.Select(i => i.Id));
        Assert.Equal([f.Script.Id], (await t.Library.SearchAsync("#invoice report")).Items.Select(i => i.Id));
        Assert.Empty((await t.Library.SearchAsync("#invoice #nope")).Items);
    }

    [Fact]
    public async Task GlobalSearch_ReturnsMatchingCategories()
    {
        var f = await CreateAsync();
        await using var t = f.T;
        var results = await t.Library.SearchAsync("pyth");
        var cat = Assert.Single(results.Categories);
        Assert.Equal(f.Python.Id, cat.Category.Id);
        Assert.Equal(1, cat.ItemCount);
        Assert.Empty(results.Items);

        Assert.Empty((await t.Library.SearchAsync("#invoice")).Categories);
        Assert.Empty((await t.Library.SearchAsync("   ")).Items);
    }

    [Fact]
    public async Task ThreadSearch_CoversDescendants_AndNoteText()
    {
        var f = await CreateAsync();
        await using var t = f.T;
        var note = await t.Library.CreateNoteAsync(f.Python.Id, "remember the quarterly figures");

        Assert.Equal([f.Script.Id], (await t.Library.GetThreadItemsAsync(f.Code.Id, "march")).Select(i => i.Id));
        Assert.Equal([note.Id], (await t.Library.GetThreadItemsAsync(f.Code.Id, "quarterly")).Select(i => i.Id));
        Assert.Equal([note.Id], (await t.Library.GetThreadItemsAsync(f.Code.Id, "he")).Select(i => i.Id));
        Assert.Empty(await t.Library.GetThreadItemsAsync(f.Docs.Id, "quarterly"));

        // Main-window search covers file names only.
        Assert.Empty((await t.Library.SearchAsync("quarterly")).Items);
    }

    [Fact]
    public async Task Filters_TypeDateSizeModeMissingExtension()
    {
        var f = await CreateAsync();
        await using var t = f.T;
        async Task<long[]> Find(ItemFilter filter) => (await t.Library.SearchAsync("", filter)).Items.Select(i => i.Id).Order().ToArray();

        Assert.Equal([f.Photo.Id], await Find(new ItemFilter { TypeKeys = ["images"] }));
        Assert.Equal([f.Script.Id], await Find(new ItemFilter { TypeKeys = ["code"] }));
        Assert.Equal([f.Invoice.Id, f.Pointer.Id], await Find(new ItemFilter { TypeKeys = ["documents", "text"] }));
        Assert.Equal([f.Invoice.Id, f.Photo.Id], await Find(new ItemFilter { MinSizeBytes = 1000 }));
        Assert.Equal([f.Script.Id, f.Pointer.Id], await Find(new ItemFilter { MaxSizeBytes = 100 }));
        Assert.Equal([f.Pointer.Id], await Find(new ItemFilter { Mode = ItemMode.Pointer }));
        Assert.Equal([f.Invoice.Id, f.Pointer.Id], await Find(new ItemFilter { Extensions = ["PDF", ".txt"] }));
        Assert.Equal([f.Script.Id, f.Photo.Id],
            await Find(new ItemFilter { AddedFrom = f.Script.AddedAt, AddedTo = f.Photo.AddedAt }));

        Assert.Empty(await Find(new ItemFilter { MissingOnly = true }));
        File.Delete(f.Pointer.FullPath);
        Assert.False(await t.Library.VerifyExistsAsync(f.Pointer.Id));
        Assert.Equal([f.Pointer.Id], await Find(new ItemFilter { MissingOnly = true }));

        var other = await t.AddOneAsync(null, t.CreateFile("thing.unknownext", "?"));
        Assert.Equal([other.Id], await Find(new ItemFilter { TypeKeys = ["other"] }));

        // Filters also apply to thread search.
        Assert.Equal([f.Pointer.Id], (await t.Library.GetThreadItemsAsync(f.Docs.Id, null, new ItemFilter { Mode = ItemMode.Pointer })).Select(i => i.Id));
    }

    [Fact]
    public async Task Tags_AreNormalized_Counted_AndCleanedUp()
    {
        var f = await CreateAsync();
        await using var t = f.T;
        var tags = await t.Library.GetTagsAsync();
        Assert.Equal(["2026", "Invoice", "tax"], tags.Select(x => x.Name));
        Assert.Equal(2, tags.Single(x => x.Name == "Invoice").ItemCount);

        var invoice = await t.ItemAsync(f.Invoice.Id);
        Assert.Equal(["2026", "Invoice", "tax"], invoice.Tags);

        await t.Library.AddTagsAsync([f.Photo.Id, f.Script.Id], ["holiday", "#Holiday"]);
        Assert.Equal(["holiday"], (await t.ItemAsync(f.Photo.Id)).Tags);
        Assert.Equal(["holiday", "invoice"], (await t.ItemAsync(f.Script.Id)).Tags.Select(x => x.ToLowerInvariant()).Order());

        await t.Library.RemoveTagsAsync([f.Photo.Id, f.Script.Id], ["HOLIDAY"]);
        await t.Library.SetItemTagsAsync(f.Invoice.Id, []);
        tags = await t.Library.GetTagsAsync();
        Assert.Equal(["Invoice"], tags.Select(x => x.Name));
        Assert.Equal(1, tags[0].ItemCount);
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Tags) && e.ItemIds.Contains(f.Invoice.Id));
    }
}
