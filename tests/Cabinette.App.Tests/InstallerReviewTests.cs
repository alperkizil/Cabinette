using System.IO;
using System.Text.RegularExpressions;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests of the bug hunt of branch stage4-installer (0981ff2, 2026-10-24): cabinette-mcp.exe in the installer,
/// its build and the docs. They started as the reviewer's repro tests, which asserted the behavior found (F1-F5); each now
/// asserts the fixed behavior and keeps its finding's number (F3 stays a record of the mismatch, skipped). The facts about
/// Inno Setup's order of events come from its source (jrsoftware/issrc at is-7_1_0, Projects/Src/Setup.MainForm.pas,
/// Setup.MainFunc.pas, Setup.Install.pas and Setup.Install.HelperFunc.pas, read on 2026-10-24); the version pinned here is
/// 7.1.0 (installer/InnoSetup.csproj).
/// </summary>
public class InstallerReviewTests
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<string> Iss = new(() => Read(@"installer\Cabinette.iss"));

    /// <summary>
    /// F1. Setup used to end the MCP servers from CurStepChanged(ssInstall), which runs before PerformInstall (TMainForm.Install
    /// in Setup.MainForm.pas: SetStep(ssInstall, False), then PerformInstall), so before Restart Manager's ShutdownApplications
    /// asks Cabinette to close (inside PerformInstall, Setup.Install.pas). When Cabinette refused (a vault operation runs), the
    /// AI client had lost its server and Setup stopped anyway. Now a BeforeInstall procedure on the first [Files] entry ends
    /// them: PerformInstall runs ShutdownApplications, then [InstallDelete], [Dirs] and only then [Files], so it runs after
    /// Cabinette has closed and before any file is replaced. (The uninstaller's order, Cabinette first, was right and stays.)
    /// </summary>
    [Fact]
    public void F1_Setup_ends_the_MCP_servers_after_Restart_Manager_closed_Cabinette_and_before_any_file()
    {
        // Not from CurStepChanged any more (it runs before Restart Manager closes Cabinette).
        Assert.DoesNotContain("procedure CurStepChanged", Iss.Value, StringComparison.Ordinal);

        // The first [Files] entry is cabinette-mcp.exe's own, and it ends the running copies before it is installed.
        var files = SectionLines("Files");
        Assert.NotEmpty(files);
        Assert.StartsWith(@"Source: ""{#PublishDir}\{#McpExe}"";", files[0], StringComparison.Ordinal);
        Assert.EndsWith("BeforeInstall: EndMcpServersBeforeFiles", files[0], StringComparison.Ordinal);
        Assert.DoesNotContain("*", files[0], StringComparison.Ordinal);   // one file: BeforeInstall runs once
        Assert.Single(files, line => line.Contains("BeforeInstall:", StringComparison.Ordinal));
        Assert.Contains("EndMcpServersOrAsk", Body("procedure EndMcpServersBeforeFiles;"), StringComparison.Ordinal);

        // Nothing replaces or deletes a file between Restart Manager and that entry.
        Assert.Empty(SectionLines("InstallDelete"));

        // The docs describe this order.
        var readme = Read(@"installer\README.md");
        Assert.DoesNotContain("after Cabinette itself has closed", readme, StringComparison.Ordinal);
        Assert.Contains("BeforeInstall", readme, StringComparison.Ordinal);
        Assert.Contains("\"Closing applications...\"", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// F1, second effect. Cancel in the "still running" box used to raise Abort inside CurStepChanged(ssInstall), which Setup
    /// re-raises as a fatal error (exit code 3, ecNextStepError, nothing rolled back). From a BeforeInstall procedure Abort
    /// wouldn't stop Setup at all: NotifyInstallEntry (Setup.MainFunc.pas) catches every exception there, shows it and goes on.
    /// So Setup now cancels the way its Cancel button does: WizardForm.Close, which TMainForm.Close turns into
    /// NeedToAbortInstall on the installing page. The next progress update raises EAbort, and PerformInstall logs "User
    /// canceled the installation process.", exits with code 5 and rolls back. CancelButtonClick skips the second "Exit
    /// Setup?" question for that request only. The uninstaller still uses Abort, which stops it in usUninstall.
    /// </summary>
    [Fact]
    public void F1_Cancel_at_the_MCP_box_cancels_Setup_with_rollback()
    {
        var beforeFiles = Body("procedure EndMcpServersBeforeFiles;");
        Assert.Matches(@"if\s+not\s+EndMcpServersOrAsk\s+then\s+CancelInstallation;", beforeFiles);
        Assert.DoesNotContain("Abort", beforeFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseException", beforeFiles, StringComparison.Ordinal);

        // The box itself only reports the choice.
        var ask = Body("function EndMcpServersOrAsk: Boolean;");
        Assert.Contains("MB_RETRYCANCEL, IDCANCEL", ask, StringComparison.Ordinal);
        Assert.DoesNotContain("Abort", ask, StringComparison.Ordinal);

        var cancel = Body("procedure CancelInstallation;");
        Assert.Contains("CancelRequested := True;", cancel, StringComparison.Ordinal);
        Assert.Contains("WizardForm.Close;", cancel, StringComparison.Ordinal);
        Assert.Contains("CancelRequested := False;", cancel, StringComparison.Ordinal);
        Assert.DoesNotContain("Abort", cancel, StringComparison.Ordinal);

        var click = Body("procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);");
        Assert.Matches(@"if\s+CancelRequested\s+then", click);
        Assert.Contains("Confirm := False;", click, StringComparison.Ordinal);
        Assert.Contains("CancelAccepted := Cancel;", click, StringComparison.Ordinal);

        var uninstall = Body("procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);");
        Assert.Matches(@"if\s+not\s+EndMcpServersOrAsk\s+then\s+Abort;", uninstall);
        Assert.DoesNotContain("(Setup then rolls back, the uninstaller stops)", Iss.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// F3. The README's developer registration and the one Settings > AI shows for a dev run disagree. README.md: local
    /// scope (no --scope user) and an --app pointing at the Debug Cabinette.exe. AiConnectCommands.Build (branch
    /// worktree-agent-add1d28fb67aed923, src/Cabinette.App/Services/Ai/AiConnectCommands.cs) for a dev run:
    /// "claude mcp add --scope user cabinette-dev -- &lt;exe&gt; --data-dir &lt;dir&gt;", no --app. A developer who uses both ends
    /// up with two "cabinette-dev" registrations in two scopes (the local one shadows the user one), and the Settings one
    /// can't start the dev Cabinette when it isn't running: a Debug cabinette-mcp.exe has no Cabinette.exe next to itself.
    /// The README is the reference; Settings > AI is aligned to it on branch fix/ai-ui. Kept as the record of the mismatch;
    /// the integrator replaces it with a cross-check of both once the branches are merged. The README side is pinned by
    /// F3_The_readme_dev_registration_is_the_full_command_with_full_paths.
    /// </summary>
    [Fact]
    public void F3_The_readme_dev_registration_is_the_one_Settings_AI_shows()
    {
        var readme = Read("README.md");
        var line = Assert.Single(readme.Split('\n'), l => l.Contains("claude mcp add cabinette-dev", StringComparison.Ordinal)).Trim();

        // The README's example paths, fed to the same builder Settings > AI uses for a dev run.
        var paths = System.Text.RegularExpressions.Regex.Matches(line, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, paths.Count);
        var settings = Cabinette.App.Services.Ai.AiConnectCommands.Build(paths[0], new Cabinette.App.Services.Ai.AiDevRun(paths[1], paths[2]));

        // Claude Code's local scope is its default, which the README leaves out.
        Assert.Equal(line, settings.ClaudeAdd.Replace(" --scope local", "", StringComparison.Ordinal));
        Assert.Contains("Without `--scope user`, the registration only applies to the folder you run it in", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// F3, the README side (the reference Settings > AI is aligned to): the whole dev command, in Claude Code's local scope,
    /// with the Debug build's exe and both --data-dir and --app as quoted full paths into the projects' real Debug output
    /// folders. cabinette-mcp.exe refuses relative ones (Claude Code starts it from whatever folder you're in).
    /// </summary>
    [Fact]
    public void F3_The_readme_dev_registration_is_the_full_command_with_full_paths()
    {
        var readme = Read("README.md");
        var line = Assert.Single(readme.Split('\n'), l => l.Contains("claude mcp add cabinette-dev", StringComparison.Ordinal)).Trim();
        var command = Regex.Match(line, @"^claude mcp add cabinette-dev -- ""(?<exe>[^""]+)"" --data-dir ""(?<data>[^""]+)"" --app ""(?<app>[^""]+)""$");
        Assert.True(command.Success, line);

        foreach (var path in new[] { command.Groups["exe"].Value, command.Groups["data"].Value, command.Groups["app"].Value })
        {
            Assert.True(Path.IsPathFullyQualified(path), $"Not a full path: {path}");
        }

        Assert.EndsWith($@"\src\Cabinette.Mcp\bin\Debug\{TargetFramework(@"src\Cabinette.Mcp\Cabinette.Mcp.csproj")}\cabinette-mcp.exe",
            command.Groups["exe"].Value, StringComparison.Ordinal);
        Assert.EndsWith($@"\src\Cabinette.App\bin\Debug\{TargetFramework(@"src\Cabinette.App\Cabinette.App.csproj")}\Cabinette.exe",
            command.Groups["app"].Value, StringComparison.Ordinal);

        Assert.Contains("Both options take full paths", readme, StringComparison.Ordinal);
        Assert.Contains("Without `--scope user`, the registration only applies to the folder you run it in", readme, StringComparison.Ordinal);
        Assert.Contains("claude mcp remove cabinette-dev", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// F4. The script's WMI query (Get-CimInstance Win32_Process) had no -OperationTimeoutSec, and Setup waits for the script
    /// with ewWaitUntilTerminated and no limit, so a WMI service that hangs (the Winmgmt repository or a stuck provider)
    /// hung Setup on the installing page. Every WMI query of the script now gives up after 30 s. The error that follows ends
    /// the script through its catch with exit code 9, which EndMcpServers logs and treats as nothing to end (only 1, "still
    /// running", asks to retry), so Setup goes on.
    /// </summary>
    [Fact]
    public void F4_The_end_script_gives_WMI_30_seconds_and_Setup_goes_on_after_a_timeout()
    {
        var script = Body("function EndMcpScript: String;");
        var queries = Regex.Matches(script, @"Get-CimInstance[^|]*");
        Assert.NotEmpty(queries);
        Assert.All(queries, query => Assert.Contains("-OperationTimeoutSec 30", query.Value, StringComparison.Ordinal));
        Assert.Contains("Wait-Process", script, StringComparison.Ordinal);
        Assert.Contains("-Timeout 5", script, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = ''Stop''", script, StringComparison.Ordinal);
        Assert.Contains("exit 9 }", script, StringComparison.Ordinal);

        // A failed or timed-out check is logged, and Setup goes on.
        var endServers = Body("function EndMcpServers: Boolean;");
        Assert.Contains("ewWaitUntilTerminated", endServers, StringComparison.Ordinal);
        Assert.Contains("Result := ResultCode <> 1;", endServers, StringComparison.Ordinal);
        Assert.Contains("Log(Format('Ending %s: exit code %d', [McpExe, ResultCode]));", endServers, StringComparison.Ordinal);
    }

    /// <summary>
    /// F5. check-install.ps1's default -InstallDir was Join-Path $env:ProgramFiles 'Cabinette', which is C:\Program Files
    /// (x86)\Cabinette in a 32-bit PowerShell host (SysWOW64\WindowsPowerShell, or a 32-bit program's embedded host), so the
    /// check reported the program files and cabinette-mcp.exe as missing on a correct installation. Now the default is where
    /// Setup put the files: the uninstall entry's InstallLocation, else $env:ProgramW6432 (the 64-bit Program Files folder in
    /// any host), else $env:ProgramFiles. The uninstall entry is read from the 64-bit registry view, where 64-bit Setup writes
    /// it: through HKLM:\ a 32-bit host would see the 32-bit view and miss it.
    /// </summary>
    [Fact]
    public void F5_check_install_looks_where_Setup_put_the_files_in_any_host()
    {
        var check = Read(@"installer\check-install.ps1");
        Assert.Contains("param([string]$InstallDir)", check, StringComparison.Ordinal);
        Assert.DoesNotContain("$InstallDir = (Join-Path $env:ProgramFiles", check, StringComparison.Ordinal);

        // The uninstall entry, from the 64-bit view.
        Assert.Contains("[Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')", check, StringComparison.Ordinal);
        Assert.Contains("$installLocation = \"$($uninstall.GetValue('InstallLocation'))\"", check, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ItemProperty 'HKLM:", check, StringComparison.Ordinal);

        // InstallLocation first, then ProgramW6432, then ProgramFiles, and only when -InstallDir wasn't given.
        var choice = Regex.Match(check, @"if \(-not \$InstallDir\) \{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(choice.Success, "check-install.ps1 doesn't choose a default for -InstallDir.");
        var body = choice.Groups[1].Value;
        var location = body.IndexOf("if ($installLocation) { $InstallDir = $installLocation }", StringComparison.Ordinal);
        var w6432 = body.IndexOf("elseif ($env:ProgramW6432) { $InstallDir = Join-Path $env:ProgramW6432 'Cabinette' }", StringComparison.Ordinal);
        var programFiles = body.IndexOf("else { $InstallDir = Join-Path $env:ProgramFiles 'Cabinette' }", StringComparison.Ordinal);
        Assert.True(location >= 0 && w6432 > location && programFiles > w6432, body);

        // Chosen before the program files are looked for.
        Assert.True(check.IndexOf("if (-not $InstallDir)", StringComparison.Ordinal) < check.IndexOf("$appExe = Join-Path $InstallDir", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cabinette.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No Cabinette.slnx above {AppContext.BaseDirectory}.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root.Value, relative));

    private static string TargetFramework(string project)
    {
        var match = Regex.Match(Read(project), @"<TargetFramework>\s*([^<\s]+)\s*</TargetFramework>");
        Assert.True(match.Success, $"{project} has no TargetFramework.");
        return match.Groups[1].Value;
    }

    /// <summary>The non-comment lines of a script section (none when the section doesn't exist).</summary>
    private static List<string> SectionLines(string name)
    {
        var section = Regex.Match(Iss.Value, $@"^\[{name}\]\s*$(.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        return !section.Success
            ? []
            : section.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();
    }

    /// <summary>The text of a [Code] routine, from its header to its final "end;" at the start of a line.</summary>
    private static string Body(string header)
    {
        var start = Iss.Value.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"installer/Cabinette.iss has no {header}");
        var end = Regex.Match(Iss.Value[start..], @"^end;", RegexOptions.Multiline);
        Assert.True(end.Success, $"No end of {header}");
        return Iss.Value.Substring(start, end.Index);
    }
}
