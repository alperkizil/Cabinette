using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Services;

/// <summary>
/// SQLite-backed <see cref="ILibraryService"/>. Split over several partial files by area.
/// Writes are serialized by <see cref="_writeLock"/> and run in one IMMEDIATE transaction each; reads use their own
/// pooled connections (WAL), so they run concurrently. All public methods do their work on the thread pool.
/// The AI activity log is the exception: its appends have a short transaction of their own (see LibraryService.Activity.cs).
/// </summary>
internal sealed partial class LibraryService : ILibraryService
{
    private const string SettingsKey = "app_settings";

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        // The font list will grow: a font from a newer build reads as the default font, not as unreadable settings.
        Converters = { new TolerantEnumConverter<AppFont>(), new JsonStringEnumConverter() },
    };

    private readonly Database _db;
    private readonly IRecycleBin _recycleBin;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly BackgroundHashQueue _hashQueue;
    private volatile AppSettings _settings;
    private volatile bool _disposed;

    private LibraryService(Database db, IRecycleBin recycleBin, TimeProvider time, AppSettings settings, IDateTakenReader? dateTakenReader)
    {
        _db = db;
        _recycleBin = recycleBin;
        _time = time;
        _settings = settings;
        _dateTakenReader = dateTakenReader;
        _hashQueue = new BackgroundHashQueue(ProcessPendingHashAsync);
    }

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public string? VaultPath => _settings.VaultPath;

    /// <summary>Pointer files larger than this are added without a hash and hashed in the background. Tests lower it.</summary>
    internal long BackgroundHashThresholdBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Completes when the background hash queue has nothing left to do (for tests).</summary>
    internal Task WhenBackgroundHashingIdleAsync() => _hashQueue.WhenIdleAsync();

    /// <summary>The sweep of the staging folder that <see cref="OpenAsync"/> starts in the background (it never fails).</summary>
    private Task _stagingSweep = Task.CompletedTask;

    /// <summary>Completes when the staging folder sweep started by opening the library is done (for tests).</summary>
    internal Task WhenStagingSweptAsync() => _stagingSweep;

    /// <summary>
    /// Opens the library. <paramref name="sweepStaging"/>: also empty the vault's staging folder of what interrupted copies left
    /// (in the background) and add the built-in subcategories of later releases; restoring a backup turns both off, since the
    /// database it opens still names the backup's old vault (the restore adds those subcategories once it names the new one).
    /// <paramref name="readDatesTaken"/>: start reading the dates taken still missing (in the background, when the options have a
    /// reader); restoring a backup starts that itself once the library names the new vault.
    /// </summary>
    internal static Task<LibraryService> OpenAsync(LibraryOptions options, IRecycleBin recycleBin, CancellationToken ct, bool sweepStaging = true,
        bool readDatesTaken = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);

        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var db = new Database(options.DataDirectory);
            db.Initialize(options.TimeProvider.GetUtcNow());

            AppSettings settings;
            List<long> unhashed;
            using (var connection = db.Open())
            {
                settings = LoadSettings(connection);
                unhashed = connection.Query<long>("SELECT id FROM items WHERE mode = 1 AND hash IS NULL ORDER BY id").AsList();
            }

            var service = new LibraryService(db, recycleBin, options.TimeProvider, settings, options.DateTakenReader);

            // Built-in subcategories of later releases reach an existing library once.
            if (sweepStaging)
            {
                await service.AddLaterBuiltInsAsync(ct).ConfigureAwait(false);
            }

            foreach (var id in unhashed)
            {
                service._hashQueue.Enqueue(id);
            }

            if (sweepStaging && settings.VaultPath is { } vault)
            {
                // Real time, not the library's clock: it is compared with file creation times.
                var openedAt = DateTime.UtcNow;
                service._stagingSweep = Task.Run(() => SweepStagingFolder(vault, openedAt), CancellationToken.None);
            }

            // AI activity entries older than a year go, in the background too (also for a restored library).
            service.StartActivityPrune();
            if (readDatesTaken)
            {
                service.StartReadingDatesTaken();
            }

            return service;
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // The monitor writes through this library, so it stops first.
        VaultMonitor? monitor;
        lock (_monitorGate)
        {
            monitor = _monitor;
        }

        if (monitor is not null)
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
        await _hashQueue.DisposeAsync().ConfigureAwait(false);
        await StopReadingDatesTakenAsync().ConfigureAwait(false);

        // Let an in-flight write finish before releasing the database, and an activity append or prune batch (they don't take
        // the write lock). Both check _disposed once they hold their lock, so nothing opens the database after this.
        await _writeLock.WaitAsync().ConfigureAwait(false);
        await _activityLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _db.ClearPool();
        }
        finally
        {
            _activityLock.Release();
            _writeLock.Release();
        }
    }

    // ================================================================ settings & vault

    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.FromResult(_settings);
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return WriteAsync(ctx =>
        {
            var current = _settings;
            var requestedVault = string.IsNullOrWhiteSpace(settings.VaultPath) ? null : NormalizeVaultPath(settings.VaultPath);
            if (current.VaultPath is not null && requestedVault is not null && !FileOps.PathsEqual(current.VaultPath, requestedVault))
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, "The vault location can't be changed here.");
            }

            var vault = current.VaultPath ?? requestedVault;
            if (current.VaultPath is null && vault is not null)
            {
                EnsureVaultLeavesOutDataFolder(vault);
                CreateVaultFolders(ctx, vault);
            }

            var saved = settings with { VaultPath = vault };
            StoreSettings(ctx, saved);
            ctx.OnCommitted(() => _settings = saved);
            ctx.Changes.Kind |= LibraryChangeKind.Settings;
        }, ct);
    }

    public Task InitializeVaultAsync(string vaultPath, CancellationToken ct = default)
    {
        return WriteAsync(ctx =>
        {
            var vault = NormalizeVaultPath(vaultPath);
            var current = _settings;
            if (current.VaultPath is not null && !FileOps.PathsEqual(current.VaultPath, vault))
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, "A vault is already set up. Moving the vault is not supported yet.");
            }

            EnsureVaultLeavesOutDataFolder(vault);
            CreateVaultFolders(ctx, vault);
            var saved = current with { VaultPath = vault };
            StoreSettings(ctx, saved);
            ctx.OnCommitted(() => _settings = saved);
            ctx.Changes.Kind |= LibraryChangeKind.Settings;
            ctx.Changes.Categories(ctx.Tree, ctx.Tree.All.Select(c => c.Id).ToList());
        }, ct);
    }

    internal static string NormalizeVaultPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "Choose a full folder path for the vault, for example D:\\Cabinette.");
        }

        try
        {
            return FileOps.Normalize(path.Trim());
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, path);
        }
    }

    /// <summary>
    /// The vault monitor would take Cabinette's own files (thumbnails, logs, the database) in as library items. The same check as
    /// restoring a backup; it also rules out a whole user profile or the root of the data folder's drive.
    /// </summary>
    private void EnsureVaultLeavesOutDataFolder(string vault)
    {
        if (FileOps.IsSameOrInside(_db.DataDirectory, vault))
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "The vault can't contain Cabinette's data folder.");
        }
    }

    private static void CreateVaultFolders(WriteContext ctx, string vault)
    {
        ctx.EnsureDirectory(vault);
        foreach (var category in ctx.Tree.All)
        {
            ctx.EnsureDirectory(ctx.Tree.FolderPath(vault, category.Id));
        }
    }

    private static AppSettings LoadSettings(SqliteConnection connection)
    {
        var json = connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @k", new { k = SettingsKey });
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, SettingsJsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Cabinette: settings could not be read, using defaults: {ex}");
            return new AppSettings();
        }
    }

    private static void StoreSettings(WriteContext ctx, AppSettings settings)
        => ctx.Execute(
            "INSERT INTO settings(key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { k = SettingsKey, v = JsonSerializer.Serialize(settings, SettingsJsonOptions) });

    private string RequireVault()
        => _settings.VaultPath ?? throw new LibraryException(LibraryErrorCode.VaultNotConfigured, "Choose a vault folder first.");

    // ================================================================ infrastructure

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Runs a read on the thread pool with its own pooled connection.</summary>
    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> body, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _db.Open();
            return body(connection);
        }, ct);
    }

    /// <summary>Synchronous read for code that already runs on the thread pool.</summary>
    private T ReadNow<T>(Func<SqliteConnection, T> body)
    {
        ThrowIfDisposed();
        using var connection = _db.Open();
        return body(connection);
    }

    private Task WriteAsync(Action<WriteContext> body, CancellationToken ct)
        => WriteAsync<bool>(ctx =>
        {
            body(ctx);
            return true;
        }, ct);

    /// <summary>Runs a write on the thread pool, then raises <see cref="Changed"/> (outside the lock).</summary>
    private Task<T> WriteAsync<T>(Func<WriteContext, T> body, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.Run(() => WriteCoreAsync(body, ct, accumulate: null), ct);
    }

    /// <summary>
    /// One serialized write transaction. If <paramref name="accumulate"/> is given the changes are merged into it
    /// instead of being raised (the caller raises them later).
    /// </summary>
    private async Task<T> WriteCoreAsync<T>(Func<WriteContext, T> body, CancellationToken ct, ChangeSet? accumulate)
    {
        ThrowIfDisposed();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        T result;
        WriteContext ctx;
        try
        {
            ThrowIfDisposed();
            (result, ctx) = RunTransaction(body);
        }
        finally
        {
            _writeLock.Release();
        }

        return FinishWrite(result, ctx, accumulate);
    }

    /// <summary>Runs <paramref name="body"/> in one IMMEDIATE transaction. The caller must hold <see cref="_writeLock"/>.</summary>
    private (T Result, WriteContext Context) RunTransaction<T>(Func<WriteContext, T> body)
    {
        try
        {
            using var connection = _db.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var ctx = new WriteContext(connection, tx, _settings.VaultPath, _time.GetUtcNow());
            T result;
            try
            {
                result = body(ctx);
                tx.Commit();
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch (Exception rollbackError)
                {
                    Debug.WriteLine($"Cabinette: rollback failed: {rollbackError}");
                }

                ctx.Undo();
                throw;
            }

            ctx.RunPostCommit();
            return (result, ctx);
        }
        catch (SqliteException ex)
        {
            throw new LibraryException(LibraryErrorCode.Unknown, $"The library database reported an error: {ex.Message}", ex);
        }
    }

    /// <summary>After the lock is released: raises (or accumulates) the changes and surfaces a post-commit error.</summary>
    private T FinishWrite<T>(T result, WriteContext ctx, ChangeSet? accumulate)
    {
        if (accumulate is not null)
        {
            accumulate.MergeFrom(ctx.Changes);
        }
        else
        {
            Raise(ctx.Changes);
        }

        if (ctx.PostCommitError is { } error)
        {
            throw error is LibraryException ? error : new LibraryException(LibraryErrorCode.Unknown, error.Message, error);
        }

        return result;
    }

    private void Raise(ChangeSet changes)
    {
        if (changes.IsEmpty)
        {
            return;
        }

        // Every changed item may be a new photo or video, or one whose file changed: the date-taken reader checks them.
        QueueDateTakenChecks(changes.ItemIds);
        RaiseChanged(changes);
    }

    /// <summary>Raises <see cref="Changed"/> for <paramref name="changes"/> (not empty), shielding the caller from failing subscribers.</summary>
    private void RaiseChanged(ChangeSet changes)
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        var args = changes.ToEventArgs();
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<LibraryChangedEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                // A failing subscriber must not break the library or the caller's operation.
                Debug.WriteLine($"Cabinette: Changed handler threw: {ex}");
            }
        }
    }
}
