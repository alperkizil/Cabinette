using System.Data;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Dapper;

namespace Cabinette.Core.Vault;

/// <summary>What the vault monitor asks the library to reconcile.</summary>
internal sealed class ReconcileRequest
{
    public static IReadOnlyList<TimeSpan> DefaultLockRetryDelays { get; } =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1)];

    /// <summary>Walk the whole vault (startup, resume, watcher overflow).</summary>
    public bool Full { get; init; }

    /// <summary>Folders (relative to the vault, "" = the vault root) whose direct contents changed.</summary>
    public IReadOnlyCollection<string> DirtyFolders { get; init; } = [];

    /// <summary>
    /// Renames reported by the watcher, in order, as relative paths. They are only hints: a hint is used when the database
    /// and the disk agree with it (an old location the library knows that is gone, and a new location that is unknown).
    /// </summary>
    public IReadOnlyList<(string OldPath, string NewPath)> Renames { get; init; } = [];

    /// <summary>Full paths of files that may be hashed although another program keeps them open for writing.</summary>
    public IReadOnlySet<string>? AllowWriters { get; init; }

    /// <summary>Waits between attempts to open a file that is still being written.</summary>
    public IReadOnlyList<TimeSpan> LockRetryDelays { get; init; } = DefaultLockRetryDelays;

    public static ReconcileRequest Rescan() => new() { Full = true };
}

internal sealed class ReconcileOutcome
{
    public VaultChangeSummary Summary { get; init; } = new();

    /// <summary>The vault folder is missing, unreadable or looks empty. Nothing was changed.</summary>
    public bool VaultUnavailable { get; init; }

    public string? Message { get; init; }

    /// <summary>Folders (relative) to look at again later: files still being written, or locked.</summary>
    public IReadOnlyCollection<string> DeferredFolders { get; init; } = [];

    /// <summary>Full paths of the files that were put off.</summary>
    public IReadOnlyCollection<string> DeferredFiles { get; init; } = [];
}

/// <summary>Hashes computed outside the write lock, valid while the file keeps the same size and modification time.</summary>
internal sealed class VaultHashCache
{
    private readonly Dictionary<string, (long Size, long Ticks, byte[] Hash)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(FileInfo file, out byte[] hash)
    {
        if (_entries.TryGetValue(file.FullName, out var entry) && entry.Size == file.Length && entry.Ticks == file.LastWriteTimeUtc.Ticks)
        {
            hash = entry.Hash;
            return true;
        }

        hash = [];
        return false;
    }

    public void Set(string path, long size, DateTime lastWriteUtc, byte[] hash) => _entries[path] = (size, lastWriteUtc.Ticks, hash);
}

/// <summary>A folder of the vault during planning: an existing category (possibly moved or renamed), a new one, or the vault root.</summary>
internal sealed class PlanFolder
{
    public long? CategoryId { get; set; }

    public PlanFolder? Parent { get; set; }

    public List<PlanFolder> Children { get; } = [];

    public string FolderName { get; set; } = "";

    /// <summary>Display name of an existing category.</summary>
    public string? Name { get; init; }

    public CategoryKind Kind { get; init; }

    public long? OriginalParentId { get; init; }

    public bool IsRoot { get; init; }

    /// <summary>A folder with no category yet; becomes a new category.</summary>
    public bool IsNew { get; init; }

    /// <summary>An existing category whose folder was renamed or moved (it gets the folder's new parent and name).</summary>
    public bool Relocated { get; set; }

    /// <summary>The folder's name changed (not only its letter case or its parent), so the category is renamed too.</summary>
    public bool FolderRenamed { get; set; }

    /// <summary>Only the letter case of the folder name differs from the database.</summary>
    public bool FolderCaseChanged { get; set; }

    /// <summary>The folder is gone: the category and its subtree are removed.</summary>
    public bool Missing { get; set; }

    internal List<FileSystemInfo>? Listing { get; set; }

    internal bool Examined { get; set; }

    internal bool ChildrenCompared { get; set; }

    internal bool ExpandedDeep { get; set; }

    internal List<PlanFolder> MatchedChildren { get; } = [];

    public string RelativePath
        => IsRoot ? "" : Parent is null || Parent.IsRoot ? FolderName : Parent.RelativePath + Path.DirectorySeparatorChar + FolderName;

    public string FullPath(string vault) => IsRoot ? vault : Path.Combine(vault, RelativePath);

    public bool IsSameOrInside(PlanFolder other)
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, other))
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerable<PlanFolder> SelfAndDescendants()
    {
        var stack = new Stack<PlanFolder>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }
}

/// <summary>The columns of a copy-mode item that reconciling needs.</summary>
internal sealed class PlanItem
{
    private long? _ticks;

    public long Id { get; set; }

    public long HomeId { get; set; }

    public string FileName { get; set; } = "";

    public long? Size { get; set; }

    public string? ModifiedAt { get; set; }

    public byte[]? Hash { get; set; }

    public long Kind { get; set; }

    /// <summary>The item's "missing" mark (1 = marked missing).</summary>
    public long IsMissing { get; set; }

    public bool SameSizeAndTime(FileInfo file)
    {
        if (Size != file.Length || ModifiedAt is null)
        {
            return false;
        }

        _ticks ??= DbValues.ParseTime(ModifiedAt).UtcTicks;
        return _ticks == file.LastWriteTimeUtc.Ticks;
    }
}

/// <summary>A file on disk with no item at its location.</summary>
internal sealed class PlanNewFile
{
    public required FileInfo Info { get; init; }

    /// <summary>Folder it is in (the vault root for files placed directly in the vault).</summary>
    public required PlanFolder Folder { get; init; }

    public byte[]? Hash { get; set; }

    public bool Paired { get; set; }

    public string RelativePath => Folder.IsRoot ? Info.Name : Folder.RelativePath + Path.DirectorySeparatorChar + Info.Name;
}

/// <summary>Everything one reconcile would change, computed from the database and the disk without changing either.</summary>
internal sealed class ReconcilePlan
{
    public bool Unavailable { get; set; }

    public string? Message { get; set; }

    public PlanFolder Root { get; set; } = new() { IsRoot = true };

    /// <summary>Top-most categories whose folders are gone.</summary>
    public List<PlanFolder> MissingRoots { get; } = [];

    /// <summary>New files to add (with their hash).</summary>
    public List<PlanNewFile> NewFiles { get; } = [];

    /// <summary>Items whose file was renamed or moved (also letter-case-only renames).</summary>
    public List<(PlanItem Item, PlanNewFile File)> Moves { get; } = [];

    /// <summary>Items whose size or modification time differs; Hash is the file's current hash.</summary>
    public List<(PlanItem Item, FileInfo File, byte[] Hash)> Changed { get; } = [];

    /// <summary>Items whose file is gone.</summary>
    public List<PlanItem> Removed { get; } = [];

    /// <summary>Items marked missing whose file is at its place: the mark is cleared.</summary>
    public List<PlanItem> FoundInPlace { get; } = [];

    /// <summary>
    /// Full rescans only: temp files that adds left in category folders before the staging folder existed (see
    /// <see cref="NameRules.IsLegacyCopyTempName"/>). They are deleted.
    /// </summary>
    public List<string> LegacyTempFiles { get; } = [];

    /// <summary>Files to hash before the plan can be applied (first pass only).</summary>
    public List<FileInfo> NeedHash { get; } = [];

    public HashSet<string> DeferredFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> DeferredFiles { get; } = [];
}

/// <summary>
/// Compares the vault on disk with the library and plans the changes (SPEC.md section 8). It only reads, so it can run once
/// without the write lock (to find the files that need hashing) and again under the lock right before applying.
/// The comparison is based on the database, never on the kind of file-system event, so Cabinette's own writes plan nothing.
/// </summary>
internal static class VaultReconciler
{
    /// <summary>Small files that appear between the two passes are hashed right away instead of waiting for the next batch.</summary>
    private const long InlineHashBudgetBytes = 16L * 1024 * 1024;

    private const string ItemSql = """
        SELECT id AS Id, home_category_id AS HomeId, file_name AS FileName, size_bytes AS Size, file_modified_at AS ModifiedAt,
               hash AS Hash, kind AS Kind, is_missing AS IsMissing
        FROM items WHERE mode = 0
        """;

    private static readonly EnumerationOptions ListOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static ReconcilePlan Build(IDbConnection connection, IDbTransaction? transaction, string vault, ReconcileRequest request,
        VaultHashCache hashes, bool finalPass, CancellationToken ct)
    {
        var plan = new ReconcilePlan();
        var exists = Directory.Exists(vault);
        var rootListing = exists ? TryList(vault) : null;
        if (rootListing is null)
        {
            plan.Unavailable = true;
            plan.Message = exists ? $"The vault folder can't be read: {vault}" : $"The vault folder can't be found: {vault}";
            return plan;
        }

        new Builder(connection, transaction, vault, request, hashes, finalPass, plan, ct).Run(rootListing);
        return plan;
    }

    /// <summary>Lists a folder, or returns null when it is missing or can't be read (its parent then decides).</summary>
    internal static List<FileSystemInfo>? TryList(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListOptions).ToList();
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return null;
        }
    }

    internal static string NormalizeRelative(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class NewDir(PlanFolder parent, DirectoryInfo info)
    {
        public PlanFolder Parent => parent;

        public DirectoryInfo Info => info;

        public List<FileSystemInfo>? Listing { get; set; }

        public string RelativePath => parent.IsRoot ? info.Name : parent.RelativePath + Path.DirectorySeparatorChar + info.Name;
    }

    private sealed class Builder(
        IDbConnection connection,
        IDbTransaction? transaction,
        string vault,
        ReconcileRequest request,
        VaultHashCache hashes,
        bool finalPass,
        ReconcilePlan plan,
        CancellationToken ct)
    {
        private readonly PlanFolder _root = plan.Root;
        private readonly Dictionary<string, PlanFolder> _byOriginalPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<(PlanFolder Node, bool Deep)> _queue = new();
        private readonly List<PlanFolder> _examined = [];
        private readonly List<NewDir> _newDirs = [];
        private readonly HashSet<string> _newDirKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PlanFolder> _missing = [];
        private readonly Dictionary<long, List<PlanItem>> _itemsByHome = [];
        private readonly HashSet<long> _loadedHomes = [];
        private readonly HashSet<PlanItem> _missingItems = [];
        private readonly List<(PlanItem Item, FileInfo File)> _changed = [];
        private readonly HashSet<string> _needHashKeys = new(StringComparer.OrdinalIgnoreCase);
        private CategoryTree _tree = null!;
        private HashSet<string> _pointerPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _allItemsLoaded;
        private long _inlineBudget = InlineHashBudgetBytes;

        public void Run(List<FileSystemInfo> rootListing)
        {
            _tree = CategoryTree.Load(connection, transaction);
            BuildNodes();
            _root.Listing = rootListing;

            if (LooksEmpty(rootListing))
            {
                plan.Unavailable = true;
                plan.Message = $"The vault folder is empty: {vault}. If it is on a drive that is not connected, connect it.";
                return;
            }

            if (request.Full)
            {
                _queue.Enqueue((_root, true));
            }
            else
            {
                foreach (var dirty in request.DirtyFolders)
                {
                    _queue.Enqueue((MapDeepest(dirty), false));
                }
            }

            var hints = ComposeHints(request.Renames);
            for (var round = 0; round < 10_000; round++)
            {
                ct.ThrowIfCancellationRequested();
                ProcessQueue();
                if (PairFoldersByHints(hints) || PairFoldersByContent())
                {
                    continue;
                }

                if (_newDirs.Count == 0)
                {
                    break;
                }

                foreach (var dir in _newDirs)
                {
                    var node = new PlanFolder { IsNew = true, Parent = dir.Parent, FolderName = dir.Info.Name, Kind = CategoryKind.User, Listing = dir.Listing };
                    dir.Parent.Children.Add(node);
                    _queue.Enqueue((node, true));
                }

                _newDirs.Clear();
            }

            foreach (var category in _missing)
            {
                foreach (var node in category.SelfAndDescendants())
                {
                    node.Missing = true;
                }

                plan.MissingRoots.Add(category);
            }

            CollectFiles();
            PairFiles(hints);
            Finish();
        }

        // ------------------------------------------------------------ folders

        private void BuildNodes()
        {
            var nodes = new Dictionary<long, PlanFolder>();
            foreach (var row in _tree.All)
            {
                var node = new PlanFolder
                {
                    CategoryId = row.Id,
                    FolderName = row.FolderName,
                    Name = row.Name,
                    Kind = row.CategoryKind,
                    OriginalParentId = row.ParentId,
                };
                nodes[row.Id] = node;
                _byOriginalPath.TryAdd(_tree.RelativeFolder(row.Id), node);
            }

            foreach (var row in _tree.All.OrderBy(r => r.Id))
            {
                var node = nodes[row.Id];
                var parent = row.ParentId is long p && nodes.TryGetValue(p, out var parentNode) ? parentNode : _root;
                node.Parent = parent;
                parent.Children.Add(node);
            }
        }

        /// <summary>
        /// Safety net: a vault root without a single known category folder while the library holds files is far more likely
        /// a wrong or not-yet-mounted drive than the user deleting everything. Treat it as unavailable and remove nothing.
        /// </summary>
        private bool LooksEmpty(List<FileSystemInfo> rootListing)
        {
            if (_root.Children.Count == 0)
            {
                return false;
            }

            var folders = rootListing.OfType<DirectoryInfo>().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_root.Children.Any(c => folders.Contains(c.FolderName)))
            {
                return false;
            }

            return connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM items)", transaction: transaction) != 0;
        }

        private PlanFolder MapDeepest(string relative)
        {
            var node = _root;
            foreach (var part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                var child = node.Children.FirstOrDefault(c => NameEquals(c.FolderName, part));
                if (child is null)
                {
                    break;
                }

                node = child;
            }

            return node;
        }

        private void ProcessQueue()
        {
            while (_queue.Count > 0)
            {
                var (node, deep) = _queue.Dequeue();
                if (node.Missing)
                {
                    continue;
                }

                if (node.Listing is null)
                {
                    node.Listing = TryList(node.FullPath(vault));
                    if (node.Listing is null)
                    {
                        // Gone or unreadable: never treat its contents as deleted from here; the parent's listing decides.
                        continue;
                    }
                }

                if (!node.Examined)
                {
                    node.Examined = true;
                    _examined.Add(node);
                }

                if (!node.ChildrenCompared)
                {
                    CompareChildren(node);
                }

                if (deep && !node.ExpandedDeep)
                {
                    node.ExpandedDeep = true;
                    foreach (var child in node.MatchedChildren)
                    {
                        _queue.Enqueue((child, true));
                    }
                }
            }
        }

        private void CompareChildren(PlanFolder node)
        {
            node.ChildrenCompared = true;
            var dirs = new Dictionary<string, DirectoryInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in node.Listing!.OfType<DirectoryInfo>().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                dirs.TryAdd(dir.Name, dir);
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in node.Children.ToList())
            {
                if (child.IsNew || child.Relocated)
                {
                    matched.Add(child.FolderName);
                    continue;
                }

                if (dirs.TryGetValue(child.FolderName, out var dir))
                {
                    matched.Add(dir.Name);
                    if (!string.Equals(dir.Name, child.FolderName, StringComparison.Ordinal))
                    {
                        child.FolderName = dir.Name;
                        child.FolderCaseChanged = true;
                    }

                    node.MatchedChildren.Add(child);
                }
                else if (!_missing.Contains(child))
                {
                    _missing.Add(child);
                }
            }

            // "Deleted" can't have subcategories; folders created inside it are left alone.
            if (node.Kind == CategoryKind.System)
            {
                return;
            }

            foreach (var dir in dirs.Values)
            {
                if (matched.Contains(dir.Name) || NameRules.IsIgnoredVaultDirectory(dir) || !_newDirKeys.Add(dir.FullName))
                {
                    continue;
                }

                _newDirs.Add(new NewDir(node, dir));
            }
        }

        private bool IsMissingOrInsideMissing(PlanFolder category)
        {
            for (var node = category; node is not null; node = node.Parent)
            {
                if (_missing.Contains(node))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Renamed or moved folders reported by the watcher (old location gone and known, new location new).</summary>
        private bool PairFoldersByHints(List<(string Old, string New)> hints)
        {
            var paired = false;
            foreach (var (oldPath, newPath) in hints)
            {
                if (!_byOriginalPath.TryGetValue(oldPath, out var category) || category.Relocated || !IsMissingOrInsideMissing(category))
                {
                    continue;
                }

                var dir = _newDirs.FirstOrDefault(d => NameEquals(d.RelativePath, newPath));
                if (dir is null || dir.Parent.IsSameOrInside(category))
                {
                    continue;
                }

                Relocate(category, dir);
                paired = true;
            }

            return paired;
        }

        /// <summary>
        /// Without a hint (a rescan after the app was closed): a missing category and a new folder whose contents match at least
        /// half of the category's files (by name and size) and subfolders are the same folder, renamed or moved.
        /// </summary>
        private bool PairFoldersByContent()
        {
            if (_missing.Count == 0 || _newDirs.Count == 0)
            {
                return false;
            }

            foreach (var category in _missing.ToList())
            {
                var items = ItemsOf(category.CategoryId!.Value);
                var total = items.Count + category.Children.Count;
                if (total == 0)
                {
                    continue;
                }

                NewDir? best = null;
                var bestScore = 0;
                foreach (var dir in _newDirs)
                {
                    if (dir.Parent.IsSameOrInside(category))
                    {
                        continue;
                    }

                    dir.Listing ??= TryList(dir.Info.FullName);
                    if (dir.Listing is null)
                    {
                        continue;
                    }

                    var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in dir.Listing)
                    {
                        if (entry is FileInfo f)
                        {
                            files.TryAdd(f.Name, f.Length);
                        }
                        else
                        {
                            folders.Add(entry.Name);
                        }
                    }

                    var score = items.Count(i => files.TryGetValue(i.FileName, out var length) && length == i.Size)
                                + category.Children.Count(c => folders.Contains(c.FolderName));
                    var better = score > bestScore
                                 || (score == bestScore && score > 0 && best is not null
                                     && !ReferenceEquals(best.Parent, category.Parent) && ReferenceEquals(dir.Parent, category.Parent));
                    if (better)
                    {
                        best = dir;
                        bestScore = score;
                    }
                }

                if (best is not null && bestScore * 2 >= total)
                {
                    Relocate(category, best);
                    return true;
                }
            }

            return false;
        }

        private void Relocate(PlanFolder category, NewDir dir)
        {
            category.Parent!.Children.Remove(category);
            category.Parent.MatchedChildren.Remove(category);
            dir.Parent.Children.Add(category);
            category.Parent = dir.Parent;
            if (!NameEquals(category.FolderName, dir.Info.Name))
            {
                category.FolderRenamed = true;
            }

            category.FolderName = dir.Info.Name;
            category.FolderCaseChanged = false;
            category.Relocated = true;
            category.Listing = dir.Listing;
            _missing.Remove(category);
            _newDirs.Remove(dir);
            _queue.Enqueue((category, true));
        }

        // ------------------------------------------------------------ files

        private void CollectFiles()
        {
            var missingNodes = plan.MissingRoots.SelectMany(r => r.SelfAndDescendants()).ToList();
            if (request.Full)
            {
                LoadAllItems();
            }
            else
            {
                LoadItems(_examined.Concat(missingNodes).Where(n => n.CategoryId is not null).Select(n => n.CategoryId!.Value));
            }

            LoadPointerPaths();

            foreach (var node in _examined)
            {
                if (node.Missing)
                {
                    continue;
                }

                var files = node.Listing!.OfType<FileInfo>().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
                if (node.IsRoot)
                {
                    foreach (var file in files)
                    {
                        if (IsCandidate(file))
                        {
                            plan.NewFiles.Add(new PlanNewFile { Info = file, Folder = node });
                        }
                    }

                    continue;
                }

                var byName = new Dictionary<string, PlanItem>(StringComparer.OrdinalIgnoreCase);
                if (node.CategoryId is long id)
                {
                    foreach (var item in ItemsOf(id))
                    {
                        byName.TryAdd(item.FileName, item);
                    }
                }

                var matched = new HashSet<long>();
                foreach (var file in files)
                {
                    if (byName.TryGetValue(file.Name, out var item) && matched.Add(item.Id))
                    {
                        if (!string.Equals(item.FileName, file.Name, StringComparison.Ordinal))
                        {
                            plan.Moves.Add((item, new PlanNewFile { Info = file, Folder = node, Paired = true }));
                        }
                        else if (item.IsMissing != 0)
                        {
                            plan.FoundInPlace.Add(item);
                        }

                        if (!item.SameSizeAndTime(file))
                        {
                            _changed.Add((item, file));
                        }
                    }
                    else if (IsCandidate(file))
                    {
                        plan.NewFiles.Add(new PlanNewFile { Info = file, Folder = node });
                    }
                    else if (request.Full && NameRules.IsLegacyCopyTempName(file.Name) && !_pointerPaths.Contains(file.FullName))
                    {
                        plan.LegacyTempFiles.Add(file.FullName);
                    }
                }

                foreach (var item in byName.Values)
                {
                    if (!matched.Contains(item.Id))
                    {
                        _missingItems.Add(item);
                    }
                }
            }

            foreach (var node in missingNodes)
            {
                if (node.CategoryId is long id)
                {
                    _missingItems.UnionWith(ItemsOf(id));
                }
            }
        }

        /// <summary>A file the monitor would add: not ignored, and not the real file of a pointer item that happens to live in the vault.</summary>
        private bool IsCandidate(FileInfo file) => !NameRules.IsIgnoredVaultFile(file) && !_pointerPaths.Contains(file.FullName);

        private void PairFiles(List<(string Old, string New)> hints)
        {
            // Renames and moves the watcher reported: no hash needed when size and time are unchanged.
            foreach (var (oldPath, newPath) in hints)
            {
                var item = FindItemAtOriginalPath(oldPath);
                if (item is null || !_missingItems.Contains(item))
                {
                    continue;
                }

                var file = plan.NewFiles.FirstOrDefault(f => !f.Paired && NameEquals(f.RelativePath, newPath));
                if (file is not null && item.SameSizeAndTime(file.Info))
                {
                    Pair(item, file);
                }
            }

            foreach (var file in plan.NewFiles)
            {
                if (!file.Paired)
                {
                    file.Hash = GetHash(file.Info);
                }
            }

            // Moves without a hint (a delete plus a create, or a rescan): same size and content.
            var byContent = _missingItems
                .Where(i => i.Hash is not null && i.Size is not null)
                .GroupBy(i => (i.Size!.Value, Convert.ToHexString(i.Hash!)))
                .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).ToList());
            foreach (var file in plan.NewFiles)
            {
                if (file.Paired || file.Hash is null
                    || !byContent.TryGetValue((file.Info.Length, Convert.ToHexString(file.Hash)), out var candidates) || candidates.Count == 0)
                {
                    continue;
                }

                var item = candidates.FirstOrDefault(c => NameEquals(c.FileName, file.Info.Name)) ?? candidates[0];
                candidates.Remove(item);
                Pair(item, file);
            }

            // Items that were never hashed: same name and size.
            foreach (var file in plan.NewFiles)
            {
                if (file.Paired)
                {
                    continue;
                }

                var item = _missingItems.Where(i => i.Hash is null).OrderBy(i => i.Id)
                    .FirstOrDefault(i => i.Size == file.Info.Length && NameEquals(i.FileName, file.Info.Name));
                if (item is not null)
                {
                    Pair(item, file);
                }
            }
        }

        private void Pair(PlanItem item, PlanNewFile file)
        {
            _missingItems.Remove(item);
            file.Paired = true;
            plan.Moves.Add((item, file));
        }

        private PlanItem? FindItemAtOriginalPath(string relative)
        {
            var folder = Path.GetDirectoryName(relative) ?? "";
            var name = Path.GetFileName(relative);
            if (folder.Length == 0 || name.Length == 0 || !_byOriginalPath.TryGetValue(folder, out var node) || node.CategoryId is not long id)
            {
                return null;
            }

            return ItemsOf(id).FirstOrDefault(i => NameEquals(i.FileName, name));
        }

        private void Finish()
        {
            foreach (var (item, file) in _changed)
            {
                var hash = GetHash(file);
                if (hash is not null)
                {
                    plan.Changed.Add((item, file, hash));
                }
                else if (finalPass)
                {
                    Defer(file);
                }
            }

            var deferredSizes = new HashSet<long>();
            foreach (var file in plan.NewFiles.ToList())
            {
                if (file.Paired)
                {
                    plan.NewFiles.Remove(file);
                }
                else if (file.Hash is null)
                {
                    plan.NewFiles.Remove(file);
                    if (finalPass)
                    {
                        Defer(file.Info);
                        deferredSizes.Add(file.Info.Length);
                    }
                }
            }

            foreach (var item in _missingItems.OrderBy(i => i.Id))
            {
                // A file still being written might be this item moved elsewhere; decide on the next attempt.
                if (finalPass && item.Size is long size && deferredSizes.Contains(size))
                {
                    plan.DeferredFolders.Add(_tree.RelativeFolder(item.HomeId));
                    continue;
                }

                plan.Removed.Add(item);
            }
        }

        private byte[]? GetHash(FileInfo file)
        {
            if (hashes.TryGet(file, out var cached))
            {
                return cached;
            }

            if (!finalPass)
            {
                if (_needHashKeys.Add(file.FullName))
                {
                    plan.NeedHash.Add(file);
                }

                return null;
            }

            if (file.Length > _inlineBudget)
            {
                return null;
            }

            try
            {
                var allowWriters = request.AllowWriters?.Contains(file.FullName) == true;
                var (hash, size, lastWrite) = FileOps.HashCompleteFile(file.FullName, allowWriters, ct);
                _inlineBudget -= size;
                if (size != file.Length || lastWrite != file.LastWriteTimeUtc)
                {
                    return null;
                }

                hashes.Set(file.FullName, size, lastWrite, hash);
                return hash;
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                return null;
            }
        }

        private void Defer(FileInfo file)
        {
            plan.DeferredFiles.Add(file.FullName);
            var folder = file.DirectoryName is { } dir ? Path.GetRelativePath(vault, dir) : "";
            plan.DeferredFolders.Add(folder == "." ? "" : folder);
        }

        // ------------------------------------------------------------ database

        private List<PlanItem> ItemsOf(long categoryId)
        {
            if (!_allItemsLoaded && !_loadedHomes.Contains(categoryId))
            {
                LoadItems([categoryId]);
            }

            return _itemsByHome.TryGetValue(categoryId, out var list) ? list : [];
        }

        private void LoadAllItems()
        {
            // Homes loaded earlier (pairing folders by content) keep their instances: one PlanItem per item, so the item sets
            // (_missingItems) and pairing work on the same objects.
            foreach (var item in connection.Query<PlanItem>(ItemSql, transaction: transaction))
            {
                if (!_loadedHomes.Contains(item.HomeId))
                {
                    AddLoaded(item);
                }
            }

            _allItemsLoaded = true;
        }

        private void LoadItems(IEnumerable<long> categoryIds)
        {
            if (_allItemsLoaded)
            {
                return;
            }

            var ids = categoryIds.Where(id => !_loadedHomes.Contains(id)).Distinct().ToList();
            if (ids.Count == 0)
            {
                return;
            }

            _loadedHomes.UnionWith(ids);
            foreach (var item in connection.Query<PlanItem>(ItemSql + " AND home_category_id IN (SELECT value FROM json_each(@ids))",
                         new { ids = DbValues.JsonArray(ids) }, transaction))
            {
                AddLoaded(item);
            }
        }

        private void AddLoaded(PlanItem item)
        {
            if (!_itemsByHome.TryGetValue(item.HomeId, out var list))
            {
                _itemsByHome[item.HomeId] = list = [];
            }

            list.Add(item);
        }

        private void LoadPointerPaths()
        {
            var prefix = Path.EndsInDirectorySeparator(vault) ? vault : vault + Path.DirectorySeparatorChar;
            var like = prefix.Replace("\\", "\\\\", StringComparison.Ordinal)
                             .Replace("%", "\\%", StringComparison.Ordinal)
                             .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            _pointerPaths = connection.Query<string>(
                    "SELECT pointer_path FROM items WHERE mode = 1 AND pointer_path LIKE @like ESCAPE '\\'", new { like }, transaction)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<(string Old, string New)> ComposeHints(IReadOnlyList<(string OldPath, string NewPath)> renames)
        {
            // A→B followed by B→C is A→C.
            var chains = new List<(string Old, string New)>();
            foreach (var (oldRaw, newRaw) in renames)
            {
                var oldPath = NormalizeRelative(oldRaw);
                var newPath = NormalizeRelative(newRaw);
                if (oldPath.Length == 0 || newPath.Length == 0)
                {
                    continue;
                }

                var index = chains.FindIndex(c => NameEquals(c.New, oldPath));
                if (index >= 0)
                {
                    chains[index] = (chains[index].Old, newPath);
                }
                else
                {
                    chains.Add((oldPath, newPath));
                }
            }

            return chains;
        }
    }
}
