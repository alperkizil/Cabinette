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
using Cabinette.App.Services.Downloads;
using Cabinette.App.Services.Monitor;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Dialogs;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// "Bold text" (SPEC.md section 11): the weight mapping, the setting and its dev flag, the XAML conventions that keep every
/// weight live, and real views switching live. The test process has no Application, so the views here get the weights from
/// a host's resources the way the app's windows get them from Application.Resources; TextLookAuditTests checks the real app.
/// </summary>
public class BoldTextTests
{
    // ------------------------------------------------------------------ the mapping

    [Theory]
    [InlineData(300, false, 300)]
    [InlineData(400, false, 400)]
    [InlineData(500, false, 500)]
    [InlineData(600, false, 600)]
    [InlineData(700, false, 700)]
    [InlineData(300, true, 400)] // Light → Regular
    [InlineData(400, true, 600)] // Regular → SemiBold
    [InlineData(500, true, 700)] // Medium → Bold
    [InlineData(600, true, 700)] // SemiBold → Bold
    [InlineData(700, true, 700)] // Bold stays
    [InlineData(900, true, 900)] // nothing heavier gets lighter
    public void Each_weight_maps_one_step_heavier_when_on(int designed, bool bold, int drawn)
        => Assert.Equal(FontWeight.FromOpenTypeWeight(drawn), UiWeight.Map(FontWeight.FromOpenTypeWeight(designed), bold));

    [Fact]
    public void Every_key_holds_its_designed_weight_when_off_and_the_mapped_one_when_on()
    {
        Assert.Equal(
            [UiWeight.LightKey, UiWeight.BodyKey, UiWeight.MediumKey, UiWeight.StrongKey, UiWeight.BoldKey],
            UiWeight.Keys.Select(k => k.Key));
        var resources = new ResourceDictionary();

        UiWeight.Apply(false, resources);
        Assert.Equal([FontWeights.Light, FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold], Weights(resources));

        UiWeight.Apply(true, resources);
        Assert.Equal([FontWeights.Normal, FontWeights.SemiBold, FontWeights.Bold, FontWeights.Bold, FontWeights.Bold], Weights(resources));

        UiWeight.Apply(false, resources);
        Assert.Equal([FontWeights.Light, FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold], Weights(resources));
    }

    /// <summary>App.xaml's defaults (in place before settings load) are the design, so a start with Bold text off draws as before.</summary>
    [Fact]
    public void App_xaml_starts_with_the_designed_weights()
    {
        var app = XDocument.Load(Path.Combine(AppSource, "App.xaml"));
        var declared = app.Descendants(Wpf + "FontWeight").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());
        Assert.Equal(UiWeight.Keys.Select(k => k.Key).Order(), declared.Keys.Order());
        foreach (var (key, designed) in UiWeight.Keys)
        {
            Assert.Equal(designed.ToString(), declared[key]);
        }
    }

    // ------------------------------------------------------------------ the dev flag

    [Theory]
    [InlineData(new[] { "--bold-text", "on" }, true)]
    [InlineData(new[] { "--bold-text", "OFF" }, false)]
    [InlineData(new[] { "--BOLD-TEXT", "On" }, true)]
    [InlineData(new[] { "--bold-text", "yes" }, null)]
    [InlineData(new[] { "--bold-text" }, null)]
    [InlineData(new[] { "--sample-data" }, null)]
    public void The_dev_flag_parses_on_and_off(string[] args, bool? expected)
        => Assert.Equal(expected, CommandLineOptions.Parse(args).BoldTextOverride);

    [Fact]
    public void The_dev_flag_leaves_the_other_flags_alone()
    {
        var options = CommandLineOptions.Parse(["--sample-data", "--bold-text", "on", "--text-size", "xlarge", "--screenshot", "x.png"]);
        Assert.True(options.BoldTextOverride);
        Assert.Equal(TextSize.ExtraLarge, options.TextSizeOverride);
        Assert.True(options.SampleData);
        Assert.Equal("x.png", options.ScreenshotPath);
    }

    // ------------------------------------------------------------------ conventions

    /// <summary>
    /// No XAML file sets a weight itself: a literal FontWeight wouldn't follow Bold text. Use the Cab.FontWeight.* key named
    /// after the weight. Nothing needs to stay fixed so far, so the allow-list is empty; an entry needs a reason.
    /// </summary>
    [Fact]
    public void No_XAML_file_hardcodes_a_font_weight()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var literal = new Regex(@"^(Thin|ExtraLight|UltraLight|Light|Normal|Regular|Medium|SemiBold|DemiBold|Bold|ExtraBold|UltraBold|Black|Heavy|ExtraBlack|UltraBlack|\d+)$", RegexOptions.IgnoreCase);
        var offending = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppSource, "*.xaml", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var name = Path.GetRelativePath(AppSource, file);
            foreach (var element in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "FontWeight" or "TextElement.FontWeight"))
                {
                    if (literal.IsMatch(attribute.Value.Trim()))
                    {
                        offending.Add($"{name}:{((System.Xml.IXmlLineInfo)element).LineNumber} {attribute.Name.LocalName}=\"{attribute.Value}\"");
                    }
                }

                if (element.Name.LocalName == "Setter" && ((string?)element.Attribute("Property"))?.Split('.')[^1] == "FontWeight")
                {
                    var value = (string?)element.Attribute("Value") ?? element.Value;
                    if (literal.IsMatch(value.Trim()))
                    {
                        offending.Add($"{name}:{((System.Xml.IXmlLineInfo)element).LineNumber} Setter {element.Attribute("Property")!.Value}=\"{value}\"");
                    }
                }

                if (element.Name.LocalName is "FontWeight" && element.Parent?.Name.LocalName != "ResourceDictionary" && !name.Equals("App.xaml", StringComparison.Ordinal))
                {
                    offending.Add($"{name}:{((System.Xml.IXmlLineInfo)element).LineNumber} <FontWeight> element");
                }
            }
        }

        offending.RemoveAll(o => allowed.Contains(o.Split(' ')[0]));
        Assert.Empty(offending);
    }

    /// <summary>
    /// The same in code: a FontWeights.* value set on UI text wouldn't follow Bold text (use SetResourceReference with a
    /// UiWeight key, as AiActivityInlines does). UiWeight itself defines the design, and the DesignTime folder only draws
    /// sample pictures.
    /// </summary>
    [Fact]
    public void No_code_sets_a_fixed_font_weight_on_ui_text()
    {
        var offending = Directory.EnumerateFiles(AppSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f) && !f.Contains($"{Path.DirectorySeparatorChar}DesignTime{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith($"{Path.DirectorySeparatorChar}UiWeight.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(AppSource, f), Line: i + 1, Text: line)))
            .Where(l => Regex.IsMatch(l.Text, @"FontWeights\.\w+|FontWeight\.FromOpenTypeWeight"))
            .Select(l => $"{l.File}:{l.Line} {l.Text.Trim()}")
            .ToList();
        Assert.Empty(offending);
    }

    /// <summary>
    /// Plain text takes its weight from the top of each tree it lives in: every window, the title bars (WPF-UI's TitleBar
    /// style sets a weight of its own), and the tooltips and context menus, which are trees of their own.
    /// </summary>
    [Fact]
    public void Every_tree_top_takes_the_body_weight()
    {
        const string body = "{DynamicResource Cab.FontWeight.Body}";
        var windows = Directory.EnumerateFiles(AppSource, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(f => (File: Path.GetRelativePath(AppSource, f), Root: XDocument.Load(f).Root!))
            .Where(d => d.Root.Name.LocalName is "Window" or "FluentWindow")
            .ToList();
        Assert.Equal(4, windows.Count); // main window, first run, media viewer, tag editor
        Assert.All(windows, w => Assert.True((string?)w.Root.Attribute("FontWeight") == body, $"{w.File} has no body weight"));

        var titleBars = Directory.EnumerateFiles(AppSource, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => XDocument.Load(f).Descendants(Ui + "TitleBar").Select(e => (File: Path.GetRelativePath(AppSource, f), Element: e)))
            .ToList();
        Assert.NotEmpty(titleBars);
        Assert.All(titleBars.Where(t => t.Element.Element(Ui + "TitleBar.Header") is not null), t => Assert.Equal(body, (string?)t.Element.Attribute("FontWeight")));

        var styles = XDocument.Load(Path.Combine(AppSource, "Styles", "Controls.xaml"));
        foreach (var type in new[] { "ToolTip", "ContextMenu" })
        {
            var style = styles.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute("TargetType") == type && s.Attribute(X + "Key") is null);
            Assert.Contains(style.Elements(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "FontWeight" && (string?)s.Attribute("Value") == body);
        }
    }

    // ------------------------------------------------------------------ live switching in real views

    /// <summary>
    /// The thread pane as the main window hosts it (the body weight inherited from the window), and the filter popup inside
    /// it: switching on makes every text one step heavier, switching off brings back exactly the weights before.
    /// </summary>
    [Fact]
    public void The_thread_pane_and_its_popup_switch_live() => GalleryTests.RunOnDispatcher(async () =>
    {
        var vm = await OpenThreadAsync("Documents");
        var view = new ThreadView { DataContext = vm };
        var host = WindowLikeHost(view);
        using var source = HiddenWpf.Source(host, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);

        var popup = (Popup)view.FindName("FilterPopup");
        var panel = (FrameworkElement)popup.Child;
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        CheckLiveSwitch(host.Resources, () => [.. Texts(view), .. Texts(panel)], minimum: 20, layout: () =>
        {
            view.UpdateLayout();
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        });
        vm.Dispose();
    });

    /// <summary>A file's right-click menu (built by the real builder) with the app's own menu styles.</summary>
    [Fact]
    public void A_context_menu_switches_live() => GalleryTests.RunOnDispatcher(async () =>
    {
        var vm = await OpenThreadAsync("Documents");
        vm.SelectOnly([vm.Items[^1]]);
        var menu = ThreadItemMenuBuilder.Build(vm, default)!;
        menu.Resources.MergedDictionaries.Add(Controls.Value);
        menu.Style = (Style)Controls.Value![typeof(ContextMenu)];
        UiWeight.Apply(false, menu.Resources);
        Arrange(menu);

        CheckLiveSwitch(menu.Resources, () => Texts(menu), minimum: 5, layout: () => Arrange(menu));
        Assert.All(Texts(menu), t => Assert.Equal(FontWeights.Normal, t.Weight()));
        vm.Dispose();
    });

    [Fact]
    public void A_tooltip_switches_live() => HiddenWpf.RunSta(() =>
    {
        var tip = new ToolTip { Content = "IMG_2041.jpg\n2 MB\nDate added: 24/10/2026 09:39", Style = (Style)Controls.Value![typeof(ToolTip)] };
        tip.Resources.MergedDictionaries.Add(Controls.Value);
        UiWeight.Apply(false, tip.Resources);
        Arrange(tip);

        CheckLiveSwitch(tip.Resources, () => Texts(tip), minimum: 1, layout: () => Arrange(tip));
    });

    /// <summary>A dialog window of its own (the tag editor): its title, text and the box you type in.</summary>
    [Fact]
    public void A_dialog_switches_live() => HiddenWpf.RunSta(() =>
    {
        var dialog = new TagEditorDialog(new TagEditorViewModel("Tags for 2 files", "Report.pdf, Notes.txt", ["work"], [], []));
        UiWeight.Apply(false, dialog.Resources);
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(50));
        var title = LogicalTexts(dialog).Single(t => t.Text == "Tags for 2 files");
        var subtitle = LogicalTexts(dialog).Single(t => t.Text == "Report.pdf, Notes.txt");
        Assert.Equal(FontWeights.SemiBold, title.FontWeight);
        Assert.Equal(FontWeights.Normal, subtitle.FontWeight);
        Assert.Equal(FontWeights.Normal, dialog.Input.FontWeight);

        UiWeight.Apply(true, dialog.Resources);
        Assert.Equal(FontWeights.Bold, title.FontWeight);
        Assert.Equal(FontWeights.SemiBold, subtitle.FontWeight);
        Assert.Equal(FontWeights.SemiBold, dialog.Input.FontWeight);

        UiWeight.Apply(false, dialog.Resources);
        Assert.Equal(FontWeights.SemiBold, title.FontWeight);
        Assert.Equal(FontWeights.Normal, dialog.Input.FontWeight);
        dialog.Close();
    });

    /// <summary>
    /// Settings › AI's command boxes wrap a path only where it no longer fits on one line. Bold text makes a path wider
    /// without changing the box's width, so the box measures it again: no path is broken in the middle of a name.
    /// </summary>
    [Fact]
    public void A_command_box_measures_its_path_again_when_the_weight_changes() => HiddenWpf.RunSta(() =>
    {
        const string command = "claude mcp add cabinette -- \"C:\\Program Files\\Cabinette\\cabinette-mcp.exe\"";
        var box = new TextBox
        {
            // The real boxes use Cascadia Mono, whose weights all have the same widths; a proportional font shows the re-measuring.
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var root = new StackPanel();
        root.Children.Add(box);
        using var source = HiddenWpf.Source(root, 1000, 600);
        const string path = "\"C:\\Program Files\\Cabinette\\cabinette-mcp.exe\"";

        // A box just wide enough for the path at the regular weight, too narrow for it at semibold.
        var regular = Width(box, path, FontWeights.Normal);
        var semibold = Width(box, path, FontWeights.SemiBold);
        Assert.True(semibold > regular + 2, $"{semibold} vs {regular}");
        box.Width = Math.Ceiling(regular) + 8;
        AiCommandText.SetCommand(box, command);
        Settle(root);
        Assert.Equal(AiCommandText.ForDisplay(command, _ => false), box.Text);

        root.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
        Settle(root);
        Assert.Equal(AiCommandText.ForDisplay(command, part => part == path), box.Text);
        Assert.Contains(AiCommandText.BreakOpportunity, box.Text);

        root.SetValue(TextElement.FontWeightProperty, FontWeights.Normal);
        Settle(root);
        Assert.Equal(AiCommandText.ForDisplay(command, _ => false), box.Text);
    });

    // ------------------------------------------------------------------ the setting

    /// <summary>Settings shows the saved value, and switching it saves it and applies it.</summary>
    [Fact]
    public void Settings_shows_saves_and_applies_Bold_text() => AiConnectCommandsTests.RunUi(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        await library.SaveSettingsAsync((await library.GetSettingsAsync()) with { BoldText = true });
        var settings = CreateSettings(library);
        await settings.LoadAsync();
        Assert.True(settings.BoldText);

        settings.BoldText = false;
        await WaitUntilAsync(async () => !(await library.GetSettingsAsync()).BoldText);
        Assert.False(UiWeight.IsBold);

        settings.BoldText = true;
        await WaitUntilAsync(async () => (await library.GetSettingsAsync()).BoldText);
        Assert.True(UiWeight.IsBold);
        UiWeight.Apply(false);
    });

    // ------------------------------------------------------------------ helpers

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "http://schemas.lepo.co/wpfui/2022/xaml";

    internal static string AppSource => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Cabinette.App"));

    /// <summary>Styles/Controls.xaml as checked in (its implicit ToolTip, ContextMenu and MenuItem styles).</summary>
    internal static readonly ThreadLocal<ResourceDictionary> Controls = new(() => GalleryReviewSupport.LoadComponent<ResourceDictionary>("Styles/Controls.xaml"));

    internal static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
           path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static FontWeight[] Weights(ResourceDictionary resources) => [.. UiWeight.Keys.Select(k => (FontWeight)resources[k.Key])];

    /// <summary>A text on screen and how to read its weight and family now.</summary>
    internal sealed record Text(string Content, Func<FontWeight> Weight, Func<FontFamily> Family, string Kind);

    /// <summary>Every piece of text below <paramref name="root"/> (runs in text blocks, text boxes), icons left out.</summary>
    internal static List<Text> Texts(DependencyObject root)
    {
        var texts = new List<Text>();

        void Inlines(IEnumerable<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Run { Text.Length: > 0 } run)
                {
                    texts.Add(new Text(run.Text, () => run.FontWeight, () => run.FontFamily, "Run"));
                }
                else if (inline is Span span)
                {
                    Inlines(span.Inlines);
                }
            }
        }

        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case global::Wpf.Ui.Controls.IconElement:
                    return;
                case TextBlock block when block.Inlines.Count > 0:
                    Inlines(block.Inlines);
                    break;
                case TextBlock { Text.Length: > 0 } block:
                    texts.Add(new Text(block.Text, () => block.FontWeight, () => block.FontFamily, "TextBlock"));
                    break;
                case TextBox box:
                    texts.Add(new Text(box.Text, () => box.FontWeight, () => box.FontFamily, "TextBox"));
                    break;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(root);
        return texts;
    }

    /// <summary>
    /// Off, records each text's weight; on, each must be the mapped weight (and at least one plain and one strong text must
    /// be there); off again, each must be back to what it was.
    /// </summary>
    private static void CheckLiveSwitch(ResourceDictionary resources, Func<List<Text>> collect, int minimum, Action layout)
    {
        UiWeight.Apply(false, resources);
        layout();
        var texts = collect();
        Assert.True(texts.Count >= minimum, $"only {texts.Count} texts");
        var designed = texts.Select(t => t.Weight()).ToList();
        Assert.Contains(FontWeights.Normal, designed);

        UiWeight.Apply(true, resources);
        layout();
        for (var i = 0; i < texts.Count; i++)
        {
            Assert.True(UiWeight.Map(designed[i], bold: true) == texts[i].Weight(), $"\"{texts[i].Content}\" ({texts[i].Kind}): {designed[i]} drew as {texts[i].Weight()}");
        }

        UiWeight.Apply(false, resources);
        layout();
        Assert.Equal(designed, texts.Select(t => t.Weight()));
    }

    private static IEnumerable<TextBlock> LogicalTexts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock text)
            {
                yield return text;
            }

            foreach (var nested in LogicalTexts(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>What a window gives the thread pane: the theme, the weights, and the body weight to inherit.</summary>
    internal static Border WindowLikeHost(UIElement content)
    {
        var host = new Border { Child = content };
        host.Resources.MergedDictionaries.Add(GalleryReviewSupport.LoadComponent<ResourceDictionary>("Themes/Light.xaml"));
        UiWeight.Apply(false, host.Resources);
        host.SetResourceReference(TextElement.FontWeightProperty, UiWeight.BodyKey);
        return host;
    }

    /// <summary>A sample library's top-level thread, loaded.</summary>
    internal static async Task<ThreadViewModel> OpenThreadAsync(string name)
    {
        var library = SampleLibraryService.Create(withVault: true);
        var category = (await library.GetAllCategoriesAsync()).Single(c => c.ParentId is null && c.Name == name);
        var vm = new ThreadViewModel(new ThreadServices(
            library,
            NullProxy.Create<IShellContextMenuService>(),
            NullProxy.Create<IShellOperations>(),
            NullProxy.Create<IFileIntake>(),
            NullProxy.Create<IDialogService>(),
            new NotificationService(),
            new ShellNavigator(),
            new ThumbnailLoader(NullProxy.Create<IThumbnailService>(), NullProxy.Create<IMediaInfoService>()),
            new ThreadViewState()));
        await vm.LoadAsync(category.Id);
        Assert.NotEmpty(vm.Items);
        return vm;
    }

    /// <summary>Lays out a menu or tooltip on its own, as the screenshot tool does (opening one would show a window).</summary>
    internal static void Arrange(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static double Width(TextBox box, string text, FontWeight weight)
        => new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(box.FontFamily, box.FontStyle, weight, box.FontStretch), box.FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    private static void Settle(FrameworkElement root)
    {
        root.UpdateLayout();
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(30));
        root.UpdateLayout();
    }

    internal static SettingsViewModel CreateSettings(ILibraryService library)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "Cabinette-bold-text-tests-unused"));
        var dialogs = new DialogService(library, new CategoryIconService(library, NullProxy.Create<ICategoryIconGenerator>()), new CategoryDirectory(library));
        var notifications = new NotificationService();
        var monitor = new VaultMonitorHost(library);
        var shell = NullProxy.Create<IShellOperations>();
        var operations = new VaultOperationsService(library, dialogs, notifications, NullProxy.Create<IRecycleBin>(), shell, paths, monitor, new AppLifetime());
        return new SettingsViewModel(library, NullProxy.Create<IThemeService>(), notifications, shell, monitor, operations,
            new DownloadsModeConfirmation(NullProxy.Create<IDialogService>(), new AppStateStore(paths), CommandLineOptions.Parse(["--sample-data"])));
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the setting to be saved.");
            await Task.Delay(20);
        }
    }
}
