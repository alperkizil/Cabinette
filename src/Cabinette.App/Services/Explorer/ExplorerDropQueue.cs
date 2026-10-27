using Cabinette.Windows;

namespace Cabinette.App.Services.Explorer;

/// <summary>
/// Explorer batches (from the COM drop target or the local channel's "add") wait here until the library is open with a
/// vault, then each goes to the handler in arrival order. When first run ends without a vault, or Cabinette quits, the
/// queue is closed: waiting batches are dropped and later ones refused. Thread-safe.
/// </summary>
public sealed class ExplorerDropQueue
{
    private readonly Lock _gate = new();
    private readonly List<ExplorerDrop> _pending = [];
    private Func<ExplorerDrop, Task>? _handler;
    private bool _closed;

    /// <summary>Batches waiting for the library.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _handler is not null;
            }
        }
    }

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>Takes a batch: handled now when the queue is open, kept until then otherwise. False once closed.</summary>
    public bool Enqueue(ExplorerDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            if (_handler is null)
            {
                _pending.Add(drop);
                return true;
            }

            // Inside the lock so batches reach the handler in arrival order; the handler only schedules the work.
            Run(_handler, drop);
            return true;
        }
    }

    /// <summary>
    /// The library is open with a vault: hands over the waiting batches, then every new one as it arrives.
    /// <paramref name="handler"/> must return quickly (schedule the add, don't run it). Ignored once closed or opened.
    /// </summary>
    public void Open(Func<ExplorerDrop, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_closed || _handler is not null)
            {
                return;
            }

            _handler = handler;
            foreach (var drop in _pending)
            {
                Run(handler, drop);
            }

            _pending.Clear();
        }
    }

    /// <summary>No library after all (first run closed without a vault) or quitting: drops the waiting batches and refuses new ones.</summary>
    /// <returns>How many waiting batches were dropped.</returns>
    public int Close() => TakeAndClose().Count;

    /// <summary>Like <see cref="Close"/>, but returns the waiting batches (a restart keeps them for the next start).</summary>
    public IReadOnlyList<ExplorerDrop> TakeAndClose()
    {
        lock (_gate)
        {
            _closed = true;
            _handler = null;
            var waiting = _pending.ToList();
            _pending.Clear();
            return waiting;
        }
    }

    private static void Run(Func<ExplorerDrop, Task> handler, ExplorerDrop drop)
    {
        Task task;
        try
        {
            task = handler(drop);
        }
        catch (Exception ex)
        {
            AppLog.Error("Adding files from Explorer failed.", ex);
            return;
        }

        task.ContinueWith(
            t => AppLog.Error("Adding files from Explorer failed.", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
