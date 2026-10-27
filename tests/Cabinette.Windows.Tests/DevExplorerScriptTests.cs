using System.IO;
using System.Text.RegularExpressions;

namespace Cabinette.Windows.Tests;

/// <summary>
/// The dev scripts in tools/explorer-dev are only read here, never run: they must use the dev ids from
/// <see cref="ExplorerIntegration"/>, never the installed ones, and write only to HKCU\Software\Classes.
/// </summary>
public sealed partial class DevExplorerScriptTests
{
    private static readonly string Register = File.ReadAllText(Path.Combine(ScriptFolder(), "Register-DevExplorerEntries.ps1"));
    private static readonly string Unregister = File.ReadAllText(Path.Combine(ScriptFolder(), "Unregister-DevExplorerEntries.ps1"));

    private static string Id(Guid clsid) => clsid.ToString("B").ToUpperInvariant();

    [Fact]
    public void Register_writes_the_dev_verbs_and_classes()
    {
        Assert.Contains($"Verb = '{ExplorerIntegration.DevCopyVerb}'", Register);
        Assert.Contains($"Verb = '{ExplorerIntegration.DevPointerVerb}'", Register);
        Assert.Contains($"Clsid = '{Id(ExplorerIntegration.DevCopyDropTargetClsid)}'", Register);
        Assert.Contains($"Clsid = '{Id(ExplorerIntegration.DevPointerDropTargetClsid)}'", Register);
        Assert.Contains("'Copy to Cabinette (dev)'", Register);
        Assert.Contains("'Add to Cabinette (Pointer, dev)'", Register);
        Assert.Contains("'MultiSelectModel' 'Player'", Register);
        Assert.Contains(@"\DropTarget"" 'Clsid'", Register);
        Assert.Contains(@"\LocalServer32"" ''", Register);
        Assert.Contains("--data-dir", Register);
    }

    [Fact]
    public void Unregister_removes_exactly_what_register_writes()
    {
        foreach (var key in new[]
                 {
                     $@"Software\Classes\AllFilesystemObjects\shell\{ExplorerIntegration.DevCopyVerb}",
                     $@"Software\Classes\AllFilesystemObjects\shell\{ExplorerIntegration.DevPointerVerb}",
                     $@"Software\Classes\CLSID\{Id(ExplorerIntegration.DevCopyDropTargetClsid)}",
                     $@"Software\Classes\CLSID\{Id(ExplorerIntegration.DevPointerDropTargetClsid)}",
                 })
        {
            Assert.Contains($"'{key}'", Unregister);
        }

        Assert.Contains("DeleteSubKeyTree", Unregister);
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("Unregister")]
    public void The_scripts_never_touch_the_installed_entries_or_other_hives(string which)
    {
        var script = which == "Register" ? Register : Unregister;

        Assert.DoesNotContain(ExplorerIntegration.CopyDropTargetClsid.ToString(), script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExplorerIntegration.PointerDropTargetClsid.ToString(), script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"'{ExplorerIntegration.CopyVerb}'", script);
        Assert.DoesNotContain($"'{ExplorerIntegration.PointerVerb}'", script);
        foreach (var hive in new[] { "LocalMachine", "HKLM", "ClassesRoot", "HKCR:", "Users]", "HKEY_" })
        {
            Assert.DoesNotContain(hive, script, StringComparison.OrdinalIgnoreCase);
        }

        // Every registry access goes through HKCU, and every key it names is under Software\Classes.
        var hives = RegistryHive().Matches(script).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(["CurrentUser"], hives);
        Assert.All(KeyLiteral().Matches(script), m => Assert.StartsWith(@"Software\Classes\", m.Groups[1].Value));
    }

    private static string ScriptFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cabinette.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "tools", "explorer-dev");
    }

    [GeneratedRegex(@"Microsoft\.Win32\.Registry\]::(\w+)")]
    private static partial Regex RegistryHive();

    [GeneratedRegex(@"[""'](Software\\[^""']*)[""']")]
    private static partial Regex KeyLiteral();
}
