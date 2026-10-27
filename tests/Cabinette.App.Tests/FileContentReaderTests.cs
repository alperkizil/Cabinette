using System.IO;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Models;
using Cabinette.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App.Tests;

/// <summary>Which files read_file reads how, what it refuses, and pages of extracted text with the extraction cache.</summary>
public sealed class FileContentReaderTests : IDisposable
{
    private static readonly byte[] Binary = [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x08, 0x00, 0x01, 0x02, 0xFF, 0x00, 0x13];
    private readonly ContentFolder _folder = new();
    private readonly FakeTextExtractor _extractor = new();

    public void Dispose() => _folder.Dispose();

    // ------------------------------------------------------------------ text

    [Theory]
    [InlineData("readme.md")]
    [InlineData("main.cs")]
    [InlineData("data.csv")]
    [InlineData("data.tsv")]
    [InlineData("config.json")]
    [InlineData("feed.xml")]
    [InlineData("logo.svg")]
    [InlineData("page.html")]
    [InlineData("site.url")]
    [InlineData("notes.unknownext")]
    [InlineData("Makefile")]
    [InlineData("model.obj")] // a 3D model in a text format looks like text
    public async Task Reads_text_like_files_as_they_are(string name)
    {
        var path = _folder.Text(name, "<svg>Hello, ğüş 😀</svg>\n");
        var result = await Reader().ReadAsync(ContentItems.For(path), 0);

        Assert.Equal("text", result.Kind);
        Assert.Equal("<svg>Hello, ğüş 😀</svg>\n", result.Text);
        Assert.Equal("file", result.Source);
        Assert.Equal("bytes", result.Unit);
        Assert.Null(result.File);
        Assert.Empty(_extractor.Calls);
    }

    [Fact]
    public async Task Reads_notes_and_links_made_in_cabinette_as_text()
    {
        var note = _folder.Text("note.md", "# A note");
        Assert.Equal("# A note", (await Reader().ReadAsync(ContentItems.For(note, ItemKind.Note), 0)).Text);

        var link = _folder.Text("link.url", "[InternetShortcut]\r\nURL=https://example.com/\r\n");
        Assert.Contains("URL=https://example.com/", (await Reader().ReadAsync(ContentItems.For(link, ItemKind.Link), 0)).Text);
    }

    /// <summary>Always-binary types (.lnk, .class) have their own message: see the review tests, F3.</summary>
    [Theory]
    [InlineData("notes.txt")]
    [InlineData("Main.java")]
    [InlineData("data.json")]
    [InlineData("page.url")]
    public async Task A_text_extension_with_binary_content_is_refused(string name)
    {
        var path = _folder.Bytes(name, Binary);
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.Contains(name, ex.Message);
        Assert.Contains("binary", ex.Message);
        Assert.Contains("Cabinette can read", ex.Message);
    }

    // ------------------------------------------------------------------ refusals

    [Theory]
    [InlineData("song.mp3", "song.mp3 is an audio file.")]
    [InlineData("clip.mp4", "clip.mp4 is a video file.")]
    [InlineData("backup.zip", "backup.zip is an archive.")]
    [InlineData("setup.exe", "setup.exe is a program.")]
    [InlineData("windows.iso", "windows.iso is a disk image.")]
    [InlineData("font.ttf", "font.ttf is a font file.")]
    [InlineData("part.stl", "part.stl is a 3D model in a binary format.")]
    [InlineData("cover.psd", "cover.psd is a design file in a binary format, and no text extractor for .psd files is installed on this PC.")]
    [InlineData("deck.key", "No text extractor for .key files is installed on this PC, so Cabinette can't read deck.key.")]
    [InlineData("blob.bin", "blob.bin is a binary file, not text.")]
    public async Task Refuses_what_it_cannot_read(string name, string message)
    {
        var path = _folder.Bytes(name, Binary);
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.Equal($"{message} {FileContentMessages.WhatCanBeRead}", ex.Message);
    }

    [Fact]
    public async Task Refuses_audio_even_when_it_looks_like_text()
    {
        var path = _folder.Text("song.mp3", "ID3 text-looking header");
        await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
    }

    [Fact]
    public async Task A_missing_file_is_an_IOException()
    {
        var missing = Path.Combine(_folder.Path, "gone.txt");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Reader().ReadAsync(ContentItems.For(missing), 0));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Reader().ReadAsync(ContentItems.For(Path.Combine(_folder.Path, "gone.pdf")), 0));
    }

    [Fact]
    public async Task A_folder_is_an_IOException()
    {
        var folder = Path.Combine(_folder.Path, "folder.txt");
        Directory.CreateDirectory(folder);
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => Reader().ReadAsync(ContentItems.For(folder), 0));
        Assert.IsNotType<FileNotFoundException>(ex);
    }

    [Fact]
    public async Task A_locked_file_is_an_IOException()
    {
        var path = _folder.Text("locked.txt", "text");
        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
    }

    // ------------------------------------------------------------------ extracted text

    [Fact]
    public async Task Documents_go_through_their_extractor_first()
    {
        // RTF is text, but its words are what the AI wants.
        var path = _folder.Text("letter.rtf", @"{\rtf1 Hello}");
        _extractor.Texts[path] = "Hello";
        var result = await Reader().ReadAsync(ContentItems.For(path), 0);

        Assert.Equal("Hello", result.Text);
        Assert.Equal("extractor", result.Source);
        Assert.Equal("chars", result.Unit);
        Assert.Equal(5, result.Total);
        Assert.Null(result.NextOffset);
        Assert.Null(result.Encoding);
    }

    [Fact]
    public async Task Other_files_are_text_first_and_extracted_when_binary()
    {
        var text = _folder.Text("mail.msg", "plain text message");
        Assert.Equal("file", (await Reader().ReadAsync(ContentItems.For(text), 0)).Source);
        Assert.Empty(_extractor.Calls);

        var binary = _folder.Bytes("outlook.msg", Binary);
        _extractor.Texts[binary] = "From the extractor";
        var extracted = await Reader().ReadAsync(ContentItems.For(binary), 0);
        Assert.Equal("From the extractor", extracted.Text);
        Assert.Equal("extractor", extracted.Source);
    }

    [Fact]
    public async Task A_document_without_an_extractor_is_refused()
    {
        var path = _folder.Bytes("report.pdf", Binary);
        _extractor.Extensions.Clear();
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.StartsWith("No text extractor for .pdf files is installed on this PC", ex.Message);

        // The extractor said yes but found none after all.
        _extractor.Extensions.Add(".pdf");
        _extractor.ReturnNull = true;
        await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
    }

    [Fact]
    public async Task A_document_without_an_extractor_that_is_text_is_read_as_text()
    {
        var path = _folder.Text("table.ods", "looks like text");
        Assert.Equal("looks like text", (await Reader().ReadAsync(ContentItems.For(path), 0)).Text);
    }

    [Fact]
    public async Task Extraction_failures_become_refusals_or_IOExceptions()
    {
        var path = _folder.Bytes("report.pdf", Binary);

        _extractor.Throw = new InvalidDataException("report.pdf is protected by a password.");
        var rejected = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.Equal("Windows' text extractor couldn't read report.pdf: report.pdf is protected by a password.", rejected.Message);

        _extractor.Throw = new TimeoutException();
        var slow = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.Equal("Windows' text extractor for .pdf files took too long on report.pdf and was stopped.", slow.Message);

        _extractor.Throw = new IOException("locked");
        await Assert.ThrowsAsync<IOException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
    }

    [Fact]
    public async Task Pages_extracted_text_and_caches_the_extraction()
    {
        var path = _folder.Bytes("book.pdf", Binary);
        var text = string.Concat(Enumerable.Range(0, 600).Select(i => (char)('a' + (i % 26))));
        _extractor.Texts[path] = text;
        var reader = Reader(pageChars: 256);
        var item = ContentItems.For(path);

        var first = await reader.ReadAsync(item, 0);
        Assert.Equal(text[..256], first.Text);
        Assert.Equal(256, first.NextOffset);
        Assert.Null(first.Total); // not known yet: only the first 256 characters were extracted
        Assert.Equal(new[] { 256 }, _extractor.Calls);

        var second = await reader.ReadAsync(item, 256);
        Assert.Equal(text[256..512], second.Text);
        Assert.Equal(512, second.NextOffset);
        Assert.Equal(new[] { 256, 512 }, _extractor.Calls);

        // Twice what was extracted before (1,024 > 768): the rest of the document, at once.
        var third = await reader.ReadAsync(item, 512);
        Assert.Equal(text[512..], third.Text);
        Assert.Null(third.NextOffset);
        Assert.Equal(600, third.Total);
        Assert.Equal(new[] { 256, 512, 1024 }, _extractor.Calls);

        // The whole text is cached now: any page comes from memory.
        Assert.Equal(text[..256], (await reader.ReadAsync(item, 0)).Text);
        Assert.Equal(text[100..356], (await reader.ReadAsync(item, 100)).Text);
        Assert.Equal(3, _extractor.Calls.Count);

        // Past the end: an empty last page.
        var past = await reader.ReadAsync(item, 5_000);
        Assert.Equal("", past.Text);
        Assert.Null(past.NextOffset);

        // A changed file is extracted again.
        File.WriteAllBytes(path, [.. Binary, 1, 2, 3]);
        await reader.ReadAsync(item, 0);
        Assert.Equal(4, _extractor.Calls.Count);
    }

    [Fact]
    public async Task Extracted_pages_never_split_a_surrogate_pair()
    {
        var path = _folder.Bytes("emoji.pdf", Binary);
        var text = new string('a', 255) + "😀b";
        _extractor.Texts[path] = text;
        var reader = Reader(pageChars: 256);
        var item = ContentItems.For(path);

        var first = await reader.ReadAsync(item, 0);
        Assert.Equal(new string('a', 255), first.Text);
        Assert.Equal(255, first.NextOffset);

        var second = await reader.ReadAsync(item, 255);
        Assert.Equal("😀b", second.Text);
        Assert.Null(second.NextOffset);

        // An offset on the second half of 😀 moves on to "b".
        var inside = await reader.ReadAsync(item, 256);
        Assert.Equal("b", inside.Text);
        Assert.Equal(257, inside.Offset);
    }

    [Fact]
    public async Task Refuses_offsets_beyond_what_it_extracts()
    {
        var path = _folder.Bytes("huge.pdf", Binary);
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(
            () => Reader().ReadAsync(ContentItems.For(path), ExtractedTextPages.MaxExtractChars));
        Assert.Contains("16,777,216", ex.Message);
        Assert.Empty(_extractor.Calls);
    }

    [Fact]
    public void The_cache_is_small_and_bounded()
    {
        var cache = new ExtractedTextCache();
        for (var i = 0; i < 6; i++)
        {
            cache.Put(new ExtractionKey($@"C:\{i}.pdf", i, DateTime.UnixEpoch), new ExtractedText("text", false));
        }

        Assert.Equal(ExtractedTextCache.MaxEntries, cache.Count);
        Assert.Null(cache.Get(new ExtractionKey(@"C:\0.pdf", 0, DateTime.UnixEpoch)));
        Assert.NotNull(cache.Get(new ExtractionKey(@"c:\5.PDF", 5, DateTime.UnixEpoch)));

        // A newer version of a file replaces the older one; another time or size is another key.
        cache.Put(new ExtractionKey(@"C:\5.pdf", 50, DateTime.UnixEpoch), new ExtractedText("new", false));
        Assert.Null(cache.Get(new ExtractionKey(@"C:\5.pdf", 5, DateTime.UnixEpoch)));
        Assert.Equal("new", cache.Get(new ExtractionKey(@"C:\5.pdf", 50, DateTime.UnixEpoch))?.Text);

        // Too many characters in all: the oldest go, the newest always stays.
        var big = new ExtractedText(new string('x', ExtractedTextCache.MaxTotalChars), false);
        cache.Put(new ExtractionKey(@"C:\big.pdf", 1, DateTime.UnixEpoch), big);
        Assert.Equal(1, cache.Count);
        Assert.Same(big, cache.Get(new ExtractionKey(@"C:\big.pdf", 1, DateTime.UnixEpoch)));
    }

    [Fact]
    public void Extracts_twice_as_much_as_before_when_paging_on()
    {
        Assert.Equal(256, ExtractedTextPages.CharsToExtract(0, 256, null));
        Assert.Equal(1_000 + 256, ExtractedTextPages.CharsToExtract(1_000, 256, null));
        Assert.Equal(2_000, ExtractedTextPages.CharsToExtract(500, 256, new ExtractedText(new string('x', 1_000), true)));
        Assert.Equal(ExtractedTextPages.MaxExtractChars, ExtractedTextPages.CharsToExtract(long.MaxValue / 2, 256, null));
    }

    [Fact]
    public void Registers_the_isolated_extractor_and_the_reader()
    {
        using var provider = new ServiceCollection().AddAiContentReading().BuildServiceProvider();
        Assert.IsType<IsolatedTextExtractor>(provider.GetRequiredService<ITextExtractor>());
        Assert.IsType<FileContentReader>(provider.GetRequiredService<IFileContentReader>());
    }

    private FileContentReader Reader(int pageBytes = 1024, int pageChars = 1024) => new(_extractor, pageBytes, pageChars, 1568);
}
