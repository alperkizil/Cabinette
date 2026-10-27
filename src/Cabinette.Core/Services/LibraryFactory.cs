using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Services;

public sealed record LibraryOptions
{
    /// <summary>Folder for the database (normally %LocalAppData%\Cabinette). Created if missing.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Clock, replaceable in tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Reads photos' and videos' date taken (<see cref="LibraryItem.TakenAt"/>). Null: dates taken are never read.</summary>
    public IDateTakenReader? DateTakenReader { get; init; }
}

public static class LibraryFactory
{
    /// <summary>
    /// Opens (creating and migrating if needed) the library database at <c>DataDirectory\cabinette.db</c>
    /// and seeds the built-in categories on first run.
    /// </summary>
    public static async Task<ILibraryService> OpenAsync(LibraryOptions options, IRecycleBin recycleBin, CancellationToken ct = default)
        => await LibraryService.OpenAsync(options, recycleBin, ct).ConfigureAwait(false);

    /// <summary>Reads a backup's manifest without extracting it (shown to the user before restoring). Throws LibraryException if it is not a Cabinette backup.</summary>
    public static Task<BackupInfo> ReadBackupInfoAsync(string zipPath, CancellationToken ct = default)
        => BackupArchive.ReadInfoAsync(zipPath, ct);

    /// <summary>
    /// Restores a backup made by <see cref="ILibraryService.CreateBackupAsync"/>: extracts the vault into
    /// <paramref name="newVaultPath"/> (must be empty or not exist) and the database into <c>DataDirectory</c>, then points the
    /// library at the new vault. An existing database is first saved as <c>cabinette.db.bak-yyyyMMdd-HHmmss</c>.
    /// The caller must have closed (disposed) any open library for that data directory. Returns the opened library.
    /// </summary>
    public static Task<ILibraryService> RestoreAsync(
        string zipPath,
        string newVaultPath,
        LibraryOptions options,
        IRecycleBin recycleBin,
        IProgress<VaultOperationProgress>? progress = null,
        CancellationToken ct = default)
        => BackupArchive.RestoreAsync(zipPath, newVaultPath, options, recycleBin, progress, ct);
}
