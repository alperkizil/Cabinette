using System.IO;
using System.Text.RegularExpressions;
using Cabinette.App.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// installer/Cabinette.iss registers the Explorer entries and the COM classes that the app serves (SPEC section 9).
/// These tests read the script text, so the verb names, class ids and Run-key names can't drift from the app's.
/// </summary>
public class InstallerScriptTests
{
    private const string AppExe = "\"{app}\\Cabinette.exe\"";

    private static readonly Lazy<string> ScriptText = new(() => File.ReadAllText(FindScript()));

    private static readonly Lazy<IReadOnlyList<RegistryEntry>> RegistryEntries = new(ParseRegistrySection);

    public static TheoryData<string, string, Guid> Verbs => new()
    {
        { ExplorerIntegration.CopyVerb, "Copy to Cabinette", ExplorerIntegration.CopyDropTargetClsid },
        { ExplorerIntegration.PointerVerb, "Add to Cabinette (Pointer)", ExplorerIntegration.PointerDropTargetClsid },
    };

    public static TheoryData<Guid> Classes => new()
    {
        ExplorerIntegration.CopyDropTargetClsid,
        ExplorerIntegration.PointerDropTargetClsid,
    };

    [Fact]
    public void The_defines_match_ExplorerIntegration()
    {
        Assert.Equal(ExplorerIntegration.CopyVerb, Define("CopyVerb"));
        Assert.Equal(ExplorerIntegration.PointerVerb, Define("PointerVerb"));
        Assert.Equal(ExplorerIntegration.CopyDropTargetClsid, Guid.Parse(Define("CopyClsid")));
        Assert.Equal(ExplorerIntegration.PointerDropTargetClsid, Guid.Parse(Define("PointerClsid")));
    }

    /// <summary>
    /// The bundled fonts' licenses (SIL OFL 1.1 asks for them to travel with the fonts) are in the publish folder's
    /// licenses subfolder; Setup installs the publish folder with its subfolders, so they arrive next to Cabinette.exe.
    /// </summary>
    [Fact]
    public void Setup_installs_the_font_licenses_with_the_program()
    {
        var files = Regex.Match(ScriptText.Value, @"^\[Files\]\s*$(?<body>.*?)^\[", RegexOptions.Multiline | RegexOptions.Singleline).Groups["body"].Value;
        var publish = Assert.Single(files.Split('\n'), line => line.TrimStart().StartsWith(@"Source: ""{#PublishDir}\*""", StringComparison.Ordinal));
        Assert.Contains("recursesubdirs", publish, StringComparison.Ordinal);
        Assert.Contains("createallsubdirs", publish, StringComparison.Ordinal);

        var project = File.ReadAllText(Path.Combine(Path.GetDirectoryName(FindScript())!, "..", "src", "Cabinette.App", "Cabinette.App.csproj"));
        foreach (var font in new[] { "Inter", "SourceSans3", "AtkinsonHyperlegibleNext", "Lexend", "JetBrainsMono" })
        {
            Assert.Matches($@"Link=""licenses\\{font}-OFL\.txt""[^>]*CopyToPublishDirectory=""PreserveNewest""", project);
        }
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void Each_verb_is_a_classic_menu_entry_with_a_drop_target(string verb, string text, Guid clsid)
    {
        var key = $@"Software\Classes\AllFilesystemObjects\shell\{verb}";

        Assert.Contains("uninsdeletekey", Single(key, valueName: null).Flags);
        Assert.Equal(text, Single(key, "MUIVerb").ValueData);
        Assert.Equal(AppExe + ",0", Single(key, "Icon").ValueData);
        Assert.Equal("Player", Single(key, "MultiSelectModel").ValueData);
        Assert.Equal(Braced(clsid), Single(key + @"\DropTarget", "Clsid").ValueData);
    }

    /// <summary>In both registry views: WOW64 redirects HKLM\Software\Classes\CLSID, and 32-bit file dialogs show the entries too.</summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void Each_class_is_served_by_Cabinette_exe(Guid clsid)
    {
        var key = $@"Software\Classes\CLSID\{Braced(clsid)}";

        foreach (var root in new[] { "HKLM", "HKLM32" })
        {
            Assert.Contains("uninsdeletekey", Single(key, valueName: null, root).Flags);
            Assert.False(string.IsNullOrWhiteSpace(Single(key, "", root).ValueData));
            Assert.Equal(AppExe, Single(key + @"\LocalServer32", "", root).ValueData);
        }
    }

    [Fact]
    public void Everything_goes_to_HKLM_and_nothing_else_is_registered()
    {
        string[] verbs =
        [
            $@"Software\Classes\AllFilesystemObjects\shell\{ExplorerIntegration.CopyVerb}",
            $@"Software\Classes\AllFilesystemObjects\shell\{ExplorerIntegration.PointerVerb}",
        ];
        string[] classes =
        [
            $@"Software\Classes\CLSID\{Braced(ExplorerIntegration.CopyDropTargetClsid)}",
            $@"Software\Classes\CLSID\{Braced(ExplorerIntegration.PointerDropTargetClsid)}",
        ];

        static bool Under(string subkey, IEnumerable<string> keys) => keys.Any(key =>
            subkey.Equals(key, StringComparison.OrdinalIgnoreCase) || subkey.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase));

        // The verb keys are shared by both views; only the class keys also go to the 32-bit view.
        Assert.All(RegistryEntries.Value, entry => Assert.True(
            entry.Root == "HKLM" ? Under(entry.Subkey, verbs.Concat(classes)) : entry.Root == "HKLM32" && Under(entry.Subkey, classes),
            $"{entry.Root}\\{entry.Subkey}"));
    }

    [Fact]
    public void Dev_run_verbs_and_classes_are_never_installed()
    {
        var text = ScriptText.Value;
        Assert.DoesNotContain(ExplorerIntegration.DevCopyVerb, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExplorerIntegration.DevPointerVerb, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExplorerIntegration.DevCopyDropTargetClsid.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExplorerIntegration.DevPointerDropTargetClsid.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_uninstaller_removes_the_apps_own_startup_value_and_setup_never_writes_it()
    {
        Assert.Equal(StartupCommand.RunKeyPath, Define("RunKey"));
        Assert.Equal(StartupCommand.ValueName, Define("RunValue"));
        Assert.Contains("RegDeleteValue(HKCU, '{#RunKey}', '{#RunValue}')", ScriptText.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(RegistryEntries.Value, entry => entry.Subkey.Contains(@"CurrentVersion\Run", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ script parsing

    private sealed record RegistryEntry(string Root, string Subkey, string? ValueName, string? ValueData, string Flags);

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

    private static string Define(string name)
    {
        var match = Regex.Match(ScriptText.Value, $@"^#define\s+{name}\s+""([^""]*)""\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"installer/Cabinette.iss has no #define {name}.");
        return match.Groups[1].Value;
    }

    private static string Braced(Guid clsid) => clsid.ToString("B").ToUpperInvariant();

    private static RegistryEntry Single(string subkey, string? valueName, string root = "HKLM")
    {
        var matches = RegistryEntries.Value
            .Where(e => e.Root == root && e.Subkey.Equals(subkey, StringComparison.OrdinalIgnoreCase) && e.ValueName == valueName)
            .ToList();
        Assert.True(matches.Count == 1, $"Expected one [Registry] entry for {root}\\{subkey} / {valueName ?? "(key)"}, found {matches.Count}.");
        return matches[0];
    }

    /// <summary>
    /// The [Registry] lines with the script's #define values filled in (like the preprocessor) and Inno's "{{" escape
    /// undone, so subkeys and values read as they end up in the registry ({app} stays a constant).
    /// </summary>
    private static List<RegistryEntry> ParseRegistrySection()
    {
        var text = ScriptText.Value;
        var section = Regex.Match(text, @"^\[Registry\]\s*$(.*?)(?=^\[)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(section.Success, "installer/Cabinette.iss has no [Registry] section.");

        var entries = new List<RegistryEntry>();
        foreach (var raw in section.Groups[1].Value.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            line = Regex.Replace(line, @"\{#(\w+)\}", m => Define(m.Groups[1].Value));
            var parameters = Regex.Matches(line, @"(\w+):\s*(""(?:[^""]|"""")*""|[^;]*)")
                .ToDictionary(m => m.Groups[1].Value, m => Unquote(m.Groups[2].Value.Trim()), StringComparer.OrdinalIgnoreCase);

            entries.Add(new RegistryEntry(
                parameters["Root"],
                parameters["Subkey"],
                parameters.GetValueOrDefault("ValueName"),
                parameters.GetValueOrDefault("ValueData"),
                parameters.GetValueOrDefault("Flags") ?? ""));
        }

        return entries;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return value.Replace("{{", "{", StringComparison.Ordinal);
    }
}
