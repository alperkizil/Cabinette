using System.Diagnostics;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Downloads;

/// <summary>
/// Clean My Downloads mode (SPEC.md section 5): keeps the top level of a Downloads folder empty by moving new files into the
/// vault. A file moves once it is complete (partial and temporary files are ignored, like in the vault monitor), nothing
/// changed it for <see cref="DownloadsCleanerOptions.QuietPeriod"/> and no other program has it open. Moving means: added like
/// a drop onto the main window (auto-categorized, copy mode, <see cref="AddedBy.Downloads"/>, keeping its Mark of the Web), checked
/// against the size and hash the library computed (a vault file this add didn't write, as for a duplicate, is read back too),
/// then the original goes to the Recycle Bin. It is never deleted permanently: if the Recycle Bin refuses it, it stays; if the
/// Recycle Bin fails otherwise, it is tried again later. Only files created after <see cref="DownloadsCleanerOptions.Watermark"/> are touched, so the files
/// that were there before the mode was switched on never are; subfolders and their contents are left alone.
/// Watches the folder with a <see cref="FileSystemWatcher"/> and looks again on a timer. Events are raised on background threads.
/// </summary>
public sealed class DownloadsCleaner : IAsyncDisposable
{
    /// <summary>
    /// An unfinished download, never moved: browsers (Chrome and Edge .crdownload, Firefox .part, old Edge .partial, Safari
    /// .download, Opera .opdownload), download managers (Free Download Manager .fdmdownload, FlashGet .jc!, Thunder .td, and
    /// aria2's control file .aria2) and torrent clients that mark incomplete files (qBittorrent .!qB, µTorrent .!ut, BitTorrent
    /// .!bt, BitComet .bc!, Vuze .az!). While one exists it holds the watermark back (it becomes a new file when it completes),
    /// unless it was abandoned (<see cref="DownloadsCleanerOptions.AbandonedPartialAge"/>). A file named like it without the
    /// suffix waits for it: Firefox's empty placeholder, and the file aria2 writes in place next to "name.aria2".
    /// </summary>
    private static readonly string[] PartialDownloadSuffixes =
    [
        ".crdownload", ".part", ".partial", ".download", ".opdownload",
        ".fdmdownload", ".jc!", ".td", ".aria2",
        ".!qB", ".!ut", ".!bt", ".bc!", ".az!",
    ];

    private readonly ILibraryService _library;
    private readonly IRecycleBin _recycleBin;
    private readonly DownloadsCleanerOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // Pass state: only touched while holding _passLock.
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _kept = new(StringComparer.OrdinalIgnoreCase);

    // Shared with the watcher's threads: guarded by _gate.
    private readonly Dictionary<string, DateTimeOffset> _touched = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _wake = NewWake();
    private FileSystemWatcher? _watcher;
    private Task? _loop;
    private DateTimeOffset _watermark;
    private DateTimeOffset? _nextDue;
    private bool _started;
    private bool _paused;
    private bool _disposed;

    public DownloadsCleaner(ILibraryService library, IRecycleBin recycleBin, DownloadsCleanerOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Folder);
        _library = library;
        _recycleBin = recycleBin;
        _options = options with { Folder = FileOps.Normalize(options.Folder) };
        _time = time ?? TimeProvider.System;
        _watermark = options.Watermark;
    }

    /// <summary>Raised after a look at the folder dealt with files (see <see cref="DownloadsBatch"/>).</summary>
    public event EventHandler<DownloadsBatch>? Processed;

    /// <summary>Raised when the watermark moved forward, so the caller can remember it for the next start.</summary>
    public event EventHandler<DateTimeOffset>? WatermarkChanged;

    public string Folder => _options.Folder;

    public DateTimeOffset Watermark
    {
        get
        {
            lock (_gate)
            {
                return _watermark;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    // ------------------------------------------------------------ public API

    /// <summary>Starts watching and looks at the folder right away (files that arrived while Cabinette was closed).</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            if (!_paused)
            {
                StartWatcherLocked();
            }

            _loop = Task.Run(RunLoopAsync);
            _wake.TrySetResult();
        }
    }

    /// <summary>Stops moving files until <see cref="ResumeAsync"/>. A batch already being moved is finished first.</summary>
    public async Task PauseAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _paused = true;
            StopWatcherLocked();
            _touched.Clear();
        }

        await _passLock.WaitAsync().ConfigureAwait(false);
        _passLock.Release();
    }

    /// <summary>Watches again and looks at the folder right away, catching up on what arrived while paused.</summary>
    public Task ResumeAsync()
    {
        lock (_gate)
        {
            if (_disposed || !_paused)
            {
                return Task.CompletedTask;
            }

            _paused = false;
            if (_started)
            {
                StartWatcherLocked();
            }

            _wake.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatcherLocked();
            loop = _loop;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        // Let a pass that is still finishing (it was cancelled) let go of the library.
        await _passLock.WaitAsync().ConfigureAwait(false);
        _passLock.Release();
    }

    // ------------------------------------------------------------ used by tests

    /// <summary>The next time something in the folder is due (a quiet period ends, a retry), or null.</summary>
    internal DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                return _nextDue;
            }
        }
    }

    /// <summary>What a watcher event does: the file counts as changed now.</summary>
    internal void NoteChanged(string path)
    {
        lock (_gate)
        {
            if (!_paused && !_disposed)
            {
                _touched[FullPath(path)] = _time.GetUtcNow();
            }
        }
    }

    /// <summary>One look at the folder: moves the files that are ready. Returns what it did (also raised as <see cref="Processed"/>).</summary>
    internal async Task<DownloadsBatch> RunPassAsync(CancellationToken ct = default)
    {
        await _passLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_paused || _disposed)
                {
                    return DownloadsBatch.Empty;
                }

                if (_started && _watcher is null)
                {
                    // The folder couldn't be watched before (missing, or a drive that wasn't there): try again.
                    StartWatcherLocked();
                }
            }

            return await PassAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _passLock.Release();
        }
    }

    // ------------------------------------------------------------ one pass

    private async Task<DownloadsBatch> PassAsync(CancellationToken ct)
    {
        var vault = _library.VaultPath;
        if (vault is null || IsSameOrInside(_options.Folder, vault))
        {
            // No vault yet, or the folder is the vault (or inside it): the vault monitor owns those files.
            SetNextDue(null);
            return DownloadsBatch.Empty;
        }

        var now = _time.GetUtcNow();
        var scan = Scan(now);
        if (scan is null)
        {
            SetNextDue(null);
            return DownloadsBatch.Empty;
        }

        var outcomes = new List<DownloadOutcome>(scan.Finished);
        if (scan.Ready.Count > 0)
        {
            await MoveAsync(scan, outcomes, ct).ConfigureAwait(false);
        }

        AdvanceWatermark(scan);
        SetNextDue(scan.NextDue);

        var batch = outcomes.Count == 0 ? DownloadsBatch.Empty : new DownloadsBatch(outcomes);
        if (!batch.IsEmpty)
        {
            Raise(Processed, batch);
        }

        return batch;
    }

    /// <summary>Lists the folder's top level and decides, per file, whether it is ready to move. Null when the folder can't be read.</summary>
    private ScanResult? Scan(DateTimeOffset now)
    {
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_options.Folder)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                })
                .ToList();
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            Debug.WriteLine($"Cabinette: the Downloads folder can't be read: {ex.Message}");
            return null;
        }

        Dictionary<string, DateTimeOffset> touched;
        DateTimeOffset watermark;
        lock (_gate)
        {
            touched = new Dictionary<string, DateTimeOffset>(_touched, StringComparer.OrdinalIgnoreCase);
            _touched.Clear();
            watermark = _watermark;
        }

        var partials = files.Where(f => IsPartialDownload(f.Name)).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var scan = new ScanResult(now);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            FileState state;
            try
            {
                state = new FileState(file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, file.Attributes);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                continue; // Gone meanwhile.
            }

            var created = Utc(state.CreationUtc);
            if (created <= watermark)
            {
                continue; // There before the mode was switched on, or dealt with already.
            }

            var path = file.FullName;
            present.Add(path);
            if (_kept.TryGetValue(path, out var keptCreation) && keptCreation == state.CreationUtc.Ticks)
            {
                scan.Kept.Add(created);
                continue;
            }

            if (IsPartialDownload(file.Name))
            {
                if (!IsAbandoned(file, now, touched))
                {
                    scan.Waiting(created, due: null);
                }

                continue;
            }

            if (NameRules.IsIgnoredVaultName(file.Name) || (state.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue; // Temporary files, and links or cloud placeholders: never moved.
            }

            var tracked = Track(scan, path, state, now, touched.GetValueOrDefault(path));
            if (PartialSibling(file.Name, partials) is { } sibling)
            {
                // Firefox keeps an empty placeholder under the final name while "name.part" downloads, and aria2 writes the file
                // in place while "name.aria2" exists. Never moved then; held back only while that download isn't abandoned.
                if (!IsAbandoned(sibling, now, touched))
                {
                    scan.Waiting(created, due: null);
                }

                continue;
            }

            var quietUntil = tracked.ChangedAt + _options.QuietPeriod;
            if (now < quietUntil)
            {
                scan.Waiting(created, quietUntil);
                continue;
            }

            if (tracked.RetryAt is { } retryAt && now < retryAt)
            {
                scan.Waiting(created, retryAt);
                continue;
            }

            if (FileOps.IsLocked(path))
            {
                tracked.RetryAt = now + _options.InUseRetryDelay;
                scan.Waiting(created, tracked.RetryAt);
                continue;
            }

            scan.Ready.Add(new Candidate(path, created, state.CreationUtc.Ticks, tracked));
        }

        foreach (var (gone, tracked) in _tracked.Where(kv => !present.Contains(kv.Key)).ToList())
        {
            _tracked.Remove(gone);
            if (tracked.Stored is { } stored && !File.Exists(gone))
            {
                // Removed by someone else while it waited to go to the Recycle Bin: the vault has it.
                scan.Finished.Add(new DownloadOutcome(gone, stored.Kind, stored.Item));
            }
        }

        foreach (var gone in _kept.Keys.Where(key => !present.Contains(key)).ToList())
        {
            _kept.Remove(gone);
        }

        return scan;
    }

    /// <summary>
    /// True when a partial download was abandoned: nothing wrote to it, named it in a watcher event or holds it open for
    /// <see cref="DownloadsCleanerOptions.AbandonedPartialAge"/>. It no longer holds the watermark back then.
    /// </summary>
    private bool IsAbandoned(FileInfo partial, DateTimeOffset now, Dictionary<string, DateTimeOffset> touched)
    {
        var age = _options.AbandonedPartialAge;
        try
        {
            if (now - LastActivity(partial) < age || now - touched.GetValueOrDefault(partial.FullName, DateTimeOffset.MinValue) < age)
            {
                return false;
            }

            // The folder listing can lag behind a file that is open for writing: ask the file itself, and an open one is in use.
            partial.Refresh();
            return partial.Exists && now - LastActivity(partial) >= age && !FileOps.IsLocked(partial.FullName);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return false;
        }
    }

    private static DateTimeOffset LastActivity(FileInfo file)
        => Utc(file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc);

    /// <summary>The file's tracking entry, updated: any change (or a watcher event) restarts its quiet period.</summary>
    private Tracked Track(ScanResult scan, string path, FileState state, DateTimeOffset now, DateTimeOffset touchedAt)
    {
        if (!_tracked.TryGetValue(path, out var tracked) || tracked.State.CreationUtc != state.CreationUtc)
        {
            if (tracked?.Stored is { } stored)
            {
                // Replaced by another file of that name while it waited to go to the Recycle Bin: the vault has the old one.
                scan.Finished.Add(new DownloadOutcome(path, stored.Kind, stored.Item));
            }

            // First seen: it last changed when it was created or written, as far as the file says (never later than now).
            var stamp = Utc(state.LastWriteUtc > state.CreationUtc ? state.LastWriteUtc : state.CreationUtc);
            tracked = new Tracked { State = state, ChangedAt = stamp < now ? stamp : now };
            _tracked[path] = tracked;
        }
        else if (tracked.State.Size != state.Size || tracked.State.LastWriteUtc != state.LastWriteUtc)
        {
            tracked.State = state;
            tracked.ChangedAt = now;
        }

        if (touchedAt > tracked.ChangedAt)
        {
            tracked.ChangedAt = touchedAt < now ? touchedAt : now;
        }

        return tracked;
    }

    // ------------------------------------------------------------ moving

    private async Task MoveAsync(ScanResult scan, List<DownloadOutcome> outcomes, CancellationToken ct)
    {
        // Files an earlier pass already stored (it stopped before the Recycle Bin: in use, or the Recycle Bin failed) finish from
        // there, without a second copy. If their item is gone meanwhile, they are added again.
        var ready = new List<Candidate>();
        foreach (var candidate in scan.Ready)
        {
            if (candidate.Tracked.Stored is not { } stored)
            {
                ready.Add(candidate);
                continue;
            }

            LibraryItem? item;
            try
            {
                item = await _library.GetItemAsync(stored.Item.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LibraryException || FileOps.IsIoError(ex))
            {
                Fail(scan, candidate, ex.Message, outcomes);
                continue;
            }

            if (item is null || item.Mode != ItemMode.Copy)
            {
                candidate.Tracked.Stored = null;
                ready.Add(candidate);
                continue;
            }

            await FinishAsync(scan, candidate, stored, item, freshCopy: false, outcomes, ct).ConfigureAwait(false);
        }

        if (ready.Count == 0)
        {
            return;
        }

        // New items of this add have this added time or a later one; an item "found again" (re-linked) keeps its old one.
        var addStarted = _time.GetUtcNow();
        AddResult result;
        try
        {
            var request = new AddRequest { TargetCategoryId = null, Mode = ItemMode.Copy, AddedBy = AddedBy.Downloads };
            result = await _library.AddPathsAsync(ready.Select(c => c.Path).ToList(), request, progress: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // The library is closing (Cabinette quits): the files stay and are looked at next time.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = ex is LibraryException ? ex.Message : FileOps.IsIoError(ex) ? FileOps.ToLibraryException(ex, _options.Folder).Message : ex.Message;
            foreach (var candidate in ready)
            {
                Fail(scan, candidate, message, outcomes);
            }

            return;
        }

        var added = result.Added
            .Where(i => i.OriginalPath is not null)
            .GroupBy(i => i.OriginalPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var duplicates = result.Duplicates
            .GroupBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var failures = result.Failures
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ready)
        {
            ct.ThrowIfCancellationRequested();
            if (failures.TryGetValue(candidate.Path, out var failure))
            {
                Fail(scan, candidate, failure.Message, outcomes);
            }
            else if (added.TryGetValue(candidate.Path, out var item))
            {
                var stored = new Stored(item, DownloadOutcomeKind.Moved, IsNew: item.AddedAt >= addStarted);
                await FinishAsync(scan, candidate, stored, item, freshCopy: true, outcomes, ct).ConfigureAwait(false);
            }
            else if (duplicates.TryGetValue(candidate.Path, out var duplicate))
            {
                if (duplicate.Existing.Mode == ItemMode.Copy)
                {
                    // Not written by this add: its vault file is read back before the original goes.
                    var stored = new Stored(duplicate.Existing, DownloadOutcomeKind.AlreadyInLibrary, IsNew: false);
                    await FinishAsync(scan, candidate, stored, duplicate.Existing, freshCopy: false, outcomes, ct).ConfigureAwait(false);
                }
                else
                {
                    // Only a pointer item has this content: the vault doesn't hold it, so the original stays.
                    Keep(scan, candidate, duplicate.Existing, DownloadKeptReason.LinkedAsPointer, null, outcomes);
                }
            }
            else
            {
                Fail(scan, candidate, $"\"{Path.GetFileName(candidate.Path)}\" could not be added.", outcomes);
            }
        }
    }

    /// <summary>
    /// Checks the vault copy against the original, then sends the original to the Recycle Bin. Until that is done, the item is
    /// remembered on the file's tracking entry, so a pass that has to stop (the file is in use, the Recycle Bin failed) is
    /// finished by a later one from here. <paramref name="item"/> is the item as the library has it now (an earlier pass's item is
    /// read again: it may have moved). <paramref name="freshCopy"/>: this pass's add just wrote and hashed the vault file;
    /// otherwise (a duplicate, or an item an earlier pass stored) the vault file is read back too.
    /// </summary>
    private async Task FinishAsync(ScanResult scan, Candidate candidate, Stored stored, LibraryItem item, bool freshCopy,
        List<DownloadOutcome> outcomes, CancellationToken ct)
    {
        var tracked = candidate.Tracked;
        var kind = stored.Kind;
        tracked.Stored = stored;
        var now = _time.GetUtcNow();
        switch (await VerifyAsync(candidate.Path, item, checkVaultContent: !freshCopy, ct).ConfigureAwait(false))
        {
            case Check.OriginalGone:
                // Someone removed it meanwhile; the vault has it.
                Done(candidate);
                outcomes.Add(new DownloadOutcome(candidate.Path, kind, item));
                return;
            case Check.OriginalChanged:
                // It changed while it was being added: wait for it to be quiet again (the next add stores the new content). An item
                // this download stored holds content the file no longer has, and nobody was told about it: it goes.
                tracked.Stored = null;
                if (stored.IsNew)
                {
                    // Compared with the item exactly as the add stored it: any use since then keeps it.
                    await DiscardStaleAddAsync(stored.Item, ct).ConfigureAwait(false);
                }

                if (TryReadState(candidate.Path) is { } changed)
                {
                    tracked.State = changed;
                }

                tracked.ChangedAt = now;
                scan.Waiting(candidate.Created, now + _options.QuietPeriod);
                return;
            case Check.InUse:
                tracked.RetryAt = now + _options.InUseRetryDelay;
                scan.Waiting(candidate.Created, tracked.RetryAt);
                return;
            case Check.VaultCopyBad:
                // The next attempt adds the file again: a vault file that is gone is then copied once more ("found again").
                tracked.Stored = null;
                CheckFailed(scan, candidate, item, outcomes);
                return;
        }

        if (FileOps.IsLocked(candidate.Path))
        {
            tracked.RetryAt = now + _options.InUseRetryDelay;
            scan.Waiting(candidate.Created, tracked.RetryAt);
            return;
        }

        bool canRecycle;
        try
        {
            canRecycle = _recycleBin.CanRecycle(candidate.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only a pre-check: sending it reports what really fails.
            Debug.WriteLine($"Cabinette: the Recycle Bin check failed for {candidate.Path}: {ex.Message}");
            canRecycle = true;
        }

        if (!canRecycle)
        {
            Keep(scan, candidate, item, DownloadKeptReason.NoRecycleBin, null, outcomes);
            return;
        }

        try
        {
            // Never permanently: a file the Recycle Bin can't take is left in place.
            _recycleBin.SendToRecycleBin([candidate.Path]);
        }
        catch (RecycleBinUnavailableException) when (File.Exists(candidate.Path))
        {
            // A real refusal (no Recycle Bin there, or too large for it): it stays for good.
            Keep(scan, candidate, item, DownloadKeptReason.NoRecycleBin, null, outcomes);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && File.Exists(candidate.Path))
        {
            if (FileOps.IsLocked(candidate.Path))
            {
                tracked.RetryAt = now + _options.InUseRetryDelay;
                scan.Waiting(candidate.Created, tracked.RetryAt);
                return;
            }

            // The shell was busy, a virus scanner held the file for a moment...: tried again later.
            RecycleFailed(scan, candidate, item, ex.Message, outcomes);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // It is gone anyway.
            Debug.WriteLine($"Cabinette: the Recycle Bin reported an error for {candidate.Path}, but the file is gone: {ex.Message}");
        }

        if (File.Exists(candidate.Path))
        {
            RecycleFailed(scan, candidate, item, null, outcomes);
            return;
        }

        Done(candidate);
        outcomes.Add(new DownloadOutcome(candidate.Path, kind, item));
    }

    private enum Check
    {
        Ok,
        OriginalGone,
        OriginalChanged,

        /// <summary>The original or the vault file is open in another program that doesn't let it be read.</summary>
        InUse,
        VaultCopyBad,
    }

    /// <summary>
    /// The vault copy is complete when its file is where the item says, with the size the library computed, and the original
    /// still has that size and the hash the library computed while copying it. With <paramref name="checkVaultContent"/> the
    /// vault file is hashed too: the library's stored hash is only what the file had when it was stored, and a vault file
    /// changed in place (same size, modification time put back) still matches it.
    /// </summary>
    private static async Task<Check> VerifyAsync(string original, LibraryItem item, bool checkVaultContent, CancellationToken ct)
    {
        if (item.Mode != ItemMode.Copy || item.SizeBytes is not long size || item.Hash is not { Length: > 0 } hash)
        {
            return Check.VaultCopyBad;
        }

        try
        {
            var copy = new FileInfo(item.FullPath);
            if (!copy.Exists || copy.Length != size)
            {
                return Check.VaultCopyBad;
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return Check.VaultCopyBad;
        }

        byte[] originalHash;
        try
        {
            var info = new FileInfo(original);
            if (!info.Exists)
            {
                return Check.OriginalGone;
            }

            if (info.Length != size)
            {
                return Check.OriginalChanged;
            }

            originalHash = await FileOps.HashFileAsync(original, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return !File.Exists(original) ? Check.OriginalGone : FileOps.IsSharingViolation(ex) ? Check.InUse : Check.OriginalChanged;
        }

        if (!originalHash.AsSpan().SequenceEqual(hash))
        {
            return Check.OriginalChanged;
        }

        if (!checkVaultContent)
        {
            return Check.Ok;
        }

        try
        {
            var copyHash = await FileOps.HashFileAsync(item.FullPath, null, ct).ConfigureAwait(false);
            return copyHash.AsSpan().SequenceEqual(hash) ? Check.Ok : Check.VaultCopyBad;
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return FileOps.IsSharingViolation(ex) && File.Exists(item.FullPath) ? Check.InUse : Check.VaultCopyBad;
        }
    }

    /// <summary>
    /// The vault copy failed its check. Tried again with a doubling delay (each attempt adds the file again), said once; after
    /// <see cref="DownloadsCleanerOptions.VaultCheckAttempts"/> attempts the original stays for good, with a notice.
    /// </summary>
    private void CheckFailed(ScanResult scan, Candidate candidate, LibraryItem item, List<DownloadOutcome> outcomes)
    {
        if (!File.Exists(candidate.Path))
        {
            Done(candidate);
            return;
        }

        var tracked = candidate.Tracked;
        tracked.CheckFailures++;
        if (tracked.CheckFailures >= _options.VaultCheckAttempts)
        {
            Keep(scan, candidate, item, DownloadKeptReason.VaultCopyUnchecked, null, outcomes);
            return;
        }

        RetryLater(scan, candidate, Backoff(tracked.CheckFailures), outcomes,
            new DownloadOutcome(candidate.Path, DownloadOutcomeKind.Failed,
                Message: $"The copy of \"{Path.GetFileName(candidate.Path)}\" in the vault couldn't be checked, so the original stays for now."));
    }

    /// <summary>
    /// Sending the original to the Recycle Bin failed, but not because the Recycle Bin refuses it. Tried again with a doubling
    /// delay, said once. It keeps holding the watermark back meanwhile, so a restart tries it again too.
    /// </summary>
    private void RecycleFailed(ScanResult scan, Candidate candidate, LibraryItem item, string? detail, List<DownloadOutcome> outcomes)
    {
        var tracked = candidate.Tracked;
        tracked.RecycleFailures++;
        var reason = string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail.Trim().TrimEnd('.')})";
        RetryLater(scan, candidate, Backoff(tracked.RecycleFailures), outcomes,
            new DownloadOutcome(candidate.Path, DownloadOutcomeKind.Failed, item,
                Message: $"It is in your vault, but it couldn't go to the Recycle Bin yet{reason}. Cabinette tries again later."));
    }

    private void RetryLater(ScanResult scan, Candidate candidate, TimeSpan delay, List<DownloadOutcome> outcomes, DownloadOutcome report)
    {
        var tracked = candidate.Tracked;
        tracked.RetryAt = _time.GetUtcNow() + delay;
        scan.Waiting(candidate.Created, tracked.RetryAt);
        if (!tracked.FailureReported)
        {
            // Said once; it is tried again quietly.
            tracked.FailureReported = true;
            outcomes.Add(report);
        }
    }

    /// <summary><see cref="DownloadsCleanerOptions.FailureRetryDelay"/>, doubled for each failure after the first, at most the maximum.</summary>
    private TimeSpan Backoff(int failures)
    {
        var delay = _options.FailureRetryDelay;
        for (var i = 1; i < failures && delay < _options.MaxFailureRetryDelay; i++)
        {
            delay += delay;
        }

        return delay < _options.MaxFailureRetryDelay ? delay : _options.MaxFailureRetryDelay;
    }

    /// <summary>
    /// Removes an item this download stored but that turned out stale (the file changed while it was copied). Nobody was told
    /// about it; it goes only while it is untouched, and its vault file is deleted permanently (see LibraryService.DiscardStaleAddAsync).
    /// </summary>
    private async Task DiscardStaleAddAsync(LibraryItem item, CancellationToken ct)
    {
        if (_library is not LibraryService library)
        {
            return;
        }

        try
        {
            await library.DiscardStaleAddAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LibraryException || FileOps.IsIoError(ex))
        {
            // It stays: an extra item, but nothing lost.
            Debug.WriteLine($"Cabinette: the stale copy of {item.FullPath} could not be removed: {ex.Message}");
        }
    }

    private void Fail(ScanResult scan, Candidate candidate, string message, List<DownloadOutcome> outcomes)
    {
        if (!File.Exists(candidate.Path))
        {
            // Deleted or moved away meanwhile: nothing to report or to try again.
            Done(candidate);
            return;
        }

        RetryLater(scan, candidate, _options.FailureRetryDelay, outcomes,
            new DownloadOutcome(candidate.Path, DownloadOutcomeKind.Failed, Message: message));
    }

    private void Keep(ScanResult scan, Candidate candidate, LibraryItem item, DownloadKeptReason reason, string? message, List<DownloadOutcome> outcomes)
    {
        _tracked.Remove(candidate.Path);
        _kept[candidate.Path] = candidate.CreationTicks;
        scan.Kept.Add(candidate.Created);
        outcomes.Add(new DownloadOutcome(candidate.Path, DownloadOutcomeKind.Kept, item, reason, message));
    }

    private void Done(Candidate candidate) => _tracked.Remove(candidate.Path);

    /// <summary>
    /// Moves the watermark past the files that stay where they are (kept), but never past a file still to be handled
    /// (waiting, retried, or a download in progress), so a restart looks at those again and at nothing else.
    /// </summary>
    private void AdvanceWatermark(ScanResult scan)
    {
        var oldestWaiting = scan.OldestWaiting ?? DateTimeOffset.MaxValue;
        var target = scan.Kept.Where(created => created < oldestWaiting).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        DateTimeOffset moved;
        lock (_gate)
        {
            if (target <= _watermark)
            {
                return;
            }

            _watermark = target;
            moved = target;
        }

        foreach (var path in _kept.Where(kv => Utc(new DateTime(kv.Value, DateTimeKind.Utc)) <= moved).Select(kv => kv.Key).ToList())
        {
            _kept.Remove(path);
        }

        Raise(WatermarkChanged, moved);
    }

    // ------------------------------------------------------------ loop and watcher

    private async Task RunLoopAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            while (true)
            {
                Task wake;
                TimeSpan wait;
                lock (_gate)
                {
                    wake = _wake.Task;
                    wait = _paused ? Timeout.InfiniteTimeSpan : WaitLocked();
                }

                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var delay = Task.Delay(wait, _time, delayCts.Token);
                    var first = await Task.WhenAny(wake, delay).ConfigureAwait(false);
                    await delayCts.CancelAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (first == wake)
                    {
                        lock (_gate)
                        {
                            _wake = NewWake();
                        }

                        // A download raises a burst of events: look once they settled.
                        await Task.Delay(_options.EventDelay, _time, ct).ConfigureAwait(false);
                    }
                }

                try
                {
                    await RunPassAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Never let one bad pass stop the cleaner.
                    Debug.WriteLine($"Cabinette: cleaning the Downloads folder failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    private TimeSpan WaitLocked()
    {
        var now = _time.GetUtcNow();
        var due = now + _options.RescanInterval;
        if (_nextDue is { } next && next < due)
        {
            due = next;
        }

        var wait = due - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    private void SetNextDue(DateTimeOffset? due)
    {
        lock (_gate)
        {
            _nextDue = due;
        }
    }

    private void StartWatcherLocked()
    {
        StopWatcherLocked();
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(_options.Folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Attributes,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Missing or unreachable: the timer keeps looking, and the next pass tries to watch it again.
            Debug.WriteLine($"Cabinette: the Downloads folder can't be watched ({_options.Folder}): {ex.Message}");
            watcher?.Dispose();
            _watcher = null;
        }
    }

    private void StopWatcherLocked()
    {
        var watcher = _watcher;
        if (watcher is null)
        {
            return;
        }

        _watcher = null;
        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch (Exception ex) when (FileOps.IsIoError(ex) || ex is ObjectDisposedException)
        {
            // Already broken.
        }

        watcher.Created -= OnChanged;
        watcher.Changed -= OnChanged;
        watcher.Deleted -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => OnEvent(sender, e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => OnEvent(sender, e.FullPath);

    private void OnError(object sender, ErrorEventArgs e) => OnEvent(sender, null);

    private void OnEvent(object sender, string? path)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || _paused || _disposed)
            {
                return;
            }

            if (path is not null)
            {
                _touched[path] = _time.GetUtcNow();
            }

            _wake.TrySetResult();
        }
    }

    // ------------------------------------------------------------ helpers

    private static bool IsPartialDownload(string name)
        => PartialDownloadSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The unfinished download "name.crdownload", "name.aria2" and so on next to <paramref name="name"/>, or null.</summary>
    private static FileInfo? PartialSibling(string name, Dictionary<string, FileInfo> partials)
    {
        foreach (var suffix in PartialDownloadSuffixes)
        {
            if (partials.TryGetValue(name + suffix, out var sibling))
            {
                return sibling;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="folder"/> is <paramref name="vault"/> or inside it, as written or once junctions, symbolic
    /// links (and, with <see cref="DownloadsCleanerOptions.ResolveFinalPath"/>, short names and subst drives) are resolved.
    /// </summary>
    private bool IsSameOrInside(string folder, string vault)
    {
        if (FileOps.IsSameOrInside(folder, vault))
        {
            return true;
        }

        var resolve = _options.ResolveFinalPath;
        var finalFolder = (resolve is null ? null : SafeResolve(resolve, folder)) ?? FileOps.ResolveLinks(folder);
        var finalVault = (resolve is null ? null : SafeResolve(resolve, vault)) ?? FileOps.ResolveLinks(vault);
        return FileOps.IsSameOrInside(finalFolder, finalVault);
    }

    private static string? SafeResolve(Func<string, string?> resolve, string path)
    {
        try
        {
            return resolve(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Cabinette: the final path of {path} could not be resolved: {ex.Message}");
            return null;
        }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static FileState? TryReadState(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileState(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes) : null;
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return null;
        }
    }

    private string FullPath(string path) => Path.IsPathFullyQualified(path) ? path : Path.Combine(_options.Folder, path);

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                // A failing subscriber must not stop the cleaner.
                Debug.WriteLine($"Cabinette: a Downloads cleaner event handler threw: {ex}");
            }
        }
    }

    private readonly record struct FileState(long Size, DateTime CreationUtc, DateTime LastWriteUtc, FileAttributes Attributes);

    private sealed class Tracked
    {
        public required FileState State { get; set; }

        /// <summary>The last time the file changed (or a watcher event named it): its quiet period runs from here.</summary>
        public DateTimeOffset ChangedAt { get; set; }

        /// <summary>Not before this time (in use, or it failed).</summary>
        public DateTimeOffset? RetryAt { get; set; }

        public bool FailureReported { get; set; }

        /// <summary>
        /// The item that holds its content, while the original still has to go to the Recycle Bin (a pass stopped because it was
        /// in use or the Recycle Bin failed): the next pass finishes from here instead of adding it again.
        /// </summary>
        public Stored? Stored { get; set; }

        /// <summary>How often its vault copy failed the check.</summary>
        public int CheckFailures { get; set; }

        /// <summary>How often sending it to the Recycle Bin failed (other than a refusal).</summary>
        public int RecycleFailures { get; set; }
    }

    /// <param name="Item">The item that holds the file's content, as the add returned it.</param>
    /// <param name="Kind">What the outcome says once the original is gone: Moved, or AlreadyInLibrary.</param>
    /// <param name="IsNew">This download's add created the item (not a duplicate, not an item found again).</param>
    private sealed record Stored(LibraryItem Item, DownloadOutcomeKind Kind, bool IsNew);

    private sealed record Candidate(string Path, DateTimeOffset Created, long CreationTicks, Tracked Tracked);

    private sealed class ScanResult(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; } = now;

        public List<Candidate> Ready { get; } = [];

        /// <summary>Files an earlier pass stored that someone else removed or replaced before they went to the Recycle Bin.</summary>
        public List<DownloadOutcome> Finished { get; } = [];

        /// <summary>Creation times of files that stay where they are (kept), newer than the watermark.</summary>
        public List<DateTimeOffset> Kept { get; } = [];

        /// <summary>The oldest creation time among files still to be handled, or null.</summary>
        public DateTimeOffset? OldestWaiting { get; private set; }

        /// <summary>When the next of them is due, or null.</summary>
        public DateTimeOffset? NextDue { get; private set; }

        public void Waiting(DateTimeOffset created, DateTimeOffset? due)
        {
            if (OldestWaiting is null || created < OldestWaiting)
            {
                OldestWaiting = created;
            }

            if (due is { } at && (NextDue is null || at < NextDue))
            {
                NextDue = at;
            }
        }
    }
}
