using System.IO;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

// Long vault operations (SPEC.md section 10, decided 2026-10-23): they start and answer at once with an operation id; the AI
// follows them with get_operation_status while Cabinette shows its usual progress dialog. One at a time ("busy").
public sealed partial class AiCommandHandler
{
    private Task<object> MoveVaultAsync(AiCall call)
    {
        var path = Args<MoveVaultArgs>(call).NewPath;
        call.Details = call.Details with { Paths = [path ?? ""] };
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw AiCallException.Invalid("newPath must be the full path of an empty or new folder.");
        }

        EnsureNotDevicePath(path, "newPath");

        return StartOperationAsync(call, AiTools.MoveVault, status => _vault.StartMoveVaultForAiAsync(path, status));
    }

    private Task<object> MigratePointersAsync(AiCall call)
        => StartOperationAsync(call, AiTools.MigratePointers, _vault.StartMigrateForAiAsync);

    private Task<object> CreateBackupAsync(AiCall call)
    {
        var path = Args<CreateBackupArgs>(call).ZipPath;
        call.Details = call.Details with { Paths = [path ?? ""] };
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !path.TrimEnd().EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw AiCallException.Invalid("zipPath must be the full path of a new .zip file.");
        }

        EnsureNotDevicePath(path, "zipPath");

        return StartOperationAsync(call, AiTools.CreateBackup, status => _vault.StartBackupForAiAsync(path, status));
    }

    /// <summary>
    /// A device path ("\\?\C:\...", "\\.\C:\...", "\??\...") skips the normalizing Windows does for plain paths ("..", trailing
    /// dots and spaces) and can name the vault in ways the "not inside the vault" checks don't expect, so the vault operations only
    /// take plain full paths. (The checks also compare the final paths Windows resolves, which see through such spellings.)
    /// </summary>
    private static void EnsureNotDevicePath(string path, string what)
    {
        if (IsDevicePath(path))
        {
            throw AiCallException.Invalid(
                $@"{what} can't be a device path (\\?\... or \\.\...). Give the plain full path, such as D:\Backups\cabinette.zip.");
        }
    }

    /// <summary>"\\?\" or "\\.\" (either slash) or "\??\" at the start: .NET's own rule for device paths.</summary>
    internal static bool IsDevicePath(string path)
    {
        var p = path.AsSpan().TrimStart();
        return p.Length >= 4
               && ((IsSeparator(p[0]) && IsSeparator(p[1]) && (p[2] is '.' or '?') && IsSeparator(p[3]))
                   || p.StartsWith(@"\??\", StringComparison.Ordinal));

        static bool IsSeparator(char c) => c is '\\' or '/';
    }

    /// <summary>
    /// Starts the operation on the UI thread (which marks it as running before this answers, so a second call is busy at once),
    /// lists it for get_operation_status and follows it to its end in the background.
    /// </summary>
    private async Task<object> StartOperationAsync(AiCall call, string kind, Func<IProgress<VaultOperationProgress>, Task<AiVaultOperationStart>> start)
    {
        var operation = _operations.Create(kind);
        var started = await _host.InvokeOnUiAsync(() => start(operation)).ConfigureAwait(false);
        if (started.Completion is not { } completion)
        {
            throw new AiCallException(started.ErrorCode ?? AiErrorCodes.Failed, started.Error ?? "The operation could not be started.");
        }

        _operations.Add(operation);
        call.Details = call.Details with { OperationId = operation.Id };
        AppLog.Info($"{call.Client} started {kind} ({operation.Id}).");
        _ = FollowOperationAsync(call.Client, operation, completion);
        return new OperationStarted(operation.Id, kind);
    }

    /// <summary>Records the end for get_operation_status and reports it (the notice replaces the result dialog).</summary>
    private async Task FollowOperationAsync(string client, AiCommandOperation operation, Task<AiVaultOperationOutcome> completion)
    {
        AiVaultOperationOutcome outcome;
        try
        {
            outcome = await completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"The vault operation {operation.Id} ({operation.Kind}) failed unexpectedly.", ex);
            outcome = new AiVaultOperationOutcome(AiVaultOperationState.Failed) { Error = $"Something went wrong: {ex.Message}", UserMessage = ex.Message };
        }

        operation.Complete(outcome);
        AppLog.Info($"{client}'s {operation.Kind} ({operation.Id}) ended: {outcome.State}.");
        Report(OperationChange(client, operation.Kind, outcome));
    }

    internal static AiChange OperationChange(string client, string kind, AiVaultOperationOutcome outcome)
    {
        if (outcome.State != AiVaultOperationState.Succeeded)
        {
            return new AiChange(client, AiChangeKind.OperationFailed, 0) { Detail = outcome.UserMessage ?? outcome.Error };
        }

        return kind switch
        {
            AiTools.MoveVault => new AiChange(client, AiChangeKind.VaultMoved, outcome.Moved?.FilesCopied ?? 0),
            AiTools.MigratePointers => new AiChange(client, AiChangeKind.PointersMigrated, outcome.Migrated?.Migrated ?? 0)
            {
                Failures = (outcome.Migrated?.Failures.Count ?? 0) + (outcome.Migrated?.SkippedMissing.Count ?? 0),
            },
            _ => new AiChange(client, AiChangeKind.BackupCreated, outcome.Backup?.FilesIncluded ?? 0),
        };
    }
}
