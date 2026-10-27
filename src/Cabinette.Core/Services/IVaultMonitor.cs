using Cabinette.Core.Models;

namespace Cabinette.Core.Services;

/// <summary>
/// Watches the vault folder and applies changes made outside Cabinette to the library (SPEC.md section 8).
/// Created by <see cref="ILibraryService.CreateVaultMonitor"/>; one per library. Pointer files are never watched.
/// Library changes it makes are raised through <see cref="ILibraryService.Changed"/> with Source = VaultMonitor.
/// All events are raised on background threads.
/// </summary>
public interface IVaultMonitor : IAsyncDisposable
{
    VaultMonitorStatus Status { get; }

    event EventHandler<VaultMonitorStatus>? StatusChanged;

    /// <summary>Raised after each applied batch that changed something (for notifications).</summary>
    event EventHandler<VaultChangeSummary>? ChangesApplied;

    /// <summary>Rescans the vault (startup rescan), then keeps watching. Returns once the rescan finished.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops applying changes until <see cref="ResumeAsync"/>.</summary>
    Task PauseAsync();

    /// <summary>Rescans, then watches again.</summary>
    Task ResumeAsync(CancellationToken ct = default);

    /// <summary>Full reconcile of the vault against the library (also used after a watcher buffer overflow).</summary>
    Task RescanAsync(CancellationToken ct = default);
}
