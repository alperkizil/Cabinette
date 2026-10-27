using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Interop;

/// <summary>GDI bitmap/icon → frozen WPF bitmap conversion that keeps the alpha channel intact.</summary>
internal static unsafe class BitmapInterop
{
    /// <summary>
    /// Copies a 32bpp (or lower) HBITMAP into a frozen <see cref="BitmapSource"/>. Does not delete the HBITMAP.
    /// Shell images are usually premultiplied BGRA; bitmaps without any alpha are treated as opaque (the classic
    /// "black background" bug comes from reading them as straight alpha or from dropping the alpha entirely).
    /// </summary>
    public static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (hbitmap == IntPtr.Zero || !TryCopyPixels(hbitmap, out var pixels, out var width, out var height))
        {
            return null;
        }

        var format = ChooseFormat(pixels);
        if (format == PixelFormats.Bgr32)
        {
            // No alpha at all: make the reserved byte opaque so encoders/compositors never see transparency.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            format = PixelFormats.Bgra32;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Converts an HICON to a frozen bitmap. Does not destroy the icon.</summary>
    public static BitmapSource? FromHIcon(IntPtr hicon)
    {
        if (hicon == IntPtr.Zero)
        {
            return null;
        }

        var source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var copy = Materialize(source);
        return copy;
    }

    /// <summary>Scales <paramref name="source"/> down (never up) so its larger side is at most <paramref name="maxSize"/>.</summary>
    public static BitmapSource DownscaleToFit(BitmapSource source, int maxSize)
    {
        var largest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxSize <= 0 || largest <= maxSize)
        {
            return source;
        }

        var scale = (double)maxSize / largest;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        return Materialize(transformed);
    }

    /// <summary>Copies a (possibly lazily evaluated) bitmap into a plain frozen BGRA bitmap.</summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        BitmapSource converted = source;
        if (source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgra32)
        {
            converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        }

        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        var copy = BitmapSource.Create(w, h, 96, 96, converted.Format, null, pixels, w * 4);
        copy.Freeze();
        return copy;
    }

    /// <summary>
    /// Bgr32 when no pixel has alpha; Bgra32 (straight) when some color channel exceeds its alpha (impossible for
    /// premultiplied data); otherwise Pbgra32, which is what the shell hands out for thumbnails and icons.
    /// </summary>
    internal static PixelFormat ChooseFormat(ReadOnlySpan<byte> bgra)
    {
        var anyAlpha = false;
        var straight = false;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a != 0)
            {
                anyAlpha = true;
            }

            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
            {
                straight = true;
            }
        }

        if (!anyAlpha)
        {
            return PixelFormats.Bgr32;
        }

        return straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
    }

    private static bool TryCopyPixels(IntPtr hbitmap, out byte[] pixels, out int width, out int height)
    {
        pixels = [];
        width = height = 0;

        DIBSECTION ds;
        var written = GetObject(hbitmap, sizeof(DIBSECTION), &ds);
        if (written < sizeof(BITMAP))
        {
            return false;
        }

        width = ds.dsBm.bmWidth;
        height = Math.Abs(ds.dsBm.bmHeight);
        if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
        {
            return false;
        }

        pixels = new byte[width * height * 4];

        // Let GDI copy into a top-down 32bpp buffer. Reading the DIB section's bits directly is not an option:
        // GetObject reports a positive biHeight even for top-down sections (the shell's are top-down), so the
        // orientation can't be known. For 32bpp sources GetDIBits keeps the alpha byte as is.
        var info = stackalloc byte[sizeof(BITMAPINFOHEADER) + (256 * 4)];
        new Span<byte>(info, sizeof(BITMAPINFOHEADER) + (256 * 4)).Clear();
        var header = (BITMAPINFOHEADER*)info;
        header->biSize = (uint)sizeof(BITMAPINFOHEADER);
        header->biWidth = width;
        header->biHeight = -height;
        header->biPlanes = 1;
        header->biBitCount = 32;
        header->biCompression = BI_RGB;

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* dst = pixels)
            {
                var lines = GetDIBits(hdc, hbitmap, 0, (uint)height, dst, info, DIB_RGB_COLORS);
                if (lines != height)
                {
                    return false;
                }
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, hdc);
        }

        if (ds.dsBm.bmBitsPixel < 32)
        {
            // Lower bit depths carry no alpha; GDI leaves the byte at zero, which ChooseFormat reads as opaque.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0;
            }
        }

        return true;
    }
}
