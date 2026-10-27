using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Cabinette.App.Localization;
using Cabinette.App.Services.Monitor;
using Cabinette.Core.Services;
using Cabinette.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Cabinette.App.Services.Tray;

/// <summary>
/// The notification-area icon: status tooltip and icon variant (idle, syncing, paused, error), left click opens the
/// window, and a context menu styled like the app's own menus (the implicit ContextMenu/MenuItem styles and the text-size
/// transform are app-wide resources, so they apply to the tray menu's popup too). Also shows the tray notifications.
/// Create, use and dispose on the UI thread.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly VaultMonitorHost _monitor;
    private readonly AppLifetime _lifetime;
    private readonly IShellOperations _shell;
    private readonly ILibraryService _library;
    private readonly INotificationService _notifications;
    private readonly Dictionary<MonitorIndicator, System.Drawing.Icon> _icons = [];
    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _balloonIcon;
    private MonitorIndicator? _shownIndicator;
    private Action? _balloonAction;
    private MenuItem? _statusItem;
    private MenuItem? _pauseItem;
    private bool _disposed;

    public TrayIconService(VaultMonitorHost monitor, AppLifetime lifetime, IShellOperations shell, ILibraryService library, INotificationService notifications)
    {
        _monitor = monitor;
        _lifetime = lifetime;
        _shell = shell;
        _library = library;
        _notifications = notifications;
    }

    /// <summary>True when the icon is in the notification area (closing the window may then hide it to the tray).</summary>
    public bool IsCreated => _icon is { IsCreated: true } && !_disposed;

    /// <summary>Adds the icon to the notification area. Returns false (and logs) if that failed.</summary>
    public bool TryCreate()
    {
        if (_icon is not null || _disposed)
        {
            return IsCreated;
        }

        try
        {
            var icon = new TaskbarIcon
            {
                ToolTipText = _monitor.TrayTooltip,
                NoLeftClickDelay = true,
                MenuActivation = PopupActivationMode.RightClick,
                ContextMenu = BuildMenu(),
            };
            icon.TrayLeftMouseUp += (_, _) => _lifetime.ActivateMainWindow();
            icon.TrayBalloonTipClicked += OnBalloonClicked;
            _icon = icon;
            UpdateFromMonitor();
            icon.ForceCreate(enablesEfficiencyMode: false);
            _monitor.PropertyChanged += OnMonitorPropertyChanged;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("The tray icon could not be created.", ex);
            _icon?.Dispose();
            _icon = null;
            return false;
        }
    }

    /// <summary>A tray notification (a toast on Windows 10/11). <paramref name="onClick"/> runs when it is clicked.</summary>
    public void ShowNotification(string title, string message, Action? onClick = null)
    {
        if (!IsCreated)
        {
            return;
        }

        _balloonAction = onClick;
        try
        {
            // The app logo as the notification's icon. Windows wants exactly the large icon size (32 px at 100 %).
            var largeSize = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXICON);
            _balloonIcon ??= LoadIcon("Cabinette.ico", largeSize > 0 ? largeSize : 32);
            if (_balloonIcon is { } logo && logo.Width == largeSize)
            {
                _icon!.ShowNotification(title, message, NotificationIcon.None, logo.Handle, largeIcon: true, sound: false);
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AppLog.Info($"The logo can't be used as the notification icon ({ex.Message}).");
        }

        try
        {
            _icon!.ShowNotification(title, message, NotificationIcon.Info, sound: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Showing a tray notification failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.PropertyChanged -= OnMonitorPropertyChanged;
        try
        {
            _icon?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Removing the tray icon failed.", ex);
        }

        _icon = null;
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
        _balloonIcon?.Dispose();
        _balloonIcon = null;
    }

    // ------------------------------------------------------------------ menu

    /// <summary>The tray menu (also rendered by the screenshot tool).</summary>
    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        _statusItem = new MenuItem
        {
            Header = _monitor.StatusLine,
            Icon = new SymbolIcon { Symbol = StatusSymbol(_monitor.Indicator), FontSize = 18 },
            IsEnabled = false,

            // A status line, not a disabled command: keep it readable.
            Opacity = 1,
        };
        _statusItem.SetResourceReference(Control.ForegroundProperty, "Cab.TextSecondary");
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());

        Add(menu, "Tray_Open", SymbolRegular.Open20, _lifetime.ActivateMainWindow);
        Add(menu, "Tray_OpenVault", SymbolRegular.FolderOpen20, () => _ = OpenVaultFolderAsync());
        _pauseItem = Add(menu, "Tray_Pause", SymbolRegular.Pause20, () => _ = TogglePauseAsync());
        menu.Items.Add(new Separator());
        Add(menu, "Tray_Quit", SymbolRegular.Power20, () => _ = _lifetime.QuitAsync());

        menu.Opened += (_, _) => RefreshMenu();
        RefreshMenu();
        return menu;
    }

    private static MenuItem Add(ContextMenu menu, string textKey, SymbolRegular symbol, Action action)
    {
        var item = new MenuItem
        {
            Header = L.Get(textKey),
            Icon = new SymbolIcon { Symbol = symbol, FontSize = 18 },
        };
        item.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        menu.Items.Add(item);
        return item;
    }

    private void RefreshMenu()
    {
        if (_statusItem is not null)
        {
            _statusItem.Header = _monitor.StatusLine;
            if (_statusItem.Icon is SymbolIcon symbol)
            {
                symbol.Symbol = StatusSymbol(_monitor.Indicator);
            }
        }

        if (_pauseItem is not null)
        {
            var paused = _monitor.IsPaused;
            _pauseItem.Header = L.Get(paused ? "Tray_Resume" : "Tray_Pause");
            if (_pauseItem.Icon is SymbolIcon symbol)
            {
                symbol.Symbol = paused ? SymbolRegular.Play20 : SymbolRegular.Pause20;
            }

            _pauseItem.IsEnabled = _monitor.CanTogglePause;
        }
    }

    private static SymbolRegular StatusSymbol(MonitorIndicator indicator) => indicator switch
    {
        MonitorIndicator.Idle => SymbolRegular.CheckmarkCircle20,
        MonitorIndicator.Syncing => SymbolRegular.ArrowSync20,
        MonitorIndicator.Paused => SymbolRegular.PauseCircle20,
        MonitorIndicator.Error => SymbolRegular.Warning20,
        _ => SymbolRegular.EyeOff20,
    };

    private async Task TogglePauseAsync()
    {
        var wasPaused = _monitor.IsPaused;
        if (!await _monitor.TogglePauseAsync())
        {
            _notifications.Show(L.Get(wasPaused ? "Monitor_ResumeFailed" : "Monitor_PauseFailed"), NotificationKind.Error);
        }
    }

    private async Task OpenVaultFolderAsync()
    {
        var path = _library.VaultPath;
        var exists = path is not null && await Task.Run(() => Directory.Exists(path));
        if (exists)
        {
            ShellLauncher.OpenFolder(_shell, path!);
            return;
        }

        _lifetime.ActivateMainWindow();
        _notifications.Show(L.Format("Category_FolderNotFound", path ?? ""), NotificationKind.Warning);
    }

    // ------------------------------------------------------------------ status

    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VaultMonitorHost.Status) or nameof(VaultMonitorHost.IsAvailable) or nameof(VaultMonitorHost.IsBusy))
        {
            UpdateFromMonitor();
        }
    }

    private void UpdateFromMonitor()
    {
        if (_icon is null || _disposed)
        {
            return;
        }

        _icon.ToolTipText = _monitor.TrayTooltip;
        var indicator = _monitor.Indicator;
        if (indicator != _shownIndicator)
        {
            var file = indicator switch
            {
                MonitorIndicator.Syncing => "tray-syncing.ico",
                MonitorIndicator.Paused => "tray-paused.ico",
                MonitorIndicator.Error => "tray-error.ico",
                _ => "tray-idle.ico",
            };
            if (!_icons.TryGetValue(indicator, out var icon))
            {
                icon = LoadIcon(file, SmallIconSize());
                if (icon is not null)
                {
                    _icons[indicator] = icon;
                }
            }

            if (icon is not null)
            {
                try
                {
                    // TaskbarIcon disposes the icon it replaces, so it gets its own copy and the cached original stays usable.
                    _icon.Icon = (System.Drawing.Icon)icon.Clone();
                    _shownIndicator = indicator;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException or ExternalException)
                {
                    // A status icon is cosmetic: it must never break the monitor status update (or a restore that stops it).
                    AppLog.Warn("Updating the tray icon failed.", ex);
                }
            }
        }

        RefreshMenu();
    }

    private void OnBalloonClicked(object sender, RoutedEventArgs e)
    {
        var action = _balloonAction;
        _balloonAction = null;
        if (action is null)
        {
            _lifetime.ActivateMainWindow();
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tray notification action failed.", ex);
        }
    }

    /// <summary>The tray uses small icons (16 px at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %).</summary>
    private static int SmallIconSize()
    {
        var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        return size > 0 ? size : 16;
    }

    private static System.Drawing.Icon? LoadIcon(string file, int size)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute));
            if (info is null)
            {
                return null;
            }

            using var stream = info.Stream;
            return new System.Drawing.Icon(stream, size, size);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Loading the icon {file} failed.", ex);
            return null;
        }
    }
}
