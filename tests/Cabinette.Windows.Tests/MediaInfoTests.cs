using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Cabinette.Windows.Tests;

public sealed class MediaInfoTests(ITestOutputHelper output)
{
    private readonly ShellMediaInfoService _service = new();

    [Fact]
    public void ImageSizeOfGeneratedPng()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "size.png");
        ThumbnailTests.WritePng(path, 123, 45, dc => dc.DrawRectangle(Brushes.Orange, null, new Rect(0, 0, 123, 45)));

        var info = _service.GetMediaInfo(path);

        Assert.NotNull(info);
        Assert.Equal(123, info.Width);
        Assert.Equal(45, info.Height);
        Assert.Null(info.Duration);
    }

    [Fact]
    public void DurationOfSystemSound()
    {
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        var wav = Directory.Exists(media) ? Directory.EnumerateFiles(media, "*.wav").FirstOrDefault() : null;
        if (wav is null)
        {
            output.WriteLine("No .wav in C:\\Windows\\Media; skipped.");
            return;
        }

        var info = _service.GetMediaInfo(wav);
        output.WriteLine($"{wav}: {info}");
        Assert.NotNull(info);
        Assert.NotNull(info.Duration);
        Assert.True(info.Duration > TimeSpan.Zero && info.Duration < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void VideoIfAvailable()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources"),
        };
        string? video = null;
        foreach (var dir in candidates.Where(Directory.Exists))
        {
            try
            {
                video = Directory.EnumerateFiles(dir, "*.mp4", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 })
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                // Ignore unreadable folders.
            }

            if (video is not null)
            {
                break;
            }
        }

        if (video is null)
        {
            output.WriteLine("No .mp4 found; skipped.");
            return;
        }

        var info = _service.GetMediaInfo(video);
        output.WriteLine($"{video}: {info}");
        Assert.NotNull(info);
        Assert.True(info.Duration > TimeSpan.Zero || info.Width > 0);
    }

    [Fact]
    public void WallpaperDimensions()
    {
        var image = ThumbnailTests.FindWallpaper();
        if (image is null)
        {
            return;
        }

        var info = _service.GetMediaInfo(image);
        output.WriteLine($"{image}: {info}");
        Assert.NotNull(info);
        Assert.True(info.Width > 100 && info.Height > 100);
    }

    [Fact]
    public void MissingOrUnsupportedReturnsNull()
    {
        using var temp = new TempFolder();
        Assert.Null(_service.GetMediaInfo(Path.Combine(temp.Path, "missing.mp4")));
        Assert.Null(_service.GetMediaInfo(temp.File("plain.txt", "hello")));
        Assert.Null(_service.GetMediaInfo(""));
    }

    [Fact]
    public void ThreadSafeUnderParallelCalls()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "parallel.png");
        ThumbnailTests.WritePng(path, 40, 30, dc => dc.DrawRectangle(Brushes.Green, null, new Rect(0, 0, 40, 30)));

        var results = new MediaInfo?[64];
        Parallel.For(0, results.Length, i => results[i] = _service.GetMediaInfo(path));
        Assert.All(results, r => Assert.Equal(new MediaInfo(null, 40, 30), r));

        var fromSta = Sta.Run(() => _service.GetMediaInfo(path));
        Assert.Equal(new MediaInfo(null, 40, 30), fromSta);
    }

    [Theory]
    [InlineData(@"C:\x\report.pdf", true)]
    [InlineData(@"C:\x\Letter.DOCX", true)]
    [InlineData(@"C:\x\slides.pptx", true)]
    [InlineData(@"C:\x\setup.msi", false)] // stores the installer version in the page-count property ("200 pages")
    [InlineData(@"C:\x\patch.msp", false)]
    [InlineData(@"C:\x\photo.jpg", false)]
    [InlineData(@"C:\x\noextension", false)]
    public void PageCountOnlyForPagedDocuments(string path, bool expected)
        => Assert.Equal(expected, ShellMediaInfoService.HasPages(path));
}