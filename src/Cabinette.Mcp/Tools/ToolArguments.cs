using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cabinette.Mcp.Tools;

/// <summary>
/// Turns an MCP call's arguments into the tool's args record: unknown parameters (also inside <c>filter</c>) and
/// missing required ones are refused with a message naming them, so a typo never silently drops a condition; JSON null
/// counts as omitted. Relative paths are then resolved against the working folder (SPEC.md section 10).
/// </summary>
internal static partial class ToolArguments
{
    public static bool TryRead(ToolDefinition tool, IDictionary<string, JsonElement>? arguments, string workingDirectory,
        out object args, out string error)
    {
        args = null!;
        var json = new JsonObject();
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                json[key] = JsonSerializer.SerializeToNode(value);
            }
        }

        if (!TryCheck(tool.Input, json, prefix: "", tool.Name, out error))
        {
            return false;
        }

        object? parsed;
        try
        {
            parsed = tool.Deserialize(json);
        }
        catch (JsonException ex)
        {
            error = WrongType(tool.Input, ex);
            return false;
        }

        if (parsed is null)
        {
            error = "the arguments must be an object.";
            return false;
        }

        args = tool.ResolvePaths(parsed, path => ResolvePath(path, workingDirectory));
        error = "";
        return true;
    }

    /// <summary>
    /// A relative path made full against the working folder (the AI client's project folder). Surrounding whitespace and
    /// one pair of matching quotes (models often quote a path copied from a shell) are dropped first, so
    /// <c>"C:\Data\x.pdf"</c> is the full path it names. Full paths are passed on unquoted; blank text and paths Windows
    /// can't parse stay as they are (Cabinette then explains); <c>~</c> isn't expanded.
    /// </summary>
    public static string ResolvePath(string path, string workingDirectory)
    {
        var unquoted = Unquote(path);
        if (string.IsNullOrWhiteSpace(unquoted))
        {
            return path;
        }

        if (Path.IsPathFullyQualified(unquoted))
        {
            return unquoted;
        }

        try
        {
            return Path.GetFullPath(unquoted, workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>The text without surrounding whitespace and one pair of matching <c>"</c> or <c>'</c> around it.</summary>
    public static string Unquote(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length >= 2 && trimmed[0] is ('"' or '\'') && trimmed[^1] == trimmed[0] ? trimmed[1..^1] : trimmed;
    }

    /// <summary>Checks names against the schema (case-insensitive, like the deserializer) and renames them to its spelling.</summary>
    private static bool TryCheck(ObjectSchema schema, JsonObject json, string prefix, string toolName, out string error)
    {
        foreach (var (key, value) in json.ToList())
        {
            if (schema.Find(key, out var canonical, out var nested) is null)
            {
                var known = schema.PropertyNames.Count == 0 ? "it takes no parameters" : "its parameters are " + string.Join(", ", schema.PropertyNames.Select(p => prefix + p));
                error = $"unknown parameter \"{prefix}{key}\" ({toolName}: {known}).";
                return false;
            }

            if (value is null)
            {
                json.Remove(key);
                continue;
            }

            if (canonical != key)
            {
                json.Remove(key);
                json[canonical] = value;
            }

            if (nested is not null && value is JsonObject inner && !TryCheck(nested, inner, prefix + canonical + ".", toolName, out error))
            {
                return false;
            }
        }

        foreach (var required in schema.Required)
        {
            if (!json.ContainsKey(required))
            {
                error = $"\"{prefix}{required}\" is required ({toolName}).";
                return false;
            }
        }

        error = "";
        return true;
    }

    /// <summary>"\"fileIds[0]\" must be an integer.", from the deserializer's JSON path.</summary>
    private static string WrongType(ObjectSchema schema, JsonException ex)
    {
        var path = ex.Path is { Length: > 2 } p && p.StartsWith("$.", StringComparison.Ordinal) ? p[2..] : null;
        if (path is null)
        {
            return "the arguments don't fit the tool's input schema.";
        }

        // Find the schema of the property the path names (array indexes select the item schema).
        JsonNode? node = null;
        var current = schema;
        foreach (Match segment in PathSegment().Matches(path))
        {
            if (segment.Groups["index"].Success)
            {
                node = node?["items"];
                continue;
            }

            if (current is null)
            {
                node = null;
                break;
            }

            node = current.Find(segment.Groups["name"].Value, out _, out var nested);
            current = nested;
        }

        return node is null ? $"\"{path}\" has the wrong type." : $"\"{path}\" must be {Json.Describe(node)}.";
    }

    [GeneratedRegex(@"(?<name>[^.\[\]]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathSegment();
}
