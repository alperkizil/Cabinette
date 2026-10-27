using System.Globalization;
using System.Text;

namespace Cabinette.Mcp;

/// <summary>
/// The name Cabinette shows for the AI client (activity log, toasts: "Claude Code added 3 files to Images"), from what
/// the client said about itself in the MCP <c>initialize</c> request (<c>clientInfo</c>).
/// </summary>
internal static class ClientNames
{
    public const string Fallback = "AI client";

    public const int MaxLength = 64;

    // What the clients really send (read from their code, 2026-10-24):
    // - Claude Code 2.1.x: {"name":"claude-code","title":"Claude Code",...} (its MCP client in the shipped claude.exe).
    // - Codex 0.159: {"name":"codex-mcp-client","title":"Codex",...} (codex-rs/codex-mcp/src/rmcp_client.rs). Older
    //   versions sent no title, hence the name mapping.
    private static readonly Dictionary<string, string> KnownClients = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-code"] = "Claude Code",
        ["codex-mcp-client"] = "Codex",
        ["codex"] = "Codex",
    };

    /// <summary>"Claude Code", "Codex", else the client's title, else its name, else <see cref="Fallback"/>.</summary>
    public static string FromClientInfo(string? name, string? title)
    {
        if (name is not null && KnownClients.TryGetValue(name.Trim(), out var known))
        {
            return known;
        }

        return Sanitize(title) ?? Sanitize(name) ?? Fallback;
    }

    /// <summary>
    /// Trimmed, runs of white space (including line breaks) collapsed to one space, control and invisible format
    /// characters (such as right-to-left overrides) removed, at most <see cref="MaxLength"/> characters. Null when
    /// nothing is left.
    /// </summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxLength + 1));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length > MaxLength)
            {
                break;
            }
        }

        if (builder.Length > MaxLength)
        {
            builder.Length = MaxLength;
        }

        // Never end on half of a surrogate pair.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        var result = builder.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }
}
