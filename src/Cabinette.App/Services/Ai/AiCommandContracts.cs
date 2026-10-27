using Cabinette.Core.Models;

namespace Cabinette.App.Services.Ai;

// What the "ai" command handler (AiCommandHandler) needs from the running app. The app implements these with its window,
// AppLifetime and VaultOperationsService; tests use fakes.

/// <summary>The running app as the "ai" command handler sees it: lifetime, the window and the UI thread.</summary>
public interface IAiCommandHost
{
    /// <summary>True once Cabinette is quitting or restarting (calls are answered "not_ready").</summary>
    bool IsQuitting { get; }

    /// <summary>True while a long vault operation runs (move, migrate, backup or restore, from Settings or an AI).</summary>
    Task<bool> IsOperationRunningAsync();

    /// <summary>Brings the main window up (also from the tray) and opens the category's thread, scrolled to the item when one is given.</summary>
    Task ShowAsync(long categoryId, long? focusItemId);

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action);
}

/// <summary>How a vault operation started by an AI ended.</summary>
public enum AiVaultOperationState
{
    Succeeded,
    Failed,

    /// <summary>The user cancelled it in the progress dialog (or Cabinette closed the dialog).</summary>
    Cancelled,
}

/// <summary>The end of a vault operation started by an AI.</summary>
public sealed record AiVaultOperationOutcome(AiVaultOperationState State)
{
    /// <summary>Why it failed or stopped, in English for the AI.</summary>
    public string? Error { get; init; }

    /// <summary>Why it failed or stopped, for the user (the notice): a localized text or the library's message.</summary>
    public string? UserMessage { get; init; }

    public MoveVaultResult? Moved { get; init; }

    public MigrateResult? Migrated { get; init; }

    public BackupResult? Backup { get; init; }
}

/// <summary>The answer to starting a vault operation: <see cref="Completion"/> when it started, else an error code and message.</summary>
public sealed record AiVaultOperationStart
{
    /// <summary>Completes when the operation is over (never faults).</summary>
    public Task<AiVaultOperationOutcome>? Completion { get; init; }

    /// <summary>One of <see cref="Cabinette.Ipc.AiErrorCodes"/> when it didn't start.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Why it didn't start, in English for the AI.</summary>
    public string? Error { get; init; }

    public static AiVaultOperationStart Started(Task<AiVaultOperationOutcome> completion) => new() { Completion = completion };

    public static AiVaultOperationStart Refused(string code, string error) => new() { ErrorCode = code, Error = error };
}

/// <summary>
/// Move vault, Migrate and Backup started by an AI (SPEC.md section 10): no folder picker and no confirmation, the same checks as
/// the UI, the usual progress dialog (which brings the window up, and where the user can cancel) and no result dialog. Each
/// marks the operation as running before it returns, so a second start is refused as busy at once. Call on the UI thread.
/// </summary>
public interface IAiVaultOperations
{
    /// <param name="status">Also receives the operation's progress (for get_operation_status).</param>
    Task<AiVaultOperationStart> StartMoveVaultForAiAsync(string newVaultPath, IProgress<VaultOperationProgress> status);

    Task<AiVaultOperationStart> StartMigrateForAiAsync(IProgress<VaultOperationProgress> status);

    /// <summary>Writes a backup without migrating linked files first.</summary>
    Task<AiVaultOperationStart> StartBackupForAiAsync(string zipPath, IProgress<VaultOperationProgress> status);
}
