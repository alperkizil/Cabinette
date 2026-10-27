using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>
/// Reads the clipboard with the Win32 API instead of WPF's Clipboard class: file drops (CF_HDROP), images (a registered
/// "PNG" format first, then CF_DIBV5/CF_DIB with alpha and bottom-up rows handled) and Unicode text.
/// Never throws; an unreadable clipboard yields an empty payload.
/// </summary>
/// <remarks>When files are present the image is not decoded (files win the paste anyway); text is always included.</remarks>
public sealed class ClipboardReader : IClipboardReader
{
    private const int OpenAttempts = 10;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly Lazy<uint[]> PngFormats = new(() =>
        new[] { "PNG", "image/png" }.Select(RegisterClipboardFormat).Where(f => f != 0).ToArray());

    private static readonly ClipboardPayload Empty = new(null, null, null);

    public ClipboardPayload Read()
    {
        try
        {
            if (!TryOpenClipboard())
            {
                return Empty;
            }

            try
            {
                var formats = EnumerateFormats();
                var files = formats.Contains(CF_HDROP) ? ReadFileDrop() : null;
                var image = files is { Count: > 0 } ? null : ReadImage(formats);
                var text = formats.Contains(CF_UNICODETEXT) ? ReadText() : null;
                return new ClipboardPayload(files is { Count: > 0 } ? files : null, image, string.IsNullOrEmpty(text) ? null : text);
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    /// <summary>The clipboard is often held for a moment by another app (CLIPBRD_E_CANT_OPEN); retry briefly.</summary>
    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(15 + (attempt * 5));
        }

        return false;
    }

    private static List<uint> EnumerateFormats()
    {
        var formats = new List<uint>();
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0 && formats.Count < 512)
        {
            formats.Add(format);
        }

        return formats;
    }

    private static List<string>? ReadFileDrop()
    {
        var hdrop = GetClipboardData(CF_HDROP);
        return hdrop == IntPtr.Zero ? null : HDrop.ReadPaths(hdrop);
    }

    private static string? ReadText()
    {
        var bytes = ReadGlobal(CF_UNICODETEXT);
        if (bytes is null)
        {
            return null;
        }

        var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1));
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }

    private static byte[]? ReadImage(List<uint> formats)
    {
        foreach (var format in PngFormats.Value)
        {
            if (formats.Contains(format) && ReadGlobal(format) is { } png && TrimPng(png) is { } valid)
            {
                return valid;
            }
        }

        // The format the app put first is the native one; the other is synthesized by Windows from it.
        var dibFormats = formats.Where(f => f is CF_DIBV5 or CF_DIB).ToList();
        foreach (var format in dibFormats)
        {
            if (ReadGlobal(format) is { } dib && DibConverter.ToPng(dib) is { } converted)
            {
                return converted;
            }
        }

        return null;
    }

    private static byte[]? ReadGlobal(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (long)(ulong)GlobalSize(handle);
        if (size <= 0 || size > int.MaxValue)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Validates a PNG and cuts the padding GlobalSize adds after the IEND chunk.</summary>
    private static byte[]? TrimPng(byte[] data)
    {
        if (data.Length < PngSignature.Length || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return null;
        }

        var offset = PngSignature.Length;
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var end = offset + 12L + length;
            if (end > data.Length)
            {
                break;
            }

            if (data.AsSpan(offset + 4, 4).SequenceEqual("IEND"u8))
            {
                var trimmed = end == data.Length ? data : data.AsSpan(0, (int)end).ToArray();
                return IsDecodable(trimmed) ? trimmed : null;
            }

            offset = (int)end;
        }

        return IsDecodable(data) ? data : null;
    }

    private static bool IsDecodable(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
