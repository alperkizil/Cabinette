using Cabinette.Core.Models;

namespace Cabinette.Core.Services;

/// <summary>
/// The activity log of an in-memory library (the sample data, screenshot runs, test fakes), with the same rules as
/// <see cref="ILibraryService.AddActivityAsync"/> and <see cref="ILibraryService.GetActivityAsync"/>: names are trimmed and
/// empty ones refused, times compare as instants, ids are never handed out twice, and entries older than
/// <see cref="ILibraryService.ActivityRetention"/> are never returned; they are removed at most once a day, by an append or a read
/// (a new log has nothing to remove yet). Thread-safe.
/// </summary>
public sealed class InMemoryActivityLog
{
    private readonly Lock _gate = new();
    private readonly List<ActivityEntry> _entries = [];
    private readonly TimeProvider _time;
    private DateTimeOffset _prunedAt;
    private long _nextId = 1;

    /// <param name="time">The clock for retention and for entries without a time; the system clock when null.</param>
    public InMemoryActivityLog(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _prunedAt = _time.GetUtcNow();
    }

    public ActivityEntry Add(ActivityEntry entry)
    {
        var now = _time.GetUtcNow();
        var normalized = ActivityRules.Normalize(entry, now);
        lock (_gate)
        {
            var stored = normalized with { Id = _nextId++ };
            _entries.Add(stored);
            PruneIfDueLocked(now);
            return stored;
        }
    }

    public ActivityPage Get(ActivityQuery query)
    {
        var now = _time.GetUtcNow();
        var filter = ActivityRules.Filter(query, now);
        lock (_gate)
        {
            PruneIfDueLocked(now);

            // Ids grow in list order, so the newest are at the end.
            var matches = new List<ActivityEntry>(Math.Min(filter.Limit + 1, _entries.Count));
            for (var i = _entries.Count - 1; i >= 0 && matches.Count <= filter.Limit; i--)
            {
                if (filter.Matches(_entries[i]))
                {
                    matches.Add(_entries[i]);
                }
            }

            var hasMore = matches.Count > filter.Limit;
            return new ActivityPage(hasMore ? matches[..filter.Limit] : matches, hasMore);
        }
    }

    /// <summary>Every entry kept, pruned or not, oldest first (for tests).</summary>
    internal IReadOnlyList<ActivityEntry> Stored
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    private void PruneIfDueLocked(DateTimeOffset now)
    {
        if (!ActivityRules.PruneDue(_prunedAt, now))
        {
            return;
        }

        _prunedAt = now;
        var cutoff = ActivityRules.RetentionCutoff(now);
        _entries.RemoveAll(e => e.At < cutoff);
    }
}
