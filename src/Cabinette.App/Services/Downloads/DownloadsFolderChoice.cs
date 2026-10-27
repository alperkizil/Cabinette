using System.IO;
using Cabinette.Windows;

namespace Cabinette.App.Services.Downloads;

/// <summary>
/// Which folder Clean My Downloads mode watches in this run (SPEC.md section 5: dev runs never watch the real Downloads folder).
/// <list type="bullet">
/// <item>A normal run: the Windows Downloads folder, wherever it was moved.</item>
/// <item><c>--sample-data</c> and <c>--screenshot</c> runs: none.</item>
/// <item><c>--data-dir</c> runs: none, unless <c>--downloads-dir &lt;folder&gt;</c> names one (never the real Downloads folder itself).</item>
/// <item><c>--downloads-dir</c> without <c>--data-dir</c>: none (that copy uses the real library, so the flag doesn't count).</item>
/// </list>
/// </summary>
public static class DownloadsFolderChoice
{
    /// <param name="windowsDownloads">The real Downloads folder (only asked when it is needed).</param>
    /// <returns>The folder, or null when the mode doesn't run in this copy.</returns>
    public static string? Resolve(CommandLineOptions options, Func<string?> windowsDownloads)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(windowsDownloads);
        if (options.IsSandboxed)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.DownloadsDirectory))
        {
            if (options.DataDirectory is null || TryFullPath(options.DownloadsDirectory) is not { } devFolder)
            {
                return null;
            }

            // A dev run on the real Downloads folder is exactly what this flag must not allow, also when it is spelled another
            // way: through a junction or symbolic link, an 8.3 short name or a subst drive.
            return IsSameFolder(devFolder, windowsDownloads()) ? null : devFolder;
        }

        return options.IsDevRun ? null : windowsDownloads();
    }

    /// <summary>
    /// True when both paths name the same folder: as written, or once Windows resolved them to their final paths (junctions,
    /// symbolic links, short names, subst and mapped drives).
    /// </summary>
    internal static bool IsSameFolder(string? a, string? b)
    {
        if (PathsEqual(a, b))
        {
            return true;
        }

        return a is not null && b is not null && FinalPaths.TryResolve(a) is { } finalA && FinalPaths.TryResolve(b) is { } finalB
               && string.Equals(finalA, finalB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When <see cref="Resolve"/> found no folder: true if that is because of developer flags, false if Windows didn't say
    /// where the Downloads folder is (for the notice when the mode is switched on).
    /// </summary>
    public static bool IsDeveloperRun(CommandLineOptions options)
        => options.IsDevRun || !string.IsNullOrWhiteSpace(options.DownloadsDirectory);

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> itself or anywhere below it: as written, or once Windows
    /// resolved both to their final paths (see <see cref="IsSameFolder"/>).
    /// </summary>
    internal static bool IsSameOrInside(string path, string folder)
    {
        if (TryFullPath(path) is not { } fullPath || TryFullPath(folder) is not { } fullFolder)
        {
            return false;
        }

        return IsSameOrInsideFull(fullPath, fullFolder)
               || (FinalPaths.TryResolve(fullPath) is { } finalPath && FinalPaths.TryResolve(fullFolder) is { } finalFolder
                   && IsSameOrInsideFull(finalPath, finalFolder));
    }

    private static bool IsSameOrInsideFull(string fullPath, string fullFolder)
        => string.Equals(fullPath, fullFolder, StringComparison.OrdinalIgnoreCase)
           || fullPath.StartsWith(Path.EndsInDirectorySeparator(fullFolder) ? fullFolder : fullFolder + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    internal static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null || TryFullPath(a) is not { } fullA || TryFullPath(b) is not { } fullB)
        {
            return false;
        }

        return string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
