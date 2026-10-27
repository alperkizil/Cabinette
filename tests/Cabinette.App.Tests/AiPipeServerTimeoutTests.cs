using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Cabinette.App.Services.Instance;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// The pipe server's wait limit (stage 4): it covers waiting for each request line and writing each reply, not the handler, so
/// an "ai" call that takes longer still gets its answer while an idle connection is dropped. A private, uniquely named pipe
/// (current user only) with a short limit; no UI, no app state.
/// </summary>
public sealed class AiPipeServerTimeoutTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task A_handler_slower_than_the_wait_limit_still_gets_its_reply_back()
    {
        var name = NewPipeName();
        await using var server = new InstancePipeServer(name, async line =>
        {
            await Task.Delay(ShortTimeout * 3);
            return "{\"ok\":true,\"error\":\"slow\"}";
        }, ShortTimeout);
        server.Start();

        var reply = await InstancePipeClient.SendAsync(name, InstanceMessage.Activate(), TimeSpan.FromSeconds(15));

        Assert.NotNull(reply);
        Assert.True(reply.Ok);
        Assert.Equal("slow", reply.Error);
    }

    [Fact]
    public async Task Each_line_on_one_connection_gets_its_own_wait_limit()
    {
        var name = NewPipeName();
        var handled = 0;
        await using var server = new InstancePipeServer(name, async line =>
        {
            await Task.Delay(ShortTimeout * 2);
            return $"{{\"ok\":true,\"error\":\"{Interlocked.Increment(ref handled)}\"}}";
        }, ShortTimeout);
        server.Start();

        await using var pipe = await ConnectAsync(name);
        var reader = new LineReader(pipe, 64 * 1024);
        for (var i = 1; i <= 2; i++)
        {
            await WriteLineAsync(pipe, InstanceProtocol.Serialize(InstanceMessage.Activate()));
            var line = await reader.ReadLineAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            Assert.Equal(i.ToString(), InstanceProtocol.TryParseReply(line)!.Error);
        }
    }

    [Fact]
    public async Task An_idle_connection_is_dropped_after_the_wait_limit()
    {
        var name = NewPipeName();
        await using var server = new InstancePipeServer(name, line => Task.FromResult("{\"ok\":true}"), ShortTimeout);
        server.Start();

        await using var pipe = await ConnectAsync(name);
        var elapsed = Stopwatch.StartNew();
        var dropped = await IsDroppedAsync(pipe, TimeSpan.FromSeconds(10));

        Assert.True(dropped, "the server kept an idle connection open");
        Assert.True(elapsed.Elapsed >= ShortTimeout * 0.5, $"dropped too early ({elapsed.Elapsed.TotalMilliseconds:0} ms)");
    }

    [Fact]
    public async Task A_connection_idle_after_a_reply_is_dropped_too()
    {
        var name = NewPipeName();
        await using var server = new InstancePipeServer(name, line => Task.FromResult("{\"ok\":true}"), ShortTimeout);
        server.Start();

        await using var pipe = await ConnectAsync(name);
        await WriteLineAsync(pipe, InstanceProtocol.Serialize(InstanceMessage.Activate()));
        var reader = new LineReader(pipe, 64 * 1024);
        Assert.True(InstanceProtocol.TryParseReply(await reader.ReadLineAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))!.Ok);

        Assert.True(await IsDroppedAsync(pipe, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void The_default_wait_limit_stays_15_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), InstancePipeServer.DefaultTimeout);
    }

    private static string NewPipeName() => "Cabinette-test-" + Guid.NewGuid().ToString("N");

    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        return pipe;
    }

    private static async Task WriteLineAsync(Stream pipe, string line)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await pipe.FlushAsync();
    }

    /// <summary>True when the server closes its end (the read ends or fails) before <paramref name="within"/>.</summary>
    private static async Task<bool> IsDroppedAsync(Stream pipe, TimeSpan within)
    {
        try
        {
            var read = await pipe.ReadAsync(new byte[16], new CancellationTokenSource(within).Token);
            return read == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
