using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Cabinette.Ipc;

namespace Cabinette.Mcp.Tests;

/// <summary>
/// A stand-in for the running Cabinette, built from the Ipc types: serves the local channel's pipe (owned by this user,
/// like the app's), records every message and answers with canned replies.
/// </summary>
internal sealed class FakeCabinette : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private NamedPipeServerStream? _listening;

    public FakeCabinette(string? pipeName = null, Func<InstanceMessage, InstanceReply>? reply = null)
    {
        PipeName = pipeName ?? UniquePipeName();
        Reply = reply ?? (_ => InstanceReply.WithResult(new { ok = 1 }));

        // The first instance exists before the constructor returns, so the pipe is there for the client at once.
        _listening = CreateInstance();
        _loop = Task.Run(ListenAsync);
    }

    public string PipeName { get; }

    /// <summary>The answer to each message (may block to simulate a long call).</summary>
    public Func<InstanceMessage, InstanceReply> Reply { get; set; }

    public ConcurrentQueue<InstanceMessage> Messages { get; } = new();

    public ConcurrentQueue<string> Lines { get; } = new();

    public int ConnectionCount => _connections;

    private int _connections;

    public static string UniquePipeName() => $"CabinetteMcpTests-{Guid.NewGuid():N}";

    /// <summary>The last message's args as compact JSON.</summary>
    public string LastArgsJson() => Messages.Last().Args is { } args ? JsonSerializer.Serialize(args) : "null";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _listening?.Dispose();
        _stop.Dispose();
    }

    private NamedPipeServerStream CreateInstance() => new(
        PipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var server = _listening ?? CreateInstance();
            _listening = server;
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _listening = CreateInstance();
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(server));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                var reader = new LineReader(server, InstanceProtocol.MaxLineLength);
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    Lines.Enqueue(line);
                    InstanceReply reply;
                    if (InstanceProtocol.TryParseMessage(line, out var message, out var error))
                    {
                        Messages.Enqueue(message);
                        reply = Reply(message);
                    }
                    else
                    {
                        reply = InstanceReply.Failed(error);
                    }

                    var bytes = Encoding.UTF8.GetBytes(InstanceProtocol.Serialize(reply) + "\n");
                    await server.WriteAsync(bytes, _stop.Token);
                    await server.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away (a cancelled call closes the pipe).
            }
        }
    }
}
