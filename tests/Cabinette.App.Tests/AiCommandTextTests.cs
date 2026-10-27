using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Cabinette.App.Services.Ai;
using Cabinette.App.Views.Dialogs;

namespace Cabinette.App.Tests;

/// <summary>
/// Settings › AI's command boxes wrap only between the arguments of a command, never inside a quoted path (they used to
/// end a line with "C: and start the next with \Program Files\…). Laid out in a hidden window with the boxes' font.
/// </summary>
public sealed class AiCommandTextTests
{
    private const string Exe = @"C:\Program Files\Cabinette\cabinette-mcp.exe";

    [Fact]
    public void A_command_wraps_before_its_quoted_path() => HiddenWpf.RunSta(() =>
    {
        var command = AiConnectCommands.Build(Exe, null).ClaudeAdd;
        var path = AiConnectCommands.Quote(Exe);
        var box = CommandBox();
        box.Width = Measure(box, path) + 40;
        Assert.True(box.Width < Measure(box, command));

        var lines = Lines(box, command);

        Assert.Equal(["claude mcp add --scope user cabinette -- ", path], lines);
    });

    [Fact]
    public void A_dev_command_wraps_only_between_its_arguments() => HiddenWpf.RunSta(() =>
    {
        var command = AiConnectCommands.Build(@"D:\src\Cabinette\cabinette-mcp.exe", new AiDevRun(@"C:\Temp\Cab dev", @"D:\src\Cabinette\Cabinette.exe")).ClaudeAdd;
        var box = CommandBox();
        box.Width = Measure(box, @"""D:\src\Cabinette\cabinette-mcp.exe"" ") + 20;

        var lines = Lines(box, command);

        Assert.Equal(command, string.Concat(lines));
        Assert.True(lines.Count >= 4);
        Assert.All(lines.SkipLast(1), line => Assert.EndsWith(" ", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(line.Count(c => c == '"') % 2 == 0, $"A line breaks inside quotes: {line}"));
    });

    /// <summary>A path wider than the whole box breaks after a folder name's backslash, never after just "C:\".</summary>
    [Fact]
    public void A_path_wider_than_the_box_breaks_after_a_folder_name() => HiddenWpf.RunSta(() =>
    {
        const string Long = @"C:\Users\you\Documents\Cabinette\src\Cabinette.Mcp\bin\Debug\net10.0-windows\cabinette-mcp.exe";
        var command = AiConnectCommands.Build(Long, null).CodexAdd;
        var box = CommandBox();
        box.Width = Measure(box, AiConnectCommands.Quote(Long)) * 0.6;

        var lines = Lines(box, command);

        Assert.Equal(command, string.Concat(lines));
        Assert.True(lines.Count >= 3);
        Assert.All(lines.SkipLast(1), line => Assert.True(line.EndsWith(' ') || line.EndsWith('\\'), $"Broken elsewhere: {line}"));
        Assert.All(lines, line => Assert.False(line.EndsWith(@"C:\", StringComparison.Ordinal), $"Ends with the drive: {line}"));
    });

    [Theory]
    [InlineData(@"claude mcp add --scope user cabinette -- ""C:\Program Files\Cabinette\cabinette-mcp.exe""")]
    [InlineData(@"codex mcp add cabinette -- ""C:\dev`$data\日本 ğüş\x.exe"" --data-dir ""C:\a b"" --app ""C:\`“q`”\Cabinette.exe""")]
    [InlineData("claude mcp remove cabinette-dev --scope local")]
    [InlineData("")]
    public void The_displayed_text_reads_back_as_the_command(string command)
    {
        Assert.Equal(command, AiCommandText.FromDisplay(AiCommandText.ForDisplay(command)));
        Assert.Equal(command, AiCommandText.FromDisplay(AiCommandText.ForDisplay(command, _ => true)));
    }

    /// <summary>Nothing is put inside a character: a letter and its combining accent, or a surrogate pair, stay together.</summary>
    [Fact]
    public void Nothing_is_put_inside_a_character()
    {
        var command = "codex mcp add x -- \"C:\\Zoe\u0301 \U0001F600\\x.exe\"";

        var display = AiCommandText.ForDisplay(command);

        Assert.Contains("e\u0301", display, StringComparison.Ordinal);
        Assert.Contains("\U0001F600", display, StringComparison.Ordinal);
        Assert.Equal(command, AiCommandText.FromDisplay(display));
    }

    [Fact]
    public void The_text_may_break_only_after_a_space_outside_quotes()
    {
        var display = AiCommandText.ForDisplay(@"codex mcp add x -- ""C:\a b\c.exe""");

        Assert.Equal(
            Join("codex") + " " + Join("mcp") + " " + Join("add") + " " + Join("x") + " " + Join("--") + " " + Join(@"""C:\a b\c.exe"""),
            display);

        // Too wide: also after "b\" (a folder name), not after "C:\".
        Assert.Equal(
            Join("--") + " " + Join(@"""C:\a b\") + AiCommandText.BreakOpportunity + Join(@"c.exe"""),
            AiCommandText.ForDisplay(@"-- ""C:\a b\c.exe""", _ => true));

        static string Join(string word) => string.Join(AiCommandText.WordJoiner, word.Select(c => c.ToString()));
    }

    /// <summary>A selection copied (or dragged) out of a box is the command's own text, without the invisible characters.</summary>
    [Fact]
    public void Copying_a_selection_leaves_the_invisible_characters_behind() => HiddenWpf.RunSta(() =>
    {
        var command = AiConnectCommands.Build(Exe, null).ClaudeAdd;
        var box = CommandBox();
        AiCommandText.SetCommand(box, command);
        Assert.NotEqual(command, box.Text);

        // What a TextBox puts on the clipboard: its selection as text, handed to the Copying handlers first.
        var data = new DataObject();
        data.SetData(DataFormats.Text, box.Text, true);
        data.SetData(DataFormats.UnicodeText, box.Text, true);
        box.RaiseEvent(new DataObjectCopyingEventArgs(data, isDragDrop: false));

        // (As strings: compared as objects, xUnit would use the culture's comparison, which ignores the invisible characters.)
        Assert.Equal(command, (string?)data.GetData(DataFormats.UnicodeText));
        Assert.Equal(command, (string?)data.GetData(DataFormats.Text));
    });

    /// <summary>SettingsView.xaml shows every command through AiCommandText, not as plain text.</summary>
    [Fact]
    public void Settings_show_every_command_through_AiCommandText()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace views = "clr-namespace:Cabinette.App.Views.Dialogs";
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Cabinette.App", "Views", "Dialogs", "SettingsView.xaml"));
        var boxes = XDocument.Load(path).Descendants(wpf + "TextBox")
            .Where(b => (string?)b.Attribute("FontFamily") == "{StaticResource CommandFont}" || (string?)b.Attribute("Style") == "{StaticResource SmallCommand}")
            .ToList();

        Assert.Equal(3, boxes.Count);
        Assert.All(boxes, b => Assert.Null(b.Attribute("Text")));
        Assert.Equal(
            ["{Binding Text}", "{Binding ClaudeRemove}", "{Binding CodexRemove}"],
            boxes.Select(b => (string?)b.Attribute(views + "AiCommandText.Command")));
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>A box like Settings › AI's: the command font, wrapping, no padding.</summary>
    private static TextBox CommandBox() => new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        IsReadOnly = true,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>The command's lines as the box wraps it (the invisible characters taken out).</summary>
    private static List<string> Lines(TextBox box, string command)
    {
        var root = new StackPanel();
        root.Children.Add(box);
        using var source = HiddenWpf.Source(root, 1000, 600);
        AiCommandText.SetCommand(box, command);
        root.UpdateLayout();
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(30));
        root.UpdateLayout();
        return [.. Enumerable.Range(0, box.LineCount).Select(i => AiCommandText.FromDisplay(box.GetLineText(i)))];
    }

    private static double Measure(TextBox box, string text)
        => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
}
