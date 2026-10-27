using System.Data;
using Cabinette.Core.Categorization;
using Cabinette.Core.Services;
using Dapper;

namespace Cabinette.Core.Data;

/// <summary>
/// In-memory snapshot of all categories (without icons) for tree walks and vault path resolution.
/// Loaded per operation; categories are few, so this is cheap.
/// </summary>
internal sealed class CategoryTree
{
    private const long TopLevelKey = 0;

    private readonly Dictionary<long, CategoryRow> _byId = [];
    private readonly Dictionary<long, List<CategoryRow>> _children = [];
    private readonly Dictionary<long, string> _relativeFolders = [];

    public IEnumerable<CategoryRow> All => _byId.Values;

    public int Count => _byId.Count;

    public long? DeletedId => FindBuiltIn(ExtensionMap.DeletedKey)?.Id;

    public static CategoryTree Load(IDbConnection connection, IDbTransaction? transaction = null)
    {
        var tree = new CategoryTree();
        foreach (var row in connection.Query<CategoryRow>(CategoryRow.SelectSql + " ORDER BY id", transaction: transaction))
        {
            tree.Add(row);
        }

        return tree;
    }

    public void Add(CategoryRow row)
    {
        _byId[row.Id] = row;
        var key = row.ParentId ?? TopLevelKey;
        if (!_children.TryGetValue(key, out var list))
        {
            _children[key] = list = [];
        }

        list.Add(row);
    }

    public CategoryRow? Find(long id) => _byId.GetValueOrDefault(id);

    public CategoryRow Get(long id)
        => Find(id) ?? throw new LibraryException(LibraryErrorCode.NotFound, "The category no longer exists.");

    public CategoryRow? FindBuiltIn(string key)
    {
        foreach (var row in _byId.Values)
        {
            if (string.Equals(row.BuiltInKey, key, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    public IReadOnlyList<CategoryRow> ChildrenOf(long? parentId)
        => _children.TryGetValue(parentId ?? TopLevelKey, out var list) ? list : [];

    /// <summary>The category itself and all its descendants.</summary>
    public List<long> SubtreeIds(long id)
    {
        var result = new List<long>();
        if (!_byId.ContainsKey(id))
        {
            return result;
        }

        var queue = new Queue<long>();
        queue.Enqueue(id);
        var seen = new HashSet<long>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            result.Add(current);
            foreach (var child in ChildrenOf(current))
            {
                queue.Enqueue(child.Id);
            }
        }

        return result;
    }

    /// <summary>Parent, grandparent, ... (nearest first). Excludes the category itself.</summary>
    public List<long> AncestorIds(long id)
    {
        var result = new List<long>();
        var current = Find(id);
        while (current?.ParentId is long parentId && result.Count < 1000)
        {
            result.Add(parentId);
            current = Find(parentId);
        }

        return result;
    }

    /// <summary>From the top-level ancestor down to the category itself.</summary>
    public List<CategoryRow> PathTo(long id)
    {
        var result = new List<CategoryRow>();
        var current = Find(id);
        while (current is not null && result.Count < 1000)
        {
            result.Add(current);
            current = current.ParentId is long p ? Find(p) : null;
        }

        result.Reverse();
        return result;
    }

    public bool IsInSubtree(long id, long rootId)
    {
        if (id == rootId)
        {
            return true;
        }

        foreach (var ancestor in AncestorIds(id))
        {
            if (ancestor == rootId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Folder of the category relative to the vault root ("Code\Python").</summary>
    public string RelativeFolder(long id)
    {
        if (_relativeFolders.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var path = string.Join(Path.DirectorySeparatorChar, PathTo(id).Select(c => c.FolderName));
        _relativeFolders[id] = path;
        return path;
    }

    public string FolderPath(string vaultPath, long id) => Path.Combine(vaultPath, RelativeFolder(id));
}
