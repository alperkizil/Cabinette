using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cabinette.Ipc;

/// <summary>Commands understood by the running instance.</summary>
public static class InstanceCommands
{
    /// <summary>Show, restore and bring the main window to the front.</summary>
    public const string Activate = "activate";

    /// <summary>
    /// Add files like an Explorer entry (stage 3): <c>{"command":"add","mode":"copy|pointer","paths":[...]}</c>, one
    /// selection as one batch. Sent by a copy that COM started for an Explorer entry while another copy runs.
    /// The running copy replies once it has taken the batch, not when the files are added.
    /// </summary>
    public const string Add = "add";

    /// <summary>
    /// One tool call from an AI client through cabinette-mcp.exe (stage 4, SPEC.md section 10):
    /// <c>{"command":"ai","version":1,"client":"Claude Code","tool":"search","args":{...}}</c>. The reply carries the
    /// tool's result in <see cref="InstanceReply.Result"/>, or an error with an <see cref="AiErrorCodes">error code</see>.
    /// The running copy replies when the call is done (vault operations reply once they have started).
    /// </summary>
    public const string Ai = "ai";
}

/// <summary>One request from another Cabinette process (one JSON object per line).</summary>
public sealed record InstanceMessage
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    /// <summary>For "add": "copy" or "pointer".</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>For "add": the files and folders to add.</summary>
    [JsonPropertyName("paths")]
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>For "ai": <see cref="AiProtocol.Version"/> of the sender.</summary>
    [JsonPropertyName("version")]
    public int? Version { get; init; }

    /// <summary>For "ai": the AI client's display name ("Claude Code", "Codex"), used in the activity log and toasts.</summary>
    [JsonPropertyName("client")]
    public string? Client { get; init; }

    /// <summary>For "ai": the tool name (<see cref="AiTools"/>).</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>For "ai": the tool's arguments, one of the <c>*Args</c> records in <see cref="AiProtocol"/>'s file.</summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }

    public static InstanceMessage Activate() => new() { Command = InstanceCommands.Activate };

    /// <summary>An "add" message for one Explorer selection (<paramref name="mode"/> is "copy" or "pointer").</summary>
    public static InstanceMessage Add(string mode, IReadOnlyList<string> paths)
        => new() { Command = InstanceCommands.Add, Mode = mode, Paths = paths };

    /// <summary>An "ai" message for one tool call.</summary>
    public static InstanceMessage Ai<TArgs>(string client, string tool, TArgs args) => new()
    {
        Command = InstanceCommands.Ai,
        Version = AiProtocol.Version,
        Client = client,
        Tool = tool,
        Args = JsonSerializer.SerializeToElement(args, AiProtocol.JsonOptions),
    };
}

/// <summary>The running instance's answer to one message (one JSON object per line).</summary>
public sealed record InstanceReply
{
    public static InstanceReply Success { get; } = new() { Ok = true };

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>For "ai" errors: one of <see cref="AiErrorCodes"/>, so the caller can tell "busy" or "not ready" from a failure.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>For "ai": the tool's result, one of the <c>*Result</c> records in <see cref="AiProtocol"/>'s file.</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    public static InstanceReply Failed(string error) => new() { Ok = false, Error = error };

    /// <summary>A failed "ai" call: <paramref name="code"/> is one of <see cref="AiErrorCodes"/>; the message is for the AI to read.</summary>
    public static InstanceReply Failed(string code, string error) => new() { Ok = false, Code = code, Error = error };

    /// <summary>A successful "ai" call.</summary>
    public static InstanceReply WithResult<TResult>(TResult result)
        => new() { Ok = true, Result = JsonSerializer.SerializeToElement(result, AiProtocol.JsonOptions) };
}

/// <summary>
/// The local channel's wire format: UTF-8, one JSON object per line (<c>\n</c>), for example
/// <c>{"command":"activate"}</c>. Each message gets one reply line such as <c>{"ok":true}</c>.
/// </summary>
public static class InstanceProtocol
{
    /// <summary>
    /// Longest accepted line, in characters: an "add" with a few hundred thousand paths still fits (SPEC 9: the Explorer
    /// entries take any number of items, also when a selection is passed on over this channel). Also the longest reply
    /// cabinette-mcp.exe reads: a text page of a file or a scaled-down image in base64 is far below it.
    /// </summary>
    public const int MaxLineLength = 32 * 1024 * 1024;

    public const string CopyMode = "copy";
    public const string PointerMode = "pointer";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Compact JSON on one line (string contents are escaped, so it never contains a raw line break).</summary>
    public static string Serialize(InstanceMessage message) => JsonSerializer.Serialize(message, Options);

    public static string Serialize(InstanceReply reply) => JsonSerializer.Serialize(reply, Options);

    /// <summary>Parses one line. The command is trimmed and lower-cased; unknown fields are ignored.</summary>
    public static bool TryParseMessage(string? line, out InstanceMessage message, out string error)
    {
        message = new InstanceMessage();
        error = "";
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "empty message";
            return false;
        }

        if (line.Length > MaxLineLength)
        {
            error = "message too long";
            return false;
        }

        InstanceMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<InstanceMessage>(line, Options);
        }
        catch (JsonException)
        {
            error = "invalid JSON";
            return false;
        }

        var command = parsed?.Command?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(command))
        {
            error = "missing command";
            return false;
        }

        message = parsed! with
        {
            Command = command,
            Mode = parsed.Mode?.Trim().ToLowerInvariant(),
            Paths = parsed.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            Tool = parsed.Tool?.Trim().ToLowerInvariant(),
        };
        return true;
    }

    public static InstanceReply? TryParseReply(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InstanceReply>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
