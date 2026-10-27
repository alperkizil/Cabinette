using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services.Tray;
using Cabinette.Core.Models;

namespace Cabinette.App.Services.Monitor;

/// <summary>
/// Turns applied vault batches into grouped notifications ("3 files added to Images"), merging batches that arrive
/// within a few seconds. Shown as a tray notification, or as an in-app notice while the window is in front.
/// Clicking opens the window, and the category when exactly one was involved. Honors "Notifications on vault changes".
/// </summary>
public sealed class VaultChangeNotifier : IDisposable
{
    private readonly VaultMonitorHost _monitor;
    private readonly SettingsCache _settings;
    private readonly TrayIconService _tray;
    private readonly INotificationService _inApp;
    private readonly AppLifetime _lifetime;
    private readonly CategoryDirectory _directory;
    private readonly IShellNavigator _navigator;
    private readonly DispatcherTimer _timer;
    private VaultChangeSummary? _pending;
    private DateTimeOffset _firstAt;
    private DateTimeOffset _lastAt;

    public VaultChangeNotifier(
        VaultMonitorHost monitor,
        SettingsCache settings,
        TrayIconService tray,
        INotificationService inApp,
        AppLifetime lifetime,
        CategoryDirectory directory,
        IShellNavigator navigator)
    {
        _monitor = monitor;
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
            Flush();
        };
        _monitor.ChangesApplied += OnChangesApplied;
    }

    public void Dispose()
    {
        _monitor.ChangesApplied -= OnChangesApplied;
        _timer.Stop();
        _pending = null;
    }

    private void OnChangesApplied(object? sender, VaultChangeSummary summary)
    {
        if (!_settings.Current.NotifyOnVaultChanges || _lifetime.IsQuitting)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (_pending is null)
        {
            _pending = summary;
            _firstAt = now;
        }
        else
        {
            _pending = VaultChangeText.Merge(_pending, summary);
        }

        _lastAt = now;
        var wait = VaultChangeText.FlushAt(_firstAt, _lastAt) - now;
        _timer.Stop();
        _timer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void Flush()
    {
        var summary = _pending;
        _pending = null;
        if (summary is null || !_settings.Current.NotifyOnVaultChanges || _lifetime.IsQuitting)
        {
            return;
        }

        var message = VaultChangeText.Build(summary);
        if (message is null)
        {
            return;
        }

        var window = Application.Current?.MainWindow;
        if (NotificationService.IsSeen(window) || !_tray.IsCreated)
        {
            // The user is looking at the app (or there is no tray): an in-app notice is enough.
            if (window is { IsVisible: true })
            {
                var category = message.CategoryName;
                _inApp.Show(
                    message.OneLine,
                    NotificationKind.Info,
                    category is null ? null : L.Get("Intake_Show"),
                    category is null ? null : () => _ = OpenAsync(category));
            }

            return;
        }

        _tray.ShowNotification(message.Title, message.Body, () => _ = OpenAsync(message.CategoryName));
    }

    private async Task OpenAsync(string? categoryName)
    {
        _lifetime.ActivateMainWindow();
        if (categoryName is null)
        {
            return;
        }

        var id = await FindCategoryAsync(categoryName);
        if (id is null)
        {
            // Maybe the category was created moments ago.
            _directory.Invalidate();
            id = await FindCategoryAsync(categoryName);
        }

        if (id is { } found)
        {
            _navigator.OpenCategory(found);
        }
    }

    /// <summary>The category with that name (preferring a top-level one when several share it).</summary>
    private async Task<long?> FindCategoryAsync(string name)
    {
        await _directory.EnsureLoadedAsync();
        var matches = _directory.All.Where(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        return (matches.FirstOrDefault(c => c.ParentId is null) ?? matches[0]).Id;
    }
}
