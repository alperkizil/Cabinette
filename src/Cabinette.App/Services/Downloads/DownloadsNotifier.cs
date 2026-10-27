using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services.Monitor;
using Cabinette.App.Services.Tray;
using Cabinette.Core.Downloads;

namespace Cabinette.App.Services.Downloads;

/// <summary>
/// Turns what Clean My Downloads mode did into grouped notifications ("3 files from Downloads added to Images"), merging
/// batches that arrive within a few seconds like the vault monitor's. A Windows toast while the window isn't in front, an
/// in-app notice while it is. Clicking opens the window, and the category when all moved files went to one.
/// "Notifications when the vault changes" covers them, except files that stay in Downloads and failures, which always show.
/// Use on the UI thread.
/// </summary>
public sealed class DownloadsNotifier : IDisposable
{
    private readonly DownloadsCleanerHost _host;
    private readonly SettingsCache _settings;
    private readonly TrayIconService _tray;
    private readonly INotificationService _inApp;
    private readonly AppLifetime _lifetime;
    private readonly CategoryDirectory _directory;
    private readonly IShellNavigator _navigator;
    private readonly DispatcherTimer _timer;
    private readonly List<DownloadOutcome> _pending = [];
    private DateTimeOffset _firstAt;
    private bool _disposed;

    public DownloadsNotifier(
        DownloadsCleanerHost host,
        SettingsCache settings,
        TrayIconService tray,
        INotificationService inApp,
        AppLifetime lifetime,
        CategoryDirectory directory,
        IShellNavigator navigator)
    {
        _host = host;
        _settings = settings;
        _tray = tray;
        _inApp = inApp;
        _lifetime = lifetime;
        _directory = directory;
        _navigator = navigator;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = FlushAsync();
        };
        _host.Processed += OnProcessed;
    }

    public void Dispose()
    {
        _disposed = true;
        _host.Processed -= OnProcessed;
        _timer.Stop();
        _pending.Clear();
    }

    private void OnProcessed(object? sender, DownloadsBatch batch)
    {
        if (_disposed || _lifetime.IsQuitting || batch.IsEmpty)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (_pending.Count == 0)
        {
            _firstAt = now;
        }

        _pending.AddRange(batch.Outcomes);
        var wait = VaultChangeText.FlushAt(_firstAt, now) - now;
        _timer.Stop();
        _timer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private async Task FlushAsync()
    {
        var outcomes = _pending.ToList();
        _pending.Clear();
        if (outcomes.Count == 0 || _disposed || _lifetime.IsQuitting)
        {
            return;
        }

        try
        {
            // Moved files may have re-created a type category moments ago: make sure its name is known.
            await _directory.EnsureLoadedAsync();
            if (outcomes.Any(o => o.Item is { } item && _directory.Get(item.HomeCategoryId) is null))
            {
                _directory.Invalidate();
                await _directory.EnsureLoadedAsync();
            }

            var message = DownloadsText.Build(outcomes, _directory.GetName, includeSuccess: _settings.Current.NotifyOnVaultChanges);
            if (message is null || _disposed || _lifetime.IsQuitting)
            {
                return;
            }

            Show(message);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Showing what Clean My Downloads did failed.", ex);
        }
    }

    private void Show(DownloadsMessage message)
    {
        var categoryId = message.CategoryId;
        var window = Application.Current?.MainWindow;
        if (NotificationService.IsSeen(window) || !_tray.IsCreated)
        {
            // The user is looking at the app (or there is no tray): an in-app notice is enough.
            if (window is { IsVisible: true })
            {
                _inApp.Show(
                    message.OneLine,
                    message.HasProblems ? NotificationKind.Warning : NotificationKind.Success,
                    categoryId is null ? null : L.Get("Intake_Show"),
                    categoryId is null ? null : () => _navigator.OpenCategory(categoryId.Value));
            }

            return;
        }

        _tray.ShowNotification(message.Title, message.Body, () => Open(categoryId));
    }

    private void Open(long? categoryId)
    {
        _lifetime.ActivateMainWindow();
        if (categoryId is { } id)
        {
            _navigator.OpenCategory(id);
        }
    }
}
