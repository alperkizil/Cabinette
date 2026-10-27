using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Cabinette.Windows;

/// <summary>
/// PDF page count and first-page preview through Windows' built-in PDF engine (Windows.Data.Pdf).
/// Used as a fallback because a stock Windows install has no shell thumbnail or property handler for PDFs.
/// Must be called from background threads; never throws (encrypted or broken PDFs give null).
/// </summary>
internal static class PdfDocuments
{
    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static int? GetPageCount(string path)
    {
        try
        {
            var document = LoadAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            return document.PageCount > 0 ? (int)document.PageCount : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Renders page 1 on white with its longer side at <paramref name="sizePx"/> pixels, like a shell thumbnail.</summary>
    public static async Task<BitmapSource?> RenderFirstPageAsync(string path, int sizePx, CancellationToken ct)
    {
        try
        {
            var document = await LoadAsync(path, ct).ConfigureAwait(false);
            if (document.PageCount == 0)
            {
                return null;
            }

            using var page = document.GetPage(0);
            var size = page.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }

            var scale = sizePx / Math.Max(size.Width, size.Height);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };

            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);
            using var managed = stream.AsStreamForRead();
            var decoder = BitmapDecoder.Create(managed, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return PlainBitmap.From(decoder.Frames[0]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<PdfDocument> LoadAsync(string path, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(ct).ConfigureAwait(false);
        return await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
    }
}
