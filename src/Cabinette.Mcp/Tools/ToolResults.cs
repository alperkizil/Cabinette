using System.Buffers.Text;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinette.Ipc;
using ModelContextProtocol.Protocol;

namespace Cabinette.Mcp.Tools;

/// <summary>Turns Cabinette's reply into the MCP tool result the model reads.</summary>
internal static class ToolResults
{
    /// <summary>
    /// The error code for "Cabinette can't be reached or started" (cabinette-mcp.exe's own; Cabinette's codes are
    /// <see cref="AiErrorCodes"/>).
    /// </summary>
    public const string Unavailable = "unavailable";

    /// <summary>The records' JSON (camelCase), with non-ASCII names kept readable instead of \u escapes.</summary>
    private static readonly JsonSerializerOptions Output = new(AiProtocol.JsonOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CallToolResult FromReply(string tool, InstanceReply reply)
    {
        if (!reply.Ok)
        {
            return FromError(reply.Code, reply.Error);
        }

        if (reply.Result is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } result)
        {
            return Error(AiErrorCodes.Failed, "Cabinette answered without a result.");
        }

        return tool switch
        {
            AiTools.ReadFile => FromReadFile(result),
            AiTools.ListFiles => Text(WithNextOffset(JsonSerializer.SerializeToNode(result, Output))),
            _ => Text(JsonSerializer.Serialize(result, Output)),
        };
    }

    /// <summary>The <c>nextOffset</c> property of list_files results and read_file metadata (camelCase, like the records).</summary>
    public const string NextOffsetProperty = "nextOffset";

    /// <summary>
    /// The JSON with <c>"nextOffset": null</c> when it has none. The protocol leaves nulls out, but the tool descriptions
    /// promise null on the last page, and a model shouldn't have to guess what a missing property means.
    /// </summary>
    private static string WithNextOffset(JsonNode? node)
    {
        if (node is JsonObject json && !json.ContainsKey(NextOffsetProperty))
        {
            json[NextOffsetProperty] = null;
        }

        return node?.ToJsonString(Output) ?? "null";
    }

    /// <summary>An MCP tool error (<c>isError</c>) whose text starts with the code: "busy: ...".</summary>
    public static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{code}: {message}" }],
    };

    private static CallToolResult FromError(string? code, string? message)
    {
        message = string.IsNullOrWhiteSpace(message) ? "(Cabinette gave no details)" : message.Trim();
        if (code is null)
        {
            // A Cabinette from before stage 4 answers "unknown command 'ai'" without a code.
            return message.StartsWith("unknown command", StringComparison.OrdinalIgnoreCase)
                ? Error(AiErrorCodes.Unsupported, $"{Sentence(message)} This Cabinette is older than cabinette-mcp.exe and has no AI tools: update both (reinstall Cabinette).")
                : Error(AiErrorCodes.Failed, message);
        }

        if (code == AiErrorCodes.Unsupported)
        {
            // Every tool cabinette-mcp.exe offers exists in its own protocol version, so this means another version.
            return Error(code, $"{Sentence(message)} Cabinette and cabinette-mcp.exe must be the same version (AI protocol {AiProtocol.Version} here): update both (reinstall Cabinette).");
        }

        return Error(code, message);
    }

    /// <summary>
    /// A text page as two blocks, the metadata as JSON and the text itself (so code isn't JSON-escaped); an image as an
    /// MCP image block plus its metadata without the base64.
    /// </summary>
    private static CallToolResult FromReadFile(JsonElement result)
    {
        var page = AiProtocol.FromElement<ReadFileResult>(result);
        if (page is null)
        {
            return Error(AiErrorCodes.Failed, "Cabinette's read_file answer couldn't be read.");
        }

        if (page.Kind == "image")
        {
            if (string.IsNullOrEmpty(page.ImageBase64) || string.IsNullOrWhiteSpace(page.MimeType) || !Base64.IsValid(page.ImageBase64))
            {
                return Error(AiErrorCodes.Failed, "Cabinette's image answer has no valid image data.");
            }

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = WithNextOffset(JsonSerializer.SerializeToNode(page with { ImageBase64 = null }, Output)) },
                    new ImageContentBlock { Data = Encoding.ASCII.GetBytes(page.ImageBase64), MimeType = page.MimeType },
                ],
            };
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = WithNextOffset(JsonSerializer.SerializeToNode(page with { Text = null }, Output)) },
                new TextContentBlock { Text = page.Text ?? "" },
            ],
        };
    }

    private static string Sentence(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
}
