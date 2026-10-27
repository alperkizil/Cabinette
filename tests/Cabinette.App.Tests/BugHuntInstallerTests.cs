using System.IO;
using System.Text.RegularExpressions;
using Cabinette.App.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests from the bug hunt of the stage 3 installer (2026-10-21, docs/review/bughunt-2026-10-21-installer).
/// They failed on the reviewed branch and assert the fixed behavior.
/// </summary>
public class BugHuntInstallerTests
{
    private static readonly Lazy<string> ScriptText = new(() => File.ReadAllText(FindScript()));

    public static TheoryData<Guid> Classes => new()
    {
        ExplorerIntegration.CopyDropTargetClsid,
        ExplorerIntegration.PointerDropTargetClsid,
    };

    /// <summary>
    /// HKLM\Software\Classes\CLSID is one of the keys WOW64 redirects: a 32-bit process reads
    /// Software\Classes\WOW6432Node\CLSID instead. The verb keys (AllFilesystemObjects\shell\...) are shared, so a
    /// 32-bit host that shows the classic shell menu (the file dialogs of 32-bit programs, 32-bit file managers) shows
    /// "Copy to Cabinette" but can't find its DropTarget class. Out-of-process servers are normally registered in both
    /// views (Root: HKLM32 in Inno Setup).
    /// </summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void Each_class_is_also_registered_for_32bit_clients(Guid clsid)
    {
        var braced = clsid.ToString("B").ToUpperInvariant();
        var pattern = $@"^Root:\s*HKLM32;\s*Subkey:\s*""Software\\Classes\\CLSID\\\{{\{{\{{#\w+\}}\}}\\LocalServer32""";
        var lines = ScriptText.Value.Split('\n').Select(l => l.Trim()).ToList();
        var defines = Regex.Matches(ScriptText.Value, @"^#define\s+(\w+)\s+""([^""]*)""\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        var registered32 = lines.Any(line =>
        {
            if (!Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
            {
                return false;
            }

            var expanded = Regex.Replace(line, @"\{#(\w+)\}", m => defines.GetValueOrDefault(m.Groups[1].Value, ""));
            return expanded.Contains(braced, StringComparison.OrdinalIgnoreCase);
        });

        Assert.True(registered32, $"installer/Cabinette.iss registers CLSID {braced}\\LocalServer32 only in the 64-bit registry view (Root: HKLM); 32-bit shell hosts can't activate it.");
    }

    /// <summary>
    /// Setup and the uninstaller close Cabinette through Restart Manager, which sends the same message as a Windows
    /// sign-out. Ending the session used to quit even in the middle of a vault move or restore, leaving a half-restored
    /// library. Now it is refused like Quit in the tray, and Setup offers Retry.
    /// </summary>
    [Fact]
    public void Setup_or_sign_out_cannot_close_Cabinette_during_a_vault_operation()
    {
        var lifetime = new AppLifetime();
        var blocked = 0;
        lifetime.ExitBlocked += (_, _) => blocked++;

        using (lifetime.BeginOperation())
        {
            Assert.False(lifetime.TryEndSession());
            Assert.False(lifetime.IsQuitting);
            Assert.Equal(1, blocked);
        }

        Assert.True(lifetime.TryEndSession());
        Assert.True(lifetime.IsQuitting);
        Assert.Equal(1, blocked);
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cabinette.slnx")))
            {
                return Path.Combine(dir.FullName, "installer", "Cabinette.iss");
            }
        }

        throw new InvalidOperationException($"No Cabinette.slnx above {AppContext.BaseDirectory}.");
    }
}
