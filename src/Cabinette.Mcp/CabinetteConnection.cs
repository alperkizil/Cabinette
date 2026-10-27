using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Cabinette.Ipc;

namespace Cabinette.Mcp;

/// <summary>Cabinette couldn't be reached or started. The message is for the AI client's model.</summary>
internal sealed class CabinetteUnavailableException(string message) : Exception(message);

/// <summary>Sends one "ai" message to the running Cabinette and returns its reply.</summary>
internal interface ICabinetteChannel
{
    /// <exception cref="CabinetteUnavailableException">Cabinette couldn't be reached or started.</exception>
    /// <exception cref="OperationCanceledException">The tool call was cancelled (the pipe is closed).</exception>
    Task<InstanceReply> SendAsync(InstanceMessage message, CancellationToken ct);
}

/// <summary>The waits of <see cref="CabinetteConnection"/> (shorter in tests).</summary>
internal sealed record ConnectionTimings
{
    /// <summary>How long one connection attempt waits for a free pipe instance.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a Cabinette this process started has to open its channel.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the channel is looked for while Cabinette starts.</summary>
    public TimeSpan StartPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A "not_ready" reply is retried this often...</summary>
    public TimeSpan NotReadyRetryInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>...for at most this long in all, then the message goes to the model.</summary>
    public TimeSpan NotReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The local channel to Cabinette (SPEC.md section 10): one pipe connection per tool call. It connects with
/// <see cref="PipeOptions.CurrentUserOnly"/> (only a pipe owned by this Windows account), writes one message line and
/// reads one reply line. There is no reply timeout, since adding large files takes time; cancelling the tool call closes
/// the pipe. When Cabinette isn't running it is started in the tray and given <see cref="ConnectionTimings.StartTimeout"/>
/// to open its channel; "not_ready" replies (starting, first-run setup, quitting) are retried for a while.
/// </summary>
internal sealed class CabinetteConnection : ICabinetteChannel
{
    private readonly string _pipeName;
    private readonly ICabinetteLauncher _launcher;
    private readonly ConnectionTimings _timings;
    private readonly Func<string, bool> _pipeExists;

    public CabinetteConnection(string pipeName, ICabinetteLauncher launcher, ConnectionTimings? timings = null, Func<string, bool>? pipeExists = null)
    {
        _pipeName = pipeName;
        _launcher = launcher;
        _timings = timings ?? new ConnectionTimings();
        _pipeExists = pipeExists ?? NativeMethods.PipeExists;
    }

    /// <summary>Lets Cabinette bring its window to the front for this call (show_in_cabinette, the vault operations).</summary>
    public static bool WantsForeground(string? tool) =>
        tool is AiTools.ShowInCabinette or AiTools.MoveVault or AiTools.MigratePointers or AiTools.CreateBackup;

    public async Task<InstanceReply> SendAsync(InstanceMessage message, CancellationToken ct)
    {
        var line = InstanceProtocol.Serialize(message);
        var allowForeground = WantsForeground(message.Tool);
        Stopwatch? notReady = null;
        while (true)
        {
            var reply = await SendOnceAsync(line, allowForeground, ct).ConfigureAwait(false);
            if (reply is { Ok: false, Code: AiErrorCodes.NotReady })
            {
                notReady ??= Stopwatch.StartNew();
                if (notReady.Elapsed + _timings.NotReadyRetryInterval <= _timings.NotReadyTimeout)
                {
                    await Task.Delay(_timings.NotReadyRetryInterval, ct).ConfigureAwait(false);
                    continue;
                }
            }

            return reply;
        }
    }

    private async Task<InstanceReply> SendOnceAsync(string line, bool allowForeground, CancellationToken ct)
    {
        var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        await using (pipe.ConfigureAwait(false))
        {
            if (allowForeground)
            {
                AllowServerToTakeForeground(pipe);
            }

            string? replyLine;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                replyLine = await new LineReader(pipe, InstanceProtocol.MaxLineLength).ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                throw new CabinetteUnavailableException("The connection to Cabinette broke before it answered (did Cabinette quit?). The change may or may not have been made: check before retrying.");
            }
            catch (InvalidDataException)
            {
                throw new CabinetteUnavailableException("Cabinette's answer was too long to read.");
            }

            if (replyLine is null)
            {
                throw new CabinetteUnavailableException("Cabinette closed the connection without answering (did it quit?). The change may or may not have been made: check before retrying.");
            }

            return InstanceProtocol.TryParseReply(replyLine)
                ?? throw new CabinetteUnavailableException("Cabinette sent an answer this cabinette-mcp.exe can't read. Cabinette and cabinette-mcp.exe may be different versions: update both.");
        }
    }

    /// <summary>Connects to the running Cabinette, starting it first when its channel doesn't exist.</summary>
    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        // Running: its channel exists. Connect within ConnectTimeout (all its pipe instances may be busy for a moment).
        var connecting = Stopwatch.StartNew();
        while (_pipeExists(_pipeName))
        {
            var remaining = _timings.ConnectTimeout - connecting.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new CabinetteUnavailableException(
                    $"Cabinette is running but didn't accept the connection within {_timings.ConnectTimeout.TotalSeconds:0} s. Try again in a moment.");
            }

            var pipe = await TryConnectAsync(remaining, ct).ConfigureAwait(false);
            if (pipe is not null)
            {
                return pipe;
            }

            await Task.Delay(_timings.StartPollInterval, ct).ConfigureAwait(false);
        }

        // Not running (or it just quit): start it in the tray and wait for its channel. A second start halfway covers a
        // Cabinette that was still quitting the first time (the new one then exits at once, as a second copy started
        // with --tray does); when the first one is just slow, the second exits the same way.
        _launcher.Start();
        var started = Stopwatch.StartNew();
        var startedAgain = false;
        while (true)
        {
            await Task.Delay(_timings.StartPollInterval, ct).ConfigureAwait(false);
            var remaining = _timings.StartTimeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            if (_pipeExists(_pipeName))
            {
                var pipe = await TryConnectAsync(remaining < _timings.ConnectTimeout ? remaining : _timings.ConnectTimeout, ct).ConfigureAwait(false);
                if (pipe is not null)
                {
                    return pipe;
                }
            }
            else if (!startedAgain && started.Elapsed >= _timings.StartTimeout / 3)
            {
                startedAgain = true;
                _launcher.Start();
            }
        }

        throw new CabinetteUnavailableException(
            $"Cabinette was started but didn't open its channel within {_timings.StartTimeout.TotalSeconds:0} s. " +
            "Ask the user to check that Cabinette is running (its icon is in the tray), then try again.");
    }

    /// <summary>The connected pipe, or null when no instance became free in time (or the pipe went away).</summary>
    private async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new CabinetteUnavailableException(
                "Cabinette's channel belongs to another Windows account (or to a copy running as administrator), so it can't be used from here.");
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Passes this process's right to bring a window to the front on to Cabinette, so the window it opens comes up
    /// instead of only flashing. Windows grants it only when this process may set the foreground itself; else no effect.
    /// </summary>
    private static void AllowServerToTakeForeground(NamedPipeClientStream pipe)
    {
        try
        {
            if (NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId) && processId != 0)
            {
                NativeMethods.AllowSetForegroundWindow(processId);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
