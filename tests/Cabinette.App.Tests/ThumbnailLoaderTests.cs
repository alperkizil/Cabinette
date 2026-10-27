using System.Windows.Media.Imaging;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>Transient thumbnail failures are retried later instead of being cached as "no thumbnail" (finding 19).</summary>
public sealed class ThumbnailLoaderTests
{
    [Fact]
    public async Task Failure_is_not_cached_and_retried_after_a_while()
    {
        var shell = new FakeThumbnails { Fail = true };
        var clock = new ManualClock();
        var loader = new ThumbnailLoader(shell, new NoMediaInfo(), clock);
        var key = ThumbnailLoader.ThumbnailKey(@"C:\a.jpg", "v1", 96);

        Assert.Null(await loader.GetThumbnailAsync(@"C:\a.jpg", "v1", 96, CancellationToken.None));
        Assert.Equal(1, shell.Calls);
        Assert.False(loader.TryGetCached(key, out _)); // not remembered as "no thumbnail"

        // Right away: not asked again (recycled rows must not hammer the shell).
        Assert.Null(await loader.GetThumbnailAsync(@"C:\a.jpg", "v1", 96, CancellationToken.None));
        Assert.Equal(1, shell.Calls);

        // Later: asked again; this time the shell answers (genuinely no thumbnail), and that answer is cached.
        shell.Fail = false;
        clock.Advance(ThumbnailLoader.RetryAfterFailure + TimeSpan.FromSeconds(1));
        Assert.Null(await loader.GetThumbnailAsync(@"C:\a.jpg", "v1", 96, CancellationToken.None));
        Assert.Equal(2, shell.Calls);
        Assert.True(loader.TryGetCached(key, out var cached));
        Assert.Null(cached);

        Assert.Null(await loader.GetThumbnailAsync(@"C:\a.jpg", "v1", 96, CancellationToken.None));
        Assert.Equal(2, shell.Calls);
    }

    [Fact]
    public async Task Cancelled_request_leaves_nothing_behind()
    {
        var shell = new FakeThumbnails { Fail = true };
        var loader = new ThumbnailLoader(shell, new NoMediaInfo(), new ManualClock());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Null(await loader.GetThumbnailAsync(@"C:\b.jpg", "v1", 96, cts.Token));
        Assert.Null(await loader.GetThumbnailAsync(@"C:\b.jpg", "v1", 96, CancellationToken.None));
        Assert.Equal(1, shell.Calls);
    }

    private sealed class FakeThumbnails : IThumbnailService
    {
        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public Task<BitmapSource?> GetThumbnailAsync(string path, int sizePx, CancellationToken ct = default)
        {
            Calls++;
            return Fail ? throw new System.IO.IOException("The file is being written.") : Task.FromResult<BitmapSource?>(null);
        }

        public Task<BitmapSource?> GetFileIconAsync(string pathOrExtension, int sizePx, CancellationToken ct = default) => Task.FromResult<BitmapSource?>(null);
    }

    private sealed class NoMediaInfo : IMediaInfoService
    {
        public MediaInfo? GetMediaInfo(string path) => null;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
