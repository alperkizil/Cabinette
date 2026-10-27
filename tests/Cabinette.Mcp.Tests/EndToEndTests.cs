using System.IO.Pipelines;
using System.Text.Json;
using Cabinette.Ipc;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cabinette.Mcp.Tests;

/// <summary>The whole server over in-memory streams, driven by the SDK's own MCP client, against the fake Cabinette.</summary>
public sealed class EndToEndTests
{
    [Fact]
    public async Task AClientListsTheToolsAndCallsThemThroughThePipe()
    {
        await using var cabinette = new FakeCabinette(reply: message => message.Tool switch
        {
            AiTools.Search => InstanceReply.WithResult(new SearchResult([], [new AiFile { Id = 7, Name = "taxes.pdf", Type = "documents" }], HasMore: false)),
            AiTools.ReadFile => InstanceReply.WithResult(new ReadFileResult { Kind = "text", Text = "line 1\n\"line 2\"", Unit = "bytes", Encoding = "utf-8", Total = 15 }),
            _ => InstanceReply.Failed(AiErrorCodes.Failed, "unexpected"),
        });

        await using var session = await Session.StartAsync(cabinette, new Implementation { Name = "claude-code", Title = "Claude Code", Version = "2.1.306" });
        var client = session.Client;

        // Server info and instructions.
        Assert.Equal("cabinette", client.ServerInfo.Name);
        Assert.Equal(BuildInfo.Version, client.ServerInfo.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", client.ServerInfo.Version);
        Assert.Contains("Recycle Bin", client.ServerInstructions);

        // The tools: names, annotations, schemas.
        var tools = await client.ListToolsAsync(cancellationToken: TestContext());
        Assert.Equal(AiTools.All.Order(), tools.Select(t => t.Name).Order());
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            Assert.NotNull(tool.Annotations);
            Assert.Equal(AiTools.Reading.Contains(tool.Name), tool.Annotations.ReadOnlyHint);
            Assert.False(tool.Annotations.OpenWorldHint);
            Assert.NotNull(tool.Annotations.DestructiveHint);
            Assert.NotNull(tool.Annotations.IdempotentHint);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(Tools.ToolCatalog.Find(tool.Name)!.Description, tool.Description); // nothing added on the way
            ToolCatalogTests.AssertHasNoInstructions(tool.Name, tool.Description);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        }

        var delete = tools.Single(t => t.Name == AiTools.DeleteFiles).ProtocolTool;
        Assert.True(delete.Annotations!.DestructiveHint);
        Assert.Equal(["fileIds"], delete.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("integer", delete.InputSchema.GetProperty("properties").GetProperty("fileIds").GetProperty("items").GetProperty("type").GetString());
        var search = tools.Single(t => t.Name == AiTools.Search).ProtocolTool;
        Assert.Equal(
            ["query", "filter", "limit"],
            search.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.True(search.Annotations!.ReadOnlyHint);

        // A call: the args reach Cabinette with the client's display name, the result comes back as JSON text.
        var searchResult = await client.CallToolAsync(AiTools.Search, new Dictionary<string, object?> { ["query"] = "taxes", ["limit"] = 5 }, cancellationToken: TestContext());
        Assert.NotEqual(true, searchResult.IsError);
        var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(searchResult.Content)).Text);
        Assert.Equal(7, json.RootElement.GetProperty("files")[0].GetProperty("id").GetInt64());
        var message = cabinette.Messages.Last();
        Assert.Equal("Claude Code", message.Client);
        Assert.Equal(AiTools.Search, message.Tool);
        Assert.Equal(TestTools.ArgsJson(new SearchArgs("taxes", null, 5)), cabinette.LastArgsJson());

        // Another: read_file as metadata plus raw text.
        var read = await client.CallToolAsync(AiTools.ReadFile, new Dictionary<string, object?> { ["fileId"] = 7 }, cancellationToken: TestContext());
        Assert.NotEqual(true, read.IsError);
        Assert.Equal(2, read.Content.Count);
        Assert.Equal("line 1\n\"line 2\"", Assert.IsType<TextContentBlock>(read.Content[1]).Text);
        Assert.Equal(TestTools.ArgsJson(new ReadFileArgs(7)), cabinette.LastArgsJson());

        // A bad call is a tool error, not a protocol error, and never reaches Cabinette.
        var count = cabinette.Messages.Count;
        var bad = await client.CallToolAsync(AiTools.DeleteFiles, new Dictionary<string, object?> { ["ids"] = new[] { 1 } }, cancellationToken: TestContext());
        Assert.True(bad.IsError);
        Assert.StartsWith("invalid_argument: unknown parameter \"ids\"", Assert.IsType<TextContentBlock>(Assert.Single(bad.Content)).Text);
        Assert.Equal(count, cabinette.Messages.Count);
    }

    [Fact]
    public async Task CodexIsNamedCodex()
    {
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.WithResult(new ListTagsResult([new AiTag("tax", 3)])));
        await using var session = await Session.StartAsync(cabinette, new Implementation { Name = "codex-mcp-client", Title = "Codex", Version = "0.159.3" });

        var result = await session.Client.CallToolAsync(AiTools.ListTags, cancellationToken: TestContext());

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Codex", cabinette.Messages.Single().Client);
    }

    private static CancellationToken TestContext() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>The server and an SDK client connected by two in-memory pipes.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly Task _serverRun;
        private readonly CancellationTokenSource _stop;
        private readonly Pipe _toServer;
        private readonly Pipe _toClient;

        private Session(McpServer server, Task serverRun, CancellationTokenSource stop, Pipe toServer, Pipe toClient, McpClient client)
        {
            _server = server;
            _serverRun = serverRun;
            _stop = stop;
            _toServer = toServer;
            _toClient = toClient;
            Client = client;
        }

        public McpClient Client { get; }

        public static async Task<Session> StartAsync(FakeCabinette cabinette, Implementation clientInfo)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var connection = new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), TestTools.FastTimings);
            var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), CabinetteMcpServer.ServerName);
            var server = CabinetteMcpServer.Create(transport, connection, loggerFactory: null, () => TestTools.WorkingDirectory);
            var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                new McpClientOptions { ClientInfo = clientInfo },
                cancellationToken: TestContext());
            return new Session(server, run, stop, toServer, toClient, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            await _toServer.Writer.CompleteAsync();
            try
            {
                await _serverRun.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }

            await _server.DisposeAsync();
            await _toClient.Writer.CompleteAsync();
            _stop.Dispose();
        }
    }
}
