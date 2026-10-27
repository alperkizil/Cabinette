using System.IO;
using System.Text.Json;

namespace Cabinette.App.Services;

/// <summary>Small bits of app state that are not settings (stored as JSON in the data folder).</summary>
public sealed record AppState
{
    /// <summary>The "still running in the tray" notice was shown once already.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Categories whose thread was opened, the most recent first (the quick switcher's empty box; see <see cref="RecentCategories"/>).</summary>
    public IReadOnlyList<long>? RecentCategoryIds { get; init; }

    /// <summary>Switching Clean My Downloads mode on was confirmed once (the confirm only shows the first time).</summary>
    public bool CleanDownloadsConfirmed { get; init; }

    /// <summary>
    /// Clean My Downloads mode: files in <see cref="CleanDownloadsFolder"/> created at or before this moment are never touched
    /// (they were there before the mode was switched on, or were dealt with). Null while the mode is off.
    /// </summary>
    public DateTimeOffset? CleanDownloadsSince { get; init; }

    /// <summary>The folder <see cref="CleanDownloadsSince"/> belongs to. Another folder (Downloads was moved) starts afresh.</summary>
    public string? CleanDownloadsFolder { get; init; }

    /// <summary>The Gallery's cell size ("Small", "Medium" or "Large"); null until one was chosen (Medium).</summary>
    public string? GallerySize { get; init; }
}

/// <summary>Reads and writes <see cref="AppState"/> (<c>state.json</c> next to the database). Never throws.</summary>
public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _file;
    private readonly Lock _gate = new();
    private AppState? _state;
    private Task _writes = Task.CompletedTask;

    public AppStateStore(AppPaths paths)
    {
        _file = paths.AppStateFile;
    }

    public AppState Load()
    {
        lock (_gate)
        {
            if (_state is not null)
            {
                return _state;
            }

            try
            {
                _state = File.Exists(_file) ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(_file)) ?? new AppState() : new AppState();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn("App state could not be read.", ex);
                _state = new AppState();
            }

            return _state;
        }
    }

    public void Update(Func<AppState, AppState> change)
    {
        lock (_gate)
        {
            var updated = change(Load());
            _state = updated;

            // In the background, one write after another; each writes the state as it is by then.
            _writes = _writes.ContinueWith(_ => Write(updated), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Completes once every update made so far has been written to the file (or failed to). For tests.</summary>
    internal Task WhenSavedAsync()
    {
        lock (_gate)
        {
            return _writes;
        }
    }

    private void Write(AppState updated)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            lock (_gate)
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(_state ?? updated, JsonOptions));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("App state could not be saved.", ex);
        }
    }
}
