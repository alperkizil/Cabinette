using System.IO;

namespace Cabinette.App.Services;

/// <summary>Where the app keeps its own data (database, thumbnail cache, logs, window state).</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>Normally %LocalAppData%\Cabinette.</summary>
    public string DataDirectory { get; }

    public string ThumbnailCacheDirectory => Path.Combine(DataDirectory, "thumbs");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string WindowStateFile => Path.Combine(DataDirectory, "window.json");

    /// <summary>Small app state that is not a setting (see <see cref="AppStateStore"/>).</summary>
    public string AppStateFile => Path.Combine(DataDirectory, "state.json");

    /// <summary>Explorer selections still waiting when Cabinette restarted (after a Restore), for the next start.</summary>
    public string PendingExplorerFile => Path.Combine(DataDirectory, "explorer-pending.json");

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cabinette");

    /// <summary>Suggested vault on first run: %UserProfile%\Documents\Cabinette Vault.</summary>
    public static string DefaultVaultSuggestion =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Cabinette Vault");
}
