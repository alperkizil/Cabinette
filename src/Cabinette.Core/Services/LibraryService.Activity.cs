using System.Diagnostics;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Services;

// The AI activity log (stage 4, SPEC.md section 10): the activity_log table, kept for a year. The rules it shares with
// InMemoryActivityLog are in ActivityRules.
//
// Appends don't take _writeLock. The AI logs every call, reads included, and _writeLock can be held for minutes or hours: Move
// vault holds it while it copies and verifies the whole vault, a backup while it zips the vault (Migrate takes it once per file,
// briefly). An append is one INSERT in a short IMMEDIATE transaction of its own instead, and SQLite's busy timeout (10 s,
// Database.Open) lets it wait for a library write transaction in progress; those are short, since copying, hashing and the
// Recycle Bin all happen outside them (Move vault and Backup hold no SQLite transaction while they copy or zip).
// Why that is safe: nothing _writeLock protects involves the log. No other table refers to it, no library write reads it, and
// Move vault and Migrate never look at it. A backup's snapshot (VACUUM INTO) is a read transaction, so it holds exactly the
// entries committed before it; the vault files it must match are untouched by appends.
// _activityLock serializes appends and prune batches among themselves and lets DisposeAsync wait for one in flight, so nothing
// opens the database after it (a restore may rename the database file right after).
internal sealed partial class LibraryService
{
    /// <summary>Rows a prune deletes per transaction, so appends never wait long behind it.</summary>
    internal const int ActivityPruneBatchSize = 1000;

    // The distinct client names in the log: one seek on ix_activity_log_source per name, however many entries there are.
    private const string DistinctActivitySourcesSql = """
        WITH RECURSIVE s(source) AS (
          SELECT MIN(source) FROM activity_log
          UNION ALL
          SELECT (SELECT MIN(source) FROM activity_log WHERE source > s.source) FROM s WHERE s.source IS NOT NULL)
        SELECT source FROM s WHERE source IS NOT NULL
        """;

    private readonly SemaphoreSlim _activityLock = new(1, 1);
    private readonly Lock _activityPruneGate = new();
    private DateTimeOffset _activityPrunedAt;
    private Task _activityPrune = Task.CompletedTask;

    /// <summary>Completes when the latest prune of the activity log (when the library opened, or the daily one) is done. Never faults (for tests).</summary>
    internal Task WhenActivityPrunedAsync()
    {
        lock (_activityPruneGate)
        {
            return _activityPrune;
        }
    }

    public Task<ActivityEntry> AddActivityAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        var normalized = ActivityRules.Normalize(entry, _time.GetUtcNow());
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            await _activityLock.WaitAsync(ct).ConfigureAwait(false);
            ActivityEntry stored;
            try
            {
                ThrowIfDisposed();
                stored = InsertActivity(normalized);
            }
            finally
            {
                _activityLock.Release();
            }

            // No Changed event: the app tells its own viewer, and the chat list would reload on every AI call.
            PruneActivityIfDue();
            return stored;
        }, ct);
    }

    public Task<ActivityPage> GetActivityAsync(ActivityQuery query, CancellationToken ct = default)
    {
        // The filter leaves out what is past the retention cutoff, pruned or not. A library left open without AI calls (in the
        // tray) gets its daily prune from here, from the viewer reading the log.
        var filter = ActivityRules.Filter(query, _time.GetUtcNow());
        PruneActivityIfDue();
        return ReadAsync(connection =>
        {
            // One snapshot for the source names and the page.
            using var tx = connection.BeginTransaction(deferred: true);
            var where = new List<string>();
            var p = new DynamicParameters();

            // Times are stored as fixed-width UTC text (DbValues) and the bounds are converted the same way, so comparing the text
            // compares the instants, whatever offset the caller's times have (DST included). The unary + keeps SQLite off
            // ix_activity_log_at: walking the ids from the newest stops after one page, while a range on that index sorts every
            // entry in it first. Measured at a million entries: a year's range 0.3 s on the index, 0.1 ms walking the ids; an old
            // month 26 ms on the index, 95 ms walking (the worst case).
            where.Add("+at >= @from");
            p.Add("from", DbValues.ToText(filter.From));

            if (filter.To is { } to)
            {
                where.Add("+at < @to");
                p.Add("to", DbValues.ToText(to));
            }

            if (filter.Actions is { } actions)
            {
                where.Add("action IN (SELECT value FROM json_each(@actions))");
                p.Add("actions", DbValues.JsonArray(actions));
            }

            if (filter.Source is { } source)
            {
                // SQLite's NOCASE folds only ASCII letters. The few names stored are compared here instead (as InMemoryActivityLog
                // does), then matched exactly.
                var spellings = connection.Query<string>(DistinctActivitySourcesSql, transaction: tx)
                    .Where(stored => ActivityRules.SourceMatches(stored, source))
                    .ToList();
                if (spellings.Count == 0)
                {
                    return new ActivityPage([], false);
                }

                where.Add("source IN (SELECT value FROM json_each(@sources))");
                p.Add("sources", DbValues.JsonArray(spellings));
            }

            if (filter.BeforeId is { } before)
            {
                where.Add("id < @before");
                p.Add("before", before);
            }

            // One row more than the limit tells whether there are more.
            p.Add("take", filter.Limit + 1);
            var sql = ActivityRow.SelectSql
                      + " WHERE " + string.Join(" AND ", where)
                      + " ORDER BY id DESC LIMIT @take";
            var entries = connection.Query<ActivityRow>(sql, p, tx).Select(row => row.ToEntry()).ToList();
            tx.Commit();
            var hasMore = entries.Count > filter.Limit;
            if (hasMore)
            {
                entries.RemoveRange(filter.Limit, entries.Count - filter.Limit);
            }

            return new ActivityPage(entries, hasMore);
        }, ct);
    }

    private ActivityEntry InsertActivity(ActivityEntry entry)
    {
        try
        {
            using var connection = _db.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var id = connection.ExecuteScalar<long>(
                "INSERT INTO activity_log(at, source, action, details_json) VALUES (@at, @source, @action, @details) RETURNING id",
                new { at = DbValues.ToText(entry.At), source = entry.Source, action = entry.Action, details = entry.DetailsJson }, tx);
            tx.Commit();
            return entry with { Id = id };
        }
        catch (SqliteException ex)
        {
            throw new LibraryException(LibraryErrorCode.Unknown, $"The AI activity log could not be written: {ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------- retention

    /// <summary>Removes the entries older than a year in the background (called once the library is open). Never fails the open.</summary>
    private void StartActivityPrune()
    {
        lock (_activityPruneGate)
        {
            StartActivityPruneLocked(_time.GetUtcNow());
        }
    }

    /// <summary>
    /// After an append or with a read: prunes again (in the background) when a day has passed since the last prune, unless one is
    /// still running. Cheap when it isn't due: a lock and a comparison.
    /// </summary>
    private void PruneActivityIfDue()
    {
        var now = _time.GetUtcNow();
        lock (_activityPruneGate)
        {
            if (_disposed || !_activityPrune.IsCompleted || !ActivityRules.PruneDue(_activityPrunedAt, now))
            {
                return;
            }

            StartActivityPruneLocked(now);
        }
    }

    private void StartActivityPruneLocked(DateTimeOffset now)
    {
        _activityPrunedAt = now;
        _activityPrune = Task.Run(() => PruneActivityAsync(now), CancellationToken.None);
    }

    /// <summary>Deletes the entries from before the retention cutoff in short batches. Never throws: what's left goes next time.</summary>
    private async Task PruneActivityAsync(DateTimeOffset now)
    {
        var cutoff = DbValues.ToText(ActivityRules.RetentionCutoff(now));
        try
        {
            int removed;
            do
            {
                await _activityLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed)
                    {
                        return;
                    }

                    using var connection = _db.Open();
                    using var tx = connection.BeginTransaction(deferred: false);
                    removed = connection.Execute(
                        "DELETE FROM activity_log WHERE id IN (SELECT id FROM activity_log WHERE at < @cutoff LIMIT @batch)",
                        new { cutoff, batch = ActivityPruneBatchSize }, tx);
                    tx.Commit();
                }
                finally
                {
                    _activityLock.Release();
                }
            }
            while (removed == ActivityPruneBatchSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cabinette: pruning the AI activity log failed; it is tried again tomorrow: {ex}");
        }
    }
}
