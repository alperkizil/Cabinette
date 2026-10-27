using Cabinette.Mcp.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cabinette.Mcp;

/// <summary>Builds the MCP server: name "cabinette", the instructions for the model, and one tool per AI tool.</summary>
internal static class CabinetteMcpServer
{
    public const string ServerName = "cabinette";

    public static McpServerOptions CreateOptions(ICabinetteChannel channel, Func<string>? workingDirectory = null)
    {
        workingDirectory ??= () => Environment.CurrentDirectory;
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var definition in ToolCatalog.All)
        {
            tools.Add(new CabinetteTool(definition, channel, workingDirectory));
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Title = "Cabinette", Version = BuildInfo.Version },
            ServerInstructions = ToolCatalog.ServerInstructions,
            ToolCollection = tools,
        };
    }

    /// <summary>The server on a transport (stdio in the program, in-memory streams in tests).</summary>
    public static McpServer Create(ITransport transport, ICabinetteChannel channel, ILoggerFactory? loggerFactory, Func<string>? workingDirectory = null)
        => McpServer.Create(transport, CreateOptions(channel, workingDirectory), loggerFactory);
}
