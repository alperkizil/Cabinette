using System.Diagnostics;
using System.IO;
using System.Text;

namespace Cabinette.App.Tests;

/// <summary>
/// Lets Windows PowerShell or cmd.exe parse a command as the user would paste it, and returns the arguments the program
/// receives. The program name (the first word, "claude" or "codex") is replaced by the full path of a tiny echo program
/// that writes its arguments to a file, so the real Claude Code or Codex never runs and nothing is registered anywhere.
/// The echo program is compiled once per test run with the .NET Framework's own C# compiler (part of Windows) into a
/// temp folder; it parses its command line the Windows way, like claude.exe and codex.exe.
/// </summary>
internal static class ShellEcho
{
    private static readonly Lazy<string> EchoExe = new(CompileEcho);

    /// <summary>The arguments the program gets when <paramref name="command"/> is pasted into Windows PowerShell 5.1.</summary>
    public static IReadOnlyList<string> PowerShell(string command)
    {
        var rest = Rest(command);
        var script = "& '" + EchoExe.Value.Replace("'", "''", StringComparison.Ordinal) + "' " + rest;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return Run(info, command);
    }

    /// <summary>The arguments the program gets when <paramref name="command"/> is typed at a cmd.exe prompt.</summary>
    public static IReadOnlyList<string> Cmd(string command)
    {
        var rest = Rest(command);
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        // /d: no AutoRun commands. /s /c "…": cmd removes only the outer quotes and runs the line as typed at its prompt
        // (where %NAME% of a defined variable expands, also inside quotes). Arguments is passed on verbatim.
        var info = new ProcessStartInfo(cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "/d /s /c \"\"" + EchoExe.Value + "\" " + rest + "\"",
        };
        return Run(info, command);
    }

    /// <summary>The command after its program name.</summary>
    private static string Rest(string command)
    {
        var space = command.IndexOf(' ', StringComparison.Ordinal);
        Assert.True(space > 0, $"No program name in: {command}");
        var program = command[..space];
        Assert.True(program is "claude" or "codex", $"Unexpected program: {program}");
        return command[(space + 1)..];
    }

    private static IReadOnlyList<string> Run(ProcessStartInfo info, string command)
    {
        var output = Path.Combine(Path.GetDirectoryName(EchoExe.Value)!, Guid.NewGuid().ToString("N") + ".txt");
        info.Environment["CABINETTE_ECHO_OUT"] = output;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "The shell didn't finish.");
        Assert.True(File.Exists(output), $"The echo program didn't run for: {command}\n{stdout.Result}\n{stderr.Result}");
        try
        {
            return File.ReadAllText(output, Encoding.UTF8).Split('\n');
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string CompileEcho()
    {
        const string Source = """
            using System;
            using System.IO;
            using System.Text;

            static class Echo
            {
                static int Main(string[] args)
                {
                    File.WriteAllText(Environment.GetEnvironmentVariable("CABINETTE_ECHO_OUT"), string.Join("\n", args), new UTF8Encoding(false));
                    return 0;
                }
            }
            """;
        var folder = Path.Combine(Path.GetTempPath(), "cabinette-tests-echo-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // Best effort: a temp folder.
            }
        };
        var exe = Path.Combine(folder, "echo-args.exe");
        if (File.Exists(exe))
        {
            return exe;
        }

        var source = Path.Combine(folder, "echo-args.cs");
        File.WriteAllText(source, Source);
        var csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        Assert.True(File.Exists(csc), $"The .NET Framework C# compiler isn't at {csc}.");
        var info = new ProcessStartInfo(csc) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = folder };
        info.ArgumentList.Add("-nologo");
        info.ArgumentList.Add("-out:" + exe);
        info.ArgumentList.Add(source);
        using var process = Process.Start(info)!;
        var log = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)) && process.ExitCode == 0 && File.Exists(exe), "Compiling the echo program failed: " + log);
        return exe;
    }
}
