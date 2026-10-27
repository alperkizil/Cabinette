using System.Windows;
using System.Windows.Media;
using Cabinette.Core.Models;

namespace Cabinette.App.Services;

/// <summary>
/// App-wide font (Settings › Appearance › Font). All of Cabinette's text takes its family from two shared resources,
/// <c>{DynamicResource Shell.Font}</c> (text) and <c>{DynamicResource Shell.FontDisplay}</c> (titles), replaced live here
/// like <see cref="UiScale"/>'s transform and <see cref="UiWeight"/>'s weights. The windows, tooltips and context menus take
/// the text family; everything else inherits it. Unchanged by it: icons (ui:SymbolIcon has its own font), the Settings › AI
/// command boxes (always Cascadia Mono) and what Windows draws.
/// Every font on the list has a real SemiBold and Bold, so Bold text keeps titles heavier. Five are bundled (embedded in
/// Cabinette.dll, see assets/fonts); the others come with Windows and are offered only when installed. A Windows font
/// that went missing falls back, by the family list itself, to the default pair, and that one to Segoe UI.
/// </summary>
public static class UiFont
{
    public const string TextKey = "Shell.Font";
    public const string DisplayKey = "Shell.FontDisplay";

    private const string BundledRoot = "pack://application:,,,/Cabinette;component/Assets/Fonts/";

    // Registers the "pack" URI scheme before the list below builds pack URIs (an Application does it too, tests have none).
    private static readonly string PackScheme = System.IO.Packaging.PackUriHelper.UriSchemePack;
    private const string DefaultText = "Segoe UI Variable Text, Segoe UI";
    private const string DefaultDisplay = "Segoe UI Variable Display, Segoe UI";

    /// <summary>One font on the list.</summary>
    /// <param name="Font">The setting.</param>
    /// <param name="NameKey">The string key of its name in Settings.</param>
    /// <param name="Text">The family for text (and, except for the default, for titles).</param>
    /// <param name="Display">The family for titles.</param>
    /// <param name="WindowsFamily">The Windows font family it needs, or null when it is bundled.</param>
    public sealed record Choice(AppFont Font, string NameKey, FontFamily Text, FontFamily Display, string? WindowsFamily);

    /// <summary>The list in its order: sans-serif, then the serif, then the monospaced fonts. The first is the default.</summary>
    public static IReadOnlyList<Choice> All { get; } =
    [
        new(AppFont.SegoeUIVariable, "Settings_FontSegoeVariable", new FontFamily(DefaultText), new FontFamily(DefaultDisplay), "Segoe UI Variable Text"),
        FromWindows(AppFont.SegoeUI, "Settings_FontSegoe", "Segoe UI"),
        Bundled(AppFont.Inter, "Settings_FontInter", "Inter", "Inter"),
        Bundled(AppFont.SourceSans3, "Settings_FontSourceSans", "SourceSans3", "Source Sans 3"),
        Bundled(AppFont.AtkinsonHyperlegibleNext, "Settings_FontAtkinson", "AtkinsonHyperlegibleNext", "Atkinson Hyperlegible Next"),
        Bundled(AppFont.Lexend, "Settings_FontLexend", "Lexend", "Lexend"),
        FromWindows(AppFont.Bahnschrift, "Settings_FontBahnschrift", "Bahnschrift"),
        FromWindows(AppFont.Sitka, "Settings_FontSitka", "Sitka Text"),
        FromWindows(AppFont.CascadiaMono, "Settings_FontCascadia", "Cascadia Mono"),
        Bundled(AppFont.JetBrainsMono, "Settings_FontJetBrains", "JetBrainsMono", "JetBrains Mono"),
    ];

    /// <summary>The font drawn now (the setting, or the dev flag).</summary>
    public static AppFont Current { get; private set; } = AppFont.SegoeUIVariable;

    public static Choice Get(AppFont font) => All.FirstOrDefault(c => c.Font == font) ?? All[0];

    /// <summary>The fonts this PC can draw, in list order: the bundled ones always, a Windows one when it is installed.</summary>
    public static IReadOnlyList<Choice> Available(Func<string, bool>? isInstalled = null)
    {
        isInstalled ??= IsInstalled;
        return [.. All.Where(c => c.WindowsFamily is null || isInstalled(c.WindowsFamily))];
    }

    /// <summary>
    /// The list's entry for a saved font: itself while it is available, else the default, and where even that is missing
    /// (Windows 10 has no Segoe UI Variable), Segoe UI, which the default then draws as.
    /// </summary>
    public static AppFont Resolve(AppFont saved, Func<string, bool>? isInstalled = null)
    {
        var available = Available(isInstalled);
        return available.Any(c => c.Font == saved) ? saved
            : available.Any(c => c.Font == AppFont.SegoeUIVariable) ? AppFont.SegoeUIVariable
            : AppFont.SegoeUI;
    }

    /// <summary>Applies a font live. Call on the UI thread. Without an Application (tests) there is nothing to update.</summary>
    public static void Apply(AppFont font)
    {
        Current = Get(font).Font;
        if (Application.Current is { } app)
        {
            Apply(font, app.Resources);
        }
    }

    /// <summary>Puts the families of <paramref name="font"/> into <paramref name="resources"/> (the app's own, or a test host's).</summary>
    public static void Apply(AppFont font, ResourceDictionary resources)
    {
        var choice = Get(font);
        resources[TextKey] = choice.Text;
        resources[DisplayKey] = choice.Display;
    }

    /// <summary>
    /// Whether Windows has a font family of this name (the installed fonts are read once, on first use). If Windows can't
    /// list its fonts, every font counts as installed: a missing one draws as the default, and Settings still opens.
    /// </summary>
    public static bool IsInstalled(string family) => InstalledFamilies.Value?.Contains(family) ?? true;

    private static readonly Lazy<HashSet<string>?> InstalledFamilies = new(() => ReadInstalledFamilies(() => Fonts.SystemFontFamilies));

    /// <summary>The installed family names, or null when listing them fails (a damaged font, say).</summary>
    internal static HashSet<string>? ReadInstalledFamilies(Func<IEnumerable<FontFamily>> systemFamilies)
    {
        try
        {
            return systemFamilies().SelectMany(f => f.FamilyNames.Values.Append(f.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the installed fonts failed; every font is offered.", ex);
            return null;
        }
    }

    /// <summary>A Windows font for text and titles alike, falling back to the default pair while it isn't installed.</summary>
    private static Choice FromWindows(AppFont font, string nameKey, string family)
        => new(font, nameKey, new FontFamily($"{family}, {DefaultText}"), new FontFamily($"{family}, {DefaultDisplay}"), family);

    /// <summary>A font embedded from assets/fonts/<paramref name="folder"/>, by its typographic family name.</summary>
    private static Choice Bundled(AppFont font, string nameKey, string folder, string family)
    {
        var fontFamily = new FontFamily(new Uri(BundledRoot + folder + "/"), "./#" + family);
        return new(font, nameKey, fontFamily, fontFamily, null);
    }
}
