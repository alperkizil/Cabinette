using System.IO;
using System.Text;
using System.Windows.Media;

namespace Cabinette.Windows.Tests;

/// <summary>PDFs get a page count and a first-page preview from Windows' built-in PDF engine (no PDF handler on stock Windows).</summary>
public sealed class PdfTests : IDisposable
{
    private readonly TempFolder _files = new();
    private readonly TempFolder _cache = new();

    public void Dispose()
    {
        _files.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public void PageCountComesFromThePdfItself()
    {
        var pdf = WritePdf("three pages.pdf", ["One", "Two", "Three"]);
        Assert.Equal(3, new ShellMediaInfoService().GetMediaInfo(pdf)?.PageCount);
    }

    [Fact]
    public async Task FirstPageIsRenderedAsThumbnail()
    {
        var pdf = WritePdf("preview.pdf", ["Title page", "Second"]);
        using var service = new ShellThumbnailService(_cache.Path);
        var thumb = await service.GetThumbnailAsync(pdf, 300);

        Assert.NotNull(thumb);
        Assert.Equal(PixelFormats.Pbgra32, thumb.Format);
        Assert.Equal(300, Math.Max(thumb.PixelWidth, thumb.PixelHeight));
        Assert.True(thumb.PixelHeight > thumb.PixelWidth, "US Letter page is portrait");

        // White page background, and somewhere near the top-left there is dark text.
        var pixels = Pixels.Bgra(thumb, out var w, out var h);
        Assert.True(Pixels.At(pixels, w, w - 3, h - 3).R > 240);
        var hasInk = false;
        for (var y = 0; y < h / 3 && !hasInk; y++)
        {
            for (var x = 0; x < w && !hasInk; x++)
            {
                hasInk = Pixels.At(pixels, w, x, y).R < 100;
            }
        }

        Assert.True(hasInk, "expected rendered text on page 1");
    }

    [Fact]
    public async Task BrokenPdfGivesNoThumbnailAndNoPageCount()
    {
        var path = Path.Combine(_files.Path, "broken.pdf");
        File.WriteAllText(path, "not really a pdf");

        using var service = new ShellThumbnailService(_cache.Path);
        Assert.Null(await service.GetThumbnailAsync(path, 200));
        Assert.Null(new ShellMediaInfoService().GetMediaInfo(path)?.PageCount);
    }

    private string WritePdf(string name, IReadOnlyList<string> pages)
    {
        var path = Path.Combine(_files.Path, name);
        File.WriteAllBytes(path, BuildPdf(pages));
        return path;
    }

    /// <summary>Minimal valid PDF, one line of Helvetica text per page.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<int>();
        foreach (var text in pages)
        {
            var stream = $"BT /F1 36 Tf 60 700 Td ({text}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
            var content = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            kids.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
