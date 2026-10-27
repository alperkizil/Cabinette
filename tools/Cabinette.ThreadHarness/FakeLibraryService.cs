using System.IO;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.ThreadHarness;

/// <summary>In-memory <see cref="ILibraryService"/> with realistic behaviour for the harness (no disk writes except notes' text in memory).</summary>
internal sealed class FakeLibraryService : ILibraryService
{
    private readonly Lock _lock = new();
    private readonly List<Category> _categories = [];
    private readonly List<LibraryItem> _items = [];
    private readonly Dictionary<long, string> _noteText = [];
    private readonly InMemoryActivityLog _activity = new();
    private AppSettings _settings = new() { VaultPath = @"C:\Users\Public\Cabinette Vault" };
    private long _nextId = 100_000;

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public string? VaultPath => _settings.VaultPath;

    // ------------------------------------------------------------------ seeding (harness only)

    public Category AddCategory(long id, string name, long? parentId, byte[]? icon = null, string? builtInKey = null)
    {
        var category = new Category
        {
            Id = id,
            Name = name,
            ParentId = parentId,
            FolderName = name,
            IconPng = icon,
            BuiltInKey = builtInKey,
            Kind = builtInKey is null ? CategoryKind.User : CategoryKind.BuiltIn,
            CreatedAt = DateTimeOffset.Now.AddDays(-100),
        };
        lock (_lock)
        {
            _categories.Add(category);
        }

        return category;
    }

    public LibraryItem AddItem(LibraryItem item, string? noteText = null)
    {
        lock (_lock)
        {
            _items.Add(item);
            if (noteText is not null)
            {
                _noteText[item.Id] = noteText;
            }
        }

        return item;
    }

    // ------------------------------------------------------------------ settings

    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(_settings);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        Raise(LibraryChangeKind.Settings, [], []);
        return Task.CompletedTask;
    }

    public Task InitializeVaultAsync(string vaultPath, CancellationToken ct = default) => Task.CompletedTask;

    // ------------------------------------------------------------------ categories

    public Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync(long? parentId, CategorySort sort, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<CategorySummary> list = _categories
                .Where(c => c.ParentId == parentId)
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(Summary)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<CategorySummary?> GetCategorySummaryAsync(long categoryId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var category = _categories.FirstOrDefault(c => c.Id == categoryId);
            return Task.FromResult(category is null ? null : Summary(category));
        }
    }

    public Task<Category?> GetCategoryAsync(long categoryId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_categories.FirstOrDefault(c => c.Id == categoryId));
        }
    }

    public Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Category>>(_categories.ToList());
        }
    }

    public Task<IReadOnlyList<Category>> GetCategoryPathAsync(long categoryId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var path = new List<Category>();
            for (var c = _categories.FirstOrDefault(x => x.Id == categoryId); c is not null; c = _categories.FirstOrDefault(x => x.Id == c.ParentId))
            {
                path.Insert(0, c);
            }

            return Task.FromResult<IReadOnlyList<Category>>(path);
        }
    }

    public Task<Category> GetOrCreateBuiltInCategoryAsync(string builtInKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_categories.First(c => c.BuiltInKey == builtInKey));
        }
    }

    public Task<Category> CreateCategoryAsync(string name, long? parentId, byte[]? iconPng = null, CancellationToken ct = default)
    {
        var category = AddCategory(Interlocked.Increment(ref _nextId), name, parentId, iconPng);
        Raise(LibraryChangeKind.Categories, [category.Id], []);
        return Task.FromResult(category);
    }

    public Task RenameCategoryAsync(long categoryId, string newName, CancellationToken ct = default)
    {
        Update(categoryId, c => c with { Name = newName });
        return Task.CompletedTask;
    }

    public Task SetCategoryIconAsync(long categoryId, byte[]? iconPng, CancellationToken ct = default)
    {
        Update(categoryId, c => c with { IconPng = iconPng });
        return Task.CompletedTask;
    }

    public Task SetCategoryPinnedAsync(long categoryId, bool pinned, CancellationToken ct = default)
    {
        Update(categoryId, c => c with { IsPinned = pinned });
        return Task.CompletedTask;
    }

    public Task DeleteCategoryAsync(long categoryId, CategoryDeletePolicy policy, DeleteItemsOptions? options = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task MarkCategoryViewedAsync(long categoryId, CancellationToken ct = default)
    {
        Console.WriteLine($"[library] MarkCategoryViewed {categoryId}");
        return Task.CompletedTask;
    }

    public Task EmptyDeletedAsync(DeleteItemsOptions? options = null, CancellationToken ct = default) => Task.CompletedTask;

    // ------------------------------------------------------------------ items

    public async Task<IReadOnlyList<LibraryItem>> GetThreadItemsAsync(long categoryId, string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default)
    {
        await Task.Delay(15, ct).ConfigureAwait(false); // feel of a real query
        lock (_lock)
        {
            var subtree = Subtree(categoryId);
            IEnumerable<LibraryItem> query = _items.Where(i => i.CategoryIds.Any(subtree.Contains));
            query = ApplySearch(query, searchText);
            query = ApplyFilter(query, filter);
            return query.OrderBy(i => i.AddedAt).ThenBy(i => i.Id).ToList();
        }
    }

    public Task<LibraryItem?> GetItemAsync(long itemId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.FirstOrDefault(i => i.Id == itemId));
        }
    }

    public Task<SearchResults> SearchAsync(string searchText, ItemFilter? filter = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var items = ApplyFilter(ApplySearch(_items, searchText), filter).OrderByDescending(i => i.AddedAt).ToList();
            return Task.FromResult(new SearchResults([], items));
        }
    }

    public async Task<IReadOnlyList<LibraryItem>> SearchNamePrefixAsync(string searchText, int limit, ItemFilter? filter = null, CancellationToken ct = default)
    {
        var phrase = string.Join(' ', searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith('#')));
        if (phrase.Length == 0 || limit <= 0)
        {
            return [];
        }

        var results = await SearchAsync(searchText, filter, ct); // newest first; OrderBy below is stable
        bool Exact(string name) => string.Equals(name, phrase, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(Path.GetFileNameWithoutExtension(name), phrase, StringComparison.OrdinalIgnoreCase);
        return results.Items
            .Where(i => i.FileName.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => Exact(i.FileName) ? 0 : 1)
            .Take(limit)
            .ToList();
    }

    public Task<AddResult> AddPathsAsync(IReadOnlyList<string> paths, AddRequest request, IProgress<AddProgress>? progress = null, CancellationToken ct = default)
    {
        var batch = Guid.NewGuid().ToString("N");
        var added = new List<LibraryItem>();
        foreach (var path in paths.Where(File.Exists))
        {
            var ext = ExtensionMap.Normalize(Path.GetExtension(path));
            var item = new LibraryItem
            {
                Id = Interlocked.Increment(ref _nextId),
                FileName = Path.GetFileName(path),
                Extension = ext,
                FullPath = path,
                Mode = request.Mode ?? ItemMode.Copy,
                HomeCategoryId = request.TargetCategoryId ?? 1,
                CategoryIds = [request.TargetCategoryId ?? 1],
                SizeBytes = new FileInfo(path).Length,
                AddedAt = DateTimeOffset.Now,
                BatchId = batch,
                TypeKey = ExtensionMap.GetTypeKey(ext),
            };
            AddItem(item);
            added.Add(item);
        }

        Raise(LibraryChangeKind.Items, added.SelectMany(i => i.CategoryIds).Distinct().ToList(), added.Select(i => i.Id).ToList());
        return Task.FromResult(new AddResult { Added = added });
    }

    public Task<LibraryItem> CreateNoteAsync(long categoryId, string text, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var item = new LibraryItem
        {
            Id = Interlocked.Increment(ref _nextId),
            Kind = ItemKind.Note,
            FileName = $"Note {now:yyyy-MM-dd HH-mm-ss}.md",
            Extension = "md",
            FullPath = Path.Combine(VaultPath!, $"Note {now:yyyy-MM-dd HH-mm-ss}.md"),
            HomeCategoryId = categoryId,
            CategoryIds = [categoryId],
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
            AddedAt = now,
            AddedBy = addedBy,
            NotePreview = text.Length > 300 ? text[..300] : text,
            TypeKey = ExtensionMap.NotesKey,
        };
        AddItem(item, text);
        Raise(LibraryChangeKind.Items, Ancestors(categoryId), [item.Id]);
        return Task.FromResult(item);
    }

    public Task<AddResult> AddPastedTextAsync(long categoryId, string text, CancellationToken ct = default)
        => Task.FromResult(new AddResult());

    public Task<AddResult> AddPastedImageAsync(long categoryId, byte[] png, CancellationToken ct = default)
        => Task.FromResult(new AddResult());

    public Task RenameItemAsync(long itemId, string newFileName, CancellationToken ct = default)
    {
        UpdateItem(itemId, i => i with { FileName = newFileName });
        return Task.CompletedTask;
    }

    public Task DeleteItemsAsync(IReadOnlyList<long> itemIds, DeleteItemsOptions? options = null, CancellationToken ct = default)
    {
        List<long> categories;
        lock (_lock)
        {
            categories = _items.Where(i => itemIds.Contains(i.Id)).SelectMany(i => i.CategoryIds).Distinct().ToList();
            _items.RemoveAll(i => itemIds.Contains(i.Id));
        }

        Raise(LibraryChangeKind.Items, categories.SelectMany(Ancestors).Distinct().ToList(), itemIds);
        return Task.CompletedTask;
    }

    public Task RemoveFromCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, CancellationToken ct = default)
    {
        var subtree = Subtree(categoryId);
        foreach (var id in itemIds)
        {
            UpdateItem(id, i => i with { CategoryIds = i.CategoryIds.Where(c => !subtree.Contains(c)).DefaultIfEmpty(99).ToList() });
        }

        return Task.CompletedTask;
    }

    public Task AddToCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default)
    {
        foreach (var id in itemIds)
        {
            UpdateItem(id, i => i with { CategoryIds = i.CategoryIds.Append(categoryId).Distinct().ToList() });
        }

        return Task.CompletedTask;
    }

    public Task MoveToCategoryAsync(IReadOnlyList<long> itemIds, long fromCategoryId, long toCategoryId, AddedBy addedBy = AddedBy.AppUi,
        CancellationToken ct = default)
    {
        if (fromCategoryId == toCategoryId)
        {
            return Task.CompletedTask;
        }

        var leaving = Subtree(fromCategoryId);
        foreach (var id in itemIds)
        {
            UpdateItem(id, i =>
            {
                var remaining = i.CategoryIds.Where(c => !leaving.Contains(c) || c == toCategoryId).Append(toCategoryId).Distinct().ToList();
                return i with { CategoryIds = remaining, HomeCategoryId = remaining.Contains(i.HomeCategoryId) ? i.HomeCategoryId : toCategoryId };
            });
        }

        return Task.CompletedTask;
    }

    public Task<bool> VerifyExistsAsync(long itemId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            return Task.FromResult(item is not null && !item.IsMissing && File.Exists(item.FullPath));
        }
    }

    public Task<RelocateOutcome> RelocateItemAsync(long itemId, string newPath, bool acceptHashMismatch, CancellationToken ct = default)
    {
        UpdateItem(itemId, i => i with { FullPath = newPath, IsMissing = false });
        return Task.FromResult(RelocateOutcome.Relocated);
    }

    public Task<string> ReadNoteTextAsync(long itemId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_noteText.TryGetValue(itemId, out var text) ? text : _items.First(i => i.Id == itemId).NotePreview ?? "");
        }
    }

    // ------------------------------------------------------------------ starred files

    public Task SetStarredAsync(IReadOnlyList<long> itemIds, bool starred, CancellationToken ct = default)
    {
        // Like the real library: an existing star keeps its time, and one event names exactly the items that changed.
        var now = DateTimeOffset.Now;
        List<long> changedIds = [];
        HashSet<long> categories = [];
        lock (_lock)
        {
            foreach (var id in itemIds.Distinct())
            {
                var index = _items.FindIndex(i => i.Id == id);
                if (index >= 0 && _items[index].IsStarred != starred)
                {
                    _items[index] = _items[index] with { StarredAt = starred ? now : null };
                    changedIds.Add(id);
                    categories.UnionWith(_items[index].CategoryIds);
                }
            }
        }

        if (changedIds.Count > 0)
        {
            Raise(LibraryChangeKind.Items | LibraryChangeKind.Starred, categories, changedIds);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<LibraryItem>> GetStarredItemsAsync(string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default)
    {
        await Task.Delay(15, ct).ConfigureAwait(false);
        lock (_lock)
        {
            var query = ApplyFilter(ApplySearch(_items.Where(i => i.IsStarred), searchText), filter);
            return query.OrderBy(i => i.StarredAt).ThenBy(i => i.AddedAt).ThenBy(i => i.Id).ToList();
        }
    }

    public Task<StarredSummary> GetStarredSummaryAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var starred = _items.Where(i => i.IsStarred).ToList();
            return Task.FromResult(new StarredSummary(starred.Count, starred.Max(i => i.StarredAt)));
        }
    }

    // ================================================================== AI activity log

    public Task<ActivityEntry> AddActivityAsync(ActivityEntry entry, CancellationToken ct = default) => Task.FromResult(_activity.Add(entry));

    public Task<ActivityPage> GetActivityAsync(ActivityQuery query, CancellationToken ct = default) => Task.FromResult(_activity.Get(query));

    // ------------------------------------------------------------------ tags

    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<TagInfo> tags = _items
                .SelectMany(i => i.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TagInfo(g.Key, g.Count()))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return Task.FromResult(tags);
        }
    }

    public Task SetItemTagsAsync(long itemId, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        UpdateItem(itemId, i => i with { Tags = tags.ToList() }, LibraryChangeKind.Tags);
        return Task.CompletedTask;
    }

    public Task AddTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        foreach (var id in itemIds)
        {
            UpdateItem(id, i => i with { Tags = i.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, LibraryChangeKind.Tags);
        }

        return Task.CompletedTask;
    }

    public Task RemoveTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        foreach (var id in itemIds)
        {
            UpdateItem(id, i => i with { Tags = i.Tags.Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList() }, LibraryChangeKind.Tags);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------ helpers

    private CategorySummary Summary(Category category)
    {
        var subtree = Subtree(category.Id);
        return new CategorySummary
        {
            Category = category,
            ItemCount = _items.Count(i => i.CategoryIds.Any(subtree.Contains)),
            SubcategoryCount = _categories.Count(c => c.ParentId == category.Id),
            UnreadCount = category.Name == "Mountains" ? 2 : 0,
        };
    }

    private HashSet<long> Subtree(long categoryId)
    {
        var result = new HashSet<long> { categoryId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var c in _categories)
            {
                if (c.ParentId is { } parent && result.Contains(parent) && result.Add(c.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    private List<long> Ancestors(long categoryId)
    {
        var result = new List<long>();
        lock (_lock)
        {
            for (var c = _categories.FirstOrDefault(x => x.Id == categoryId); c is not null; c = _categories.FirstOrDefault(x => x.Id == c.ParentId))
            {
                result.Add(c.Id);
            }
        }

        return result;
    }

    private static IEnumerable<LibraryItem> ApplySearch(IEnumerable<LibraryItem> items, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return items;
        }

        var tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tags = tokens.Where(t => t.StartsWith('#') && t.Length > 1).Select(t => t[1..]).ToList();
        var words = tokens.Where(t => !t.StartsWith('#')).ToList();
        return items.Where(i =>
            tags.All(tag => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) &&
            words.All(w => i.FileName.Contains(w, StringComparison.OrdinalIgnoreCase) || (i.NotePreview?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    private static IEnumerable<LibraryItem> ApplyFilter(IEnumerable<LibraryItem> items, ItemFilter? f)
    {
        if (f is null || f.IsEmpty)
        {
            return items;
        }

        return items.Where(i =>
            (f.TypeKeys is null || f.TypeKeys.Count == 0 || f.TypeKeys.Contains(i.TypeKey)) &&
            (f.AddedFrom is null || i.AddedAt >= f.AddedFrom) &&
            (f.AddedTo is null || i.AddedAt <= f.AddedTo) &&
            (f.MinSizeBytes is null || (i.SizeBytes ?? 0) >= f.MinSizeBytes) &&
            (f.MaxSizeBytes is null || (i.SizeBytes ?? 0) <= f.MaxSizeBytes) &&
            (f.Mode is null || i.Mode == f.Mode) &&
            (!f.MissingOnly || i.IsMissing) &&
            (f.Extensions is null || f.Extensions.Count == 0 || f.Extensions.Contains(i.Extension)));
    }

    private void Update(long categoryId, Func<Category, Category> change)
    {
        lock (_lock)
        {
            var index = _categories.FindIndex(c => c.Id == categoryId);
            if (index >= 0)
            {
                _categories[index] = change(_categories[index]);
            }
        }

        Raise(LibraryChangeKind.Categories, [categoryId], []);
    }

    private void UpdateItem(long itemId, Func<LibraryItem, LibraryItem> change, LibraryChangeKind kind = LibraryChangeKind.Items)
    {
        List<long> categories = [];
        lock (_lock)
        {
            var index = _items.FindIndex(i => i.Id == itemId);
            if (index >= 0)
            {
                categories.AddRange(_items[index].CategoryIds);
                _items[index] = change(_items[index]);
                categories.AddRange(_items[index].CategoryIds);
            }
        }

        Raise(kind, categories.SelectMany(Ancestors).Distinct().ToList(), [itemId]);
    }

    private void Raise(LibraryChangeKind kind, IReadOnlyCollection<long> categoryIds, IReadOnlyCollection<long> itemIds)
    {
        var args = new LibraryChangedEventArgs { Kind = kind, CategoryIds = categoryIds, ItemIds = itemIds, Source = AddedBy.AppUi };
        _ = Task.Run(() => Changed?.Invoke(this, args));
    }

    // Stage 2 vault operations are not simulated by this fake.
    public Cabinette.Core.Services.IVaultMonitor CreateVaultMonitor() => throw new NotSupportedException("Not available with sample data.");

    public Task<Cabinette.Core.Models.MoveVaultResult> MoveVaultAsync(string newVaultPath, IProgress<Cabinette.Core.Models.VaultOperationProgress>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not available with sample data.");

    public Task<Cabinette.Core.Models.MigrateResult> MigratePointersAsync(IProgress<Cabinette.Core.Models.VaultOperationProgress>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not available with sample data.");

    public Task<Cabinette.Core.Models.BackupResult> CreateBackupAsync(string zipPath, IProgress<Cabinette.Core.Models.VaultOperationProgress>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not available with sample data.");
}