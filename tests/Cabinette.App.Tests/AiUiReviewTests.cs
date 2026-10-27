using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using Cabinette.App.DesignTime;
using Cabinette.App.Localization;
using Cabinette.App.Services.Ai;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.Views.Dialogs;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.Tests;

/// <summary>
/// Review of the AI UI (grouped toasts, Settings › AI). The reviewer's tests reproduced each finding (F1–F4); with the
/// fixes they are regression tests asserting the fixed behavior, joined by F5 (a dev run's cabinette-mcp.exe). Nothing
/// here touches the desktop, the clipboard or the real tray, and the real claude or codex never runs.
/// </summary>
public sealed class AiUiReviewTests
{
    // ------------------------------------------------------------------ F1: one bad details entry never hides the log

    /// <summary>
    /// A details JSON whose "files" (or "categories") array holds a null element parses fine, and Describe used to
    /// dereference the null ref and throw. Fixed: null refs are dropped, the counts stay as written.
    /// </summary>
    [Fact]
    public void F1_null_refs_in_the_details_json_are_skipped()
    {
        Assert.Equal("Read 1 file", Sentence(AiTools.ReadFile, """{"ok":true,"files":[null],"fileCount":1}"""));
        Assert.Equal("Deleted 3 files: a.pdf, b.pdf and 1 more", Sentence(AiTools.DeleteFiles, """{"ok":true,"files":[{"id":1,"name":"a.pdf"},null,{"id":2,"name":"b.pdf"}],"fileCount":3}"""));
        Assert.Equal("Moved x.pdf to another category", Sentence(AiTools.MoveFiles, """{"ok":true,"files":[{"id":1,"name":"x.pdf"}],"fileCount":1,"categories":[null]}"""));
        Assert.Equal("Renamed a category to Work", Sentence(AiTools.RenameCategory, """{"ok":true,"categories":[null],"newName":"Work"}"""));

        var failed = AiActivityText.Describe(new ActivityEntry { Source = "Codex", Action = AiTools.DeleteFiles, DetailsJson = """{"ok":false,"error":"Busy.","files":[null]}""" });
        Assert.Equal("Delete files", failed.Sentence);
        Assert.Equal("Failed: Busy.", failed.FailureText);

        var categories = AiActivityText.Describe(new ActivityEntry { Source = "Codex", Action = AiTools.DeleteCategory, DetailsJson = """{"ok":false,"error":"Busy.","categories":[null,{"id":4,"name":"Old"}]}""" });
        Assert.Equal("Delete category: Old", categories.Sentence);

        static string Sentence(string tool, string json)
            => AiActivityText.Describe(new ActivityEntry { Source = "Codex", Action = tool, DetailsJson = json }).Sentence;
    }

    /// <summary>
    /// The consequence in Settings › AI was that the row was built inside LoadAsync's try, so the one entry took the
    /// whole page down: "couldn't be read: NullReferenceException" and no rows at all, for a year. Fixed: all three rows
    /// show, the bad one with what its details still say.
    /// </summary>
    [Fact]
    public void F1_one_entry_with_a_null_file_ref_shows_with_the_others() => AiConnectCommandsTests.RunUi(async () =>
    {
        var log = new RawJsonActivityLog();
        var now = DateTimeOffset.Now;
        log.Add(now.AddHours(-2), "Claude Code", AiTools.Search, new AiActivityDetails { Query = "invoice", FileCount = 3 }.ToJson());
        log.Add(now.AddHours(-1), "Codex", AiTools.ReadFile, """{"ok":true,"files":[null],"fileCount":1}""");
        log.Add(now, "Claude Code", AiTools.ListTags, new AiActivityDetails().ToJson());
        using var vm = new AiActivityViewModel(log, SampleLibraryService.Create(withVault: true), new FakeNavigator(), () => { }, Dispatcher.CurrentDispatcher, TimeProvider.System);

        await vm.LoadAsync();

        Assert.Null(vm.Message);
        Assert.False(vm.IsEmpty);
        Assert.Equal(["Listed the tags", "Read 1 file", "Searched for “invoice” (3 results)"], vm.Entries.Select(r => r.Sentence));
        Assert.Equal("Codex", vm.Entries[1].Client);
    });

    /// <summary>
    /// Whatever else makes an entry impossible to describe, its row falls back to the tool's name (with its client and
    /// time), as SPEC.md section 10 promises for unreadable details, and the other rows are unaffected.
    /// </summary>
    [Fact]
    public void F1_an_entry_that_cant_be_described_shows_the_tool_name_and_client() => AiConnectCommandsTests.RunUi(() =>
    {
        var entry = new ActivityEntry { Id = 7, At = DateTimeOffset.Now, Source = "Codex", Action = AiTools.ReadFile, DetailsJson = """{"ok":true}""" };
        var command = new RelayCommand<AiActivitySegment?>(_ => { });

        var row = new AiActivityRowViewModel(entry, command, TimeZoneInfo.Local, DateTime.Today, _ => throw new InvalidOperationException("Can't describe."));

        Assert.Equal("Read file contents", row.Sentence);
        Assert.Equal([new AiActivitySegment("Read file contents")], row.Segments);
        Assert.False(row.IsFailed);
        Assert.Equal("Codex", row.Client);
        Assert.False(string.IsNullOrEmpty(row.Time));
        return Task.CompletedTask;
    });

    // ------------------------------------------------------------------ F2: every activity link is reachable by keyboard

    /// <summary>
    /// The activity list is a ListBox whose rows are not focusable (ActivityItem style) and whose links are Hyperlinks
    /// inside the row's TextBlock (AiActivityInlines). With a ListBox's own tab navigation ("Once") Tab entered on the
    /// first link and the next Tab left the whole list, so only the first link of the log could be opened. Fixed: the
    /// list's TabNavigation is "Local", so Tab walks every link in order and Shift+Tab walks back. Built in a hidden window
    /// from SettingsView.xaml as checked in (the list's and the item style's keyboard settings, Shell.ListBox's directional
    /// navigation) and the real AiActivityInlines.
    /// </summary>
    [Fact]
    public void F2_tab_walks_every_link_of_the_activity_list() => HiddenWpf.RunSta(() =>
    {
        var (list, before, _, root) = ActivityList(rows: 3, height: 200);
        using var source = HiddenWpf.Source(root, 400, 400);
        root.UpdateLayout();
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(50));

        Assert.True(before.Focus());
        Assert.Equal(["Hyperlink file1.pdf", "Hyperlink file2.pdf", "Hyperlink file3.pdf", "Button Close"], Walk(FocusNavigationDirection.Next, 4));
        Assert.Equal(["Hyperlink file3.pdf", "Hyperlink file2.pdf", "Hyperlink file1.pdf", "Button Show older"], Walk(FocusNavigationDirection.Previous, 4));
        Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation(list));
    });

    /// <summary>
    /// The list virtualizes: only the rows around the visible ones exist, and the rest of its cache is realized later, in
    /// the background. A link that gets keyboard focus makes sure the rows before and after its own exist and scrolls into
    /// view, so Tab reaches the last link of a long page, and Shift+Tab the first, even when the keys come faster than the
    /// background work (here: only what runs before the next key press is handled).
    /// </summary>
    [Fact]
    public void F2_tab_reaches_links_in_rows_that_were_not_realized_yet() => HiddenWpf.RunSta(() =>
    {
        const int Rows = 40;
        var (list, before, _, root) = ActivityList(Rows, height: 160);
        using var source = HiddenWpf.Source(root, 400, 400);
        root.UpdateLayout();
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(50));
        Assert.True(CountRealized(list) < Rows);

        Assert.True(before.Focus());
        var reached = Walk(FocusNavigationDirection.Next, Rows + 1, pumpBetween: true);

        Assert.Equal(Enumerable.Range(1, Rows).Select(i => $"Hyperlink file{i}.pdf").Append("Button Close"), reached);

        // And back: the first rows were recycled on the way down.
        var back = Walk(FocusNavigationDirection.Previous, Rows + 1, pumpBetween: true);
        Assert.Equal(Enumerable.Range(1, Rows).Reverse().Select(i => $"Hyperlink file{i}.pdf").Append("Button Show older"), back);
    });

    /// <summary>Each link keeps an accessible name, its file's or category's name, and is a tab stop.</summary>
    [Fact]
    public void F2_activity_links_are_named_after_their_items() => HiddenWpf.RunSta(() =>
    {
        var text = new TextBlock();
        AiActivityInlines.SetLinkCommand(text, new RelayCommand<AiActivitySegment?>(_ => { }));
        AiActivityInlines.SetSegments(text, [new AiActivitySegment("Moved "), new AiActivitySegment("Tax 2025.pdf", AiActivityLinkKind.File, 1), new AiActivitySegment(" to "), new AiActivitySegment("Work", AiActivityLinkKind.Category, 2)]);

        var links = text.Inlines.OfType<Hyperlink>().ToList();
        var names = links.Select(link => ContentElementAutomationPeer.CreatePeerForElement(link)?.GetName()).ToList();

        Assert.Equal(["Tax 2025.pdf", "Work"], names);
        Assert.All(links, link => Assert.True(link.Focusable));
    });

    private static List<string> Walk(FocusNavigationDirection direction, int steps, bool pumpBetween = false)
    {
        var reached = new List<string>();
        for (var i = 0; i < steps; i++)
        {
            var moved = Keyboard.FocusedElement switch
            {
                UIElement ui => ui.MoveFocus(new TraversalRequest(direction)),
                ContentElement ce => ce.MoveFocus(new TraversalRequest(direction)),
                _ => false,
            };
            Assert.True(moved, $"Focus didn't move after: {string.Join(", ", reached)}");
            reached.Add(Describe(Keyboard.FocusedElement));
            if (pumpBetween)
            {
                // What runs before the next key press is handled (input has a lower priority than layout): the scroll a
                // focused link asked for, which realizes the next rows. Not a timed pump, which a loaded machine outruns.
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
            }
        }

        return reached;
    }

    /// <summary>"Show older", the activity list with one file link per row, and "Close", with SettingsView.xaml's keyboard settings.</summary>
    private static (ListBox List, Button Before, Button After, StackPanel Root) ActivityList(int rows, double height)
    {
        var markup = ActivityListMarkup.Value;
        var list = new ListBox { ItemContainerStyle = ActivityItemStyle(markup.ItemFocusable), Height = height };
        KeyboardNavigation.SetTabNavigation(list, markup.TabNavigation);
        KeyboardNavigation.SetDirectionalNavigation(list, markup.DirectionalNavigation);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);

        // Rows come from a template, like the real list's, so the virtualizing panel creates and recycles them.
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 7, 14, 7));
        text.SetValue(AiActivityInlines.LinkCommandProperty, new RelayCommand<AiActivitySegment?>(_ => { }));
        text.SetBinding(AiActivityInlines.SegmentsProperty, new Binding());
        list.ItemTemplate = new DataTemplate { VisualTree = text };
        list.ItemsSource = Enumerable.Range(1, rows)
            .Select(i => (IReadOnlyList<AiActivitySegment>)[new AiActivitySegment("Read "), new AiActivitySegment($"file{i}.pdf", AiActivityLinkKind.File, i)])
            .ToList();

        var before = new Button { Content = "Show older" };
        var after = new Button { Content = "Close" };
        var root = new StackPanel();
        root.Children.Add(before);
        root.Children.Add(list);
        root.Children.Add(after);
        return (list, before, after, root);
    }

    private static int CountRealized(ListBox list)
        => Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);

    /// <summary>The activity list's keyboard settings as checked in: SettingsView.xaml's ActivityList and ActivityItem, and Shell.ListBox.</summary>
    private static readonly Lazy<(KeyboardNavigationMode TabNavigation, KeyboardNavigationMode DirectionalNavigation, bool ItemFocusable)> ActivityListMarkup = new(() =>
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Cabinette.App"));
        var settings = XDocument.Load(Path.Combine(app, "Views", "Dialogs", "SettingsView.xaml"));
        var styles = XDocument.Load(Path.Combine(app, "Styles", "Controls.xaml"));

        var list = settings.Descendants(wpf + "ListBox").Single(e => (string?)e.Attribute(x + "Name") == "ActivityList");
        Assert.Equal("{StaticResource Shell.ListBox}", (string?)list.Attribute("Style"));
        Assert.Equal("{StaticResource ActivityItem}", (string?)list.Attribute("ItemContainerStyle"));
        var item = settings.Descendants(wpf + "Style").Single(e => (string?)e.Attribute(x + "Key") == "ActivityItem");
        var shellList = styles.Descendants(wpf + "Style").Single(e => (string?)e.Attribute(x + "Key") == "Shell.ListBox");

        string? Setter(XElement style, string property)
            => style.Elements(wpf + "Setter").Where(s => (string?)s.Attribute("Property") == property).Select(s => (string?)s.Attribute("Value")).SingleOrDefault();

        var tab = (string?)list.Attribute("KeyboardNavigation.TabNavigation") ?? Setter(shellList, "KeyboardNavigation.TabNavigation");
        var directional = (string?)list.Attribute("KeyboardNavigation.DirectionalNavigation") ?? Setter(shellList, "KeyboardNavigation.DirectionalNavigation");
        var focusable = Setter(item, "Focusable");
        return (
            tab is null ? KeyboardNavigationMode.Once : Enum.Parse<KeyboardNavigationMode>(tab),
            directional is null ? KeyboardNavigationMode.Contained : Enum.Parse<KeyboardNavigationMode>(directional),
            focusable is null || bool.Parse(focusable));
    });

    // ------------------------------------------------------------------ F3: the commands read back unchanged in PowerShell

    /// <summary>
    /// The registration commands quote their paths with double quotes. Windows' default terminal shell is PowerShell, and
    /// inside double quotes it expands <c>$name</c> and <c>$(…)</c>, treats the backtick as its escape character and takes
    /// “ ” „ as double quotes, so such a path read back changed and the command registered a path that doesn't exist, with
    /// no error. Fixed: those characters get a backtick. Checked by letting Windows PowerShell parse each command exactly as
    /// the user would paste it (only the program name replaced by an echo program), for installed and dev commands.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\Cabinette\cabinette-mcp.exe")]
    [InlineData(@"C:\Users\Zoë Ünal\Programs (x86)\Kabinet ğüşıöç 日本語\cabinette-mcp.exe")]
    [InlineData(@"C:\dev$data\$(1+1) ${env:OS} $env:OS $\cabinette-mcp.exe")]
    [InlineData(@"C:\Temp\cab`dev`n `$x\cabinette-mcp.exe")]
    [InlineData(@"C:\it's & 100% done\%OS%\cabinette-mcp.exe")]
    [InlineData(@"C:\“quoted” ‘single’ „low‟\cabinette-mcp.exe")]
    [InlineData(@"C:\ends with $")]
    [InlineData(@"C:\ends with `")]
    public void F3_powershell_reads_every_path_back_unchanged(string path)
    {
        var installed = AiConnectCommands.Build(path, null);
        Assert.Equal(["mcp", "add", "--scope", "user", "cabinette", "--", path], ShellEcho.PowerShell(installed.ClaudeAdd));
        Assert.Equal(["mcp", "add", "cabinette", "--", path], ShellEcho.PowerShell(installed.CodexAdd));

        var dataDir = Path.Combine(Path.GetDirectoryName(path)!, "data $dir`");
        var app = Path.Combine(Path.GetDirectoryName(path)!, "app $(x) “y”", "Cabinette.exe");
        var dev = AiConnectCommands.Build(path, new AiDevRun(dataDir, app));
        Assert.Equal(["mcp", "add", "--scope", "local", "cabinette-dev", "--", path, "--data-dir", dataDir, "--app", app], ShellEcho.PowerShell(dev.ClaudeAdd));
        Assert.Equal(["mcp", "add", "cabinette-dev", "--", path, "--data-dir", dataDir, "--app", app], ShellEcho.PowerShell(dev.CodexAdd));
    }

    /// <summary>
    /// cmd.exe can't get the same text right for every path: it keeps the backticks PowerShell needs (before $, a backtick
    /// and “ ” „), and expands %NAME% of a defined variable even inside quotes. So Settings › AI says the commands are for
    /// PowerShell. A path without those still reads back unchanged in cmd, so the usual commands work there too.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\Cabinette\cabinette-mcp.exe")]
    [InlineData(@"C:\Users\Zoë Ünal\Programs (x86)\Kabinet ğüşıöç 日本語\cabinette-mcp.exe")]
    [InlineData(@"C:\it's & 100% done ^ (1)\cabinette-mcp.exe")]
    [InlineData(@"C:\Sam’s ‘single’ quotes\cabinette-mcp.exe")]
    public void F3_cmd_reads_paths_without_powershell_escapes_back_unchanged(string path)
    {
        var installed = AiConnectCommands.Build(path, null);
        Assert.Equal(["mcp", "add", "--scope", "user", "cabinette", "--", path], ShellEcho.Cmd(installed.ClaudeAdd));
        Assert.Equal(["mcp", "add", "cabinette", "--", path], ShellEcho.Cmd(installed.CodexAdd));
    }

    /// <summary>The section says which shell the commands are for.</summary>
    [Fact]
    public void F3_settings_say_the_commands_are_for_powershell()
        => Assert.Contains("PowerShell", L.Get("SettingsAi_ConnectHint"), StringComparison.Ordinal);

    // ------------------------------------------------------------------ F4: a library failure no longer reads as "gone"

    /// <summary>
    /// OpenLinkAsync turned every exception into the "no longer in Cabinette" / "no longer exists" message, so a library
    /// that merely couldn't answer right now (closed during a vault move or restore, busy, a database error) told the user
    /// their file was deleted. Fixed: only a null result means gone; an error says the item couldn't be opened, and why.
    /// </summary>
    [Fact]
    public void F4_a_library_error_while_opening_a_link_says_it_couldnt_be_opened() => AiConnectCommandsTests.RunUi(async () =>
    {
        var navigator = new FakeNavigator();
        var closed = 0;
        var library = ThrowingProxy.Create<ILibraryService>(new LibraryException(LibraryErrorCode.Unknown, "The library is closed."));
        using var vm = new AiActivityViewModel(new RawJsonActivityLog(), library, navigator, () => closed++, Dispatcher.CurrentDispatcher, TimeProvider.System);

        await vm.OpenLinkCommand.ExecuteAsync(new AiActivitySegment("Tax return 2025.pdf", AiActivityLinkKind.File, 42));
        Assert.Equal("“Tax return 2025.pdf” couldn't be opened: The library is closed.", vm.Message);

        await vm.OpenLinkCommand.ExecuteAsync(new AiActivitySegment("Work", AiActivityLinkKind.Category, 7));
        Assert.Equal("The category “Work” couldn't be opened: The library is closed.", vm.Message);

        Assert.Empty(navigator.Opened);
        Assert.Equal(0, closed);
    });

    /// <summary>Any other error says so too, naming its kind (like the log's own read failures).</summary>
    [Fact]
    public void F4_another_error_while_opening_a_link_says_it_couldnt_be_opened() => AiConnectCommandsTests.RunUi(async () =>
    {
        var library = ThrowingProxy.Create<ILibraryService>(new IOException("The disk is gone."));
        using var vm = new AiActivityViewModel(new RawJsonActivityLog(), library, new FakeNavigator(), () => { }, Dispatcher.CurrentDispatcher, TimeProvider.System);

        await vm.OpenLinkCommand.ExecuteAsync(new AiActivitySegment("Tax return 2025.pdf", AiActivityLinkKind.File, 42));

        Assert.Equal("“Tax return 2025.pdf” couldn't be opened: IOException", vm.Message);
    });

    // ------------------------------------------------------------------ F5: a dev run finds the repository's own build

    /// <summary>
    /// In a dev run cabinette-mcp.exe isn't next to Cabinette.exe (the projects build to different folders), so Settings ›
    /// AI always said it wasn't found and to reinstall. Fixed: a dev run walks up from Cabinette.exe to the folder with
    /// Cabinette.slnx and uses src\Cabinette.Mcp\bin\Debug or Release\net10.0-windows\cabinette-mcp.exe, the newest.
    /// </summary>
    [Fact]
    public void F5_a_dev_run_uses_the_newest_build_of_its_repository() => AiConnectCommandsTests.RunUi(async () =>
    {
        using var repo = new FakeRepository();
        var debug = repo.Build("Debug", DateTime.UtcNow.AddHours(-2));
        var release = repo.Build("Release", DateTime.UtcNow.AddHours(-1));

        Assert.Equal(new AiExeLocation(release, true), AiConnectCommands.Locate(repo.NextToApp, repo.AppFolder));
        File.SetLastWriteTimeUtc(debug, DateTime.UtcNow);
        Assert.Equal(new AiExeLocation(debug, true), AiConnectCommands.Locate(repo.NextToApp, repo.AppFolder));

        using var settings = AiConnectCommandsTests.CreateSettings(repo.NextToApp, repo.Dev, devSearchStart: repo.AppFolder);
        await settings.LoadAsync();

        Assert.Null(settings.ExeMissingText);
        Assert.Equal(debug, settings.Commands.ExePath);
        Assert.Equal($@"claude mcp add --scope local cabinette-dev -- ""{debug}"" --data-dir ""{repo.Dev.DataDirectory}"" --app ""{repo.Dev.AppPath}""", settings.ClaudeCode.Text);
        Assert.Equal($@"codex mcp add cabinette-dev -- ""{debug}"" --data-dir ""{repo.Dev.DataDirectory}"" --app ""{repo.Dev.AppPath}""", settings.Codex.Text);
    });

    /// <summary>A cabinette-mcp.exe next to the dev Cabinette.exe (a published dev copy) still comes first.</summary>
    [Fact]
    public void F5_a_dev_run_prefers_the_exe_next_to_cabinette()
    {
        using var repo = new FakeRepository();
        repo.Build("Debug", DateTime.UtcNow);
        File.WriteAllBytes(repo.NextToApp, []);

        Assert.Equal(new AiExeLocation(repo.NextToApp, true), AiConnectCommands.Locate(repo.NextToApp, repo.AppFolder));
    }

    /// <summary>Without a build, the commands show where the Debug build will be, and the hint says to build it, not to reinstall.</summary>
    [Fact]
    public void F5_a_dev_run_without_a_build_says_to_build_cabinette_mcp() => AiConnectCommandsTests.RunUi(async () =>
    {
        using var repo = new FakeRepository();
        var expected = Path.Combine(repo.Root, "src", "Cabinette.Mcp", "bin", "Debug", "net10.0-windows", "cabinette-mcp.exe");
        using var settings = AiConnectCommandsTests.CreateSettings(repo.NextToApp, repo.Dev, devSearchStart: repo.AppFolder);

        await settings.LoadAsync();

        Assert.Equal(expected, settings.Commands.ExePath);
        Assert.StartsWith($@"claude mcp add --scope local cabinette-dev -- ""{expected}""", settings.ClaudeCode.Text, StringComparison.Ordinal);
        Assert.Equal($"cabinette-mcp.exe wasn't found in {Path.GetDirectoryName(expected)}. Build src/Cabinette.Mcp (dotnet build Cabinette.slnx) before running these commands.", settings.ExeMissingText);
    });

    /// <summary>A dev run outside any repository (an installed copy started with --data-dir) looks next to itself, and says to build too.</summary>
    [Fact]
    public void F5_a_dev_run_outside_a_repository_looks_next_to_cabinette() => AiConnectCommandsTests.RunUi(async () =>
    {
        var folder = Directory.CreateTempSubdirectory("cabinette-tests-").FullName;
        try
        {
            var nextToApp = Path.Combine(folder, "cabinette-mcp.exe");
            Assert.Equal(new AiExeLocation(nextToApp, false), AiConnectCommands.Locate(nextToApp, folder));

            using var settings = AiConnectCommandsTests.CreateSettings(nextToApp, new AiDevRun(Path.Combine(folder, "data"), Path.Combine(folder, "Cabinette.exe")), devSearchStart: folder);
            await settings.LoadAsync();

            Assert.Equal(nextToApp, settings.Commands.ExePath);
            Assert.Equal($"cabinette-mcp.exe wasn't found in {folder}. Build src/Cabinette.Mcp (dotnet build Cabinette.slnx) before running these commands.", settings.ExeMissingText);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    /// <summary>A temp folder laid out like the repository: Cabinette.slnx, the dev Cabinette.exe's folder, and room for cabinette-mcp.exe builds.</summary>
    private sealed class FakeRepository : IDisposable
    {
        public FakeRepository()
        {
            Root = Directory.CreateTempSubdirectory("cabinette-tests-repo ").FullName;
            File.WriteAllText(Path.Combine(Root, "Cabinette.slnx"), "<Solution />");
            AppFolder = Path.Combine(Root, "src", "Cabinette.App", "bin", "Debug", "net10.0-windows10.0.19041.0");
            Directory.CreateDirectory(AppFolder);
            Dev = new AiDevRun(Path.Combine(Root, "dev data"), Path.Combine(AppFolder, "Cabinette.exe"));
        }

        public string Root { get; }

        public string AppFolder { get; }

        public string NextToApp => Path.Combine(AppFolder, "cabinette-mcp.exe");

        public AiDevRun Dev { get; }

        public string Build(string configuration, DateTime writtenUtc)
        {
            var folder = Path.Combine(Root, "src", "Cabinette.Mcp", "bin", configuration, "net10.0-windows");
            Directory.CreateDirectory(folder);
            var exe = Path.Combine(folder, "cabinette-mcp.exe");
            File.WriteAllBytes(exe, []);
            File.SetLastWriteTimeUtc(exe, writtenUtc);
            return exe;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    // ------------------------------------------------------------------ pieces

    private static string Describe(IInputElement? element) => element switch
    {
        null => "null",
        Button b => $"Button {b.Content}",
        Hyperlink h => $"Hyperlink {((Run)h.Inlines.FirstInline).Text}",
        ListBox => "ListBox",
        _ => element.GetType().Name,
    };

    /// <summary>The "ActivityItem" style of SettingsView.xaml: rows that show only their content (and aren't focusable).</summary>
    private static Style ActivityItemStyle(bool focusable)
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(FrameworkElement.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, focusable));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        return style;
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>A service whose every call throws the given exception (a library that can't answer right now). DispatchProxy needs it public and unsealed.</summary>
    public class ThrowingProxy : DispatchProxy
    {
        private Exception _error = new InvalidOperationException();

        public static T Create<T>(Exception error)
            where T : class
        {
            var proxy = Create<T, ThrowingProxy>();
            ((ThrowingProxy)(object)proxy)._error = error;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw _error;
    }

    /// <summary>An activity log that stores the details JSON as given (to feed entries another version might have written).</summary>
    private sealed class RawJsonActivityLog : IAiActivityLog
    {
        private readonly InMemoryActivityLog _store = new();

        public event EventHandler<ActivityEntry>? EntryAdded;

        public ActivityEntry Add(DateTimeOffset at, string client, string tool, string? detailsJson)
            => _store.Add(new ActivityEntry { At = at, Source = client, Action = tool, DetailsJson = detailsJson });

        public Task<ActivityEntry?> LogAsync(string client, string tool, AiActivityDetails details)
        {
            var entry = Add(DateTimeOffset.Now, client, tool, details.ToJson());
            EntryAdded?.Invoke(this, entry);
            return Task.FromResult<ActivityEntry?>(entry);
        }

        public Task<ActivityPage> GetAsync(ActivityQuery query, CancellationToken ct = default) => Task.FromResult(_store.Get(query));
    }
}
