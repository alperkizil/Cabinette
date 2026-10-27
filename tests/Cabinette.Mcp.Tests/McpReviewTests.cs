using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Cabinette.Ipc;
using Cabinette.Mcp.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cabinette.Mcp.Tests;

/// <summary>
/// Review findings for cabinette-mcp.exe (branch review/mcp-server-bughunt). Each F-test pins the behavior as it is
/// now, so it passes on this branch; a fix flips it into a regression test. Dropped_ tests record leads that didn't
/// hold up (the behavior is right) so nobody chases them again.
/// </summary>
public sealed class McpReviewTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cabinette-mcp-review-").FullName;

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

    // ------------------------------------------------------------------ leads dropped: cancellation does reach the pipe

    /// <summary>
    /// Lead dropped: "a client that cancels mid-wait leaves the pipe open". It doesn't. notifications/cancelled reaches
    /// the tool's token (the SDK links each request's token to it) and CabinetteConnection's cancelled read closes the
    /// pipe, which a Cabinette that never answers sees within milliseconds. The notification is written by hand
    /// because the SDK's own client never sends one (next test).
    /// </summary>
    [Fact]
    public async Task Dropped_ACancelledCallClosesThePipeToCabinette()
    {
        var pipeName = FakeCabinette.UniquePipeName();
        await using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var (serve, requestRead, disconnected) = ServeOnceWithoutAnswering(server);

        var connection = new CabinetteConnection(pipeName, new RefusingLauncher(), TestTools.FastTimings);
        await using var raw = await RawSession.StartAsync(connection);
        await raw.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","title":"Claude Code","version":"2.1.306"}}}""");
        await raw.ReadResponseAsync(1);
        await raw.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await raw.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_tags","arguments":{}}}""");
        await requestRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await raw.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5,"reason":"user pressed Esc"}}""");

        var afterCancel = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serve;
        Assert.True(afterCancel < TimeSpan.FromSeconds(3), $"Cabinette saw the disconnect only {afterCancel} after the request");
    }

    /// <summary>
    /// Test-harness fact, not a product finding: the C# SDK's client (2.2.0) sends no notifications/cancelled when its
    /// token is cancelled, so an SDK-client cancellation never reaches the server. Cancellation tests must write the
    /// notification themselves (Claude Code's TypeScript SDK does send it).
    /// </summary>
    [Fact]
    public async Task Dropped_TheSdkClientSendsNoCancelledNotification()
    {
        var channel = new RecordingChannel();
        var toServer = new Pipe();
        var toClient = new Pipe();
        var seen = new MemoryStream();
        var transport = new StreamServerTransport(new TeeStream(toServer.Reader.AsStream(), seen), toClient.Writer.AsStream(), CabinetteMcpServer.ServerName);
        var server = CabinetteMcpServer.Create(transport, channel, loggerFactory: null, () => TestTools.WorkingDirectory);
        using var stop = new CancellationTokenSource();
        var run = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Title = "Claude Code", Version = "2.1.306" } },
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        using var cancel = new CancellationTokenSource();
        var call = client.CallToolAsync(AiTools.ListTags, cancellationToken: cancel.Token).AsTask();
        await channel.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await Task.Delay(500);

        string text;
        lock (seen)
        {
            text = Encoding.UTF8.GetString(seen.ToArray());
        }

        channel.ReleaseFirst.TrySetResult();
        await client.DisposeAsync();
        await stop.CancelAsync();
        await toServer.Writer.CompleteAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        await server.DisposeAsync();
        Assert.Contains("tools/call", text);
        Assert.DoesNotContain("notifications/cancelled", text);
    }

    private static (Task Serve, TaskCompletionSource RequestRead, TaskCompletionSource<TimeSpan> Disconnected) ServeOnceWithoutAnswering(System.IO.Pipes.NamedPipeServerStream server)
    {
        var disconnected = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await new LineReader(server, 1024 * 1024).ReadLineAsync(CancellationToken.None);
            requestRead.SetResult();
            var watch = Stopwatch.StartNew();
            try
            {
                _ = await server.ReadAsync(new byte[1]); // never answers; completes only when the client goes away
            }
            catch (IOException)
            {
            }

            disconnected.SetResult(watch.Elapsed);
        });
        return (serve, requestRead, disconnected);
    }

    /// <summary>Reads from one stream and keeps a copy of everything read.</summary>
    private sealed class TeeStream(Stream inner, Stream copy) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            lock (copy)
            {
                copy.Write(buffer, offset, read);
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            lock (copy)
            {
                copy.Write(buffer.Span[..read]);
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>An image survives the SDK's wire format: the client gets the same bytes and MIME type (not covered elsewhere).</summary>
    [Fact]
    public async Task Dropped_AnImageSurvivesTheMcpWire()
    {
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.WithResult(new ReadFileResult
        {
            Kind = "image",
            ImageBase64 = Convert.ToBase64String(bytes),
            MimeType = "image/jpeg",
            Width = 10,
            Height = 300,
        }));
        var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings);
        await using var session = await ReviewSession.StartAsync(connection);

        var result = await session.Client.CallToolAsync(AiTools.ReadFile, new Dictionary<string, object?> { ["fileId"] = 3 }, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        Assert.NotEqual(true, result.IsError);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(bytes, image.DecodedData.ToArray());
        Assert.DoesNotContain("imageBase64", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    // ------------------------------------------------------------------ F1: the process lingers after the client is gone

    /// <summary>
    /// The AI client exits (or crashes) while a call waits for Cabinette: stdin closes. cabinette-mcp.exe used to keep
    /// running until Cabinette answered (forever, for a Cabinette that never does); now the end of stdin cancels the
    /// waiting call, which closes its pipe, and the process exits at once. StdioTests only cover the idle case.
    /// </summary>
    [Fact]
    public async Task F1_TheExeExitsPromptlyWhenStdinClosesWhileACallIsPending()
    {
        var dataDir = Path.Combine(_temp, "data");
        using var release = new ManualResetEventSlim();
        await using var cabinette = new FakeCabinette(InstanceNames.ForCurrentUser(dataDir).PipeName, _ =>
        {
            release.Wait(TimeSpan.FromSeconds(60));
            return InstanceReply.WithResult(new ListTagsResult([]));
        });

        using var process = StartExe("--data-dir", dataDir, "--app", Path.Combine(_temp, "missing", "Cabinette.exe"));
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = new List<string>();
        bool exitedInTime;
        TimeSpan exitedAfter;
        try
        {
            await SendAsync(process, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","title":"Claude Code","version":"2.1.306"}}}""");
            await ReadUntilResponseAsync(process, stdout, 1);
            await SendAsync(process, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await SendAsync(process, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_tags","arguments":{}}}""");
            await WaitUntilAsync(() => cabinette.Messages.Count == 1, TimeSpan.FromSeconds(10));

            var watch = Stopwatch.StartNew();
            process.StandardInput.Close();
            exitedInTime = process.WaitForExit(5000);
            exitedAfter = watch.Elapsed;
        }
        finally
        {
            release.Set();
            if (!process.HasExited)
            {
                process.Kill();
            }
        }

        Assert.True(exitedInTime, "cabinette-mcp.exe still ran 5 s after stdin closed with a call pending");
        Assert.True(exitedAfter < SessionLifetime.Grace, $"cabinette-mcp.exe needed {exitedAfter} to exit: the call wasn't cancelled, only abandoned after the grace period");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr); // a client closing stdin is a normal shutdown: the cancelled call isn't a warning
    }

    // ------------------------------------------------------------------ F2: a Debug build reaches the real library after all

    /// <summary>
    /// Usage said "A Debug build requires --data-dir, so it can never reach the real library", but naming the real data
    /// folder (any spelling) was accepted: it hashes to the real Cabinette's pipe, and Cabinette would have been started
    /// with --data-dir on the real folder (a "dev run" on the real library). A Debug build now refuses it. These tests
    /// only parse and compute names: nothing connects or starts.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealDataFolderSpellings))]
    public void F2_ADebugBuildRefusesTheRealDataFolderInAnySpelling(string spelling)
    {
        var result = McpCommandLine.Parse(["--data-dir", spelling], requireDataDirectory: true);

        Assert.Null(result.Options);
        Assert.Equal(McpCommandLine.UsageError, result.ExitCode);
        Assert.Contains("a debug build must not use the real data folder", result.Message);
    }

    /// <summary>For a Release build, the real data folder as --data-dir means exactly no --data-dir: not a dev run.</summary>
    [Theory]
    [MemberData(nameof(RealDataFolderSpellings))]
    public void F2_AReleaseBuildTreatsTheRealDataFolderAsNoDataDir(string spelling)
    {
        var result = McpCommandLine.Parse(["--data-dir", spelling, "--app", @"C:\Dev\Cabinette.exe"], requireDataDirectory: false);

        var options = Assert.IsType<McpOptions>(result.Options);
        Assert.Equal(McpCommandLine.Parse(["--app", @"C:\Dev\Cabinette.exe"], requireDataDirectory: false).Options, options);
        Assert.Equal(McpPaths.DefaultDataDirectory, options.DataDirectory);
        Assert.False(options.IsDevDataDirectory);
        Assert.Equal(["--tray"], new CabinetteLauncher(options.AppPath, options.IsDevDataDirectory ? options.DataDirectory : null, new NeverStarter()).Arguments);
    }

    /// <summary>Folders next to or inside the real one are dev data folders like any other.</summary>
    [Theory]
    [InlineData("-dev")]
    [InlineData("\\dev")]
    [InlineData(" (2)")]
    public void F2_FoldersNearTheRealOneAreStillDevDataFolders(string suffix)
    {
        var folder = McpPaths.DefaultDataDirectory + suffix;
        var result = McpCommandLine.Parse(["--data-dir", folder], requireDataDirectory: true);

        var options = Assert.IsType<McpOptions>(result.Options);
        Assert.True(options.IsDevDataDirectory);
        Assert.Equal(folder, options.DataDirectory);
        Assert.NotEqual(InstanceNames.For("S-1-5-21-1", McpPaths.DefaultDataDirectory).PipeName, InstanceNames.For("S-1-5-21-1", options.DataDirectory).PipeName);
    }

    public static TheoryData<string> RealDataFolderSpellings()
    {
        var real = McpPaths.DefaultDataDirectory;
        return
        [
            real,
            real.ToLowerInvariant(),
            real.ToUpperInvariant() + "\\",
            real.ToLowerInvariant() + "\\.",
            real + "\\.\\",
            Path.Combine(real, "..", "Cabinette"),
            "\"" + real + "\\\"",
            "  " + real + "  ",
        ];
    }

    // ------------------------------------------------------------------ F3: relative options follow the AI client's project folder

    /// <summary>
    /// The registration is global (claude mcp add --scope user), but a relative --app or --data-dir used to be resolved
    /// against the process's working folder, which Claude Code and Codex set to the open project: the Cabinette.exe that
    /// got started (and the data folder that got created) depended on which repository was open, and a repository can
    /// carry a Cabinette.exe of its own. Relative values (also drive- and root-relative ones) are now refused.
    /// </summary>
    [Theory]
    [InlineData("--app", "Cabinette.exe")]
    [InlineData("--app", @".\Cabinette.exe")]
    [InlineData("--app", @"..\bin\Cabinette.exe")]
    [InlineData("--app", "\"bin\\Cabinette.exe\"")]
    [InlineData("--app", @"C:Cabinette.exe")]
    [InlineData("--app", @"\Tools\Cabinette.exe")]
    [InlineData("--data-dir", "dev-data")]
    [InlineData("--data-dir", @"dev\data\")]
    [InlineData("--data-dir", ".")]
    [InlineData("--data-dir", @"D:data")]
    [InlineData("--data-dir", @"\data")]
    public void F3_RelativeAppAndDataDirAreRefused(string option, string value)
    {
        string[] args = option == McpCommandLine.AppOption
            ? ["--data-dir", @"C:\Dev\data", option, value]
            : ["--app", @"C:\Dev\Cabinette.exe", option, value];

        var result = McpCommandLine.Parse(args, requireDataDirectory: false);

        Assert.Null(result.Options);
        Assert.Equal(McpCommandLine.UsageError, result.ExitCode);
        Assert.StartsWith($"cabinette-mcp: {option} needs a full path", result.Message);
        Assert.Contains(value, result.Message);
    }

    /// <summary>The real exe, started in a project folder that carries a Cabinette.exe, refuses to take it.</summary>
    [Fact]
    public async Task F3_TheExeRefusesARelativeAppEvenWhenTheProjectFolderHasOne()
    {
        var project = Path.Combine(_temp, "cloned repo");
        Directory.CreateDirectory(project);
        File.WriteAllBytes(Path.Combine(project, "Cabinette.exe"), []);

        using var process = StartExeIn(project, "--data-dir", Path.Combine(_temp, "data"), "--app", "Cabinette.exe");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(process.WaitForExit(20_000), "cabinette-mcp.exe didn't exit");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }

        Assert.Equal(McpCommandLine.UsageError, process.ExitCode);
        Assert.Equal("", await stdout);
        Assert.Contains("--app needs a full path", await stderr);
    }

    // ------------------------------------------------------------------ F4: quoted paths become nonsense paths

    /// <summary>
    /// Models often quote a path they copied from a shell. McpCommandLine strips quotes around its own values, but
    /// ToolArguments.ResolvePath didn't: a quoted full path was treated as relative and sent to Cabinette as
    /// C:\Work\Project\"C:\x", which Cabinette could only report as not found. Surrounding whitespace and one pair of
    /// matching quotes are now dropped before the full-path check.
    /// </summary>
    [Theory]
    [InlineData("\"C:\\Data\\report.pdf\"", @"C:\Data\report.pdf")]
    [InlineData("'C:\\Data\\report.pdf'", @"C:\Data\report.pdf")]
    [InlineData("  \"C:\\My Data\\report.pdf\"\t", @"C:\My Data\report.pdf")]
    [InlineData(" C:\\Data\\report.pdf \n", @"C:\Data\report.pdf")]
    [InlineData("\"\\\\server\\share\\a.txt\"", @"\\server\share\a.txt")]
    [InlineData("\"docs\\a.txt\"", @"C:\Work\Project\docs\a.txt")]
    [InlineData("'..\\a.txt'", @"C:\Work\a.txt")]
    [InlineData("\"\"C:\\Data\\a.txt\"\"", "C:\\Work\\Project\\\"C:\\Data\\a.txt\"")] // only one pair goes
    [InlineData("\"C:\\Data\\a.txt'", "C:\\Work\\Project\\\"C:\\Data\\a.txt'")] // quotes that don't match stay
    [InlineData("\"\"", "\"\"")]
    [InlineData("' '", "' '")]
    public void F4_QuotedPathsAreUnquotedBeforeTheFullPathCheck(string path, string expected)
        => Assert.Equal(expected, ToolArguments.ResolvePath(path, TestTools.WorkingDirectory));

    /// <summary>Every tool that takes a path passes the quoted full path on unquoted.</summary>
    [Theory]
    [InlineData(AiTools.AddFiles, """{"paths":["\"C:\\Data\\a.txt\"","'docs\\b.pdf'"]}""", """{"paths":["C:\\Data\\a.txt","C:\\Work\\Project\\docs\\b.pdf"]}""")]
    [InlineData(AiTools.MoveVault, """{"newPath":"'D:\\New vault'"}""", """{"newPath":"D:\\New vault"}""")]
    [InlineData(AiTools.CreateBackup, """{"zipPath":" \"D:\\Backups\\cab.zip\" "}""", """{"zipPath":"D:\\Backups\\cab.zip"}""")]
    public async Task F4_ToolsThatTakePathsSendThemUnquoted(string tool, string argumentsJson, string expectedArgs)
    {
        await using var cabinette = new FakeCabinette();
        var result = await TestTools.Tool(tool, cabinette).CallAsync("Claude Code", TestTools.Args(argumentsJson), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        var sent = Assert.Single(cabinette.Messages).Args!.Value;
        foreach (var (name, expected) in System.Text.Json.Nodes.JsonNode.Parse(expectedArgs)!.AsObject())
        {
            var actual = System.Text.Json.Nodes.JsonNode.Parse(sent.GetProperty(name).GetRawText());
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual), $"{name}: expected {expected?.ToJsonString()}, sent {actual?.ToJsonString()}");
        }
    }

    // ------------------------------------------------------------------ F5: an option name is swallowed as a path

    /// <summary>
    /// <c>cabinette-mcp.exe --data-dir --app</c> (a forgotten value) used to run with the data folder &lt;project&gt;\--app,
    /// where a Cabinette started for it would create a library. A value starting with <c>--</c> now counts as missing.
    /// </summary>
    [Theory]
    [InlineData("--data-dir", "--app")]
    [InlineData("--data-dir", "--app", @"C:\Dev\Cabinette.exe")]
    [InlineData("--app", "--data-dir", @"C:\Dev\data")]
    [InlineData("--data-dir", "--help")]
    [InlineData("--data-dir", " --verbose")]
    public void F5_AnOptionNameIsNotTakenAsThePathOfThePrecedingOption(params string[] args)
    {
        var result = McpCommandLine.Parse(args, requireDataDirectory: true);

        Assert.Null(result.Options);
        Assert.Equal(McpCommandLine.UsageError, result.ExitCode);
        Assert.StartsWith($"cabinette-mcp: {args[0]} needs a value.", result.Message);
    }

    // ------------------------------------------------------------------ leads dropped (behavior is right)

    [Fact]
    public async Task Dropped_ParallelCallsFromOneClientRunConcurrently()
    {
        var channel = new RecordingChannel();
        await using var session = await ReviewSession.StartAsync(channel);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = session.Client.CallToolAsync(AiTools.ListTags, cancellationToken: timeout.Token).AsTask();
        await channel.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = session.Client.CallToolAsync(AiTools.ListCategories, cancellationToken: timeout.Token).AsTask();

        var secondDone = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(3)));
        channel.ReleaseFirst.SetResult();
        await first;
        await second;

        Assert.Same(second, secondDone);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "cabinette-mcp.exe");

    private Process StartExe(params string[] arguments) => StartExeIn(_temp, arguments);

    private static Process StartExeIn(string workingDirectory, params string[] arguments)
    {
        Assert.True(File.Exists(ExePath), $"{ExePath} is missing");
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, "condition not met in time");
            await Task.Delay(20);
        }
    }

    /// <summary>A starter that records what it was asked to start and starts nothing.</summary>
    private sealed class NeverStarter : IProcessStarter
    {
        public List<string> Started { get; } = [];

        public int Start(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Started.Add(exePath);
            return 4242;
        }
    }

    /// <summary>A channel whose first call blocks until released and records whether its token was cancelled.</summary>
    private sealed class RecordingChannel : ICabinetteChannel
    {
        private int _calls;

        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCallCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<InstanceReply> SendAsync(InstanceMessage message, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallStarted.TrySetResult();
                using var registration = ct.Register(() => FirstCallCancelled.TrySetResult());
                await ReleaseFirst.Task.WaitAsync(ct);
            }

            return InstanceReply.WithResult(new ListTagsResult([]));
        }
    }

    /// <summary>The server driven with raw JSON-RPC lines over in-memory pipes (no SDK client in the way).</summary>
    private sealed class RawSession : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly Task _run;
        private readonly CancellationTokenSource _stop;
        private readonly Pipe _toServer;
        private readonly Pipe _toClient;
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;

        private RawSession(McpServer server, Task run, CancellationTokenSource stop, Pipe toServer, Pipe toClient)
        {
            _server = server;
            _run = run;
            _stop = stop;
            _toServer = toServer;
            _toClient = toClient;
            _writer = new StreamWriter(toServer.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
            _reader = new StreamReader(toClient.Reader.AsStream(), Encoding.UTF8);
        }

        public static Task<RawSession> StartAsync(ICabinetteChannel channel)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), CabinetteMcpServer.ServerName);
            var server = CabinetteMcpServer.Create(transport, channel, loggerFactory: null, () => TestTools.WorkingDirectory);
            var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            return Task.FromResult(new RawSession(server, run, stop, toServer, toClient));
        }

        public Task SendAsync(string line) => _writer.WriteAsync(line + "\n");

        public async Task<JsonElement> ReadResponseAsync(int id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("server output ended");
                if (line.Length == 0)
                {
                    continue;
                }

                var json = JsonDocument.Parse(line).RootElement.Clone();
                if (json.TryGetProperty("id", out var found) && found.ValueKind == JsonValueKind.Number && found.GetInt32() == id)
                {
                    return json;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _toServer.Writer.CompleteAsync();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }

            await _server.DisposeAsync();
            await _toClient.Writer.CompleteAsync();
            _stop.Dispose();
        }
    }

    /// <summary>The server with an arbitrary channel and an SDK client, over in-memory pipes.</summary>
    private sealed class ReviewSession : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly Task _run;
        private readonly CancellationTokenSource _stop;
        private readonly Pipe _toServer;
        private readonly Pipe _toClient;

        private ReviewSession(McpServer server, Task run, CancellationTokenSource stop, Pipe toServer, Pipe toClient, McpClient client)
        {
            _server = server;
            _run = run;
            _stop = stop;
            _toServer = toServer;
            _toClient = toClient;
            Client = client;
        }

        public McpClient Client { get; }

        public static async Task<ReviewSession> StartAsync(ICabinetteChannel channel, Implementation? clientInfo = null)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), CabinetteMcpServer.ServerName);
            var server = CabinetteMcpServer.Create(transport, channel, loggerFactory: null, () => TestTools.WorkingDirectory);
            var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                new McpClientOptions { ClientInfo = clientInfo ?? new Implementation { Name = "claude-code", Title = "Claude Code", Version = "2.1.306" } },
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            return new ReviewSession(server, run, stop, toServer, toClient, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            await _toServer.Writer.CompleteAsync();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }

            await _server.DisposeAsync();
            await _toClient.Writer.CompleteAsync();
            _stop.Dispose();
        }
    }
}
