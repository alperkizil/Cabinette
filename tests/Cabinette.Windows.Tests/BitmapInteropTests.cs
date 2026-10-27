using System.Runtime.InteropServices;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Tests;

/// <summary>HBITMAP → BitmapSource: orientation and alpha for the DIB sections the shell hands out.</summary>
public sealed unsafe partial class BitmapInteropTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DibSectionKeepsOrientationAndPremultipliedAlpha(bool topDown)
    {
        // 2x2: top row = opaque red, 50% white (premultiplied 128,128,128,128); bottom row = transparent, opaque blue.
        uint[] topRow = [0xFFFF0000, 0x80808080];
        uint[] bottomRow = [0x00000000, 0xFF0000FF];
        var hbitmap = CreateDib(2, 2, topDown, out var bits);
        try
        {
            var memoryRows = topDown ? new[] { topRow, bottomRow } : new[] { bottomRow, topRow };
            for (var r = 0; r < 2; r++)
            {
                for (var c = 0; c < 2; c++)
                {
                    ((uint*)bits)[(r * 2) + c] = memoryRows[r][c];
                }
            }

            DIBSECTION ds;
            _ = GetObject(hbitmap, sizeof(DIBSECTION), &ds);
            output.WriteLine($"topDown={topDown}: dsBmih.biHeight={ds.dsBmih.biHeight}, bmHeight={ds.dsBm.bmHeight}");

            var bitmap = Sta.Run(() => BitmapInterop.FromHBitmap(hbitmap));
            Assert.NotNull(bitmap);
            var pixels = Pixels.Bgra(bitmap, out var w, out _);
            Assert.Equal((0, 0, 255, 255), Tuple(Pixels.At(pixels, w, 0, 0)));        // red, top-left
            var white = Pixels.At(pixels, w, 1, 0);
            Assert.InRange(white.A, 126, 130);
            Assert.True(white.R > 250 && white.G > 250 && white.B > 250, $"un-premultiplied white expected, got {white}");
            Assert.Equal(0, Pixels.At(pixels, w, 0, 1).A);                             // transparent, bottom-left
            Assert.Equal((255, 0, 0, 255), Tuple(Pixels.At(pixels, w, 1, 1)));        // blue, bottom-right
        }
        finally
        {
            DeleteObject(hbitmap);
        }
    }

    [Fact]
    public void BitmapWithoutAlphaIsOpaque()
    {
        var hbitmap = CreateDib(1, 1, topDown: true, out var bits);
        try
        {
            *(uint*)bits = 0x00336699; // alpha byte 0
            var bitmap = Sta.Run(() => BitmapInterop.FromHBitmap(hbitmap));
            var pixels = Pixels.Bgra(bitmap!, out var w, out _);
            Assert.Equal((0x99, 0x66, 0x33, 255), Tuple(Pixels.At(pixels, w, 0, 0)));
        }
        finally
        {
            DeleteObject(hbitmap);
        }
    }

    private static (int, int, int, int) Tuple((byte B, byte G, byte R, byte A) p) => (p.B, p.G, p.R, p.A);

    private static IntPtr CreateDib(int width, int height, bool topDown, out IntPtr bits)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = topDown ? -height : height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        var hbitmap = CreateDIBSection(IntPtr.Zero, &header, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        Assert.NotEqual(IntPtr.Zero, hbitmap);
        return hbitmap;
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFOHEADER* pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
}
