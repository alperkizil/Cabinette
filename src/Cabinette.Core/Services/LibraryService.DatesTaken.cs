using System.Diagnostics;
using System.Threading.Channels;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;
using Dapper;

namespace Cabinette.Core.Services;

// Date taken of photos and videos (SPEC.md section 4, "Gallery"): read from the files by LibraryOptions.DateTakenReader, in the
// background, into item_dates (see Schema.sql), and shown as LibraryItem.TakenAt.
//
// One worker reads, in this order:
// - items named by a change the library raised (added files, files found again, vault files the monitor saw change, pointer
//   files whose new size or time a write noticed, renames): a photo or video without a row is read, one with a row is read
//   again when its file's size or last-write time is no longer the one it was read with;
// - once per opening, every photo or video without a row (the files from before dates were read, resumed after a restart since
//   each batch is stored), and every pointer file, which nothing watches, so a changed one is read again.
// Files are read outside the write lock, in batches, each stored in one short write. The pass pauses after each full batch for as
// long as reading it took, so it uses the disk half the time at most; changed items are read at once. Items whose date taken
// changed are raised as Items changes, at most once a second (and when the worker runs out of work), so open threads redraw them.
internal sealed partial class LibraryService
{
    /// <summary>Files read per batch (and rows per write).</summary>
    internal const int DateTakenBatchSize = 32;

    /// <summary>Changes for dates that landed are raised at most this often while the worker is busy.</summary>
    private static readonly TimeSpan DateTakenEventInterval = TimeSpan.FromSeconds(1);

    /// <summary>The pass's longest pause after a full batch (the pause is as long as reading it took, so it reads half the time at most).</summary>
    private static readonly TimeSpan DateTakenMaxPause = TimeSpan.FromMilliseconds(500);

    /// <summary>Looking for a file that took this long and found nothing: its drive or share is taken as unreachable for the pass.</summary>
    private static readonly TimeSpan UnreachableAfter = TimeSpan.FromSeconds(2);

    private readonly IDateTakenReader? _dateTakenReader;
    private readonly Channel<long> _dateTakenQueue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _dateTakenGate = new();
    private TaskCompletionSource _dateTakenIdle = CompletedSource();
    private CancellationTokenSource? _dateTakenCts;
    private Task _dateTakenWorker = Task.CompletedTask;

    /// <summary>
    /// Completes when the date-taken worker has nothing left to do: no queued item, the pass over the library done, and the
    /// changes for what it stored raised (for tests). Already complete without a reader.
    /// </summary>
    internal Task WhenDatesTakenIdleAsync()
    {
        lock (_dateTakenGate)
        {
            return _dateTakenIdle.Task;
        }
    }

    /// <summary>Test hook: runs on the worker before it reads a batch, with the paths it is about to read.</summary>
    internal Action<IReadOnlyList<string>>? BeforeDateTakenBatch { get; set; }

    /// <summary>For tests: some write holds the write lock right now.</summary>
    internal bool IsWriteLockHeld => _writeLock.CurrentCount == 0;

    /// <summary>Starts the worker and its pass over the library (once; nothing without a reader).</summary>
    internal void StartReadingDatesTaken()
    {
        if (_dateTakenReader is null)
        {
            return;
        }

        lock (_dateTakenGate)
        {
            if (_dateTakenCts is not null || _disposed)
            {
                return;
            }

            _dateTakenCts = new CancellationTokenSource();
            if (_dateTakenIdle.Task.IsCompleted)
            {
                _dateTakenIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var token = _dateTakenCts.Token;
            _dateTakenWorker = Task.Run(() => RunDateTakenWorkerAsync(_dateTakenReader, token), CancellationToken.None);
        }
    }

    /// <summary>Queues items to check (see the top of this file). Cheap: no I/O. Nothing without a reader.</summary>
    private void QueueDateTakenChecks(IReadOnlyCollection<long> itemIds)
    {
        if (_dateTakenReader is null || itemIds.Count == 0)
        {
            return;
        }

        lock (_dateTakenGate)
        {
            if (_disposed)
            {
                return;
            }

            if (_dateTakenIdle.Task.IsCompleted)
            {
                _dateTakenIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Inside the lock, so the worker can't find the queue empty and report idle in between.
            foreach (var id in itemIds)
            {
                _dateTakenQueue.Writer.TryWrite(id);
            }
        }
    }

    /// <summary>Stops the worker (on dispose). A file being read is left to finish on its own; nothing touches the database after this.</summary>
    private async Task StopReadingDatesTakenAsync()
    {
        CancellationTokenSource? cts;
        Task worker;
        lock (_dateTakenGate)
        {
            cts = _dateTakenCts;
            worker = _dateTakenWorker;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The worker handles its own errors; disposing must not fail because of it.
                Debug.WriteLine($"Cabinette: the date-taken worker ended with: {ex.Message}");
            }

            cts.Dispose();
        }

        lock (_dateTakenGate)
        {
            _dateTakenIdle.TrySetResult();
        }
    }

    private async Task RunDateTakenWorkerAsync(IDateTakenReader reader, CancellationToken ct)
    {
        var landed = new ChangeSet();
        var sinceRaised = Stopwatch.StartNew();
        long? passAfter = 0; // the pass over the library: the last item id it looked at, or null once it is through
        var unreachableRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var fromPass = false;
                try
                {
                    var ids = TakeQueuedDateTakenChecks();
                    List<DateTakenCandidate> batch;
                    if (ids.Count > 0)
                    {
                        batch = ReadNow(connection => LoadDateTakenCandidates(connection, ids, after: null, out _));
                    }
                    else if (passAfter is long after)
                    {
                        fromPass = true;
                        long? last = null;
                        batch = ReadNow(connection => LoadDateTakenCandidates(connection, null, after, out last));
                        passAfter = last;
                    }
                    else
                    {
                        RaiseLandedDates(landed, sinceRaised);
                        if (MarkDatesTakenIdleIfNothingQueued())
                        {
                            await _dateTakenQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                        }

                        continue;
                    }

                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    // Read on a pool thread of its own: on dispose the worker stops at once, even while a file on a slow or
                    // unreachable drive keeps the reader waiting; that read then ends on its own and its result is dropped.
                    var reading = Stopwatch.StartNew();
                    var roots = fromPass ? unreachableRoots : null;
                    var results = await Task.Run(() => ReadDatesTaken(reader, batch, roots, ct), CancellationToken.None)
                        .WaitAsync(ct).ConfigureAwait(false);
                    reading.Stop();
                    if (results.Count > 0)
                    {
                        await StoreDatesTakenAsync(results, landed, ct).ConfigureAwait(false);
                    }

                    if (sinceRaised.Elapsed >= DateTakenEventInterval)
                    {
                        RaiseLandedDates(landed, sinceRaised);
                    }

                    if (fromPass && batch.Count >= DateTakenBatchSize)
                    {
                        await Task.Delay(reading.Elapsed < DateTakenMaxPause ? reading.Elapsed : DateTakenMaxPause, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !(ex is ObjectDisposedException && _disposed))
                {
                    // The database failing (e.g. busy for too long): this batch is dropped and read on a later start. A pass
                    // that can't load its next batch stops for this session.
                    Debug.WriteLine($"Cabinette: reading dates taken failed for a batch: {ex}");
                    if (fromPass)
                    {
                        passAfter = null;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Disposed between two steps.
        }
        catch (Exception ex)
        {
            // Not expected (every batch has its own catch); what wasn't read is read on the next start.
            Debug.WriteLine($"Cabinette: reading dates taken stopped: {ex}");
        }
        finally
        {
            lock (_dateTakenGate)
            {
                _dateTakenIdle.TrySetResult();
            }
        }
    }

    private List<long> TakeQueuedDateTakenChecks()
    {
        var ids = new HashSet<long>();
        while (ids.Count < DateTakenBatchSize && _dateTakenQueue.Reader.TryRead(out var id))
        {
            ids.Add(id);
        }

        return [.. ids];
    }

    /// <summary>Marks the worker idle unless something was queued meanwhile. Returns true when it did.</summary>
    private bool MarkDatesTakenIdleIfNothingQueued()
    {
        lock (_dateTakenGate)
        {
            if (_dateTakenQueue.Reader.TryPeek(out _))
            {
                return false;
            }

            _dateTakenIdle.TrySetResult();
            return true;
        }
    }

    /// <summary>Raises the Items change for the dates stored since the last time (not queued for checking again: they are ours).</summary>
    private void RaiseLandedDates(ChangeSet landed, Stopwatch sinceRaised)
    {
        sinceRaised.Restart();
        if (landed.IsEmpty)
        {
            return;
        }

        RaiseChanged(landed);
        landed.Clear();
    }

    /// <summary>A photo or video to check, with where its file is and what its stored row says.</summary>
    private sealed record DateTakenCandidate(long Id, string Path, bool HasRow, long? StoredSize, string? StoredModifiedAt);

    /// <summary>
    /// What reading found. <see cref="Read"/> false: the file couldn't be looked at (gone, or not reachable); only stored when the
    /// item's path changed meanwhile, which makes it checked again at its new path.
    /// </summary>
    private sealed record DateTakenResult(long Id, string Path, bool Read, DateTimeOffset? TakenAt, long Size, string ModifiedAt);

    private sealed class DateTakenRow
    {
        public long Id { get; set; }

        public long Mode { get; set; }

        public long Kind { get; set; }

        public string FileName { get; set; } = "";

        public string Extension { get; set; } = "";

        public string? PointerPath { get; set; }

        public long HomeCategoryId { get; set; }

        public long HasRow { get; set; }

        public string? TakenAt { get; set; }

        public long? StoredSize { get; set; }

        public string? StoredModifiedAt { get; set; }
    }

    private const string DateTakenRowSql = """
        SELECT i.id AS Id, i.mode AS Mode, i.kind AS Kind, i.file_name AS FileName, i.extension AS Extension,
               i.pointer_path AS PointerPath, i.home_category_id AS HomeCategoryId,
               d.item_id IS NOT NULL AS HasRow, d.taken_at AS TakenAt, d.file_size AS StoredSize, d.file_modified_at AS StoredModifiedAt
        FROM items i LEFT JOIN item_dates d ON d.item_id = i.id
        """;

    /// <summary>
    /// The photos and videos among <paramref name="ids"/>, or the next batch of the pass after item <paramref name="after"/>: those
    /// without a row, and pointer files. Items marked missing are left out (they are checked when they are found again), and so
    /// are vault files while there is no vault. <paramref name="lastId"/>: the last item looked at (where the pass goes on), or
    /// null when there was none.
    /// </summary>
    private List<DateTakenCandidate> LoadDateTakenCandidates(Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyCollection<long>? ids,
        long? after, out long? lastId)
    {
        var filter = ids is not null
            ? "i.id IN (SELECT value FROM json_each(@ids))"
            : "i.id > @after AND (d.item_id IS NULL OR i.mode = 1)";
        var rows = connection.Query<DateTakenRow>(
            $"""
            {DateTakenRowSql}
            WHERE {filter} AND i.kind = 0 AND i.is_missing = 0 AND i.extension IN (SELECT value FROM json_each(@extensions))
            ORDER BY i.id
            LIMIT @limit
            """,
            new { ids = ids is null ? null : DbValues.JsonArray(ids), after, extensions = DateTakenTypes.ExtensionsJson, limit = DateTakenBatchSize })
            .AsList();
        lastId = rows.Count > 0 ? rows[^1].Id : null;
        if (rows.Count == 0)
        {
            return [];
        }

        var tree = CategoryTree.Load(connection);
        var vault = _settings.VaultPath;
        var result = new List<DateTakenCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (DateTakenPath(row, tree, vault) is { } path)
            {
                result.Add(new DateTakenCandidate(row.Id, path, row.HasRow != 0, row.StoredSize, row.StoredModifiedAt));
            }
        }

        return result;
    }

    /// <summary>Where the item's file is: the pointer's path, or the vault file in its home folder; null when there is none.</summary>
    private static string? DateTakenPath(DateTakenRow row, CategoryTree tree, string? vault)
    {
        var path = (ItemMode)row.Mode == ItemMode.Pointer
            ? row.PointerPath
            : vault is not null && tree.Find(row.HomeCategoryId) is not null
                ? Path.Combine(tree.FolderPath(vault, row.HomeCategoryId), row.FileName)
                : null;
        return !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) ? path : null;
    }

    /// <summary>
    /// Reads the batch's files that have no row, or whose file changed since. Runs on a pool thread, outside every lock.
    /// <paramref name="unreachableRoots"/> (the pass): drives and network shares where looking for a file took long and found
    /// nothing; their other files are skipped for this pass, so a NAS that is off costs one timeout instead of one per file.
    /// </summary>
    private List<DateTakenResult> ReadDatesTaken(IDateTakenReader reader, List<DateTakenCandidate> batch, HashSet<string>? unreachableRoots,
        CancellationToken ct)
    {
        var results = new List<DateTakenResult>(batch.Count);
        BeforeDateTakenBatch?.Invoke(batch.Select(c => c.Path).ToList());
        foreach (var candidate in batch)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var root = Path.GetPathRoot(candidate.Path) ?? "";
            if (unreachableRoots is not null && unreachableRoots.Contains(root))
            {
                continue;
            }

            long size;
            string modified;
            var looking = Stopwatch.StartNew();
            try
            {
                var info = new FileInfo(candidate.Path);
                if (!info.Exists)
                {
                    if (looking.Elapsed >= UnreachableAfter)
                    {
                        unreachableRoots?.Add(root);
                    }

                    results.Add(new DateTakenResult(candidate.Id, candidate.Path, Read: false, null, 0, ""));
                    continue;
                }

                size = info.Length;
                modified = DbValues.ToText(info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                results.Add(new DateTakenResult(candidate.Id, candidate.Path, Read: false, null, 0, ""));
                continue;
            }

            if (candidate.HasRow && candidate.StoredSize == size && candidate.StoredModifiedAt == modified)
            {
                continue; // read already, and the file is as it was then
            }

            DateTimeOffset? taken;
            try
            {
                taken = reader.ReadDateTaken(candidate.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The reader must not throw; if one does, the file counts as unreadable this time.
                Debug.WriteLine($"Cabinette: reading the date taken of {candidate.Path} failed: {ex.Message}");
                results.Add(new DateTakenResult(candidate.Id, candidate.Path, Read: false, null, 0, ""));
                continue;
            }

            results.Add(new DateTakenResult(candidate.Id, candidate.Path, Read: true, taken, size, modified));
        }

        return results;
    }

    /// <summary>
    /// Stores a batch in one short write. A result is only stored while the item's file is still at the path that was read (else
    /// the item is checked again at its new path); items whose date taken changed go into <paramref name="landed"/>.
    /// </summary>
    private async Task StoreDatesTakenAsync(List<DateTakenResult> results, ChangeSet landed, CancellationToken ct)
    {
        await WriteCoreAsync(ctx =>
        {
            var current = ctx.Query<DateTakenRow>(
                    $"{DateTakenRowSql} WHERE i.id IN (SELECT value FROM json_each(@ids))",
                    new { ids = DbValues.JsonArray(results.Select(r => r.Id)) })
                .ToDictionary(r => r.Id);
            var tree = ctx.Tree;
            var again = new List<long>();
            foreach (var result in results)
            {
                if (!current.TryGetValue(result.Id, out var row))
                {
                    continue; // deleted meanwhile
                }

                if (!FileOps.PathsEqual(DateTakenPath(row, tree, ctx.VaultPath), result.Path))
                {
                    again.Add(result.Id); // renamed, moved, or the vault moved: what was read may not be this file
                    continue;
                }

                if (!result.Read || !DateTakenTypes.Applies((ItemKind)row.Kind, row.Extension))
                {
                    continue;
                }

                var takenAt = DbValues.ToTextWithOffset(result.TakenAt);
                ctx.Execute(
                    """
                    INSERT INTO item_dates(item_id, taken_at, file_size, file_modified_at) VALUES (@id, @takenAt, @size, @modified)
                    ON CONFLICT(item_id) DO UPDATE SET taken_at = excluded.taken_at, file_size = excluded.file_size,
                                                       file_modified_at = excluded.file_modified_at
                    """,
                    new { id = result.Id, takenAt, size = result.Size, modified = result.ModifiedAt });
                if (takenAt != row.TakenAt)
                {
                    // Only the item: threads showing it redraw it, nothing else (counts and the chat list are as they were).
                    ctx.Changes.Item(result.Id);
                }
            }

            if (again.Count > 0)
            {
                ctx.OnCommitted(() => QueueDateTakenChecks(again));
            }

            return true;
        }, ct, accumulate: landed).ConfigureAwait(false);
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
