using System.Collections.Frozen;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.Windows.Interop;

namespace Cabinette.Windows;

/// <summary>
/// Explorer's own thumbnails and file-type icons. Shell calls run on a small pool of dedicated STA threads; results are
/// cached in memory (LRU, ~500 entries, including "no thumbnail") and real thumbnails also on disk as PNG in the cache folder
/// (kept in bounds by <see cref="ThumbnailDiskCache.Prune"/>).
/// </summary>
/// <remarks>
/// <see cref="GetFileIconAsync"/> accepts a full path, a file name, or a bare extension (".pdf" or "pdf").
/// Icons of existing .exe/.lnk/.ico/.url files are per file; everything else is cached per extension and size.
/// </remarks>
public sealed class ShellThumbnailService : IThumbnailService, IDisposable
{
    private const int MemoryCacheCapacity = 500;
    private const long MemoryCacheMaxBytes = 192L * 1024 * 1024; // Also bound by pixels in case the UI asks for big sizes.
    private const int MaxSizePx = 1024;

    /// <summary>Types whose icon depends on the file itself rather than on its extension.</summary>
    private static readonly FrozenSet<string> PerFileIconExtensions = new[]
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".cpl", ".msc", ".appref-ms", ".website",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _cacheDirectory;
    private readonly StaWorkerPool _pool;
    private readonly LruCache<string, BitmapSource?> _thumbnails = new(MemoryCacheCapacity, StringComparer.Ordinal, PixelBytes, MemoryCacheMaxBytes);
    private readonly LruCache<string, BitmapSource?> _icons = new(MemoryCacheCapacity, StringComparer.Ordinal, PixelBytes, MemoryCacheMaxBytes / 4);

    public ShellThumbnailService(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _pool = new StaWorkerPool("Cabinette shell images", Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    }

    public async Task<BitmapSource?> GetThumbnailAsync(string path, int sizePx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || sizePx <= 0)
        {
            return null;
        }

        sizePx = Math.Min(sizePx, MaxSizePx);
        ct.ThrowIfCancellationRequested();

        // File system work (stat + disk cache) happens on the thread pool, never on the caller's thread.
        var lookup = await Task.Run(() => LookUpCached(path, sizePx), ct).ConfigureAwait(false);
        if (lookup.Probe is null)
        {
            return null; // Missing file.
        }

        if (lookup.Found)
        {
            return lookup.Bitmap;
        }

        var probe = lookup.Probe;
        var bitmap = await _pool.RunAsync(() => ShellImages.GetThumbnail(probe.FullPath, sizePx), ct).ConfigureAwait(false);
        if (bitmap is null && PdfDocuments.IsPdf(probe.FullPath))
        {
            // Stock Windows has no PDF thumbnail handler; render page 1 with the built-in PDF engine instead.
            bitmap = await PdfDocuments.RenderFirstPageAsync(probe.FullPath, sizePx, ct).ConfigureAwait(false);
        }

        _thumbnails.Set(probe.Key, bitmap);
        if (bitmap is not null)
        {
            // "No thumbnail" is only remembered in memory: it may be transient (file locked, still being written).
            _ = Task.Run(() => SaveToDisk(probe, bitmap), CancellationToken.None);
        }

        return bitmap;
    }

    public async Task<BitmapSource?> GetFileIconAsync(string pathOrExtension, int sizePx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension) || sizePx <= 0)
        {
            return null;
        }

        sizePx = Math.Min(sizePx, 256);
        ct.ThrowIfCancellationRequested();

        var request = IsPathLike(pathOrExtension)
            ? await Task.Run(() => ResolveIconRequest(pathOrExtension), ct).ConfigureAwait(false)
            : IconRequest.ForExtension(NormalizeExtension(pathOrExtension), isFolder: false);

        var key = $"{request.Key}|{sizePx}";
        if (_icons.TryGet(key, out var cached))
        {
            return cached;
        }

        var bitmap = await _pool.RunAsync(
            () => request.ExistingPath is not null
                ? ShellImages.GetIcon(request.ExistingPath, sizePx) ?? ShellImages.GetIconForExtension(request.Extension, request.IsFolder, sizePx)
                : ShellImages.GetIconForExtension(request.Extension, request.IsFolder, sizePx),
            ct).ConfigureAwait(false);
        _icons.Set(key, bitmap);
        return bitmap;
    }

    public void Dispose() => _pool.Dispose();

    /// <summary>Visible for tests: where the PNG for a given file and size is cached.</summary>
    internal string? GetDiskCachePath(string path, int sizePx)
    {
        var probe = Probe(path, Math.Min(sizePx, MaxSizePx));
        return probe is null ? null : DiskPath(probe.Key, ".png");
    }

    private CacheLookup LookUpCached(string path, int sizePx)
    {
        var probe = Probe(path, sizePx);
        if (probe is null)
        {
            return new CacheLookup(null, false, null);
        }

        if (_thumbnails.TryGet(probe.Key, out var cached))
        {
            return new CacheLookup(probe, true, cached);
        }

        if (TryLoadFromDisk(probe.Key, out var fromDisk))
        {
            _thumbnails.Set(probe.Key, fromDisk);
            return new CacheLookup(probe, true, fromDisk);
        }

        return new CacheLookup(probe, false, null);
    }

    private static FileProbe? Probe(string path, int sizePx)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            long length;
            DateTime lastWrite;
            var file = new FileInfo(fullPath);
            if (file.Exists)
            {
                length = file.Length;
                lastWrite = file.LastWriteTimeUtc;
            }
            else if (Directory.Exists(fullPath))
            {
                length = 0;
                lastWrite = Directory.GetLastWriteTimeUtc(fullPath);
            }
            else
            {
                return null;
            }

            var key = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{fullPath.ToUpperInvariant()}|{length}|{lastWrite.Ticks}|{sizePx}");
            return new FileProbe(fullPath, key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool TryLoadFromDisk(string key, out BitmapSource? bitmap)
    {
        bitmap = null;
        var png = DiskPath(key, ".png");
        if (!File.Exists(png))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(png);
            using var stream = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            // A decoder-backed frame from this pool thread renders blank on the UI thread; copy it into a plain bitmap.
            bitmap = PlainBitmap.From(decoder.Frames[0]);
            ThumbnailDiskCache.MarkUsed(png);
            return true;
        }
        catch (Exception)
        {
            // Corrupt or half-written cache file: drop it and regenerate.
            TryDelete(png);
            return false;
        }
    }

    private void SaveToDisk(FileProbe probe, BitmapSource bitmap)
    {
        var png = DiskPath(probe.Key, ".png");
        var temp = $"{png}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(temp))
            {
                encoder.Save(stream);
            }

            File.Move(temp, png, overwrite: true);
        }
        catch (Exception)
        {
            // The disk cache is best effort.
            TryDelete(temp);
        }
    }

    private string DiskPath(string key, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, hash[..2], hash + extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Ignore.
        }
    }

    private static long PixelBytes(BitmapSource? bitmap) => bitmap is null ? 64 : (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

    private static bool IsPathLike(string value)
        => value.Contains('\\') || value.Contains('/') || value.Contains(':');

    /// <summary>".PDF" / "pdf" → ".pdf"; "report.pdf" → ".pdf"; "" when there is no extension.</summary>
    private static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('.'))
        {
            return trimmed.ToLowerInvariant();
        }

        var ext = Path.GetExtension(trimmed);
        return string.IsNullOrEmpty(ext) ? "." + trimmed.ToLowerInvariant() : ext.ToLowerInvariant();
    }

    private static IconRequest ResolveIconRequest(string path)
    {
        string extension;
        try
        {
            extension = Path.GetExtension(path).ToLowerInvariant();
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                // Folders may have custom icons (desktop.ini), so they are per path.
                return new IconRequest("dir:" + fullPath.ToUpperInvariant(), "", true, fullPath);
            }

            if (PerFileIconExtensions.Contains(extension) && File.Exists(fullPath))
            {
                return new IconRequest("file:" + fullPath.ToUpperInvariant(), extension, false, fullPath);
            }
        }
        catch (Exception)
        {
            extension = "";
        }

        return IconRequest.ForExtension(extension, isFolder: false);
    }

    private sealed record FileProbe(string FullPath, string Key);

    private readonly record struct CacheLookup(FileProbe? Probe, bool Found, BitmapSource? Bitmap);

    private sealed record IconRequest(string Key, string Extension, bool IsFolder, string? ExistingPath)
    {
        public static IconRequest ForExtension(string extension, bool isFolder)
            => new("ext:" + extension, extension, isFolder, null);
    }
}
