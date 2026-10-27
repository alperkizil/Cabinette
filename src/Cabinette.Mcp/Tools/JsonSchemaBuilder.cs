using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cabinette.Mcp.Tools;

/// <summary>
/// A small builder for the tools' input schemas (JSON Schema objects). The properties mirror the <c>*Args</c> records in
/// AiProtocol.cs; a test checks that names, required properties and descriptions match the records.
/// </summary>
internal sealed class ObjectSchema
{
    private readonly List<(string Name, JsonObject Schema, ObjectSchema? Nested)> _properties = [];
    private readonly List<string> _required = [];

    /// <summary>The property names in order (camelCase, as in the args record's JSON).</summary>
    public IReadOnlyList<string> PropertyNames => _properties.Select(p => p.Name).ToList();

    public IReadOnlyList<string> Required => _required;

    public ObjectSchema Property(string name, JsonObject schema, string description, bool required = false)
        => Add(name, schema, null, description, required);

    /// <summary>A property that is an object with its own properties (checked like the top level).</summary>
    public ObjectSchema Property(string name, ObjectSchema nested, string description, bool required = false)
        => Add(name, nested.ToJsonObject(), nested, description, required);

    /// <summary>The schema of a property (case-insensitive name), or null.</summary>
    public JsonObject? Find(string name, out string canonicalName, out ObjectSchema? nested)
    {
        foreach (var property in _properties)
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                canonicalName = property.Name;
                nested = property.Nested;
                return property.Schema;
            }
        }

        canonicalName = name;
        nested = null;
        return null;
    }

    public JsonObject ToJsonObject()
    {
        var properties = new JsonObject();
        foreach (var (name, schema, _) in _properties)
        {
            properties[name] = schema.DeepClone();
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (_required.Count > 0)
        {
            result["required"] = new JsonArray([.. _required.Select(r => JsonValue.Create(r))]);
        }

        result["additionalProperties"] = false;
        return result;
    }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(ToJsonObject());

    private ObjectSchema Add(string name, JsonObject schema, ObjectSchema? nested, string description, bool required)
    {
        schema["description"] = description;
        _properties.Add((name, schema, nested));
        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}

/// <summary>Property schemas.</summary>
internal static class Json
{
    public static JsonObject Integer(long? minimum = null, long? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (minimum is not null)
        {
            schema["minimum"] = minimum;
        }

        if (maximum is not null)
        {
            schema["maximum"] = maximum;
        }

        return schema;
    }

    public static JsonObject String(int? minLength = null, string? format = null, IReadOnlyList<string>? oneOf = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (minLength is not null)
        {
            schema["minLength"] = minLength;
        }

        if (format is not null)
        {
            schema["format"] = format;
        }

        if (oneOf is not null)
        {
            schema["enum"] = new JsonArray([.. oneOf.Select(v => JsonValue.Create(v))]);
        }

        return schema;
    }

    public static JsonObject Boolean() => new() { ["type"] = "boolean" };

    public static JsonObject Array(JsonObject items, int? minItems = null, int? maxItems = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = items };
        if (minItems is not null)
        {
            schema["minItems"] = minItems;
        }

        if (maxItems is not null)
        {
            schema["maxItems"] = maxItems;
        }

        return schema;
    }

    /// <summary>What a value of this schema is, for error messages ("an integer", "a list of integers").</summary>
    public static string Describe(JsonNode? schema) => (string?)schema?["type"] switch
    {
        "integer" => "an integer",
        "boolean" => "true or false",
        "object" => "an object",
        "array" => "a list of " + Plural(schema!["items"]),
        "string" when (string?)schema!["format"] == "date-time" => "a date and time (ISO 8601)",
        "string" when schema!["enum"] is JsonArray values => "one of " + string.Join(", ", values.Select(v => $"\"{v}\"")),
        "string" => "a string",
        _ => "a valid value",
    };

    private static string Plural(JsonNode? items) => (string?)items?["type"] switch
    {
        "integer" => "integers",
        "string" => "strings",
        _ => "values",
    };
}
