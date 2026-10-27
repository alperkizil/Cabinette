using System.Text;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Services;

// Vault monitor: applying a reconcile plan (SPEC.md section 8). Planning lives in Vault/VaultReconciler.cs.
internal sealed partial class LibraryService
{
    /// <summary>
    /// Brings the library in line with the vault for the requested scope. Files that need hashing are hashed first without the
    /// write lock; then the plan is computed again under the lock (so it can't interleave with user operations) and applied in
    /// one transaction. Changes are raised through <see cref="Changed"/> with Source = VaultMonitor.
    /// </summary>
    internal async Task<ReconcileOutcome> ReconcileAsync(ReconcileRequest request, CancellationToken ct)
    {
        ThrowIfDisposed();
        var vault = _settings.VaultPath;
        if (vault is null)
        {
            return new ReconcileOutcome { VaultUnavailable = true, Message = "No vault folder has been chosen yet." };
        }

        var hashes = new VaultHashCache();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var draft = ReadNow(connection => VaultReconciler.Build(connection, null, vault, request, hashes, finalPass: false, ct));
            if (draft.Unavailable)
            {
                return new ReconcileOutcome { VaultUnavailable = true, Message = draft.Message };
            }

            if (draft.NeedHash.Count == 0)
            {
                break;
            }

            await HashForReconcileAsync(draft.NeedHash, hashes, request, ct).ConfigureAwait(false);
        }

        return await WriteCoreAsync(ctx =>
        {
            if (!FileOps.PathsEqual(ctx.VaultPath, vault))
            {
                // The vault moved meanwhile; the monitor restarts on the new one.
                return new ReconcileOutcome();
            }

            var plan = VaultReconciler.Build(ctx.Connection, ctx.Transaction, vault, request, hashes, finalPass: true, ct);
            return plan.Unavailable
                ? new ReconcileOutcome { VaultUnavailable = true, Message = plan.Message }
                : ApplyReconcilePlan(ctx, plan, vault);
        }, ct, accumulate: null).ConfigureAwait(false);
    }

    /// <summary>Hashes files that nobody is writing to, retrying with a backoff while another program still has them open.</summary>
    private async Task HashForReconcileAsync(List<FileInfo> files, VaultHashCache hashes, ReconcileRequest request, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var allowWriters = request.AllowWriters?.Contains(file.FullName) == true;
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (hash, size, lastWrite) = FileOps.HashCompleteFile(file.FullName, allowWriters, ct);
                    hashes.Set(file.FullName, size, lastWrite, hash);
                    break;
                }
                catch (Exception ex) when (FileOps.IsSharingViolation(ex) && attempt < request.LockRetryDelays.Count)
                {
                    await Task.Delay(request.LockRetryDelays[attempt], _time, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    // Still locked, gone or unreadable: the final pass puts it off until a later batch.
                    break;
                }
            }
        }
    }

    private ReconcileOutcome ApplyReconcilePlan(WriteContext ctx, ReconcilePlan plan, string vault)
    {
        ctx.Changes.Source = AddedBy.VaultMonitor;
        var summary = new VaultSummaryBuilder();
        var deferredFolders = new HashSet<string>(plan.DeferredFolders, StringComparer.OrdinalIgnoreCase);
        var deferredFiles = new List<string>(plan.DeferredFiles);

        ApplyFolderChanges(ctx, plan, summary);

        // Files that are gone.
        if (plan.Removed.Count > 0)
        {
            var tree = ctx.Tree;
            foreach (var item in plan.Removed)
            {
                ctx.Changes.Item(item.Id, tree, MembershipsOf(ctx, item.Id));
            }

            ctx.Changes.Kind |= LibraryChangeKind.Categories;
            NoteStarsLeaving(ctx, plan.Removed.Select(i => i.Id));
            ctx.Execute("DELETE FROM items WHERE id IN (SELECT value FROM json_each(@ids))",
                new { ids = DbValues.JsonArray(plan.Removed.Select(i => i.Id)) });
            summary.Removed += plan.Removed.Count;
        }

        // Renamed and moved files. Their names are freed first so swaps and chains can't collide.
        if (plan.Moves.Count > 0)
        {
            ctx.Execute("UPDATE items SET file_name = char(1) || id WHERE id IN (SELECT value FROM json_each(@ids))",
                new { ids = DbValues.JsonArray(plan.Moves.Select(m => m.Item.Id)) });
            foreach (var (item, file) in plan.Moves)
            {
                if (!ApplyMove(ctx, item, file, vault, summary))
                {
                    deferredFolders.Add("");
                    deferredFiles.Add(file.Info.FullName);
                }
            }
        }

        // Files marked missing (e.g. looked at while the drive was unplugged) that are at their place again.
        if (plan.FoundInPlace.Count > 0)
        {
            var found = ctx.Query<long>(
                "UPDATE items SET is_missing = 0 WHERE id IN (SELECT value FROM json_each(@ids)) AND is_missing <> 0 RETURNING id",
                new { ids = DbValues.JsonArray(plan.FoundInPlace.Select(i => i.Id)) });
            foreach (var id in found)
            {
                ctx.Changes.Item(id, ctx.Tree, MembershipsOf(ctx, id));
            }
        }

        // Modified files.
        foreach (var (item, file, hash) in plan.Changed)
        {
            var contentChanged = item.Size != file.Length || item.Hash is null || !item.Hash.AsSpan().SequenceEqual(hash);
            ctx.Execute("UPDATE items SET size_bytes = @size, file_modified_at = @modified, hash = @hash WHERE id = @id",
                new { size = file.Length, modified = DbValues.ToText(file.LastWriteTimeUtc), hash, id = item.Id });
            if (!contentChanged)
            {
                // Only the time changed (a restored or touched file): nothing to tell anyone.
                continue;
            }

            summary.Modified++;
            ctx.Changes.Item(item.Id, ctx.Tree, MembershipsOf(ctx, item.Id));
            if ((ItemKind)item.Kind is ItemKind.Note or ItemKind.Link)
            {
                RefreshNoteText(ctx, item, file.FullName);
            }
        }

        // New files.
        var batchId = Guid.NewGuid().ToString("N");
        foreach (var file in plan.NewFiles)
        {
            long homeId;
            var name = file.Info.Name;
            var path = file.Info.FullName;
            if (file.Folder.IsRoot)
            {
                if (!TryFileRootFile(ctx, file, vault, excludeItemId: null, out homeId, out name))
                {
                    deferredFolders.Add("");
                    deferredFiles.Add(path);
                    continue;
                }

                path = Path.Combine(ctx.Tree.FolderPath(vault, homeId), name);
            }
            else
            {
                homeId = file.Folder.CategoryId!.Value;
            }

            // Identical content is kept as its own item (the monitor never deletes files) and reported, unless that item's file is missing.
            var existing = FindByHash(ctx, file.Hash, file.Info.Length);
            var id = InsertItem(ctx, ItemMode.Copy, ItemKind.File, name, null, homeId, file.Info.Length, file.Hash,
                new DateTimeOffset(file.Info.LastWriteTimeUtc, TimeSpan.Zero), AddedBy.VaultMonitor, batchId, null, null);
            AddMembership(ctx, id, homeId, AddedBy.VaultMonitor);
            summary.AddFile(ctx.Tree.Get(homeId).Name);
            if (existing is { Missing: false })
            {
                summary.Duplicates.Add(new DuplicateInfo(path, ItemReader.Load(ctx.Connection, ctx.Transaction, existing.Row.Id, ctx.Tree, vault)!, false));
            }
        }

        // Folders that are gone: their categories go (after their files were removed or moved out above).
        foreach (var missing in plan.MissingRoots)
        {
            if (!RemoveMissingCategory(ctx, missing, summary))
            {
                var row = missing.CategoryId is long id ? ctx.Tree.Find(id) : null;
                deferredFolders.Add(row?.ParentId is long parent ? ctx.Tree.RelativeFolder(parent) : "");
            }
        }

        if (plan.Removed.Count > 0 || plan.MissingRoots.Count > 0)
        {
            DeleteUnusedTags(ctx);
        }

        if (plan.LegacyTempFiles.Count > 0)
        {
            ctx.OnCommitted(() => DeleteLegacyTempFiles(plan.LegacyTempFiles));
        }

        return new ReconcileOutcome
        {
            Summary = summary.Build(),
            DeferredFolders = deferredFolders,
            DeferredFiles = deferredFiles,
        };
    }

    /// <summary>
    /// Deletes the temp files that adds of earlier versions left in category folders when the app quit or crashed mid-copy.
    /// Best effort and silent: a file that is open or can't be deleted stays until the next full rescan.
    /// </summary>
    private static void DeleteLegacyTempFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path) && !FileOps.IsLocked(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                System.Diagnostics.Debug.WriteLine($"Cabinette: could not delete the leftover {path}: {ex.Message}");
            }
        }
    }

    /// <summary>New folders become categories, renamed or moved folders move their category, letter-case changes are recorded.</summary>
    private static void ApplyFolderChanges(WriteContext ctx, ReconcilePlan plan, VaultSummaryBuilder summary)
    {
        // Top-down, so every parent has its id before its children are written.
        var order = new List<PlanFolder>();
        var queue = new Queue<PlanFolder>(plan.Root.Children);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Missing)
            {
                continue;
            }

            order.Add(node);
            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }

        var relocated = order.Where(n => n.Relocated).ToList();
        if (order.All(n => !n.IsNew && !n.Relocated && !n.FolderCaseChanged))
        {
            return;
        }

        var original = ctx.Tree;
        foreach (var node in relocated)
        {
            // The old parents' threads lose these files.
            ctx.Changes.Categories(original, original.SubtreeIds(node.CategoryId!.Value));
        }

        if (relocated.Count > 0)
        {
            ctx.Execute("UPDATE categories SET name = char(1) || id WHERE id IN (SELECT value FROM json_each(@ids))",
                new { ids = DbValues.JsonArray(relocated.Select(n => n.CategoryId!.Value)) });
        }

        var touched = new List<long>();
        foreach (var node in order)
        {
            long? parentId = node.Parent is null || node.Parent.IsRoot ? null : node.Parent.CategoryId;
            if (node.IsNew)
            {
                var name = UniqueSiblingName(ctx, parentId, CategoryNameFromFolder(node.FolderName), excludeId: null);
                node.CategoryId = ctx.Scalar<long>(
                    """
                    INSERT INTO categories(parent_id, name, folder_name, kind, builtin_key, icon_key, icon_png, is_pinned, created_at)
                    VALUES (@parentId, @name, @folderName, @kind, NULL, @iconKey, NULL, 0, @now);
                    SELECT last_insert_rowid();
                    """,
                    new { parentId, name, folderName = node.FolderName, kind = (int)CategoryKind.User, iconKey = ExtensionMap.FolderIconKey, now = ctx.NowText });
                summary.CategoriesCreated.Add(name);
                touched.Add(node.CategoryId.Value);
            }
            else if (node.Relocated)
            {
                var desired = node.FolderRenamed ? CategoryNameFromFolder(node.FolderName) : node.Name ?? node.FolderName;
                var name = UniqueSiblingName(ctx, parentId, desired, node.CategoryId);
                ctx.Execute("UPDATE categories SET parent_id = @parentId, folder_name = @folderName, name = @name WHERE id = @id",
                    new { parentId, folderName = node.FolderName, name, id = node.CategoryId });
                touched.Add(node.CategoryId!.Value);
            }
            else if (node.FolderCaseChanged)
            {
                ctx.Execute("UPDATE categories SET folder_name = @folderName WHERE id = @id", new { folderName = node.FolderName, id = node.CategoryId });
                touched.Add(node.CategoryId!.Value);
            }
        }

        ctx.InvalidateTree();
        var tree = ctx.Tree;
        foreach (var id in touched)
        {
            ctx.Changes.Categories(tree, tree.SubtreeIds(id));
        }

        // Paths of the files below these folders changed.
        ctx.Changes.Kind |= LibraryChangeKind.Items;
    }

    private static string CategoryNameFromFolder(string folderName)
    {
        try
        {
            return NameRules.ValidateCategoryName(folderName);
        }
        catch (LibraryException)
        {
            return "Folder";
        }
    }

    /// <summary>"Name", "Name (1)", ... free among the siblings in the database (case-insensitive).</summary>
    private static string UniqueSiblingName(WriteContext ctx, long? parentId, string desired, long? excludeId)
    {
        var taken = ctx.Query<string>(
                "SELECT name FROM categories WHERE COALESCE(parent_id, 0) = COALESCE(@parentId, 0) AND id <> @exclude",
                new { parentId, exclude = excludeId ?? -1 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NameRules.NumberedNames(desired).First(n => !taken.Contains(n));
    }

    /// <summary>A renamed or moved file keeps its id, tags and other categories; a move changes its home category.</summary>
    private static bool ApplyMove(WriteContext ctx, PlanItem item, PlanNewFile file, string vault, VaultSummaryBuilder summary)
    {
        long newHome;
        var newName = file.Info.Name;
        if (file.Folder.IsRoot)
        {
            // Moved to the top of the vault: filed into its type category like any other file placed there.
            if (!TryFileRootFile(ctx, file, vault, item.Id, out newHome, out newName))
            {
                ctx.Execute("UPDATE items SET file_name = @name WHERE id = @id", new { name = item.FileName, id = item.Id });
                return false;
            }
        }
        else
        {
            newHome = file.Folder.CategoryId!.Value;
        }

        ctx.Execute(
            "UPDATE items SET home_category_id = @newHome, file_name = @newName, extension = @extension, is_missing = 0 WHERE id = @id",
            new { newHome, newName, extension = ExtensionMap.Normalize(Path.GetExtension(newName)), id = item.Id });
        if (!item.SameSizeAndTime(file.Info))
        {
            ctx.Execute("UPDATE items SET size_bytes = @size, file_modified_at = @modified WHERE id = @id",
                new { size = file.Info.Length, modified = DbValues.ToText(file.Info.LastWriteTimeUtc), id = item.Id });
        }

        var memberships = MembershipsOf(ctx, item.Id);
        ctx.Changes.Item(item.Id, ctx.Tree, memberships.Append(item.HomeId).Append(newHome));
        if (newHome != item.HomeId)
        {
            RemoveMemberships(ctx, item.Id, [item.HomeId]);
            AddMembership(ctx, item.Id, newHome, AddedBy.VaultMonitor);
            summary.Moved++;
        }
        else if (!string.Equals(newName, item.FileName, StringComparison.Ordinal))
        {
            summary.Renamed++;
        }

        return true;
    }

    /// <summary>
    /// A file placed directly in the vault folder moves into its type category's folder (unique name), as if it had been dropped
    /// onto the main window. Returns false when the file can't be moved right now (in use).
    /// </summary>
    private static bool TryFileRootFile(WriteContext ctx, PlanNewFile file, string vault, long? excludeItemId, out long homeId, out string name)
    {
        var category = GetOrCreateBuiltIn(ctx, ExtensionMap.GetCategoryKey(file.Info.Extension), null);
        homeId = category.Id;
        var folder = ctx.Tree.FolderPath(vault, homeId);
        name = UniqueVaultFileName(ctx, homeId, file.Info.Name, excludeItemId);
        try
        {
            ctx.EnsureDirectory(folder);
            ctx.MoveFile(file.Info.FullName, Path.Combine(folder, name));
            return true;
        }
        catch (LibraryException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cabinette: could not file {file.Info.FullName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes a category whose folder is gone. Returns false when it has to wait (a file inside is still being decided).</summary>
    private static bool RemoveMissingCategory(WriteContext ctx, PlanFolder missing, VaultSummaryBuilder summary)
    {
        var tree = ctx.Tree;
        if (missing.CategoryId is not long id || tree.Find(id) is not { } row)
        {
            return true;
        }

        // "Deleted" itself stays (its files were handled like any other); its folder comes back when needed.
        if (row.CategoryKind == CategoryKind.System)
        {
            return true;
        }

        var subtree = tree.SubtreeIds(id);
        var subtreeSet = subtree.ToHashSet();
        var subtreeJson = DbValues.JsonArray(subtree);
        if (ctx.Scalar<long>("SELECT COUNT(*) FROM items WHERE mode = 0 AND home_category_id IN (SELECT value FROM json_each(@ids))",
                new { ids = subtreeJson }) > 0)
        {
            return false;
        }

        // Items that are only linked here (pointer files, vault files homed elsewhere) just lose these memberships.
        var memberships = ctx.Query<MembershipRow>(
            """
            SELECT item_id AS ItemId, category_id AS CategoryId, added_at AS AddedAt, added_by AS AddedBy
            FROM item_categories
            WHERE item_id IN (SELECT item_id FROM item_categories WHERE category_id IN (SELECT value FROM json_each(@ids)))
            ORDER BY item_id, added_at, category_id
            """, new { ids = subtreeJson });
        foreach (var group in memberships.GroupBy(m => m.ItemId))
        {
            var item = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, group.Key);
            if (item is null)
            {
                continue;
            }

            var outside = group.Where(m => !subtreeSet.Contains(m.CategoryId)).Select(m => m.CategoryId).ToList();
            ctx.Changes.Item(item.Id, tree, group.Select(m => m.CategoryId));
            RemoveMemberships(ctx, item.Id, subtree);
            if (outside.Count == 0)
            {
                MoveToDeleted(ctx, item);
            }
            else if (subtreeSet.Contains(item.HomeCategoryId))
            {
                Rehome(ctx, item, outside[0]);
            }
        }

        // Pointer items homed here without a membership (should not happen) must not block the delete.
        foreach (var itemId in ctx.Query<long>("SELECT id FROM items WHERE home_category_id IN (SELECT value FROM json_each(@ids))", new { ids = subtreeJson }))
        {
            if (ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId) is { } item)
            {
                var remaining = MembershipsOf(ctx, itemId).Where(c => !subtreeSet.Contains(c)).ToList();
                if (remaining.Count > 0)
                {
                    Rehome(ctx, item, remaining[0]);
                }
                else
                {
                    MoveToDeleted(ctx, item);
                }
            }
        }

        tree = ctx.Tree;
        ctx.Changes.Categories(tree, subtree);
        ctx.Changes.Kind |= LibraryChangeKind.Items;
        summary.CategoriesRemoved.AddRange(subtree.Select(c => tree.Find(c)?.Name).OfType<string>());
        ctx.Execute("DELETE FROM categories WHERE id = @id", new { id });
        ctx.InvalidateTree();
        return true;
    }

    /// <summary>A note or link file was edited outside Cabinette: refresh its preview and searchable text.</summary>
    private static void RefreshNoteText(WriteContext ctx, PlanItem item, string path)
    {
        string text;
        try
        {
            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[NoteSearchTextLength];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            text = new string(buffer, 0, read);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return;
        }

        string preview;
        string searchText;
        if ((ItemKind)item.Kind == ItemKind.Link)
        {
            preview = searchText = ParseUrlFile(text) ?? text;
        }
        else
        {
            preview = Truncate(text, NotePreviewLength);
            searchText = text;
        }

        ctx.Execute("UPDATE items SET note_preview = @preview WHERE id = @id", new { preview, id = item.Id });
        ctx.Execute("UPDATE search_fts SET note_text = @searchText WHERE rowid = @id", new { searchText, id = item.Id });
    }

    private sealed class VaultSummaryBuilder
    {
        public Dictionary<string, int> AddedByCategory { get; } = new(StringComparer.Ordinal);

        public int Removed { get; set; }

        public int Renamed { get; set; }

        public int Moved { get; set; }

        public int Modified { get; set; }

        public List<string> CategoriesCreated { get; } = [];

        public List<string> CategoriesRemoved { get; } = [];

        public List<DuplicateInfo> Duplicates { get; } = [];

        public void AddFile(string categoryName) => AddedByCategory[categoryName] = AddedByCategory.GetValueOrDefault(categoryName) + 1;

        public VaultChangeSummary Build() => new()
        {
            AddedByCategory = AddedByCategory,
            Removed = Removed,
            Renamed = Renamed,
            Moved = Moved,
            Modified = Modified,
            CategoriesCreated = CategoriesCreated,
            CategoriesRemoved = CategoriesRemoved,
            Duplicates = Duplicates,
        };
    }
}
