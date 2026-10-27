using System.Diagnostics;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.Core.Vault;

/// <summary>
/// Watches the vault with a <see cref="FileSystemWatcher"/>, collects events into batches (a quiet period, capped) and has the
/// library reconcile the affected folders. Reconciling compares the database with the disk, so events caused by Cabinette's
/// own writes change nothing. A missing vault removes nothing: the monitor reports it and retries on a timer.
/// </summary>
internal sealed class VaultMonitor : IVaultMonitor
{
    /// <summary>More dirty folders than this in one batch: a full rescan is cheaper than tracking them.</summary>
    private const int MaxPendingFolders = 2000;

    /// <summary>A file that stays open for writing this many batches in a row is hashed anyway (logs and the like).</summary>
    private const int AllowWritersAfterAttempts = 5;

    private readonly LibraryService _library;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetime = new();

    private readonly HashSet<string> _pendingFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string OldPath, string NewPath)> _pendingRenames = [];
    private readonly Dictionary<string, int> _deferAttempts = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingFull;
    private bool _batchOpen;
    private long _firstEventAt;
    private long _lastEventAt;
    private long _eventsSeen;

    private Task? _loop;
    private FileSystemWatcher? _watcher;
    private ITimer? _retryTimer;
    private CancellationTokenSource? _batchCts;
    private bool _started;
    private bool _userPaused;
    private bool _suspended;
    private bool _unavailable;
    private bool _retrying;
    private bool _applying;
    private bool _restartWatcher;
    private bool _disposed;
    private int _applyingCount;
    private string? _unavailableMessage;
    private DateTimeOffset? _lastSyncAt;
    private VaultMonitorStatus _lastRaised = new(VaultMonitorState.Stopped, 0, null, null);

    public VaultMonitor(LibraryService library, TimeProvider time)
    {
        _library = library;
        _time = time;
    }

    public event EventHandler<VaultMonitorStatus>? StatusChanged;

    public event EventHandler<VaultChangeSummary>? ChangesApplied;

    // ------------------------------------------------------------ timing (tests shorten these)

    /// <summary>A batch is applied once no event arrived for this long...</summary>
    internal TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>...or once its first event is this old.</summary>
    internal TimeSpan MaxBatchDelay { get; set; } = TimeSpan.FromSeconds(5);

    internal TimeSpan UnavailableRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When files that are still being written are looked at again.</summary>
    internal TimeSpan DeferredRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    internal IReadOnlyList<TimeSpan> LockRetryDelays { get; set; } = ReconcileRequest.DefaultLockRetryDelays;

    public VaultMonitorStatus Status
    {
        get
        {
            lock (_gate)
            {
                return ComputeStatusLocked();
            }
        }
    }

    // ------------------------------------------------------------ public API

    public Task StartAsync(CancellationToken ct = default)
        => Task.Run(async () =>
        {
            bool alreadyStarted;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                alreadyStarted = _started;
                _started = true;
                _loop ??= Task.Run(RunLoopAsync);
            }

            if (alreadyStarted)
            {
                if (Status.State == VaultMonitorState.VaultUnavailable)
                {
                    await RetryAsync().ConfigureAwait(false);
                }

                return;
            }

            await StartWatchingAsync(ct).ConfigureAwait(false);
            UpdateStatus();
        }, ct);

    public Task PauseAsync()
        => Task.Run(async () =>
        {
            CancellationTokenSource? batch;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _userPaused = true;
                StopWatcherLocked();
                StopRetryTimerLocked();
                ClearPendingLocked();
                batch = _batchCts;
            }

            TryCancel(batch);
            await WaitForApplyAsync().ConfigureAwait(false);
            UpdateStatus();
        });

    public Task ResumeAsync(CancellationToken ct = default)
        => Task.Run(async () =>
        {
            bool suspended;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _userPaused = false;
                _started = true;
                _unavailable = false;
                _unavailableMessage = null;
                suspended = _suspended;
                _loop ??= Task.Run(RunLoopAsync);
            }

            if (!suspended)
            {
                await StartWatchingAsync(ct).ConfigureAwait(false);
            }

            UpdateStatus();
        }, ct);

    public Task RescanAsync(CancellationToken ct = default)
        => Task.Run(async () =>
        {
            bool unavailable;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_userPaused || _suspended)
                {
                    return;
                }

                unavailable = _started && _unavailable;
            }

            if (unavailable)
            {
                await RetryAsync().ConfigureAwait(false);
                return;
            }

            await ApplyAsync(FullRequest(), ct, requireActive: false).ConfigureAwait(false);
        }, ct);

    public ValueTask DisposeAsync() => new(Task.Run(DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatcherLocked();
            StopRetryTimerLocked();
            ClearPendingLocked();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        await WaitForApplyAsync().ConfigureAwait(false);
        UpdateStatus();
    }

    // ------------------------------------------------------------ used by vault operations and tests

    /// <summary>Stops watching while a vault operation (move) runs. Waits for a batch in flight.</summary>
    internal async Task SuspendAsync()
    {
        CancellationTokenSource? batch;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _suspended = true;
            StopWatcherLocked();
            StopRetryTimerLocked();
            ClearPendingLocked();
            batch = _batchCts;
        }

        TryCancel(batch);
        await WaitForApplyAsync().ConfigureAwait(false);
        UpdateStatus();
    }

    /// <summary>Ends <see cref="SuspendAsync"/>: if the monitor was started and the user hasn't paused it, it watches the (possibly new) vault again.</summary>
    internal async Task ResumeAfterSuspendAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _suspended = false;
            _unavailable = false;
            _unavailableMessage = null;
        }

        await StartWatchingAsync(CancellationToken.None).ConfigureAwait(false);
        UpdateStatus();
    }

    /// <summary>Completes once no events arrived and nothing was pending or being applied for <paramref name="settle"/> (tests).</summary>
    internal async Task WaitForQuietAsync(TimeSpan settle, TimeSpan timeout)
    {
        var start = _time.GetTimestamp();
        var quietSince = start;
        long seen = -1;
        while (true)
        {
            var now = _time.GetTimestamp();
            bool busy;
            long current;
            lock (_gate)
            {
                busy = HasPendingLocked() || _applying;
                current = _eventsSeen;
            }

            busy |= _applyLock.CurrentCount == 0;
            if (busy || current != seen)
            {
                seen = current;
                quietSince = now;
            }
            else if (_time.GetElapsedTime(quietSince, now) >= settle)
            {
                return;
            }

            if (_time.GetElapsedTime(start, now) > timeout)
            {
                throw new TimeoutException("The vault monitor did not settle.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------ batching loop

    private async Task RunLoopAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                while (_signal.CurrentCount > 0 && _signal.Wait(0))
                {
                    // Drain: one batch covers all the signals so far.
                }

                try
                {
                    await DebounceAsync(ct).ConfigureAwait(false);
                    var request = TakePending();
                    if (request is not null)
                    {
                        await ApplyAsync(request, CancellationToken.None, requireActive: true).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Never let one bad batch stop the monitor.
                    Debug.WriteLine($"Cabinette: vault monitor batch failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    private async Task DebounceAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                if (!HasPendingLocked())
                {
                    return;
                }

                var now = _time.GetTimestamp();
                var quiet = QuietPeriod - _time.GetElapsedTime(_lastEventAt, now);
                var cap = MaxBatchDelay - _time.GetElapsedTime(_firstEventAt, now);
                wait = quiet < cap ? quiet : cap;
            }

            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private ReconcileRequest? TakePending()
    {
        lock (_gate)
        {
            if (!IsActiveLocked() || !HasPendingLocked())
            {
                ClearPendingLocked();
                return null;
            }

            var request = new ReconcileRequest
            {
                Full = _pendingFull,
                DirtyFolders = _pendingFull ? [] : _pendingFolders.ToList(),
                Renames = _pendingRenames.ToList(),
                AllowWriters = AllowWritersLocked(),
                LockRetryDelays = LockRetryDelays,
            };
            _applyingCount = Math.Max(1, _pendingPaths.Count);
            ClearPendingLocked();
            return request;
        }
    }

    private ReconcileRequest FullRequest()
    {
        lock (_gate)
        {
            return new ReconcileRequest { Full = true, AllowWriters = AllowWritersLocked(), LockRetryDelays = LockRetryDelays };
        }
    }

    /// <summary>Applies one batch (or a full rescan). Returns null when nothing was applied.</summary>
    private async Task<VaultChangeSummary?> ApplyAsync(ReconcileRequest request, CancellationToken ct, bool requireActive)
    {
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        try
        {
            await _applyLock.WaitAsync(batch.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            bool restartWatcher;
            lock (_gate)
            {
                if (_disposed || _userPaused || _suspended || (requireActive && !IsActiveLocked()))
                {
                    return null;
                }

                _applying = true;
                _batchCts = batch;
                restartWatcher = request.Full && _restartWatcher && _started && !_unavailable;
                _restartWatcher = false;
            }

            UpdateStatus();
            if (restartWatcher)
            {
                // After a watcher error, start a fresh watcher before the rescan so nothing is missed afterwards.
                string? message;
                lock (_gate)
                {
                    message = StartWatcherLocked();
                }

                if (message is not null)
                {
                    EnterUnavailable(message);
                    return null;
                }
            }

            ReconcileOutcome outcome;
            try
            {
                outcome = await _library.ReconcileAsync(request, batch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (batch.IsCancellationRequested)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cabinette: applying vault changes failed: {ex}");
                ScheduleFollowUp([], full: true, UnavailableRetryInterval);
                return null;
            }

            if (outcome.VaultUnavailable)
            {
                EnterUnavailable(outcome.Message ?? "The vault folder can't be found.");
                return null;
            }

            int attempts;
            lock (_gate)
            {
                _lastSyncAt = _time.GetUtcNow();
                attempts = TrackDeferralsLocked(outcome.DeferredFiles);
            }

            if (outcome.DeferredFolders.Count > 0)
            {
                var delay = DeferredRetryDelay * Math.Clamp(attempts, 1, 15);
                ScheduleFollowUp(outcome.DeferredFolders, full: false, delay);
            }

            if (!outcome.Summary.IsEmpty)
            {
                Raise(ChangesApplied, outcome.Summary);
            }

            return outcome.Summary;
        }
        finally
        {
            lock (_gate)
            {
                _applying = false;
                _applyingCount = 0;
                if (ReferenceEquals(_batchCts, batch))
                {
                    _batchCts = null;
                }
            }

            _applyLock.Release();
            UpdateStatus();
        }
    }

    /// <summary>Queues folders (or a full rescan) again after <paramref name="delay"/>.</summary>
    private void ScheduleFollowUp(IReadOnlyCollection<string> folders, bool full, TimeSpan delay)
    {
        var ct = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                if (!IsActiveLocked())
                {
                    return;
                }

                if (full)
                {
                    _pendingFull = true;
                    _pendingFolders.Clear();
                }

                foreach (var folder in folders)
                {
                    AddFolderLocked(folder);
                    _pendingPaths.Add(folder);
                }

                TouchLocked();
            }

            _signal.Release();
            UpdateStatus();
        }, CancellationToken.None);
    }

    private int TrackDeferralsLocked(IReadOnlyCollection<string> deferredFiles)
    {
        if (deferredFiles.Count == 0)
        {
            _deferAttempts.Clear();
            return 0;
        }

        var max = 0;
        foreach (var path in deferredFiles)
        {
            var count = _deferAttempts.GetValueOrDefault(path) + 1;
            _deferAttempts[path] = count;
            max = Math.Max(max, count);
        }

        return max;
    }

    private HashSet<string>? AllowWritersLocked()
    {
        var paths = _deferAttempts.Where(kv => kv.Value >= AllowWritersAfterAttempts).Select(kv => kv.Key).ToList();
        return paths.Count == 0 ? null : paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ watching and availability

    /// <summary>Starts the watcher, then rescans (so changes made during the rescan are caught too).</summary>
    private async Task StartWatchingAsync(CancellationToken ct)
    {
        string? message;
        lock (_gate)
        {
            if (!IsActiveLocked())
            {
                return;
            }

            message = StartWatcherLocked();
        }

        if (message is not null)
        {
            EnterUnavailable(message);
            return;
        }

        await ApplyAsync(FullRequest(), ct, requireActive: true).ConfigureAwait(false);
    }

    /// <summary>Returns null when watching, or a message explaining why the vault can't be watched.</summary>
    private string? StartWatcherLocked()
    {
        StopWatcherLocked();
        var vault = _library.VaultPath;
        if (vault is null)
        {
            return "No vault folder has been chosen yet.";
        }

        if (!Directory.Exists(vault))
        {
            return $"The vault folder can't be found: {vault}";
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(vault)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Cabinette: could not watch {vault}: {ex.Message}");
            watcher?.Dispose();
            return $"The vault folder can't be watched: {vault}";
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
            // Already broken (e.g. the drive went away).
        }

        watcher.Created -= OnChanged;
        watcher.Changed -= OnChanged;
        watcher.Deleted -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    private void StopRetryTimerLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    /// <summary>The vault is missing or unreadable: remove nothing, stop watching, and try again every <see cref="UnavailableRetryInterval"/>.</summary>
    private void EnterUnavailable(string message)
    {
        lock (_gate)
        {
            if (_disposed || !_started || _userPaused || _suspended)
            {
                return;
            }

            _unavailable = true;
            _unavailableMessage = message;
            StopWatcherLocked();
            ClearPendingLocked();
            StopRetryTimerLocked();
            _retryTimer = _time.CreateTimer(_ => _ = Task.Run(RetryAsync), null, UnavailableRetryInterval, UnavailableRetryInterval);
        }

        UpdateStatus();
    }

    private async Task RetryAsync()
    {
        lock (_gate)
        {
            if (!_unavailable || _retrying || _disposed || _userPaused || _suspended)
            {
                return;
            }

            _retrying = true;
        }

        try
        {
            var vault = _library.VaultPath;
            if (vault is null || !Directory.Exists(vault))
            {
                return;
            }

            lock (_gate)
            {
                _unavailable = false;
                _unavailableMessage = null;
                StopRetryTimerLocked();
            }

            UpdateStatus();
            await StartWatchingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Cabinette: vault retry failed: {ex}");
        }
        finally
        {
            lock (_gate)
            {
                _retrying = false;
            }
        }
    }

    // ------------------------------------------------------------ watcher events (thread pool)

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // A folder's own timestamps changed: whatever changed inside it raises its own event.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || !IsActiveLocked() || !AddPathLocked(e.Name))
            {
                return;
            }

            TouchLocked();
        }

        _signal.Release();
        UpdateStatus();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || !IsActiveLocked())
            {
                return;
            }

            var oldTracked = AddPathLocked(e.OldName);
            var newTracked = AddPathLocked(e.Name);
            if (!oldTracked && !newTracked)
            {
                return;
            }

            if (oldTracked && newTracked && e.OldName is not null && e.Name is not null)
            {
                _pendingRenames.Add((e.OldName, e.Name));
            }

            TouchLocked();
        }

        _signal.Release();
        UpdateStatus();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || !IsActiveLocked())
            {
                return;
            }

            // Buffer overflow (or a broken watcher): only a full rescan is reliable now.
            Debug.WriteLine($"Cabinette: vault watcher error: {e.GetException().Message}");
            _pendingFull = true;
            _pendingFolders.Clear();
            _restartWatcher = true;
            _pendingPaths.Add("*");
            TouchLocked();
        }

        _signal.Release();
        UpdateStatus();
    }

    /// <summary>Records a changed path (relative to the vault). Returns false for ignored names.</summary>
    private bool AddPathLocked(string? relative)
    {
        if (relative is null)
        {
            _pendingFull = true;
            _pendingFolders.Clear();
            return true;
        }

        var name = Path.GetFileName(relative);
        if (name.Length > 0 && NameRules.IsIgnoredVaultName(name))
        {
            return false;
        }

        _pendingPaths.Add(relative);
        AddFolderLocked(Path.GetDirectoryName(relative) ?? "");
        return true;
    }

    private void AddFolderLocked(string folder)
    {
        if (_pendingFull)
        {
            return;
        }

        _pendingFolders.Add(folder);
        if (_pendingFolders.Count > MaxPendingFolders)
        {
            _pendingFull = true;
            _pendingFolders.Clear();
        }
    }

    private void TouchLocked()
    {
        var now = _time.GetTimestamp();
        if (!_batchOpen)
        {
            _batchOpen = true;
            _firstEventAt = now;
        }

        _lastEventAt = now;
        _eventsSeen++;
    }

    private bool HasPendingLocked() => _pendingFull || _pendingFolders.Count > 0;

    private void ClearPendingLocked()
    {
        _pendingFull = false;
        _pendingFolders.Clear();
        _pendingPaths.Clear();
        _pendingRenames.Clear();
        _batchOpen = false;
    }

    private bool IsActiveLocked() => _started && !_userPaused && !_suspended && !_unavailable && !_disposed;

    // ------------------------------------------------------------ status and events

    private VaultMonitorStatus ComputeStatusLocked()
    {
        VaultMonitorState state;
        string? message = null;
        if (_disposed || !_started)
        {
            state = VaultMonitorState.Stopped;
        }
        else if (_userPaused || _suspended)
        {
            state = VaultMonitorState.Paused;
        }
        else if (_unavailable)
        {
            state = VaultMonitorState.VaultUnavailable;
            message = _unavailableMessage;
        }
        else
        {
            // Changes waiting for their batch count as syncing, so Idle always means "the library matches the vault".
            state = _applying || HasPendingLocked() ? VaultMonitorState.Syncing : VaultMonitorState.Idle;
        }

        var pending = state is VaultMonitorState.Syncing or VaultMonitorState.Idle ? _pendingPaths.Count + _applyingCount : 0;
        return new VaultMonitorStatus(state, pending, _lastSyncAt, message);
    }

    /// <summary>Raises <see cref="StatusChanged"/> when the state, message or last sync time changed.</summary>
    private void UpdateStatus()
    {
        VaultMonitorStatus? changed = null;
        lock (_gate)
        {
            var next = ComputeStatusLocked();
            if (next.State != _lastRaised.State || next.Message != _lastRaised.Message || next.LastSyncAt != _lastRaised.LastSyncAt)
            {
                _lastRaised = next;
                changed = next;
            }
        }

        if (changed is not null)
        {
            Raise(StatusChanged, changed);
        }
    }

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
                // A failing subscriber must not stop the monitor.
                Debug.WriteLine($"Cabinette: vault monitor event handler threw: {ex}");
            }
        }
    }

    private async Task WaitForApplyAsync()
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        _applyLock.Release();
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The batch already finished.
        }
    }
}
