using System.Buffers.Binary;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Tests;

/// <summary>Round trips through the real clipboard (this overwrites the user's clipboard).</summary>
[Collection("Clipboard")]
/// <summary>These tests overwrite the user's clipboard, so they only run when CABINETTE_CLIPBOARD_TESTS=1.</summary>
public sealed class ClipboardTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CABINETTE_CLIPBOARD_TESTS") == "1";

    private readonly ClipboardReader _reader = new();

    [Fact]
    public void ReadsUnicodeText()
    {
        if (!Enabled)
        {
            return;
        }

        const string Text = "Cabinette ✓ çğış — 你好";
        var payload = Sta.Run(() =>
        {
            Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, Text), copy: true));
            return _reader.Read();
        });

        Assert.Equal(Text, payload.Text);
        Assert.Null(payload.FilePaths);
        Assert.Null(payload.ImagePng);
    }

    [Fact]
    public void ReadsFileDropList()
    {
        if (!Enabled)
        {
            return;
        }

        using var temp = new TempFolder();
        var files = new[] { temp.File("a.txt"), temp.File("b c.txt") };
        var payload = Sta.Run(() =>
        {
            var list = new StringCollection();
            list.AddRange(files);
            Retry(() => Clipboard.SetFileDropList(list));
            return _reader.Read();
        });

        Assert.Equal(files, payload.FilePaths);
        Assert.Null(payload.ImagePng);
    }

    [Fact]
    public void ReadsRegisteredPngFormat()
    {
        if (!Enabled)
        {
            return;
        }

        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "clip.png");
        ThumbnailTests.WritePng(path, 31, 17, dc => dc.DrawEllipse(Brushes.Purple, null, new Point(15, 8), 10, 6));
        var original = File.ReadAllBytes(path);

        var payload = Sta.Run(() =>
        {
            SetWin32(("PNG", Pad(original, 100)));
            return _reader.Read();
        });

        Assert.NotNull(payload.ImagePng);
        Assert.Equal(original, payload.ImagePng); // Padding after IEND trimmed.
    }

    [Fact]
    public void ReadsBottomUpDibV5WithAlpha()
    {
        if (!Enabled)
        {
            return;
        }

        // 2x2, bottom-up rows: bottom row = red (opaque), half-transparent green; top row = blue, transparent.
        var dib = BuildDib(headerSize: 124, width: 2, height: 2, bitfields: true, alphaMask: 0xFF000000, rowsBottomUp:
        [
            [Bgra(0, 0, 255, 255), Bgra(0, 255, 0, 128)],
            [Bgra(255, 0, 0, 255), Bgra(0, 0, 0, 0)],
        ]);

        var payload = Sta.Run(() =>
        {
            SetWin32((CF_DIBV5, dib));
            return _reader.Read();
        });

        Assert.NotNull(payload.ImagePng);
        var pixels = Pixels.Bgra(Pixels.DecodePng(payload.ImagePng), out var w, out var h);
        Assert.Equal((2, 2), (w, h));
        Assert.Equal((255, 0, 0, 255), ToTuple(Pixels.At(pixels, w, 0, 0)));  // top-left blue (B=255)
        Assert.Equal(0, Pixels.At(pixels, w, 1, 0).A);                         // top-right transparent
        Assert.Equal((0, 0, 255, 255), ToTuple(Pixels.At(pixels, w, 0, 1)));  // bottom-left red
        var green = Pixels.At(pixels, w, 1, 1);
        Assert.Equal(128, green.A);
        Assert.Equal(255, green.G);
    }

    [Fact]
    public void ZeroAlphaDibIsOpaque()
    {
        if (!Enabled)
        {
            return;
        }

        // 32bpp BI_RGB with the reserved byte left at zero (what most apps do) must not come out transparent/black.
        var dib = BuildDib(headerSize: 40, width: 2, height: -1, bitfields: false, alphaMask: 0, rowsBottomUp:
        [
            [Bgra(10, 20, 200, 0), Bgra(200, 20, 10, 0)],
        ]);

        var payload = Sta.Run(() =>
        {
            SetWin32((CF_DIB, dib));
            return _reader.Read();
        });

        Assert.NotNull(payload.ImagePng);
        var pixels = Pixels.Bgra(Pixels.DecodePng(payload.ImagePng), out var w, out _);
        Assert.Equal((10, 20, 200, 255), ToTuple(Pixels.At(pixels, w, 0, 0)));
        Assert.Equal((200, 20, 10, 255), ToTuple(Pixels.At(pixels, w, 1, 0)));
    }

    [Fact]
    public void Reads24BitDib()
    {
        if (!Enabled)
        {
            return;
        }

        // 3x2, 24bpp bottom-up, rows padded to 4 bytes.
        var stride = 12;
        var dib = new byte[40 + (stride * 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        dib[40] = 255; // bottom-left pixel blue
        dib[40 + stride + 2] = 255; // top-left pixel red

        var payload = Sta.Run(() =>
        {
            SetWin32((CF_DIB, dib));
            return _reader.Read();
        });

        Assert.NotNull(payload.ImagePng);
        var pixels = Pixels.Bgra(Pixels.DecodePng(payload.ImagePng), out var w, out var h);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal((0, 0, 255, 255), ToTuple(Pixels.At(pixels, w, 0, 0)));
        Assert.Equal((255, 0, 0, 255), ToTuple(Pixels.At(pixels, w, 0, 1)));
    }

    [Fact]
    public void EmptyClipboardGivesEmptyPayload()
    {
        if (!Enabled)
        {
            return;
        }

        var payload = Sta.Run(() =>
        {
            SetWin32();
            return _reader.Read();
        });

        Assert.True(payload.IsEmpty);
    }

    private static (int, int, int, int) ToTuple((byte B, byte G, byte R, byte A) p) => (p.B, p.G, p.R, p.A);

    private static uint Bgra(byte b, byte g, byte r, byte a) => (uint)(b | (g << 8) | (r << 16) | (a << 24));

    private static byte[] Pad(byte[] data, int extra) => [.. data, .. new byte[extra]];

    private static byte[] BuildDib(int headerSize, int width, int height, bool bitfields, uint alphaMask, uint[][] rowsBottomUp)
    {
        var rows = Math.Abs(height);
        var dib = new byte[headerSize + (width * 4 * rows)];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, (uint)headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), bitfields ? BI_BITFIELDS : BI_RGB);
        if (headerSize >= 56)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), alphaMask);
        }

        var offset = headerSize;
        foreach (var row in rowsBottomUp)
        {
            foreach (var pixel in row)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(offset), pixel);
                offset += 4;
            }
        }

        return dib;
    }

    private static void SetWin32(params (object Format, byte[] Data)[] items)
    {
        // SetClipboardData needs a window as clipboard owner (EmptyClipboard with a NULL owner makes it fail).
        using var owner = new HwndSource(new HwndSourceParameters("Cabinette clipboard test") { Width = 0, Height = 0 });
        Retry(() =>
        {
            if (!OpenClipboard(owner.Handle))
            {
                throw new ExternalException("OpenClipboard failed");
            }
        });

        try
        {
            EmptyClipboard();
            foreach (var (format, data) in items)
            {
                var id = format is string name ? RegisterClipboardFormat(name) : (uint)format;
                var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
                var pointer = GlobalLock(handle);
                Marshal.Copy(data, 0, pointer, data.Length);
                GlobalUnlock(handle);
                if (SetClipboardData(id, handle) == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    throw new ExternalException("SetClipboardData failed");
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}
