using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Services;

internal sealed partial class LibraryService
{
    private const int NotePreviewLength = 300;
    private const int NoteSearchTextLength = 100_000;
    private const string TempFilePrefix = NameRules.CabinetteTempPrefix;
    private static readonly TimeSpan EventFlushInterval = TimeSpan.FromSeconds(1);

    // ================================================================ files and folders

    public Task<AddResult> AddPathsAsync(IReadOnlyList<string> paths, AddRequest request, IProgress<AddProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        return Task.Run(() => AddPathsCoreAsync(paths, request, progress, ct), ct);
    }

    private sealed class FolderNode
    {
        public required string Name { get; init; }

        public List<FolderNode> Children { get; } = [];

        public long CategoryId { get; set; }
    }

    private sealed class PlannedFile
    {
        public required string SourcePath { get; init; }

        public long Size { get; init; }

        public DateTime LastWriteUtc { get; init; }

        /// <summary>Set for files that came from an added folder.</summary>
        public FolderNode? Folder { get; init; }
    }

    /// <summary>State shared by all files of one AddPathsAsync call.</summary>
    private sealed class AddSession
    {
        public required AddRequest Request { get; init; }

        public required ItemMode Mode { get; init; }

        public required bool AlsoAddToTypeCategories { get; init; }

        /// <summary>The target's built-in key when the request sorts files by type into their own type categories, else null.</summary>
        public string? SortingKey { get; init; }

        public required string BatchId { get; init; }

        public required ProgressReporter Progress { get; init; }

        public ChangeSet Changes { get; } = new();

        public Dictionary<string, long> BuiltInIds { get; } = new(StringComparer.Ordinal);

        public List<long> AddedIds { get; } = [];

        public List<DuplicateInfo> Duplicates { get; } = [];

        public List<AddFailure> Failures { get; } = [];

        public List<long> CreatedCategoryIds { get; } = [];

        public List<SortedFile> SortedByType { get; } = [];

        public Stopwatch SinceFlush { get; } = Stopwatch.StartNew();
    }

    private async Task<AddResult> AddPathsCoreAsync(IReadOnlyList<string> paths, AddRequest request, IProgress<AddProgress>? progress,
        CancellationToken ct)
    {
        var settings = _settings;
        var mode = request.Mode ?? settings.DefaultAddMode;
        if (mode == ItemMode.Copy)
        {
            RequireVault();
        }

        string? sortingKey = null;
        if (request.TargetCategoryId is long targetId)
        {
            var target = ReadNow(connection => CategoryTree.Load(connection).Get(targetId));
            EnsureCanAddTo(target);
            if (request.SortIntoTypeCategories)
            {
                sortingKey = TypeSorting.SortingKey(target.CategoryKind, target.BuiltInKey);
            }
        }

        var files = new List<PlannedFile>();
        var roots = new List<FolderNode>();
        var scanFailures = new List<AddFailure>();
        ScanPaths(paths, request.SubfoldersAsSubcategories ?? settings.FolderSubfoldersAsSubcategories, files, roots, scanFailures, ct);

        var session = new AddSession
        {
            Request = request,
            Mode = mode,
            AlsoAddToTypeCategories = request.AlsoAddToTypeCategories ?? settings.FolderAlsoAddToTypeCategories,
            SortingKey = sortingKey,
            BatchId = Guid.NewGuid().ToString("N"),
            Progress = new ProgressReporter(progress, files.Count, files.Sum(f => f.Size)),
        };
        session.Changes.Source = request.AddedBy;
        session.Failures.AddRange(scanFailures);

        try
        {
            if (roots.Count > 0)
            {
                await WriteCoreAsync(ctx =>
                {
                    foreach (var root in roots)
                    {
                        CreateFolderCategories(ctx, root, request.TargetCategoryId, session.CreatedCategoryIds);
                    }

                    return true;
                }, ct, session.Changes).ConfigureAwait(false);
            }

            session.Progress.Report(force: true);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                session.Progress.StartFile(file.SourcePath);
                try
                {
                    await AddFileAsync(session, file, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    session.Failures.Add(new AddFailure(file.SourcePath, DescribeError(ex, file.SourcePath)));
                }

                session.Progress.FileDone(file.Size);
                if (session.SinceFlush.Elapsed >= EventFlushInterval)
                {
                    FlushEvents(session);
                }
            }

            session.Progress.Report(force: true);
        }
        finally
        {
            FlushEvents(session);
        }

        var added = ReadNow(connection => ItemReader.LoadMany(connection, null, session.AddedIds, CategoryTree.Load(connection), _settings.VaultPath));
        return new AddResult
        {
            Added = added,
            Duplicates = session.Duplicates,
            Failures = session.Failures,
            CreatedCategoryIds = session.CreatedCategoryIds,
            SortedByType = session.SortedByType,
        };
    }

    private void FlushEvents(AddSession session)
    {
        if (!session.Changes.IsEmpty)
        {
            Raise(session.Changes);
            session.Changes.Clear();
        }

        session.SinceFlush.Restart();
    }

    private static string DescribeError(Exception ex, string path)
        => ex is LibraryException le ? le.Message : FileOps.IsIoError(ex) ? FileOps.ToLibraryException(ex, path).Message : ex.Message;

    private static void ScanPaths(IReadOnlyList<string> paths, bool subfoldersAsSubcategories, List<PlannedFile> files, List<FolderNode> roots,
        List<AddFailure> failures, CancellationToken ct)
    {
        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                var full = Path.GetFullPath(raw);
                if (File.Exists(full))
                {
                    var info = new FileInfo(full);
                    files.Add(new PlannedFile { SourcePath = full, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
                }
                else if (Directory.Exists(full))
                {
                    var dir = new DirectoryInfo(full);
                    var name = Path.TrimEndingDirectorySeparator(dir.Name);
                    var root = new FolderNode { Name = string.IsNullOrWhiteSpace(name) ? "Folder" : name };
                    roots.Add(root);
                    ScanFolder(dir, root, subfoldersAsSubcategories, files, failures, ct);
                }
                else
                {
                    failures.Add(new AddFailure(raw, $"\"{Path.GetFileName(raw)}\" could not be found."));
                }
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                failures.Add(new AddFailure(raw, FileOps.ToLibraryException(ex, raw).Message));
            }
        }
    }

    /// <summary>Collects the folder's files for <paramref name="node"/>; subfolders become child nodes or are flattened into it.</summary>
    private static void ScanFolder(DirectoryInfo dir, FolderNode node, bool subfoldersAsSubcategories, List<PlannedFile> files,
        List<AddFailure> failures, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            failures.Add(new AddFailure(dir.FullName, FileOps.ToLibraryException(ex, dir.FullName).Message));
            return;
        }

        foreach (var file in entries.OfType<FileInfo>())
        {
            if (!NameRules.ShouldSkipFile(file))
            {
                files.Add(new PlannedFile { SourcePath = file.FullName, Size = file.Length, LastWriteUtc = file.LastWriteTimeUtc, Folder = node });
            }
        }

        foreach (var sub in entries.OfType<DirectoryInfo>())
        {
            if (NameRules.ShouldSkipDirectory(sub))
            {
                continue;
            }

            if (subfoldersAsSubcategories)
            {
                var child = new FolderNode { Name = sub.Name };
                node.Children.Add(child);
                ScanFolder(sub, child, subfoldersAsSubcategories, files, failures, ct);
            }
            else
            {
                ScanFolder(sub, node, subfoldersAsSubcategories, files, failures, ct);
            }
        }
    }

    private static void CreateFolderCategories(WriteContext ctx, FolderNode node, long? parentId, List<long> created)
    {
        if (parentId is long p)
        {
            EnsureCanHaveSubcategories(ctx.Tree.Get(p));
        }

        var name = UniqueCategoryName(ctx, parentId, NameRules.ValidateCategoryName(node.Name));
        var row = InsertCategory(ctx, name, parentId, CategoryKind.User, null, ExtensionMap.FolderIconKey, null);
        node.CategoryId = row.Id;
        created.Add(row.Id);
        foreach (var child in node.Children)
        {
            CreateFolderCategories(ctx, child, row.Id, created);
        }
    }

    private async Task<long> ResolveBuiltInAsync(AddSession session, string key, CancellationToken ct)
    {
        if (session.BuiltInIds.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var id = ReadNow(connection => CategoryTree.Load(connection).FindBuiltIn(key)?.Id)
            ?? await WriteCoreAsync(ctx =>
            {
                var created = new List<long>();
                var row = GetOrCreateBuiltIn(ctx, key, created);
                ctx.OnCommitted(() => session.CreatedCategoryIds.AddRange(created));
                return row.Id;
            }, ct, session.Changes).ConfigureAwait(false);
        session.BuiltInIds[key] = id;
        return id;
    }

    private async Task AddFileAsync(AddSession session, PlannedFile file, CancellationToken ct)
    {
        var extension = Path.GetExtension(file.SourcePath);
        long targetId;
        var sorted = false;
        var extraIds = new List<long>();
        if (file.Folder is not null)
        {
            targetId = file.Folder.CategoryId;
            if (session.AlsoAddToTypeCategories)
            {
                extraIds.Add(await ResolveBuiltInAsync(session, ExtensionMap.GetCategoryKey(extension), ct).ConfigureAwait(false));
            }
        }
        else if (session.Request.TargetCategoryId is long target)
        {
            // A built-in type category sorts files of another known type into their own type category (the request opts in).
            if (TypeSorting.Destination(session.SortingKey, extension) is { } typeKey)
            {
                targetId = await ResolveBuiltInAsync(session, typeKey, ct).ConfigureAwait(false);
                sorted = targetId != target;
            }
            else
            {
                targetId = target;
            }
        }
        else
        {
            targetId = await ResolveBuiltInAsync(session, ExtensionMap.GetCategoryKey(extension), ct).ConfigureAwait(false);
        }

        if (session.Mode == ItemMode.Copy)
        {
            await AddCopyAsync(session, file, targetId, extraIds, ct).ConfigureAwait(false);
        }
        else
        {
            await AddPointerAsync(session, file, targetId, extraIds, ct).ConfigureAwait(false);
        }

        // Stored, a duplicate that joined that category, or found again: it went there. A duplicate that already was in
        // its type category wasn't sorted anywhere (nothing changed), so it isn't reported as sorted.
        if (sorted && !session.Duplicates.Any(d => d.SourcePath == file.SourcePath && !d.AddedToTargetCategory))
        {
            session.SortedByType.Add(new SortedFile(file.SourcePath, targetId));
        }
    }

    /// <summary>
    /// Copy mode: copy into a hidden temp file in the vault's staging folder while hashing (outside the write lock), then under
    /// the lock either drop it (duplicate), give it to a missing item with the same content (found again), or move it into the
    /// target category's folder under a unique final name and insert the item. Category folders never hold an open temp file,
    /// so they can be renamed, moved or deleted while a copy runs. When the vault moved while the file was being copied, the
    /// copy went into the old vault: it is thrown away and the file is copied once more, into the new vault.
    /// </summary>
    private async Task AddCopyAsync(AddSession session, PlannedFile file, long targetId, List<long> extraIds, CancellationToken ct)
    {
        var fileName = Path.GetFileName(file.SourcePath);
        var hook = DuringAddCopy; // fires once per add, not again for the second copy
        for (var attempt = 0; ; attempt++)
        {
            var vault = RequireVault();
            var folder = ReadNow(connection =>
            {
                var tree = CategoryTree.Load(connection);
                tree.Get(targetId);
                return tree.FolderPath(vault, targetId);
            });
            var tempPath = NewStagingFile(vault);
            CopyOutcome outcome;
            try
            {
                byte[] hash;
                try
                {
                    hash = await FileOps.CopyAndHashAsync(file.SourcePath, tempPath, bytes =>
                    {
                        session.Progress.AddBytes(bytes);
                        var once = hook;
                        hook = null;
                        once?.Invoke(folder);
                    }, ct).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(tempPath, file.LastWriteUtc);
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    throw FileOps.ToLibraryException(ex, file.SourcePath);
                }

                var size = new FileInfo(tempPath).Length;
                outcome = await WriteCoreAsync(ctx =>
                {
                    if (!FileOps.PathsEqual(ctx.VaultPath, vault))
                    {
                        return CopyOutcome.VaultMoved;
                    }

                    var tree = ctx.Tree;
                    tree.Get(targetId);
                    if (!File.Exists(tempPath))
                    {
                        throw new LibraryException(LibraryErrorCode.NotFound,
                            $"The copy of \"{fileName}\" disappeared before it could be stored. Add it again.");
                    }

                    var match = FindByHash(ctx, hash, size);
                    if (match is { Missing: false })
                    {
                        RecordDuplicate(ctx, session, file.SourcePath, match.Row, targetId, extraIds);
                        return CopyOutcome.Stored;
                    }

                    if (match is { Missing: true })
                    {
                        RelinkAsCopy(ctx, match.Row, vault, tempPath, fileName, file.SourcePath, size, hash, file.LastWriteUtc);
                        RecordRelinked(ctx, session, match.Row.Id, targetId, extraIds);
                        return CopyOutcome.Stored;
                    }

                    var targetFolder = tree.FolderPath(vault, targetId);
                    ctx.EnsureDirectory(targetFolder);
                    var finalName = UniqueVaultFileName(ctx, targetId, fileName, excludeItemId: null);
                    var finalPath = Path.Combine(targetFolder, finalName);
                    ctx.MoveFile(tempPath, finalPath);
                    FileOps.MakeNormal(finalPath);

                    var id = InsertItem(ctx, ItemMode.Copy, ItemKind.File, finalName, null, targetId, size, hash,
                        new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc)), session.Request.AddedBy,
                        session.BatchId, file.SourcePath, null);
                    AddMembership(ctx, id, targetId, session.Request.AddedBy);
                    foreach (var extra in extraIds)
                    {
                        AddMembership(ctx, id, extra, session.Request.AddedBy);
                    }

                    ctx.OnCommitted(() => session.AddedIds.Add(id));
                    return CopyOutcome.Stored;
                }, ct, session.Changes).ConfigureAwait(false);
            }
            finally
            {
                // Duplicate, failure, cancellation or a vault that moved: never leave the temp file behind.
                FileOps.TryDelete(tempPath);
            }

            if (outcome == CopyOutcome.Stored)
            {
                return;
            }

            TryRemoveEmptyStagingFolder(vault);
            if (attempt > 0 || !File.Exists(file.SourcePath))
            {
                throw VaultMovedDuringCopy(fileName);
            }

            session.Progress.RestartFile();
        }
    }

    /// <summary>Test hook: runs once per add while a copy-mode add is copying (its temp file is open), with the target category's folder.</summary>
    internal Action<string>? DuringAddCopy { get; set; }

    /// <summary>
    /// Clean My Downloads: removes an item an add stored moments ago that nobody was told about, because its source changed while
    /// it was being copied. The vault then holds a snapshot of a file that was still being written, and the source is added again
    /// once it is quiet. The item only goes while it is exactly as that add stored it: copy mode, the same vault file (path, size
    /// and modification time, and still the content it was stored with), the same categories, no tags, no star, and its file not
    /// open in another program. Otherwise someone has used it, and it stays. Its vault file goes to the Recycle Bin, like every
    /// file Cabinette removes on its own: usually it is an unfinished copy of a file still in Downloads, but if something saved a
    /// different file over that original meanwhile, this copy is the only one left. Where the Recycle Bin can't take it, the item
    /// stays. Unlike <see cref="DeleteItemsAsync"/>, this leaves a "delete permanently?" retry the user may be asked about alone.
    /// Returns true when the item was removed.
    /// </summary>
    internal async Task<bool> DiscardStaleAddAsync(LibraryItem stored, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ThrowIfDisposed();

        // Read outside the write lock (it can be a large file); its size and date are checked again under the lock.
        try
        {
            if (File.Exists(stored.FullPath)
                && (stored.Hash is null || !(await FileOps.HashFileAsync(stored.FullPath, null, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(stored.Hash)))
            {
                return false;
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return false;
        }

        return await WriteAsync(ctx =>
        {
            var current = ItemReader.Load(ctx.Connection, ctx.Transaction, stored.Id, ctx.Tree, ctx.VaultPath);
            if (current is null || !IsAsStored(current, stored))
            {
                return false;
            }

            var path = current.FullPath;
            if (File.Exists(path) && (FileOps.IsLocked(path) || !_recycleBin.CanRecycle(path)))
            {
                return false;
            }

            ctx.Changes.Item(current.Id, ctx.Tree, MembershipsOf(ctx, current.Id));
            ctx.Changes.Kind |= LibraryChangeKind.Categories;
            ctx.Execute("DELETE FROM items WHERE id = @id", new { id = current.Id });

            // Still inside the write lock, so the vault monitor can't take the file in as a new item meanwhile. If the Recycle
            // Bin refuses it after all, the file stays where it is and the vault monitor takes it in again: nothing is lost.
            ctx.OnCommitted(() =>
            {
                if (!File.Exists(path))
                {
                    return;
                }

                try
                {
                    _recycleBin.SendToRecycleBin([path]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"Cabinette: a stale copy from Clean My Downloads stays in the vault ({path}): {ex.Message}");
                }
            });
            return true;
        }, ct).ConfigureAwait(false);
    }

    private static bool IsAsStored(LibraryItem current, LibraryItem stored)
    {
        if (current.Mode != ItemMode.Copy || current.IsStarred || current.Tags.Count > 0
            || !string.Equals(current.FullPath, stored.FullPath, StringComparison.OrdinalIgnoreCase)
            || current.SizeBytes != stored.SizeBytes || current.FileModifiedAt != stored.FileModifiedAt
            || current.Hash is null || stored.Hash is null || !current.Hash.AsSpan().SequenceEqual(stored.Hash)
            || current.BatchId != stored.BatchId
            || !current.CategoryIds.ToHashSet().SetEquals(stored.CategoryIds))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(current.FullPath);
            return !file.Exists
                   || (file.Length == current.SizeBytes
                       && current.FileModifiedAt is { } modified && file.LastWriteTimeUtc == modified.UtcDateTime);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return false;
        }
    }

    private enum CopyOutcome
    {
        Stored,

        /// <summary>The vault moved while the file was being copied, so the copy is in the old vault.</summary>
        VaultMoved,
    }

    private static LibraryException VaultMovedDuringCopy(string fileName)
        => new(LibraryErrorCode.NotFound, $"The vault moved while \"{fileName}\" was being copied. Add it again.");

    /// <summary>A new temp file path in the vault's staging folder, which is created (hidden) when needed.</summary>
    private static string NewStagingFile(string vault)
    {
        var staging = NameRules.StagingFolder(vault);
        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            // Named after the vault: the staging folder means nothing to the user.
            throw FileOps.ToLibraryException(ex, vault);
        }

        try
        {
            var attributes = File.GetAttributes(staging);
            if ((attributes & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(staging, attributes | FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            // Cosmetic only: the vault monitor ignores the folder by its name.
        }

        return Path.Combine(staging, TempFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
    }

    /// <summary>Removes the staging folder of a vault that is no longer in use (after Move vault) when nothing is in it. Best effort.</summary>
    private static void TryRemoveEmptyStagingFolder(string vault)
    {
        try
        {
            var staging = NameRules.StagingFolder(vault);
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
            {
                Directory.Delete(staging);
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            Debug.WriteLine($"Cabinette: could not remove the staging folder of {vault}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes what interrupted copies left in the vault's staging folder (the app quit or crashed mid-copy). Runs in the
    /// background once the library opened, so a vault on a network drive that can't be reached doesn't hold up the start. Only
    /// Cabinette's own temp files created before <paramref name="createdBeforeUtc"/> go (later ones belong to copies of this run),
    /// and a file still open is skipped. Never throws.
    /// </summary>
    internal static void SweepStagingFolder(string vault, DateTime createdBeforeUtc)
    {
        try
        {
            var staging = new DirectoryInfo(NameRules.StagingFolder(vault));
            if (!staging.Exists)
            {
                return;
            }

            foreach (var file in staging.EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if (!file.Name.StartsWith(TempFilePrefix, StringComparison.OrdinalIgnoreCase) || file.CreationTimeUtc >= createdBeforeUtc)
                {
                    continue;
                }

                try
                {
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    Debug.WriteLine($"Cabinette: could not delete the leftover {file.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Cabinette: could not empty the staging folder of {vault}: {ex.Message}");
        }
    }

    /// <summary>Pointer mode: hash in place (large files later, in the background) and reference the file where it is.</summary>
    private async Task AddPointerAsync(AddSession session, PlannedFile file, long targetId, List<long> extraIds, CancellationToken ct)
    {
        var path = file.SourcePath;
        byte[]? hash = null;
        if (file.Size <= BackgroundHashThresholdBytes)
        {
            try
            {
                hash = await FileOps.HashFileAsync(path, session.Progress.AddBytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw FileOps.ToLibraryException(ex, path);
            }
        }
        else
        {
            session.Progress.AddBytes(file.Size);
        }

        await WriteCoreAsync(ctx =>
        {
            ctx.Tree.Get(targetId);

            // An item for this very path that was marked missing has its file back.
            var samePath = ItemReader.QueryRows(ctx.Connection, ctx.Transaction, "WHERE i.mode = 1 AND i.pointer_path = @path COLLATE NOCASE",
                new { path }).FirstOrDefault();
            var match = samePath is not null ? new ContentMatch(samePath, samePath.IsMissing != 0) : FindByHash(ctx, hash, file.Size);
            if (match is { Missing: false })
            {
                RecordDuplicate(ctx, session, path, match.Row, targetId, extraIds);
                return true;
            }

            if (match is { Missing: true })
            {
                RelinkAsPointer(ctx, match.Row, path, file.Size, hash, DbValues.ToText(file.LastWriteUtc));
                RecordRelinked(ctx, session, match.Row.Id, targetId, extraIds);
                return true;
            }

            var id = InsertItem(ctx, ItemMode.Pointer, ItemKind.File, Path.GetFileName(path), path, targetId, file.Size, hash,
                new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc)), session.Request.AddedBy,
                session.BatchId, null, null);
            AddMembership(ctx, id, targetId, session.Request.AddedBy);
            foreach (var extra in extraIds)
            {
                AddMembership(ctx, id, extra, session.Request.AddedBy);
            }

            ctx.OnCommitted(() =>
            {
                session.AddedIds.Add(id);
                if (hash is null)
                {
                    _hashQueue.Enqueue(id);
                }
            });
            return true;
        }, ct, session.Changes).ConfigureAwait(false);
    }

    private static void RecordDuplicate(WriteContext ctx, AddSession session, string sourcePath, ItemRow existingRow, long targetId,
        List<long> extraIds)
    {
        var existingId = existingRow.Id;
        if (existingRow.IsMissing != 0)
        {
            // Marked missing, but its file is there again.
            ctx.Execute("UPDATE items SET is_missing = 0 WHERE id = @existingId", new { existingId });
            ctx.Changes.Item(existingId, ctx.Tree, MembershipsOf(ctx, existingId));
        }

        var addedToTarget = AddMembership(ctx, existingId, targetId, session.Request.AddedBy);
        foreach (var extra in extraIds)
        {
            AddMembership(ctx, existingId, extra, session.Request.AddedBy);
        }

        var existing = ItemReader.Load(ctx.Connection, ctx.Transaction, existingId, ctx.Tree, ctx.VaultPath)!;
        ctx.OnCommitted(() => session.Duplicates.Add(new DuplicateInfo(sourcePath, existing, addedToTarget)));
    }

    /// <summary>A missing item was re-linked to the added file: it joins the target categories and is reported as added.</summary>
    private void RecordRelinked(WriteContext ctx, AddSession session, long itemId, long targetId, List<long> extraIds)
    {
        AddMembership(ctx, itemId, targetId, session.Request.AddedBy);
        foreach (var extra in extraIds)
        {
            AddMembership(ctx, itemId, extra, session.Request.AddedBy);
        }

        ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, itemId));
        var unhashed = ctx.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = @itemId AND hash IS NULL", new { itemId }) > 0;
        ctx.OnCommitted(() =>
        {
            session.AddedIds.Add(itemId);
            if (unhashed)
            {
                _hashQueue.Enqueue(itemId);
            }
        });
    }

    /// <summary>Throttled <see cref="AddProgress"/> reports.</summary>
    private sealed class ProgressReporter(IProgress<AddProgress>? progress, int totalFiles, long totalBytes)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private int _processedFiles;
        private long _bytes;
        private long _bytesAtFileStart;
        private string? _current;

        public void StartFile(string path)
        {
            _current = path;
            _bytesAtFileStart = _bytes;
            Report(force: true);
        }

        public void AddBytes(long count)
        {
            _bytes += count;
            Report(force: false);
        }

        /// <summary>The current file is copied once more: its bytes count from the start again.</summary>
        public void RestartFile()
        {
            _bytes = _bytesAtFileStart;
            Report(force: true);
        }

        public void FileDone(long fileSize)
        {
            _processedFiles++;

            // Skipped or failed files still count as processed.
            _bytes = Math.Max(_bytes, _bytesAtFileStart + fileSize);
            Report(force: _processedFiles == totalFiles);
        }

        public void Report(bool force)
        {
            if (progress is null || (!force && _sinceReport.ElapsedMilliseconds < 100))
            {
                return;
            }

            _sinceReport.Restart();
            progress.Report(new AddProgress(_processedFiles, totalFiles, _current, Math.Min(_bytes, totalBytes), totalBytes));
        }
    }

    // ================================================================ notes and paste

    public Task<LibraryItem> CreateNoteAsync(long categoryId, string text, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(ctx =>
        {
            ctx.Changes.Source = addedBy;
            var id = WriteVaultTextItem(ctx, categoryId, "Note", ".md", text, ItemKind.Note, addedBy, dedupe: false, out _);
            return ItemReader.Load(ctx.Connection, ctx.Transaction, id, ctx.Tree, ctx.VaultPath)!;
        }, ct);
    }

    public Task<AddResult> AddPastedTextAsync(long categoryId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(ctx =>
        {
            ctx.Changes.Source = AddedBy.Paste;
            var url = TryGetSingleUrl(text);
            if (url is null)
            {
                var noteId = WriteVaultTextItem(ctx, categoryId, "Pasted text", ".md", text, ItemKind.Note, AddedBy.Paste, dedupe: false, out _);
                return new AddResult { Added = [ItemReader.Load(ctx.Connection, ctx.Transaction, noteId, ctx.Tree, ctx.VaultPath)!] };
            }

            var content = "[InternetShortcut]\r\nURL=" + url + "\r\n";
            var id = WriteVaultTextItem(ctx, categoryId, "Link", ".url", content, ItemKind.Link, AddedBy.Paste, dedupe: true, out var addedToTarget,
                searchText: url, preview: url);
            var item = ItemReader.Load(ctx.Connection, ctx.Transaction, id, ctx.Tree, ctx.VaultPath)!;
            return addedToTarget is null
                ? new AddResult { Added = [item] }
                : new AddResult { Duplicates = [new DuplicateInfo(url, item, addedToTarget.Value)] };
        }, ct);
    }

    public Task<AddResult> AddPastedImageAsync(long categoryId, byte[] png, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        return WriteAsync(ctx =>
        {
            ctx.Changes.Source = AddedBy.Paste;
            var id = WriteVaultItem(ctx, categoryId, "Pasted image", ".png", png, ItemKind.PastedImage, AddedBy.Paste, dedupe: true,
                out var addedToTarget, searchText: null, preview: null, out var fileName);
            var item = ItemReader.Load(ctx.Connection, ctx.Transaction, id, ctx.Tree, ctx.VaultPath)!;
            return addedToTarget is null
                ? new AddResult { Added = [item] }
                : new AddResult { Duplicates = [new DuplicateInfo(fileName, item, addedToTarget.Value)] };
        }, ct);
    }

    /// <summary>A single absolute http(s) URL (after trimming, without whitespace), or null.</summary>
    internal static string? TryGetSingleUrl(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? trimmed
            : null;
    }

    private long WriteVaultTextItem(WriteContext ctx, long categoryId, string prefix, string extension, string text, ItemKind kind,
        AddedBy addedBy, bool dedupe, out bool? addedToTarget, string? searchText = null, string? preview = null)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        if (kind == ItemKind.Note)
        {
            searchText = text.Length > NoteSearchTextLength ? Truncate(text, NoteSearchTextLength) : text;
            preview = Truncate(text, NotePreviewLength);
        }

        return WriteVaultItem(ctx, categoryId, prefix, extension, bytes, kind, addedBy, dedupe, out addedToTarget, searchText, preview, out _);
    }

    /// <summary>
    /// Writes "{prefix} yyyy-MM-dd HH-mm-ss{ext}" (local time) into the category's vault folder and inserts the item.
    /// With <paramref name="dedupe"/>, existing identical content is reused instead: returns its id and sets <paramref name="addedToTarget"/>.
    /// </summary>
    private long WriteVaultItem(WriteContext ctx, long categoryId, string prefix, string extension, byte[] content, ItemKind kind,
        AddedBy addedBy, bool dedupe, out bool? addedToTarget, string? searchText, string? preview, out string fileName)
    {
        var vault = RequireVault();
        var tree = ctx.Tree;
        EnsureCanAddTo(tree.Get(categoryId));
        var hash = FileOps.Hash(content);
        var localNow = _time.GetLocalNow();
        fileName = $"{prefix} {localNow.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}{extension}";

        // An identical item whose file is missing doesn't count: the paste becomes a new item.
        if (dedupe && FindByHash(ctx, hash, content.Length) is { Missing: false } match)
        {
            addedToTarget = AddMembership(ctx, match.Row.Id, categoryId, addedBy);
            return match.Row.Id;
        }

        addedToTarget = null;
        var folder = tree.FolderPath(vault, categoryId);
        ctx.EnsureDirectory(folder);
        fileName = UniqueVaultFileName(ctx, categoryId, fileName, excludeItemId: null);
        var path = Path.Combine(folder, fileName);
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ctx.FileCreated(path);
                stream.Write(content);
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, path);
        }

        // Store the file's real modification time so the vault monitor sees the file as unchanged.
        DateTimeOffset modified;
        try
        {
            modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            modified = ctx.Now;
        }

        var id = InsertItem(ctx, ItemMode.Copy, kind, fileName, null, categoryId, content.Length, hash, modified, addedBy,
            Guid.NewGuid().ToString("N"), null, preview);
        if (searchText is not null)
        {
            ctx.Execute("UPDATE search_fts SET note_text = @searchText WHERE rowid = @id", new { searchText, id });
        }

        AddMembership(ctx, id, categoryId, addedBy);
        return id;
    }

    private static string Truncate(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..cut];
    }

    // ================================================================ background hashing of large pointer files

    /// <summary>Test hook: runs after a pointer file was hashed in the background and before the hash is stored, with the item id.</summary>
    internal Func<long, Task>? AfterBackgroundHash { get; set; }

    private async Task ProcessPendingHashAsync(long itemId, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        var row = ReadNow(connection => ItemReader.LoadRow(connection, null, itemId));
        if (row is null || row.Hash is not null || row.ItemMode != ItemMode.Pointer || row.PointerPath is null)
        {
            return;
        }

        byte[] hash;
        try
        {
            hash = await FileOps.HashFileAsync(row.PointerPath, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            // Renamed meanwhile: hash it at its new path. Otherwise unreadable right now; it stays unhashed and is retried on the next start.
            Debug.WriteLine($"Cabinette: could not hash {row.PointerPath}: {ex.Message}");
            if (!_disposed && ReadNow(connection => ItemReader.LoadRow(connection, null, itemId)) is { Hash: null, ItemMode: ItemMode.Pointer } now
                && !FileOps.PathsEqual(now.PointerPath, row.PointerPath))
            {
                _hashQueue.Enqueue(itemId);
            }

            return;
        }

        if (AfterBackgroundHash is { } hook)
        {
            await hook(itemId).ConfigureAwait(false);
        }

        await WriteCoreAsync(ctx =>
        {
            var current = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId);
            if (current is null || current.Hash is not null || current.ItemMode != ItemMode.Pointer)
            {
                return true;
            }

            if (!FileOps.PathsEqual(current.PointerPath, row.PointerPath))
            {
                // Renamed while it was being hashed: hash it again at its new path.
                ctx.OnCommitted(() => _hashQueue.Enqueue(itemId));
                return true;
            }

            var tree = ctx.Tree;
            var match = FindByHash(ctx, hash, current.SizeBytes ?? 0, excludeItemId: itemId);
            if (match is null)
            {
                ctx.Execute("UPDATE items SET hash = @hash WHERE id = @itemId", new { hash, itemId });
                ctx.Changes.Item(itemId, tree, MembershipsOf(ctx, itemId));
                return true;
            }

            // Same content as an existing item, discovered after the fact: the new pointer item is folded into it. If that item's
            // file is missing, it was found again: it takes over the new item's file.
            var existingId = match.Row.Id;
            var memberships = ctx.Query<MembershipRow>(
                "SELECT item_id AS ItemId, category_id AS CategoryId, added_at AS AddedAt, added_by AS AddedBy FROM item_categories WHERE item_id = @itemId ORDER BY added_at",
                new { itemId });
            var addedAny = false;
            foreach (var m in memberships)
            {
                var inserted = ctx.Execute(
                    "INSERT OR IGNORE INTO item_categories(item_id, category_id, added_at, added_by) VALUES (@existingId, @cat, @at, @by)",
                    new { existingId, cat = m.CategoryId, at = m.AddedAt, by = m.AddedBy }) > 0;
                addedAny |= inserted;
                ctx.Changes.Category(tree, m.CategoryId);
            }

            ctx.Execute("INSERT OR IGNORE INTO item_tags(item_id, tag_id) SELECT @existingId, tag_id FROM item_tags WHERE item_id = @itemId",
                new { existingId, itemId });

            // A star given while it was being hashed goes along too (an existing star keeps its time).
            ctx.Execute("INSERT OR IGNORE INTO item_stars(item_id, starred_at) SELECT @existingId, starred_at FROM item_stars WHERE item_id = @itemId",
                new { existingId, itemId });
            NoteStarsLeaving(ctx, [itemId]);
            ctx.Execute("DELETE FROM items WHERE id = @itemId", new { itemId });
            if (match.Missing)
            {
                RelinkAsPointer(ctx, match.Row, current.PointerPath!, current.SizeBytes, hash, current.FileModifiedAt);
            }

            // Leaving "Deleted" works as for any other new membership.
            if (addedAny && tree.DeletedId is long deletedId && memberships.Any(m => m.CategoryId != deletedId))
            {
                var remaining = MembershipsOf(ctx, existingId);
                if (remaining.Contains(deletedId))
                {
                    RemoveMemberships(ctx, existingId, [deletedId]);
                    ctx.Changes.Category(tree, deletedId);
                    var existingRow = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, existingId)!;
                    if (existingRow.HomeCategoryId == deletedId)
                    {
                        Rehome(ctx, existingRow, remaining.First(c => c != deletedId));
                    }
                }
            }

            ctx.Changes.Item(itemId);
            ctx.Changes.Item(existingId, tree, MembershipsOf(ctx, existingId));
            if (!match.Missing)
            {
                var existing = ItemReader.Load(ctx.Connection, ctx.Transaction, existingId, tree, ctx.VaultPath)!;
                ctx.Changes.MergedDuplicates.Add(new DuplicateInfo(row.PointerPath, existing, addedAny));
            }

            return true;
        }, ct, accumulate: null).ConfigureAwait(false);
    }
}
