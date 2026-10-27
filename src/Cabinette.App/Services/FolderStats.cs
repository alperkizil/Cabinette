using System.Diagnostics;
using System.IO;

namespace Cabinette.App.Services;

/// <summary>File count and total size of a folder tree.</summary>
public sealed record FolderStats(int Files, long Bytes)
{
    /// <summary>
    /// Counts files below <paramref name="folder"/>, giving up (null) after <paramref name="budget"/> so a huge vault does
    /// not hold up a confirmation dialog. Inaccessible folders are skipped. Call off the UI thread.
    /// </summary>
    public static FolderStats? Measure(string folder, TimeSpan budget, CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return new FolderStats(0, 0);
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var clock = Stopwatch.StartNew();
            var files = 0;
            long bytes = 0;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                files++;
                bytes += file.Length;
                if ((files & 255) == 0 && (clock.Elapsed > budget || ct.IsCancellationRequested))
                {
                    return null;
                }
            }

            return new FolderStats(files, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn($"Measuring '{folder}' failed.", ex);
            return null;
        }
    }

    /// <summary>Free space on the drive that holds <paramref name="path"/>, or null when unknown.</summary>
    public static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        try
        {
            return Norm(path).StartsWith(Norm(folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
