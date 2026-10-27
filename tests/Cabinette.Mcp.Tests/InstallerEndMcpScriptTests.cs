using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cabinette.Mcp.Tests;

/// <summary>
/// installer/Cabinette.iss ends running copies of the installed cabinette-mcp.exe before Setup replaces the file and
/// before the uninstaller removes it (EndMcpScript, a Windows PowerShell script; installer/README.md says why not through
/// Restart Manager). These tests run that exact script the way Setup does, against real cabinette-mcp.exe processes
/// started from temp folders. They get a <c>--data-dir</c> of their own and an <c>--app</c> that doesn't exist, and no
/// tool call, so they only wait on stdin: nothing else is started, and only processes from the test's folders are ended.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class InstallerEndMcpScriptTests : IDisposable
{
    private const string ExeName = "cabinette-mcp.exe";

    private static readonly Lazy<string> Script = new(ReadScript);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "cabinette-endmcp-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    [Fact]
    public void The_script_has_no_double_quotes_so_Setup_can_pass_it_in_Command()
    {
        Assert.DoesNotContain('"', Script.Value);
    }

    [Fact]
    public void It_ends_only_the_copies_started_from_the_installed_file_and_the_file_can_then_be_replaced()
    {
        // A path like Program Files, with spaces, parentheses, a quote and an ampersand.
        var installed = Install(Path.Combine(_work, "Program Files (x86)", "Cabi'nette & co"));
        var devBuild = Install(Path.Combine(_work, "dev build"));
        var first = Start(installed);
        var second = Start(installed);
        var other = Start(devBuild);

        // Compared as Windows compares paths: the case doesn't matter.
        var (exitCode, output) = RunScript(installed.ToUpperInvariant());

        Assert.Equal(0, exitCode);
        Assert.True(first.WaitForExit(10_000), output);
        Assert.True(second.WaitForExit(10_000), output);
        Assert.False(other.HasExited, "A cabinette-mcp.exe from another folder must be left alone.\n" + output);
        Assert.Contains($"Ending {ExeName} (pid {first.Id})", output, StringComparison.Ordinal);
        Assert.Contains($"Ending {ExeName} (pid {second.Id})", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"pid {other.Id})", output, StringComparison.Ordinal);

        // What Setup does next: replace the file, which a running copy would keep locked.
        File.Copy(Path.Combine(AppContext.BaseDirectory, ExeName), installed, overwrite: true);
    }

    [Fact]
    public void Nothing_running_is_a_success()
    {
        var installed = Install(Path.Combine(_work, "Cabinette"));

        var (exitCode, output) = RunScript(installed);

        Assert.Equal(0, exitCode);
        Assert.Contains("Not running: ", output, StringComparison.Ordinal);
    }

    /// <summary>Setup treats 9 like nothing to end (and logs it); only 1, "still running", asks to retry.</summary>
    [Fact]
    public void A_check_that_fails_exits_with_9()
    {
        var (exitCode, output) = RunScript(null);

        Assert.Equal(9, exitCode);
        Assert.Contains("Check failed: ", output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind; nothing else depends on it.
        }
    }

    /// <summary>Copies the test output's cabinette-mcp.exe with what it needs to run into a folder; returns the exe path.</summary>
    private static string Install(string folder)
    {
        Directory.CreateDirectory(folder);
        var source = AppContext.BaseDirectory;
        var deps = File.ReadAllText(Path.Combine(source, "cabinette-mcp.deps.json"));
        var files = Regex.Matches(deps, @"([A-Za-z0-9_.\-]+\.dll)""").Select(m => m.Groups[1].Value)
            .Concat([ExeName, "cabinette-mcp.dll", "cabinette-mcp.runtimeconfig.json", "cabinette-mcp.deps.json"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => File.Exists(Path.Combine(source, name)));
        foreach (var name in files)
        {
            File.Copy(Path.Combine(source, name), Path.Combine(folder, name));
        }

        return Path.Combine(folder, ExeName);
    }

    private Process Start(string exe)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(Path.Combine(_work, "data"));
        info.ArgumentList.Add("--app");
        info.ArgumentList.Add(Path.Combine(_work, "no-cabinette", "Cabinette.exe"));
        var process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} didn't start.");
        _started.Add(process);

        // It is running and waiting for MCP messages on stdin (a wrong command line would end it at once).
        if (process.WaitForExit(1_000))
        {
            Assert.Fail($"{exe} ended at once: {process.StandardError.ReadToEnd()}");
        }

        return process;
    }

    /// <summary>Runs the script as Setup's EndMcpServers does: Windows PowerShell, the path in CABINETTE_MCP_EXE.</summary>
    private static (int ExitCode, string Output) RunScript(string? installedExe)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" + Script.Value + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment.Remove("CABINETTE_MCP_EXE");
        if (installedExe is not null)
        {
            info.Environment["CABINETTE_MCP_EXE"] = installedExe;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("powershell.exe didn't start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The script didn't finish within 60 s.");
        return (process.ExitCode, stdout + stderr.Result);
    }

    /// <summary>The script text, from the string literals of EndMcpScript in installer/Cabinette.iss.</summary>
    private static string ReadScript()
    {
        var iss = File.ReadAllText(Path.Combine(TestTools.RepositoryRoot(), "installer", "Cabinette.iss"));
        var function = Regex.Match(iss, @"(?s)function EndMcpScript: String;\s*begin\s*Result :=(.*?);\s*end;");
        Assert.True(function.Success, "installer/Cabinette.iss has no function EndMcpScript.");
        var script = new System.Text.StringBuilder();
        foreach (var line in function.Groups[1].Value.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match literal in Regex.Matches(text, "'((?:[^']|'')*)'"))
            {
                script.Append(literal.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal));
            }
        }

        return script.ToString();
    }
}
