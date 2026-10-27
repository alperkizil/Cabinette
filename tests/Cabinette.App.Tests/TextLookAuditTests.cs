using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Cabinette.App.Services;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>
/// The real app, every piece of text it shows: Cabinette.exe renders a state off-screen with sample data (--sample-data
/// --screenshot: never the user's library, nothing on screen, no Run key) and lists each text with its weight and font
/// (CABINETTE_TEXT_AUDIT, see ScreenshotRunner). This is where WPF-UI's own styles, the Application-level resources and
/// the dialogs that only exist inside the main window take part, which the in-process tests can't build.
/// </summary>
public class TextLookAuditTests
{
    /// <summary>
    /// Bold text on draws every text one step heavier than off, nothing missed: the chat list, the Settings dialog, the
    /// Gallery with a file's context menu, a chat thread (drilled into Work) and a tooltip. Turned the other way live (-boldswitch), the app draws
    /// exactly what a clean start with that setting draws.
    /// </summary>
    [Theory]
    [InlineData("settings-appearance", "off")]
    [InlineData("thread-menu:images", "on")]
    [InlineData("drilldown", "off")]
    [InlineData("gallery-tooltip", "on")]
    public async Task Bold_text_reaches_every_text_and_switches_live(string state, string switchFrom)
    {
        var runs = await Task.WhenAll(
            RenderAsync(state, "--bold-text", "off"),
            RenderAsync(state, "--bold-text", "on"),
            RenderAsync(state + "-boldswitch", "--bold-text", switchFrom));
        var (off, on, switched) = (runs[0], runs[1], runs[2]);

        Assert.True(off.Count > 15, $"only {off.Count} texts");
        Assert.Contains(off, t => t.Weight == FontWeights.Normal);
        Assert.Contains(off, t => t.Weight == FontWeights.SemiBold);
        AssertSameTexts(off, on);
        for (var i = 0; i < off.Count; i++)
        {
            Assert.True(UiWeight.Map(off[i].Weight, bold: true) == on[i].Weight, $"\"{off[i].Text}\" ({off[i].Kind}): off {off[i].Weight}, on {on[i].Weight}");
        }

        var expected = switchFrom == "off" ? on : off;
        AssertSameTexts(expected, switched);
        Assert.Equal(expected.Select(t => t.Weight), switched.Select(t => t.Weight));
    }

    /// <summary>
    /// A font reaches every text the same way (its own family for titles too, or the default's title fallback while a Windows
    /// font is missing): the chat list, Settings, the Gallery with a context menu, a chat thread and a tooltip. The exception
    /// is the Font list itself, where each name is drawn in its own font.
    /// </summary>
    [Theory]
    [InlineData("settings-appearance", "jetbrains", AppFont.JetBrainsMono)]
    [InlineData("thread-menu:images", "sitka", AppFont.Sitka)]
    [InlineData("drilldown", "inter", AppFont.Inter)]
    [InlineData("gallery-tooltip", "cascadia", AppFont.CascadiaMono)]
    public async Task The_font_reaches_every_text(string state, string flag, AppFont font)
    {
        var texts = await RenderAsync(state, "--font", flag, "--bold-text", "on");
        Assert.True(texts.Count > 15, $"only {texts.Count} texts");
        var families = new[] { UiFont.Get(font).Text.Source, UiFont.Get(font).Display.Source };
        var ownFonts = UiFont.All.ToDictionary(c => Localization.L.Get(c.NameKey), c => c.Text.Source);
        var others = texts.Where(t => !(ownFonts.TryGetValue(t.Text, out var own) && own == t.Font)).ToList();
        Assert.All(others, t => Assert.True(families.Contains(t.Font), $"\"{t.Text}\" ({t.Kind}) is in {t.Font}"));
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record AuditLine(string Kind, FontWeight Weight, string Font, string Text);

    private static readonly SemaphoreSlim Concurrency = new(3);

    /// <summary>Renders <paramref name="state"/> in Cabinette.exe off-screen and returns its text audit.</summary>
    internal static async Task<List<AuditLine>> RenderAsync(string state, params string[] flags)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Cabinette.exe");
        Assert.True(File.Exists(exe), $"{exe} is missing");
        var folder = Path.Combine(Path.GetTempPath(), "Cabinette-text-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await Concurrency.WaitAsync();
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--sample-data", "--screenshot", Path.Combine(folder, "shot.png"), "--screenshot-state", state }.Concat(flags))
            {
                start.ArgumentList.Add(argument);
            }

            var audit = Path.Combine(folder, "audit.txt");
            start.Environment["CABINETTE_TEXT_AUDIT"] = audit;
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Cabinette.exe didn't finish rendering {state}.");
            }

            Assert.True(process.ExitCode == 0, $"Cabinette.exe exited with {process.ExitCode} rendering {state}");
            return [.. File.ReadAllLines(audit).Select(Parse)];
        }
        finally
        {
            Concurrency.Release();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held for a moment: it is in the temp folder.
            }
        }
    }

    private static AuditLine Parse(string line)
    {
        var parts = line.Split('\t');
        return new AuditLine(parts[0], (FontWeight)new FontWeightConverter().ConvertFromInvariantString(parts[1])!, parts[2], parts[3]);
    }

    /// <summary>The same texts in the same order (digits aside: a clock may tick between two runs).</summary>
    internal static void AssertSameTexts(List<AuditLine> expected, List<AuditLine> actual)
    {
        static string Shape(AuditLine line) => $"{line.Kind}:{Regex.Replace(line.Text, @"\d", "#")}";
        Assert.Equal(expected.Select(Shape), actual.Select(Shape));
    }
}
