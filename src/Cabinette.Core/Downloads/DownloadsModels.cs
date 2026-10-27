using Cabinette.Core.Models;

namespace Cabinette.Core.Downloads;

/// <summary>Settings for one <see cref="DownloadsCleaner"/> (Clean My Downloads mode, SPEC.md section 5).</summary>
public sealed record DownloadsCleanerOptions
{
    /// <summary>The folder whose top level is cleaned (the Windows Downloads folder, or a dev run's <c>--downloads-dir</c>).</summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Files created at or before this moment (UTC) are never touched: they were there before the mode was switched on, or
    /// they were already dealt with. The cleaner moves it forward (<see cref="DownloadsCleaner.WatermarkChanged"/>), never past
    /// a file it still has to handle, so a file that arrives while Cabinette is closed is handled at the next start.
    /// </summary>
    public required DateTimeOffset Watermark { get; init; }

    /// <summary>A file moves once nothing changed it for this long (so the browser's "Open" right after downloading still works).</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A file open in another program is looked at again after this long.</summary>
    public TimeSpan InUseRetryDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A file that couldn't be added is tried again after this long. When the vault copy fails its check or the Recycle Bin
    /// fails, the delay doubles with each attempt, up to <see cref="MaxFailureRetryDelay"/>.
    /// </summary>
    public TimeSpan FailureRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest a doubled retry delay gets (see <see cref="FailureRetryDelay"/>).</summary>
    public TimeSpan MaxFailureRetryDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often a file is added before Cabinette gives up because the vault copy keeps failing its check (the vault file
    /// doesn't have the content the library stored for it). The file then stays in Downloads (<see cref="DownloadKeptReason.VaultCopyUnchecked"/>).
    /// </summary>
    public int VaultCheckAttempts { get; init; } = 3;

    /// <summary>
    /// A partial download (<c>.crdownload</c>, <c>.part</c> and so on) that nobody wrote to or held open for this long counts as
    /// abandoned: it no longer holds the watermark back, so files kept after it aren't looked at again at every start.
    /// </summary>
    public TimeSpan AbandonedPartialAge { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gives a folder's final path (junctions and symbolic links followed, short names expanded), or null when it can't. Used
    /// to tell whether the Downloads folder is the vault or inside it. Null: links are followed with managed APIs only.
    /// </summary>
    public Func<string, string?>? ResolveFinalPath { get; init; }

    /// <summary>Changes in the folder are collected for this long before they are looked at (a download raises a burst of events).</summary>
    public TimeSpan EventDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The folder is looked at least this often, also without events (missed events, a folder that can't be watched).</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromMinutes(5);
}

public enum DownloadOutcomeKind
{
    /// <summary>Copied into the vault (sorted by type), checked, and the original sent to the Recycle Bin.</summary>
    Moved = 0,

    /// <summary>Its content was already in the vault: no second copy was stored, and the original was sent to the Recycle Bin.</summary>
    AlreadyInLibrary = 1,

    /// <summary>The original stays in the Downloads folder (see <see cref="DownloadOutcome.Reason"/>). It is not tried again.</summary>
    Kept = 2,

    /// <summary>
    /// It couldn't be moved yet (see <see cref="DownloadOutcome.Message"/>): adding it, checking the vault copy or sending the
    /// original to the Recycle Bin failed. The original stays and is tried again later.
    /// </summary>
    Failed = 3,
}

/// <summary>Why a file stayed in the Downloads folder.</summary>
public enum DownloadKeptReason
{
    None = 0,

    /// <summary>It is in the vault, but the original can't go to the Recycle Bin (its drive has none, or it is too large for it).</summary>
    NoRecycleBin = 1,

    /// <summary>
    /// It is in the vault, but sending the original to the Recycle Bin failed (see <see cref="DownloadOutcome.Message"/>).
    /// No longer given by the cleaner: such a failure is retried (and reported once as <see cref="DownloadOutcomeKind.Failed"/>).
    /// </summary>
    RecycleFailed = 2,

    /// <summary>
    /// Its content is in the library only as a pointer item (a file outside the vault, maybe this very file), so the vault
    /// doesn't hold it and the original stays.
    /// </summary>
    LinkedAsPointer = 3,

    /// <summary>
    /// The vault file of the item that holds its content kept failing the check (it doesn't have the content the library stored
    /// for it), so Cabinette can't be sure the vault has this file and the original stays.
    /// </summary>
    VaultCopyUnchecked = 4,
}

/// <summary>What happened to one file of the Downloads folder.</summary>
/// <param name="SourcePath">The file in the Downloads folder.</param>
/// <param name="Item">
/// Moved: the new library item. AlreadyInLibrary and Kept: the item that holds its content (its home category is where it is).
/// Failed: null, or the item that holds its content when only sending the original to the Recycle Bin failed.
/// </param>
public sealed record DownloadOutcome(
    string SourcePath,
    DownloadOutcomeKind Kind,
    LibraryItem? Item = null,
    DownloadKeptReason Reason = DownloadKeptReason.None,
    string? Message = null);

/// <summary>
/// The files one look at the Downloads folder dealt with. A file that couldn't be added is listed the first time only, and
/// files put off for later (still changing, or open in another program) aren't listed.
/// </summary>
public sealed record DownloadsBatch(IReadOnlyList<DownloadOutcome> Outcomes)
{
    public static DownloadsBatch Empty { get; } = new([]);

    public bool IsEmpty => Outcomes.Count == 0;
}
