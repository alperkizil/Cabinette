using System.IO;
using System.IO.Compression;
using System.Text;

namespace Cabinette.Windows.Tests;

/// <summary>
/// A test that needs the IFilter of a file type which only some PCs have (PDF, Office): skipped, with the reason, when no
/// filter for it is registered on this PC.
/// </summary>
public sealed class IFilterFactAttribute : FactAttribute
{
    public IFilterFactAttribute(string extension)
    {
        if (IFilterRegistry.FindFilter(extension) is null)
        {
            Skip = $"No IFilter for .{extension} files is installed on this PC.";
        }
    }
}

/// <summary>
/// The IFilter extractor, run in the test process. The plain-text (query.dll), HTML (nlhtml.dll), RTF (rtffilt.dll) and XML
/// (xmlfilter.dll) filters come with every Windows; the PDF and Office tests run where those filters are installed.
/// </summary>
public sealed class FilterTextExtractorTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FilterTextExtractor _extractor = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Lists_the_filters_of_this_pc()
    {
        foreach (var ext in new[] { "pdf", "docx", "xlsx", "pptx", "doc", "rtf", "html", "txt" })
        {
            var filter = IFilterRegistry.FindFilter(ext);
            output.WriteLine($".{ext}: {(filter is { } f ? $"{f:B} {IFilterRegistry.FindFilterDll(f)}" : "none")}");
        }

        Assert.NotNull(IFilterRegistry.FindFilter("txt"));
    }

    [Theory]
    [InlineData("txt")]
    [InlineData(".txt")]
    [InlineData("*.TXT")]
    [InlineData("html")]
    [InlineData("htm")]
    public void Finds_the_filters_every_windows_has(string extension)
        => Assert.NotNull(IFilterRegistry.FindFilter(extension));

    [Theory]
    [InlineData("mp3")] // the "null" filter: properties only, no text
    [InlineData("cabinette-no-such-type")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("tar.gz")]
    public void Has_no_text_filter_for(string? extension) => Assert.Null(IFilterRegistry.FindFilter(extension));

    [Fact]
    public void CanExtract_follows_the_registry()
    {
        Assert.True(_extractor.CanExtract(@"C:\x\page.HTML"));
        Assert.False(_extractor.CanExtract(@"C:\x\song.mp3"));
        Assert.False(_extractor.CanExtract(@"C:\x\README"));
    }

    [Fact]
    public async Task Extracts_plain_text()
    {
        var path = _folder.File("plain.txt", "Hello from the plain-text filter.\r\nSecond line.");
        var result = await _extractor.ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        Assert.False(result.Truncated);
        Assert.Contains("Hello from the plain-text filter.", result.Text);
        Assert.Contains("Second line.", result.Text);
    }

    [Fact]
    public async Task Extracts_unicode_text()
    {
        var path = Path.Combine(_folder.Path, "unicode.txt");
        File.WriteAllText(path, "Çağrı İşık ğüşöç 😀 end", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var result = await _extractor.ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        Assert.Contains("Çağrı İşık ğüşöç 😀 end", result.Text);
    }

    [Fact]
    public async Task Extracts_html_without_markup()
    {
        var path = _folder.File("page.html",
            "<html><head><title>The title</title></head><body><h1>Heading</h1><p>Hello <b>bold</b> world &amp; more.</p></body></html>");
        var result = await _extractor.ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        output.WriteLine(result.Text);
        Assert.Contains("Heading", result.Text);
        Assert.Contains("Hello bold world & more.", result.Text);
        Assert.DoesNotContain("<p>", result.Text);
        Assert.DoesNotContain("<b>", result.Text);
    }

    [Fact]
    public async Task Extracts_rtf()
    {
        var path = _folder.File("letter.rtf", @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs24 Hello from \b RTF\b0  text.\par Second paragraph.\par}");
        var result = await _extractor.ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        output.WriteLine(result.Text);
        // Windows' RTF filter runs paragraphs together ("text.Second paragraph.").
        Assert.Contains("Hello from RTF text.", result.Text);
        Assert.Contains("Second paragraph.", result.Text);
        Assert.DoesNotContain(@"\rtf1", result.Text);
    }

    [Fact]
    public async Task Stops_at_max_chars_and_says_so()
    {
        var path = _folder.File("long.txt", string.Concat(Enumerable.Repeat("0123456789", 5_000)));

        var cut = await _extractor.ExtractAsync(path, 1_234);
        Assert.NotNull(cut);
        Assert.True(cut.Truncated);
        Assert.Equal(1_234, cut.Text.Length);

        var whole = await _extractor.ExtractAsync(path, 1_000_000);
        Assert.NotNull(whole);
        Assert.False(whole.Truncated);
        Assert.StartsWith(cut.Text, whole.Text, StringComparison.Ordinal);
        Assert.Equal(50_000, whole.Text.Trim().Length);
    }

    [Fact]
    public async Task Never_ends_on_half_a_surrogate_pair()
    {
        var path = Path.Combine(_folder.Path, "emoji.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("a😀", 100)), new UnicodeEncoding(false, true));

        // "a😀" is three chars: a, high, low. A cut after the high surrogate backs off by one.
        var result = await _extractor.ExtractAsync(path, 5);
        Assert.NotNull(result);
        Assert.True(result.Truncated);
        Assert.Equal("a😀a", result.Text);
    }

    [Fact]
    public async Task Zero_max_chars_says_whether_there_is_text()
    {
        var path = _folder.File("some.txt", "some text");
        var result = await _extractor.ExtractAsync(path, 0);
        Assert.NotNull(result);
        Assert.Equal("", result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Gives_null_without_a_filter()
    {
        var path = _folder.File("data.cabinette-no-such-type", "text");
        Assert.Null(await _extractor.ExtractAsync(path, 100));
    }

    [Fact]
    public async Task A_missing_file_is_an_IOException()
    {
        var path = Path.Combine(_folder.Path, "missing.txt");
        await Assert.ThrowsAsync<FileNotFoundException>(() => _extractor.ExtractAsync(path, 100));
    }

    [Fact]
    public async Task A_cancelled_call_is_cancelled()
    {
        var path = _folder.File("plain.txt", "text");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _extractor.ExtractAsync(path, 100, cts.Token));
    }

    [IFilterFact("pdf")]
    public async Task Extracts_pdf()
    {
        var path = Path.Combine(_folder.Path, "hello.pdf");
        File.WriteAllBytes(path, TestDocuments.Pdf("Hello Cabinette PDF"));
        var result = await _extractor.ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        output.WriteLine(result.Text);
        Assert.Contains("Hello Cabinette PDF", result.Text);
    }

    [IFilterFact("docx")]
    public async Task Extracts_docx()
    {
        var path = Path.Combine(_folder.Path, "hello.docx");
        File.WriteAllBytes(path, TestDocuments.Docx("Hello Cabinette DOCX", "Second paragraph"));
        var result = await _extractor.ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        output.WriteLine(result.Text);
        Assert.Contains("Hello Cabinette DOCX", result.Text);
        Assert.Contains("Second paragraph", result.Text);
    }

    [IFilterFact("docx")]
    public async Task A_damaged_docx_is_rejected()
    {
        var path = _folder.File("broken.docx", "this is not a zip package");
        await Assert.ThrowsAsync<InvalidDataException>(() => _extractor.ExtractAsync(path, 10_000));
    }
}

/// <summary>Minimal documents for the IFilter tests.</summary>
internal static class TestDocuments
{
    /// <summary>A one-page PDF with one line of Helvetica text.</summary>
    public static byte[] Pdf(string line)
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({line}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>A minimal Word document with one paragraph per line.</summary>
    public static byte[] Docx(params string[] paragraphs)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            // Office's filter refuses a package without a styles part.
            Add(zip, "word/_rels/document.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "word/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"/>""");
            var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"));
            Add(zip, "word/document.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
