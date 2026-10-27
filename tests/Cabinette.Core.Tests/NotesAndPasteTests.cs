using System.Text;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class NotesAndPasteTests
{
    [Fact]
    public async Task CreateNote_WritesAMarkdownFileNamedByLocalTime()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notes = await t.BuiltInAsync("notes");
        var text = "Buy milk\nand ünïcödé bread";

        var note = await t.Library.CreateNoteAsync(notes.Id, text);

        Assert.Equal("Note 2026-03-14 09-26-53.md", note.FileName);
        Assert.Equal(Path.Combine(t.Vault, "Notes", note.FileName), note.FullPath);
        Assert.Equal(ItemKind.Note, note.Kind);
        Assert.Equal(ItemMode.Copy, note.Mode);
        Assert.Equal(AddedBy.AppUi, note.AddedBy);
        Assert.Equal(text, note.NotePreview);
        var bytes = File.ReadAllBytes(note.FullPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB, "No BOM expected");
        Assert.Equal(text, Encoding.UTF8.GetString(bytes));
        Assert.Equal(text, await t.Library.ReadNoteTextAsync(note.Id));
    }

    [Fact]
    public async Task Notes_AreNeverDeduplicated_AndSameSecondNamesGetNumbered()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notes = await t.BuiltInAsync("notes");
        var a = await t.Library.CreateNoteAsync(notes.Id, "same");
        var b = await t.Library.CreateNoteAsync(notes.Id, "same");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("Note 2026-03-14 09-26-53.md", a.FileName);
        Assert.Equal("Note 2026-03-14 09-26-53 (1).md", b.FileName);
        Assert.Equal(2, (await t.Library.GetThreadItemsAsync(notes.Id)).Count);
    }

    [Fact]
    public async Task LongNote_PreviewIsTruncated_ButFullTextIsSearchable()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notes = await t.BuiltInAsync("notes");
        var text = new string('a', 1000) + " needle-in-the-haystack";
        var note = await t.Library.CreateNoteAsync(notes.Id, text);
        Assert.Equal(300, note.NotePreview!.Length);

        var found = await t.Library.GetThreadItemsAsync(notes.Id, "haystack");
        Assert.Equal(note.Id, Assert.Single(found).Id);
    }

    [Fact]
    public async Task PastedText_BecomesAPastedTextNote()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notes = await t.BuiltInAsync("notes");
        var result = await t.Library.AddPastedTextAsync(notes.Id, "some pasted words");
        var item = Assert.Single(result.Added);
        Assert.Equal("Pasted text 2026-03-14 09-26-53.md", item.FileName);
        Assert.Equal(ItemKind.Note, item.Kind);
        Assert.Equal(AddedBy.Paste, item.AddedBy);
        Assert.Equal("some pasted words", File.ReadAllText(item.FullPath));

        // Pasting the same text again is not de-duplicated.
        var again = await t.Library.AddPastedTextAsync(notes.Id, "some pasted words");
        Assert.Single(again.Added);
        Assert.Empty(again.Duplicates);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("  http://example.com  ", true)]
    [InlineData("https://example.com and more", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("C:\\Windows", false)]
    public async Task PastedText_SingleHttpUrl_BecomesALink(string text, bool isLink)
    {
        await using var t = await TestLibrary.CreateAsync();
        var links = await t.BuiltInAsync("links");
        var item = Assert.Single((await t.Library.AddPastedTextAsync(links.Id, text)).Added);
        if (isLink)
        {
            Assert.Equal(ItemKind.Link, item.Kind);
            Assert.Equal("Link 2026-03-14 09-26-53.url", item.FileName);
            Assert.Equal("links", item.TypeKey);
            Assert.Equal($"[InternetShortcut]\r\nURL={text.Trim()}\r\n", File.ReadAllText(item.FullPath));
            Assert.Equal(text.Trim(), await t.Library.ReadNoteTextAsync(item.Id));
        }
        else
        {
            Assert.Equal(ItemKind.Note, item.Kind);
        }
    }

    [Fact]
    public async Task PastedLink_IsDeduplicated()
    {
        await using var t = await TestLibrary.CreateAsync();
        var links = await t.BuiltInAsync("links");
        var notes = await t.BuiltInAsync("notes");
        var first = Assert.Single((await t.Library.AddPastedTextAsync(links.Id, "https://example.com/")).Added);
        t.Time.Advance(TimeSpan.FromSeconds(5));

        var same = await t.Library.AddPastedTextAsync(links.Id, "https://example.com/");
        Assert.Empty(same.Added);
        Assert.False(Assert.Single(same.Duplicates).AddedToTargetCategory);

        var elsewhere = await t.Library.AddPastedTextAsync(notes.Id, "https://example.com/");
        var dup = Assert.Single(elsewhere.Duplicates);
        Assert.True(dup.AddedToTargetCategory);
        Assert.Equal(first.Id, dup.Existing.Id);
        Assert.Single(Directory.GetFiles(Path.Combine(t.Vault, "Links")));
    }

    [Fact]
    public async Task PastedImage_IsSavedAsPng_AndDeduplicated()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };

        var item = Assert.Single((await t.Library.AddPastedImageAsync(images.Id, png)).Added);
        Assert.Equal("Pasted image 2026-03-14 09-26-53.png", item.FileName);
        Assert.Equal(ItemKind.PastedImage, item.Kind);
        Assert.Equal(AddedBy.Paste, item.AddedBy);
        Assert.Equal(png, File.ReadAllBytes(item.FullPath));

        var notes = await t.BuiltInAsync("notes");
        var again = await t.Library.AddPastedImageAsync(notes.Id, png);
        Assert.Empty(again.Added);
        var dup = Assert.Single(again.Duplicates);
        Assert.True(dup.AddedToTargetCategory);
        Assert.Equal(item.Id, dup.Existing.Id);

        // A file from disk with the same bytes is a duplicate as well.
        var fromDisk = await t.AddAsync(images.Id, ItemMode.Copy, t.CreateFile("same.png", png));
        Assert.Single(fromDisk.Duplicates);
    }
}
