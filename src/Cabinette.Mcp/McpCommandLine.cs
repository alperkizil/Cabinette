using System.Reflection;
using Cabinette.Ipc;

namespace Cabinette.Mcp;

/// <summary>Facts about this build of cabinette-mcp.exe.</summary>
internal static class BuildInfo
{
    /// <summary>
    /// A Debug build only ever talks to a dev run of Cabinette: it refuses to start without <c>--data-dir</c>, or with
    /// the real data folder as <c>--data-dir</c> (SPEC.md section 10, "a dev build can't reach your real library").
    /// </summary>
    public static bool RequiresDataDirectory
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>The product version ("0.1.0"), without the source revision the SDK appends.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(BuildInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

/// <summary>Where Cabinette keeps its data. Must equal the app's <c>AppPaths.DefaultDataDirectory</c> (a test pins the two).</summary>
internal static class McpPaths
{
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cabinette");

    /// <summary>The folder this program runs from (next to which the installer puts Cabinette.exe).</summary>
    public static string ProgramDirectory => AppContext.BaseDirectory;

    public const string CabinetteExeName = "Cabinette.exe";
}

/// <summary>What the command line asked for.</summary>
/// <param name="DataDirectory">Full path of Cabinette's data folder: it picks the pipe to talk to.</param>
/// <param name="IsDevDataDirectory">
/// True when <c>--data-dir</c> named a folder other than the real one (Cabinette is then started with it too).
/// </param>
/// <param name="AppPath">Full path of the Cabinette.exe to start when it isn't running.</param>
internal sealed record McpOptions(string DataDirectory, bool IsDevDataDirectory, string AppPath);

/// <summary>The outcome of parsing: options to run with, or an exit code and a message for stderr.</summary>
internal sealed record McpParseResult(McpOptions? Options, int ExitCode, string? Message)
{
    public static McpParseResult Run(McpOptions options) => new(options, 0, null);

    public static McpParseResult Exit(int exitCode, string message) => new(null, exitCode, message);
}

/// <summary>
/// Options of cabinette-mcp.exe: <c>--data-dir &lt;dir&gt;</c> (a dev run's data folder) and <c>--app &lt;Cabinette.exe&gt;</c>
/// (which Cabinette to start, for dev). Anything else is an error (exit code 2), and so is a Debug build without
/// <c>--data-dir</c>. Both values must be full paths: the AI client starts this program in the open project's folder,
/// so a relative path would pick a different Cabinette.exe (perhaps one a cloned repository carries) or data folder per
/// project. Cabinette.exe is never looked for in the current folder or on PATH.
/// </summary>
internal static class McpCommandLine
{
    /// <summary>Exit code for a wrong command line.</summary>
    public const int UsageError = 2;

    public const string DataDirOption = "--data-dir";
    public const string AppOption = "--app";

    public static string Usage =>
        """
        Usage: cabinette-mcp.exe [--data-dir <folder>] [--app <path to Cabinette.exe>]

        The MCP server of Cabinette, for Claude Code and Codex (they start it and talk to it over stdin/stdout).
          --data-dir <folder>  Talk to the dev run of Cabinette that uses this data folder. Default: the real data folder,
                               %LocalAppData%\Cabinette (naming it is the same as leaving --data-dir out).
          --app <exe>          Start this Cabinette.exe when Cabinette isn't running. Default: Cabinette.exe next to this program.
        Both take a full path (C:\...), never a relative one.
        A Debug build requires --data-dir and refuses the real data folder, so it can't reach the real library.
        """;

    /// <param name="requireDataDirectory">
    /// <see cref="BuildInfo.RequiresDataDirectory"/> outside tests: true requires <c>--data-dir</c> and refuses the real data folder.
    /// </param>
    public static McpParseResult Parse(IReadOnlyList<string> args, bool requireDataDirectory)
    {
        string? dataDirectory = null;
        string? appPath = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    return McpParseResult.Exit(0, Usage);
                case DataDirOption or AppOption:
                {
                    // A value starting with "--" is the next option, not a path: "--data-dir --app x" lacks a value.
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].TrimStart().StartsWith("--", StringComparison.Ordinal))
                    {
                        return McpParseResult.Exit(UsageError, $"cabinette-mcp: {arg} needs a value.\n\n{Usage}");
                    }

                    var value = args[++i];
                    if ((arg == DataDirOption ? dataDirectory : appPath) is not null)
                    {
                        return McpParseResult.Exit(UsageError, $"cabinette-mcp: {arg} is given twice.\n\n{Usage}");
                    }

                    var unquoted = value.Trim().Trim('"').Trim();
                    if (unquoted.Length > 0 && !Path.IsPathFullyQualified(unquoted))
                    {
                        return McpParseResult.Exit(
                            UsageError,
                            $"cabinette-mcp: {arg} needs a full path (like C:\\...), not \"{value}\". A relative path would depend on " +
                            "the folder the AI client starts cabinette-mcp.exe in, which is the open project's folder.");
                    }

                    if (!TryResolve(unquoted, out var full))
                    {
                        return McpParseResult.Exit(UsageError, $"cabinette-mcp: {arg} is not a valid path: \"{value}\".");
                    }

                    if (arg == DataDirOption)
                    {
                        dataDirectory = full;
                    }
                    else
                    {
                        appPath = full;
                    }

                    break;
                }

                default:
                    return McpParseResult.Exit(UsageError, $"cabinette-mcp: unknown argument \"{arg}\".\n\n{Usage}");
            }
        }

        // The real data folder in any spelling (case, a trailing "\" or "\.") hashes to the real Cabinette's pipe, and
        // passing it on would start Cabinette as a "dev run" on the real library. A Debug build refuses it; for a Release
        // build it means the same as no --data-dir.
        if (dataDirectory is not null && IsDefaultDataDirectory(dataDirectory))
        {
            if (requireDataDirectory)
            {
                return McpParseResult.Exit(
                    UsageError,
                    $"cabinette-mcp: --data-dir \"{dataDirectory}\" is the real data folder, and a debug build must not use the real data folder. " +
                    "Give it the data folder of a dev run of Cabinette.");
            }

            dataDirectory = null;
        }

        if (dataDirectory is null && requireDataDirectory)
        {
            return McpParseResult.Exit(
                UsageError,
                "cabinette-mcp: this is a Debug build, so it needs --data-dir <folder> of a dev run of Cabinette. " +
                "It never talks to the Cabinette that uses your real library (only a Release build does).");
        }

        return McpParseResult.Run(new McpOptions(
            dataDirectory ?? McpPaths.DefaultDataDirectory,
            IsDevDataDirectory: dataDirectory is not null,
            appPath ?? Path.Combine(McpPaths.ProgramDirectory, McpPaths.CabinetteExeName)));
    }

    /// <summary>True when the folder is <see cref="McpPaths.DefaultDataDirectory"/>, compared as the pipe name compares them.</summary>
    private static bool IsDefaultDataDirectory(string fullPath)
        => string.Equals(
            InstanceNames.NormalizeDirectory(fullPath),
            InstanceNames.NormalizeDirectory(McpPaths.DefaultDataDirectory),
            StringComparison.Ordinal);

    /// <summary>The fully qualified path, normalized (<c>..</c> resolved), without a trailing separator.</summary>
    private static bool TryResolve(string fullyQualified, out string fullPath)
    {
        fullPath = "";
        if (fullyQualified.Length == 0)
        {
            return false;
        }

        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullyQualified));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
