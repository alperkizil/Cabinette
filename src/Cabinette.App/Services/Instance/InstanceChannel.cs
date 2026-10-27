using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Instance;

/// <summary>
/// Owns the single-instance mutex. Only one process per user and data folder gets it.
/// The mutex is thread-affine: acquire and release it on the UI thread.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// Returns the guard, or null when another instance already holds the mutex. That includes a copy started as
    /// administrator: its mutex exists, but its default security doesn't let this account's normal processes open it.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string mutexName)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out _);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed without releasing it: it is ours now.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    /// <summary>Releases the mutex (before a restart, so the new process can take it). Safe to call twice.</summary>
    public void Release()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            AppLog.Warn("The single-instance mutex could not be released (wrong thread?).", ex);
        }
        finally
        {
            mutex.Dispose();
        }
    }

    public void Dispose() => Release();
}

/// <summary>
/// The running instance's end of the local channel: a named pipe that only the current Windows account can open
/// (its ACL grants the user's SID and denies network logons). Every line received is handed to the line handler and
/// its answer is written back. Runs on background threads.
/// </summary>
public sealed class InstancePipeServer : IAsyncDisposable
{
    /// <summary>
    /// How long the server waits for each request line and for each reply to be written. An idle connection is dropped after
    /// it; the handler itself has no limit (an "ai" call such as adding many files may take longer).
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly string _pipeName;
    private readonly Func<string, Task<string>> _handleLine;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public InstancePipeServer(string pipeName, Func<string, Task<string>> handleLine)
        : this(pipeName, handleLine, DefaultTimeout)
    {
    }

    /// <summary>With another wait limit (tests).</summary>
    internal InstancePipeServer(string pipeName, Func<string, Task<string>> handleLine, TimeSpan timeout)
    {
        _pipeName = pipeName;
        _handleLine = handleLine;
        _timeout = timeout;
    }

    public void Start() => _loop ??= Task.Run(() => ListenAsync(_cts.Token));

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    /// <summary>A pipe instance whose ACL only lets the current user in.</summary>
    public static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        // Clients connect with PipeOptions.CurrentUserOnly, which checks that the pipe's owner is this user.
        // Set it explicitly: an elevated process would otherwise default to the Administrators group as owner.
        security.SetOwner(user);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServerStream(_pipeName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures++;
                AppLog.Warn($"The instance pipe could not be created (attempt {failures}).", ex);
                if (failures >= 5)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(failures), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException || ct.IsCancellationRequested)
                {
                    return;
                }

                AppLog.Warn("Waiting for an instance connection failed.", ex);
                continue;
            }

            failures = 0;
            _ = Task.Run(() => ServeAsync(server, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                var reader = new LineReader(server, InstanceProtocol.MaxLineLength);
                while (true)
                {
                    // The wait limit covers receiving each line and writing each reply, not the handler: a long "ai" call keeps
                    // its connection, an idle one is still dropped.
                    string? line;
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        wait.CancelAfter(_timeout);
                        line = await reader.ReadLineAsync(wait.Token).ConfigureAwait(false);
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var reply = await _handleLine(line).ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    write.CancelAfter(_timeout);
                    await server.WriteAsync(bytes, write.Token).ConfigureAwait(false);
                    await server.FlushAsync(write.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
            {
                // Client went away, took too long or sent garbage: just drop the connection.
            }
            catch (Exception ex)
            {
                AppLog.Warn("Serving an instance connection failed.", ex);
            }
        }
    }
}

/// <summary>The second process's end: sends one message to the running instance and waits briefly for its reply.</summary>
public static class InstancePipeClient
{
    /// <summary>
    /// Connects (retrying until <paramref name="timeout"/>), sends the message and returns the reply, or null when the
    /// running instance could not be reached. For "activate" the running instance is first allowed to take the foreground.
    /// </summary>
    public static async Task<InstanceReply?> SendAsync(string pipeName, InstanceMessage message, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);

            if (message.Command == InstanceCommands.Activate)
            {
                AllowServerToTakeForeground(pipe);
            }

            var bytes = Encoding.UTF8.GetBytes(InstanceProtocol.Serialize(message) + "\n");
            await pipe.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cts.Token).ConfigureAwait(false);

            var reader = new LineReader(pipe, 64 * 1024);
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return InstanceProtocol.TryParseReply(line);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn($"The running Cabinette could not be reached ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>
    /// This process was just started by the user, so it may hand its right to set the foreground window to the running
    /// instance (otherwise Windows only flashes its taskbar button).
    /// </summary>
    private static void AllowServerToTakeForeground(NamedPipeClientStream pipe)
    {
        try
        {
            if (NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) && processId != 0)
            {
                NativeMethods.AllowSetForegroundWindow(processId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("AllowSetForegroundWindow failed.", ex);
        }
    }
}
