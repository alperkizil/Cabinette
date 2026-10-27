using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cabinette.Core.Services;

/// <summary>
/// Stores an enum by name like <see cref="JsonStringEnumConverter"/>, but a name (or number) this build doesn't know reads
/// as the enum's default instead of failing. A newer build may add a value to a setting (a font, say); reading it must not
/// make the whole settings unreadable, or every setting would go back to its default.
/// </summary>
internal sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String when Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named):
                return named;
            case JsonTokenType.Number when reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(TEnum), number):
                return (TEnum)Enum.ToObject(typeof(TEnum), number);
            default:
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
