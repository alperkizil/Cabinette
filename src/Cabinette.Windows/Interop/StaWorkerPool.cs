using System.Collections.Concurrent;

namespace Cabinette.Windows.Interop;

/// <summary>
/// A few dedicated STA threads that run shell calls from a FIFO queue. Some thumbnail/icon handlers require STA,
/// and keeping the work off the thread pool avoids starving it with slow extractions. Work whose token is cancelled
/// before it starts is skipped (the UI cancels when virtualized rows are recycled).
/// </summary>
internal sealed class StaWorkerPool : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly Thread[] _threads;
    private int _disposed;

    public StaWorkerPool(string name, int threadCount)
    {
        _threads = new Thread[Math.Max(1, threadCount)];
        for (var i = 0; i < _threads.Length; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"{name} #{i + 1}",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _threads[i] = thread;
        }
    }

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var item = new WorkItem<T>(work, ct);
        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            item.Abandon();
            return Task.FromException<T>(new ObjectDisposedException(nameof(StaWorkerPool)));
        }

        return item.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        // Anything left (threads stuck in a slow handler) is cancelled so awaiting callers don't hang.
        while (_queue.TryTake(out var leftover))
        {
            leftover.Abandon();
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute();
            }
        }
        catch (ObjectDisposedException)
        {
            // Pool disposed while waiting.
        }
    }

    private abstract class WorkItem
    {
        public abstract void Execute();

        public abstract void Abandon();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<T> _work;
        private readonly CancellationToken _ct;
        private readonly CancellationTokenRegistration _registration;

        public WorkItem(Func<T> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;
            if (ct.CanBeCanceled)
            {
                // Complete the caller's task immediately; the worker skips the item when it reaches it.
                _registration = ct.Register(static s => ((WorkItem<T>)s!).Cancel(), this);
            }
        }

        public Task<T> Task => _tcs.Task;

        public override void Execute()
        {
            try
            {
                if (_ct.IsCancellationRequested || _tcs.Task.IsCompleted)
                {
                    _tcs.TrySetCanceled(_ct);
                    return;
                }

                _tcs.TrySetResult(_work());
            }
            catch (OperationCanceledException oce)
            {
                _tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                _registration.Dispose();
            }
        }

        public override void Abandon()
        {
            _tcs.TrySetCanceled();
            _registration.Dispose();
        }

        private void Cancel() => _tcs.TrySetCanceled(_ct);
    }
}
