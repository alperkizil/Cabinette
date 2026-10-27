using System.Text;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Search;
using Cabinette.Core.Vault;
using Dapper;

namespace Cabinette.Core.Services;

internal sealed partial class LibraryService
{
    // ================================================================ reads

    public Task<IReadOnlyList<LibraryItem>> GetThreadItemsAsync(long categoryId, string? searchText = null, ItemFilter? filter = null,
        CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<LibraryItem>>(connection =>
        {
            var tree = CategoryTree.Load(connection);
            if (tree.Find(categoryId) is null)
            {
                return [];
            }

            var p = new DynamicParameters();
            var where = ItemQueryBuilder.BuildWhere(tree.SubtreeIds(categoryId), ParsedSearch.Parse(searchText), includeNoteText: true, filter, p);
            var rows = ItemReader.QueryRows(connection, null, $"WHERE {where} ORDER BY i.added_at, i.id", p);
            return ItemReader.Materialize(connection, null, rows, tree, _settings.VaultPath);
        }, ct);

    public Task<LibraryItem?> GetItemAsync(long itemId, CancellationToken ct = default)
        => ReadAsync(connection => ItemReader.Load(connection, null, itemId, CategoryTree.Load(connection), _settings.VaultPath), ct);

    public Task<string> ReadNoteTextAsync(long itemId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            var (row, path) = ReadNow(connection =>
            {
                var r = ItemReader.LoadRow(connection, null, itemId)
                    ?? throw new LibraryException(LibraryErrorCode.NotFound, "The item no longer exists.");
                return (r, ItemReader.ResolvePath(r, CategoryTree.Load(connection), _settings.VaultPath));
            });

            if (row.ItemKind is not (ItemKind.Note or ItemKind.Link))
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, $"\"{row.FileName}\" is not a note.");
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw FileOps.ToLibraryException(ex, path);
            }

            return row.ItemKind == ItemKind.Link ? ParseUrlFile(text) ?? text : text;
        }, ct);
    }

    private static string? ParseUrlFile(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[4..];
            }
        }

        return null;
    }

    // ================================================================ writes

    public Task RenameItemAsync(long itemId, string newFileName, CancellationToken ct = default)
        => WriteAsync(ctx =>
        {
            var item = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId)
                ?? throw new LibraryException(LibraryErrorCode.NotFound, "The item no longer exists.");
            var newName = NameRules.ValidateFileName(newFileName);
            if (string.Equals(item.FileName, newName, StringComparison.Ordinal))
            {
                return;
            }

            var caseOnly = string.Equals(item.FileName, newName, StringComparison.OrdinalIgnoreCase);
            var extension = ExtensionMap.Normalize(Path.GetExtension(newName));
            var clash = new LibraryException(LibraryErrorCode.NameAlreadyExists, $"A file named \"{newName}\" already exists in this folder.");

            if (item.ItemMode == ItemMode.Copy)
            {
                var vault = RequireVault();
                var folder = ctx.Tree.FolderPath(vault, item.HomeCategoryId);
                var oldPath = Path.Combine(folder, item.FileName);
                var newPath = Path.Combine(folder, newName);
                if (!caseOnly)
                {
                    var takenInDb = ctx.Scalar<long>(
                        "SELECT COUNT(*) FROM items WHERE mode = 0 AND home_category_id = @home AND file_name = @newName COLLATE NOCASE AND id <> @itemId",
                        new { home = item.HomeCategoryId, newName, itemId }) > 0;
                    if (takenInDb || File.Exists(newPath) || Directory.Exists(newPath))
                    {
                        throw clash;
                    }
                }

                ctx.Execute("UPDATE items SET file_name = @newName, extension = @extension WHERE id = @itemId",
                    new { newName, extension, itemId });
                ctx.MoveFile(oldPath, newPath);
            }
            else
            {
                var oldPath = item.PointerPath ?? throw new LibraryException(LibraryErrorCode.NotFound, "The item has no file.");
                var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", newName);
                if (!caseOnly)
                {
                    var takenInDb = ctx.Scalar<long>(
                        "SELECT COUNT(*) FROM items WHERE mode = 1 AND pointer_path = @newPath COLLATE NOCASE AND id <> @itemId",
                        new { newPath, itemId }) > 0;
                    if (takenInDb || File.Exists(newPath) || Directory.Exists(newPath))
                    {
                        throw clash;
                    }
                }

                ctx.Execute(
                    "UPDATE items SET file_name = @newName, extension = @extension, pointer_path = @newPath, is_missing = 0 WHERE id = @itemId",
                    new { newName, extension, newPath, itemId });
                ctx.MoveFile(oldPath, newPath);
            }

            ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, itemId));
            ctx.Changes.Kind |= LibraryChangeKind.Categories; // chat-list preview may show the old name
        }, ct);

    public Task DeleteItemsAsync(IReadOnlyList<long> itemIds, DeleteItemsOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return WriteAsync(ctx =>
        {
            var how = ResolveFileDeletion(options);
            var ordered = itemIds.Distinct().ToList();
            var ids = ordered.ToHashSet();

            // The confirmed retry of a delete the Recycle Bin refused after its commit: those rows are gone, and their ids may
            // belong to new items by now, so remembered ids are not looked up again. Their refused files are deleted instead.
            // Only a delete of exactly those ids takes the remembered refusal; any other delete leaves it for its retry.
            var remembered = TakeRefusedDelete(r => r.Kind == RefusedDeleteKind.Items && r.ItemIds.SetEquals(ids));
            var retried = remembered is not null && how.PermanentWhenNoRecycleBin ? remembered : null;
            if (retried is not null)
            {
                DeleteRefusedFiles(ctx, retried.Files.Where(f => ids.Contains(f.ItemId)));
            }

            var items = ordered
                .Where(id => retried is null || !retried.ItemIds.Contains(id))
                .Select(id => ItemReader.LoadRow(ctx.Connection, ctx.Transaction, id))
                .OfType<ItemRow>()
                .ToList();
            DeleteItemsAfterCommit(ctx, items, how, new RefusedDelete(RefusedDeleteKind.Items, ids, CategoryId: null, CategoryFolder: null));
            DeleteUnusedTags(ctx);
        }, ct);
    }

    public Task RemoveFromCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return WriteAsync(ctx =>
        {
            var tree = ctx.Tree;
            tree.Get(categoryId);
            var subtree = tree.SubtreeIds(categoryId).ToHashSet();
            var deletedId = tree.DeletedId;
            foreach (var itemId in itemIds.Distinct())
            {
                var item = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId);
                if (item is null)
                {
                    continue;
                }

                var memberships = MembershipsOf(ctx, itemId);
                var toRemove = memberships.Where(subtree.Contains).ToList();
                if (toRemove.Count == 0)
                {
                    continue;
                }

                // Removing an item from "Deleted" when it has nowhere else to go keeps it in Deleted.
                if (toRemove.Count == memberships.Count && deletedId is long d && toRemove.Contains(d))
                {
                    toRemove.Remove(d);
                }

                ctx.Changes.Item(itemId, tree, memberships);
                ctx.Changes.Categories(tree, toRemove);
                RemoveMemberships(ctx, itemId, toRemove);
                AfterMembershipsRemoved(ctx, item);
            }
        }, ct);
    }

    public Task AddToCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return WriteAsync(ctx =>
        {
            EnsureCanAddTo(ctx.Tree.Get(categoryId));
            ctx.Changes.Source = addedBy;
            foreach (var itemId in itemIds.Distinct())
            {
                if (ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId) is not null)
                {
                    AddMembership(ctx, itemId, categoryId, addedBy);
                }
            }
        }, ct);
    }

    public Task<bool> VerifyExistsAsync(long itemId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            var (row, path) = ReadNow(connection =>
            {
                var r = ItemReader.LoadRow(connection, null, itemId)
                    ?? throw new LibraryException(LibraryErrorCode.NotFound, "The item no longer exists.");
                return (r, ItemReader.ResolvePath(r, CategoryTree.Load(connection), _settings.VaultPath));
            });

            var exists = path.Length > 0 && File.Exists(path);
            if (exists == (row.IsMissing == 0))
            {
                return exists;
            }

            await WriteCoreAsync(ctx =>
            {
                var changed = ctx.Execute("UPDATE items SET is_missing = @missing WHERE id = @itemId AND is_missing <> @missing",
                    new { missing = exists ? 0 : 1, itemId });
                if (changed > 0)
                {
                    ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, itemId));
                }

                return true;
            }, ct, accumulate: null).ConfigureAwait(false);
            return exists;
        }, ct);
    }

    public Task<RelocateOutcome> RelocateItemAsync(long itemId, string newPath, bool acceptHashMismatch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            var row = ReadNow(connection => ItemReader.LoadRow(connection, null, itemId))
                ?? throw new LibraryException(LibraryErrorCode.NotFound, "The item no longer exists.");
            if (row.ItemMode != ItemMode.Pointer)
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, "Only linked (pointer) files can be located.");
            }

            string full;
            try
            {
                full = Path.GetFullPath(newPath);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                return RelocateOutcome.NotFound;
            }

            if (!File.Exists(full))
            {
                return RelocateOutcome.NotFound;
            }

            byte[] hash;
            FileInfo info;
            try
            {
                info = new FileInfo(full);
                hash = await FileOps.HashFileAsync(full, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw FileOps.ToLibraryException(ex, full);
            }

            if (row.Hash is not null && !row.Hash.AsSpan().SequenceEqual(hash) && !acceptHashMismatch)
            {
                return RelocateOutcome.HashMismatch;
            }

            await WriteCoreAsync(ctx =>
            {
                var current = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId)
                    ?? throw new LibraryException(LibraryErrorCode.NotFound, "The item no longer exists.");
                var otherHasPath = ctx.Scalar<long>(
                    "SELECT COUNT(*) FROM items WHERE mode = 1 AND pointer_path = @full COLLATE NOCASE AND id <> @itemId",
                    new { full, itemId }) > 0;
                if (otherHasPath)
                {
                    throw new LibraryException(LibraryErrorCode.NameAlreadyExists, $"\"{info.Name}\" is already in Cabinette.");
                }

                var fileName = info.Name;
                ctx.Execute(
                    """
                    UPDATE items SET pointer_path = @full, file_name = @fileName, extension = @extension, size_bytes = @size,
                                     file_modified_at = @modified, hash = @hash, is_missing = 0
                    WHERE id = @itemId
                    """,
                    new
                    {
                        full,
                        fileName,
                        extension = ExtensionMap.Normalize(info.Extension),
                        size = info.Length,
                        modified = DbValues.ToText(info.LastWriteTimeUtc),
                        hash,
                        itemId,
                    });
                ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, current.Id));
                return true;
            }, ct, accumulate: null).ConfigureAwait(false);
            return RelocateOutcome.Relocated;
        }, ct);
    }

    // ================================================================ membership helpers (inside a write)

    /// <summary>Category ids of the item's memberships, oldest first.</summary>
    private static List<long> MembershipsOf(WriteContext ctx, long itemId)
        => ctx.Query<long>("SELECT category_id FROM item_categories WHERE item_id = @itemId ORDER BY added_at, category_id", new { itemId });

    private static void RemoveMemberships(WriteContext ctx, long itemId, IReadOnlyCollection<long> categoryIds)
    {
        if (categoryIds.Count == 0)
        {
            return;
        }

        ctx.Execute("DELETE FROM item_categories WHERE item_id = @itemId AND category_id IN (SELECT value FROM json_each(@ids))",
            new { itemId, ids = DbValues.JsonArray(categoryIds) });
    }

    /// <summary>
    /// Adds a membership. Returns false when the item already was in the category.
    /// An item in "Deleted" that gets a real category leaves Deleted (and its file moves out of the Deleted folder).
    /// </summary>
    private static bool AddMembership(WriteContext ctx, long itemId, long categoryId, AddedBy addedBy)
    {
        var tree = ctx.Tree;
        tree.Get(categoryId);
        var inserted = ctx.Execute(
            "INSERT OR IGNORE INTO item_categories(item_id, category_id, added_at, added_by) VALUES (@itemId, @categoryId, @now, @by)",
            new { itemId, categoryId, now = ctx.NowText, by = (int)addedBy }) > 0;
        if (!inserted)
        {
            return false;
        }

        TouchLastUpload(ctx, categoryId);
        ctx.Changes.Item(itemId);
        ctx.Changes.Category(tree, categoryId);

        if (tree.DeletedId is long deletedId && categoryId != deletedId)
        {
            var leftDeleted = ctx.Execute("DELETE FROM item_categories WHERE item_id = @itemId AND category_id = @deletedId",
                new { itemId, deletedId }) > 0;
            if (leftDeleted)
            {
                ctx.Changes.Category(tree, deletedId);
                var item = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId);
                if (item is not null && item.HomeCategoryId == deletedId)
                {
                    // Taking a vault file out of Deleted moves it into its new home folder. If it is gone from the Deleted
                    // folder, refuse (as "Move to category…" does) instead of leaving an item with no file behind it.
                    if (item.ItemMode == ItemMode.Copy && ctx.VaultPath is { } vault
                        && !File.Exists(Path.Combine(tree.FolderPath(vault, deletedId), item.FileName)))
                    {
                        throw new LibraryException(LibraryErrorCode.NotFound,
                            $"\"{item.FileName}\" could not be found. It may have been moved or deleted.");
                    }

                    Rehome(ctx, item, categoryId);
                }
            }
        }

        return true;
    }

    /// <summary>Sets last_upload_at = now on the category and all its ancestors.</summary>
    private static void TouchLastUpload(WriteContext ctx, long categoryId)
    {
        var ids = new List<long> { categoryId };
        ids.AddRange(ctx.Tree.AncestorIds(categoryId));
        ctx.Execute(
            """
            UPDATE categories SET last_upload_at = @now
            WHERE id IN (SELECT value FROM json_each(@ids)) AND (last_upload_at IS NULL OR last_upload_at < @now)
            """,
            new { now = ctx.NowText, ids = DbValues.JsonArray(ids) });
        foreach (var id in ids)
        {
            if (ctx.Tree.Find(id) is { } row && (row.LastUploadAt is null || string.CompareOrdinal(row.LastUploadAt, ctx.NowText) < 0))
            {
                row.LastUploadAt = ctx.NowText;
            }
        }
    }

    /// <summary>After memberships were removed: no category left → Deleted; home removed → move to the oldest remaining category.</summary>
    private static void AfterMembershipsRemoved(WriteContext ctx, ItemRow item)
    {
        var remaining = MembershipsOf(ctx, item.Id);
        if (remaining.Count == 0)
        {
            MoveToDeleted(ctx, item);
        }
        else if (!remaining.Contains(item.HomeCategoryId))
        {
            Rehome(ctx, item, remaining[0]);
        }
    }

    private static void MoveToDeleted(WriteContext ctx, ItemRow item)
    {
        var deletedId = EnsureDeletedCategory(ctx);
        ctx.Execute(
            "INSERT OR IGNORE INTO item_categories(item_id, category_id, added_at, added_by) VALUES (@itemId, @deletedId, @now, @by)",
            new { itemId = item.Id, deletedId, now = ctx.NowText, by = (int)AddedBy.AppUi });
        TouchLastUpload(ctx, deletedId);
        ctx.Changes.Item(item.Id);
        ctx.Changes.Category(ctx.Tree, deletedId);
        Rehome(ctx, item, deletedId);
    }

    /// <summary>Changes the home category. Copy-mode files move physically into the new folder (with a unique name).</summary>
    private static void Rehome(WriteContext ctx, ItemRow item, long newHomeId)
    {
        if (item.HomeCategoryId == newHomeId)
        {
            return;
        }

        var tree = ctx.Tree;
        var fileName = item.FileName;
        if (item.ItemMode == ItemMode.Copy && ctx.VaultPath is { } vault)
        {
            var source = Path.Combine(tree.FolderPath(vault, item.HomeCategoryId), item.FileName);
            var targetFolder = tree.FolderPath(vault, newHomeId);
            fileName = UniqueVaultFileName(ctx, newHomeId, item.FileName, excludeItemId: item.Id);
            if (File.Exists(source))
            {
                ctx.EnsureDirectory(targetFolder);
                ctx.MoveFile(source, Path.Combine(targetFolder, fileName));
            }
        }

        ctx.Execute("UPDATE items SET home_category_id = @newHomeId, file_name = @fileName WHERE id = @id",
            new { newHomeId, fileName, id = item.Id });
        ctx.Changes.Item(item.Id, tree, [item.HomeCategoryId, newHomeId]);
        item.HomeCategoryId = newHomeId;
        item.FileName = fileName;
    }

    /// <summary>A file name that is free in the category's vault folder, both in the library and on disk.</summary>
    private static string UniqueVaultFileName(WriteContext ctx, long categoryId, string desired, long? excludeItemId)
    {
        var folder = ctx.VaultPath is { } vault ? ctx.Tree.FolderPath(vault, categoryId) : null;
        foreach (var candidate in NameRules.NumberedFileNames(desired))
        {
            var takenInDb = ctx.Scalar<long>(
                "SELECT COUNT(*) FROM items WHERE mode = 0 AND home_category_id = @categoryId AND file_name = @candidate COLLATE NOCASE AND id <> @exclude",
                new { categoryId, candidate, exclude = excludeItemId ?? -1 }) > 0;
            if (takenInDb)
            {
                continue;
            }

            if (folder is not null)
            {
                var path = Path.Combine(folder, candidate);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    continue;
                }
            }

            return candidate;
        }

        throw new InvalidOperationException("Unreachable.");
    }

    /// <summary>
    /// A pointer file whose size or modification time changed since it was hashed is hashed again inside the duplicate check up to
    /// this size; a larger one is hashed again in the background. Tests lower it.
    /// </summary>
    internal long InlineRehashLimitBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>An existing item with the same content. <see cref="Missing"/>: its file is gone, so an add re-links it instead.</summary>
    private sealed record ContentMatch(ItemRow Row, bool Missing);

    /// <summary>What the duplicate check found out about a candidate's file.</summary>
    private enum CandidateFile
    {
        /// <summary>There, with the same content.</summary>
        Present,

        /// <summary>Gone: the candidate can be found again.</summary>
        Gone,

        /// <summary>Not a match: its content changed, it can't be read right now, or it can't be checked (vault unreachable).</summary>
        NoMatch,
    }

    /// <summary>
    /// Existing item with the same content, or null. One whose file is there wins over one whose file is missing.
    /// Empty files are never treated as duplicates. Each candidate's file is checked on disk (see <see cref="CheckVaultFile"/> and
    /// <see cref="CheckPointerFile"/>), and a candidate whose file is there has its "missing" mark cleared in this write.
    /// </summary>
    private ContentMatch? FindByHash(WriteContext ctx, byte[]? hash, long size, long? excludeItemId = null)
    {
        if (hash is null || size == 0)
        {
            return null;
        }

        var rows = ItemReader.QueryRows(ctx.Connection, ctx.Transaction,
            "WHERE i.hash = @hash AND i.id <> @exclude AND COALESCE(i.size_bytes, -1) <> 0 ORDER BY i.is_missing, i.id",
            new { hash, exclude = excludeItemId ?? -1 });
        bool? vaultReachable = null;
        ContentMatch? missing = null;
        foreach (var row in rows)
        {
            var file = row.ItemMode == ItemMode.Pointer
                ? CheckPointerFile(ctx, row, hash)
                : CheckVaultFile(ctx, row, vaultReachable ??= ctx.VaultPath is { } vault && Directory.Exists(vault));
            if (file == CandidateFile.Present)
            {
                if (row.IsMissing != 0)
                {
                    // Marked missing, but its file is there again.
                    ctx.Execute("UPDATE items SET is_missing = 0 WHERE id = @id", new { id = row.Id });
                    ctx.Changes.Item(row.Id, ctx.Tree, MembershipsOf(ctx, row.Id));
                    row.IsMissing = 0;
                }

                return new ContentMatch(row, Missing: false);
            }

            if (file == CandidateFile.Gone)
            {
                missing ??= new ContentMatch(row, Missing: true);
            }
        }

        return missing;
    }

    /// <summary>
    /// A vault file is there when its file is at its place. The vault monitor can't be relied on for this: it may be paused, or
    /// the file may have gone while Cabinette wasn't looking. While the vault folder itself can't be reached (an unplugged drive)
    /// every file would look gone, so the "missing" mark is trusted instead, and an item marked missing is left out: nothing is
    /// re-linked to a file outside the vault because a drive is unplugged.
    /// </summary>
    private static CandidateFile CheckVaultFile(WriteContext ctx, ItemRow row, bool vaultReachable)
    {
        if (!vaultReachable)
        {
            return row.IsMissing == 0 ? CandidateFile.Present : CandidateFile.NoMatch;
        }

        return File.Exists(ItemReader.ResolvePath(row, ctx.Tree, ctx.VaultPath)) ? CandidateFile.Present : CandidateFile.Gone;
    }

    /// <summary>
    /// Pointer files are never watched, so a pointer counts as there only while its file still has the size and modification time
    /// stored with its hash. A changed file of up to <see cref="InlineRehashLimitBytes"/> is hashed again right away and matches
    /// only when its new content still does; a larger one is no match and is hashed again in the background after the commit.
    /// Both store the file's new size and time. A file that can't be read right now is no match.
    /// </summary>
    private CandidateFile CheckPointerFile(WriteContext ctx, ItemRow row, byte[] hash)
    {
        var path = row.PointerPath;
        FileInfo info;
        try
        {
            if (string.IsNullOrEmpty(path) || !(info = new FileInfo(path)).Exists)
            {
                return CandidateFile.Gone;
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return CandidateFile.Gone;
        }

        if (row.SizeBytes == info.Length && row.FileModifiedAt is { } stored && DbValues.ParseTime(stored).UtcTicks == info.LastWriteTimeUtc.Ticks)
        {
            return CandidateFile.Present;
        }

        var tree = ctx.Tree;
        if (info.Length > InlineRehashLimitBytes)
        {
            var itemId = row.Id;
            ctx.Execute("UPDATE items SET size_bytes = @size, file_modified_at = @modified, hash = NULL WHERE id = @itemId",
                new { size = info.Length, modified = DbValues.ToText(info.LastWriteTimeUtc), itemId });
            ctx.Changes.Item(itemId, tree, MembershipsOf(ctx, itemId));
            ctx.OnCommitted(() => _hashQueue.Enqueue(itemId));
            return CandidateFile.NoMatch;
        }

        byte[] current;
        long currentSize;
        DateTime lastWrite;
        try
        {
            (current, currentSize, lastWrite) = FileOps.HashCompleteFile(path, allowWriters: true, CancellationToken.None);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return CandidateFile.NoMatch;
        }

        ctx.Execute("UPDATE items SET size_bytes = @size, file_modified_at = @modified, hash = @current WHERE id = @id",
            new { size = currentSize, modified = DbValues.ToText(lastWrite), current, id = row.Id });
        ctx.Changes.Item(row.Id, tree, MembershipsOf(ctx, row.Id));
        row.SizeBytes = currentSize;
        row.FileModifiedAt = DbValues.ToText(lastWrite);
        row.Hash = current;
        return current.AsSpan().SequenceEqual(hash) ? CandidateFile.Present : CandidateFile.NoMatch;
    }

    /// <summary>
    /// "Found again" in copy mode: a missing item takes the file being added (<paramref name="tempPath"/>, already in the vault)
    /// and becomes a copy-mode item, keeping its id, tags and categories. Like migrating a pointer, the file goes into the item's
    /// home folder.
    /// </summary>
    private static void RelinkAsCopy(WriteContext ctx, ItemRow item, string vault, string tempPath, string fileName, string sourcePath,
        long size, byte[] hash, DateTime lastWriteUtc)
    {
        var folder = ctx.Tree.FolderPath(vault, item.HomeCategoryId);
        ctx.EnsureDirectory(folder);
        var finalName = UniqueVaultFileName(ctx, item.HomeCategoryId, fileName, excludeItemId: item.Id);
        var finalPath = Path.Combine(folder, finalName);
        ctx.MoveFile(tempPath, finalPath);
        FileOps.MakeNormal(finalPath);
        ctx.Execute(
            """
            UPDATE items SET mode = 0, pointer_path = NULL, original_path = @sourcePath, file_name = @finalName, extension = @extension,
                             size_bytes = @size, hash = @hash, file_modified_at = @modified, is_missing = 0
            WHERE id = @id
            """,
            new
            {
                sourcePath,
                finalName,
                extension = ExtensionMap.Normalize(Path.GetExtension(finalName)),
                size,
                hash,
                modified = DbValues.ToText(lastWriteUtc),
                id = item.Id,
            });
    }

    /// <summary>"Found again" in pointer mode: a missing item points at <paramref name="path"/> now, keeping its id, tags and categories.</summary>
    private static void RelinkAsPointer(WriteContext ctx, ItemRow item, string path, long? size, byte[]? hash, string? modifiedAt)
    {
        var fileName = Path.GetFileName(path);
        ctx.Execute(
            """
            UPDATE items SET mode = 1, pointer_path = @path, original_path = NULL, file_name = @fileName, extension = @extension,
                             size_bytes = @size, hash = @hash, file_modified_at = @modifiedAt, is_missing = 0
            WHERE id = @id
            """,
            new
            {
                path,
                fileName,
                extension = ExtensionMap.Normalize(Path.GetExtension(fileName)),
                size,
                hash,
                modifiedAt,
                id = item.Id,
            });
    }

    private static long InsertItem(WriteContext ctx, ItemMode mode, ItemKind kind, string fileName, string? pointerPath, long homeCategoryId,
        long? size, byte[]? hash, DateTimeOffset? modifiedAt, AddedBy addedBy, string? batchId, string? originalPath, string? notePreview)
        => ctx.Scalar<long>(
            """
            INSERT INTO items(mode, kind, file_name, extension, pointer_path, home_category_id, size_bytes, hash, file_modified_at,
                              added_at, added_by, batch_id, original_path, is_missing, note_preview)
            VALUES (@mode, @kind, @fileName, @extension, @pointerPath, @homeCategoryId, @size, @hash, @modified,
                    @now, @addedBy, @batchId, @originalPath, 0, @notePreview);
            SELECT last_insert_rowid();
            """,
            new
            {
                mode = (int)mode,
                kind = (int)kind,
                fileName,
                extension = ExtensionMap.Normalize(Path.GetExtension(fileName)),
                pointerPath,
                homeCategoryId,
                size,
                hash,
                modified = DbValues.ToText(modifiedAt),
                now = ctx.NowText,
                addedBy = (int)addedBy,
                batchId,
                originalPath,
                notePreview,
            });

    private static void DeleteUnusedTags(WriteContext ctx)
        => ctx.Execute("DELETE FROM tags WHERE id NOT IN (SELECT DISTINCT tag_id FROM item_tags)");

    /// <summary>How the files of deleted items go: from the call's options, or the item-delete settings.</summary>
    private readonly record struct FileDeletion(bool RecycleVaultFiles, bool DeleteRealPointerFiles, bool PermanentWhenNoRecycleBin);

    private FileDeletion ResolveFileDeletion(DeleteItemsOptions? options)
    {
        var settings = _settings;
        return new FileDeletion(
            options?.RecycleVaultFiles ?? settings.RecycleVaultFiles,
            options?.DeleteRealPointerFiles ?? settings.DeleteRealPointerFiles,
            options?.PermanentWhenNoRecycleBin ?? false);
    }

    /// <summary>
    /// Deletes the item rows now and their files right after the commit (still inside the write lock, so the vault monitor can't
    /// pick a file up in between): vault files to the Recycle Bin or permanently, pointer files to the Recycle Bin when requested.
    /// A locked file, or a file that can't go to the Recycle Bin (its drive has none, or it's too large for it), makes the whole
    /// write fail before anything changes, unless deleting it permanently is allowed. A file that still can't be deleted after
    /// the commit stays on disk and is reported as an error. When the Recycle Bin refused it, it is remembered with
    /// <paramref name="operation"/>, so that the confirmed retry deletes exactly that file (see <see cref="RefusedDelete"/>);
    /// without a retry a vault file is picked up again by the vault monitor.
    /// </summary>
    private void DeleteItemsAfterCommit(WriteContext ctx, IReadOnlyList<ItemRow> items, FileDeletion how, RefusedDelete operation)
    {
        if (items.Count == 0)
        {
            return;
        }

        var tree = ctx.Tree;
        var recycle = new List<string>();
        var permanent = new List<string>();
        var owners = new Dictionary<string, RefusedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var path = ItemReader.ResolvePath(item, tree, ctx.VaultPath);
            if (path.Length == 0 || !File.Exists(path))
            {
                continue;
            }

            if (item.ItemMode == ItemMode.Copy)
            {
                (how.RecycleVaultFiles ? recycle : permanent).Add(path);
            }
            else if (how.DeleteRealPointerFiles)
            {
                recycle.Add(path);
            }

            owners.TryAdd(PathKey(path), new RefusedFile(item.Id, path));
        }

        if (recycle.Concat(permanent).FirstOrDefault(FileOps.IsLocked) is { } locked)
        {
            throw InUse(locked, inner: null);
        }

        var noRecycleBin = FilesWithoutRecycleBin(recycle);
        if (noRecycleBin.Count > 0)
        {
            if (!how.PermanentWhenNoRecycleBin)
            {
                throw NoRecycleBin(noRecycleBin, inner: null);
            }

            recycle.RemoveAll(noRecycleBin.Contains);
            permanent.AddRange(noRecycleBin);
        }

        foreach (var item in items)
        {
            ctx.Changes.Item(item.Id, tree, MembershipsOf(ctx, item.Id));
        }

        ctx.Changes.Kind |= LibraryChangeKind.Categories;
        NoteStarsLeaving(ctx, items.Select(i => i.Id));
        ctx.Execute("DELETE FROM items WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = DbValues.JsonArray(items.Select(i => i.Id)) });

        if (recycle.Count > 0 || permanent.Count > 0)
        {
            ctx.OnCommitted(() =>
            {
                var error = DeleteFiles(recycle, permanent, how.PermanentWhenNoRecycleBin, out var refused);
                var files = refused.Select(p => owners.GetValueOrDefault(PathKey(p))).OfType<RefusedFile>().ToList();
                if (files.Count > 0)
                {
                    RememberRefusedDelete(operation with { Files = files });
                }

                if (error is not null)
                {
                    throw error;
                }
            });
        }
    }

    /// <summary>
    /// The files that can't go to the Recycle Bin: their drive has none, or they are too large for it. A best-effort check, asked
    /// for every file (the size limit is per file; the Recycle Bin implementation caches what it reads per drive).
    /// </summary>
    private HashSet<string> FilesWithoutRecycleBin(IReadOnlyList<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            bool canRecycle;
            try
            {
                canRecycle = _recycleBin.CanRecycle(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Only a pre-check: sending to the Recycle Bin reports what really fails.
                System.Diagnostics.Debug.WriteLine($"Cabinette: the Recycle Bin check failed for {path}: {ex.Message}");
                canRecycle = true;
            }

            if (!canRecycle)
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>
    /// Deletes files after a commit. Returns an error describing the first one that is still there, or null.
    /// <paramref name="refused"/> lists the files the Recycle Bin refused and that were left in place because deleting them
    /// permanently wasn't allowed.
    /// </summary>
    private LibraryException? DeleteFiles(IReadOnlyList<string> recycle, IReadOnlyList<string> permanent, bool permanentWhenNoRecycleBin,
        out IReadOnlyList<string> refused)
    {
        LibraryException? error = null;
        refused = [];
        var deletePermanently = permanent.ToList();
        if (recycle.Count > 0)
        {
            try
            {
                _recycleBin.SendToRecycleBin(recycle);
            }
            catch (RecycleBinUnavailableException ex)
            {
                // These could only have been deleted permanently (no Recycle Bin, or too large for it), so they were left in place.
                var left = ex.Paths.Where(File.Exists).ToList();
                if (permanentWhenNoRecycleBin)
                {
                    deletePermanently.AddRange(left);
                }
                else if (left.Count > 0)
                {
                    refused = left;
                    error = NoRecycleBin(left, ex);
                }

                var skipped = ex.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                error ??= RecycleFailure(recycle.Where(p => !skipped.Contains(p)), ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The contract says IOException, but a shell implementation may surface COM/Win32 errors too.
                error = RecycleFailure(recycle, ex);
            }
        }

        var permanentError = DeletePermanently(deletePermanently);
        return error ?? permanentError;
    }

    /// <summary>Deletes files permanently (read-only ones too). Returns an error for the first one that failed, or null.</summary>
    private static LibraryException? DeletePermanently(IEnumerable<string> paths)
    {
        LibraryException? error = null;
        foreach (var path in paths)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                error ??= FileOps.ToLibraryException(ex, path);
            }
        }

        return error;
    }

    /// <summary>An error for the first of <paramref name="paths"/> that is still there after sending them to the Recycle Bin, or null.</summary>
    private static LibraryException? RecycleFailure(IEnumerable<string> paths, Exception ex)
    {
        var path = paths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }

        return FileOps.IsLocked(path)
            ? InUse(path, ex)
            : new LibraryException(LibraryErrorCode.Unknown, $"\"{Path.GetFileName(path)}\" could not be moved to the Recycle Bin: {ex.Message}", ex);
    }

    private static LibraryException InUse(string path, Exception? inner)
        => new(LibraryErrorCode.FileInUse, $"\"{Path.GetFileName(path)}\" is in use by another program. Close it and try again.", inner);

    private static LibraryException NoRecycleBin(IReadOnlyCollection<string> paths, Exception? inner)
        => new(LibraryErrorCode.RecycleBinUnavailable,
            paths.Count == 1
                ? $"\"{Path.GetFileName(paths.First())}\" can't be moved to the Recycle Bin: its drive has none, or it's too large for it."
                : $"{paths.Count} files can't be moved to the Recycle Bin: their drive has none, or they're too large for it.",
            inner);

    // ================================================================ files the Recycle Bin refused after the commit

    /// <summary>Which delete call a refusal came from.</summary>
    private enum RefusedDeleteKind
    {
        Items,
        Category,
        EmptyDeleted,
    }

    /// <summary>A file the Recycle Bin refused after the commit, and the item it belonged to (whose row is gone).</summary>
    private sealed record RefusedFile(long ItemId, string Path);

    /// <summary>
    /// A delete whose files the Recycle Bin refused only after the commit (e.g. too large for it when the size check before the
    /// commit couldn't know the limit). Its rows are gone, so the confirmed retry, the same call again with
    /// <see cref="DeleteItemsOptions.PermanentWhenNoRecycleBin"/>, can't find them: it deletes exactly <see cref="Files"/> instead.
    /// The retry is recognized by the same item ids (<see cref="ItemIds"/>, which it doesn't look up again because a new item may
    /// have one of them by now), the same, already deleted category id, or Empty Deleted. Other deletes in between (an AI's,
    /// say, while the user's "Delete permanently?" question is open) neither use nor forget it.
    /// </summary>
    private sealed record RefusedDelete(RefusedDeleteKind Kind, IReadOnlySet<long> ItemIds, long? CategoryId, string? CategoryFolder)
    {
        public static readonly IReadOnlySet<long> NoItemIds = new HashSet<long>();

        public IReadOnlyList<RefusedFile> Files { get; init; } = [];
    }

    /// <summary>The most refusals remembered at once (the oldest is forgotten first).</summary>
    internal const int MaxRememberedRefusals = 8;

    /// <summary>See <see cref="RefusedDelete"/>, oldest first. Only used inside the write lock.</summary>
    private readonly List<RefusedDelete> _refusedDeletes = [];

    /// <summary>
    /// Returns the remembered refusal of this same delete and forgets it, or null. Only the same delete again takes it: its
    /// confirmed retry uses it, and the same delete without <see cref="DeleteItemsOptions.PermanentWhenNoRecycleBin"/> drops it
    /// and starts over (the user didn't confirm then, and a new item may have one of its ids by now).
    /// </summary>
    private RefusedDelete? TakeRefusedDelete(Predicate<RefusedDelete> sameDelete)
    {
        var index = _refusedDeletes.FindLastIndex(sameDelete);
        if (index < 0)
        {
            return null;
        }

        var refused = _refusedDeletes[index];
        _refusedDeletes.RemoveAt(index);
        return refused;
    }

    /// <summary>Remembers a refusal for its retry, in place of an older one of the same delete.</summary>
    private void RememberRefusedDelete(RefusedDelete refused)
    {
        _refusedDeletes.RemoveAll(r => r.Kind == refused.Kind && r.CategoryId == refused.CategoryId && r.ItemIds.SetEquals(refused.ItemIds));
        _refusedDeletes.Add(refused);
        if (_refusedDeletes.Count > MaxRememberedRefusals)
        {
            _refusedDeletes.RemoveAt(0);
        }
    }

    /// <summary>
    /// "Delete permanently" after a refusal: deletes the remembered files permanently once this write commits. A file is left
    /// alone when it is gone, or when a library item's file is at that path again (e.g. the vault monitor took it back in).
    /// Call it before this write deletes any rows, so a file one of them owns is left to that item's own delete.
    /// </summary>
    private static void DeleteRefusedFiles(WriteContext ctx, IEnumerable<RefusedFile> files)
    {
        var paths = files
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => File.Exists(p) && !IsLibraryFile(ctx, p))
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        ctx.OnCommitted(() =>
        {
            if (DeletePermanently(paths.Where(File.Exists)) is { } error)
            {
                throw error;
            }
        });
    }

    /// <summary>True when a library item's file is at <paramref name="path"/>: a pointer's file, or a vault file in its home folder.</summary>
    private static bool IsLibraryFile(WriteContext ctx, string path)
    {
        if (ctx.Scalar<long>("SELECT COUNT(*) FROM items WHERE mode = 1 AND pointer_path = @path COLLATE NOCASE", new { path }) > 0)
        {
            return true;
        }

        if (ctx.VaultPath is not { } vault || Path.GetDirectoryName(path) is not { } folder)
        {
            return false;
        }

        var name = Path.GetFileName(path);
        var tree = ctx.Tree;
        return tree.All
            .Where(c => FileOps.PathsEqual(tree.FolderPath(vault, c.Id), folder))
            .Any(c => ctx.Scalar<long>(
                "SELECT COUNT(*) FROM items WHERE mode = 0 AND home_category_id = @id AND file_name = @name COLLATE NOCASE",
                new { id = c.Id, name }) > 0);
    }

    /// <summary>A path for comparing the shell's paths with ours.</summary>
    private static string PathKey(string path)
    {
        try
        {
            return FileOps.Normalize(path);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return path;
        }
    }
}
