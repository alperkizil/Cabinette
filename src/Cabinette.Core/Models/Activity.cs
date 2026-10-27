namespace Cabinette.Core.Models;

/// <summary>One entry of the AI activity log (SPEC.md section 10): one tool call by an AI client.</summary>
public sealed record ActivityEntry
{
    /// <summary>0 for a new entry; the stored entry's id otherwise (ids grow with time).</summary>
    public long Id { get; init; }

    /// <summary>When the call was made.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The AI client's display name ("Claude Code", "Codex").</summary>
    public required string Source { get; init; }

    /// <summary>The tool name ("add_files", "delete_files", ...).</summary>
    public required string Action { get; init; }

    /// <summary>What the call named and did, as JSON written and read by the app. Opaque to the library.</summary>
    public string? DetailsJson { get; init; }
}

/// <summary>Which activity entries to read: newest first, in pages. Every condition set must match.</summary>
public sealed record ActivityQuery
{
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only these tool names. Null or empty: all.</summary>
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>Only this client (exact, case-insensitive).</summary>
    public string? Source { get; init; }

    /// <summary>Paging: only entries with a smaller id (the last id of the previous page).</summary>
    public long? BeforeId { get; init; }

    /// <summary>At most this many entries (1–1000).</summary>
    public int Limit { get; init; } = 200;
}

/// <summary>One page of the activity log, newest first.</summary>
/// <param name="HasMore">Older entries match too; ask again with <see cref="ActivityQuery.BeforeId"/> = the last entry's id.</param>
public sealed record ActivityPage(IReadOnlyList<ActivityEntry> Entries, bool HasMore);
