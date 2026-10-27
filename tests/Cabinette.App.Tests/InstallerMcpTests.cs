using System.IO;
using System.Text.RegularExpressions;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// cabinette-mcp.exe in the installer, its build and the docs (SPEC.md section 10, installer/README.md): Setup ships it
/// next to Cabinette.exe and registers nothing for it, build.ps1 publishes it as one file and never the test helper,
/// and the registration commands in the docs use the server and exe names the code uses. These tests read the files,
/// so none of them can drift. The script that ends a running cabinette-mcp.exe is run for real in
/// Cabinette.Mcp.Tests (InstallerEndMcpScriptTests), which has a cabinette-mcp.exe to end.
/// </summary>
public class InstallerMcpTests
{
    private const string InstallDir = @"C:\Program Files\Cabinette";

    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<string> Iss = new(() => Read(@"installer\Cabinette.iss"));
    private static readonly Lazy<string> Build = new(() => Read(@"installer\build.ps1"));

    /// <summary>The program name the project builds: its AssemblyName plus ".exe".</summary>
    private static string McpExeName
    {
        get
        {
            var match = Regex.Match(Read(@"src\Cabinette.Mcp\Cabinette.Mcp.csproj"), @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>");
            Assert.True(match.Success, "src/Cabinette.Mcp/Cabinette.Mcp.csproj has no AssemblyName.");
            return match.Groups[1].Value + ".exe";
        }
    }

    /// <summary>The MCP server's name, CabinetteMcpServer.ServerName: what users register it as.</summary>
    private static string ServerName
    {
        get
        {
            var match = Regex.Match(Read(@"src\Cabinette.Mcp\CabinetteMcpServer.cs"), @"const string ServerName = ""([^""]+)""");
            Assert.True(match.Success, "CabinetteMcpServer.ServerName not found.");
            return match.Groups[1].Value;
        }
    }

    private static string InstalledMcp => $@"{InstallDir}\{McpExeName}";

    // ------------------------------------------------------------------ Cabinette.iss

    [Fact]
    public void The_installer_ships_the_exe_the_project_builds()
    {
        Assert.Equal("cabinette-mcp.exe", McpExeName);
        Assert.Equal(McpExeName, Define("McpExe"));
    }

    [Fact]
    public void Setup_installs_cabinette_mcp_next_to_Cabinette_exe()
    {
        var files = SectionLines("Files");

        // Named on its own (ISCC fails when the publish folder lacks it), into the same folder as everything else.
        var mcp = Assert.Single(files, line => Parameter(line, "Source") == @"{#PublishDir}\{#McpExe}");
        Assert.Equal("{app}", Parameter(mcp, "DestDir"));
        Assert.Null(Parameter(mcp, "DestName"));

        // The rest of the publish folder (Cabinette.exe among it) goes to {app} too, without a second copy of it.
        var app = Assert.Single(files, line => Parameter(line, "Source") == @"{#PublishDir}\*");
        Assert.Equal("{app}", Parameter(app, "DestDir"));
        Assert.Equal(@"\{#McpExe}", Parameter(app, "Excludes"));

        // Nothing keeps it on uninstall.
        Assert.All(files, line => Assert.DoesNotContain("uninsneveruninstall", line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Setup_registers_nothing_for_it_and_never_touches_the_AI_clients_settings()
    {
        foreach (var section in new[] { "Registry", "Run", "Icons", "INI", "UninstallRun", "UninstallDelete", "Tasks" })
        {
            Assert.All(SectionLines(section), line => Assert.DoesNotContain("mcp", line, StringComparison.OrdinalIgnoreCase));
        }

        // Claude Code keeps its MCP servers in %USERPROFILE%\.claude.json, Codex in %USERPROFILE%\.codex\config.toml.
        foreach (var forbidden in new[] { ".claude", ".codex", "config.toml", "mcp_servers", "mcp add", "mcp remove", "{%USERPROFILE", "{userprofile}" })
        {
            Assert.DoesNotContain(forbidden, Iss.Value, StringComparison.OrdinalIgnoreCase);
        }

        // Claude Code's JSON key (EndMcpServers, a routine of the script, differs in case).
        Assert.DoesNotContain("mcpServers", Iss.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restart Manager would close cabinette-mcp.exe with Ctrl+C, which reaches every process on its console (the AI
    /// client too), so it is excluded, and [Code] ends exactly the installed copies: in Setup from the BeforeInstall of the
    /// first [Files] entry (after Restart Manager closed Cabinette, before any file is replaced; InstallerReviewTests F1),
    /// in the uninstaller after Cabinette is closed.
    /// </summary>
    [Fact]
    public void A_running_cabinette_mcp_is_ended_by_Code_not_by_Restart_Manager()
    {
        var setup = SectionLines("Setup");
        Assert.Contains("CloseApplications=yes", setup);
        Assert.Contains("CloseApplicationsFilterExcludes={#McpExe}", setup);

        var endServers = Body("function EndMcpServers: Boolean;");
        Assert.Contains(@"ExpandConstant('{app}\{#McpExe}')", endServers, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariable('CABINETTE_MCP_EXE', McpExe)", endServers, StringComparison.Ordinal);
        Assert.Contains("EndMcpScript", endServers, StringComparison.Ordinal);
        Assert.Contains("$env:CABINETTE_MCP_EXE", Body("function EndMcpScript: String;"), StringComparison.Ordinal);

        Assert.Contains("BeforeInstall: EndMcpServersBeforeFiles", SectionLines("Files")[0], StringComparison.Ordinal);
        Assert.Contains("EndMcpServersOrAsk", Body("procedure EndMcpServersBeforeFiles;"), StringComparison.Ordinal);

        var uninstall = Body("procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);");
        var closeApp = uninstall.IndexOf(@"CloseProcessesUsing(ExpandConstant('{app}\Cabinette.exe'))", StringComparison.Ordinal);
        var endMcp = uninstall.IndexOf("EndMcpServersOrAsk", StringComparison.Ordinal);
        Assert.True(closeApp >= 0 && endMcp > closeApp, "The uninstaller must close Cabinette, then end cabinette-mcp.exe.");
    }

    // ------------------------------------------------------------------ build.ps1

    [Fact]
    public void Build_publishes_the_MCP_project_as_one_file_next_to_Cabinette_exe()
    {
        var build = Build.Value;
        Assert.Contains(@"$mcpProject = Join-Path $repo 'src\Cabinette.Mcp\Cabinette.Mcp.csproj'", build, StringComparison.Ordinal);
        Assert.Contains($"$mcpExe = '{McpExeName}'", build, StringComparison.Ordinal);
        Assert.Contains($"$mcpFiles = @($mcpExe, '{Path.ChangeExtension(McpExeName, ".pdb")}')", build, StringComparison.Ordinal);

        var arguments = Regex.Match(build, @"^\$mcpArguments = @\((.*)\)\s*$", RegexOptions.Multiline);
        Assert.True(arguments.Success, "build.ps1 has no $mcpArguments.");
        foreach (var expected in new[] { "'-c', 'Release'", "'-r', 'win-x64'", "'--self-contained', 'false'", "'-p:PublishSingleFile=true'" })
        {
            Assert.Contains(expected, arguments.Groups[1].Value, StringComparison.Ordinal);
        }

        Assert.Contains("@('build', $mcpProject, '--no-incremental') + $mcpArguments", build, StringComparison.Ordinal);
        Assert.Contains("@('publish', $mcpProject, '--no-build') + $mcpArguments + @('-o', $mcpPublishDir)", build, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $source -Destination $publishDir", build, StringComparison.Ordinal);

        // Warnings fail the build, and so does a publish that isn't a single file.
        Assert.Contains("$mcpWarnings = @(Select-BuildWarnings $mcpOutput)", build, StringComparison.Ordinal);
        Assert.Contains("was not published as a single file", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_never_publishes_the_test_helper()
    {
        var build = Build.Value;
        // The only projects (or solutions) it names at all.
        var projects = Regex.Matches(build, @"[\w.\\-]+\.(?:csproj|slnx|sln)\b")
            .Select(m => Path.GetFileName(m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Cabinette.App.csproj", "Cabinette.Mcp.csproj", "InnoSetup.csproj" }, projects);

        var built = Regex.Matches(build, @"@\('(?:publish|build)', \$(\w+)").Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "appProject", "mcpProject" }, built);

        // Publishing the MCP project can't pull the helper in: it isn't referenced, only allowed to see internals.
        Assert.DoesNotMatch(@"<ProjectReference[^>]*TestHelper", Read(@"src\Cabinette.Mcp\Cabinette.Mcp.csproj"));
        Assert.Contains("-Filter '*TestHelper*'", build, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ docs

    [Fact]
    public void The_docs_register_the_server_and_exe_names_the_code_uses()
    {
        var readme = Read("README.md");
        var installerReadme = Read(@"installer\README.md");
        var name = ServerName;
        Assert.Equal("cabinette", name);

        var claudeAdd = $"claude mcp add --scope user {name} -- \"{InstalledMcp}\"";
        var codexAdd = $"codex mcp add {name} -- \"{InstalledMcp}\"";
        var claudeRemove = $"claude mcp remove --scope user {name}";
        var codexRemove = $"codex mcp remove {name}";
        foreach (var command in new[] { claudeAdd, codexAdd, claudeRemove, codexRemove })
        {
            Assert.Contains(command, readme, StringComparison.Ordinal);
            Assert.Contains(command, installerReadme, StringComparison.Ordinal);
        }

        Assert.Contains($"/mcp reconnect {name}", readme, StringComparison.Ordinal);

        // Every registration line anywhere in the docs (SPEC's Settings > AI included) uses the same names.
        var lines = new[] { readme, installerReadme, Read("SPEC.md") }
            .SelectMany(text => text.Split('\n'))
            .Where(line => Regex.IsMatch(line, @"\b(?:claude|codex) mcp add\b"))
            .ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.Matches($@"mcp add (?:--scope user )?{name}(?:-dev)? -- ", line);
            Assert.Contains($@"\{McpExeName}", line, StringComparison.Ordinal);
        });
        Assert.All(
            new[] { readme, installerReadme }.SelectMany(text => Regex.Matches(text, @"/mcp reconnect (\S+)")).Select(m => m.Groups[1].Value.Trim('`', '.', ',', ')')),
            server => Assert.Equal(name, server));
    }

    [Fact]
    public void The_readme_table_lists_exactly_the_AI_tools()
    {
        var readme = Read("README.md");
        var section = Regex.Match(readme, @"^### What the AI can do\s*$(.*?)^###? ", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(section.Success, "README.md has no \"### What the AI can do\" section.");

        var listed = section.Groups[1].Value.Split('\n')
            .Where(line => line.TrimStart().StartsWith('|'))
            .SelectMany(line => Regex.Matches(line, @"`([a-z_]+)`").Select(m => m.Groups[1].Value));
        Assert.Equal(AiTools.All.Order(StringComparer.Ordinal), listed.Distinct().Order(StringComparer.Ordinal));
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

    private static string Define(string name)
    {
        var match = Regex.Match(Iss.Value, $@"^#define\s+{name}\s+""([^""]*)""\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"installer/Cabinette.iss has no #define {name}.");
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

    /// <summary>One "Name: value" parameter of a section line, quotes removed; null when the line doesn't have it.</summary>
    private static string? Parameter(string line, string name)
    {
        var match = Regex.Match(line, $@"(?:^|;)\s*{name}:\s*(""(?:[^""]|"""")*""|[^;]*)");
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : value;
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
