using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services.Tray;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// The grouped toasts for AI changes (SPEC.md section 10, "Toasts"): each client's changes are merged over the vault
/// monitor's windows and shown as one tray toast titled with the client ("Claude Code", lines like "3 files added to
/// Images"), or, while the user is looking at Cabinette, as one in-app notice ("Claude Code added 3 files to Images").
/// Clicking opens the window, and the category when exactly one was involved. "Notifications when the vault changes"
/// covers them, except a failed vault operation, which always shows. Nothing shows while Cabinette quits.
/// <see cref="Report"/> may be called from any thread (the "ai" handler runs on the local channel's threads); everything
/// else happens on the UI thread. Create it on the UI thread at startup, after the tray icon.
/// </summary>
public sealed class AiChangeNotifier : IAiChangeReporter, IDisposable
{
    /// <summary>A client's pending changes are shown at once when this many have piled up (the window keeps them).</summary>
    public const int MaxPendingPerClient = 500;

    private readonly IAiNoticeSurface _surface;
    private readonly Func<bool> _notificationsOn;
    private readonly Func<bool> _isQuitting;
    private readonly Func<AiCategoryTarget?, bool, Task> _open;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly AiChangeBatcher _batcher = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public AiChangeNotifier(
        SettingsCache settings,
        TrayIconService tray,
        INotificationService inApp,
        AppLifetime lifetime,
        CategoryDirectory directory,
        IShellNavigator navigator)
        : this(
            new AppAiNoticeSurface(tray, inApp),
            () => settings.Current.NotifyOnVaultChanges,
            () => lifetime.IsQuitting,
            (target, activate) => OpenAsync(lifetime, directory, navigator, target, activate),
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            TimeProvider.System)
    {
    }

    /// <param name="open">Opens the window (when the bool is true, for a toast) and the category, if any.</param>
    internal AiChangeNotifier(
        IAiNoticeSurface surface,
        Func<bool> notificationsOn,
        Func<bool> isQuitting,
        Func<AiCategoryTarget?, bool, Task> open,
        Dispatcher dispatcher,
        TimeProvider time)
    {
        _surface = surface;
        _notificationsOn = notificationsOn;
        _isQuitting = isQuitting;
        _open = open;
        _dispatcher = dispatcher;
        _time = time;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            FlushDue();
        };
    }

    /// <summary>Changes waiting for their notice (UI thread).</summary>
    internal int PendingCount => _batcher.Count;

    public void Report(AiChange change)
    {
        if (change is null || _disposed)
        {
            return;
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                Enqueue(change);
            }
            else
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Enqueue(change));
            }
        }
        catch (Exception ex)
        {
            // Never throws: the AI's call already succeeded.
            AppLog.Warn("An AI change could not be queued for its notice.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _batcher.Clear();
    }

    /// <summary>Shows every client's changes whose merge window is over. Called by the timer (UI thread).</summary>
    internal void FlushDue()
    {
        var now = _time.GetUtcNow();
        foreach (var batch in _batcher.TakeDue(now))
        {
            Show(batch.Client, batch.Changes);
        }

        Schedule(now);
    }

    /// <summary>Shows every pending change now (screenshots and tests; UI thread).</summary>
    internal void FlushAll()
    {
        foreach (var batch in _batcher.TakeAll())
        {
            Show(batch.Client, batch.Changes);
        }

        _timer.Stop();
    }

    private void Enqueue(AiChange change)
    {
        try
        {
            if (_disposed || _isQuitting())
            {
                return;
            }

            // With notifications off only a failed vault operation is worth keeping.
            if (change.Kind != AiChangeKind.OperationFailed && !_notificationsOn())
            {
                return;
            }

            var now = _time.GetUtcNow();
            var pending = _batcher.Add(change, now);
            if (pending >= MaxPendingPerClient && _batcher.Take(change.Client) is { } full)
            {
                Show(full.Client, full.Changes);
            }

            Schedule(now);
        }
        catch (Exception ex)
        {
            AppLog.Warn("An AI change could not be queued for its notice.", ex);
        }
    }

    private void Schedule(DateTimeOffset now)
    {
        _timer.Stop();
        if (_disposed || _batcher.NextDue is not { } due)
        {
            return;
        }

        var wait = due - now;
        _timer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void Show(string client, IReadOnlyList<AiChange> changes)
    {
        if (_disposed || _isQuitting())
        {
            return;
        }

        AiChangeMessage? message;
        try
        {
            message = AiChangeText.Build(client, changes, includeSuccess: _notificationsOn());
        }
        catch (Exception ex)
        {
            AppLog.Warn("The notice for an AI's changes could not be worded.", ex);
            return;
        }

        if (message is null)
        {
            return;
        }

        var target = message.Category;
        if (_surface.IsWindowSeen || !_surface.IsTrayAvailable)
        {
            // The user is looking at the app (or there is no tray): an in-app notice is enough.
            if (_surface.IsWindowVisible)
            {
                _surface.ShowInApp(
                    message.OneLine,
                    message.HasProblems ? NotificationKind.Warning : NotificationKind.Info,
                    target is null ? null : L.Get("Intake_Show"),
                    target is null ? null : () => _ = _open(target, false));
            }

            return;
        }

        _surface.ShowToast(message.Title, message.Body, () => _ = _open(target, true));
    }

    /// <summary>Brings the window up (for a toast), then opens the category by id, else by name.</summary>
    private static async Task OpenAsync(AppLifetime lifetime, CategoryDirectory directory, IShellNavigator navigator, AiCategoryTarget? target, bool activate)
    {
        try
        {
            if (activate)
            {
                lifetime.ActivateMainWindow();
            }

            if (target is null)
            {
                return;
            }

            var id = target.Id;
            if (id is null && target.Name is { } name)
            {
                id = await FindCategoryAsync(directory, name);
                if (id is null)
                {
                    // Maybe the category was created moments ago.
                    directory.Invalidate();
                    id = await FindCategoryAsync(directory, name);
                }
            }

            if (id is { } found)
            {
                navigator.OpenCategory(found);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Opening the category of an AI notice failed.", ex);
        }
    }

    /// <summary>The category with that name (preferring a top-level one when several share it).</summary>
    private static async Task<long?> FindCategoryAsync(CategoryDirectory directory, string name)
    {
        await directory.EnsureLoadedAsync();
        var matches = directory.All.Where(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        return (matches.FirstOrDefault(c => c.ParentId is null) ?? matches[0]).Id;
    }
}

/// <summary>Where a grouped AI notice goes: the tray toast or the in-app notice. UI thread.</summary>
internal interface IAiNoticeSurface
{
    /// <summary>The user is looking at Cabinette (<see cref="NotificationService.IsSeen"/>).</summary>
    bool IsWindowSeen { get; }

    /// <summary>The main window is on screen (perhaps behind other windows).</summary>
    bool IsWindowVisible { get; }

    /// <summary>The tray icon exists, so a toast can be shown.</summary>
    bool IsTrayAvailable { get; }

    void ShowToast(string title, string body, Action onClick);

    void ShowInApp(string message, NotificationKind kind, string? actionText, Action? action);
}

/// <summary>The app's tray icon and in-app notices.</summary>
internal sealed class AppAiNoticeSurface(TrayIconService tray, INotificationService inApp) : IAiNoticeSurface
{
    public bool IsWindowSeen => NotificationService.IsSeen(Application.Current?.MainWindow);

    public bool IsWindowVisible => Application.Current?.MainWindow is { IsVisible: true };

    public bool IsTrayAvailable => tray.IsCreated;

    public void ShowToast(string title, string body, Action onClick) => tray.ShowNotification(title, body, onClick);

    public void ShowInApp(string message, NotificationKind kind, string? actionText, Action? action) => inApp.Show(message, kind, actionText, action);
}

/// <summary>
/// Pending AI changes per client, each client with its own merge window (<see cref="AiChangeText.FlushAt"/>): shown
/// shortly after its last change, but at most <see cref="AiChangeText.MergeWindow"/> after its first. Not thread-safe.
/// </summary>
internal sealed class AiChangeBatcher
{
    private readonly Dictionary<string, Batch> _batches = new(StringComparer.Ordinal);

    /// <summary>All pending changes.</summary>
    public int Count => _batches.Values.Sum(b => b.Changes.Count);

    /// <summary>When the next client's notice is due, or null when nothing is pending.</summary>
    public DateTimeOffset? NextDue => _batches.Count == 0 ? null : _batches.Values.Min(b => b.Due);

    /// <summary>Adds a change; returns how many changes its client now has pending.</summary>
    public int Add(AiChange change, DateTimeOffset now)
    {
        var key = Key(change.Client);
        if (!_batches.TryGetValue(key, out var batch))
        {
            batch = new Batch(change.Client?.Trim() ?? "", now);
            _batches[key] = batch;
        }

        batch.Changes.Add(change);
        batch.Due = AiChangeText.FlushAt(batch.FirstAt, now);
        return batch.Changes.Count;
    }

    /// <summary>The clients whose notice is due, oldest first, removed from the pending ones.</summary>
    public IReadOnlyList<AiChangeBatch> TakeDue(DateTimeOffset now)
    {
        var due = _batches.Where(kv => kv.Value.Due <= now).OrderBy(kv => kv.Value.FirstAt).ToList();
        foreach (var (key, _) in due)
        {
            _batches.Remove(key);
        }

        return due.Select(kv => kv.Value.ToResult()).ToList();
    }

    public IReadOnlyList<AiChangeBatch> TakeAll()
    {
        var all = _batches.Values.OrderBy(b => b.FirstAt).Select(b => b.ToResult()).ToList();
        _batches.Clear();
        return all;
    }

    /// <summary>One client's pending changes (removed), or null.</summary>
    public AiChangeBatch? Take(string? client)
        => _batches.Remove(Key(client), out var batch) ? batch.ToResult() : null;

    public void Clear() => _batches.Clear();

    private static string Key(string? client) => client?.Trim() ?? "";

    private sealed class Batch(string client, DateTimeOffset firstAt)
    {
        public string Client { get; } = client;

        public DateTimeOffset FirstAt { get; } = firstAt;

        public DateTimeOffset Due { get; set; } = firstAt;

        public List<AiChange> Changes { get; } = [];

        public AiChangeBatch ToResult() => new(Client, Changes);
    }
}

/// <summary>One client's merged changes.</summary>
internal sealed record AiChangeBatch(string Client, IReadOnlyList<AiChange> Changes);
