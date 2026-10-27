using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using Cabinette.Ipc;

namespace Cabinette.Mcp.Tests;

/// <summary>The channel: one connection per call, the not_ready retry, cancellation, and starting Cabinette.</summary>
public sealed class ConnectionTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cabinette-mcp-tests-").FullName;

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
    public void TheDefaultWaitsAreTheSpecifiedOnes()
    {
        var timings = new ConnectionTimings();
        Assert.Equal(TimeSpan.FromSeconds(5), timings.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), timings.StartTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), timings.NotReadyRetryInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), timings.NotReadyTimeout);
    }

    [Fact]
    public async Task EachCallUsesItsOwnConnection()
    {
        await using var cabinette = new FakeCabinette();
        var launcher = new RefusingLauncher();
        var connection = new CabinetteConnection(cabinette.PipeName, launcher, TestTools.FastTimings);

        await connection.SendAsync(Message(), CancellationToken.None);
        await connection.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(2, cabinette.Messages.Count);
        Assert.Equal(2, cabinette.ConnectionCount);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task NotReadyIsRetriedUntilCabinetteIsReady()
    {
        var calls = 0;
        await using var cabinette = new FakeCabinette(reply: _ => Interlocked.Increment(ref calls) <= 3
            ? InstanceReply.Failed(AiErrorCodes.NotReady, "Cabinette is starting.")
            : InstanceReply.WithResult(new ListTagsResult([])));
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings with { NotReadyTimeout = TimeSpan.FromSeconds(5) });

        var reply = await connection.SendAsync(Message(), CancellationToken.None);

        Assert.True(reply.Ok);
        Assert.Equal(4, cabinette.Messages.Count);
        Assert.Equal(4, cabinette.ConnectionCount);
    }

    [Fact]
    public async Task NotReadyGivesTheMessageToTheModelAfterTheTimeLimit()
    {
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.Failed(AiErrorCodes.NotReady, "Finish first-run setup in the window that opened."));
        var timings = TestTools.FastTimings with { NotReadyRetryInterval = TimeSpan.FromMilliseconds(50), NotReadyTimeout = TimeSpan.FromMilliseconds(600) };
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), timings);

        var watch = Stopwatch.StartNew();
        var reply = await connection.SendAsync(Message(), CancellationToken.None);
        watch.Stop();

        Assert.False(reply.Ok);
        Assert.Equal(AiErrorCodes.NotReady, reply.Code);
        Assert.Equal("Finish first-run setup in the window that opened.", reply.Error);

        // It kept retrying until the limit (no attempt starts after it), at most one interval apart.
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(600) + TimeSpan.FromSeconds(2));
        Assert.InRange(cabinette.Messages.Count, 4, 13);
    }

    [Fact]
    public async Task RepliesFarLargerThan64KBAreRead()
    {
        // A full text page (256 KB, JSON-escaped to more) and an image in base64 are far above the app's own 64 KB client limit.
        var text = string.Concat(Enumerable.Repeat("line \"quoted\" \\ ü\n", 40_000));
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.WithResult(new ReadFileResult { Kind = "text", Text = text, Unit = "bytes" }));
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings);

        var reply = await connection.SendAsync(InstanceMessage.Ai("Codex", AiTools.ReadFile, new ReadFileArgs(1)), CancellationToken.None);

        Assert.True(reply.Ok);
        Assert.True(reply.Result!.Value.GetRawText().Length > 1_000_000);
        Assert.Equal(text, AiProtocol.FromElement<ReadFileResult>(reply.Result)!.Text);
    }

    [Theory]
    [InlineData(AiTools.ShowInCabinette, true)]
    [InlineData(AiTools.MoveVault, true)]
    [InlineData(AiTools.MigratePointers, true)]
    [InlineData(AiTools.CreateBackup, true)]
    [InlineData(AiTools.Search, false)]
    [InlineData(AiTools.DeleteFiles, false)]
    public void CallsThatOpenAWindowLetCabinetteComeToTheFront(string tool, bool expected)
        => Assert.Equal(expected, CabinetteConnection.WantsForeground(tool));

    [Fact]
    public async Task AShowCallStillWorksWhenWindowsRefusesTheForegroundHandOver()
    {
        // AllowSetForegroundWindow is best effort: a test process may not hand over the foreground, and nothing fails.
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.WithResult(new ShowResult(true)));
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings);

        var reply = await connection.SendAsync(InstanceMessage.Ai("Codex", AiTools.ShowInCabinette, new ShowArgs(FileId: 3)), CancellationToken.None);

        Assert.True(reply.Ok);
    }

    [Fact]
    public async Task OtherErrorsAreNotRetried()
    {
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.Failed(AiErrorCodes.Busy, "busy"));
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings);

        var reply = await connection.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(AiErrorCodes.Busy, reply.Code);
        Assert.Single(cabinette.Messages);
    }

    [Fact]
    public async Task ALongCallHasNoReplyTimeout()
    {
        // Longer than every wait in the timings: adding big files takes as long as it takes.
        await using var cabinette = new FakeCabinette(reply: _ =>
        {
            Thread.Sleep(2500);
            return InstanceReply.WithResult(new ListTagsResult([]));
        });
        var timings = TestTools.FastTimings with { ConnectTimeout = TimeSpan.FromMilliseconds(500), NotReadyTimeout = TimeSpan.FromMilliseconds(500) };
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), timings);

        var reply = await connection.SendAsync(Message(), CancellationToken.None);

        Assert.True(reply.Ok);
    }

    [Fact]
    public async Task CancellingTheCallClosesThePipe()
    {
        using var release = new ManualResetEventSlim();
        await using var cabinette = new FakeCabinette(reply: _ =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return InstanceReply.WithResult(new ListTagsResult([]));
        });
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendAsync(Message(), cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        release.Set();
    }

    [Fact]
    public async Task ARunningCabinetteIsNeverStartedAgain()
    {
        await using var cabinette = new FakeCabinette();
        var starter = new FakeStarter();
        var connection = new CabinetteConnection(cabinette.PipeName, new CabinetteLauncher(CreateExe(), null, starter), TestTools.FastTimings);

        await connection.SendAsync(Message(), CancellationToken.None);

        Assert.Empty(starter.Calls);
    }

    [Fact]
    public async Task WhenCabinetteIsntRunningItIsStartedInTheTrayAndTheCallGoesThrough()
    {
        var pipeName = FakeCabinette.UniquePipeName();
        FakeCabinette? started = null;
        var starter = new FakeStarter(() => _ = Task.Run(async () =>
        {
            await Task.Delay(300); // Cabinette takes a moment to open its channel.
            started = new FakeCabinette(pipeName);
        }));
        var exe = CreateExe();
        var dataDir = Path.Combine(_temp, "data dir");
        var connection = new CabinetteConnection(pipeName, new CabinetteLauncher(exe, dataDir, starter), TestTools.FastTimings);

        try
        {
            var reply = await connection.SendAsync(Message(), CancellationToken.None);

            Assert.True(reply.Ok);
            var call = Assert.Single(starter.Calls);
            Assert.Equal(exe, call.Exe);
            Assert.Equal(["--tray", "--data-dir", dataDir], call.Arguments);
            Assert.Equal(Path.GetDirectoryName(exe), call.WorkingDirectory);
            Assert.Single(started!.Messages);
        }
        finally
        {
            if (started is not null)
            {
                await started.DisposeAsync();
            }
        }
    }

    [Fact]
    public void TheRealLibraryRunIsStartedWithoutDataDir()
    {
        var launcher = new CabinetteLauncher(@"C:\Program Files\Cabinette\Cabinette.exe", null, new FakeStarter());
        Assert.Equal(["--tray"], launcher.Arguments);
    }

    [Fact]
    public async Task ACabinetteThatNeverOpensItsChannelFailsAfterTheStartTimeout()
    {
        var starter = new FakeStarter();
        var timings = TestTools.FastTimings with { StartTimeout = TimeSpan.FromMilliseconds(400) };
        var connection = new CabinetteConnection(FakeCabinette.UniquePipeName(), new CabinetteLauncher(CreateExe(), null, starter), timings);

        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<CabinetteUnavailableException>(() => connection.SendAsync(Message(), CancellationToken.None));

        Assert.Equal(2, starter.Calls.Count); // started, and once more after a third of the wait
        Assert.Contains("didn't open its channel within", error.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AStartThatComesToNothingIsTriedOnceMore()
    {
        // The first copy exits at once (the old Cabinette was still quitting); the second one opens the channel.
        var pipeName = FakeCabinette.UniquePipeName();
        FakeCabinette? started = null;
        var calls = 0;
        var starter = new FakeStarter(() =>
        {
            if (++calls == 2)
            {
                started = new FakeCabinette(pipeName);
            }
        });
        var timings = TestTools.FastTimings with { StartTimeout = TimeSpan.FromSeconds(3) };
        var connection = new CabinetteConnection(pipeName, new CabinetteLauncher(CreateExe(), null, starter), timings);

        try
        {
            var watch = Stopwatch.StartNew();
            var reply = await connection.SendAsync(Message(), CancellationToken.None);

            Assert.True(reply.Ok);
            Assert.Equal(2, starter.Calls.Count);
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (started is not null)
            {
                await started.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AMissingCabinetteExeIsAClearErrorAndNothingIsStarted()
    {
        var starter = new FakeStarter();
        var exe = Path.Combine(_temp, "nowhere", "Cabinette.exe");
        var connection = new CabinetteConnection(FakeCabinette.UniquePipeName(), new CabinetteLauncher(exe, null, starter), TestTools.FastTimings);

        var error = await Assert.ThrowsAsync<CabinetteUnavailableException>(() => connection.SendAsync(Message(), CancellationToken.None));

        Assert.Empty(starter.Calls);
        Assert.Contains("isn't running", error.Message);
        Assert.Contains(exe, error.Message);
    }

    [Fact]
    public async Task AStartWindowsRefusesIsAClearError()
    {
        var starter = new FakeStarter(() => throw new Win32Exception(5));
        var connection = new CabinetteConnection(FakeCabinette.UniquePipeName(), new CabinetteLauncher(CreateExe(), null, starter), TestTools.FastTimings);

        var error = await Assert.ThrowsAsync<CabinetteUnavailableException>(() => connection.SendAsync(Message(), CancellationToken.None));

        Assert.StartsWith("Cabinette isn't running, and starting", error.Message);
    }

    [Fact]
    public async Task ABusyChannelIsReportedWithoutStartingAnotherCabinette()
    {
        // A pipe with one instance, held by another client: it exists, but no connection is possible.
        var pipeName = FakeCabinette.UniquePipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var other = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), other.ConnectAsync(2000));

        var starter = new FakeStarter();
        var timings = TestTools.FastTimings with { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        var connection = new CabinetteConnection(pipeName, new CabinetteLauncher(CreateExe(), null, starter), timings);

        var error = await Assert.ThrowsAsync<CabinetteUnavailableException>(() => connection.SendAsync(Message(), CancellationToken.None));

        Assert.Contains("didn't accept the connection", error.Message);
        Assert.Empty(starter.Calls);
    }

    [Fact]
    public async Task CabinetteQuittingMidCallIsReported()
    {
        var pipeName = FakeCabinette.UniquePipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await new LineReader(server, 1024 * 1024).ReadLineAsync(CancellationToken.None);
            server.Disconnect(); // gone without a reply
        });
        var connection = new CabinetteConnection(pipeName, new RefusingLauncher(), TestTools.FastTimings);

        var error = await Assert.ThrowsAsync<CabinetteUnavailableException>(() => connection.SendAsync(Message(), CancellationToken.None));
        await serve;

        Assert.Contains("may or may not have been made", error.Message);
    }

    [Fact]
    public void ThePipeProbeSeesNoPipeWhereThereIsNone()
        => Assert.False(NativeMethods.PipeExists(FakeCabinette.UniquePipeName()));

    [Fact]
    public async Task ThePipeProbeSeesAListeningPipe()
    {
        await using var cabinette = new FakeCabinette();
        Assert.True(NativeMethods.PipeExists(cabinette.PipeName));
    }

    private static InstanceMessage Message() => InstanceMessage.Ai("Claude Code", AiTools.ListTags, new ListTagsArgs());

    private string CreateExe()
    {
        var path = Path.Combine(_temp, "app", "Cabinette.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private sealed class FakeStarter(Action? onStart = null) : IProcessStarter
    {
        public List<(string Exe, IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

        public int Start(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Calls.Add((exePath, arguments, workingDirectory));
            onStart?.Invoke();
            return 4242;
        }
    }
}
