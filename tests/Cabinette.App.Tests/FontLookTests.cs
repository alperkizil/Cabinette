using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>
/// The Font setting (SPEC.md section 11): the list and its fallbacks, the bundled fonts' weights, the dev flag, the XAML
/// conventions that keep every family live, and real views switching live (as in <see cref="BoldTextTests"/>, from a
/// host's resources: the test process has no Application). TextLookAuditTests checks the real app.
/// </summary>
public class FontLookTests
{
    private static readonly AppFont[] Bundled = [AppFont.Inter, AppFont.SourceSans3, AppFont.AtkinsonHyperlegibleNext, AppFont.Lexend, AppFont.JetBrainsMono];

    // ------------------------------------------------------------------ the list

    [Fact]
    public void The_list_has_every_font_once_in_its_order_with_the_default_first()
    {
        Assert.Equal(
            [AppFont.SegoeUIVariable, AppFont.SegoeUI, AppFont.Inter, AppFont.SourceSans3, AppFont.AtkinsonHyperlegibleNext, AppFont.Lexend,
             AppFont.Bahnschrift, AppFont.Sitka, AppFont.CascadiaMono, AppFont.JetBrainsMono],
            UiFont.All.Select(c => c.Font));
        Assert.Equal(Enum.GetValues<AppFont>().Order(), UiFont.All.Select(c => c.Font).Order());
        Assert.Equal(Bundled.Order(), UiFont.All.Where(c => c.WindowsFamily is null).Select(c => c.Font).Order());
        Assert.All(UiFont.All, c => Assert.DoesNotContain("[", Localization.L.Get(c.NameKey), StringComparison.Ordinal));
    }

    /// <summary>The default keeps today's pair (Display for titles); every other font draws titles in the same family as text.</summary>
    [Fact]
    public void The_default_keeps_its_title_family_and_the_others_use_one_family()
    {
        var standard = UiFont.Get(AppFont.SegoeUIVariable);
        Assert.Equal("Segoe UI Variable Text, Segoe UI", standard.Text.Source);
        Assert.Equal("Segoe UI Variable Display, Segoe UI", standard.Display.Source);
        Assert.All(UiFont.All.Skip(1).Where(c => c.WindowsFamily is null), c => Assert.Same(c.Text, c.Display));
        Assert.All(UiFont.All.Skip(1).Where(c => c.WindowsFamily is not null), c =>
        {
            Assert.Equal($"{c.WindowsFamily}, Segoe UI Variable Text, Segoe UI", c.Text.Source);
            Assert.Equal($"{c.WindowsFamily}, Segoe UI Variable Display, Segoe UI", c.Display.Source);
        });
    }

    [Fact]
    public void Windows_fonts_that_are_not_installed_are_left_out_and_bundled_ones_never()
    {
        var windows10 = UiFont.Available(family => family is not ("Segoe UI Variable Text" or "Cascadia Mono"));
        Assert.Equal(
            [AppFont.SegoeUI, AppFont.Inter, AppFont.SourceSans3, AppFont.AtkinsonHyperlegibleNext, AppFont.Lexend, AppFont.Bahnschrift, AppFont.Sitka, AppFont.JetBrainsMono],
            windows10.Select(c => c.Font));

        Assert.Equal(Bundled, UiFont.Available(_ => false).Select(c => c.Font));
        Assert.Equal(UiFont.All.Select(c => c.Font), UiFont.Available(_ => true).Select(c => c.Font));
    }

    /// <summary>The real check, on this PC: Segoe UI is everywhere, and a name that isn't a font is not installed.</summary>
    [Fact]
    public void Installed_fonts_are_read_from_Windows()
    {
        Assert.True(UiFont.IsInstalled("Segoe UI"));
        Assert.True(UiFont.IsInstalled("segoe ui"));
        Assert.False(UiFont.IsInstalled("No Such Font " + Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData(AppFont.CascadiaMono, true, AppFont.CascadiaMono)]
    [InlineData(AppFont.CascadiaMono, false, AppFont.SegoeUIVariable)] // no longer installed: the default
    [InlineData(AppFont.JetBrainsMono, false, AppFont.JetBrainsMono)] // bundled: always there
    [InlineData(AppFont.SegoeUIVariable, true, AppFont.SegoeUIVariable)]
    public void A_saved_font_that_is_no_longer_available_falls_back_to_the_default(AppFont saved, bool installed, AppFont shown)
        => Assert.Equal(shown, UiFont.Resolve(saved, family => family == "Segoe UI Variable Text" || installed));

    /// <summary>Windows 10 has no Segoe UI Variable: the default, and a font gone missing, show as Segoe UI, which they draw as.</summary>
    [Fact]
    public void Without_Segoe_UI_Variable_the_default_is_Segoe_UI()
    {
        static bool Windows10(string family) => family is not ("Segoe UI Variable Text" or "Cascadia Mono");
        Assert.Equal(AppFont.SegoeUI, UiFont.Resolve(AppFont.SegoeUIVariable, Windows10));
        Assert.Equal(AppFont.SegoeUI, UiFont.Resolve(AppFont.CascadiaMono, Windows10));
        Assert.Equal(AppFont.Lexend, UiFont.Resolve(AppFont.Lexend, Windows10));
    }

    /// <summary>App.xaml starts with the default font, the one a start without settings uses.</summary>
    [Fact]
    public void App_xaml_starts_with_the_default_families()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var families = XDocument.Load(Path.Combine(BoldTextTests.AppSource, "App.xaml")).Descendants(wpf + "FontFamily")
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value.Trim());
        Assert.Equal(UiFont.Get(AppFont.SegoeUIVariable).Text.Source, families[UiFont.TextKey]);
        Assert.Equal(UiFont.Get(AppFont.SegoeUIVariable).Display.Source, families[UiFont.DisplayKey]);
    }

    [Fact]
    public void Apply_puts_both_families_into_the_resources()
    {
        var resources = new ResourceDictionary();
        foreach (var choice in UiFont.All)
        {
            UiFont.Apply(choice.Font, resources);
            Assert.Same(choice.Text, resources[UiFont.TextKey]);
            Assert.Same(choice.Display, resources[UiFont.DisplayKey]);
        }
    }

    // ------------------------------------------------------------------ the bundled fonts

    /// <summary>
    /// Each bundled weight is a file of its own, embedded in Cabinette.dll. The static files' Win32 family names differ
    /// ("JetBrains Mono SemiBold" and so on); WPF must still pick each weight's own file by the shared typographic family,
    /// never a simulated bold.
    /// </summary>
    [Theory]
    [InlineData(AppFont.Inter, "Inter")]
    [InlineData(AppFont.SourceSans3, "SourceSans3")]
    [InlineData(AppFont.AtkinsonHyperlegibleNext, "AtkinsonHyperlegibleNext")]
    [InlineData(AppFont.Lexend, "Lexend")]
    [InlineData(AppFont.JetBrainsMono, "JetBrainsMono")]
    public void Every_bundled_weight_draws_from_its_own_file(AppFont font, string folder) => HiddenWpf.RunSta(() =>
    {
        // Application's type initializer makes pack://application: resources readable, as in the app (no instance is made:
        // other tests need Application.Current to stay null).
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        var family = UiFont.Get(font).Text;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var weight in new[] { FontWeights.Light, FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold })
        {
            Assert.True(new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var glyphs), $"{font} {weight}");
            Assert.Equal(weight, glyphs.Weight);
            Assert.Equal(StyleSimulations.None, glyphs.StyleSimulations);
            Assert.Contains($"/Assets/Fonts/{folder}/", glyphs.FontUri.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/Cabinette;component/", glyphs.FontUri.ToString(), StringComparison.OrdinalIgnoreCase);
            files.Add(glyphs.FontUri.ToString());

            // The letters this app's text needs, in Turkish too, and the "…" and "·" of its labels.
            foreach (var character in "AaZz09çÇğĞıİöÖşŞüÜ…·›")
            {
                Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(character), $"{font} {weight} has no '{character}'");
            }
        }

        Assert.Equal(5, files.Count);
    });

    /// <summary>The OFL travels with each bundled font: its license is next to Cabinette.exe, unchanged.</summary>
    [Fact]
    public void Every_bundled_font_ships_its_license()
    {
        var assets = Path.GetFullPath(Path.Combine(BoldTextTests.AppSource, "..", "..", "assets", "fonts"));
        var folders = Directory.GetDirectories(assets).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["AtkinsonHyperlegibleNext", "Inter", "JetBrainsMono", "Lexend", "SourceSans3"], folders);
        foreach (var folder in folders)
        {
            var license = Directory.GetFiles(Path.Combine(assets, folder!)).Single(f => Regex.IsMatch(Path.GetFileName(f), @"^(OFL\.txt|LICENSE\.(txt|md))$"));
            Assert.Contains("SIL OPEN FONT LICENSE", File.ReadAllText(license).ToUpperInvariant(), StringComparison.Ordinal);
            var shipped = Path.Combine(AppContext.BaseDirectory, "licenses", $"{folder}-OFL.txt");
            Assert.True(File.Exists(shipped), $"{shipped} is missing");
            Assert.Equal(File.ReadAllBytes(license), File.ReadAllBytes(shipped));
        }

        Assert.Equal(folders.Count, Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "licenses")).Length);
    }

    // ------------------------------------------------------------------ the dev flag

    [Theory]
    [InlineData("segoe-variable", AppFont.SegoeUIVariable)]
    [InlineData("segoe", AppFont.SegoeUI)]
    [InlineData("inter", AppFont.Inter)]
    [InlineData("source-sans", AppFont.SourceSans3)]
    [InlineData("atkinson", AppFont.AtkinsonHyperlegibleNext)]
    [InlineData("lexend", AppFont.Lexend)]
    [InlineData("bahnschrift", AppFont.Bahnschrift)]
    [InlineData("SITKA", AppFont.Sitka)]
    [InlineData("cascadia", AppFont.CascadiaMono)]
    [InlineData("jetbrains", AppFont.JetBrainsMono)]
    public void The_dev_flag_names_every_font(string name, AppFont font)
        => Assert.Equal(font, CommandLineOptions.Parse(["--sample-data", "--font", name, "--bold-text", "on"]).FontOverride);

    [Theory]
    [InlineData("--font comic-sans")]
    [InlineData("--font")]
    [InlineData("--sample-data")]
    public void An_unknown_or_missing_font_name_changes_nothing(string commandLine)
        => Assert.Null(CommandLineOptions.Parse(commandLine.Split(' ')).FontOverride);

    [Fact]
    public void Every_font_has_a_dev_flag_name() => Assert.Equal(Enum.GetValues<AppFont>().Order(), CommandLineOptions.FontNames.Values.Order());

    // ------------------------------------------------------------------ conventions

    /// <summary>Nothing reads the families once and keeps them: every use is a DynamicResource, so the font applies live.</summary>
    [Fact]
    public void No_static_use_of_the_font_families_is_left()
    {
        var offending = Directory.EnumerateFiles(BoldTextTests.AppSource, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !BoldTextTests.IsBuildOutput(f))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(BoldTextTests.AppSource, f), Line: i + 1, Text: line)))
            .Where(l => Regex.IsMatch(l.Text, @"StaticResource\s+Shell\.Font|FindResource\(""Shell\.Font|TryFindResource\(""Shell\.Font|Resources\[""Shell\.Font|\.FontFamily\s*=\s*owner"))
            .Select(l => $"{l.File}:{l.Line} {l.Text.Trim()}")
            .ToList();
        Assert.Empty(offending);
    }

    /// <summary>
    /// The families come from the two resources only. The allowed exception: Settings › AI's command font (the command
    /// boxes stay Cascadia Mono whatever the font, so a long path keeps its columns).
    /// </summary>
    [Fact]
    public void No_XAML_file_sets_a_family_of_its_own()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var allowed = new HashSet<string>(["{DynamicResource Shell.Font}", "{DynamicResource Shell.FontDisplay}", "{StaticResource CommandFont}", "{Binding Family}"], StringComparer.Ordinal);
        var offending = new List<string>();
        foreach (var file in Directory.EnumerateFiles(BoldTextTests.AppSource, "*.xaml", SearchOption.AllDirectories).Where(f => !BoldTextTests.IsBuildOutput(f)))
        {
            var name = Path.GetRelativePath(BoldTextTests.AppSource, file);
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "FontFamily" or "TextElement.FontFamily"))
                {
                    if (!allowed.Contains(attribute.Value))
                    {
                        offending.Add($"{name}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
                    }
                }

                if (element.Name.LocalName == "Setter" && ((string?)element.Attribute("Property"))?.Split('.')[^1] == "FontFamily" && !allowed.Contains((string?)element.Attribute("Value") ?? ""))
                {
                    offending.Add($"{name}: Setter FontFamily=\"{element.Attribute("Value")?.Value}\"");
                }

                if (element.Name == wpf + "FontFamily" && name != "App.xaml" && (string?)element.Attribute(x + "Key") != "CommandFont")
                {
                    offending.Add($"{name}: <FontFamily x:Key=\"{element.Attribute(x + "Key")?.Value}\">");
                }
            }
        }

        Assert.Empty(offending);
    }

    /// <summary>Every window, and the tooltips and context menus (trees of their own), take the text family.</summary>
    [Fact]
    public void Every_tree_top_takes_the_text_family()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        const string text = "{DynamicResource Shell.Font}";
        var windows = Directory.EnumerateFiles(BoldTextTests.AppSource, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !BoldTextTests.IsBuildOutput(f))
            .Select(f => (File: Path.GetRelativePath(BoldTextTests.AppSource, f), Root: XDocument.Load(f).Root!))
            .Where(d => d.Root.Name.LocalName is "Window" or "FluentWindow")
            .ToList();
        Assert.Equal(4, windows.Count);
        Assert.All(windows, w => Assert.True((string?)w.Root.Attribute("FontFamily") == text, $"{w.File} has no text family"));

        var styles = XDocument.Load(Path.Combine(BoldTextTests.AppSource, "Styles", "Controls.xaml"));
        foreach (var type in new[] { "ToolTip", "ContextMenu" })
        {
            var style = styles.Descendants(wpf + "Style").Single(s => (string?)s.Attribute("TargetType") == type && s.Attribute(x + "Key") is null);
            Assert.Contains(style.Elements(wpf + "Setter"), s => (string?)s.Attribute("Property") == "FontFamily" && (string?)s.Attribute("Value") == text);
        }
    }

    // ------------------------------------------------------------------ live switching in real views

    [Fact]
    public void The_thread_pane_and_its_popup_switch_live() => GalleryTests.RunOnDispatcher(async () =>
    {
        var vm = await BoldTextTests.OpenThreadAsync("Documents");
        var view = new ThreadView { DataContext = vm };
        var host = BoldTextTests.WindowLikeHost(view);
        UiFont.Apply(AppFont.SegoeUIVariable, host.Resources);
        host.SetResourceReference(TextElement.FontFamilyProperty, UiFont.TextKey);
        using var source = HiddenWpf.Source(host, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        var panel = (FrameworkElement)((Popup)view.FindName("FilterPopup")).Child;

        CheckLiveSwitch(host.Resources, () => [.. BoldTextTests.Texts(view), .. BoldTextTests.Texts(panel)], minimum: 20, layout: () =>
        {
            view.UpdateLayout();
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        });
        vm.Dispose();
    });

    [Fact]
    public void A_context_menu_and_a_tooltip_switch_live() => GalleryTests.RunOnDispatcher(async () =>
    {
        var vm = await BoldTextTests.OpenThreadAsync("Documents");
        vm.SelectOnly([vm.Items[^1]]);
        var menu = ThreadItemMenuBuilder.Build(vm, default)!;
        menu.Resources.MergedDictionaries.Add(BoldTextTests.Controls.Value!);
        menu.Style = (Style)BoldTextTests.Controls.Value![typeof(ContextMenu)];
        CheckLiveSwitch(menu.Resources, () => BoldTextTests.Texts(menu), minimum: 5, layout: () => BoldTextTests.Arrange(menu));

        var tip = new ToolTip { Content = "IMG_2041.jpg\n2 MB", Style = (Style)BoldTextTests.Controls.Value![typeof(ToolTip)] };
        CheckLiveSwitch(tip.Resources, () => BoldTextTests.Texts(tip), minimum: 1, layout: () => BoldTextTests.Arrange(tip));
        vm.Dispose();
    });

    [Fact]
    public void A_dialog_switches_live() => HiddenWpf.RunSta(() =>
    {
        var dialog = new TagEditorDialog(new ViewModels.Thread.TagEditorViewModel("Tags for 2 files", "Report.pdf, Notes.txt", ["work"], [], []));
        foreach (var choice in UiFont.All)
        {
            UiFont.Apply(choice.Font, dialog.Resources);
            Assert.Same(choice.Text, dialog.FontFamily);
            Assert.Same(choice.Text, dialog.Input.FontFamily);
        }

        dialog.Close();
    });

    // ------------------------------------------------------------------ the setting

    /// <summary>Settings lists the fonts this PC has, each name in its own font, shows the saved one, and applies and saves a change.</summary>
    [Fact]
    public void Settings_lists_shows_applies_and_saves_the_font() => AiConnectCommandsTests.RunUi(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        await library.SaveSettingsAsync((await library.GetSettingsAsync()) with { Font = AppFont.Lexend });
        var settings = BoldTextTests.CreateSettings(library);
        await settings.LoadAsync();

        Assert.Equal(UiFont.Available().Select(c => c.Font), settings.Fonts.Select(f => f.Font));
        Assert.All(settings.Fonts, f => Assert.Same(UiFont.Get(f.Font).Text, f.Family));
        Assert.All(settings.Fonts, f => Assert.Equal(Localization.L.Get(UiFont.Get(f.Font).NameKey), f.Name));
        Assert.Equal(AppFont.Lexend, settings.SelectedFont!.Font);

        settings.SelectedFont = settings.Fonts.Single(f => f.Font == AppFont.JetBrainsMono);
        await BoldTextTests.WaitUntilAsync(async () => (await library.GetSettingsAsync()).Font == AppFont.JetBrainsMono);
        Assert.Equal(AppFont.JetBrainsMono, UiFont.Current);
        UiFont.Apply(AppFont.SegoeUIVariable);
    });

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// With the default font, records each text's family (all the text family: these views draw no titles in the display
    /// family); with each other font, every text must be in it; back to the default, each must be what it was.
    /// </summary>
    private static void CheckLiveSwitch(ResourceDictionary resources, Func<List<BoldTextTests.Text>> collect, int minimum, Action layout)
    {
        UiFont.Apply(AppFont.SegoeUIVariable, resources);
        layout();
        var texts = collect();
        Assert.True(texts.Count >= minimum, $"only {texts.Count} texts");
        var before = texts.Select(t => t.Family()).ToList();
        Assert.All(before, f => Assert.Same(UiFont.Get(AppFont.SegoeUIVariable).Text, f));

        // Collected again each time: a text that changes (a count) gets a new run, and the old one leaves the tree.
        foreach (var choice in UiFont.All.Skip(1))
        {
            UiFont.Apply(choice.Font, resources);
            layout();
            var now = collect();
            Assert.Equal(texts.Count, now.Count);
            Assert.All(now, t => Assert.True(ReferenceEquals(choice.Text, t.Family()), $"\"{t.Content}\" ({t.Kind}) is in {t.Family().Source}, not {choice.Text.Source}"));
        }

        UiFont.Apply(AppFont.SegoeUIVariable, resources);
        layout();
        Assert.Equal(before, collect().Select(t => t.Family()));
    }
}
