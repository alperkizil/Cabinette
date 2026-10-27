using Cabinette.Core.Models;

namespace Cabinette.Core.Services;

/// <summary>
/// The rules of the AI activity log that the library (<see cref="ILibraryService.AddActivityAsync"/>,
/// <see cref="ILibraryService.GetActivityAsync"/>) and <see cref="InMemoryActivityLog"/> share, so both behave alike.
/// </summary>
internal static class ActivityRules
{
    public const int MinLimit = 1;
    public const int MaxLimit = 1000;

    /// <summary>After the prune when the log opens, entries are pruned again at most this often (triggered by an append or a read).</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The entry as it is stored: Source and Action trimmed (empty ones are refused), At as a UTC instant (the clock's time when
    /// it was left unset). The id is assigned by the store.
    /// </summary>
    public static ActivityEntry Normalize(ActivityEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry with
        {
            Id = 0,
            At = (entry.At == default ? now : entry.At).ToUniversalTime(),
            Source = Required(entry.Source, "source"),
            Action = Required(entry.Action, "action"),
        };
    }

    /// <summary>
    /// The query's conditions with the limit clamped and the names trimmed the way stored names are. A blank Source means every
    /// client, like a null one (no stored entry has a blank source); a blank tool name in Actions matches nothing. From is never
    /// before the <see cref="RetentionCutoff"/> at <paramref name="now"/>: entries past it are never returned, whether the prune
    /// has removed them yet or not (it runs at most once a day).
    /// </summary>
    public static ActivityFilter Filter(ActivityQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var actions = query.Actions is { Count: > 0 } list
            ? list.Select(a => a?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToArray()
            : null;
        var source = string.IsNullOrWhiteSpace(query.Source) ? null : query.Source.Trim();
        var cutoff = RetentionCutoff(now);
        var from = query.From is { } wanted && wanted > cutoff ? wanted : cutoff;
        return new ActivityFilter(from, query.To, actions, source, query.BeforeId, Math.Clamp(query.Limit, MinLimit, MaxLimit));
    }

    /// <summary>Source names match whole, ignoring case.</summary>
    public static bool SourceMatches(string stored, string wanted) => string.Equals(stored, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>Entries from before this instant are removed by a prune at <paramref name="now"/>.</summary>
    public static DateTimeOffset RetentionCutoff(DateTimeOffset now) => now - ILibraryService.ActivityRetention;

    /// <summary>A day has passed since the last prune, or the clock went back past it.</summary>
    public static bool PruneDue(DateTimeOffset lastPrune, DateTimeOffset now) => now - lastPrune >= PruneInterval || now < lastPrune;

    private static string Required(string? value, string name)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? throw new ArgumentException($"An activity entry needs a {name}.", "entry")
            : trimmed;
    }
}

/// <summary>An <see cref="ActivityQuery"/> after <see cref="ActivityRules.Filter"/>: every condition set must match.</summary>
/// <param name="From">The query's From or the retention cutoff, whichever is later.</param>
/// <param name="Actions">Null: all. Otherwise the entry's action must be one of these (exact).</param>
/// <param name="Source">Null: all. Otherwise the entry's source must be this one (<see cref="ActivityRules.SourceMatches"/>).</param>
internal sealed record ActivityFilter(DateTimeOffset From, DateTimeOffset? To, IReadOnlyList<string>? Actions, string? Source, long? BeforeId,
    int Limit)
{
    /// <summary>The reference definition; the library's SQL query implements the same conditions.</summary>
    public bool Matches(ActivityEntry entry)
        => entry.At >= From
           && (To is not { } to || entry.At < to)
           && (Actions is not { } actions || actions.Contains(entry.Action, StringComparer.Ordinal))
           && (Source is not { } source || ActivityRules.SourceMatches(entry.Source, source))
           && (BeforeId is not { } before || entry.Id < before);
}
