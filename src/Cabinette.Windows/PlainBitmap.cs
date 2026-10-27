using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.Windows;

internal static class PlainBitmap
{
    /// <summary>
    /// Copies any bitmap into a frozen, decoder-free Pbgra32 bitmap at 96 DPI. Decoder-backed frames created on a
    /// background thread do not render on the UI thread even when frozen, so every decoded image goes through this.
    /// </summary>
    public static BitmapSource From(BitmapSource source)
    {
        if (source.Format != PixelFormats.Pbgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        }

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }
}
