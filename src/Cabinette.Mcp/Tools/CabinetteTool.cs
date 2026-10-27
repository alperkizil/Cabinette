using System.Text.Json;
using Cabinette.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cabinette.Mcp.Tools;

/// <summary>
/// One MCP tool: checks the arguments, sends them to Cabinette as an "ai" message (one pipe connection) and turns the
/// reply into the tool result. Errors come back as tool errors (<c>isError</c>) the model can read, never as exceptions.
/// </summary>
internal sealed class CabinetteTool : McpServerTool
{
    private readonly ToolDefinition _definition;
    private readonly ICabinetteChannel _channel;
    private readonly Func<string> _workingDirectory;

    public CabinetteTool(ToolDefinition definition, ICabinetteChannel channel, Func<string> workingDirectory)
    {
        _definition = definition;
        _channel = channel;
        _workingDirectory = workingDirectory;
        ProtocolTool = new Tool
        {
            Name = definition.Name,
            Title = definition.Title,
            Description = definition.Description,
            InputSchema = definition.Input.ToJsonElement(),
            Annotations = new ToolAnnotations
            {
                Title = definition.Title,
                ReadOnlyHint = definition.ReadOnly,
                DestructiveHint = definition.Destructive,
                IdempotentHint = definition.Idempotent,
                OpenWorldHint = false,
            },
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var info = request.Server.ClientInfo;
        var client = ClientNames.FromClientInfo(info?.Name, info?.Title);
        return await CallAsync(client, request.Params?.Arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The call without the MCP plumbing (the client's display name is already worked out).</summary>
    public async Task<CallToolResult> CallAsync(string client, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (!ToolArguments.TryRead(_definition, arguments, _workingDirectory(), out var args, out var error))
        {
            return ToolResults.Error(AiErrorCodes.InvalidArgument, error);
        }

        InstanceReply reply;
        try
        {
            reply = await _channel.SendAsync(_definition.CreateMessage(client, args), ct).ConfigureAwait(false);
        }
        catch (CabinetteUnavailableException ex)
        {
            return ToolResults.Error(ToolResults.Unavailable, ex.Message);
        }

        return ToolResults.FromReply(_definition.Name, reply);
    }
}
