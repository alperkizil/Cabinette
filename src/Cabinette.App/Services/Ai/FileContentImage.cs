using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.Core.Categorization;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// Images for the read_file tool (SPEC.md section 10): decoded with WPF imaging (WIC, so webp and heic work when their
/// codecs are installed), scaled down so that the longer side is at most <see cref="AiProtocol.MaxImageEdgePixels"/>
/// (never up), turned upright by a JPEG's EXIF orientation, and encoded as PNG when the pixel format has alpha, else as
/// JPEG at quality 85. The first frame of an animated GIF or a multi-page TIFF; the largest image of an icon.
/// </summary>
internal static class ImageContent
{
    public const int JpegQuality = 85;

    /// <summary>EXIF "Orientation" in a JPEG's APP1 block.</summary>
    private const string JpegOrientationQuery = "/app1/ifd/{ushort=274}";

    /// <summary>Types every Windows decodes (WIC's own codecs); for others a codec may not be installed.</summary>
    private static readonly HashSet<string> BuiltInCodecs = new(StringComparer.Ordinal)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "ico", "wdp", "jxr",
    };

    private const BitmapCreateOptions CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;

    /// <summary>Decodes and encodes on a background STA thread (never the UI thread).</summary>
    public static Task<ReadFileResult> ReadAsync(string path, string fileName, string extension, int maxEdge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return StaWorker.RunAsync(() => Read(path, fileName, extension, maxEdge, ct));
    }

    internal static ReadFileResult Read(string path, string fileName, string extension, int maxEdge, CancellationToken ct)
    {
        using var stream = TextFilePages.Open(path);
        try
        {
            return Decode(stream, maxEdge, ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or
                                       COMException or OverflowException or EndOfStreamException or InvalidDataException)
        {
            // No decoder took the file (NotSupportedException): not an image, or one whose codec isn't installed.
            var codecMayBeMissing = ex is NotSupportedException && !BuiltInCodecs.Contains(ExtensionMap.Normalize(extension));
            throw new FileContentRefusedException(FileContentMessages.ImageNotDecodable(fileName, extension, codecMayBeMissing));
        }
    }

    private static ReadFileResult Decode(Stream stream, int maxEdge, CancellationToken ct)
    {
        // Header and metadata only; pixels are decoded below, scaled while decoding where the codec can.
        var decoder = BitmapDecoder.Create(stream, CreateOptions | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("No frames.");
        }

        var index = FrameToShow(decoder);
        var frame = decoder.Frames[index];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Empty image.");
        }

        var hasAlpha = HasAlpha(frame.Format, frame.Palette);
        var orientation = decoder is JpegBitmapDecoder ? ReadOrientation(frame) : 1;
        ct.ThrowIfCancellationRequested();

        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
        BitmapSource bitmap;
        if (scale < 1 && index == 0 && decoder is not IconBitmapDecoder)
        {
            // BitmapImage scales while decoding (a JPEG decodes at a fraction of its size): fast and small in memory.
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = CreateOptions;
            if (width >= height)
            {
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale));
            }
            else
            {
                image.DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale));
            }

            image.EndInit();
            bitmap = image;
        }
        else
        {
            bitmap = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        }

        bitmap = Orient(bitmap, orientation);
        var (originalWidth, originalHeight) = orientation >= 5 ? (height, width) : (width, height);
        ct.ThrowIfCancellationRequested();

        BitmapEncoder encoder;
        BitmapSource pixels;
        if (hasAlpha)
        {
            encoder = new PngBitmapEncoder();
            pixels = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        }
        else
        {
            encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            pixels = bitmap.Format == PixelFormats.Bgr24 || bitmap.Format == PixelFormats.Bgr32 || bitmap.Format == PixelFormats.Gray8
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        }

        if (pixels.CanFreeze)
        {
            pixels.Freeze();
        }

        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new ReadFileResult
        {
            Kind = "image",
            ImageBase64 = Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length),
            MimeType = hasAlpha ? "image/png" : "image/jpeg",
            Width = pixels.PixelWidth,
            Height = pixels.PixelHeight,
            OriginalWidth = originalWidth,
            OriginalHeight = originalHeight,
        };
    }

    /// <summary>The first frame (an animated GIF's first, a TIFF's first page), or an icon's largest image.</summary>
    private static int FrameToShow(BitmapDecoder decoder)
    {
        if (decoder is not IconBitmapDecoder || decoder.Frames.Count == 1)
        {
            return 0;
        }

        var best = 0;
        long bestArea = -1;
        for (var i = 0; i < decoder.Frames.Count; i++)
        {
            var area = (long)decoder.Frames[i].PixelWidth * decoder.Frames[i].PixelHeight;
            if (area > bestArea)
            {
                best = i;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>The pixel format has alpha (an indexed one has a transparent palette color).</summary>
    internal static bool HasAlpha(PixelFormat format, BitmapPalette? palette)
    {
        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64 ||
            format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
        {
            return true;
        }

        var indexed = format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4 ||
                      format == PixelFormats.Indexed8;
        return indexed && palette is not null && palette.Colors.Any(c => c.A < 255);
    }

    /// <summary>EXIF orientation 1–8 of a JPEG frame; 1 (upright) when it has none.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(JpegOrientationQuery) &&
                metadata.GetQuery(JpegOrientationQuery) is ushort value && value is >= 1 and <= 8)
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
            // Unreadable metadata: show the pixels as stored.
        }

        return 1;
    }

    /// <summary>Turns the pixels upright for an EXIF orientation (2–8 mirror and/or rotate; 1 is upright).</summary>
    internal static BitmapSource Orient(BitmapSource bitmap, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };

        return transform is null ? bitmap : new TransformedBitmap(bitmap, transform);
    }
}
