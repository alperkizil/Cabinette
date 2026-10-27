using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.Windows.Tests;

public sealed class ThumbnailTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolder _cache = new("thumbs-" + Guid.NewGuid().ToString("N")[..6]);
    private readonly TempFolder _files = new();

    public void Dispose()
    {
        _cache.Dispose();
        _files.Dispose();
    }

    [Fact]
    public async Task ThumbnailForWallpaperIsFrozenAndSized()
    {
        var image = FindWallpaper();
        if (image is null)
        {
            output.WriteLine("No wallpaper image found; skipped.");
            return;
        }

        using var service = new ShellThumbnailService(_cache.Path);
        var thumb = await service.GetThumbnailAsync(image, 200);

        Assert.NotNull(thumb);
        Assert.True(thumb.IsFrozen);
        var largest = Math.Max(thumb.PixelWidth, thumb.PixelHeight);
        Assert.InRange(largest, 100, 200);

        var pixels = Pixels.Bgra(thumb, out _, out _);
        Assert.Contains(pixels.Where((_, i) => i % 4 != 3), b => b > 40); // Not a black image.
        output.WriteLine($"{image} → {thumb.PixelWidth}x{thumb.PixelHeight} {thumb.Format}");

        // Written to the disk cache, and a fresh service instance (empty memory cache) is served from disk.
        var cachePath = service.GetDiskCachePath(image, 200)!;
        await WaitUntil(() => File.Exists(cachePath));
        using var second = new ShellThumbnailService(_cache.Path);
        var cached = await second.GetThumbnailAsync(image, 200);
        Assert.NotNull(cached);
        Assert.Equal(thumb.PixelWidth, cached.PixelWidth);
        Assert.Equal(thumb.PixelHeight, cached.PixelHeight);

        // Regression: a decoder-backed BitmapFrame loaded on a pool thread rendered blank in the app.
        // The cached thumbnail must be a plain Pbgra32 bitmap that draws on a different (UI) thread.
        Assert.IsNotAssignableFrom<BitmapFrame>(cached);
        Assert.Equal(PixelFormats.Pbgra32, cached.Format);
        var centerAlpha = Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new ImageBrush(cached), null, new Rect(0, 0, cached.PixelWidth, cached.PixelHeight));
            }

            var target = new RenderTargetBitmap(cached.PixelWidth, cached.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var pixels = Pixels.Bgra(target, out var w, out var h);
            return Pixels.At(pixels, w, w / 2, h / 2).A;
        });
        Assert.True(centerAlpha > 200, $"cached thumbnail rendered with center alpha {centerAlpha}");
    }

    [Fact]
    public async Task TransparentPngKeepsItsAlpha()
    {
        // 256x256, transparent except an opaque red square in the middle and a 50% blue band at the bottom.
        var path = Path.Combine(_files.Path, "alpha.png");
        WritePng(path, 256, 256, dc =>
        {
            dc.DrawRectangle(Brushes.Red, null, new Rect(64, 64, 128, 128));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 255)), null, new Rect(0, 208, 256, 48));
        });

        using var service = new ShellThumbnailService(_cache.Path);
        var thumb = await service.GetThumbnailAsync(path, 128);
        AssertTransparentWithRedCenter(thumb);

        // Same after the PNG round trip through the disk cache.
        var cachePath = service.GetDiskCachePath(path, 128)!;
        await WaitUntil(() => File.Exists(cachePath));
        using var second = new ShellThumbnailService(_cache.Path);
        AssertTransparentWithRedCenter(await second.GetThumbnailAsync(path, 128));
    }

    private void AssertTransparentWithRedCenter(BitmapSource? thumb)
    {
        Assert.NotNull(thumb);
        var pixels = Pixels.Bgra(thumb, out var w, out var h);
        output.WriteLine($"{w}x{h} {thumb.Format}");
        var corner = Pixels.At(pixels, w, 2, 2);
        var center = Pixels.At(pixels, w, w / 2, h / 2);
        Assert.True(corner.A < 10, $"corner alpha {corner.A}");
        Assert.True(center.A > 245 && center.R > 230 && center.G < 30 && center.B < 30, $"center {center}");

        // Semi-transparent: straight color stays full blue (premultiplied data read as straight would come out ~50% darker).
        var band = Pixels.At(pixels, w, w / 2, h - 3);
        Assert.True(band.A is > 100 and < 156 && band.B > 220 && band.R < 30, $"band {band}");
    }

    [Fact]
    public async Task MissingFileAndTypesWithoutThumbnailReturnNull()
    {
        using var service = new ShellThumbnailService(_cache.Path);
        Assert.Null(await service.GetThumbnailAsync(Path.Combine(_files.Path, "missing.jpg"), 128));

        var unknown = _files.File("data.cabinettetest", "no thumbnail handler for this");
        Assert.Null(await service.GetThumbnailAsync(unknown, 128));
    }

    [Fact]
    public async Task CancelledRequestsAreCancelled()
    {
        using var service = new ShellThumbnailService(_cache.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetThumbnailAsync(FindWallpaper() ?? @"C:\Windows\explorer.exe", 128, cts.Token));
    }

    [Fact]
    public async Task CorruptDiskCacheIsRegenerated()
    {
        var image = Path.Combine(_files.Path, "plain.png");
        WritePng(image, 64, 64, dc => dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 64, 64)));

        using (var service = new ShellThumbnailService(_cache.Path))
        {
            var cachePath = service.GetDiskCachePath(image, 64)!;
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, [1, 2, 3, 4]);
            var thumb = await service.GetThumbnailAsync(image, 64);
            Assert.NotNull(thumb);
        }
    }

    [Theory]
    [InlineData(".pdf", 32)]
    [InlineData(".txt", 48)]
    [InlineData("docx", 256)]
    [InlineData(".cabinette-unknown", 64)]
    [InlineData("", 32)]
    public async Task FileIconByExtension(string extension, int size)
    {
        using var service = new ShellThumbnailService(_cache.Path);
        var icon = extension.Length == 0
            ? await service.GetFileIconAsync(@"C:\nope\README", size)
            : await service.GetFileIconAsync(extension, size);

        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
        output.WriteLine($"'{extension}' → {icon.PixelWidth}x{icon.PixelHeight} {icon.Format}");
        Assert.InRange(Math.Max(icon.PixelWidth, icon.PixelHeight), Math.Min(size, 32), size);

        var pixels = Pixels.Bgra(icon, out var w, out _);
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), a => a == 0);   // Some transparency (no black box)...
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), a => a == 255); // ...and some opaque icon pixels.
        Assert.True(Pixels.At(pixels, w, 0, 0).A == 0, "top-left corner should be transparent");

        // Cached per extension + size.
        var again = extension.Length == 0 ? await service.GetFileIconAsync(@"C:\nope\README", size) : await service.GetFileIconAsync(extension, size);
        Assert.Same(icon, again);
    }

    [Fact]
    public async Task FileIconForMissingPathUsesItsExtension()
    {
        using var service = new ShellThumbnailService(_cache.Path);
        var missing = await service.GetFileIconAsync(@"C:\Cabinette\missing\report.pdf", 48);
        var byExtension = await service.GetFileIconAsync(".pdf", 48);
        Assert.NotNull(missing);
        Assert.Same(byExtension, missing);
    }

    [Fact]
    public async Task FileIconForExistingExeAndFolder()
    {
        using var service = new ShellThumbnailService(_cache.Path);
        var exe = await service.GetFileIconAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), 64);
        var folder = await service.GetFileIconAsync(_files.Path, 64);
        Assert.NotNull(exe);
        Assert.NotNull(folder);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    internal static string? FindWallpaper()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).FirstOrDefault();
    }

    internal static void WritePng(string path, int width, int height, Action<DrawingContext> draw)
    {
        Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                draw(dc);
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
    }
}
