using System.Diagnostics;
using System.Threading.Channels;

namespace Cabinette.Core.Services;

/// <summary>Single-consumer queue that hashes large pointer files in the background.</summary>
internal sealed class BackgroundHashQueue : IAsyncDisposable
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<long, CancellationToken, Task> _process;
    private readonly object _gate = new();
    private readonly Task _worker;
    private int _pending;
    private TaskCompletionSource _idle;
    private bool _disposed;

    public BackgroundHashQueue(Func<long, CancellationToken, Task> process)
    {
        _process = process;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _idle.SetResult();
        _worker = Task.Run(RunAsync);
    }

    public void Enqueue(long itemId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (!_channel.Writer.TryWrite(itemId))
        {
            MarkDone();
        }
    }

    /// <summary>Completes when the queue is empty and nothing is being processed (or the queue was disposed).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _idle.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cts.Dispose();
        lock (_gate)
        {
            _idle.TrySetResult();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var itemId in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await _process(itemId, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Cabinette: background hashing of item {itemId} failed: {ex}");
                }
                finally
                {
                    MarkDone();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private void MarkDone()
    {
        lock (_gate)
        {
            if (_pending > 0 && --_pending == 0)
            {
                _idle.TrySetResult();
            }
        }
    }
}
