using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace Cabinette.App.Services;

/// <summary>
/// App-wide lifetime: bringing the window to the front (tray click, second launch), quitting from the tray, and
/// restarting after a restore. Long vault operations register themselves so the app does not quit in the middle of one.
/// Call on the UI thread.
/// </summary>
public sealed class AppLifetime
{
    private Func<Task>? _cleanup;
    private int _runningOperations;
    private bool _pendingActivation;
    private bool _cleanedUp;

    /// <summary>True once a real exit (quit, restart, Windows sign-out) has started: closing the window must not hide it.</summary>
    public bool IsQuitting { get; private set; }

    /// <summary>True once <see cref="RestartAsync"/> started: a fresh copy follows, so the cleanup may hand work over to it.</summary>
    public bool IsRestarting { get; private set; }

    public bool IsOperationRunning => _runningOperations > 0;

    /// <summary>Raised when quitting was refused because a vault operation is running.</summary>
    public event EventHandler? ExitBlocked;

    /// <summary>
    /// The composition root's async cleanup (tray, instance channel, monitor, library, single-instance mutex),
    /// run once before quitting or restarting.
    /// </summary>
    public void SetCleanup(Func<Task> cleanup) => _cleanup = cleanup;

    /// <summary>Marks a long operation (move, migrate, backup, restore) as running until the result is disposed.</summary>
    public IDisposable BeginOperation()
    {
        _runningOperations++;
        return new Operation(this);
    }

    /// <summary>
    /// Windows is signing out or shutting down, or Setup or the uninstaller is closing Cabinette (Restart Manager sends
    /// the same message). Refused (with <see cref="ExitBlocked"/>) while a vault operation runs, so a move or restore
    /// isn't cut off halfway; Setup then offers Retry. Returns whether the session may end and the windows close.
    /// </summary>
    public bool TryEndSession()
    {
        if (IsOperationRunning)
        {
            ActivateMainWindow();
            ExitBlocked?.Invoke(this, EventArgs.Empty);
            return false;
        }

        MarkQuitting();
        return true;
    }

    /// <summary>A real exit has started (the caller already checked for running operations): let the windows close.</summary>
    public void MarkQuitting() => IsQuitting = true;

    /// <summary>Shows, restores and activates the current main window (main or first-run). Remembered if none exists yet.</summary>
    public void ActivateMainWindow()
    {
        var window = Application.Current?.MainWindow;
        if (window is null || IsQuitting)
        {
            _pendingActivation = true;
            return;
        }

        WindowActivation.Activate(window);
    }

    /// <summary>True (once) when someone asked for the window before it existed.</summary>
    public bool TakePendingActivation()
    {
        var pending = _pendingActivation;
        _pendingActivation = false;
        return pending;
    }

    /// <summary>Quit from the tray: a real exit. Refused (with <see cref="ExitBlocked"/>) while a vault operation runs.</summary>
    public async Task QuitAsync()
    {
        if (IsQuitting)
        {
            return;
        }

        if (IsOperationRunning)
        {
            ActivateMainWindow();
            ExitBlocked?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsQuitting = true;
        await CleanupAsync();
        Application.Current?.Shutdown(0);
    }

    /// <summary>
    /// Starts a fresh copy of Cabinette with the same arguments (minus <c>--tray</c> and COM's <c>-Embedding</c>, so the
    /// window shows) and exits. The single-instance mutex is released by the cleanup first, so the new process can take it.
    /// </summary>
    public async Task RestartAsync()
    {
        IsQuitting = true;
        IsRestarting = true;
        await CleanupAsync();
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var arg in RestartArguments(Environment.GetCommandLineArgs().Skip(1)))
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start)?.Dispose();
            AppLog.Info("Restarting Cabinette.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Restarting Cabinette failed.", ex);
        }

        Application.Current?.Shutdown(0);
    }

    /// <summary>The arguments for a restart: the same ones without the flags that start quietly in the tray.</summary>
    public static IEnumerable<string> RestartArguments(IEnumerable<string> args)
        => args.Where(arg => !string.Equals(arg, CommandLineOptions.TrayFlag, StringComparison.OrdinalIgnoreCase) &&
                             !CommandLineOptions.IsEmbeddingArgument(arg));

    /// <summary>Runs the cleanup once (also used by the normal exit path).</summary>
    public async Task CleanupAsync()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        if (_cleanup is { } cleanup)
        {
            try
            {
                await cleanup();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Cleanup before exit failed.", ex);
            }
        }
    }

    private sealed class Operation(AppLifetime owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner._runningOperations--;
            }
        }
    }
}

/// <summary>Brings a window to the front, also when it is hidden in the tray or minimized.</summary>
public static class WindowActivation
{
    public static void Activate(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized || (handle != IntPtr.Zero && NativeMethods.IsIconic(handle)))
        {
            // SW_RESTORE returns to the state before minimizing (maximized stays maximized).
            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        }

        window.Activate();
        if (!window.IsActive && handle != IntPtr.Zero)
        {
            // Windows may refuse the foreground to a background process; the topmost toggle and an explicit
            // SetForegroundWindow (allowed after the second instance's AllowSetForegroundWindow) get it there.
            window.Topmost = true;
            window.Topmost = false;
            NativeMethods.SetForegroundWindow(handle);
        }

        window.Focus();
    }
}
