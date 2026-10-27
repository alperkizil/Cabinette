using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.App.Services.Downloads;
using Cabinette.App.Services.Explorer;
using Cabinette.App.Services.Instance;
using Cabinette.App.Services.Monitor;
using Cabinette.App.Services.Tray;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Shell;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using Cabinette.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App;

/// <summary>
/// Composition root: parses the flags, makes sure only one Cabinette runs per user and data folder, serves the Explorer
/// entries, opens the library (or the in-memory sample), runs first-run setup, wires the services, starts the tray icon
/// and the vault monitor and shows the main window (unless started with <c>--tray</c> or by COM with <c>-Embedding</c>).
/// See <see cref="CommandLineOptions"/> for the flags.
/// Screenshot and sample-data runs skip the single instance, tray, monitor and Windows integration entirely.
/// </summary>
public partial class App : Application
{
    private readonly AppLifetime _lifetime = new();
    private readonly InstanceMessageDispatcher _instanceDispatcher = new();
    private readonly ExplorerDropQueue _explorerQueue = new();
    private readonly AiCommandGate _aiGate = new();
    private IExplorerDropServer? _explorerServer;
    private AppPaths? _paths;
    private InstanceIdentity? _identity;
    private CommandLineOptions _options = new();
    private ServiceProvider? _services;
    private ThemeService? _theme;
    private ILibraryService? _library;
    private MainViewModel? _mainViewModel;
    private SingleInstanceGuard? _instanceGuard;
    private InstancePipeServer? _pipeServer;
    private SettingsCache? _settingsCache;
    private StartupManager? _startup;
    private VaultMonitorHost? _monitor;
    private TrayIconService? _tray;
    private VaultChangeNotifier? _notifier;
    private AiChangeNotifier? _aiNotifier;
    private DownloadsCleanerHost? _downloads;
    private DownloadsNotifier? _downloadsNotifier;
    private bool _cleanedUp;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _options = CommandLineOptions.Parse(e.Args);

        // The hidden text-extraction child (read_file runs IFilters in it): decided before anything else, and it does
        // nothing else. A malformed child command line ends here too, with an error code.
        if (_options.IsTextExtraction)
        {
            await RunTextExtractionChildAsync();
            return;
        }

        base.OnStartup(e);
        // Screenshot and sample-data runs without --data-dir keep their logs and caches in a temp folder, never in the
        // real data folder.
        var paths = new AppPaths(_options.DataDirectory is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : _options.IsSandboxed ? Path.Combine(Path.GetTempPath(), "Cabinette-sandbox") : AppPaths.DefaultDataDirectory);
        _paths = paths;
        AppLog.Initialize(paths.LogDirectory);
        HookExceptionHandlers();
        if (_options.IsScreenshotMode)
        {
            StartWatchdog();
            LogBindingErrors();
        }

        // ---------------------------------------------------------------- single instance
        if (!_options.IsSandboxed)
        {
            var role = await BecomePrimaryInstanceAsync(paths);
            if (role == StartupRole.ForwardExplorerDrop)
            {
                await ForwardExplorerDropAsync();
                return;
            }

            if (role != StartupRole.Primary)
            {
                Shutdown(0);
                return;
            }

            // Selections a restart (Restore from first run) kept for this start go first; they wait like the others.
            foreach (var kept in await Task.Run(() => ExplorerPendingStore.Take(paths.PendingExplorerFile, DateTimeOffset.Now)))
            {
                _explorerQueue.Enqueue(kept);
            }

            // As early as possible, before the library opens: COM gives a copy it started for an Explorer entry only
            // a limited time to register. Selections wait in the queue until the library is open with a vault.
            StartServingExplorerEntries();
        }

        _theme = new ThemeService(_options.ThemeOverride);
        _theme.Apply(ThemePreference.System);

        try
        {
            await RunAsync(paths);
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed.", ex);
            if (!_options.IsScreenshotMode)
            {
                MessageBox.Show(
                    L.Format("Startup_OpenFailed", ex.Message),
                    L.Get("Startup_OpenFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            await _lifetime.CleanupAsync();
            Shutdown(1);
        }
    }

    /// <summary>The text-extraction child's whole run; the process always ends with its exit code.</summary>
    private async Task RunTextExtractionChildAsync()
    {
        int exitCode;
        try
        {
            exitCode = await TextExtractionChild.RunAsync(_options.ExtractTextPath, _options.ExtractMaxChars);
        }
        catch (Exception)
        {
            exitCode = TextExtractionChild.Failed;
        }

        Shutdown(exitCode);
    }

    /// <summary>
    /// Takes the single-instance mutex and starts the local channel. When another Cabinette (same user, same data
    /// folder) already runs, asks it to show its window (unless started with <c>--tray</c>, or by COM for an Explorer
    /// entry: that copy passes the selection on instead) and returns what this process should do.
    /// </summary>
    private async Task<StartupRole> BecomePrimaryInstanceAsync(AppPaths paths)
    {
        InstanceIdentity identity;
        try
        {
            identity = InstanceNames.ForCurrentUser(paths.DataDirectory);
            _instanceGuard = SingleInstanceGuard.TryAcquire(identity.MutexName);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Single-instance check failed; running without it.", ex);
            return StartupRole.Primary;
        }

        _identity = identity;
        var role = ExplorerStartup.Decide(isPrimary: _instanceGuard is not null, _options);
        switch (role)
        {
            case StartupRole.ExitQuietly:
                AppLog.Info("Cabinette is already running (started with --tray): nothing to do.");
                return role;
            case StartupRole.ForwardExplorerDrop:
                AppLog.Info("Started by Windows for an Explorer entry while Cabinette already runs: passing the files on.");
                return role;
            case StartupRole.ActivateRunning:
                var reply = await InstancePipeClient.SendAsync(identity.PipeName, InstanceMessage.Activate(), TimeSpan.FromSeconds(8));
                AppLog.Info(reply is { Ok: true }
                    ? "Cabinette is already running: brought its window to the front."
                    : $"Cabinette is already running but did not answer ({reply?.Error ?? "no reply"}).");
                return role;
        }

        _instanceDispatcher.Register(InstanceCommands.Activate, _ => Dispatcher.InvokeAsync(() =>
        {
            _lifetime.ActivateMainWindow();
            return InstanceReply.Success;
        }).Task);

        // A copy COM started for an Explorer entry (because it couldn't use this one) passes the selection on here.
        ExplorerStartup.RegisterAddCommand(_instanceDispatcher, AcceptExplorerDrop);

        // Stage 4: tool calls from cabinette-mcp.exe. Answered "not_ready" until the library is open with a vault and the main
        // window is ready (the gate opens at the end of startup).
        _instanceDispatcher.Register(InstanceCommands.Ai, _aiGate.HandleAsync);

        try
        {
            _pipeServer = new InstancePipeServer(identity.PipeName, _instanceDispatcher.HandleLineAsync);
            _pipeServer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The instance channel could not be started.", ex);
        }

        return StartupRole.Primary;
    }

    // -------------------------------------------------------------------- Explorer entries (stage 3)

    /// <summary>Registers the Explorer entries' COM classes (dev runs serve the dev entries). Call on the UI thread.</summary>
    private void StartServingExplorerEntries()
    {
        var classes = ExplorerStartup.ClassesFor(_options);
        var server = ExplorerStartup.CreateServer(classes);
        if (server is null)
        {
            return;
        }

        server.Dropped += (_, drop) => AcceptExplorerDrop(drop);
        server.DropFailed += (_, ex) => AppLog.Warn("A selection from Explorer could not be read.", ex);
        try
        {
            server.Register();
            _explorerServer = server;
            AppLog.Info($"Serving the Explorer entries ({classes}).");
        }
        catch (Exception ex)
        {
            server.Dispose();
            AppLog.Warn("The Explorer entries can't be served by this copy.", ex);
        }
    }

    /// <summary>
    /// One Explorer selection, from the COM drop target or the local channel. Waits until the library is open with a vault.
    /// False when Cabinette can't take it any more (quitting, or first run closed without a vault). Any thread.
    /// </summary>
    private bool AcceptExplorerDrop(ExplorerDrop drop)
    {
        var accepted = _explorerQueue.Enqueue(drop);
        AppLog.Info(accepted
            ? $"Explorer: {drop.Paths.Count} item(s) to add ({InstanceModes.Name(drop.Mode)})."
            : $"Explorer: {drop.Paths.Count} item(s) refused: Cabinette is closing or has no vault.");
        return accepted;
    }

    /// <summary>Adds one selection (the queue is open, so the library and the main window exist). UI thread.</summary>
    private Task AddExplorerDropAsync(ExplorerDrop drop)
    {
        if (_lifetime.IsQuitting || _services is null)
        {
            return Task.CompletedTask;
        }

        return _services.GetRequiredService<FileIntake>().AddFromExplorerAsync(drop, _services.GetRequiredService<ExplorerAddNotifier>());
    }

    /// <summary>
    /// This copy was started by COM for an Explorer entry, but another Cabinette runs that COM couldn't use (for example
    /// one running as administrator): serve the entry, pass the selection to the running copy over the local channel, exit.
    /// </summary>
    private async Task ForwardExplorerDropAsync()
    {
        var server = ExplorerStartup.CreateServer(ExplorerStartup.ClassesFor(_options));
        var pipeName = _identity?.PipeName;
        if (server is null || pipeName is null)
        {
            Shutdown(0);
            return;
        }

        ForwardOutcome outcome;
        using (server)
        {
            server.DropFailed += (_, ex) => AppLog.Warn("A selection from Explorer could not be read.", ex);
            var forwarder = new ExplorerDropForwarder(server, drop => SendExplorerDropAsync(pipeName, drop), ExplorerStartup.ForwarderWaitForDrop);
            outcome = await forwarder.RunAsync();
        }

        AppLog.Info($"Passing an Explorer selection on: {outcome}.");
        if (outcome == ForwardOutcome.Failed)
        {
            // Nothing else would tell the user: this copy has no window or tray icon.
            MessageBox.Show(L.Get("Explorer_ForwardFailed"), L.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Shutdown(outcome is ForwardOutcome.Forwarded or ForwardOutcome.NoDrop ? 0 : 1);
    }

    private static async Task<bool> SendExplorerDropAsync(string pipeName, ExplorerDrop drop)
    {
        var reply = await InstancePipeClient.SendAsync(pipeName, InstanceModes.AddMessage(drop.Mode, drop.Paths), ExplorerStartup.ForwardTimeout);
        if (reply is { Ok: true })
        {
            return true;
        }

        AppLog.Warn($"The running Cabinette didn't take the selection from Explorer ({reply?.Error ?? "no reply"}).");
        return false;
    }

    private async Task RunAsync(AppPaths paths)
    {
        // ---------------------------------------------------------------- library
        IRecycleBin recycleBin = new ShellRecycleBin();
        var firstRunShot = _options.IsScreenshotMode && _options.ScreenshotState.StartsWith("firstrun", StringComparison.Ordinal);
        _library = _options.SampleData
            ? await Task.Run(() => SampleLibraryService.Create(withVault: !firstRunShot))
            : await LibraryFactory.OpenAsync(
                new LibraryOptions { DataDirectory = paths.DataDirectory, DateTakenReader = new ShellDateTakenReader() }, recycleBin);

        var settings = await _library.GetSettingsAsync();
        _theme!.Apply(settings.Theme);
        UiScale.Apply(_options.TextSizeOverride ?? settings.TextSize);
        UiWeight.Apply(_options.BoldTextOverride ?? settings.BoldText);
        UiFont.Apply(_options.FontOverride ?? settings.Font);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(paths.ThumbnailCacheDirectory);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Thumbnail cache folder could not be created.", ex);
            }
        });

        _services = ConfigureServices(paths, recycleBin, _library, _theme, _options, _lifetime);
        _settingsCache = _services.GetRequiredService<SettingsCache>();
        _settingsCache.Initialize(settings);
        _settingsCache.Changed += OnSettingsChanged;
        _lifetime.SetCleanup(CleanupAsync);
        _lifetime.ExitBlocked += (_, _) => _services?.GetService<NotificationService>()?.Show(L.Get("Vault_WaitForOperation"), NotificationKind.Warning);
        SessionEnding += (_, e) => e.Cancel = !_lifetime.TryEndSession();

        // Start with Windows: keep the Run entry in line with the setting (a fake in dev runs, never the real registry).
        if (!_options.IsSandboxed)
        {
            _startup = _services.GetRequiredService<StartupManager>();
            _ = _startup.ApplyAsync(settings.StartWithWindows);
            _ = Task.Run(() => _services.GetRequiredService<AppStateStore>().Load());
        }

        // ---------------------------------------------------------------- first run
        var firstRunShown = false;
        if (_library.VaultPath is null)
        {
            firstRunShown = true;
            _aiGate.WaitForFirstRun();
            var completed = await RunFirstRunAsync();
            if (!completed)
            {
                // No vault: Explorer selections that were waiting for one are not added.
                var dropped = _explorerQueue.Close();
                if (dropped > 0)
                {
                    AppLog.Info($"First run closed without a vault: {dropped} Explorer selection(s) not added.");
                }

                await _lifetime.CleanupAsync();
                Shutdown(0);
                return;
            }

            _aiGate.WaitForStartup();
        }

        // ---------------------------------------------------------------- tray + vault monitor
        var trayReady = false;
        if (!_options.IsSandboxed)
        {
            _monitor = _services.GetRequiredService<VaultMonitorHost>();
            _tray = _services.GetRequiredService<TrayIconService>();
            trayReady = _tray.TryCreate();
            _notifier = _services.GetRequiredService<VaultChangeNotifier>();
            // The AI's grouped toasts: created here, on the UI thread, before the "ai" handler can resolve it on a pipe thread.
            _aiNotifier = _services.GetRequiredService<AiChangeNotifier>();
            _monitor.Start();
        }

        // ---------------------------------------------------------------- main window
        _mainViewModel = _services.GetRequiredService<MainViewModel>();
        var placement = _options.IsScreenshotMode ? null : _services.GetRequiredService<WindowPlacementStore>();
        var window = new MainWindow(_mainViewModel, placement);
        MainWindow = window;
        window.ClosePolicy = DecideClose;
        window.HiddenToTray += OnHiddenToTray;
        window.Closed += async (_, _) =>
        {
            // The window really closed (not hidden to the tray): exit.
            await _lifetime.CleanupAsync();
            Shutdown();
        };
        if (_options.IsScreenshotMode)
        {
            ScreenshotRunner.PrepareOffscreen(window);
        }

        // --tray and -Embedding (COM, for an Explorer entry) start quietly; a second launch meanwhile, or a missing tray
        // icon, still shows the window. So does a COM start that needed first run: the user is already looking at Cabinette.
        var showWindow = !_options.StartQuietly || !trayReady || _lifetime.TakePendingActivation() || (firstRunShown && _options.IsEmbedding);
        if (showWindow)
        {
            window.Show();
        }
        else
        {
            AppLog.Info("Started in the tray.");
        }

        await _mainViewModel.InitializeAsync();
        if (!_options.IsScreenshotMode)
        {
            ThumbnailCacheMaintenance.ScheduleStartupPrune(paths.ThumbnailCacheDirectory);
        }

        if (!_options.IsSandboxed)
        {
            // The library is open with a vault: Explorer selections that arrived meanwhile are added now, later ones as
            // they arrive (each one as one batch, on the UI thread).
            _explorerQueue.Open(drop => Dispatcher.InvokeAsync(() => AddExplorerDropAsync(drop)).Task.Unwrap());

            // Clean My Downloads mode (when on): files that arrived while Cabinette was closed first, then new ones.
            _downloads = _services.GetRequiredService<DownloadsCleanerHost>();
            _downloadsNotifier = _services.GetRequiredService<DownloadsNotifier>();
            _downloads.Start();

            // Stage 4: AI tool calls are served from now on. Resolved here, on the UI thread, whose dispatcher the handler's
            // host keeps.
            if (!_lifetime.IsQuitting)
            {
                _aiGate.Open(_services.GetRequiredService<AiCommandHandler>());
            }
        }

        if (_options.IsScreenshotMode)
        {
            await ScreenshotRunner.CaptureMainAsync(window, _mainViewModel, _theme!, _services, _options.ScreenshotState, _options.ScreenshotPath!);
            Shutdown(0);
        }
    }

    private async Task<bool> RunFirstRunAsync()
    {
        var vm = _services!.GetRequiredService<FirstRunViewModel>();
        var window = new FirstRunWindow(vm)
        {
            // A restore that is running must not be cut off by closing the window.
            CanClose = () => !_lifetime.IsOperationRunning || _lifetime.IsQuitting,
        };
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(window.Completed);

        if (_options.IsScreenshotMode)
        {
            ScreenshotRunner.PrepareOffscreen(window, 820, 540);
            window.Show();
            await ScreenshotRunner.PrepareFirstRunAsync(vm, _options.ScreenshotState);
            await ScreenshotRunner.CaptureAsync(window, _options.ScreenshotPath!);
            window.Close();
            return false;
        }

        MainWindow = window;
        window.Show();
        if (_lifetime.TakePendingActivation())
        {
            _lifetime.ActivateMainWindow();
        }

        return await closed.Task;
    }

    private static ServiceProvider ConfigureServices(AppPaths paths, IRecycleBin recycleBin, ILibraryService library, ThemeService theme, CommandLineOptions options, AppLifetime lifetime)
    {
        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddSingleton(library);
        services.AddSingleton(recycleBin);
        services.AddSingleton(options);
        services.AddSingleton(lifetime);

        // Windows interop
        services.AddSingleton<IThumbnailService>(_ => new ShellThumbnailService(paths.ThumbnailCacheDirectory));
        services.AddSingleton<IShellContextMenuService, ShellContextMenuService>();
        services.AddSingleton<IMediaInfoService, ShellMediaInfoService>();
        services.AddSingleton<IDateTakenReader, ShellDateTakenReader>();
        if (options.SampleIcons)
        {
            services.AddSingleton<ICategoryIconGenerator, SampleIconGenerator>();
        }
        else
        {
            services.AddSingleton<ICategoryIconGenerator, CategoryIconGenerator>();
        }

        services.AddSingleton<IClipboardReader, ClipboardReader>();
        services.AddSingleton<IShellOperations, ShellOperations>();

        // Shell services
        services.AddSingleton<IThemeService>(theme);
        services.AddSingleton<ShellNavigator>();
        services.AddSingleton<IShellNavigator>(sp => sp.GetRequiredService<ShellNavigator>());
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<FileIntake>();
        services.AddSingleton<IFileIntake>(sp => sp.GetRequiredService<FileIntake>());
        services.AddSingleton<CategoryIconService>();
        services.AddSingleton<CategoryDirectory>();
        services.AddSingleton<WindowPlacementStore>();

        // Stage 2: settings cache, Windows startup entry, vault monitor, tray, vault operations
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<IStartupRegistration>(_ => options.IsDevRun ? new DevStartupRegistration() : new RegistryStartupRegistration());
        services.AddSingleton(sp => new StartupManager(
            sp.GetRequiredService<IStartupRegistration>(),
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Cabinette.exe")));
        services.AddSingleton<VaultMonitorHost>();
        services.AddSingleton<VaultOperationsService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<VaultChangeNotifier>();

        // Stage 3: toasts for Explorer selections added while the window isn't on screen
        services.AddSingleton<ExplorerAddNotifier>();

        // Quick switcher (Ctrl+K): recently opened categories (in memory only for sample-data and screenshot runs),
        // and opening a file in its default app from outside a thread
        services.AddSingleton(sp => new RecentCategories(sp.GetRequiredService<AppStateStore>(), options));
        services.AddSingleton<ItemOpener>();
        // Clean My Downloads mode
        services.AddSingleton<DownloadsCleanerHost>();
        services.AddSingleton<DownloadsNotifier>();
        services.AddSingleton<DownloadsModeConfirmation>();

        // View models
        services.AddTransient<FirstRunViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<Func<SettingsViewModel>>(sp => () => sp.GetRequiredService<SettingsViewModel>());
        services.AddSingleton<MainViewModel>();

        // Thread pane (owned by the thread workstream)
        services.AddThreadFeature();

        // Stage 4: the tools cabinette-mcp.exe calls, reading file contents for them, and the AI UI (toasts, Settings › AI)
        services.AddAiCommands();
        services.AddAiContentReading();
        services.AddAiUi();

        return services.BuildServiceProvider();
    }

    // -------------------------------------------------------------------- window, tray, settings

    /// <summary>The close button: hide to the tray (setting on), wait for a running operation, or exit.</summary>
    private WindowCloseAction DecideClose()
    {
        if (_lifetime.IsQuitting)
        {
            return WindowCloseAction.Close;
        }

        if (_tray is { IsCreated: true } && _settingsCache?.Current.CloseToTray != false)
        {
            return WindowCloseAction.HideToTray;
        }

        if (_lifetime.IsOperationRunning)
        {
            _services?.GetService<NotificationService>()?.Show(L.Get("Vault_WaitForOperation"), NotificationKind.Warning);
            return WindowCloseAction.Block;
        }

        _lifetime.MarkQuitting();
        return WindowCloseAction.Close;
    }

    /// <summary>The first time the window goes to the tray, say that Cabinette is still running.</summary>
    private void OnHiddenToTray(object? sender, EventArgs e)
    {
        var store = _services?.GetService<AppStateStore>();
        if (store is null || _tray is null || store.Load().TrayHintShown)
        {
            return;
        }

        _tray.ShowNotification(L.Get("Tray_HintTitle"), L.Get("Tray_HintBody"));
        store.Update(state => state with { TrayHintShown = true });
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_startup is not null && e.Previous.StartWithWindows != e.Current.StartWithWindows)
        {
            _ = _startup.ApplyAsync(e.Current.StartWithWindows);
        }
    }

    /// <summary>Async cleanup before exiting or restarting (runs once, on the UI thread).</summary>
    private async Task CleanupAsync()
    {
        // First: no new AI calls ("Cabinette is closing."), no new Explorer selections (COM then starts or finds another copy),
        // and none still waiting. A restart (Restore) keeps the waiting ones for the next start, which adds them to the
        // restored library.
        _aiGate.Close();
        _explorerServer?.Dispose();
        var waiting = _explorerQueue.TakeAndClose();
        if (waiting.Count > 0)
        {
            if (_lifetime.IsRestarting && _paths is { } paths)
            {
                await Task.Run(() => ExplorerPendingStore.Save(paths.PendingExplorerFile, waiting, DateTimeOffset.Now));
            }
            else
            {
                AppLog.Info($"Quitting: {waiting.Count} waiting Explorer selection(s) not added.");
            }
        }
        _notifier?.Dispose();
        _aiNotifier?.Dispose();
        _tray?.Dispose();
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync();
        }

        _mainViewModel?.Dispose();
        _downloadsNotifier?.Dispose();
        if (_downloads is not null)
        {
            await _downloads.DisposeAsync();
        }

        if (_monitor is not null)
        {
            await _monitor.DisposeAsync();
        }

        _settingsCache?.Dispose();
        if (_library is not null)
        {
            try
            {
                await _library.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Closing the library failed or took too long.", ex);
            }
        }

        // Last: let a restarted (or next) Cabinette take over.
        _instanceGuard?.Release();
        _cleanedUp = true;
    }

    // -------------------------------------------------------------------- errors

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception (fatal).", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (e.Exception is LibraryException library)
        {
            _services?.GetService<NotificationService>()?.Show(library.Message, NotificationKind.Error);
            return;
        }

        AppLog.Error("Unhandled UI exception.", e.Exception);
        if (_options.IsScreenshotMode)
        {
            return;
        }

        if (MainWindow is MainWindow { IsVisible: true } && _services?.GetService<NotificationService>() is { } notifications)
        {
            notifications.Show(L.Get("Notify_Error"), NotificationKind.Error, L.Get("Notify_OpenLog"), FileIntake.OpenLogFolder);
        }
        else
        {
            MessageBox.Show(L.Format("Crash_Message", AppLog.Directory ?? ""), L.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Dev aid for screenshot runs: XAML binding errors go to the log instead of the debugger output.</summary>
    private static void LogBindingErrors()
    {
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
    }

    private sealed class BindingErrorListener : System.Diagnostics.TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message) => AppLog.Warn("Binding: " + message);
    }

    /// <summary>Screenshot runs must never hang or leave a process behind.</summary>
    private static void StartWatchdog()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) =>
        {
            AppLog.Error("Screenshot run timed out.");
            Environment.Exit(3);
        };
        timer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ => Environment.Exit(4), TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normally CleanupAsync already ran (quit, restart, window closed). This is the fallback for other exits.
        try
        {
            // Also after a failed startup, which exits before the cleanup is set up. Safe to repeat.
            _aiGate.Close();
            _explorerServer?.Dispose();
            if (!_cleanedUp)
            {
                _notifier?.Dispose();
                _aiNotifier?.Dispose();
                _tray?.Dispose();
                _pipeServer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                _mainViewModel?.Dispose();
                _downloadsNotifier?.Dispose();
                _downloads?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                _monitor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                _settingsCache?.Dispose();
                if (_library is not null)
                {
                    // Give the library a moment to flush; never block exit for long.
                    _library.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                }
            }

            _theme?.Dispose();
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Shutdown cleanup failed.", ex);
        }
        finally
        {
            _instanceGuard?.Release();
        }

        base.OnExit(e);
    }
}
