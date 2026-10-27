using Cabinette.App.Services;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>Everything a <see cref="ThreadViewModel"/> needs. Resolved from DI by the factory in <see cref="ThreadFeature"/>.</summary>
public sealed record ThreadServices(
    ILibraryService Library,
    IShellContextMenuService ContextMenu,
    IShellOperations Shell,
    IFileIntake Intake,
    IDialogService Dialogs,
    INotificationService Notifications,
    IShellNavigator Navigator,
    ThumbnailLoader Loader,
    ThreadViewState ViewState)
{
    /// <summary>
    /// Cached category tree, used to tell which files are only in this thread (its category and subcategories).
    /// Optional: without it, a file counts as "only here" when it has a single category.
    /// </summary>
    public CategoryDirectory? Directory { get; init; }

    /// <summary>
    /// The category avatar generator: the banner takes the color it gives a category whose avatar isn't generated yet.
    /// Optional: without it, such a category's banner has the theme's banner color.
    /// </summary>
    public ICategoryIconGenerator? IconGenerator { get; init; }
}

/// <summary>Which presentation the thread pane uses.</summary>
public enum ThreadViewMode
{
    Chat = 0,
    Grid = 1,
    Details = 2,

    /// <summary>Longhorn-style gallery, offered only in Images and its subcategories (SPEC.md section 4, "Gallery").</summary>
    Gallery = 3,
}

/// <summary>
/// Session-wide memory of per-category view choices (e.g. Images stays in Grid while the app runs), and the Gallery's
/// per-category sort. The Gallery's cell size is the one choice remembered across restarts (in the app's state file).
/// </summary>
public sealed class ThreadViewState
{
    private readonly Dictionary<long, ThreadViewMode> _modes = [];
    private readonly Dictionary<long, GallerySort> _gallerySorts = [];
    private readonly Action<GalleryCellSize>? _saveGallerySize;
    private readonly Lock _gate = new();
    private GalleryCellSize _gallerySize = GalleryCellSize.Medium;
    private bool _gallerySizeChosen;

    /// <summary>Everything in memory only (tests, the dev harness, sample-data and screenshot runs).</summary>
    public ThreadViewState()
        : this(null, null)
    {
    }

    /// <param name="loadGallerySize">Reads the stored cell size (runs on a background thread); null keeps it in memory only.</param>
    /// <param name="saveGallerySize">Stores a newly chosen cell size; must not block.</param>
    public ThreadViewState(Func<GalleryCellSize?>? loadGallerySize, Action<GalleryCellSize>? saveGallerySize)
    {
        _saveGallerySize = saveGallerySize;
        GallerySizeLoaded = loadGallerySize is null ? Task.CompletedTask : Task.Run(() => MarkGallerySizeLoaded(loadGallerySize));
    }

    /// <summary>Completes once the stored Gallery cell size was read (or failed to read).</summary>
    public Task GallerySizeLoaded { get; }

    /// <summary>The Gallery's cell size. Setting it stores it for the next start.</summary>
    public GalleryCellSize GallerySize
    {
        get
        {
            lock (_gate)
            {
                return _gallerySize;
            }
        }

        set
        {
            lock (_gate)
            {
                _gallerySizeChosen = true;
                if (_gallerySize == value)
                {
                    return;
                }

                _gallerySize = value;
            }

            try
            {
                _saveGallerySize?.Invoke(value);
            }
            catch (Exception ex)
            {
                AppLog.Warn("The gallery size could not be saved.", ex);
            }
        }
    }

    /// <summary>The state behind the app: the cell size in the app's state file, except in sandboxed dev runs (memory only).</summary>
    public static ThreadViewState Create(AppStateStore? store, CommandLineOptions? options)
        => store is null || options is null || options.IsSandboxed
            ? new ThreadViewState()
            : new ThreadViewState(
                () => Enum.TryParse<GalleryCellSize>(store.Load().GallerySize, ignoreCase: true, out var size) && Enum.IsDefined(size) ? size : null,
                size => store.Update(state => state with { GallerySize = size.ToString() }));

    public ThreadViewMode GetViewMode(long categoryId)
        => GetViewMode(categoryId, ThreadViewMode.Chat);

    /// <summary>The view chosen for the category in this session, or <paramref name="fallback"/> (its default view) when none was.</summary>
    public ThreadViewMode GetViewMode(long categoryId, ThreadViewMode fallback)
        => _modes.TryGetValue(categoryId, out var mode) ? mode : fallback;

    public void SetViewMode(long categoryId, ThreadViewMode mode) => _modes[categoryId] = mode;

    /// <summary>The Gallery sort chosen for the category in this session (date added, newest first, until one is).</summary>
    public GallerySort GetGallerySort(long categoryId)
        => _gallerySorts.TryGetValue(categoryId, out var sort) ? sort : GallerySort.Default;

    public void SetGallerySort(long categoryId, GallerySort sort) => _gallerySorts[categoryId] = sort;

    private void MarkGallerySizeLoaded(Func<GalleryCellSize?> load)
    {
        GalleryCellSize? stored;
        try
        {
            stored = load();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The gallery size could not be read.", ex);
            return;
        }

        lock (_gate)
        {
            // A size chosen while the file was being read wins.
            if (!_gallerySizeChosen && stored is { } size)
            {
                _gallerySize = size;
            }
        }
    }
}
