using System.Windows;
using Cabinette.App.Services.Tray;
using Cabinette.Core.Models;

namespace Cabinette.App.Services.Explorer;

/// <summary>Reports an Explorer batch outside the window while the window isn't on screen.</summary>
internal interface IExplorerAddReporter
{
    /// <summary>
    /// True when the outcome should go to a toast: the window isn't the active window (hidden in the tray, minimized, or
    /// behind Explorer, where the user is), and the tray icon exists. The vault monitor's rule.
    /// </summary>
    bool ReportsOutsideWindow { get; }

    Task ReportAsync(ItemMode mode, AddResult result);

    /// <summary>The batch failed as a whole (for example the vault can't be reached).</summary>
    void ReportFailure(ItemMode mode, string message);
}

/// <summary>
/// The Windows toast for an Explorer batch: the tray notification the vault monitor uses, grouped the same way
/// ("3 files added to Images"), plus duplicates and failures. Clicking it opens the window, and the category when all
/// files went to one. "Notifications when the vault changes" governs it, except that failures are always reported:
/// nothing else would tell the user that files they sent from Explorer didn't arrive. Use on the UI thread.
/// </summary>
internal sealed class ExplorerAddNotifier : IExplorerAddReporter
{
    private readonly TrayIconService _tray;
    private readonly SettingsCache _settings;
    private readonly AppLifetime _lifetime;
    private readonly CategoryDirectory _directory;
    private readonly IShellNavigator _navigator;

    public ExplorerAddNotifier(TrayIconService tray, SettingsCache settings, AppLifetime lifetime, CategoryDirectory directory, IShellNavigator navigator)
    {
        _tray = tray;
        _settings = settings;
        _lifetime = lifetime;
        _directory = directory;
        _navigator = navigator;
    }

    public bool ReportsOutsideWindow => _tray.IsCreated && !NotificationService.IsSeen(Application.Current?.MainWindow);

    public async Task ReportAsync(ItemMode mode, AddResult result)
    {
        // A folder add creates categories moments ago: make sure their names are known.
        await _directory.EnsureLoadedAsync();
        var ids = result.Added.Select(i => i.HomeCategoryId)
            .Concat(result.CreatedCategoryIds)
            .Concat(result.Duplicates.Select(d => d.Existing.HomeCategoryId));
        if (ids.Any(id => _directory.Get(id) is null))
        {
            _directory.Invalidate();
            await _directory.EnsureLoadedAsync();
        }

        Show(ExplorerAddText.Build(mode, result, _directory.GetName, includeSuccess: _settings.Current.NotifyOnVaultChanges));
    }

    public void ReportFailure(ItemMode mode, string message) => Show(ExplorerAddText.Failed(mode, message));

    private void Show(ExplorerAddMessage? message)
    {
        if (message is null || _lifetime.IsQuitting)
        {
            return;
        }

        var categoryId = message.CategoryId;
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
