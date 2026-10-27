namespace Cabinette.Core.Models;

public enum VaultMonitorState
{
    /// <summary>Not started yet, or disposed.</summary>
    Stopped = 0,

    /// <summary>Watching; the library matches the vault.</summary>
    Idle = 1,

    /// <summary>Applying changes (startup rescan, or a batch of file-system events).</summary>
    Syncing = 2,

    /// <summary>Paused by the user. Nothing is applied until resumed (which rescans).</summary>
    Paused = 3,

    /// <summary>The vault folder is missing or unreadable (e.g. a disconnected drive). Nothing is removed; retried every 30 s.</summary>
    VaultUnavailable = 4,
}

/// <param name="PendingChanges">File-system changes waiting to be applied (0 when idle).</param>
/// <param name="LastSyncAt">When the library last matched the vault.</param>
/// <param name="Message">A short, user-readable explanation for <see cref="VaultMonitorState.VaultUnavailable"/>.</param>
public sealed record VaultMonitorStatus(VaultMonitorState State, int PendingChanges, DateTimeOffset? LastSyncAt, string? Message);

/// <summary>What one batch of vault changes did (drives the grouped tray notifications, e.g. "3 files added to Images").</summary>
public sealed record VaultChangeSummary
{
    /// <summary>Files added, per category (name → count).</summary>
    public IReadOnlyDictionary<string, int> AddedByCategory { get; init; } = new Dictionary<string, int>();

    public int Removed { get; init; }

    public int Renamed { get; init; }

    /// <summary>Files moved from one category folder to another.</summary>
    public int Moved { get; init; }

    public int Modified { get; init; }

    /// <summary>Names of categories created from new folders.</summary>
    public IReadOnlyList<string> CategoriesCreated { get; init; } = [];

    public IReadOnlyList<string> CategoriesRemoved { get; init; } = [];

    /// <summary>New files whose content was already in the library (kept as their own items).</summary>
    public IReadOnlyList<DuplicateInfo> Duplicates { get; init; } = [];

    public bool IsEmpty =>
        AddedByCategory.Count == 0 && Removed == 0 && Renamed == 0 && Moved == 0 && Modified == 0 &&
        CategoriesCreated.Count == 0 && CategoriesRemoved.Count == 0 && Duplicates.Count == 0;
}

/// <summary>Progress of a long vault operation (move, migrate, backup, restore).</summary>
/// <param name="Stage">Short English stage name for logs ("copying", "verifying", "zipping", ...). The UI shows its own text.</param>
public sealed record VaultOperationProgress(string Stage, int ProcessedFiles, int TotalFiles, long BytesProcessed, long TotalBytes, string? CurrentFile);

public sealed record MoveVaultResult(string OldVaultPath, string NewVaultPath, int FilesCopied, long BytesCopied);

/// <param name="Migrated">Pointer items that are now copy-mode items in the vault.</param>
/// <param name="SkippedMissing">Pointer items whose file could not be found (left as they are).</param>
public sealed record MigrateResult(int Migrated, IReadOnlyList<LibraryItem> SkippedMissing, IReadOnlyList<AddFailure> Failures);

/// <param name="PointerItemsNotIncluded">Pointer items in the library whose files are not in the backup (only their paths are).</param>
public sealed record BackupResult(string ZipPath, int FilesIncluded, long BytesIncluded, int PointerItemsNotIncluded);

/// <summary>A backup's manifest, shown before restoring.</summary>
public sealed record BackupInfo(DateTimeOffset CreatedAt, string MachineName, int FileCount, long TotalBytes, int CategoryCount, int PointerItems, int FormatVersion);
