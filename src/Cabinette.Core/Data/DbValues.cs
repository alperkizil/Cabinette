using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cabinette.Core.Data;

/// <summary>Conversions between .NET values and the database representation (ISO-8601 UTC text, JSON arrays for json_each).</summary>
internal static class DbValues
{
    // Fixed width, so text comparison in SQL orders correctly.
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToText(DateTimeOffset value)
        => value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static string? ToText(DateTimeOffset? value)
        => value is { } v ? ToText(v) : null;

    public static string ToText(DateTime utc)
        => ToText(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public static DateTimeOffset ParseTime(string text)
    {
        if (TryParseFixed(text, out var fast))
        {
            return fast;
        }

        if (DateTimeOffset.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            return exact;
        }

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public static DateTimeOffset? ParseTimeOrNull(string? text)
        => string.IsNullOrEmpty(text) ? null : ParseTime(text);

    // A wall-clock time with its offset (a photo's date taken): what the camera showed stays what it showed, whatever this PC's
    // zone is later. ParseTime still reads it (as the instant), so earlier builds open such rows.
    private const string OffsetTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    public static string? ToTextWithOffset(DateTimeOffset? value)
        => value?.ToString(OffsetTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Reads <see cref="ToTextWithOffset"/>'s text keeping its offset; older UTC text comes back with offset zero.</summary>
    public static DateTimeOffset ParseTimeWithOffset(string text)
        => DateTimeOffset.TryParseExact(text, OffsetTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : ParseTime(text);

    /// <summary>Fast path for our own fixed format "yyyy-MM-ddTHH:mm:ss.fffffffZ" (big threads parse many of these).</summary>
    private static bool TryParseFixed(string s, out DateTimeOffset value)
    {
        value = default;
        if (s.Length != 28 || s[4] != '-' || s[7] != '-' || s[10] != 'T' || s[13] != ':' || s[16] != ':' || s[19] != '.' || s[27] != 'Z')
        {
            return false;
        }

        var year = Digits(s, 0, 4);
        var month = Digits(s, 5, 2);
        var day = Digits(s, 8, 2);
        var hour = Digits(s, 11, 2);
        var minute = Digits(s, 14, 2);
        var second = Digits(s, 17, 2);
        var fraction = Digits(s, 20, 7);
        if ((year | month | day | hour | minute | second | fraction) < 0
            || year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var ticks = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks + fraction;
        value = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static int Digits(string s, int start, int count)
    {
        var n = 0;
        for (var i = start; i < start + count; i++)
        {
            var d = s[i] - '0';
            if ((uint)d > 9)
            {
                return -1;
            }

            n = (n * 10) + d;
        }

        return n;
    }

    /// <summary>JSON array of integers, for <c>json_each(@param)</c>.</summary>
    public static string JsonArray(IEnumerable<long> values)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var v in values)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(v.ToString(CultureInfo.InvariantCulture));
            first = false;
        }

        return sb.Append(']').ToString();
    }

    /// <summary>JSON array of strings, for <c>json_each(@param)</c>.</summary>
    public static string JsonArray(IEnumerable<string> values) => JsonSerializer.Serialize(values);

    /// <summary>LIKE pattern matching <paramref name="text"/> anywhere. Use with <c>ESCAPE '\'</c>.</summary>
    public static string LikeContains(string text) => "%" + LikeEscape(text) + "%";

    /// <summary>LIKE pattern matching text that starts with <paramref name="prefix"/>. Use with <c>ESCAPE '\'</c>.</summary>
    public static string LikeStartsWith(string prefix) => LikeEscape(prefix) + "%";

    private static string LikeEscape(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("%", "\\%", StringComparison.Ordinal)
               .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>An FTS5 string literal (phrase). Everything inside is taken literally.</summary>
    public static string FtsPhrase(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
