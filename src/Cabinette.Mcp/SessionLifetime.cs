namespace Cabinette.Mcp;

/// <summary>
/// How a session of cabinette-mcp.exe ends. A stdio client shuts its MCP server down by closing the server's stdin (MCP
/// spec, "Shutdown"). The SDK then stops reading, but <c>RunAsync</c> waits for every running handler first, and a tool
/// call waiting for Cabinette has no reply timeout (adding large files takes time): without help the process would run
/// until Cabinette answers, forever if it never does. So the end of input, like Ctrl+C, cancels the server's stop token:
/// the waiting calls are cancelled, which closes their pipes to Cabinette. If the session still hasn't ended after a
/// short grace period, it is abandoned and the process exits anyway.
/// </summary>
internal static class SessionLifetime
{
    /// <summary>How long a stopped session has to end before the process exits without it.</summary>
    public static TimeSpan Grace { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Waits until the session ends, or until the input ends or a stop is requested (then <paramref name="grace"/> more).</summary>
    /// <param name="session">The server's run and disposal; it ends when <paramref name="stop"/> is cancelled.</param>
    /// <param name="inputEnded">Completes when the client closed stdin (or reading it failed).</param>
    /// <param name="stop">The server's stop token source. Ctrl+C cancels it too.</param>
    /// <returns>True when the session ended (by itself or within the grace period); false when it was abandoned.</returns>
    public static async Task<bool> WaitAsync(Task session, Task inputEnded, CancellationTokenSource stop, TimeSpan grace)
    {
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (stop.Token.Register(() => stopRequested.TrySetResult()))
        {
            if (await Task.WhenAny(session, inputEnded, stopRequested.Task).ConfigureAwait(false) == session)
            {
                return true;
            }
        }

        try
        {
            stop.Cancel();
        }
        catch (AggregateException)
        {
            // A callback registered on the token threw; the token is cancelled all the same.
        }

        return await Task.WhenAny(session, Task.Delay(grace)).ConfigureAwait(false) == session;
    }
}
