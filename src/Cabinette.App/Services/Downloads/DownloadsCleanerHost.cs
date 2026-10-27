using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services.Monitor;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Downloads;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Services.Downloads;

/// <summary>
/// Runs Clean My Downloads mode (SPEC.md section 5) in this copy of Cabinette: a <see cref="DownloadsCleaner"/> runs while the
/// setting is on and this run may watch a Downloads folder (<see cref="DownloadsFolderChoice"/>). It pauses with the tray's
/// "Pause monitoring" (and while the vault can't be found or is being moved) and catches up when monitoring resumes. Its
/// watermark is remembered in state.json, so files that arrive while Cabinette is closed are handled at the next start, and
/// switching the mode on starts afresh (only files that arrive from then on). Create and use on the UI thread.
/// </summary>
public sealed class DownloadsCleanerHost : IAsyncDisposable, IDisposable
{
    private readonly ILibraryService _library;
    private readonly IRecycleBin _recycleBin;
    private readonly SettingsCache _settings;
    private readonly AppStateStore _state;
    private readonly VaultMonitorHost _monitor;
    private readonly INotificationService _notifications;
    private readonly CommandLineOptions _options;
    private readonly Func<string?> _windowsDownloads;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private DownloadsCleaner? _cleaner;
    private bool _started;
    private bool _disposed;

    public DownloadsCleanerHost(ILibraryService library, IRecycleBin recycleBin, SettingsCache settings, AppStateStore state,
        VaultMonitorHost monitor, INotificationService notifications, CommandLineOptions options)
        : this(library, recycleBin, settings, state, monitor, notifications, options, KnownFolders.TryGetDownloads, TimeProvider.System)
    {
    }

    internal DownloadsCleanerHost(ILibraryService library, IRecycleBin recycleBin, SettingsCache settings, AppStateStore state,
        VaultMonitorHost monitor, INotificationService notifications, CommandLineOptions options, Func<string?> windowsDownloads,
        TimeProvider time)
    {
        _library = library;
        _recycleBin = recycleBin;
        _settings = settings;
        _state = state;
        _monitor = monitor;
        _notifications = notifications;
        _options = options;
        _windowsDownloads = windowsDownloads;
        _time = time;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>Raised on the UI thread with what the cleaner did (for the notifications).</summary>
    public event EventHandler<DownloadsBatch>? Processed;

    /// <summary>The cleaner while the mode runs, else null.</summary>
    internal DownloadsCleaner? Cleaner => _cleaner;

    /// <summary>Tests shorten the cleaner's timings here.</summary>
    internal Func<DownloadsCleanerOptions, DownloadsCleanerOptions>? AdjustOptions { get; set; }

    /// <summary>Follows the setting from now on and starts the cleaner if the mode is on. Call once, on the UI thread.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnSettingsChanged;
        _monitor.PropertyChanged += OnMonitorChanged;
        if (_settings.Current.CleanMyDownloads)
        {
            _ = ApplyAsync(on: true, switchedOn: false);
        }
    }

    /// <summary>
    /// Pausing the vault monitor (the tray's "Pause monitoring", a vault move) and a vault that can't be found hold the cleaner
    /// too: adding files would only fail meanwhile.
    /// </summary>
    internal static bool Holds(bool monitorAvailable, VaultMonitorStatus? status)
        => monitorAvailable && status?.State is VaultMonitorState.Paused or VaultMonitorState.VaultUnavailable;

    /// <summary>Starts or stops the cleaner. <paramref name="switchedOn"/>: the user just switched the mode on (files there now are left alone).</summary>
    internal async Task ApplyAsync(bool on, bool switchedOn)
    {
        await _applyGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!on)
            {
                await StopAsync(forget: true);
                return;
            }

            if (_cleaner is not null)
            {
                return;
            }

            var folder = DownloadsFolderChoice.Resolve(_options, _windowsDownloads);
            if (folder is null)
            {
                var developer = DownloadsFolderChoice.IsDeveloperRun(_options);
                AppLog.Info(developer
                    ? "Clean My Downloads is on but doesn't run in this developer run (see --downloads-dir)."
                    : "Clean My Downloads is on but Windows didn't say where the Downloads folder is.");
                if (switchedOn)
                {
                    _notifications.Show(L.Get(developer ? "Downloads_NotInThisRun" : "Downloads_NoFolder"), NotificationKind.Warning);
                }

                return;
            }

            if (_library.VaultPath is { } vault && DownloadsFolderChoice.IsSameOrInside(folder, vault))
            {
                AppLog.Warn("Clean My Downloads doesn't run: the Downloads folder is the vault or inside it.");
                if (switchedOn)
                {
                    _notifications.Show(L.Get("Downloads_InsideVault"), NotificationKind.Warning);
                }

                return;
            }

            var (watermark, isNew) = DownloadsWatermark.ForStart(_state.Load(), folder, _time.GetUtcNow(), switchedOn);
            if (isNew)
            {
                _state.Update(state => state with { CleanDownloadsSince = watermark, CleanDownloadsFolder = folder });
            }

            var options = new DownloadsCleanerOptions { Folder = folder, Watermark = watermark, ResolveFinalPath = FinalPaths.TryResolve };
            var cleaner = new DownloadsCleaner(_library, _recycleBin, AdjustOptions?.Invoke(options) ?? options, _time);
            cleaner.Processed += OnProcessed;
            cleaner.WatermarkChanged += OnWatermarkChanged;
            _cleaner = cleaner;
            if (Holds(_monitor.IsAvailable, _monitor.Status))
            {
                await cleaner.PauseAsync();
            }

            cleaner.Start();
            AppLog.Info($"Clean My Downloads: watching the Downloads folder for files created after {watermark:u}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Clean My Downloads could not be started or stopped.", ex);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _monitor.PropertyChanged -= OnMonitorChanged;

        // ConfigureAwait(false): may be waited on synchronously from the UI thread at exit.
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync(forget: false).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
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

    /// <param name="forget">The mode was switched off: forget the watermark, so switching it on again starts afresh.</param>
    private async Task StopAsync(bool forget)
    {
        var cleaner = _cleaner;
        _cleaner = null;
        if (cleaner is not null)
        {
            cleaner.Processed -= OnProcessed;
            cleaner.WatermarkChanged -= OnWatermarkChanged;
            try
            {
                await cleaner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Stopping Clean My Downloads failed or took too long.", ex);
            }

            AppLog.Info("Clean My Downloads stopped.");
        }

        if (forget)
        {
            _state.Update(state => state.CleanDownloadsSince is null && state.CleanDownloadsFolder is null
                ? state
                : state with { CleanDownloadsSince = null, CleanDownloadsFolder = null });
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.CleanMyDownloads != e.Current.CleanMyDownloads)
        {
            _ = ApplyAsync(e.Current.CleanMyDownloads, switchedOn: e.Current.CleanMyDownloads);
        }
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VaultMonitorHost.Status) or nameof(VaultMonitorHost.IsAvailable))
        {
            _ = FollowMonitorAsync();
        }
    }

    private async Task FollowMonitorAsync()
    {
        var cleaner = _cleaner;
        if (cleaner is null || _disposed)
        {
            return;
        }

        var hold = Holds(_monitor.IsAvailable, _monitor.Status);
        if (hold == cleaner.IsPaused)
        {
            return;
        }

        try
        {
            if (hold)
            {
                await cleaner.PauseAsync();
                AppLog.Info("Clean My Downloads paused with the vault monitor.");
            }
            else
            {
                await cleaner.ResumeAsync();
                AppLog.Info("Clean My Downloads resumed with the vault monitor.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Pausing or resuming Clean My Downloads failed.", ex);
        }
    }

    private void OnProcessed(object? sender, DownloadsBatch batch)
        => _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && ReferenceEquals(sender, _cleaner))
            {
                Processed?.Invoke(this, batch);
            }
        });

    /// <summary>Background thread: remembers how far the cleaner got, for the next start.</summary>
    private void OnWatermarkChanged(object? sender, DateTimeOffset watermark)
    {
        if (sender is not DownloadsCleaner cleaner)
        {
            return;
        }

        _state.Update(state => DownloadsFolderChoice.PathsEqual(state.CleanDownloadsFolder, cleaner.Folder) && state.CleanDownloadsSince < watermark
            ? state with { CleanDownloadsSince = watermark }
            : state);
    }
}

/// <summary>Where the cleaner starts from (see <see cref="AppState.CleanDownloadsSince"/>).</summary>
internal static class DownloadsWatermark
{
    /// <summary>
    /// The remembered watermark when it belongs to <paramref name="folder"/> and the mode wasn't just switched on; otherwise
    /// now (then <c>IsNew</c> is true and it should be remembered). A remembered time in the future (the clock was set back)
    /// counts as now.
    /// </summary>
    public static (DateTimeOffset Watermark, bool IsNew) ForStart(AppState state, string folder, DateTimeOffset now, bool switchedOn)
    {
        if (!switchedOn && state.CleanDownloadsSince is { } since && since <= now
            && DownloadsFolderChoice.PathsEqual(state.CleanDownloadsFolder, folder))
        {
            return (since, false);
        }

        return (now, true);
    }
}
