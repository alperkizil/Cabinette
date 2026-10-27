using System.Text;
using Cabinette.Ipc;
using ModelContextProtocol.Server;

namespace Cabinette.Mcp;

/// <summary>
/// cabinette-mcp.exe: Claude Code or Codex start it and talk MCP to it over stdin/stdout; every tool call goes on to the
/// running Cabinette over the local channel (SPEC.md section 10).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Stdout is the MCP channel. The transport writes to the raw stdout stream; anything written through Console.Out
        // (by this code or a library) goes to stderr instead, as UTF-8.
        var stderr = TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetOut(stderr);
        Console.SetError(stderr);

        var parsed = McpCommandLine.Parse(args, BuildInfo.RequiresDataDirectory);
        if (parsed.Options is not { } options)
        {
            stderr.WriteLine(parsed.Message);
            return parsed.ExitCode;
        }

        // Not disposed: a session abandoned after the grace period may still hold registrations on its token.
        var stop = new CancellationTokenSource();
        using var log = new StderrLog(stderr, stopping: stop.Token);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var inputEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = RunSessionAsync(options, log, stop.Token, inputEnded);
        if (!await SessionLifetime.WaitAsync(session, inputEnded.Task, stop, SessionLifetime.Grace))
        {
            log.Warn($"The session didn't end within {SessionLifetime.Grace.TotalSeconds:0} s of being stopped; exiting without it.");
            return 0;
        }

        return await session;
    }

    /// <summary>The MCP server on stdin/stdout until the client closes stdin or the stop token is cancelled.</summary>
    /// <param name="inputEnded">Set when the transport has read everything the client will send (stdin closed or broken).</param>
    private static async Task<int> RunSessionAsync(McpOptions options, StderrLog log, CancellationToken stop, TaskCompletionSource inputEnded)
    {
        try
        {
            var pipeName = InstanceNames.ForCurrentUser(options.DataDirectory).PipeName;
            var launcher = new CabinetteLauncher(
                options.AppPath,
                options.IsDevDataDirectory ? options.DataDirectory : null,
                new DetachedProcessStarter(log.Warn));
            var connection = new CabinetteConnection(pipeName, launcher);

            await using var transport = new StdioServerTransport(CabinetteMcpServer.ServerName, log);

            // The transport completes its message channel at the end of stdin, once the server has taken every message.
            _ = transport.MessageReader.Completion.ContinueWith(
                _ => inputEnded.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            await using var server = CabinetteMcpServer.Create(transport, connection, log);
            await server.RunAsync(stop);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            log.Write(Microsoft.Extensions.Logging.LogLevel.Critical, "cabinette-mcp stopped after an unexpected error.", ex);
            return 1;
        }
    }
}
