using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.IconGen;

/// <summary>
/// Writes .ico files: 32-bit BMP (DIB) entries below 256 px for the widest compatibility (Explorer, the resource
/// compiler, System.Drawing.Icon, the tray), and a PNG-compressed entry at 256 px as Windows expects.
/// </summary>
internal static class IcoFile
{
    public static void Write(string path, IReadOnlyList<BitmapSource> images)
    {
        var entries = images
            .OrderBy(i => i.PixelWidth)
            .Select(image => (Image: image, Data: image.PixelWidth >= 256 ? EncodePng(image) : EncodeDib(image)))
            .ToList();

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)entries.Count);

        // ICONDIRENTRY × n
        var offset = 6 + (16 * entries.Count);
        foreach (var (image, data) in entries)
        {
            writer.Write((byte)(image.PixelWidth >= 256 ? 0 : image.PixelWidth));
            writer.Write((byte)(image.PixelHeight >= 256 ? 0 : image.PixelHeight));
            writer.Write((byte)0); // palette size
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // color planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in entries)
        {
            writer.Write(data);
        }
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask (height is doubled, as icons require).</summary>
    private static byte[] EncodeDib(BitmapSource image)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var straight = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);

        var maskStride = ((width + 31) / 32) * 4;
        var mask = new byte[maskStride * height];

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40); // biSize
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bit count
        writer.Write(0); // BI_RGB
        writer.Write(pixels.Length + mask.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var row = height - 1; row >= 0; row--)
        {
            writer.Write(pixels, row * stride, stride);
            var maskRow = (height - 1 - row) * maskStride;
            for (var x = 0; x < width; x++)
            {
                if (pixels[(row * stride) + (x * 4) + 3] == 0)
                {
                    mask[maskRow + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        writer.Write(mask);
        return stream.ToArray();
    }
}
