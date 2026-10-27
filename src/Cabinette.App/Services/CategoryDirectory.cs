using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Services;

/// <summary>
/// A cached snapshot of every category (id → category) for path labels like "Code › Python".
/// Refreshed lazily after category changes. Thread-safe.
/// </summary>
public sealed class CategoryDirectory
{
    public const string PathSeparator = " › ";

    private readonly ILibraryService _library;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<long, Category> _byId = new Dictionary<long, Category>();
    private volatile bool _stale = true;

    public CategoryDirectory(ILibraryService library)
    {
        _library = library;
    }

    public IReadOnlyCollection<Category> All => (IReadOnlyCollection<Category>)_byId.Values;

    public void Invalidate() => _stale = true;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (!_stale)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_stale)
            {
                return;
            }

            _stale = false;
            var all = await _library.GetAllCategoriesAsync(ct).ConfigureAwait(false);
            _byId = all.ToDictionary(c => c.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stale = true;
            AppLog.Warn("Loading the category list failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Category? Get(long id) => _byId.TryGetValue(id, out var category) ? category : null;

    public string GetName(long id) => Get(id)?.Name ?? "";

    /// <summary>"Code › Python" (top-level ancestor first).</summary>
    public string GetPath(long id)
    {
        var names = new List<string>();
        var map = _byId;
        long? current = id;
        var guard = 0;
        while (current is { } cid && map.TryGetValue(cid, out var category) && guard++ < 64)
        {
            names.Add(category.Name);
            current = category.ParentId;
        }

        names.Reverse();
        return string.Join(PathSeparator, names);
    }

    /// <summary>Ids of the category and all its descendants.</summary>
    public HashSet<long> GetSubtree(long id)
    {
        var result = new HashSet<long> { id };
        var map = _byId;
        bool added;
        do
        {
            added = false;
            foreach (var category in map.Values)
            {
                if (category.ParentId is { } parent && result.Contains(parent) && result.Add(category.Id))
                {
                    added = true;
                }
            }
        }
        while (added);

        return result;
    }
}
