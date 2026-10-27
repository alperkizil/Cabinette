using System.Windows;
using System.Windows.Threading;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cabinette.App.Services.Monitor;

/// <summary>
/// The vault monitor's lifecycle in the UI: creates it after the library is open, runs the startup rescan in the
/// background, mirrors its status on the UI thread (tray, Settings) and forwards applied batches for notifications.
/// When the library has no monitor (sample data, or a build without the Core feature) the app simply runs unwatched.
/// </summary>
public sealed partial class VaultMonitorHost : ObservableObject, IAsyncDisposable, IDisposable
{
    private readonly ILibraryService _library;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private IVaultMonitor? _monitor;
    private bool _disposed;

    public VaultMonitorHost(ILibraryService library)
    {
        _library = library;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>Raised on the UI thread after each applied batch that changed something.</summary>
    public event EventHandler<VaultChangeSummary>? ChangesApplied;

    /// <summary>True while a monitor exists (false: not watching in this run).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indicator), nameof(StatusLine), nameof(StatusDetail), nameof(TrayTooltip), nameof(IsPaused), nameof(CanTogglePause), nameof(ToggleActionText))]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indicator), nameof(StatusLine), nameof(StatusDetail), nameof(TrayTooltip), nameof(IsPaused), nameof(CanTogglePause), nameof(ToggleActionText))]
    private VaultMonitorStatus? _status;

    /// <summary>True while a pause or resume request is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTogglePause))]
    private bool _isBusy;

    public MonitorIndicator Indicator => VaultStatusText.Indicator(Status, IsAvailable);

    public string StatusLine => VaultStatusText.Line(Status, IsAvailable, DateTimeOffset.Now);

    public string? StatusDetail => VaultStatusText.Detail(Status, IsAvailable);

    public string TrayTooltip => VaultStatusText.Tooltip(Status, IsAvailable);

    public bool IsPaused => IsAvailable && Status?.State == VaultMonitorState.Paused;

    /// <summary>"Pause" or "Resume" for the button next to the status.</summary>
    public string ToggleActionText => Cabinette.App.Localization.L.Get(IsPaused ? "Monitor_Resume" : "Monitor_Pause");

    public bool CanTogglePause => IsAvailable && !IsBusy && Status is { State: not VaultMonitorState.Stopped };

    /// <summary>Creates the monitor and starts it (rescan, then watch) in the background. Call once, on the UI thread.</summary>
    public void Start()
    {
        if (_disposed || _monitor is not null)
        {
            return;
        }

        IVaultMonitor monitor;
        try
        {
            monitor = _library.CreateVaultMonitor();
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            AppLog.Info($"Vault monitoring is not available in this run ({ex.GetType().Name}).");
            IsAvailable = false;
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("Creating the vault monitor failed.", ex);
            IsAvailable = false;
            return;
        }

        _monitor = monitor;
        monitor.StatusChanged += OnStatusChanged;
        monitor.ChangesApplied += OnChangesApplied;
        IsAvailable = true;
        Status = SafeStatus(monitor);

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await monitor.StartAsync(token).ConfigureAwait(false);
                AppLog.Info("Vault monitor started.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
            {
                AppLog.Info($"Vault monitoring is not available in this run ({ex.GetType().Name}).");
                _ = _dispatcher.BeginInvoke(() => Detach(monitor));
            }
            catch (Exception ex)
            {
                AppLog.Error("Starting the vault monitor failed.", ex);
                _ = _dispatcher.BeginInvoke(() => Status = SafeStatus(monitor));
            }
        });
    }

    /// <summary>Pause (when watching) or resume (when paused). Errors are logged and reported by the caller's notice.</summary>
    public async Task<bool> TogglePauseAsync()
    {
        var monitor = _monitor;
        if (monitor is null || IsBusy)
        {
            return false;
        }

        var resume = IsPaused;
        IsBusy = true;
        try
        {
            if (resume)
            {
                await Task.Run(() => monitor.ResumeAsync(_cts.Token));
            }
            else
            {
                await Task.Run(monitor.PauseAsync);
            }

            Status = SafeStatus(monitor);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn(resume ? "Resuming the vault monitor failed." : "Pausing the vault monitor failed.", ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Dev aid for screenshots: shows a status without any monitor running.</summary>
    public void ShowPreview(VaultMonitorStatus status)
    {
        IsAvailable = true;
        Status = status;
    }

    /// <summary>Re-reads the time-dependent texts (e.g. after midnight).</summary>
    public void RefreshTexts() => OnPropertyChanged(nameof(StatusLine));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dispatcher.CheckAccess())
        {
            IsAvailable = false;
            Status = null;
        }

        // Not disposed: the background start may still hold its token.
        _cts.Cancel();
        var monitor = Interlocked.Exchange(ref _monitor, null);
        if (monitor is not null)
        {
            monitor.StatusChanged -= OnStatusChanged;
            monitor.ChangesApplied -= OnChangesApplied;
            try
            {
                // ConfigureAwait(false): may be waited on synchronously from the UI thread at exit.
                await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Disposing the vault monitor failed or took too long.", ex);
            }
        }
    }

    /// <summary>For the DI container: starts <see cref="DisposeAsync"/> without waiting (normally it already ran).</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _ = DisposeAsync().AsTask();
        }
    }

    private void Detach(IVaultMonitor monitor)
    {
        if (!ReferenceEquals(_monitor, monitor))
        {
            return;
        }

        monitor.StatusChanged -= OnStatusChanged;
        monitor.ChangesApplied -= OnChangesApplied;
        _monitor = null;
        IsAvailable = false;
        Status = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await monitor.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Disposing the vault monitor failed.", ex);
            }
        });
    }

    private void OnStatusChanged(object? sender, VaultMonitorStatus status)
        => _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && ReferenceEquals(sender, _monitor))
            {
                Status = status;
            }
        });

    private void OnChangesApplied(object? sender, VaultChangeSummary summary)
    {
        if (summary.IsEmpty)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                ChangesApplied?.Invoke(this, summary);
            }
        });
    }

    private static VaultMonitorStatus? SafeStatus(IVaultMonitor monitor)
    {
        try
        {
            return monitor.Status;
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException or ObjectDisposedException)
        {
            return null;
        }
    }
}
