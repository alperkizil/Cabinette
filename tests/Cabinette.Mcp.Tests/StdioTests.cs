using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cabinette.Ipc;

namespace Cabinette.Mcp.Tests;

/// <summary>
/// The real cabinette-mcp.exe (this Debug build) as a client starts it: stdout carries only MCP JSON, whatever happens,
/// and a wrong command line exits with code 2 without writing to stdout.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class StdioTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cabinette-mcp-stdio-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task NothingButMcpJsonReachesStdoutDuringASession()
    {
        var dataDir = Path.Combine(_temp, "data");
        await using var cabinette = new FakeCabinette(InstanceNames.ForCurrentUser(dataDir).PipeName, message => message.Tool switch
        {
            AiTools.Search => InstanceReply.WithResult(new SearchResult([], [new AiFile { Id = 1, Name = "Ödev.txt" }], false)),
            AiTools.ReadFile => InstanceReply.WithResult(new ReadFileResult { Kind = "text", Text = "Console.WriteLine(\"hi\");\n", Unit = "bytes" }),
            _ => InstanceReply.Failed(AiErrorCodes.Busy, "A vault operation is running."),
        });

        using var process = Start("--data-dir", dataDir, "--app", Path.Combine(_temp, "missing", "Cabinette.exe"));
        var stderr = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();

        try
        {
            await SendAsync(process, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","title":"Claude Code","version":"2.1.306"}}}""");
            await ReadUntilResponseAsync(process, lines, 1);

            await SendAsync(process, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search","arguments":{"query":"ödev"}}}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_file","arguments":{"fileId":1}}}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"delete_files","arguments":{"fileIds":[1]}}}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_files","arguments":{"fileIds":["x"]}}}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""");
            await SendAsync(process, "this line is not JSON");
            await SendAsync(process, """{"jsonrpc":"2.0","id":8,"method":"ping"}""");
            await ReadUntilResponseAsync(process, lines, 2, 3, 4, 5, 6, 7, 8);

            // The client goes away: the server ends by itself.
            process.StandardInput.Close();
            lines.AddRange((await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20))).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.True(process.WaitForExit(20_000), "cabinette-mcp.exe didn't exit after stdin closed");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }

        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line); // throws on anything that isn't JSON
            Assert.Equal("2.0", json.RootElement.GetProperty("jsonrpc").GetString());
        }

        var responses = lines.Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.TryGetProperty("id", out _)).ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.Equal("cabinette", responses[1].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(AiTools.All.Count, responses[2].GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.Contains("Ödev.txt", responses[3].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Console.WriteLine(\"hi\");\n", responses[4].GetProperty("result").GetProperty("content")[1].GetProperty("text").GetString());
        Assert.True(responses[5].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.True(responses[6].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.True(responses[7].TryGetProperty("error", out _));

        Assert.Equal(["Claude Code", "Claude Code", "Claude Code"], cabinette.Messages.Select(m => m.Client));
        Assert.DoesNotContain("Console.WriteLine", await stderr.WaitAsync(TimeSpan.FromSeconds(10))); // no file contents in the log
    }

    [DebugBuildFact]
    public async Task ADebugBuildWithoutDataDirExitsWithCode2AndASilentStdout()
    {
        using var process = Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        Assert.True(process.WaitForExit(20_000));
        Assert.Equal(2, process.ExitCode);
        Assert.Equal("", await stdout);
        Assert.Contains("--data-dir", await stderr);
    }

    [Fact]
    public async Task AnUnknownArgumentExitsWithCode2AndASilentStdout()
    {
        using var process = Start("--data-dir", Path.Combine(_temp, "data"), "--verbose");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        Assert.True(process.WaitForExit(20_000));
        Assert.Equal(2, process.ExitCode);
        Assert.Equal("", await stdout);
        Assert.Contains("unknown argument \"--verbose\"", await stderr);
    }

    /// <summary>The cabinette-mcp.exe built next to the tests (a project reference copies it here).</summary>
    private static string ExePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cabinette-mcp.exe");
        Assert.True(File.Exists(path), $"{path} is missing");
        return path;
    }

    private Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(ExePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _temp,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info)!;
    }

    private static async Task SendAsync(Process process, string line)
    {
        await process.StandardInput.WriteAsync(line + "\n");
        await process.StandardInput.FlushAsync();
    }

    private static async Task ReadUntilResponseAsync(Process process, List<string> lines, params int[] ids)
    {
        var waiting = ids.ToHashSet();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (waiting.Count > 0)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException($"stdout ended while waiting for {string.Join(",", waiting)}");
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(line);
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            {
                waiting.Remove(id.GetInt32());
            }
        }
    }
}

/// <summary>A test that only means something for a Debug build of cabinette-mcp.exe (built with the same configuration).</summary>
public sealed class DebugBuildFactAttribute : FactAttribute
{
    public DebugBuildFactAttribute()
    {
#if !DEBUG
        Skip = "Only a Debug build of cabinette-mcp.exe refuses to run without --data-dir.";
#endif
    }
}
