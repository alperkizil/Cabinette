namespace Cabinette.App.Services;

/// <summary>
/// The categories whose thread was opened, the most recent first, for the quick switcher's empty box (SPEC section 7).
/// Remembered across restarts in the app state file. Dev runs with sample data or screenshots keep the list in memory
/// only, so they never mix their sample ids into the real list. Thread-safe; never does file I/O on the caller's thread.
/// </summary>
public sealed class RecentCategories
{
    /// <summary>
    /// How many are remembered. More than the switcher shows (<see cref="ViewModels.Dialogs.QuickSwitcherViewModel.MaxRecent"/>),
    /// so it still lists that many when the open thread is left out or some were deleted.
    /// </summary>
    public const int RememberCount = 16;

    private readonly Action<IReadOnlyList<long>>? _save;
    private readonly Lock _gate = new();
    private List<long> _ids = [];
    private bool _loaded;
    private bool _recordedWhileLoading;

    public RecentCategories(AppStateStore store, CommandLineOptions options)
        : this(
            options.IsSandboxed ? null : () => store.Load().RecentCategoryIds ?? [],
            options.IsSandboxed ? null : ids => store.Update(state => state with { RecentCategoryIds = ids }))
    {
    }

    /// <param name="load">Reads the stored list (runs on a background thread); null keeps the list in memory only.</param>
    /// <param name="save">Stores the list. Called only after the stored list was read; must not block.</param>
    public RecentCategories(Func<IReadOnlyList<long>>? load, Action<IReadOnlyList<long>>? save)
    {
        _save = save;
        if (load is null)
        {
            MarkLoaded([]);
            Loaded = Task.CompletedTask;
        }
        else
        {
            Loaded = Task.Run(() => MarkLoaded(LoadSafe(load)));
        }
    }

    /// <summary>Completes once the stored list was read (or failed to read).</summary>
    public Task Loaded { get; }

    /// <summary>The remembered ids, the most recent first.</summary>
    public IReadOnlyList<long> Snapshot()
    {
        lock (_gate)
        {
            return [.. _ids];
        }
    }

    /// <summary>A thread was opened.</summary>
    public void Record(long categoryId)
    {
        List<long> snapshot;
        lock (_gate)
        {
            if (_ids.Count > 0 && _ids[0] == categoryId)
            {
                return;
            }

            _ids = Push(_ids, categoryId, RememberCount);
            if (!_loaded)
            {
                // Saved together with the stored list once it is read (see MarkLoaded).
                _recordedWhileLoading = true;
                return;
            }

            snapshot = [.. _ids];
        }

        Save(snapshot);
    }

    /// <summary><paramref name="id"/> first, then the others in order without it, at most <paramref name="max"/>.</summary>
    internal static List<long> Push(IEnumerable<long> ids, long id, int max)
    {
        var result = new List<long>(max) { id };
        foreach (var other in ids)
        {
            if (result.Count >= max)
            {
                break;
            }

            if (other != id && !result.Contains(other))
            {
                result.Add(other);
            }
        }

        return result;
    }

    private static IReadOnlyList<long> LoadSafe(Func<IReadOnlyList<long>> load)
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The recently opened categories could not be read.", ex);
            return [];
        }
    }

    private void MarkLoaded(IReadOnlyList<long> stored)
    {
        List<long>? snapshot = null;
        lock (_gate)
        {
            // Ids recorded while the file was read are newer than the stored ones.
            var merged = new List<long>(RememberCount);
            foreach (var id in _ids.Concat(stored))
            {
                if (merged.Count < RememberCount && !merged.Contains(id))
                {
                    merged.Add(id);
                }
            }

            _ids = merged;
            _loaded = true;
            if (_recordedWhileLoading)
            {
                _recordedWhileLoading = false;
                snapshot = [.. _ids];
            }
        }

        if (snapshot is not null)
        {
            Save(snapshot);
        }
    }

    private void Save(IReadOnlyList<long> ids)
    {
        if (_save is null)
        {
            return;
        }

        try
        {
            _save(ids);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The recently opened categories could not be saved.", ex);
        }
    }
}
