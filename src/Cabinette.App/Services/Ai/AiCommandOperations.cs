using System.Globalization;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// The vault operations AI clients started since Cabinette started (in memory): id → status, for get_operation_status.
/// Thread-safe.
/// </summary>
public sealed class AiCommandOperations
{
    /// <summary>How many operations are remembered; the oldest finished ones are forgotten first.</summary>
    internal const int MaxKept = 20;

    private readonly Lock _gate = new();
    private readonly List<AiCommandOperation> _operations = [];
    private readonly TimeProvider _time;

    public AiCommandOperations(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A new operation of <paramref name="kind"/> (the tool's name), not listed until <see cref="Add"/>.</summary>
    public AiCommandOperation Create(string kind) => new("op-" + Guid.NewGuid().ToString("N")[..12], kind, _time);

    /// <summary>Lists a started operation; it is now the latest.</summary>
    public void Add(AiCommandOperation operation)
    {
        lock (_gate)
        {
            _operations.Add(operation);
            while (_operations.Count > MaxKept && _operations.FindIndex(o => o.IsFinished) is var index and >= 0)
            {
                _operations.RemoveAt(index);
            }
        }
    }

    /// <summary>The operation with that id, or the latest one when <paramref name="id"/> is null; null when there is none.</summary>
    public OperationStatus? Find(string? id)
    {
        AiCommandOperation? operation;
        lock (_gate)
        {
            operation = id is null
                ? _operations.LastOrDefault()
                : _operations.LastOrDefault(o => string.Equals(o.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return operation?.Snapshot();
    }
}

/// <summary>One vault operation an AI started: its progress (it is the operation's <see cref="IProgress{T}"/>) and its end.</summary>
public sealed class AiCommandOperation : IProgress<VaultOperationProgress>
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private VaultOperationProgress? _progress;
    private OperationStatus? _final;

    internal AiCommandOperation(string id, string kind, TimeProvider time)
    {
        Id = id;
        Kind = kind;
        _time = time;
        StartedAt = time.GetLocalNow();
    }

    public string Id { get; }

    /// <summary>The tool that started it.</summary>
    public string Kind { get; }

    public DateTimeOffset StartedAt { get; }

    public bool IsFinished
    {
        get
        {
            lock (_gate)
            {
                return _final is not null;
            }
        }
    }

    public void Report(VaultOperationProgress value)
    {
        lock (_gate)
        {
            if (_final is null)
            {
                _progress = value;
            }
        }
    }

    /// <summary>Records how it ended (once; later calls are ignored).</summary>
    public void Complete(AiVaultOperationOutcome outcome)
    {
        var finishedAt = _time.GetLocalNow();
        lock (_gate)
        {
            if (_final is not null)
            {
                return;
            }

            var status = Running() with
            {
                State = outcome.State switch
                {
                    AiVaultOperationState.Succeeded => "succeeded",
                    AiVaultOperationState.Cancelled => "cancelled",
                    _ => "failed",
                },
                FinishedAt = finishedAt,
                Error = outcome.State == AiVaultOperationState.Succeeded ? null : outcome.Error ?? "The operation stopped.",
            };

            if (outcome.State == AiVaultOperationState.Succeeded)
            {
                status = status with { Stage = "done" };
            }

            if (outcome.Moved is { } moved)
            {
                status = status with
                {
                    FilesDone = moved.FilesCopied,
                    FilesTotal = moved.FilesCopied,
                    BytesDone = moved.BytesCopied,
                    BytesTotal = moved.BytesCopied,
                    OldVaultPath = moved.OldVaultPath,
                    Path = moved.NewVaultPath,
                    Summary = $"{AiCommandConversions.FilesAndSize(moved.FilesCopied, moved.BytesCopied)} copied to {moved.NewVaultPath}, "
                              + $"which is the vault now. The old vault {moved.OldVaultPath} is kept.",
                };
            }

            if (outcome.Migrated is { } migrated)
            {
                var failures = migrated.SkippedMissing
                    .Select(i => new AiFailure(i.FullPath, "The linked file is missing; it stays linked."))
                    .Concat(migrated.Failures.Select(f => new AiFailure(f.SourcePath, f.Message)))
                    .ToList();
                var summary = $"{Count(migrated.Migrated, "linked file")} copied into the vault.";
                if (migrated.SkippedMissing.Count > 0)
                {
                    summary += $" {Count(migrated.SkippedMissing.Count, "missing file")} skipped.";
                }

                if (migrated.Failures.Count > 0)
                {
                    summary += $" {Count(migrated.Failures.Count, "file")} failed.";
                }

                status = status with { Summary = summary, Failures = failures };
            }

            if (outcome.Backup is { } backup)
            {
                var summary = $"{AiCommandConversions.FilesAndSize(backup.FilesIncluded, backup.BytesIncluded)} saved in {backup.ZipPath}.";
                if (backup.PointerItemsNotIncluded > 0)
                {
                    summary += $" {Count(backup.PointerItemsNotIncluded, "linked file")} left out (the backup only has their paths).";
                }

                status = status with
                {
                    FilesDone = backup.FilesIncluded,
                    FilesTotal = backup.FilesIncluded,
                    BytesDone = backup.BytesIncluded,
                    BytesTotal = backup.BytesIncluded,
                    Path = backup.ZipPath,
                    Summary = summary,
                };
            }

            _final = status;
        }
    }

    public OperationStatus Snapshot()
    {
        lock (_gate)
        {
            return _final ?? Running();
        }
    }

    /// <summary>The status while running (call inside the lock).</summary>
    private OperationStatus Running()
    {
        var progress = _progress;
        return new OperationStatus
        {
            OperationId = Id,
            Kind = Kind,
            State = "running",
            StartedAt = StartedAt,
            Stage = progress?.Stage,
            FilesDone = progress is { TotalFiles: > 0 } ? Math.Min(progress.ProcessedFiles, progress.TotalFiles) : null,
            FilesTotal = progress is { TotalFiles: > 0 } ? progress.TotalFiles : null,
            BytesDone = progress is { TotalBytes: > 0 } ? Math.Min(progress.BytesProcessed, progress.TotalBytes) : null,
            BytesTotal = progress is { TotalBytes: > 0 } ? progress.TotalBytes : null,
        };
    }

    private static string Count(int count, string noun) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";
}
