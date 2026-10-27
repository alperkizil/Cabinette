using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.Windows.Interop;

/// <summary>
/// Converts clipboard DIBs (CF_DIB / CF_DIBV5: a BITMAPINFO header followed by pixels) to PNG.
/// 32bpp data is decoded by hand so alpha survives (WIC and WPF ignore it in BI_RGB DIBs, which is why
/// Clipboard.GetImage() returns black or garbled images); other depths go through the BMP decoder.
/// </summary>
internal static class DibConverter
{
    private const int FileHeaderSize = 14;

    public static byte[]? ToPng(ReadOnlySpan<byte> dib)
    {
        try
        {
            var source = Decode(dib);
            return source is null ? null : EncodePng(source);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource? Decode(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            return DecodeWithBmpDecoder(dib, headerSize); // BITMAPCOREHEADER and other oddities.
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

        if (width <= 0 || rawHeight == 0 || (long)width * Math.Abs((long)rawHeight) > 256L * 1024 * 1024)
        {
            return null;
        }

        if (bitCount != 32 || (compression != NativeMethods.BI_RGB && compression != NativeMethods.BI_BITFIELDS && compression != NativeMethods.BI_ALPHABITFIELDS))
        {
            return DecodeWithBmpDecoder(dib, headerSize);
        }

        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0;

        // Channel masks: inside V2+ headers, or as 3 (4 with alpha) DWORDs right after a 40-byte header.
        uint red = 0x00FF0000, green = 0x0000FF00, blue = 0x000000FF, alpha = 0;
        var extraAfterHeader = 0;
        var hasBitfields = compression is NativeMethods.BI_BITFIELDS or NativeMethods.BI_ALPHABITFIELDS;
        if (headerSize >= 52)
        {
            if (hasBitfields)
            {
                red = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                green = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
            }

            if (headerSize >= 56)
            {
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
            }
        }
        else if (hasBitfields)
        {
            extraAfterHeader = compression == NativeMethods.BI_ALPHABITFIELDS ? 16 : 12;
            if (dib.Length < headerSize + extraAfterHeader)
            {
                return null;
            }

            red = BinaryPrimitives.ReadUInt32LittleEndian(dib[headerSize..]);
            green = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 4)..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 8)..]);
            if (compression == NativeMethods.BI_ALPHABITFIELDS)
            {
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 12)..]);
            }
        }

        if (red == 0 || green == 0 || blue == 0)
        {
            (red, green, blue) = (0x00FF0000, 0x0000FF00, 0x000000FF);
        }

        if (alpha == 0)
        {
            // The "reserved" byte of a 32bpp DIB: many apps put real alpha there, others leave it zero.
            alpha = ~(red | green | blue);
        }

        var pixelOffset = headerSize + extraAfterHeader + ((int)Math.Min(colorsUsed, 256u) * 4);
        var stride = width * 4;
        var needed = (long)stride * height;
        if (pixelOffset + needed > dib.Length)
        {
            // Some producers write the masks after a V5 header too, or none after a V3 one; trust the size instead.
            pixelOffset = (int)(dib.Length - needed);
            if (pixelOffset < headerSize)
            {
                return null;
            }
        }

        var src = dib.Slice(pixelOffset, (int)needed);
        var pixels = new byte[needed];
        var standard = red == 0x00FF0000 && green == 0x0000FF00 && blue == 0x000000FF && alpha == 0xFF000000;
        var anyAlpha = false;

        for (var y = 0; y < height; y++)
        {
            var srcRow = src.Slice((bottomUp ? height - 1 - y : y) * stride, stride);
            var dstRow = pixels.AsSpan(y * stride, stride);
            if (standard)
            {
                srcRow.CopyTo(dstRow);
                for (var x = 3; x < stride; x += 4)
                {
                    anyAlpha |= dstRow[x] != 0;
                }

                continue;
            }

            for (var x = 0; x < width; x++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(srcRow[(x * 4)..]);
                var a = Extract(value, alpha);
                dstRow[(x * 4) + 0] = Extract(value, blue);
                dstRow[(x * 4) + 1] = Extract(value, green);
                dstRow[(x * 4) + 2] = Extract(value, red);
                dstRow[(x * 4) + 3] = a;
                anyAlpha |= a != 0;
            }
        }

        if (!anyAlpha)
        {
            // An all-zero alpha channel means "no alpha", not "fully transparent".
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte Extract(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        var shift = BitOperations.TrailingZeroCount(mask);
        var bits = BitOperations.PopCount(mask);
        var v = (value & mask) >> shift;
        return bits switch
        {
            8 => (byte)v,
            > 8 => (byte)(v >> (bits - 8)),
            _ => (byte)(v * 255 / ((1u << bits) - 1)),
        };
    }

    /// <summary>Prepends a BITMAPFILEHEADER and lets WIC decode non-32bpp DIBs (palettes, 16/24bpp, RLE).</summary>
    private static BitmapSource? DecodeWithBmpDecoder(ReadOnlySpan<byte> dib, int headerSize)
    {
        if (headerSize < 12 || headerSize > dib.Length)
        {
            return null;
        }

        var paletteBytes = 0;
        var extra = 0;
        if (headerSize >= 40)
        {
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
            paletteBytes = (int)(colorsUsed != 0 ? Math.Min(colorsUsed, 256u) : bitCount <= 8 ? 1u << bitCount : 0) * 4;
            if (headerSize == 40 && compression == NativeMethods.BI_BITFIELDS)
            {
                extra = 12;
            }
        }
        else
        {
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            paletteBytes = bitCount <= 8 ? (1 << bitCount) * 3 : 0;
        }

        var file = new byte[FileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(FileHeaderSize + headerSize + extra + paletteBytes));
        dib.CopyTo(file.AsSpan(FileHeaderSize));

        using var stream = new MemoryStream(file);
        var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
