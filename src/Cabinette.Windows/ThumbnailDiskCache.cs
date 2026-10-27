using System.IO;

namespace Cabinette.Windows;

/// <param name="MaxBytes">When the cache is bigger than this, the least recently used thumbnails are removed...</param>
/// <param name="TargetBytes">...until it is at most this big (some headroom, so the next prune is not right away).</param>
/// <param name="MaxUnused">Thumbnails not used for this long are removed whatever the size (e.g. files that changed or left the library).</param>
public sealed record ThumbnailCachePruneOptions(long MaxBytes, long TargetBytes, TimeSpan MaxUnused)
{
    /// <summary>500 MB cap, pruned down to 400 MB; thumbnails unused for 90 days are removed.</summary>
    public static ThumbnailCachePruneOptions Default { get; } = new(500L * 1024 * 1024, 400L * 1024 * 1024, TimeSpan.FromDays(90));

    /// <summary>Half-written files older than this are leftovers of a crash.</summary>
    public TimeSpan StaleTempAge { get; init; } = TimeSpan.FromHours(1);
}

public sealed record ThumbnailCachePruneResult(int FilesKept, long BytesKept, int FilesDeleted, long BytesDeleted);

/// <summary>
/// Housekeeping for the PNG thumbnails <see cref="ShellThumbnailService"/> keeps on disk ("&lt;cache&gt;\ab\ab12….png").
/// A file's last-write time is its "last used" time: it is set when the thumbnail is saved and refreshed (at most once a day)
/// when it is read back. Cache entries can't be traced back to library items (their names are hashes of path, size and
/// date), so pruning is by age and size only. Only files that look like cache entries are ever touched.
/// </summary>
public static class ThumbnailDiskCache
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromDays(1);

    /// <summary>Records that a cached thumbnail was used (cheap: a timestamp update at most once a day).</summary>
    public static void MarkUsed(string pngPath)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(pngPath) > TouchInterval)
            {
                File.SetLastWriteTimeUtc(pngPath, now);
            }
        }
        catch (Exception)
        {
            // Best effort: at worst the thumbnail is pruned a little early and regenerated.
        }
    }

    /// <summary>
    /// Removes stale temp files, thumbnails unused for <see cref="ThumbnailCachePruneOptions.MaxUnused"/>, then the least
    /// recently used ones while the cache is over <see cref="ThumbnailCachePruneOptions.MaxBytes"/>. Slow on a big cache:
    /// call it on a background thread. Never throws (files in use are skipped).
    /// </summary>
    public static ThumbnailCachePruneResult Prune(string directory, ThumbnailCachePruneOptions options, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = nowUtc ?? DateTime.UtcNow;
        var deleted = 0;
        long deletedBytes = 0;
        var entries = new List<FileInfo>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return new ThumbnailCachePruneResult(0, 0, 0, 0);
            }

            foreach (var folder in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if (!IsHexName(folder.Name, 2) || (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in folder.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsCacheEntry(file.Name))
                    {
                        if (now - file.LastWriteTimeUtc > options.MaxUnused)
                        {
                            Delete(file, ref deleted, ref deletedBytes);
                        }
                        else
                        {
                            entries.Add(file);
                        }
                    }
                    else if (IsTempFile(file.Name) && now - file.LastWriteTimeUtc > options.StaleTempAge)
                    {
                        Delete(file, ref deleted, ref deletedBytes);
                    }
                }
            }

            var total = entries.Sum(f => f.Length);
            if (total > options.MaxBytes)
            {
                foreach (var file in entries.OrderBy(f => f.LastWriteTimeUtc).ToList())
                {
                    if (total <= options.TargetBytes)
                    {
                        break;
                    }

                    ct.ThrowIfCancellationRequested();
                    var length = file.Length;
                    if (Delete(file, ref deleted, ref deletedBytes))
                    {
                        total -= length;
                        entries.Remove(file);
                    }
                }
            }

            return new ThumbnailCachePruneResult(entries.Count, total, deleted, deletedBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
        {
            return new ThumbnailCachePruneResult(entries.Count, entries.Sum(SafeLength), deleted, deletedBytes);
        }
    }

    /// <summary>"0123…cdef.png": 32 hex characters (see ShellThumbnailService.DiskPath).</summary>
    private static bool IsCacheEntry(string name)
        => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && IsHexName(name[..^4], 32);

    /// <summary>"0123…cdef.png.&lt;guid&gt;.tmp", written before the move into place.</summary>
    private static bool IsTempFile(string name)
        => name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && name.Length > 36 && IsHexName(name[..32], 32) &&
           name.AsSpan(32).StartsWith(".png.", StringComparison.OrdinalIgnoreCase);

    private static bool IsHexName(string name, int length) => name.Length == length && name.All(char.IsAsciiHexDigit);

    private static bool Delete(FileInfo file, ref int deleted, ref long deletedBytes)
    {
        try
        {
            var length = file.Length;
            file.Delete();
            deleted++;
            deletedBytes += length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // In use (being read or written right now): next time.
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
