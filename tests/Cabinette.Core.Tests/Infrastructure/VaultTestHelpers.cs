using Cabinette.Core.Models;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Tests.Infrastructure;

/// <summary>Helpers for vault monitor and vault operation tests.</summary>
internal static class VaultTestHelpers
{
    public static string VaultPath(this TestLibrary t, string relative) => Path.Combine(t.Vault, relative);

    /// <summary>Writes a file straight into the vault (as another program would) and returns its full path.</summary>
    public static string WriteVaultFile(this TestLibrary t, string relative, string content, DateTime? lastWriteUtc = null)
    {
        var path = t.VaultPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (lastWriteUtc is { } time)
        {
            File.SetLastWriteTimeUtc(path, time);
        }

        return path;
    }

    /// <summary>Every item with a known size (all vault files), newest first.</summary>
    public static async Task<IReadOnlyList<LibraryItem>> AllItemsAsync(this TestLibrary t)
        => (await t.Library.SearchAsync("", new ItemFilter { MinSizeBytes = 0 })).Items;

    /// <summary>Full reconcile of the vault (what the startup rescan does), without a watcher.</summary>
    public static async Task<VaultChangeSummary> RescanAsync(this TestLibrary t)
    {
        var outcome = await t.Service.ReconcileAsync(ReconcileRequest.Rescan(), CancellationToken.None);
        Assert.False(outcome.VaultUnavailable, outcome.Message);
        return outcome.Summary;
    }

    /// <summary>One watcher batch: the given folders (relative, "" = vault root) changed, with optional rename hints.</summary>
    public static async Task<VaultChangeSummary> ApplyBatchAsync(this TestLibrary t, string[] dirtyFolders, params (string Old, string New)[] renames)
    {
        var outcome = await t.Service.ReconcileAsync(new ReconcileRequest { DirtyFolders = dirtyFolders, Renames = renames }, CancellationToken.None);
        Assert.False(outcome.VaultUnavailable, outcome.Message);
        return outcome.Summary;
    }

    /// <summary>The library's monitor with test timings (short quiet period and retry interval).</summary>
    public static VaultMonitor FastMonitor(this TestLibrary t)
    {
        var monitor = (VaultMonitor)t.Library.CreateVaultMonitor();
        monitor.QuietPeriod = TimeSpan.FromMilliseconds(150);
        monitor.MaxBatchDelay = TimeSpan.FromSeconds(1);
        monitor.UnavailableRetryInterval = TimeSpan.FromMilliseconds(200);
        monitor.DeferredRetryDelay = TimeSpan.FromMilliseconds(200);
        monitor.LockRetryDelays = [TimeSpan.FromMilliseconds(20)];
        return monitor;
    }

    /// <summary>Polls until <paramref name="condition"/> holds (real file-system events arrive asynchronously).</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(25);
        }
    }

    public static Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
        => WaitUntilAsync(() => Task.FromResult(condition()), what, timeout);
}

/// <summary>An <see cref="IProgress{T}"/> that records reports synchronously (Progress&lt;T&gt; posts them later).</summary>
public sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_reports)
            {
                return _reports.ToList();
            }
        }
    }

    public void Report(T value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }
    }
}
