using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// The "ai" command's entry on the local channel. The channel starts before the library opens, so until the library is open
/// with a vault and the main window is ready, calls are answered "not_ready" (cabinette-mcp.exe retries for a while), with
/// what Cabinette is waiting for. Thread-safe.
/// </summary>
public sealed class AiCommandGate
{
    internal const string StartingMessage = "Cabinette is starting. Try again in a moment.";
    internal const string FirstRunMessage = "Cabinette needs its first-run setup: choose a vault folder in the Cabinette window.";

    private volatile State _state = new(null, StartingMessage);

    /// <summary>True once calls go to the handler.</summary>
    public bool IsOpen => _state.Handler is not null;

    /// <summary>The first-run window is waiting for a vault folder.</summary>
    public void WaitForFirstRun() => Set(new State(null, FirstRunMessage));

    /// <summary>The vault is chosen; the main window is being prepared.</summary>
    public void WaitForStartup() => Set(new State(null, StartingMessage));

    /// <summary>The library is open with a vault and the main window is ready: calls go to <paramref name="handler"/>.</summary>
    public void Open(AiCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Set(new State(handler, null));
    }

    /// <summary>Cabinette is quitting or restarting: "Cabinette is closing." from now on.</summary>
    public void Close() => _state = new State(null, AiCommandHandler.ClosingMessage);

    /// <summary>The dispatcher's handler for <see cref="InstanceCommands.Ai"/>.</summary>
    public Task<InstanceReply> HandleAsync(InstanceMessage message)
    {
        var state = _state;
        if (state.Handler is { } handler)
        {
            return handler.HandleAsync(message);
        }

        return Task.FromResult(message.Version != AiProtocol.Version
            ? InstanceReply.Failed(AiErrorCodes.Unsupported, AiCommandHandler.VersionMessage)
            : InstanceReply.Failed(AiErrorCodes.NotReady, state.NotReadyMessage!));
    }

    /// <summary>Changes the state unless Cabinette is already closing.</summary>
    private void Set(State next)
    {
        var current = _state;
        while (current.NotReadyMessage != AiCommandHandler.ClosingMessage)
        {
            var seen = Interlocked.CompareExchange(ref _state, next, current);
            if (ReferenceEquals(seen, current))
            {
                return;
            }

            current = seen;
        }
    }

    private sealed record State(AiCommandHandler? Handler, string? NotReadyMessage);
}
