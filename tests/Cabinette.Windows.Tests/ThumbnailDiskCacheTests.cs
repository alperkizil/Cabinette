using System.IO;

namespace Cabinette.Windows.Tests;

/// <summary>Pruning of the on-disk thumbnail cache, on a temp folder laid out like the real one.</summary>
public sealed class ThumbnailDiskCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RemovesLeastRecentlyUsedUntilUnderTarget()
    {
        using var temp = new TempFolder();
        var oldest = Entry(temp, 1, 100, Now.AddDays(-5));
        var middle = Entry(temp, 2, 100, Now.AddDays(-3));
        var newest = Entry(temp, 3, 100, Now.AddDays(-1));

        var result = ThumbnailDiskCache.Prune(temp.Path, new ThumbnailCachePruneOptions(250, 150, TimeSpan.FromDays(90)), Now);

        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.Equal(new ThumbnailCachePruneResult(1, 100, 2, 200), result);
    }

    [Fact]
    public void UnderTheCapOnlyOldEntriesGo()
    {
        using var temp = new TempFolder();
        var unused = Entry(temp, 1, 10, Now.AddDays(-91));
        var recent = Entry(temp, 2, 10, Now.AddDays(-89));

        var result = ThumbnailDiskCache.Prune(temp.Path, ThumbnailCachePruneOptions.Default, Now);

        Assert.False(File.Exists(unused));
        Assert.True(File.Exists(recent));
        Assert.Equal(1, result.FilesDeleted);
    }

    [Fact]
    public void StaleTempFilesGoAndForeignFilesStay()
    {
        using var temp = new TempFolder();
        var entry = Entry(temp, 1, 10, Now);
        var staleTemp = Write(temp, Path.Combine("01", entry[^36..^4] + ".png." + Guid.NewGuid().ToString("N") + ".tmp"), 10, Now.AddHours(-2));
        var freshTemp = Write(temp, Path.Combine("01", entry[^36..^4] + ".png." + Guid.NewGuid().ToString("N") + ".tmp"), 10, Now.AddMinutes(-5));
        var foreignInCache = Write(temp, Path.Combine("01", "notes.txt"), 10, Now.AddYears(-3));
        var foreignFolder = Write(temp, Path.Combine("my stuff", Hex(9) + ".png"), 10, Now.AddYears(-3));
        var foreignRoot = Write(temp, "readme.png", 10, Now.AddYears(-3));

        ThumbnailDiskCache.Prune(temp.Path, new ThumbnailCachePruneOptions(0, 0, TimeSpan.FromDays(1)), Now);

        Assert.False(File.Exists(entry)); // Over a zero cap.
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(freshTemp));
        Assert.True(File.Exists(foreignInCache));
        Assert.True(File.Exists(foreignFolder));
        Assert.True(File.Exists(foreignRoot));
    }

    [Fact]
    public void MissingFolderIsFine()
    {
        var result = ThumbnailDiskCache.Prune(Path.Combine(Path.GetTempPath(), "Cabinette.Windows.Tests", Guid.NewGuid().ToString("N")), ThumbnailCachePruneOptions.Default);
        Assert.Equal(new ThumbnailCachePruneResult(0, 0, 0, 0), result);
    }

    [Fact]
    public void MarkUsedRefreshesOnlyOldTimestamps()
    {
        using var temp = new TempFolder();
        var old = Entry(temp, 1, 10, DateTime.UtcNow.AddDays(-10));
        var fresh = Entry(temp, 2, 10, DateTime.UtcNow.AddHours(-2));
        var freshTime = File.GetLastWriteTimeUtc(fresh);

        ThumbnailDiskCache.MarkUsed(old);
        ThumbnailDiskCache.MarkUsed(fresh);
        ThumbnailDiskCache.MarkUsed(Path.Combine(temp.Path, "missing.png"));

        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(old) < TimeSpan.FromMinutes(5));
        Assert.Equal(freshTime, File.GetLastWriteTimeUtc(fresh));
    }

    /// <summary>A cache entry like ShellThumbnailService writes: "&lt;cache&gt;\0n\0n….png" (32 hex characters).</summary>
    private static string Entry(TempFolder temp, int n, int bytes, DateTime lastUsedUtc)
    {
        var name = Hex(n);
        return Write(temp, Path.Combine(name[..2], name + ".png"), bytes, lastUsedUtc);
    }

    private static string Hex(int n) => n.ToString("x2").PadRight(32, '0')[..2] + n.ToString("x30");

    private static string Write(TempFolder temp, string relative, int bytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(temp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
