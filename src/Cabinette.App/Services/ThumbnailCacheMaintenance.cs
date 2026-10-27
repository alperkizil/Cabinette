using Cabinette.Windows;

namespace Cabinette.App.Services;

/// <summary>Keeps the on-disk thumbnail cache (%LocalAppData%\Cabinette\thumbs) in bounds; see <see cref="ThumbnailDiskCache.Prune"/>.</summary>
public static class ThumbnailCacheMaintenance
{
    /// <summary>Long enough that startup work (chat list, first thread, vault rescan) is done.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Prunes once, a while after startup, on a low-priority background thread. Never throws.</summary>
    public static void ScheduleStartupPrune(string cacheDirectory)
    {
        _ = Task.Delay(StartupDelay).ContinueWith(
            _ =>
            {
                var thread = new Thread(() => Prune(cacheDirectory))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                    Name = "Cabinette thumbnail cache cleanup",
                };
                thread.Start();
            },
            TaskScheduler.Default);
    }

    private static void Prune(string cacheDirectory)
    {
        try
        {
            var result = ThumbnailDiskCache.Prune(cacheDirectory, ThumbnailCachePruneOptions.Default);
            if (result.FilesDeleted > 0)
            {
                AppLog.Info($"Thumbnail cache: removed {result.FilesDeleted} files ({result.BytesDeleted / (1024 * 1024)} MB), kept {result.FilesKept} ({result.BytesKept / (1024 * 1024)} MB).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Pruning the thumbnail cache failed.", ex);
        }
    }
}
