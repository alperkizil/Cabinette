using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cabinette.Mcp.Tests;

/// <summary>
/// Regression tests of the bug hunt of branch stage4-installer (0981ff2, 2026-10-24): the script in installer/Cabinette.iss
/// (EndMcpScript) that ends the installed cabinette-mcp.exe. They started as the reviewer's repro test, which asserted the
/// behavior found (F2); they now assert the fixed behavior. Like InstallerEndMcpScriptTests they run the real script against
/// cabinette-mcp.exe processes started from a temp folder of their own (a <c>--data-dir</c> and <c>--app</c> that exist
/// nowhere else, no tool call), and every process the script may end is one the test started.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class InstallerReviewTests : IDisposable
{
    private const string ExeName = "cabinette-mcp.exe";

    private static readonly Lazy<string> Script = new(ReadScript);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "cabinette-review-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];
    private readonly List<string> _junctions = [];

    /// <summary>
    /// F2. The script used to compare [IO.Path]::GetFullPath of each process's Win32_Process.ExecutablePath with the installed
    /// path, and to ask WMI only for processes named cabinette-mcp.exe. ExecutablePath is the path the process was started with,
    /// and a process started through an 8.3 short name is even named by it (CABINE~1.EXE), so a copy of the installed file that
    /// an AI client started as C:\PROGRA~1\Cabinette\CABINE~1.EXE was reported as "Not running" and left alive, and Setup then
    /// ran into "file in use". Now it also asks for 8.3 names and compares by file identity (volume serial number and file
    /// index), so the same file is found however its path is spelled; a copy in another folder is still left alone.
    /// </summary>
    [Fact]
    public void F2_A_copy_started_through_a_short_8_3_path_is_ended()
    {
        // Spaces and long names, so every folder (and the exe) has an 8.3 alias when the volume generates them.
        var installed = Install(Path.Combine(_work, "Program Files Lookalike", "Cabinette Install"));
        var shortPath = ShortPath(installed);
        if (string.Equals(shortPath, installed, StringComparison.OrdinalIgnoreCase))
        {
            // 8.3 names are turned off on this volume (fsutil 8dot3name): the case can't be shown here.
            return;
        }

        var process = Start(shortPath);
        var other = Start(Install(Path.Combine(_work, "dev build")));

        // What Setup passes: {app}\cabinette-mcp.exe, the long path.
        var (exitCode, output) = RunScript(installed);

        AssertEndedOnlyThat(process, other, exitCode, output, installed);
        Assert.Contains($" as {shortPath}", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>F2, the same for a copy started through a junction to the installation folder.</summary>
    [Fact]
    public void F2_A_copy_started_through_a_junction_is_ended()
    {
        var folder = Path.Combine(_work, "Program Files Lookalike", "Cabinette Install");
        var installed = Install(folder);
        var link = Junction(Path.Combine(_work, "link"), folder);

        var process = Start(Path.Combine(link, ExeName));
        var other = Start(Install(Path.Combine(_work, "dev build")));

        var (exitCode, output) = RunScript(installed);

        AssertEndedOnlyThat(process, other, exitCode, output, installed);
    }

    /// <summary>F2, the other way round: the installation path itself reaches the file through a junction or a short name.</summary>
    [Fact]
    public void F2_A_copy_started_by_its_long_path_is_found_when_the_installation_path_is_spelled_otherwise()
    {
        var folder = Path.Combine(_work, "Program Files Lookalike", "Cabinette Install");
        var installed = Install(folder);
        var link = Junction(Path.Combine(_work, "link"), folder);
        var other = Start(Install(Path.Combine(_work, "dev build")));

        var first = Start(installed);
        var (exitCode, output) = RunScript(Path.Combine(link, ExeName));
        AssertEndedOnlyThat(first, other, exitCode, output, installed);

        var shortPath = ShortPath(installed);
        if (!string.Equals(shortPath, installed, StringComparison.OrdinalIgnoreCase))
        {
            var second = Start(installed);
            (exitCode, output) = RunScript(shortPath);
            AssertEndedOnlyThat(second, other, exitCode, output, installed);
        }
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

        // The junctions themselves first (removing a junction never touches its target).
        foreach (var junction in _junctions)
        {
            try
            {
                Directory.Delete(junction);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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

    // ------------------------------------------------------------------ helpers (as in InstallerEndMcpScriptTests)

    /// <summary>The script ended exactly <paramref name="ended"/> and left <paramref name="other"/> (another copy) alone, and
    /// the installed file can then be replaced, which is what Setup does next.</summary>
    private static void AssertEndedOnlyThat(Process ended, Process other, int exitCode, string output, string installed)
    {
        Assert.Equal(0, exitCode);
        Assert.True(ended.WaitForExit(10_000), "The copy of the installed file should have been ended.\n" + output);
        Assert.Contains($"(pid {ended.Id})", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Not running: ", output, StringComparison.Ordinal);
        Assert.False(other.HasExited, "A cabinette-mcp.exe from another folder must be left alone.\n" + output);
        Assert.DoesNotContain($"pid {other.Id})", output, StringComparison.Ordinal);
        File.Copy(Path.Combine(AppContext.BaseDirectory, ExeName), installed, overwrite: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);

    /// <summary>The 8.3 form of an existing path (the path itself where the volume has no short names).</summary>
    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        Assert.True(length > 0 && length < buffer.Length, $"GetShortPathName failed: {Marshal.GetLastWin32Error()}");
        return new string(buffer, 0, (int)length);
    }

    /// <summary>Creates a directory junction (mklink /J, no administrator rights needed, no window).</summary>
    private string Junction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("cmd.exe didn't start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "mklink didn't finish.");
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {output}");
        _junctions.Add(link);
        Assert.True(File.Exists(Path.Combine(link, ExeName)), $"The junction {link} doesn't reach {target}.");
        return link;
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

        if (process.WaitForExit(1_000))
        {
            Assert.Fail($"{exe} ended at once: {process.StandardError.ReadToEnd()}");
        }

        return process;
    }

    /// <summary>Runs the script as Setup's EndMcpServers does: Windows PowerShell, the path in CABINETTE_MCP_EXE.</summary>
    private static (int ExitCode, string Output) RunScript(string installedExe)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" + Script.Value + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["CABINETTE_MCP_EXE"] = installedExe;

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
