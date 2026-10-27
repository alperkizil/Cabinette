using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>How the bytes of a text file are characters.</summary>
internal enum TextEncodingKind
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Utf32LittleEndian,
    Utf32BigEndian,

    /// <summary>A single-byte ANSI code page (the fallback for text that isn't UTF-8).</summary>
    SingleByte,

    /// <summary>A double-byte ANSI code page (Japanese, Chinese, Korean Windows).</summary>
    DoubleByte,
}

/// <summary>A text file's encoding, as <see cref="TextFilePages.Sniff"/> decided from its first bytes.</summary>
/// <param name="Name">What the AI is told: "utf-8", "utf-16le", "windows-1254", ...</param>
/// <param name="BomLength">Bytes of the byte-order mark at the start, skipped when reading.</param>
internal sealed record TextFormat(TextEncodingKind Kind, Encoding Encoding, string Name, int BomLength)
{
    /// <summary>Bytes per code unit: characters (and pages) only start on a multiple of this after the BOM.</summary>
    public int UnitBytes => Kind switch
    {
        TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian => 2,
        TextEncodingKind.Utf32LittleEndian or TextEncodingKind.Utf32BigEndian => 4,
        _ => 1,
    };
}

/// <summary>
/// Text files for the read_file tool (SPEC.md section 10, "Reading contents"): decides from the first 8 KB whether a file
/// is text and in which encoding (a byte-order mark, else UTF-8, else the system's ANSI code page), and reads it in pages
/// of at most <see cref="AiProtocol.MaxTextPageBytes"/> bytes that never split a character. Reads only the bytes of the
/// page (and the first 8 KB), never the whole file.
/// </summary>
internal static class TextFilePages
{
    /// <summary>How much of the start of a file decides whether it is text.</summary>
    public const int SniffBytes = 8 * 1024;

    /// <summary>More control characters than this share of the sample (other than tabs, line breaks and the like) mean binary.</summary>
    private const double MaxControlShare = 0.02;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false);
    private static readonly Encoding Utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false);

    private static readonly Lazy<TextFormat> AnsiFallback = new(CreateAnsiFallback);

    /// <summary>The format used for text that has no byte-order mark and isn't valid UTF-8 (the system's ANSI code page).</summary>
    public static TextFormat Ansi => AnsiFallback.Value;

    /// <summary>Opens a file for reading while others may write, rename or delete it (a log being written).</summary>
    public static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);

    /// <summary>Reads the first <see cref="SniffBytes"/> bytes and decides; null when the file is binary.</summary>
    public static TextFormat? Sniff(FileStream stream)
    {
        var head = new byte[(int)Math.Min(SniffBytes, stream.Length)];
        stream.Position = 0;
        stream.ReadExactly(head);
        return Sniff(head, atEnd: head.Length == stream.Length);
    }

    /// <summary>
    /// Whether <paramref name="head"/> (the start of a file) is text, and its encoding; null when it is binary.
    /// <paramref name="atEnd"/>: the sample is the whole file (else a character may be cut off at its end).
    /// </summary>
    public static TextFormat? Sniff(ReadOnlySpan<byte> head, bool atEnd)
    {
        // A byte-order mark decides the encoding. UTF-32 LE comes first: its mark starts like UTF-16 LE's.
        TextFormat? format = head switch
        {
            [0xEF, 0xBB, 0xBF, ..] => new(TextEncodingKind.Utf8, Utf8, "utf-8", 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => new(TextEncodingKind.Utf32LittleEndian, Utf32Le, "utf-32le", 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => new(TextEncodingKind.Utf32BigEndian, Utf32Be, "utf-32be", 4),
            [0xFF, 0xFE, ..] => new(TextEncodingKind.Utf16LittleEndian, Utf16Le, "utf-16le", 2),
            [0xFE, 0xFF, ..] => new(TextEncodingKind.Utf16BigEndian, Utf16Be, "utf-16be", 2),
            _ => null,
        };

        if (format is null)
        {
            // Without a mark: text has no NUL bytes. Then UTF-8 when it is valid UTF-8, else the ANSI code page.
            if (head.Contains((byte)0))
            {
                return null;
            }

            format = IsUtf8(head, atEnd) ? new TextFormat(TextEncodingKind.Utf8, Utf8, "utf-8", 0) : Ansi;
        }

        return LooksLikeText(head[format.BomLength..], format, atEnd) ? format : null;
    }

    /// <summary>
    /// One page of text from byte <paramref name="offset"/>: at most <paramref name="maxPageBytes"/> bytes, never splitting
    /// a character. An offset inside a character (or inside the byte-order mark) moves on to the next character.
    /// </summary>
    public static ReadFileResult ReadPage(FileStream stream, TextFormat format, long offset, int maxPageBytes = AiProtocol.MaxTextPageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPageBytes, 4);
        var length = stream.Length;
        var requested = Math.Max(0, offset);

        // Clamped to the file before it is moved up to a character: an offset near long.MaxValue would wrap.
        var start = Math.Min(Math.Max(requested, format.BomLength), Math.Max(length, format.BomLength));
        var unit = format.UnitBytes;
        var misaligned = (start - format.BomLength) % unit;
        if (misaligned != 0)
        {
            start += unit - misaligned;
        }

        if (start >= length)
        {
            return Page("", format, requested == 0 ? 0 : Math.Max(requested, Math.Min(start, length)), next: null, length);
        }

        // The window: one code unit before the start (a UTF-16 surrogate check), the page, and a few bytes more for
        // moving the start forward past a split character.
        var windowStart = Math.Max(format.BomLength, start - unit);
        var windowLength = (int)Math.Min(length - windowStart, (start - windowStart) + maxPageBytes + 8);
        var window = new byte[windowLength];
        stream.Position = windowStart;
        stream.ReadExactly(window);

        var s = (int)(start - windowStart);
        s = SkipToCharacterStart(window, s, format);
        if (windowStart + s >= length)
        {
            return Page("", format, length, next: null, length);
        }

        var windowEnd = (int)Math.Min(window.Length, s + (long)maxPageBytes);
        var e = windowStart + windowEnd < length ? BackOffToCharacterStart(window, s, windowEnd, format) : windowEnd;
        var text = format.Encoding.GetString(window, s, e - s);
        var end = windowStart + e;
        var reported = requested == 0 ? 0 : windowStart + s;
        return Page(text, format, reported, end < length ? end : null, length);
    }

    private static ReadFileResult Page(string text, TextFormat format, long offset, long? next, long length) => new()
    {
        Kind = "text",
        Text = text,
        Offset = offset,
        NextOffset = next,
        Total = length,
        Unit = "bytes",
        Encoding = format.Name,
        Source = "file",
    };

    /// <summary>Moves <paramref name="index"/> forward to where a character starts (it may already).</summary>
    private static int SkipToCharacterStart(byte[] window, int index, TextFormat format)
    {
        switch (format.Kind)
        {
            case TextEncodingKind.Utf8:
                // At most three continuation bytes follow a lead byte; more are invalid data and decoded as they are.
                for (var i = 0; i < 3 && index < window.Length && IsUtf8Continuation(window[index]); i++)
                {
                    index++;
                }

                return index;
            case TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian:
                // The second half of a surrogate pair (the first half just before it) belongs to the previous page.
                if (index >= 2 && index + 1 < window.Length &&
                    char.IsLowSurrogate(Utf16Unit(window, index, format)) && char.IsHighSurrogate(Utf16Unit(window, index - 2, format)))
                {
                    index += 2;
                }

                return index;
            default:
                // UTF-32 and single-byte code pages: every aligned offset starts a character. Double-byte code pages
                // can't tell a trail byte from a lead byte without reading from the start: the offset is taken as is.
                return index;
        }
    }

    /// <summary>
    /// The end of a page that stops before the end of the file: moved back so that the next page starts on a character.
    /// Never moves back to <paramref name="start"/> (a page always has text).
    /// </summary>
    private static int BackOffToCharacterStart(byte[] window, int start, int end, TextFormat format)
    {
        var e = end;
        switch (format.Kind)
        {
            case TextEncodingKind.Utf8:
                for (var i = 0; i < 3 && e > start && IsUtf8Continuation(window[e]); i++)
                {
                    e--;
                }

                if (IsUtf8Continuation(window[e]))
                {
                    // Not UTF-8 here (four or more continuation bytes): cut where the page is full.
                    e = end;
                }

                break;
            case TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian:
                e -= (e - start) % 2;
                if (e - 2 > start && char.IsHighSurrogate(Utf16Unit(window, e - 2, format)))
                {
                    e -= 2;
                }

                break;
            case TextEncodingKind.Utf32LittleEndian or TextEncodingKind.Utf32BigEndian:
                e -= (e - start) % 4;
                break;
            case TextEncodingKind.DoubleByte:
                var codePage = format.Encoding.CodePage;
                var i2 = start;
                var lastStart = start;
                while (i2 < e)
                {
                    lastStart = i2;
                    i2 += AnsiNative.IsDBCSLeadByteEx((uint)codePage, window[i2]) ? 2 : 1;
                }

                if (i2 > e)
                {
                    e = lastStart;
                }

                break;
        }

        return e > start ? e : end;
    }

    private static bool IsUtf8Continuation(byte b) => (b & 0xC0) == 0x80;

    private static char Utf16Unit(byte[] bytes, int index, TextFormat format)
        => format.Kind == TextEncodingKind.Utf16LittleEndian
            ? (char)(bytes[index] | (bytes[index + 1] << 8))
            : (char)((bytes[index] << 8) | bytes[index + 1]);

    /// <summary>Valid UTF-8, allowing a character cut off at the end of a sample that isn't the whole file.</summary>
    private static bool IsUtf8(ReadOnlySpan<byte> bytes, bool atEnd)
    {
        var chars = new char[bytes.Length];
        var status = System.Text.Unicode.Utf8.ToUtf16(bytes, chars, out _, out _, replaceInvalidSequences: false, isFinalBlock: atEnd);
        return status == System.Buffers.OperationStatus.Done || (!atEnd && status == System.Buffers.OperationStatus.NeedMoreData);
    }

    /// <summary>
    /// Mostly printable once decoded: few control characters other than tab, line breaks, form feed, escape and the old
    /// end-of-file mark (Ctrl+Z), and none of U+0000 (a UTF-16 file with binary after its mark).
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> sample, TextFormat format, bool atEnd)
    {
        // Leave out a character the sample may have cut in two.
        var usable = sample.Length - (atEnd ? 0 : Math.Min(sample.Length, 3));
        usable -= usable % format.UnitBytes;
        if (usable <= 0)
        {
            return true;
        }

        var text = format.Encoding.GetString(sample[..usable]);
        var controls = 0;
        foreach (var c in text)
        {
            if (c == '\0')
            {
                return false;
            }

            if ((c < ' ' && c is not ('\t' or '\n' or '\v' or '\f' or '\r' or '\x1A' or '\x1B')) || c == '\x7F')
            {
                controls++;
            }
        }

        return controls <= text.Length * MaxControlShare;
    }

    private static TextFormat CreateAnsiFallback()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = 1252;
        try
        {
            var system = (int)AnsiNative.GetACP();

            // With "Use Unicode UTF-8 for worldwide language support" the ANSI code page is UTF-8, which this text
            // isn't: Western European is the best guess then.
            if (system is not (65001 or 0))
            {
                codePage = system;
            }
        }
        catch (Exception)
        {
            // Keep Windows-1252.
        }

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception)
        {
            encoding = Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }

        var kind = encoding.IsSingleByte ? TextEncodingKind.SingleByte : TextEncodingKind.DoubleByte;
        return new TextFormat(kind, encoding, encoding.WebName, 0);
    }

    private static class AnsiNative
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetACP();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsDBCSLeadByteEx(uint codePage, byte testChar);
    }
}
