using System.Globalization;
using System.Text;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Services;

internal sealed partial class LibraryService
{
    /// <summary>One folder level, hidden and system entries included.</summary>
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    // ================================================================ reads

    public Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync(long? parentId, CategorySort sort, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<CategorySummary>>(connection =>
        {
            var tree = CategoryTree.Load(connection);
            if (parentId is long p && tree.Find(p) is null)
            {
                return [];
            }

            var rows = tree.ChildrenOf(parentId);
            var summaries = BuildSummaries(connection, tree, rows)
                .Where(s => s.Category.Kind != CategoryKind.System || s.ItemCount > 0);
            return SortSummaries(summaries, sort);
        }, ct);

    public Task<CategorySummary?> GetCategorySummaryAsync(long categoryId, CancellationToken ct = default)
        => ReadAsync(connection =>
        {
            var tree = CategoryTree.Load(connection);
            var row = tree.Find(categoryId);
            return row is null ? null : BuildSummaries(connection, tree, [row])[0];
        }, ct);

    public Task<Category?> GetCategoryAsync(long categoryId, CancellationToken ct = default)
        => ReadAsync(connection =>
        {
            var tree = CategoryTree.Load(connection);
            var row = tree.Find(categoryId);
            return row is null ? null : row.ToCategory(LoadIcons(connection, [categoryId]).GetValueOrDefault(categoryId));
        }, ct);

    public Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<Category>>(connection =>
        {
            var tree = CategoryTree.Load(connection);
            var icons = LoadIcons(connection, null);
            return tree.All.OrderBy(c => c.Id).Select(c => c.ToCategory(icons.GetValueOrDefault(c.Id))).ToList();
        }, ct);

    public Task<IReadOnlyList<Category>> GetCategoryPathAsync(long categoryId, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<Category>>(connection =>
        {
            var tree = CategoryTree.Load(connection);
            var path = tree.PathTo(categoryId);
            var icons = LoadIcons(connection, path.Select(c => c.Id).ToList());
            return path.Select(c => c.ToCategory(icons.GetValueOrDefault(c.Id))).ToList();
        }, ct);

    private static IReadOnlyList<CategorySummary> SortSummaries(IEnumerable<CategorySummary> summaries, CategorySort sort)
    {
        var byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        var ordered = summaries.OrderByDescending(s => s.Category.IsPinned);
        ordered = sort == CategorySort.LastUpload
            ? ordered.ThenBy(s => LastUploadForSort(s) is null ? 1 : 0)
                     .ThenByDescending(LastUploadForSort)
                     .ThenBy(s => s.Category.Name, byName)
            : ordered.ThenBy(s => s.Category.Name, byName);
        return ordered.ThenBy(s => s.Category.Id).ToList();
    }

    /// <summary>
    /// A category whose files all left sorts like a never-used one, at the bottom. One that still has files sorts by its
    /// newest remaining file (see BuildSummaries), so it falls back when its newest file leaves.
    /// </summary>
    private static DateTimeOffset? LastUploadForSort(CategorySummary summary)
        => summary.ItemCount == 0 ? null : summary.Category.LastUploadAt;

    /// <summary>Summaries (counts, unread, last item, total size) for the given rows, computed in one aggregate query.</summary>
    private static List<CategorySummary> BuildSummaries(SqliteConnection connection, CategoryTree tree, IReadOnlyList<CategoryRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        // Map every category of each row's subtree to the row: (category, row). Ids are integers we own, so inlining is safe.
        var sql = new StringBuilder("WITH m(cat, rowcat) AS (VALUES ");
        var first = true;
        foreach (var row in rows)
        {
            foreach (var cat in tree.SubtreeIds(row.Id))
            {
                sql.Append(first ? "" : ",").Append('(').Append(cat.ToString(CultureInfo.InvariantCulture))
                   .Append(',').Append(row.Id.ToString(CultureInfo.InvariantCulture)).Append(')');
                first = false;
            }
        }

        // Unread: memberships added by Explorer (1), the vault monitor (2), the AI (3) or Clean My Downloads (5) after the
        // membership's own category was last viewed. max(added_at) makes SQLite take ic.item_id from that row (bare column rule).
        // Total size (the thread banner): each item counted once per row, however many categories of the row's subtree it is
        // in; SUM skips unknown (NULL) sizes.
        sql.Append("""
            )
            SELECT s.RowId AS RowId, s.ItemCount AS ItemCount, s.UnreadCount AS UnreadCount, s.LastAt AS LastAt,
                   i.file_name AS LastName, i.kind AS LastKind, COALESCE(z.TotalSize, 0) AS TotalSize
            FROM (
                SELECT m.rowcat AS RowId,
                       COUNT(DISTINCT ic.item_id) AS ItemCount,
                       COUNT(DISTINCT CASE WHEN ic.added_by IN (1, 2, 3, 5)
                                            AND (c.last_viewed_at IS NULL OR ic.added_at > c.last_viewed_at)
                                           THEN ic.item_id END) AS UnreadCount,
                       MAX(ic.added_at) AS LastAt,
                       ic.item_id AS LastItemId
                FROM m
                JOIN categories c ON c.id = m.cat
                JOIN item_categories ic ON ic.category_id = m.cat
                GROUP BY m.rowcat
            ) s
            LEFT JOIN items i ON i.id = s.LastItemId
            LEFT JOIN (
                SELECT d.rowcat AS RowId, SUM(it.size_bytes) AS TotalSize
                FROM (SELECT DISTINCT m.rowcat AS rowcat, ic.item_id AS item_id
                      FROM m
                      JOIN item_categories ic ON ic.category_id = m.cat) d
                JOIN items it ON it.id = d.item_id
                GROUP BY d.rowcat
            ) z ON z.RowId = s.RowId
            """);

        var stats = connection.Query<CategoryStatsRow>(sql.ToString()).ToDictionary(s => s.RowId);
        var icons = LoadIcons(connection, rows.Select(r => r.Id).ToList());

        var result = new List<CategorySummary>(rows.Count);
        foreach (var row in rows)
        {
            stats.TryGetValue(row.Id, out var s);
            result.Add(new CategorySummary
            {
                // The row's time and its place in a list sorted by last upload follow the newest file still in its subtree (when
                // it arrived there; a move or "Add to category" counts as arriving), not the stored time of the last add, which
                // stays when files leave: deleting the newest file must not leave the row on top with older files.
                Category = row.ToCategory(icons.GetValueOrDefault(row.Id)) with { LastUploadAt = DbValues.ParseTimeOrNull(s?.LastAt) },
                ItemCount = (int)(s?.ItemCount ?? 0),
                UnreadCount = (int)(s?.UnreadCount ?? 0),
                SubcategoryCount = tree.ChildrenOf(row.Id).Count,
                LastItemName = s?.LastName,
                LastItemKind = s?.LastKind is long k ? (ItemKind)k : null,
                TotalSizeBytes = s?.TotalSize ?? 0,
            });
        }

        return result;
    }

    /// <summary>Custom icons by category id. <paramref name="ids"/> null = all categories.</summary>
    private static Dictionary<long, byte[]> LoadIcons(SqliteConnection connection, IReadOnlyCollection<long>? ids)
    {
        if (ids is { Count: 0 })
        {
            return [];
        }

        var rows = ids is null
            ? connection.Query<IconRow>("SELECT id AS Id, icon_png AS IconPng FROM categories WHERE icon_png IS NOT NULL")
            : connection.Query<IconRow>(
                "SELECT id AS Id, icon_png AS IconPng FROM categories WHERE icon_png IS NOT NULL AND id IN (SELECT value FROM json_each(@ids))",
                new { ids = DbValues.JsonArray(ids) });
        return rows.Where(r => r.IconPng is not null).ToDictionary(r => r.Id, r => r.IconPng!);
    }

    // ================================================================ writes

    public Task<Category> GetOrCreateBuiltInCategoryAsync(string builtInKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(builtInKey);
        return WriteAsync(ctx =>
        {
            var row = GetOrCreateBuiltIn(ctx, builtInKey, created: null);
            return row.ToCategory(LoadIconInTx(ctx, row.Id));
        }, ct);
    }

    public Task<Category> CreateCategoryAsync(string name, long? parentId, byte[]? iconPng = null, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            var validName = NameRules.ValidateCategoryName(name);
            var tree = ctx.Tree;
            if (parentId is long p)
            {
                EnsureCanHaveSubcategories(tree.Get(p));
            }

            if (tree.ChildrenOf(parentId).Any(c => string.Equals(c.Name, validName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new LibraryException(LibraryErrorCode.NameAlreadyExists, $"A category named \"{validName}\" already exists here.");
            }

            var row = InsertCategory(ctx, validName, parentId, CategoryKind.User, null, ExtensionMap.FolderIconKey, iconPng);
            return row.ToCategory(iconPng);
        }, ct);

    public Task RenameCategoryAsync(long categoryId, string newName, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            var validName = NameRules.ValidateCategoryName(newName);
            var tree = ctx.Tree;
            var row = tree.Get(categoryId);
            if (string.Equals(row.Name, validName, StringComparison.Ordinal))
            {
                return;
            }

            if (tree.ChildrenOf(row.ParentId).Any(c => c.Id != categoryId && string.Equals(c.Name, validName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new LibraryException(LibraryErrorCode.NameAlreadyExists, $"A category named \"{validName}\" already exists here.");
            }

            var desiredFolder = NameRules.SanitizeFolderName(validName);
            var newFolder = string.Equals(desiredFolder, row.FolderName, StringComparison.OrdinalIgnoreCase)
                ? desiredFolder
                : UniqueFolderName(ctx, row.ParentId, desiredFolder, excludeCategoryId: categoryId, ExistingFolders.Avoid);

            string? oldPath = null;
            string? newPath = null;
            if (ctx.VaultPath is { } vault)
            {
                oldPath = tree.FolderPath(vault, categoryId);
                var parentFolder = row.ParentId is long p ? tree.FolderPath(vault, p) : vault;
                newPath = Path.Combine(parentFolder, newFolder);
            }

            ctx.Execute("UPDATE categories SET name = @validName, folder_name = @newFolder WHERE id = @categoryId",
                new { validName, newFolder, categoryId });

            if (oldPath is not null && newPath is not null && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                if (Directory.Exists(oldPath))
                {
                    ctx.MoveDirectory(oldPath, newPath);
                }
                else
                {
                    ctx.EnsureDirectory(newPath);
                }
            }

            // Item paths under this folder changed too.
            ctx.Changes.Categories(tree, tree.SubtreeIds(categoryId));
            ctx.Changes.Kind |= LibraryChangeKind.Items;
            ctx.InvalidateTree();
        }, ct);

    public Task SetCategoryIconAsync(long categoryId, byte[]? iconPng, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            ctx.Tree.Get(categoryId);
            ctx.Execute("UPDATE categories SET icon_png = @iconPng WHERE id = @categoryId", new { iconPng, categoryId });
            ctx.Changes.Category(ctx.Tree, categoryId);
        }, ct);

    public Task SetCategoryPinnedAsync(long categoryId, bool pinned, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            ctx.Tree.Get(categoryId);
            ctx.Execute("UPDATE categories SET is_pinned = @pinned WHERE id = @categoryId", new { pinned = pinned ? 1 : 0, categoryId });
            ctx.Changes.Category(ctx.Tree, categoryId);
        }, ct);

    public Task MarkCategoryViewedAsync(long categoryId, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            var tree = ctx.Tree;
            tree.Get(categoryId);
            var subtree = tree.SubtreeIds(categoryId);
            ctx.Execute("UPDATE categories SET last_viewed_at = @now WHERE id IN (SELECT value FROM json_each(@ids))",
                new { now = ctx.NowText, ids = DbValues.JsonArray(subtree) });

            // Only the unread badges change, so subscribers can skip reloading thread contents and category icons.
            ctx.Changes.Viewed(tree, subtree);
        }, ct);

    public Task DeleteCategoryAsync(long categoryId, CategoryDeletePolicy policy, DeleteItemsOptions? options = null, CancellationToken ct = default)
    {
        if (policy is not (CategoryDeletePolicy.MoveToDeleted or CategoryDeletePolicy.RecycleFiles))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Pass MoveToDeleted or RecycleFiles.");
        }

        return WriteAsync(ctx =>
        {
            var how = ResolveFileDeletion(options);
            if (TakeRefusedDelete(r => r.Kind == RefusedDeleteKind.Category && r.CategoryId == categoryId) is { } refused
                && how.PermanentWhenNoRecycleBin)
            {
                // The confirmed retry after the Recycle Bin refused files of this delete after its commit: the category is gone.
                FinishRefusedCategoryDelete(ctx, refused);
                return;
            }

            var tree = ctx.Tree;
            var row = tree.Get(categoryId);
            if (row.CategoryKind == CategoryKind.System)
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, $"\"{row.Name}\" can't be deleted. You can empty it instead.");
            }

            var subtree = tree.SubtreeIds(categoryId);
            var subtreeSet = subtree.ToHashSet();
            var subtreeJson = DbValues.JsonArray(subtree);
            var rootFolder = ctx.VaultPath is { } vault ? tree.FolderPath(vault, categoryId) : null;
            ctx.Changes.Categories(tree, subtree);

            var memberships = ctx.Query<MembershipRow>(
                """
                SELECT item_id AS ItemId, category_id AS CategoryId, added_at AS AddedAt, added_by AS AddedBy
                FROM item_categories
                WHERE item_id IN (SELECT item_id FROM item_categories WHERE category_id IN (SELECT value FROM json_each(@ids)))
                ORDER BY item_id, added_at, category_id
                """, new { ids = subtreeJson });

            var toDelete = new List<ItemRow>();
            foreach (var group in memberships.GroupBy(m => m.ItemId))
            {
                var item = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, group.Key);
                if (item is null)
                {
                    continue;
                }

                var outside = group.Where(m => !subtreeSet.Contains(m.CategoryId)).Select(m => m.CategoryId).ToList();
                ctx.Changes.Item(item.Id, tree, outside);
                if (outside.Count == 0)
                {
                    if (policy == CategoryDeletePolicy.RecycleFiles)
                    {
                        toDelete.Add(item);
                        continue;
                    }

                    RemoveMemberships(ctx, item.Id, subtree);
                    MoveToDeleted(ctx, item);
                }
                else if (subtreeSet.Contains(item.HomeCategoryId))
                {
                    RemoveMemberships(ctx, item.Id, subtree);
                    Rehome(ctx, item, outside[0]);
                }
            }

            DeleteItemsAfterCommit(ctx, toDelete, how,
                new RefusedDelete(RefusedDeleteKind.Category, RefusedDelete.NoItemIds, categoryId, rootFolder));
            ctx.Execute("DELETE FROM categories WHERE id = @categoryId", new { categoryId });
            ctx.InvalidateTree();
            DeleteUnusedTags(ctx);

            // Runs after the item files were deleted (callbacks run in order).
            if (rootFolder is not null)
            {
                ctx.OnCommitted(() => RemoveCategoryFolder(rootFolder));
            }
        }, ct);
    }

    public Task EmptyDeletedAsync(DeleteItemsOptions? options = null, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            var permanentWhenNoRecycleBin = options?.PermanentWhenNoRecycleBin ?? false;
            if (TakeRefusedDelete(r => r.Kind == RefusedDeleteKind.EmptyDeleted) is { } refused && permanentWhenNoRecycleBin)
            {
                // The confirmed retry after the Recycle Bin refused files of the last Empty Deleted after its commit.
                DeleteRefusedFiles(ctx, refused.Files);
            }

            var tree = ctx.Tree;
            if (tree.DeletedId is not long deletedId)
            {
                return;
            }

            var itemIds = ctx.Query<long>("SELECT item_id FROM item_categories WHERE category_id = @deletedId", new { deletedId });
            var toDelete = new List<ItemRow>();
            foreach (var itemId in itemIds)
            {
                var item = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId);
                if (item is null)
                {
                    continue;
                }

                var others = MembershipsOf(ctx, itemId).Where(c => c != deletedId).ToList();
                ctx.Changes.Item(itemId, tree, [deletedId, .. others]);
                ctx.Changes.Category(tree, deletedId);
                if (others.Count == 0)
                {
                    toDelete.Add(item);
                }
                else
                {
                    // Also filed elsewhere: only leave Deleted.
                    RemoveMemberships(ctx, itemId, [deletedId]);
                    if (item.HomeCategoryId == deletedId)
                    {
                        Rehome(ctx, item, others[0]);
                    }
                }
            }

            // Vault files go to the Recycle Bin; pointer items are only removed from the library.
            DeleteItemsAfterCommit(ctx, toDelete, new FileDeletion(RecycleVaultFiles: true, DeleteRealPointerFiles: false, permanentWhenNoRecycleBin),
                new RefusedDelete(RefusedDeleteKind.EmptyDeleted, RefusedDelete.NoItemIds, CategoryId: null, CategoryFolder: null));
            DeleteUnusedTags(ctx);
        }, ct);

    // ================================================================ helpers

    private static void EnsureCanHaveSubcategories(CategoryRow parent)
    {
        if (parent.CategoryKind == CategoryKind.System)
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, $"\"{parent.Name}\" can't have subcategories.");
        }
    }

    /// <summary>Nothing can be added straight into "Deleted"; it only receives files whose categories were removed (internal paths).</summary>
    private static void EnsureCanAddTo(CategoryRow category)
    {
        if (category.CategoryKind == CategoryKind.System)
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, $"Files can't be added to \"{category.Name}\".");
        }
    }

    private static byte[]? LoadIconInTx(WriteContext ctx, long categoryId)
        => ctx.Scalar<byte[]?>("SELECT icon_png FROM categories WHERE id = @categoryId", new { categoryId });

    /// <summary>
    /// Inserts a category row (folder name unique among siblings) and creates its vault folder. An existing folder of that name is
    /// only adopted when it holds no files of its own (anything but library items and leftovers); otherwise the name is numbered.
    /// <paramref name="existingFolders"/> can make it adopt such a folder anyway (<see cref="ExistingFolders.Adopt"/>).
    /// </summary>
    private static CategoryRow InsertCategory(WriteContext ctx, string name, long? parentId, CategoryKind kind, string? builtInKey,
        string? iconKey, byte[]? iconPng, ExistingFolders existingFolders = ExistingFolders.AdoptWithoutForeignFiles)
    {
        var folderName = UniqueFolderName(ctx, parentId, NameRules.SanitizeFolderName(name), excludeCategoryId: null, existingFolders);
        var id = ctx.Scalar<long>(
            """
            INSERT INTO categories(parent_id, name, folder_name, kind, builtin_key, icon_key, icon_png, is_pinned, created_at)
            VALUES (@parentId, @name, @folderName, @kind, @builtInKey, @iconKey, @iconPng, 0, @now);
            SELECT last_insert_rowid();
            """,
            new { parentId, name, folderName, kind = (int)kind, builtInKey, iconKey, iconPng, now = ctx.NowText });

        var row = new CategoryRow
        {
            Id = id,
            ParentId = parentId,
            Name = name,
            FolderName = folderName,
            Kind = (long)kind,
            BuiltInKey = builtInKey,
            IconKey = iconKey,
            CreatedAt = ctx.NowText,
        };
        ctx.Tree.Add(row);

        if (ctx.VaultPath is { } vault)
        {
            ctx.EnsureDirectory(ctx.Tree.FolderPath(vault, id));
        }

        ctx.Changes.Category(ctx.Tree, id);
        return row;
    }

    /// <summary>What <see cref="UniqueFolderName"/> does with a folder of the candidate name that already exists on disk.</summary>
    private enum ExistingFolders
    {
        /// <summary>Skip the name (a rename must not move one folder onto another).</summary>
        Avoid,

        /// <summary>Use the folder unless it holds files that aren't library items (a new category must not take over the user's files).</summary>
        AdoptWithoutForeignFiles,

        /// <summary>
        /// Use the folder whatever it holds: the subcategories added once to existing libraries (<see cref="LaterBuiltIns"/>) adopt a
        /// folder the user made with their name, as a same-named category takes the role; the rescan then files its files there.
        /// </summary>
        Adopt,
    }

    /// <summary>Unique, case-insensitive sibling folder name ("Name", "Name (1)", ...).</summary>
    private static string UniqueFolderName(WriteContext ctx, long? parentId, string desired, long? excludeCategoryId, ExistingFolders existing)
    {
        var tree = ctx.Tree;
        var taken = tree.ChildrenOf(parentId)
            .Where(c => c.Id != excludeCategoryId)
            .Select(c => c.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? parentFolder = ctx.VaultPath is { } vault ? (parentId is long p ? tree.FolderPath(vault, p) : vault) : null;

        foreach (var candidate in NameRules.NumberedNames(desired))
        {
            if (taken.Contains(candidate))
            {
                continue;
            }

            if (parentFolder is not null)
            {
                var path = Path.Combine(parentFolder, candidate);
                if (File.Exists(path)
                    || (Directory.Exists(path)
                        && (existing == ExistingFolders.Avoid
                            || (existing == ExistingFolders.AdoptWithoutForeignFiles && HoldsForeignFiles(ctx, path)))))
                {
                    continue;
                }
            }

            return candidate;
        }

        throw new InvalidOperationException("Unreachable.");
    }

    /// <summary>
    /// True when the folder (or one below it) holds a file that is neither a pointer item's file nor a leftover (see
    /// <see cref="NameRules.IsFolderLeftover"/>). Folder links, and folders that can't be read, count as foreign too.
    /// No category owns the folder, so no vault file of the library can be inside it.
    /// </summary>
    private static bool HoldsForeignFiles(WriteContext ctx, string folder)
    {
        var pointerFiles = ctx.Query<string>(
                "SELECT pointer_path FROM items WHERE mode = 1 AND pointer_path LIKE @like ESCAPE '\\'",
                new { like = DbValues.LikeStartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return HoldsForeignFiles(new DirectoryInfo(folder), pointerFiles);

        static bool HoldsForeignFiles(DirectoryInfo dir, HashSet<string> libraryFiles)
        {
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", AllEntries))
                {
                    var foreign = entry is DirectoryInfo sub
                        ? (sub.Attributes & FileAttributes.ReparsePoint) != 0 || HoldsForeignFiles(sub, libraryFiles)
                        : !NameRules.IsFolderLeftover(entry.Name) && !libraryFiles.Contains(entry.FullName);
                    if (foreign)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                return true;
            }
        }
    }

    /// <summary>Unique, case-insensitive sibling display name ("Name", "Name (1)", ...).</summary>
    private static string UniqueCategoryName(WriteContext ctx, long? parentId, string desired)
    {
        var taken = ctx.Tree.ChildrenOf(parentId).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NameRules.NumberedNames(desired).First(n => !taken.Contains(n));
    }

    /// <summary>Finds a built-in category by key, re-creating it (and its parent) if it was deleted.</summary>
    private static CategoryRow GetOrCreateBuiltIn(WriteContext ctx, string key, List<long>? created,
        ExistingFolders existingFolders = ExistingFolders.AdoptWithoutForeignFiles)
    {
        var tree = ctx.Tree;
        if (tree.FindBuiltIn(key) is { } existing)
        {
            return existing;
        }

        var def = ExtensionMap.GetDefinition(key)
            ?? throw new LibraryException(LibraryErrorCode.NotFound, $"There is no built-in category \"{key}\".");
        long? parentId = def.ParentKey is null ? null : GetOrCreateBuiltIn(ctx, def.ParentKey, created).Id;
        var kind = key == ExtensionMap.DeletedKey ? CategoryKind.System : CategoryKind.BuiltIn;

        // A user category with the default name takes over the built-in role instead of getting a "(1)" twin.
        var sameName = tree.ChildrenOf(parentId).FirstOrDefault(c =>
            c.BuiltInKey is null && string.Equals(c.Name, def.Name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null)
        {
            ctx.Execute("UPDATE categories SET builtin_key = @key, kind = @kind, icon_key = @icon WHERE id = @id",
                new { key, kind = (int)kind, icon = def.IconKey, id = sameName.Id });
            sameName.BuiltInKey = key;
            sameName.Kind = (long)kind;
            sameName.IconKey = def.IconKey;
            ctx.Changes.Category(tree, sameName.Id);
            return sameName;
        }

        var row = InsertCategory(ctx, UniqueCategoryName(ctx, parentId, def.Name), parentId, kind, key, def.IconKey, null, existingFolders);
        created?.Add(row.Id);
        return row;
    }

    private static long EnsureDeletedCategory(WriteContext ctx) => GetOrCreateBuiltIn(ctx, ExtensionMap.DeletedKey, null).Id;

    /// <summary>Built-in subcategories added after the first release (2026-10-24, SPEC.md Appendix A).</summary>
    internal static readonly IReadOnlyList<string> LaterBuiltIns =
        ["documents.pdf", "documents.word", "executable.msi", "executable.exe", "ebooks.epub", "ebooks.pdf"];

    /// <summary>Settings row recording that <see cref="LaterBuiltIns"/> were added to this library.</summary>
    internal const string LaterBuiltInsSettingKey = "builtins-added-2026-10-24";

    /// <summary>
    /// A new library seeds <see cref="LaterBuiltIns"/> with the rest; an existing one gets them once, at an open (or right after a
    /// restore has pointed it at its new vault). Only under parents that still exist, so a category the user deleted doesn't come
    /// back, and one deleted later isn't added again (only re-created when a file needs it, like every built-in). Files already in
    /// the library stay where they are; a same-named user subcategory or a folder the user made takes the role.
    /// </summary>
    /// <remarks>
    /// Opening never fails because of this, and never touches a vault that isn't there: while the vault folder can't be found (a
    /// drive that isn't connected, a vault renamed in Explorer) or a folder can't be created, nothing is added and the next open
    /// tries again. Re-creating a missing vault would let the startup rescan take the empty folders for the vault and empty the
    /// library; categories without folders would be removed by the rescan once the vault is back.
    /// </remarks>
    internal async Task AddLaterBuiltInsAsync(CancellationToken ct)
    {
        if (_settings.VaultPath is { } vault && !Directory.Exists(vault))
        {
            return;
        }

        try
        {
            await WriteAsync(ctx =>
            {
                if (ctx.Scalar<long>("SELECT COUNT(*) FROM settings WHERE key = @k", new { k = LaterBuiltInsSettingKey }) > 0)
                {
                    return;
                }

                foreach (var key in LaterBuiltIns)
                {
                    if (ExtensionMap.GetDefinition(key)?.ParentKey is { } parentKey && ctx.Tree.FindBuiltIn(parentKey) is not null)
                    {
                        GetOrCreateBuiltIn(ctx, key, created: null, ExistingFolders.Adopt);
                    }
                }

                ctx.Execute("INSERT INTO settings(key, value) VALUES (@k, @v)", new { k = LaterBuiltInsSettingKey, v = ctx.NowText });
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Cabinette: the new built-in subcategories wait for the next open: {ex.Message}");
        }
    }

    /// <summary>
    /// "Delete permanently" after a category delete whose files the Recycle Bin refused after the commit: deletes the remembered
    /// files, then removes the category's folder like the delete itself does (<see cref="RemoveCategoryFolder"/>). The folder is
    /// left alone when it is no longer inside the vault, or when a category's folder is that folder or inside it again.
    /// </summary>
    private static void FinishRefusedCategoryDelete(WriteContext ctx, RefusedDelete refused)
    {
        DeleteRefusedFiles(ctx, refused.Files);
        if (refused.CategoryFolder is not { } folder || ctx.VaultPath is not { } vault
            || !FileOps.IsSameOrInside(folder, vault) || FileOps.PathsEqual(folder, vault))
        {
            return;
        }

        var tree = ctx.Tree;
        if (!tree.All.Any(c => FileOps.IsSameOrInside(tree.FolderPath(vault, c.Id), folder)))
        {
            // Runs after the files were deleted (callbacks run in order).
            ctx.OnCommitted(() => RemoveCategoryFolder(folder));
        }
    }

    /// <summary>
    /// After a category was deleted (and the files of its deleted items are gone): removes its folder tree as far as it is empty.
    /// Files that aren't library items stay where they are, and so do the folders holding them (the vault monitor may then pick
    /// such a folder up as a new category). Only leftovers (see <see cref="NameRules.IsFolderLeftover"/>) go with an otherwise
    /// empty folder.
    /// </summary>
    private static void RemoveCategoryFolder(string folder)
    {
        RemoveIfOnlyLeftovers(new DirectoryInfo(folder));

        static bool RemoveIfOnlyLeftovers(DirectoryInfo dir)
        {
            try
            {
                if (!dir.Exists)
                {
                    return true;
                }

                var entries = dir.EnumerateFileSystemInfos("*", AllEntries).ToList();
                var keep = false;
                foreach (var sub in entries.OfType<DirectoryInfo>())
                {
                    // Folder links are never followed or removed.
                    keep |= (sub.Attributes & FileAttributes.ReparsePoint) != 0 || !RemoveIfOnlyLeftovers(sub);
                }

                var files = entries.OfType<FileInfo>().ToList();
                if (keep || files.Any(f => !NameRules.IsFolderLeftover(f.Name)))
                {
                    return false;
                }

                foreach (var file in files)
                {
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                }

                dir.Delete();
                return true;
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                // E.g. a file another program holds open. A stray folder is harmless.
                System.Diagnostics.Debug.WriteLine($"Cabinette: could not remove folder {dir.FullName}: {ex.Message}");
                return false;
            }
        }
    }
}
