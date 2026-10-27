using System.IO;
using System.Text;

namespace Cabinette.App.Services.Ai;

/// <summary>A dev run (<c>--data-dir</c>): its data folder and the Cabinette.exe that is running, both full paths.</summary>
public sealed record AiDevRun(string DataDirectory, string AppPath);

/// <summary>Where cabinette-mcp.exe is (or would be), and whether it is there.</summary>
public sealed record AiExeLocation(string Path, bool Exists);

/// <summary>
/// The commands that register cabinette-mcp.exe with Claude Code and Codex (Settings › AI, SPEC.md section 10), and the
/// ones that remove it. A dev run (<c>--data-dir</c>) registers under <see cref="DevServerName"/>, with Claude Code's
/// local scope (only the folder the command runs in), and passes its data folder and its Cabinette.exe on as full paths,
/// as the README's developer section does: a dev cabinette-mcp.exe only starts a Cabinette.exe next to itself or the one
/// given by <c>--app</c>, and refuses relative paths. So it never replaces the real registration and only ever reaches
/// that dev run.
/// </summary>
/// <param name="ServerName">"cabinette", or "cabinette-dev" for a dev run.</param>
/// <param name="ExePath">The full path of cabinette-mcp.exe.</param>
public sealed record AiConnectCommands(string ServerName, string ExePath, string ClaudeAdd, string CodexAdd, string ClaudeRemove, string CodexRemove)
{
    public const string ExeName = "cabinette-mcp.exe";
    public const string DefaultServerName = "cabinette";
    public const string DevServerName = "cabinette-dev";

    /// <summary>The solution file that marks a repository root, for finding a dev build of cabinette-mcp.exe.</summary>
    public const string SolutionFileName = "Cabinette.slnx";

    /// <summary>Where cabinette-mcp.exe belongs: next to Cabinette.exe (the installer puts it there).</summary>
    public static string DefaultExePath => Path.Combine(AppContext.BaseDirectory, ExeName);

    /// <summary>The running Cabinette.exe (a dev run's <c>--app</c>).</summary>
    public static string RunningAppPath => Environment.ProcessPath is { } process && string.Equals(Path.GetFileName(process), "Cabinette.exe", StringComparison.OrdinalIgnoreCase)
        ? process
        : Path.Combine(AppContext.BaseDirectory, "Cabinette.exe");

    /// <summary>Where a repository's own build puts cabinette-mcp.exe, the Debug build first.</summary>
    public static IReadOnlyList<string> DevBuildPaths(string repositoryRoot)
        => [.. new[] { "Debug", "Release" }.Select(configuration => Path.Combine(repositoryRoot, "src", "Cabinette.Mcp", "bin", configuration, "net10.0-windows", ExeName))];

    /// <param name="exePath">Full path of cabinette-mcp.exe.</param>
    /// <param name="dev">A dev run, else null.</param>
    public static AiConnectCommands Build(string exePath, AiDevRun? dev)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var name = dev is null ? DefaultServerName : DevServerName;
        var scope = dev is null ? "user" : "local";
        var server = Quote(exePath);
        if (dev is not null)
        {
            server += " --data-dir " + Quote(Path.GetFullPath(dev.DataDirectory)) + " --app " + Quote(Path.GetFullPath(dev.AppPath));
        }

        return new AiConnectCommands(
            name,
            exePath,
            $"claude mcp add --scope {scope} {name} -- {server}",
            $"codex mcp add {name} -- {server}",
            $"claude mcp remove {name} --scope {scope}",
            $"codex mcp remove {name}");
    }

    /// <summary>
    /// Finds cabinette-mcp.exe on disk (call it off the UI thread). First next to Cabinette.exe, where the installer puts
    /// it. For a dev run (<paramref name="devSearchStart"/> set, normally the running Cabinette.exe's folder) without one
    /// there: the repository's own build, found by walking up to the folder with Cabinette.slnx and trying
    /// <see cref="DevBuildPaths"/>, the newest first. When there is none, the Debug build's path inside the repository
    /// (where <c>dotnet build</c> puts it), else <paramref name="nextToApp"/>, as missing.
    /// </summary>
    public static AiExeLocation Locate(string nextToApp, string? devSearchStart)
    {
        if (File.Exists(nextToApp) || devSearchStart is null)
        {
            return new AiExeLocation(nextToApp, File.Exists(nextToApp));
        }

        for (var folder = new DirectoryInfo(Path.GetFullPath(devSearchStart)); folder is not null; folder = folder.Parent)
        {
            if (!File.Exists(Path.Combine(folder.FullName, SolutionFileName)))
            {
                continue;
            }

            var builds = DevBuildPaths(folder.FullName);
            var newest = builds.Select(path => new FileInfo(path)).Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
            return newest is not null ? new AiExeLocation(newest.FullName, true) : new AiExeLocation(builds[0], false);
        }

        return new AiExeLocation(nextToApp, false);
    }

    /// <summary>
    /// A path in double quotes, for PowerShell (Windows' default terminal shell, named in Settings › AI). Inside double
    /// quotes PowerShell expands <c>$name</c> and <c>$(…)</c>, treats the backtick as its escape character and takes the
    /// typographic quotes “ ” „ as double quotes, so each of those gets a backtick in front and reads back unchanged.
    /// No single text can serve cmd as well (it would keep the backticks), but a path without <c>$</c> or a backtick reads
    /// back unchanged in cmd too. A trailing backslash would escape the closing quote for programs that parse their
    /// command line the Windows way ("C:\dir\" reads as C:\dir"), so it is dropped, and a drive root gets a "." instead
    /// ("D:\." is D:\).
    /// </summary>
    public static string Quote(string path)
    {
        var trimmed = path.Trim();
        var withoutSeparator = Path.TrimEndingDirectorySeparator(trimmed);
        if (withoutSeparator.EndsWith('\\') || withoutSeparator.EndsWith('/'))
        {
            // A drive root (C:\) keeps its separator: point at it with ".". (A share root loses it: \\server\share.)
            withoutSeparator += ".";
        }

        var quoted = new StringBuilder(withoutSeparator.Length + 8).Append('"');
        foreach (var c in withoutSeparator)
        {
            if (c is '`' or '$' or '"' or '\u201C' or '\u201D' or '\u201E')
            {
                quoted.Append('`');
            }

            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }
}
