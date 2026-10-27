using Cabinette.Core.Models;

namespace Cabinette.App.Services;

/// <summary>
/// <c>--tray</c> starts quietly in the tray without showing the window (used by "Start with Windows").
/// <c>-Embedding</c> (or <c>/Embedding</c>) is added by COM when it starts Cabinette for an Explorer entry: start quietly in
/// the tray too, and serve the entry's drop.
/// Developer flags:
/// <c>--sample-data</c> (in-memory fake library), <c>--sample-icons</c> (dev icon generator instead of the real one),
/// <c>--theme light|dark</c>, <c>--text-size small|medium|large|xlarge</c>, <c>--bold-text on|off</c>,
/// <c>--font segoe-variable|segoe|inter|source-sans|atkinson|lexend|bahnschrift|sitka|cascadia|jetbrains</c>, <c>--data-dir &lt;path&gt;</c>,
/// <c>--screenshot &lt;file.png&gt;</c> (render the window off-screen, save, exit) and <c>--screenshot-state &lt;name&gt;</c>.
/// <c>--downloads-dir &lt;folder&gt;</c> (only together with <c>--data-dir</c>): the folder Clean My Downloads mode watches in
/// that run instead of the real Downloads folder, which dev runs never watch.
/// Hidden mode: <c>--extract-text &lt;file&gt; --max-chars &lt;n&gt;</c> runs the file's IFilter, writes its text to standard
/// output and exits (the read_file tool runs Windows' text extractors in this child process, never in Cabinette's own).
/// </summary>
public sealed record CommandLineOptions
{
    public const string TrayFlag = "--tray";

    /// <summary>COM's flag for a local server it starts (it appends <c> -Embedding</c> to the LocalServer32 command).</summary>
    public const string EmbeddingFlag = "-Embedding";

    /// <summary><c>--tray</c>: start without showing the main window.</summary>
    public bool StartInTray { get; init; }

    /// <summary><c>-Embedding</c> / <c>/Embedding</c>: COM started this process to serve an Explorer entry's drop target.</summary>
    public bool IsEmbedding { get; init; }

    /// <summary>Start without showing the main window: <c>--tray</c>, or started by COM for an Explorer entry.</summary>
    public bool StartQuietly => StartInTray || IsEmbedding;

    public bool SampleData { get; init; }

    public bool SampleIcons { get; init; }

    public ThemePreference? ThemeOverride { get; init; }

    /// <summary>Dev flag <c>--text-size small|medium|large|xlarge</c>.</summary>
    public TextSize? TextSizeOverride { get; init; }

    /// <summary>Dev flag <c>--bold-text on|off</c>.</summary>
    public bool? BoldTextOverride { get; init; }

    /// <summary>Dev flag <c>--font</c> with a name from <see cref="FontNames"/>.</summary>
    public AppFont? FontOverride { get; init; }

    /// <summary>The names <c>--font</c> takes.</summary>
    public static IReadOnlyDictionary<string, AppFont> FontNames { get; } = new Dictionary<string, AppFont>(StringComparer.OrdinalIgnoreCase)
    {
        ["segoe-variable"] = AppFont.SegoeUIVariable,
        ["segoe"] = AppFont.SegoeUI,
        ["inter"] = AppFont.Inter,
        ["source-sans"] = AppFont.SourceSans3,
        ["atkinson"] = AppFont.AtkinsonHyperlegibleNext,
        ["lexend"] = AppFont.Lexend,
        ["bahnschrift"] = AppFont.Bahnschrift,
        ["sitka"] = AppFont.Sitka,
        ["cascadia"] = AppFont.CascadiaMono,
        ["jetbrains"] = AppFont.JetBrainsMono,
    };

    public string? DataDirectory { get; init; }

    /// <summary>Dev flag <c>--downloads-dir &lt;folder&gt;</c>: see <see cref="Downloads.DownloadsFolderChoice"/>.</summary>
    public string? DownloadsDirectory { get; init; }

    public string? ScreenshotPath { get; init; }

    public string ScreenshotState { get; init; } = "main";

    public bool IsScreenshotMode => ScreenshotPath is not null;

    /// <summary>Hidden mode <c>--extract-text &lt;file&gt;</c>: see <see cref="Ai.TextExtractionChild"/>.</summary>
    public string? ExtractTextPath { get; init; }

    /// <summary><c>--max-chars &lt;n&gt;</c> for <see cref="ExtractTextPath"/>; null when missing or not a number.</summary>
    public int? ExtractMaxChars { get; init; }

    /// <summary>
    /// A text-extraction child, which does nothing else (no single instance, log, window or data folder). Any argument
    /// that starts like one of its flags makes it one, also a malformed one (<c>--extract-text=x</c>, a missing file or
    /// count): such a command line ends with an error code, never in a normal start.
    /// </summary>
    public bool IsTextExtraction { get; init; }

    /// <summary>
    /// Any developer run (<c>--data-dir</c>, <c>--sample-data</c>, <c>--screenshot</c>): never touches the user's
    /// Windows setup (the "Run" registry key).
    /// </summary>
    public bool IsDevRun => SampleData || IsScreenshotMode || DataDirectory is not null;

    /// <summary>Single instance, tray, monitor and Windows integration are skipped for screenshots and sample data.</summary>
    public bool IsSandboxed => SampleData || IsScreenshotMode;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            if (IsTextExtractionArgument(arg))
            {
                options = options with { IsTextExtraction = true };
            }

            switch (arg.ToLowerInvariant())
            {
                case TrayFlag:
                    options = options with { StartInTray = true };
                    break;
                case "-embedding":
                case "/embedding":
                    options = options with { IsEmbedding = true };
                    break;
                case "--sample-data":
                    options = options with { SampleData = true };
                    break;
                case "--sample-icons":
                    options = options with { SampleIcons = true };
                    break;
                case "--text-size":
                    options = Next()?.ToLowerInvariant() switch
                    {
                        "small" => options with { TextSizeOverride = TextSize.Small },
                        "medium" => options with { TextSizeOverride = TextSize.Medium },
                        "large" => options with { TextSizeOverride = TextSize.Large },
                        "xlarge" => options with { TextSizeOverride = TextSize.ExtraLarge },
                        _ => options,
                    };
                    break;
                case "--bold-text":
                    options = Next()?.ToLowerInvariant() switch
                    {
                        "on" => options with { BoldTextOverride = true },
                        "off" => options with { BoldTextOverride = false },
                        _ => options,
                    };
                    break;
                case "--font":
                    options = Next() is { } font && FontNames.TryGetValue(font, out var appFont) ? options with { FontOverride = appFont } : options;
                    break;
                case "--theme":
                    options = Next()?.ToLowerInvariant() switch
                    {
                        "light" => options with { ThemeOverride = ThemePreference.Light },
                        "dark" => options with { ThemeOverride = ThemePreference.Dark },
                        "system" => options with { ThemeOverride = ThemePreference.System },
                        _ => options,
                    };
                    break;
                case "--data-dir":
                    options = options with { DataDirectory = Next() };
                    break;
                case "--downloads-dir":
                    options = options with { DownloadsDirectory = Next() };
                    break;
                case "--screenshot":
                    options = options with { ScreenshotPath = Next() };
                    break;
                case "--screenshot-state":
                    options = options with { ScreenshotState = Next()?.ToLowerInvariant() ?? "main" };
                    break;
                case Ai.TextExtractionChild.Flag:
                    options = options with { ExtractTextPath = Next() };
                    break;
                case Ai.TextExtractionChild.MaxCharsFlag:
                    options = options with
                    {
                        ExtractMaxChars = int.TryParse(Next(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max)
                            ? max
                            : null,
                    };
                    break;
            }
        }

        return options;
    }

    /// <summary>An argument that starts like a flag of the text-extraction child (<see cref="IsTextExtraction"/>).</summary>
    public static bool IsTextExtractionArgument(string arg)
        => arg.StartsWith(Ai.TextExtractionChild.Flag, StringComparison.OrdinalIgnoreCase) ||
           arg.StartsWith(Ai.TextExtractionChild.MaxCharsFlag, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for <c>-Embedding</c> and <c>/Embedding</c> in any case (COM's local-server flag).</summary>
    public static bool IsEmbeddingArgument(string arg)
        => arg.Length == EmbeddingFlag.Length && (arg[0] is '-' or '/') &&
           arg.AsSpan(1).Equals(EmbeddingFlag.AsSpan(1), StringComparison.OrdinalIgnoreCase);
}
