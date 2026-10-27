using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Windows;
using Xunit.Abstractions;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt of the thread banner (docs/review/bughunt-2026-10-24-banner/README.md). Each test fails on
/// <c>feat/category-banner</c> for the reason its name gives and passes once the finding is fixed.
/// </summary>
/// <remarks>
/// The band's colors are read from the real theme dictionaries and view XAML (Themes/Light.xaml, Themes/Dark.xaml,
/// Views/Thread/ThreadView.xaml, Views/Thread/ThreadResources.xaml), so the tests follow a fix that changes a key or a brush.
/// A brush given as <c>{Binding (TextElement.Foreground)}</c> or <c>{TemplateBinding Foreground}</c> counts as the band's text
/// color (Cab.BannerText, or Cab.OnAccent on the Starred band), as it is on the band.
/// </remarks>
public sealed class BannerReviewTests(ITestOutputHelper output)
{
    private static readonly CategoryIconGenerator Generator = new();

    /// <summary>WCAG 2.2 1.4.11 (non-text contrast): a focus indicator and a control's boundary need 3:1 against what they sit on.</summary>
    private const double MinNonTextContrast = 3.0;

    // ================================================================ F1: the keyboard focus ring on the band

    /// <summary>
    /// The band's tools (search, filter, view switch, select) keep <c>Cab.Thread.FocusVisual</c>: a 2 px ring in <c>Cab.Accent</c>.
    /// The Starred band <i>is</i> the accent, so the ring has 1:1 contrast there in both themes (invisible), and against the
    /// darkened avatar bands the accent reaches 3:1 on only some of them. Keyboard users lose track of which tool has focus.
    /// </summary>
    [Fact]
    public void F1_The_focus_ring_of_the_band_tools_vanishes_on_the_Starred_band_and_fades_on_avatar_bands()
    {
        var failures = new List<string>();
        output.WriteLine("theme  band                 ring              contrast");
        foreach (var theme in new[] { Theme.Load("Light"), Theme.Load("Dark") })
        {
            foreach (var (name, band, text) in Bands(theme))
            {
                // A ring of several tones (white inside a dark outline, as Windows draws focus) is visible when any tone reads on the band.
                var ring = FocusRingColors(theme, text);
                var contrast = ring.Max(color => ThreadBanner.ContrastRatio(color, band));
                output.WriteLine($"{theme.Name,-6} {name,-20} {string.Join("+", ring.Select(Hex)),-16}  {contrast,5:0.00}");
                if (contrast < MinNonTextContrast)
                {
                    failures.Add($"{theme.Name} {name}: {contrast:0.00}:1");
                }
            }
        }

        Assert.True(failures.Count == 0, "The focus ring is under 3:1 on these bands:\n" + string.Join("\n", failures));
    }

    // ================================================================ F2: the search box on the dark user-picture band

    /// <summary>
    /// The open search box (<c>SearchBoxHost</c>) is a borderless rounded box in <c>Cab.InputBackground</c>. In the dark theme
    /// that is #2A3942, and so is <c>Cab.BannerFallbackBackground</c>, the band of a category with the user's own picture: the
    /// box has no visible edge at all there (only the magnifier, the placeholder and the caret float on the band). On the old
    /// header (<c>Cab.HeaderBackground</c>) the same box was at least distinguishable.
    /// </summary>
    [Fact]
    public void F2_The_search_box_has_no_visible_edge_on_the_dark_themes_user_picture_band()
    {
        var failures = new List<string>();
        output.WriteLine("theme  band                 box bg   border   edge contrast");
        foreach (var theme in new[] { Theme.Load("Light"), Theme.Load("Dark") })
        {
            var box = SearchBox(theme);
            foreach (var (name, band, text) in Bands(theme))
            {
                var background = box.Background(theme, text);
                var edge = ThreadBanner.ContrastRatio(background, band);
                var border = box.Border(theme, text);
                if (border is { } brush)
                {
                    edge = Math.Max(edge, ThreadBanner.ContrastRatio(brush, band));
                }

                output.WriteLine($"{theme.Name,-6} {name,-20} {Hex(background)}  {(border is { } b ? Hex(b) : "none   ")}  {edge,5:0.00}");
                if (edge < 1.2)
                {
                    // 1.2:1 is what the box had against the old header in the dark theme (#2A3942 on #202C33): the band must not
                    // make it any less visible than that. 1.0 means no edge at all.
                    failures.Add($"{theme.Name} {name}: {edge:0.00}:1");
                }
            }
        }

        Assert.True(failures.Count == 0, "The search box has no visible edge on these bands:\n" + string.Join("\n", failures));
    }

    // ================================================================ F3: the user's own round logo

    /// <summary>
    /// SPEC.md section 4: "A category with your own picture as its icon uses the theme's banner color instead" and "A category
    /// you made shows its own icon there". <see cref="ThreadBanner.ReadAvatarColor"/> cannot tell a picture the user chose from
    /// a generated avatar: a round logo in one color with transparent corners (app icons, club badges, most brand marks as PNG),
    /// put through the picker's <see cref="CategoryIconService.ScaleToSquarePng"/>, passes as an avatar. The band then takes the
    /// logo's color, and the watermark is the generic folder glyph instead of the user's logo.
    /// </summary>
    [Fact]
    public void F3_A_round_logo_the_user_chose_is_taken_for_a_generated_avatar() => HiddenWpf.RunSta(() =>
    {
        // A badge: a blue disc with a white mark in the middle, transparent outside the disc, as the user's PNG would be.
        var logo = Png(512, (x, y) =>
        {
            if (!InCircle(512, x, y))
            {
                return default;
            }

            var mark = x is >= 196 and < 316 && y is >= 196 and < 316;
            return mark ? Bgra(Colors.White) : Bgra(Hex("#1F6FB2"));
        });
        var picture = CategoryIconService.ScaleToSquarePng(logo, 256);

        var category = new Category
        {
            Id = 7,
            Name = "Chess club",
            FolderName = "Chess club",
            Kind = CategoryKind.User,
            IconKey = ExtensionMap.FolderIconKey,
            IconPng = picture,
        };
        var look = ThreadBanner.ForCategory(category, Generator);

        Assert.Equal(ThreadBannerKind.Fallback, look.Kind);
        Assert.NotNull(look.WatermarkPicture);
    });

    // ================================================================ the bands

    /// <summary>Every band color with the text color on it: the user-picture band, Starred, and each palette avatar darkened as the view does.</summary>
    private static IEnumerable<(string Name, Color Band, Color Text)> Bands(Theme theme)
    {
        var white = theme.Color("Cab.BannerText");
        yield return ("user picture", ThreadBanner.ReadableBackground(theme.Color("Cab.BannerFallbackBackground"), white), white);
        var onAccent = theme.Color("Cab.OnAccent");
        yield return ("Starred", ThreadBanner.ReadableBackground(theme.Color("Cab.Accent"), onAccent), onAccent);
        var avatars = ExtensionMap.BuiltInCategories.Select(d => Generator.GetColor(d.IconKey, d.Name)).Distinct();
        foreach (var avatar in avatars)
        {
            yield return ($"avatar {Hex(avatar)}", ThreadBanner.ReadableBackground(avatar, white), white);
        }
    }

    /// <summary>The colors of the focus ring the band's toggle styles use: every border or stroke brush in their FocusVisualStyle's template.</summary>
    private static IReadOnlyList<Color> FocusRingColors(Theme theme, Color bandText)
    {
        var view = Xaml.Load("Views/Thread/ThreadView.xaml");
        var banner = view.Descendants().Single(e => (string?)e.Attribute(Xaml.X + "Name") == "Banner");
        var toggle = banner.Descendants().First(e => e.Name.LocalName == "Style" && (string?)e.Attribute(Xaml.X + "Key") == "Cab.Thread.IconToggleButton");
        var focusKey = Xaml.ResourceKey(Xaml.SetterValue(toggle, "FocusVisualStyle"))
            ?? throw new Xunit.Sdk.XunitException("The band's toggle style has no FocusVisualStyle.");

        var focusVisual = Xaml.FindKeyed(view, focusKey) ?? Xaml.FindKeyed(Xaml.Load("Views/Thread/ThreadResources.xaml"), focusKey)
            ?? throw new Xunit.Sdk.XunitException($"No style '{focusKey}' in ThreadView.xaml or ThreadResources.xaml.");
        var colors = focusVisual.Descendants()
            .Select(e => (string?)e.Attribute("BorderBrush") ?? (string?)e.Attribute("Stroke"))
            .Where(markup => markup is not null)
            .Select(markup => Brush.Resolve(markup!, theme, bandText) ?? throw new Xunit.Sdk.XunitException($"Focus ring brush not understood: {markup}"))
            .ToList();
        return colors.Count > 0 ? colors : throw new Xunit.Sdk.XunitException($"The focus visual '{focusKey}' draws no border or stroke.");
    }

    private static SearchBoxLook SearchBox(Theme theme)
    {
        var view = Xaml.Load("Views/Thread/ThreadView.xaml");
        var host = view.Descendants().Single(e => (string?)e.Attribute(Xaml.X + "Name") == "SearchBoxHost");
        return new SearchBoxLook((string?)host.Attribute("Background") ?? "", (string?)host.Attribute("BorderBrush"), (string?)host.Attribute("BorderThickness"));
    }

    private sealed record SearchBoxLook(string BackgroundMarkup, string? BorderMarkup, string? Thickness)
    {
        public Color Background(Theme theme, Color bandText)
            => Brush.Resolve(BackgroundMarkup, theme, bandText) ?? throw new Xunit.Sdk.XunitException($"Search box background not understood: {BackgroundMarkup}");

        public Color? Border(Theme theme, Color bandText)
        {
            if (BorderMarkup is null || Thickness is null || Thickness.Split(',').All(t => double.TryParse(t, out var d) && d <= 0))
            {
                return null;
            }

            return Brush.Resolve(BorderMarkup, theme, bandText);
        }
    }

    // ================================================================ XAML as data

    private static class Xaml
    {
        public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        public static XDocument Load(string relativePath) => XDocument.Load(Path.Combine(RepoRoot(), "src", "Cabinette.App", relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cabinette.slnx")))
                {
                    return dir.FullName;
                }
            }

            throw new DirectoryNotFoundException("Cabinette.slnx not found above " + AppContext.BaseDirectory);
        }

        public static XElement? FindKeyed(XDocument document, string key)
            => document.Descendants().FirstOrDefault(e => (string?)e.Attribute(X + "Key") == key);

        /// <summary>A style's setter value for a property, from the attribute or the Setter.Value element.</summary>
        public static string? SetterValue(XElement style, string property)
        {
            var setter = style.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property);
            return (string?)setter?.Attribute("Value");
        }

        public static string? ResourceKey(string? markup)
        {
            if (markup is null)
            {
                return null;
            }

            var match = Regex.Match(markup, @"\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\}");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    private static class Brush
    {
        /// <summary>A XAML brush value as a color: a Cab.* resource, a literal, or the band's text color for a binding to the inherited foreground.</summary>
        public static Color? Resolve(string markup, Theme theme, Color bandText)
        {
            if (Xaml.ResourceKey(markup) is { } key)
            {
                return theme.Color(key);
            }

            if (markup.Contains("TextElement.Foreground", StringComparison.Ordinal) || markup.Contains("TemplateBinding Foreground", StringComparison.Ordinal))
            {
                return bandText;
            }

            try
            {
                return (Color)ColorConverter.ConvertFromString(markup);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>The Cab.* colors of Themes/Light.xaml or Themes/Dark.xaml.</summary>
    private sealed class Theme
    {
        private readonly Dictionary<string, Color> _colors;

        private Theme(string name, Dictionary<string, Color> colors)
        {
            Name = name;
            _colors = colors;
        }

        public string Name { get; }

        public static Theme Load(string name)
        {
            var document = Xaml.Load($"Themes/{name}.xaml");
            var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var element in document.Root!.Elements())
            {
                var key = (string?)element.Attribute(Xaml.X + "Key");
                var value = element.Name.LocalName == "Color" ? element.Value : (string?)element.Attribute("Color");
                if (key is not null && value is not null)
                {
                    colors[key] = (Color)ColorConverter.ConvertFromString(value);
                }
            }

            return new Theme(name, colors);
        }

        public Color Color(string key) => _colors.TryGetValue(key, out var color) ? color : throw new KeyNotFoundException($"{Name}.xaml has no '{key}'.");
    }

    // ================================================================ pictures

    private static bool InCircle(int size, int x, int y)
    {
        var r = size / 2.0;
        var dx = x + 0.5 - r;
        var dy = y + 0.5 - r;
        return (dx * dx) + (dy * dy) <= r * r;
    }

    private static (byte B, byte G, byte R, byte A) Bgra(Color c) => (c.B, c.G, c.R, 255);

    private static byte[] Png(int size, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                var i = ((y * size) + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
