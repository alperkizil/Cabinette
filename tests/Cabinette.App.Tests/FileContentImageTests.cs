using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>Images for read_file: scaled down (never up), upright, PNG with alpha else JPEG; damaged ones refused.</summary>
public sealed class FileContentImageTests : IDisposable
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private readonly ContentFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Keeps_transparency_as_png()
    {
        var png = await Images.EncodeAsync(() => new PngBitmapEncoder(), Images.Pixels(100, 50, (x, _) => x < 50 ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(128, 0, 255, 0)));
        var result = await Read("clear.png", png);

        Assert.Equal("image", result.Kind);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal((100, 50, 100, 50), (result.Width, result.Height, result.OriginalWidth, result.OriginalHeight));
        var pixels = await Images.DecodeAsync(result.ImageBase64!);
        Assert.Equal(0, pixels.At(10, 25).A);
        Assert.InRange(pixels.At(90, 25).A, (byte)120, (byte)136);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Gives_jpeg_without_alpha_and_never_scales_up()
    {
        var jpeg = await Images.EncodeAsync(() => new JpegBitmapEncoder(), Images.Pixels(200, 100, (_, _) => Red));
        var result = await Read("photo.jpg", jpeg);

        Assert.Equal("image/jpeg", result.MimeType);
        Assert.Equal((200, 100, 200, 100), (result.Width, result.Height, result.OriginalWidth, result.OriginalHeight));
        Assert.True(Images.IsRed((await Images.DecodeAsync(result.ImageBase64!)).At(100, 50)));
    }

    [Fact]
    public async Task Scales_a_large_image_so_its_longer_side_fits()
    {
        var wide = await Images.EncodeAsync(() => new PngBitmapEncoder(), Images.Pixels(4000, 1000, (x, _) => x < 2000 ? Red : Blue, alpha: false));
        var result = await Read("wide.png", wide);

        Assert.Equal("image/jpeg", result.MimeType); // a PNG without alpha
        Assert.Equal((AiProtocol.MaxImageEdgePixels, 392), (result.Width, result.Height));
        Assert.Equal((4000, 1000), (result.OriginalWidth, result.OriginalHeight));
        var pixels = await Images.DecodeAsync(result.ImageBase64!);
        Assert.True(Images.IsRed(pixels.At(100, 200)));
        Assert.True(Images.IsBlue(pixels.At(1400, 200)));
    }

    [Fact]
    public async Task Scales_a_tall_image_with_alpha()
    {
        var tall = await Images.EncodeAsync(() => new PngBitmapEncoder(), Images.Pixels(600, 3000, (_, y) => Color.FromArgb(200, 0, 0, (byte)(y % 256))));
        var result = await Read("tall.png", tall);

        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(AiProtocol.MaxImageEdgePixels, result.Height);
        Assert.InRange(result.Width!.Value, 313, 314); // 600 × 1568 / 3000 = 313.6; WPF's decoder rounds down
    }

    [Fact]
    public async Task Turns_a_jpeg_upright_by_its_exif_orientation()
    {
        // Stored 300×100, left half red: orientation 6 means "rotate 90° clockwise to show", so red ends up on top.
        var jpeg = await Images.EncodeAsync(() => new JpegBitmapEncoder(), Images.Pixels(300, 100, (x, _) => x < 150 ? Red : Blue), orientation: 6);
        var result = await Read("rotated.jpg", jpeg);

        Assert.Equal((100, 300, 100, 300), (result.Width, result.Height, result.OriginalWidth, result.OriginalHeight));
        var pixels = await Images.DecodeAsync(result.ImageBase64!);
        Assert.True(Images.IsRed(pixels.At(50, 40)), "red on top");
        Assert.True(Images.IsBlue(pixels.At(50, 260)), "blue below");
    }

    [Fact]
    public async Task Turns_a_large_jpeg_upright_while_scaling_it()
    {
        var jpeg = await Images.EncodeAsync(() => new JpegBitmapEncoder(), Images.Pixels(4000, 1000, (x, _) => x < 2000 ? Red : Blue), orientation: 8);
        var result = await Read("big-rotated.jpg", jpeg);

        // Orientation 8 (rotate 90° counter-clockwise): the left half (red) ends up at the bottom.
        Assert.Equal((392, AiProtocol.MaxImageEdgePixels, 1000, 4000), (result.Width, result.Height, result.OriginalWidth, result.OriginalHeight));
        var pixels = await Images.DecodeAsync(result.ImageBase64!);
        Assert.True(Images.IsBlue(pixels.At(196, 200)));
        Assert.True(Images.IsRed(pixels.At(196, 1400)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Orients_each_exif_orientation(int orientation)
    {
        // A 3×2 image with a different color per pixel, mapped the way EXIF defines each orientation.
        const int w = 3, h = 2;
        var shown = await StaWorker.RunAsync(() =>
            Images.Read(ImageContent.Orient(Images.Pixels(w, h, (x, y) => Color.FromRgb((byte)(40 * x), (byte)(100 * y), 7), alpha: false), orientation)));

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (h - 1 - y, x),
                    7 => (h - 1 - y, w - 1 - x),
                    8 => (y, w - 1 - x),
                    _ => (x, y),
                };
                var pixel = shown.At(dx, dy);
                Assert.Equal(((byte)(40 * x), (byte)(100 * y)), (pixel.R, pixel.G));
            }
        }
    }

    [Fact]
    public async Task Shows_the_first_frame_of_an_animated_gif()
    {
        var gif = await Images.EncodeAsync(() => new GifBitmapEncoder(), Images.Pixels(40, 40, (_, _) => Red, alpha: false), Images.Pixels(40, 40, (_, _) => Blue, alpha: false));
        var result = await Read("anim.gif", gif);

        Assert.Equal((40, 40), (result.Width, result.Height));
        Assert.True(Images.IsRed((await Images.DecodeAsync(result.ImageBase64!)).At(20, 20)));
    }

    [Fact]
    public async Task Shows_the_largest_image_of_an_icon()
    {
        var small = await Images.EncodeAsync(() => new PngBitmapEncoder(), Images.Pixels(16, 16, (_, _) => Red));
        var large = await Images.EncodeAsync(() => new PngBitmapEncoder(), Images.Pixels(48, 48, (_, _) => Blue));
        var result = await Read("app.ico", Images.Icon(small, large));

        Assert.Equal((48, 48), (result.Width, result.Height));
        Assert.True(Images.IsBlue((await Images.DecodeAsync(result.ImageBase64!)).At(24, 24)));
    }

    [Fact]
    public async Task Reads_a_pasted_image()
    {
        var png = await Images.EncodeAsync(() => new PngBitmapEncoder(), Images.Pixels(10, 10, (_, _) => Red));
        var path = _folder.Bytes("Pasted image.png", png);
        var result = await Reader().ReadAsync(ContentItems.For(path, ItemKind.PastedImage), 0);
        Assert.Equal("image", result.Kind);
    }

    [Theory]
    [InlineData("broken.jpg")]
    [InlineData("broken.png")]
    [InlineData("broken.gif")]
    public async Task Refuses_a_damaged_image(string name)
    {
        byte[] bytes = name.EndsWith(".jpg", StringComparison.Ordinal) ? [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 0x01, 0x02] : [.. "not an image at all"u8];
        var path = _folder.Bytes(name, bytes);
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.StartsWith(name, ex.Message);
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public async Task Refuses_an_image_whose_codec_is_missing_or_that_is_damaged()
    {
        // Garbage in a type that needs an optional codec: either reason may be true, and the message says both.
        var path = _folder.Bytes("photo.heic", [.. "ftypheic garbage"u8]);
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => Reader().ReadAsync(ContentItems.For(path), 0));
        Assert.Contains(".heic", ex.Message);
    }

    [Fact]
    public void Knows_which_pixel_formats_have_alpha()
    {
        Assert.True(ImageContent.HasAlpha(PixelFormats.Bgra32, null));
        Assert.True(ImageContent.HasAlpha(PixelFormats.Pbgra32, null));
        Assert.True(ImageContent.HasAlpha(PixelFormats.Rgba64, null));
        Assert.False(ImageContent.HasAlpha(PixelFormats.Bgr32, null));
        Assert.False(ImageContent.HasAlpha(PixelFormats.Bgr24, null));
        Assert.False(ImageContent.HasAlpha(PixelFormats.Gray8, null));
        Assert.True(ImageContent.HasAlpha(PixelFormats.Indexed8, new BitmapPalette([Colors.Red, Colors.Transparent])));
        Assert.False(ImageContent.HasAlpha(PixelFormats.Indexed8, new BitmapPalette([Colors.Red, Colors.Blue])));
    }

    private async Task<ReadFileResult> Read(string name, byte[] bytes)
    {
        var path = _folder.Bytes(name, bytes);
        return await Reader().ReadAsync(ContentItems.For(path), offset: 12345); // the offset is ignored for images
    }

    private static FileContentReader Reader() => new(new FakeTextExtractor());
}

/// <summary>Test images made and read with WPF imaging on STA threads.</summary>
internal static class Images
{
    public sealed record Decoded(int Width, int Height, byte[] Bgra)
    {
        public Color At(int x, int y)
        {
            var i = ((y * Width) + x) * 4;
            return Color.FromArgb(Bgra[i + 3], Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        }
    }

    public static bool IsRed(Color c) => c.R > 200 && c.G < 60 && c.B < 60;

    public static bool IsBlue(Color c) => c.B > 200 && c.R < 60 && c.G < 60;

    /// <summary>A frozen bitmap with each pixel's color from <paramref name="color"/> (Bgra32, or Bgr24 without alpha).</summary>
    public static BitmapSource Pixels(int width, int height, Func<int, int, Color> color, bool alpha = true)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = color(x, y);
                var i = ((y * width) + x) * 4;
                (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) = (c.B, c.G, c.R, alpha ? c.A : (byte)255);
            }
        }

        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        if (!alpha)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        }

        source.Freeze();
        return source;
    }

    /// <summary>Encodes the frames; a JPEG with an <paramref name="orientation"/> gets it in its EXIF block.</summary>
    public static Task<byte[]> EncodeAsync(Func<BitmapEncoder> create, params BitmapSource[] frames) => EncodeAsync(create, 0, frames);

    public static Task<byte[]> EncodeAsync(Func<BitmapEncoder> create, BitmapSource frame, int orientation) => EncodeAsync(create, orientation, [frame]);

    private static Task<byte[]> EncodeAsync(Func<BitmapEncoder> create, int orientation, BitmapSource[] frames) => StaWorker.RunAsync(() =>
    {
        var encoder = create();
        foreach (var frame in frames)
        {
            BitmapMetadata? metadata = null;
            if (orientation != 0)
            {
                metadata = new BitmapMetadata("jpg");
                metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
            }

            encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
        }

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    });

    public static Task<Decoded> DecodeAsync(string base64) => StaWorker.RunAsync(() =>
    {
        using var stream = new MemoryStream(Convert.FromBase64String(base64));
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Read(decoder.Frames[0]);
    });

    public static Decoded Read(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(bytes, bgra.PixelWidth * 4, 0);
        return new Decoded(bgra.PixelWidth, bgra.PixelHeight, bytes);
    }

    /// <summary>An .ico whose images are the given PNGs (square, at most 256 px).</summary>
    public static byte[] Icon(params byte[][] pngs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)pngs.Length);
        var offset = 6 + (16 * pngs.Length);
        foreach (var png in pngs)
        {
            var size = BitConverter.ToInt32([png[19], png[18], png[17], png[16]]); // the IHDR width, big-endian
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var png in pngs)
        {
            writer.Write(png);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
