using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cabinette.Mcp.Tests;

/// <summary>How a session ends (review F1): the end of stdin stops it, and a session that won't stop is abandoned.</summary>
public sealed class ShutdownTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task TheEndOfInputCancelsTheStopTokenAndTheSessionThatHonorsItEnds()
    {
        using var stop = new CancellationTokenSource();
        var inputEnded = new TaskCompletionSource();
        var session = Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);

        var waiting = SessionLifetime.WaitAsync(session, inputEnded.Task, stop, TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        Assert.False(stop.IsCancellationRequested);

        inputEnded.SetResult();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task ASessionThatIgnoresTheStopIsAbandonedAfterTheGracePeriod()
    {
        using var stop = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var watch = Stopwatch.StartNew();

        var ended = await SessionLifetime.WaitAsync(never.Task, Task.CompletedTask, stop, Grace).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(ended);
        Assert.True(stop.IsCancellationRequested);
        Assert.InRange(watch.Elapsed, Grace - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CtrlCStopsTheSessionWithTheSameGracePeriod()
    {
        using var stop = new CancellationTokenSource();
        var never = new TaskCompletionSource();

        var waiting = SessionLifetime.WaitAsync(never.Task, new TaskCompletionSource().Task, stop, Grace);
        await stop.CancelAsync();

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ASessionThatEndsByItselfLeavesTheStopTokenAlone()
    {
        using var stop = new CancellationTokenSource();

        Assert.True(await SessionLifetime.WaitAsync(Task.CompletedTask, new TaskCompletionSource().Task, stop, Grace));
        Assert.False(stop.IsCancellationRequested);
    }

    [Fact]
    public void CancellationsAreLoggedOnlyBeforeTheStop()
    {
        using var stop = new CancellationTokenSource();
        var writer = new StringWriter();
        var log = new StderrLog(writer, stopping: stop.Token);

        log.Write(LogLevel.Warning, "before", new OperationCanceledException());
        stop.Cancel();
        log.Write(LogLevel.Warning, "after", new OperationCanceledException());
        log.Write(LogLevel.Error, "other", new IOException("broken"));

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("before", lines[0]);
        Assert.Contains("other", lines[1]);
    }
}
