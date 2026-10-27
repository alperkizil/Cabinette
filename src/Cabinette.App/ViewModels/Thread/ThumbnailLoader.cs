using System.Windows.Media.Imaging;
using Cabinette.Windows;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>
/// Loads thumbnails, file icons and media info for the thread view in the background.
/// Keeps a small LRU of recent bitmaps (so scrolling back is instant), limits concurrency and honours cancellation
/// (containers cancel when they are recycled). Never blocks the UI thread.
/// </summary>
public sealed class ThumbnailLoader
{
    private const int CacheCapacity = 1000;

    /// <summary>Upper bound for decoded pixels kept by the LRU (a 660 px bubble thumbnail is about 1.3 MB).</summary>
    private const long CacheBudgetBytes = 160L * 1024 * 1024;
    private const int MaxConcurrency = 4;

    // Icons that are specific to the file rather than its type.
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "lnk", "ico", "url", "cur", "ani", "appref-ms", "website",
    };

    /// <summary>
    /// A load that threw (file still being written or locked, a busy shell handler, ...) is not a "no thumbnail" answer:
    /// it is not cached, only not retried for this long, so recycled rows don't hammer the shell.
    /// </summary>
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private const int MaxRememberedFailures = 256;

    private readonly IThumbnailService _thumbnails;
    private readonly IMediaInfoService _mediaInfo;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(MaxConcurrency);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.Ordinal);
    private int _pending;
    private long _bytes;

    public ThumbnailLoader(IThumbnailService thumbnails, IMediaInfoService mediaInfo)
        : this(thumbnails, mediaInfo, TimeProvider.System)
    {
    }

    internal ThumbnailLoader(IThumbnailService thumbnails, IMediaInfoService mediaInfo, TimeProvider time)
    {
        _thumbnails = thumbnails;
        _mediaInfo = mediaInfo;
        _time = time;
    }

    /// <summary>Requests that have started and not finished yet (used by the dev harness to wait for a quiet screen).</summary>
    public int PendingCount => Volatile.Read(ref _pending);

    public static string ThumbnailKey(string path, string version, int sizePx) => $"t|{sizePx}|{version}|{path}";

    /// <summary>
    /// Icons are shared per extension (10,000 PDFs need one shell call), except for types whose icon is per file (.exe, .lnk, ...).
    /// Missing files always use the extension.
    /// </summary>
    public static (string Key, string Request) IconRequest(string path, string extension, bool isMissing, int sizePx)
    {
        var perFile = !isMissing && (extension.Length == 0 || PerFileIconExtensions.Contains(extension));
        var request = perFile ? path : "." + extension;
        return ($"i|{sizePx}|{request}", request);
    }

    /// <summary>True when the key is cached; <paramref name="image"/> may still be null (the shell had nothing).</summary>
    public bool TryGetCached(string key, out BitmapSource? image)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    /// <summary>Shell thumbnail. <paramref name="version"/> changes when the file changes, so edited files get a fresh image.</summary>
    public Task<BitmapSource?> GetThumbnailAsync(string path, string version, int sizePx, CancellationToken ct)
        => GetAsync(ThumbnailKey(path, version, sizePx), token => _thumbnails.GetThumbnailAsync(path, sizePx, token), ct);

    /// <summary>File-type icon (see <see cref="IconRequest"/>).</summary>
    public Task<BitmapSource?> GetIconAsync(string path, string extension, bool isMissing, int sizePx, CancellationToken ct)
    {
        var (key, request) = IconRequest(path, extension, isMissing, sizePx);
        return GetAsync(key, token => _thumbnails.GetFileIconAsync(request, sizePx, token), ct);
    }

    public async Task<MediaInfo?> GetMediaInfoAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _pending);
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                return await Task.Run(() => _mediaInfo.GetMediaInfo(path), ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task<BitmapSource?> GetAsync(string key, Func<CancellationToken, Task<BitmapSource?>> load, CancellationToken ct)
    {
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        if (FailedRecently(key))
        {
            return null;
        }

        Interlocked.Increment(ref _pending);
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (TryGetCached(key, out cached))
                {
                    return cached;
                }

                // Task.Run guarantees no synchronous shell work runs on the UI thread.
                var image = await Task.Run(() => load(ct), ct);
                if (image is { IsFrozen: false, CanFreeze: true })
                {
                    image.Freeze();
                }

                // Null here is the shell's genuine "no thumbnail": cached like an image.
                Add(key, image);
                return image;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // A broken file must never break the thread. The failure may be transient: don't cache it, retry later.
            if (!ct.IsCancellationRequested)
            {
                RememberFailure(key);
            }

            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private bool FailedRecently(string key)
    {
        lock (_lock)
        {
            if (!_failedUntil.TryGetValue(key, out var until))
            {
                return false;
            }

            if (until > _time.GetUtcNow())
            {
                return true;
            }

            _failedUntil.Remove(key);
            return false;
        }
    }

    private void RememberFailure(string key)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_failedUntil.Count >= MaxRememberedFailures)
            {
                foreach (var expired in _failedUntil.Where(f => f.Value <= now).Select(f => f.Key).ToList())
                {
                    _failedUntil.Remove(expired);
                }

                if (_failedUntil.Count >= MaxRememberedFailures)
                {
                    _failedUntil.Clear();
                }
            }

            _failedUntil[key] = now + RetryAfterFailure;
        }
    }

    private void Add(string key, BitmapSource? image)
    {
        lock (_lock)
        {
            _failedUntil.Remove(key);
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
                _bytes -= existing.Value.Bytes;
            }

            var bytes = image is null ? 0 : (long)image.PixelWidth * image.PixelHeight * 4;
            var node = _lru.AddFirst(new CacheEntry(key, image, bytes));
            _map[key] = node;
            _bytes += bytes;
            while ((_map.Count > CacheCapacity || _bytes > CacheBudgetBytes) && _lru.Last is { } last && last != node)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }
    }

    private readonly record struct CacheEntry(string Key, BitmapSource? Image, long Bytes);
}
