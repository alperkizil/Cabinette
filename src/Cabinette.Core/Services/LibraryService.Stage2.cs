using System.Diagnostics;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;
using Dapper;

namespace Cabinette.Core.Services;

// Stage 2 vault operations: monitor, move, migrate, backup and the library side of restore (SPEC.md section 8).
internal sealed partial class LibraryService
{
    private readonly object _monitorGate = new();
    private VaultMonitor? _monitor;

    /// <summary>How many times Move vault copies what changed in the old vault during the move before it gives up.</summary>
    internal const int MoveVaultCatchUpRounds = 3;

    /// <summary>Test hook: runs after each file was copied by <see cref="MoveVaultAsync"/>, with the copy's path.</summary>
    internal Action<string>? AfterMoveVaultFileCopied { get; set; }

    /// <summary>Test hook: runs before <see cref="MoveVaultAsync"/> lists the old vault again after the copy (round 1, 2, ...).</summary>
    internal Action<int>? BeforeMoveVaultRecheck { get; set; }

    public IVaultMonitor CreateVaultMonitor()
    {
        ThrowIfDisposed();
        lock (_monitorGate)
        {
            return _monitor ??= new VaultMonitor(this, _time);
        }
    }

    // ================================================================ move vault

    public Task<MoveVaultResult> MoveVaultAsync(string newVaultPath, IProgress<VaultOperationProgress>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            var target = NormalizeVaultPath(newVaultPath);
            ValidateMoveTarget(RequireVault(), target);
            VaultMonitor? monitor;
            lock (_monitorGate)
            {
                monitor = _monitor;
            }

            if (monitor is not null)
            {
                await monitor.SuspendAsync().ConfigureAwait(false);
            }

            try
            {
                var (result, ctx) = await MoveVaultLockedAsync(target, progress, ct).ConfigureAwait(false);
                FinishWrite(true, ctx, accumulate: null);
                return result;
            }
            finally
            {
                if (monitor is not null)
                {
                    await monitor.ResumeAfterSuspendAsync().ConfigureAwait(false);
                }
            }
        }, ct);
    }

    private static void ValidateMoveTarget(string currentVault, string target)
    {
        if (FileOps.PathsEqual(currentVault, target))
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "This folder already is the vault.");
        }

        if (FileOps.IsSameOrInside(target, currentVault))
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "The new vault can't be inside the current vault.");
        }

        if (File.Exists(target))
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, $"\"{target}\" is a file. Choose a folder for the vault.");
        }

        try
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, "Choose an empty folder for the new vault.");
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, target);
        }
    }

    /// <summary>A file Move vault copied: the old vault's listing entry it was copied from, its hash and the bytes written.</summary>
    private sealed record MovedFile(VaultFileEntry Source, byte[] Hash, long Bytes);

    /// <summary>
    /// Copies and verifies the whole vault, catches up with what changed in the old vault meanwhile, then switches the setting,
    /// all under the write lock so the library can't change meanwhile. On failure or cancellation exactly what the move created
    /// is removed (never anything else in the target) and the old vault stays in use.
    /// </summary>
    private async Task<(MoveVaultResult Result, WriteContext Context)> MoveVaultLockedAsync(string target, IProgress<VaultOperationProgress>? progress,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        var created = new CreatedPaths();
        var switched = false;
        try
        {
            ThrowIfDisposed();
            var oldVault = RequireVault();
            ValidateMoveTarget(oldVault, target);
            created.CreateDirectory(target);
            EnsureWritableFolder(target);

            var (directoryList, fileList) = ListVault(oldVault, ct);
            var totalBytes = fileList.Sum(f => f.Size);
            EnsureFreeSpace(target, totalBytes);
            var directories = new Dictionary<string, VaultDirectoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in directoryList)
            {
                CreateMovedFolder(created, target, dir);
                directories[dir.RelativePath] = dir;
            }

            var files = new Dictionary<string, MovedFile>(StringComparer.OrdinalIgnoreCase);
            var copying = new VaultProgressReporter(progress, "copying", fileList.Count, totalBytes);
            foreach (var file in fileList)
            {
                ct.ThrowIfCancellationRequested();
                copying.StartFile(file.RelativePath);
                if (await CopyMovedFileAsync(created, target, file, copying.AddBytes, ct).ConfigureAwait(false) is { } copy)
                {
                    files[file.RelativePath] = copy;
                }

                copying.FileDone(file.Size);
                AfterMoveVaultFileCopied?.Invoke(Path.Combine(target, file.RelativePath));
            }

            var verifying = new VaultProgressReporter(progress, "verifying", files.Count, files.Values.Sum(f => f.Bytes));
            foreach (var copy in files.Values)
            {
                ct.ThrowIfCancellationRequested();
                verifying.StartFile(copy.Source.RelativePath);
                await VerifyMovedFileAsync(target, copy, verifying.AddBytes, ct).ConfigureAwait(false);
                verifying.FileDone(copy.Bytes);
            }

            await CatchUpMovedVaultAsync(oldVault, target, directories, files, created, progress, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            var bytes = files.Values.Sum(f => f.Bytes);
            progress?.Report(new VaultOperationProgress("switching", files.Count, files.Count, bytes, bytes, null));
            var (_, ctx) = RunTransaction(c =>
            {
                // Moving the vault is the one place the vault path may change after first run.
                var saved = _settings with { VaultPath = target };
                StoreSettings(c, saved);
                c.OnCommitted(() => _settings = saved);
                c.Changes.Kind |= LibraryChangeKind.Settings | LibraryChangeKind.Categories | LibraryChangeKind.Items;

                // Every item's path changed: open threads, the chat list and search reload.
                c.Changes.Categories(c.Tree, c.Tree.All.Select(category => category.Id).ToList());
                return true;
            });
            switched = true;
            return (new MoveVaultResult(oldVault, target, files.Count, bytes), ctx);
        }
        finally
        {
            if (!switched)
            {
                created.Remove();
            }

            _writeLock.Release();
        }
    }

    private static (List<VaultDirectoryEntry> Directories, List<VaultFileEntry> Files) ListVault(string vault, CancellationToken ct)
    {
        try
        {
            return VaultFiles.Enumerate(vault, ct);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, vault);
        }
    }

    /// <summary>Creates a folder of the moved vault with its source folder's Hidden and System flags (when the move created it).</summary>
    private static void CreateMovedFolder(CreatedPaths created, string target, VaultDirectoryEntry dir)
    {
        var path = Path.Combine(target, dir.RelativePath);
        if (created.CreateDirectory(path))
        {
            FileOps.ApplyFolderAttributes(path, dir.Attributes);
        }
    }

    /// <summary>
    /// Copies one file of the old vault into a new file of the target (journaled as soon as it exists), with its modification
    /// time and attributes. Null when the file was deleted from the old vault since the listing (the catch-up sees it's gone).
    /// </summary>
    private static async Task<MovedFile?> CopyMovedFileAsync(CreatedPaths created, string target, VaultFileEntry file, Action<long> onBytes,
        CancellationToken ct)
    {
        var destination = Path.Combine(target, file.RelativePath);
        long bytes = 0;
        try
        {
            var hash = await FileOps.CopyAndHashAsync(file.FullPath, destination, read =>
            {
                bytes += read;
                onBytes(read);
            }, ct, hideDestination: false, onCreated: () => created.FileCreated(destination)).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(destination, file.LastWriteUtc);
            var attributes = file.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Archive);
            if ((attributes & ~FileAttributes.Archive) != 0)
            {
                File.SetAttributes(destination, attributes);
            }

            return new MovedFile(file, hash, bytes);
        }
        catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) && !created.IsOwnFile(destination) && !File.Exists(file.FullPath))
        {
            return null;
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, file.FullPath);
        }
    }

    /// <summary>Reads a copy back: it must have exactly the bytes and the hash that were written.</summary>
    private static async Task VerifyMovedFileAsync(string target, MovedFile copy, Action<long>? onBytes, CancellationToken ct)
    {
        var destination = Path.Combine(target, copy.Source.RelativePath);
        long size;
        byte[] hash;
        try
        {
            size = new FileInfo(destination).Length;
            hash = await FileOps.HashFileAsync(destination, onBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, destination);
        }

        if (size != copy.Bytes || !hash.AsSpan().SequenceEqual(copy.Hash))
        {
            throw new LibraryException(LibraryErrorCode.Unknown,
                $"\"{Path.GetFileName(copy.Source.RelativePath)}\" could not be verified after copying. The vault was not moved.");
        }
    }

    /// <summary>
    /// Other programs may change the old vault while it is copied. Lists it again and brings the copy up to date: new and changed
    /// files (by size and modification time) are copied again and verified, deleted ones are removed from the copy, new folders
    /// are created and removed ones go if empty. Repeats until a listing shows no change; if the vault still changes after
    /// <see cref="MoveVaultCatchUpRounds"/> rounds, the move stops and names a file.
    /// </summary>
    private async Task CatchUpMovedVaultAsync(string oldVault, string target, Dictionary<string, VaultDirectoryEntry> directories,
        Dictionary<string, MovedFile> files, CreatedPaths created, IProgress<VaultOperationProgress>? progress, CancellationToken ct)
    {
        for (var round = 1; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            BeforeMoveVaultRecheck?.Invoke(round);
            progress?.Report(new VaultOperationProgress("rechecking", 0, 0, 0, 0, null));
            var (currentDirectories, currentFiles) = ListVault(oldVault, ct);
            var changed = currentFiles
                .Where(f => !files.TryGetValue(f.RelativePath, out var copy) || copy.Source.Size != f.Size || copy.Source.LastWriteUtc != f.LastWriteUtc)
                .ToList();
            var currentFileNames = currentFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deleted = files.Keys.Where(relative => !currentFileNames.Contains(relative)).ToList();
            var newDirectories = currentDirectories.Where(d => !directories.ContainsKey(d.RelativePath)).ToList();
            var currentDirectoryNames = currentDirectories.Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removedDirectories = directories.Keys.Where(relative => !currentDirectoryNames.Contains(relative)).ToList();
            if (changed.Count == 0 && deleted.Count == 0 && newDirectories.Count == 0 && removedDirectories.Count == 0)
            {
                return;
            }

            if (round > MoveVaultCatchUpRounds)
            {
                var example = changed.Select(f => f.RelativePath).Concat(deleted)
                    .Concat(newDirectories.Select(d => d.RelativePath)).Concat(removedDirectories).First();
                throw new LibraryException(LibraryErrorCode.FileInUse,
                    $"Files in the vault kept changing during the move (for example \"{Path.GetFileName(example)}\"). The vault was not moved. " +
                    "Try again when no program is changing files in it.");
            }

            try
            {
                foreach (var relative in deleted)
                {
                    created.DeleteFile(Path.Combine(target, relative));
                    files.Remove(relative);
                }

                foreach (var relative in removedDirectories.OrderByDescending(r => r.Count(c => c == Path.DirectorySeparatorChar)))
                {
                    created.TryDeleteEmptyDirectory(Path.Combine(target, relative));
                    directories.Remove(relative);
                }

                foreach (var dir in newDirectories)
                {
                    CreateMovedFolder(created, target, dir);
                    directories[dir.RelativePath] = dir;
                }

                // A changed file's old copy is ours: it is replaced by a fresh copy.
                foreach (var file in changed)
                {
                    if (files.Remove(file.RelativePath, out var stale))
                    {
                        created.DeleteFile(Path.Combine(target, stale.Source.RelativePath));
                    }
                }
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw FileOps.ToLibraryException(ex, target);
            }

            var copying = new VaultProgressReporter(progress, "rechecking", changed.Count, changed.Sum(f => f.Size));
            foreach (var file in changed)
            {
                ct.ThrowIfCancellationRequested();
                copying.StartFile(file.RelativePath);
                if (await CopyMovedFileAsync(created, target, file, copying.AddBytes, ct).ConfigureAwait(false) is { } copy)
                {
                    await VerifyMovedFileAsync(target, copy, null, ct).ConfigureAwait(false);
                    files[file.RelativePath] = copy;
                }

                copying.FileDone(file.Size);
            }
        }
    }

    private static void EnsureWritableFolder(string folder)
    {
        var probe = Path.Combine(folder, NameRules.CabinetteTempPrefix + "probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var _ = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, folder);
        }
    }

    private static void EnsureFreeSpace(string folder, long bytesNeeded)
    {
        long available;
        try
        {
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }

            available = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return;
        }

        if (available < bytesNeeded)
        {
            throw new LibraryException(LibraryErrorCode.Unknown, "There is not enough space on the disk for the vault.");
        }
    }

    // ================================================================ migrate pointers

    public Task<MigrateResult> MigratePointersAsync(IProgress<VaultOperationProgress>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            // Each file is copied into the vault in use at that time (the vault may move meanwhile).
            RequireVault();
            var rows = ReadNow(connection => ItemReader.QueryRows(connection, null, "WHERE i.mode = 1 ORDER BY i.id", null));
            var reporter = new VaultProgressReporter(progress, "migrating", rows.Count, rows.Sum(r => r.SizeBytes ?? 0));
            var changes = new ChangeSet();
            var sinceFlush = Stopwatch.StartNew();
            var skipped = new List<long>();
            var failures = new List<AddFailure>();
            var migrated = 0;
            try
            {
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    reporter.StartFile(row.FileName);
                    var source = row.PointerPath;
                    if (string.IsNullOrEmpty(source) || !File.Exists(source))
                    {
                        skipped.Add(row.Id);
                    }
                    else
                    {
                        try
                        {
                            await MigrateOneAsync(row, source, reporter, changes, ct).ConfigureAwait(false);
                            migrated++;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failures.Add(new AddFailure(source, DescribeError(ex, source)));
                        }
                    }

                    reporter.FileDone(row.SizeBytes ?? 0);
                    if (sinceFlush.Elapsed >= EventFlushInterval)
                    {
                        RaiseAndClear(changes);
                        sinceFlush.Restart();
                    }
                }

                if (skipped.Count > 0)
                {
                    // The missing ones get their "missing" badge.
                    await WriteCoreAsync(ctx =>
                    {
                        var marked = ctx.Query<long>(
                            "UPDATE items SET is_missing = 1 WHERE id IN (SELECT value FROM json_each(@ids)) AND mode = 1 AND is_missing = 0 RETURNING id",
                            new { ids = DbValues.JsonArray(skipped) });
                        foreach (var id in marked)
                        {
                            ctx.Changes.Item(id, ctx.Tree, MembershipsOf(ctx, id));
                        }

                        return true;
                    }, ct, changes).ConfigureAwait(false);
                }
            }
            finally
            {
                RaiseAndClear(changes);
            }

            var skippedItems = ReadNow(connection => ItemReader.LoadMany(connection, null, skipped, CategoryTree.Load(connection), _settings.VaultPath));
            return new MigrateResult(migrated, skippedItems, failures);
        }, ct);
    }

    private void RaiseAndClear(ChangeSet changes)
    {
        if (!changes.IsEmpty)
        {
            Raise(changes);
            changes.Clear();
        }
    }

    /// <summary>Test hook: runs once per migrated file while it is being copied (its temp file is open), with the home category's folder.</summary>
    internal Action<string>? DuringMigrateCopy { get; set; }

    /// <summary>
    /// Copies one pointer file into the vault's staging folder (outside the lock), then moves it into its home category's folder
    /// under the lock and turns the item into a copy-mode item. When the vault moved meanwhile, the file is copied once more, into
    /// the new vault, as an add does.
    /// </summary>
    private async Task MigrateOneAsync(ItemRow row, string source, VaultProgressReporter reporter, ChangeSet changes, CancellationToken ct)
    {
        var hook = DuringMigrateCopy;
        for (var attempt = 0; ; attempt++)
        {
            var vault = RequireVault();
            var folder = ReadNow(connection => CategoryTree.Load(connection).FolderPath(vault, row.HomeCategoryId));
            var tempPath = NewStagingFile(vault);
            CopyOutcome outcome;
            try
            {
                byte[] hash;
                DateTime lastWrite;
                try
                {
                    lastWrite = File.GetLastWriteTimeUtc(source);
                    hash = await FileOps.CopyAndHashAsync(source, tempPath, bytes =>
                    {
                        reporter.AddBytes(bytes);
                        var once = hook;
                        hook = null;
                        once?.Invoke(folder);
                    }, ct).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(tempPath, lastWrite);
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    throw FileOps.ToLibraryException(ex, source);
                }

                var size = new FileInfo(tempPath).Length;
                outcome = await WriteCoreAsync(ctx =>
                {
                    if (!FileOps.PathsEqual(ctx.VaultPath, vault))
                    {
                        return CopyOutcome.VaultMoved;
                    }

                    var current = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, row.Id);
                    if (current is null || current.ItemMode != ItemMode.Pointer || !FileOps.PathsEqual(current.PointerPath, source))
                    {
                        throw new LibraryException(LibraryErrorCode.NotFound, $"\"{row.FileName}\" changed while it was being copied.");
                    }

                    if (!File.Exists(tempPath))
                    {
                        throw new LibraryException(LibraryErrorCode.NotFound, $"The copy of \"{row.FileName}\" disappeared before it could be stored.");
                    }

                    var tree = ctx.Tree;
                    var targetFolder = tree.FolderPath(vault, current.HomeCategoryId);
                    ctx.EnsureDirectory(targetFolder);
                    var finalName = UniqueVaultFileName(ctx, current.HomeCategoryId, current.FileName, excludeItemId: current.Id);
                    var finalPath = Path.Combine(targetFolder, finalName);
                    ctx.MoveFile(tempPath, finalPath);
                    FileOps.MakeNormal(finalPath);
                    ctx.Execute(
                        """
                        UPDATE items SET mode = 0, pointer_path = NULL, original_path = @source, file_name = @finalName, extension = @extension,
                                         size_bytes = @size, hash = @hash, file_modified_at = @modified, is_missing = 0
                        WHERE id = @id
                        """,
                        new
                        {
                            source,
                            finalName,
                            extension = ExtensionMap.Normalize(Path.GetExtension(finalName)),
                            size,
                            hash,
                            modified = DbValues.ToText(lastWrite),
                            id = current.Id,
                        });
                    ctx.Changes.Item(current.Id, tree, MembershipsOf(ctx, current.Id));
                    return CopyOutcome.Stored;
                }, ct, changes).ConfigureAwait(false);
            }
            finally
            {
                // Failure, cancellation or a vault that moved: never leave the temp file behind.
                FileOps.TryDelete(tempPath);
            }

            if (outcome == CopyOutcome.Stored)
            {
                return;
            }

            TryRemoveEmptyStagingFolder(vault);
            if (attempt > 0 || !File.Exists(source))
            {
                throw VaultMovedDuringCopy(row.FileName);
            }
        }
    }

    // ================================================================ backup

    public Task<BackupResult> CreateBackupAsync(string zipPath, IProgress<VaultOperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ThrowIfDisposed();
        return Task.Run(async () =>
        {
            string target;
            try
            {
                target = Path.GetFullPath(zipPath);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw new LibraryException(LibraryErrorCode.InvalidName, $"\"{zipPath}\" is not a valid file name.", ex);
            }

            if (FileOps.IsSameOrInside(target, RequireVault()))
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, "Save the backup outside the vault folder.");
            }

            var folder = Path.GetDirectoryName(target) ?? throw new LibraryException(LibraryErrorCode.InvalidName, "Choose a file for the backup.");
            FileOps.CreateDirectory(folder);
            var id = Guid.NewGuid().ToString("N");
            var tempZip = Path.Combine(folder, NameRules.CabinetteTempPrefix + "backup-" + id + ".zip.tmp");
            var tempDb = Path.Combine(folder, NameRules.CabinetteTempPrefix + "backup-" + id + ".db.tmp");

            // Writes wait until the backup is done, so the snapshot and the vault files belong together.
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var vault = RequireVault();
                progress?.Report(new VaultOperationProgress("snapshot", 0, 0, 0, 0, null));
                var manifest = new BackupManifest
                {
                    CreatedAt = _time.GetUtcNow(),
                    MachineName = Environment.MachineName,
                    AppVersion = typeof(LibraryService).Assembly.GetName().Version?.ToString() ?? "",
                    SchemaVersion = Database.SchemaVersion,
                    VaultPath = vault,
                };
                using (var connection = _db.Open())
                {
                    // A consistent copy of the live (WAL) database.
                    connection.Execute("VACUUM INTO @path", new { path = tempDb });
                    manifest.CategoryCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM categories");
                    manifest.ItemCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM items");
                    manifest.PointerItemCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM items WHERE mode = 1");
                }

                var (directories, files) = ListVault(vault, ct);
                int included;
                long bytes;
                try
                {
                    (included, bytes) = BackupArchive.Write(tempZip, manifest, tempDb, directories, files, progress, ct);
                    File.Move(tempZip, target, overwrite: true);
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    throw FileOps.ToLibraryException(ex, target);
                }

                return new BackupResult(target, included, bytes, manifest.PointerItemCount);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                throw new LibraryException(LibraryErrorCode.Unknown, $"The library database could not be copied: {ex.Message}", ex);
            }
            finally
            {
                _writeLock.Release();
                FileOps.TryDelete(tempZip);
                FileOps.TryDelete(tempDb);
            }
        }, ct);
    }

    // ================================================================ restore (library side)

    /// <summary>
    /// After a restore: checks that the restored library only names folders and files inside the vault (a crafted or damaged
    /// backup is refused before anything is created from it), points the library at the new vault (the guard against changing
    /// the vault path doesn't apply), makes sure every category has its folder, and marks pointer items whose files aren't on
    /// this PC as missing.
    /// </summary>
    internal Task CompleteRestoreAsync(string vault, CancellationToken ct)
        => WriteAsync(ctx =>
        {
            RestoredLibraryCheck.Validate(
                ctx.Tree.All.ToList(),
                ctx.Query<(long, string)>("SELECT home_category_id, file_name FROM items WHERE mode <> 1"),
                vault);
            var saved = _settings with { VaultPath = vault };
            StoreSettings(ctx, saved);
            CreateVaultFolders(ctx, vault);
            foreach (var row in ItemReader.QueryRows(ctx.Connection, ctx.Transaction, "WHERE i.mode = 1", null))
            {
                var missing = string.IsNullOrEmpty(row.PointerPath) || !File.Exists(row.PointerPath) ? 1 : 0;
                if (missing != row.IsMissing)
                {
                    ctx.Execute("UPDATE items SET is_missing = @missing WHERE id = @id", new { missing, id = row.Id });
                }
            }

            ctx.OnCommitted(() => _settings = saved);
            ctx.Changes.Kind |= LibraryChangeKind.Settings | LibraryChangeKind.Categories | LibraryChangeKind.Items;
        }, ct);
}
