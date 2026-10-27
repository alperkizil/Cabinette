using Cabinette.Windows;

namespace Cabinette.App.Services.Explorer;

public enum ForwardOutcome
{
    /// <summary>Every drop that arrived was taken by the running Cabinette.</summary>
    Forwarded,

    /// <summary>At least one drop could not be passed on (the running Cabinette didn't answer or refused it).</summary>
    Failed,

    /// <summary>No drop arrived in time (Explorer gave up, or COM started this copy for nothing).</summary>
    NoDrop,

    /// <summary>The COM classes could not be registered, so Explorer could not reach this copy either.</summary>
    NotRegistered,
}

/// <summary>
/// The copy COM starts for an Explorer entry while another Cabinette runs that COM can't use (for example one running as
/// administrator): it serves the entry's drop target itself, passes each selection to the running copy over the local
/// channel, and is done once that is answered, or after a while without a drop.
/// </summary>
public sealed class ExplorerDropForwarder
{
    private readonly IExplorerDropServer _server;
    private readonly Func<ExplorerDrop, Task<bool>> _forward;
    private readonly TimeSpan _waitForDrop;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ForwardOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _received;
    private int _inFlight;
    private bool _anyFailed;

    /// <param name="forward">Sends one selection to the running Cabinette; true when it took it. Should not throw.</param>
    public ExplorerDropForwarder(IExplorerDropServer server, Func<ExplorerDrop, Task<bool>> forward, TimeSpan waitForDrop)
    {
        _server = server;
        _forward = forward;
        _waitForDrop = waitForDrop;
    }

    /// <summary>
    /// Registers the classes (call on the UI thread), waits for a drop and passes it on. The classes are revoked as soon as
    /// the first drop arrives, and in any case before returning, so the next Explorer call starts or finds another server.
    /// </summary>
    public async Task<ForwardOutcome> RunAsync(CancellationToken ct = default)
    {
        _server.Dropped += OnDropped;
        try
        {
            try
            {
                _server.Register();
            }
            catch (Exception ex)
            {
                AppLog.Error("The Explorer entries could not be served, so the files can't be passed on.", ex);
                return ForwardOutcome.NotRegistered;
            }

            var waited = await Task.WhenAny(_done.Task, Task.Delay(_waitForDrop, ct)).ConfigureAwait(true);
            if (waited != _done.Task && Volatile.Read(ref _received) == 0)
            {
                return ForwardOutcome.NoDrop;
            }

            // A drop arrived: its forward has its own timeout, so this ends.
            return await _done.Task.ConfigureAwait(true);
        }
        finally
        {
            _server.Dropped -= OnDropped;
            _server.Revoke();
        }
    }

    private void OnDropped(object? sender, ExplorerDrop drop)
    {
        lock (_gate)
        {
            _received++;
            _inFlight++;
        }

        // One selection per copy: the next Explorer call starts a fresh copy instead of reaching this one while it exits.
        // (A drop target Explorer already holds still delivers here.)
        try
        {
            _server.Revoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Revoking the Explorer entries failed.", ex);
        }

        _ = ForwardAsync(drop);
    }

    private async Task ForwardAsync(ExplorerDrop drop)
    {
        bool ok;
        try
        {
            ok = await _forward(drop).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Passing files on to the running Cabinette failed.", ex);
            ok = false;
        }

        lock (_gate)
        {
            _anyFailed |= !ok;
            if (--_inFlight == 0)
            {
                _done.TrySetResult(_anyFailed ? ForwardOutcome.Failed : ForwardOutcome.Forwarded);
            }
        }
    }
}
