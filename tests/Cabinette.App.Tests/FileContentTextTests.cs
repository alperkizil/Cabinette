using System.IO;
using System.Text;
using Cabinette.App.Services.Ai;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>Text files for read_file: sniffing the encoding and paging bytes without ever splitting a character.</summary>
public sealed class FileContentTextTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private readonly ContentFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // ------------------------------------------------------------------ sniffing

    [Fact]
    public void Sniffs_utf8_with_and_without_a_bom()
    {
        var plain = TextFilePages.Sniff("hello ğüş"u8, atEnd: true);
        Assert.NotNull(plain);
        Assert.Equal(("utf-8", 0), (plain.Name, plain.BomLength));

        var bom = TextFilePages.Sniff([.. Utf8Bom, .. "hello"u8], atEnd: true);
        Assert.NotNull(bom);
        Assert.Equal(("utf-8", 3), (bom.Name, bom.BomLength));
    }

    [Theory]
    [InlineData("utf-16le", 2)]
    [InlineData("utf-16be", 2)]
    [InlineData("utf-32le", 4)]
    public void Sniffs_the_byte_order_marks(string name, int bomLength)
    {
        var encoding = EncodingNamed(name);
        var format = TextFilePages.Sniff([.. encoding.GetPreamble(), .. encoding.GetBytes("Hello 😀 text\r\n")], atEnd: true);
        Assert.NotNull(format);
        Assert.Equal((name, bomLength), (format.Name, format.BomLength));
    }

    [Fact]
    public void Nul_bytes_without_a_bom_are_binary()
    {
        Assert.Null(TextFilePages.Sniff("text\0more"u8, atEnd: true));

        // UTF-16 without a mark is not detected: SPEC says a byte-order mark, else UTF-8.
        Assert.Null(TextFilePages.Sniff(new UnicodeEncoding(false, false).GetBytes("plain text"), atEnd: true));
    }

    [Fact]
    public void Many_control_characters_are_binary()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 3 == 0 ? 0x01 : 'a')).ToArray();
        Assert.Null(TextFilePages.Sniff(bytes, atEnd: true));

        // Tabs, line breaks, form feeds, escapes and a DOS end-of-file mark are text.
        Assert.NotNull(TextFilePages.Sniff("a\tb\r\nc\fd\x1b[0m e\x1a"u8, atEnd: true));
    }

    [Fact]
    public void A_utf16_bom_before_binary_is_binary()
    {
        byte[] bytes = [0xFF, 0xFE, .. new byte[200]];
        Assert.Null(TextFilePages.Sniff(bytes, atEnd: true));
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_the_ansi_code_page()
    {
        byte[] latin1 = [.. "caf"u8, 0xE9, (byte)' ', 0xFC, (byte)'!'];
        var format = TextFilePages.Sniff(latin1, atEnd: true);
        Assert.NotNull(format);
        Assert.Same(TextFilePages.Ansi, format);
        Assert.StartsWith("windows-", format.Name);
    }

    [Fact]
    public void A_character_cut_off_at_the_end_of_the_sample_is_still_utf8()
    {
        // 8 KB of text whose last two bytes are the first two of "€" (E2 82 AC).
        var bytes = new byte[TextFilePages.SniffBytes];
        Array.Fill(bytes, (byte)'a');
        bytes[^2] = 0xE2;
        bytes[^1] = 0x82;

        Assert.Equal("utf-8", TextFilePages.Sniff(bytes, atEnd: false)?.Name);
        Assert.Same(TextFilePages.Ansi, TextFilePages.Sniff(bytes, atEnd: true));
    }

    [Fact]
    public void An_empty_file_is_text()
        => Assert.Equal("utf-8", TextFilePages.Sniff([], atEnd: true)?.Name);

    // ------------------------------------------------------------------ pages

    [Fact]
    public void Reads_a_small_file_in_one_page()
    {
        var path = _folder.Text("notes.txt", "Hello, ğüşöç İ 😀\nline 2");
        var page = Read(path, 0);

        Assert.Equal("text", page.Kind);
        Assert.Equal("Hello, ğüşöç İ 😀\nline 2", page.Text);
        Assert.Equal(0, page.Offset);
        Assert.Null(page.NextOffset);
        Assert.Equal(new FileInfo(path).Length, page.Total);
        Assert.Equal("bytes", page.Unit);
        Assert.Equal("utf-8", page.Encoding);
        Assert.Equal("file", page.Source);
    }

    [Fact]
    public void Skips_the_bom_at_offset_zero_and_moves_out_of_it()
    {
        var path = _folder.Bytes("bom.txt", [.. Utf8Bom, .. "abc"u8]);

        var first = Read(path, 0);
        Assert.Equal("abc", first.Text);
        Assert.Equal(0, first.Offset);

        var inside = Read(path, 1);
        Assert.Equal("abc", inside.Text);
        Assert.Equal(3, inside.Offset);
    }

    [Fact]
    public void A_utf8_page_never_ends_inside_a_character()
    {
        // a b [E2 82 AC] c d: a 4-byte page would cut "€" after its first two bytes.
        var path = _folder.Text("euro.txt", "ab€cd");

        var first = Read(path, 0, maxPageBytes: 4);
        Assert.Equal("ab", first.Text);
        Assert.Equal(2, first.NextOffset);

        var second = Read(path, 2, maxPageBytes: 4);
        Assert.Equal("€c", second.Text);
        Assert.Equal(6, second.NextOffset);

        var last = Read(path, 6, maxPageBytes: 4);
        Assert.Equal("d", last.Text);
        Assert.Null(last.NextOffset);
    }

    [Theory]
    [InlineData(3, 5, "cd")] // inside "€" (bytes 2-4): on to "c"
    [InlineData(4, 5, "cd")]
    [InlineData(2, 2, "€cd")]
    public void An_offset_inside_a_character_moves_to_the_next_one(long offset, long expectedOffset, string expectedText)
    {
        var path = _folder.Text("euro.txt", "ab€cd");
        var page = Read(path, offset);
        Assert.Equal(expectedText, page.Text);
        Assert.Equal(expectedOffset, page.Offset);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    public void An_offset_at_or_past_the_end_gives_an_empty_last_page(long offset)
    {
        var path = _folder.Text("euro.txt", "ab€cd"); // 7 bytes
        var page = Read(path, offset);
        Assert.Equal("", page.Text);
        Assert.Null(page.NextOffset);
        Assert.Equal(7, page.Total);
        Assert.Equal(offset, page.Offset);
    }

    [Fact]
    public void A_negative_offset_reads_from_the_start()
        => Assert.Equal("ab€cd", Read(_folder.Text("euro.txt", "ab€cd"), -5).Text);

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    public void Pages_put_together_give_the_whole_text(string encodingName)
    {
        var text = string.Concat(Enumerable.Repeat("Aç€😀ğ\r\n𝄞 İ x", 40));
        var encoding = EncodingNamed(encodingName == "utf-8-bom" ? "utf-8" : encodingName);
        byte[] preamble = encodingName == "utf-8" ? [] : encoding.GetPreamble();
        var bytes = (byte[])[.. preamble, .. encoding.GetBytes(text)];
        var path = _folder.Bytes("all.txt", bytes);

        foreach (var pageBytes in new[] { 4, 5, 6, 7, 9, 13, 64 })
        {
            var all = new StringBuilder();
            long? offset = 0;
            var pages = 0;
            while (offset is { } next)
            {
                var page = Read(path, next, pageBytes);
                Assert.Equal(next == 0 ? 0 : next, page.Offset);
                Assert.True(page.NextOffset is null || page.NextOffset > next, "a page always moves on");
                Assert.True((page.NextOffset ?? bytes.Length) - Math.Max(page.Offset, preamble.Length) <= pageBytes, "a page is at most the page size");
                Assert.DoesNotContain('�', page.Text!);
                all.Append(page.Text);
                offset = page.NextOffset;
                Assert.True(++pages < 10_000);
            }

            Assert.Equal(text, all.ToString());
        }
    }

    [Fact]
    public void A_utf16_page_never_splits_a_surrogate_pair()
    {
        // BOM, "a", 😀 (D83D DE00), "b": a 4-byte page would end between the halves of 😀.
        var path = _folder.Bytes("emoji16.txt", [0xFF, 0xFE, .. new UnicodeEncoding(false, false).GetBytes("a😀b")]);

        var first = Read(path, 0, maxPageBytes: 4);
        Assert.Equal("a", first.Text);
        Assert.Equal(4, first.NextOffset);

        var second = Read(path, 4, maxPageBytes: 4);
        Assert.Equal("😀", second.Text);
        Assert.Equal(8, second.NextOffset);
    }

    [Theory]
    [InlineData(3, 4, "😀b")] // odd: on to the next code unit
    [InlineData(6, 8, "b")] // the second half of 😀: on to "b"
    [InlineData(5, 8, "b")]
    public void A_utf16_offset_inside_a_character_moves_to_the_next_one(long offset, long expectedOffset, string expectedText)
    {
        var path = _folder.Bytes("emoji16.txt", [0xFF, 0xFE, .. new UnicodeEncoding(false, false).GetBytes("a😀b")]);
        var page = Read(path, offset);
        Assert.Equal(expectedText, page.Text);
        Assert.Equal(expectedOffset, page.Offset);
        Assert.Equal("utf-16le", page.Encoding);
    }

    [Fact]
    public void Real_pages_are_256_KB_and_end_on_a_character()
    {
        // "a" then two-byte "ğ"s: byte 262,144 is the second byte of a "ğ", so the first page ends one byte early.
        var text = "a" + new string('ğ', 150_000);
        var path = _folder.Text("big.txt", text);

        var first = Read(path, 0, AiProtocol.MaxTextPageBytes);
        Assert.Equal(AiProtocol.MaxTextPageBytes - 1, first.NextOffset);
        Assert.Equal(1 + ((AiProtocol.MaxTextPageBytes - 2) / 2), first.Text!.Length);

        var second = Read(path, first.NextOffset!.Value, AiProtocol.MaxTextPageBytes);
        Assert.Null(second.NextOffset);
        Assert.Equal(text, first.Text + second.Text);
    }

    [Fact]
    public void Reads_ansi_text_with_the_fallback_code_page()
    {
        byte[] bytes = [.. "caf"u8, 0xE9, (byte)' ', 0xFC];
        var path = _folder.Bytes("old.txt", bytes);
        var page = Read(path, 0);
        Assert.Equal(TextFilePages.Ansi.Name, page.Encoding);
        Assert.Equal(TextFilePages.Ansi.Encoding.GetString(bytes), page.Text);
    }

    [Fact]
    public void Reads_only_the_page_of_a_large_file()
    {
        // 64 MB of zeros after some text would be binary if the sniff read past 8 KB, and slow if the page did.
        var path = Path.Combine(_folder.Path, "large.log");
        using (var file = File.Create(path))
        {
            file.Write(System.Text.Encoding.UTF8.GetBytes(new string('x', 16 * 1024)));
            file.SetLength(64L * 1024 * 1024);
        }

        var page = Read(path, 0, AiProtocol.MaxTextPageBytes);
        Assert.Equal(AiProtocol.MaxTextPageBytes, page.NextOffset);
        Assert.Equal(64L * 1024 * 1024, page.Total);
    }

    private static ReadFileResult Read(string path, long offset, int maxPageBytes = AiProtocol.MaxTextPageBytes)
    {
        using var stream = TextFilePages.Open(path);
        var format = TextFilePages.Sniff(stream);
        Assert.NotNull(format);
        return TextFilePages.ReadPage(stream, format, offset, maxPageBytes);
    }

    private static Encoding EncodingNamed(string name) => name switch
    {
        "utf-8" => new UTF8Encoding(true),
        "utf-16le" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        "utf-32le" => new UTF32Encoding(false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
