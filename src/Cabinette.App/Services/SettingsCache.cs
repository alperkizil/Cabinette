using System.Windows;
using System.Windows.Threading;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Services;

public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;
}

/// <summary>
/// The latest settings, kept in memory for code that must decide synchronously (e.g. close to tray when the window's
/// close button is clicked). Reloaded whenever the library reports a settings change. Events are raised on the UI thread.
/// </summary>
public sealed class SettingsCache : IDisposable
{
    private readonly ILibraryService _library;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public SettingsCache(ILibraryService library)
    {
        _library = library;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _library.Changed += OnLibraryChanged;
    }

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised on the UI thread after the settings were reloaded and differ from before.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        AppSettings loaded;
        try
        {
            loaded = await _library.GetSettingsAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("Reloading settings failed.", ex);
            return;
        }

        var previous = Current;
        Current = loaded;
        if (previous != loaded)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(previous, loaded));
        }
    }

    /// <summary>Sets the cache without raising <see cref="Changed"/> (at startup).</summary>
    public void Initialize(AppSettings settings) => Current = settings;

    public void Dispose()
    {
        _disposed = true;
        _library.Changed -= OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        if (!e.Kind.HasFlag(LibraryChangeKind.Settings) || _disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(async () => await RefreshAsync());
    }
}
