using System.ComponentModel;
using System.IO;
using System.Text;
using Cabinette.App.Services;
using Cabinette.Windows;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// The hidden mode <c>Cabinette.exe --extract-text &lt;path&gt; --max-chars &lt;n&gt;</c>: runs the file's IFilter (third-party
/// in-process COM DLLs that can crash or hang) in a child process instead of Cabinette's own, the way Windows Search
/// uses SearchFilterHost.exe. The child writes the text as UTF-8 to its standard output and says how it went with its
/// exit code; a message goes to standard error.
/// </summary>
internal static class TextExtractionChild
{
    public const string Flag = "--extract-text";

    public const string MaxCharsFlag = "--max-chars";

    /// <summary>The whole text was written.</summary>
    public const int Complete = 0;

    /// <summary>The first max-chars characters were written; the document has more.</summary>
    public const int Truncated = 40;

    /// <summary>No IFilter is registered for the file's type.</summary>
    public const int NoExtractor = 41;

    /// <summary>The file can't be read (missing, locked, no access).</summary>
    public const int Unreadable = 42;

    /// <summary>The filter rejected the file (damaged, password-protected, not its format).</summary>
    public const int Rejected = 43;

    public const int BadArguments = 44;

    public const int Failed = 45;

    /// <summary>A child that is still running after this long ends itself (its parent should have killed it long before).</summary>
    public static readonly TimeSpan SelfTimeout = TimeSpan.FromSeconds(90);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Kept here so that the timer isn't collected while the child runs.</summary>
    private static Timer? _selfTimeout;

    /// <summary>
    /// The child's whole run, called first thing by <c>Program.Main</c> for any command line with a child flag; returns the
    /// exit code (a malformed command line gives <see cref="BadArguments"/>). Never throws.
    /// </summary>
    public static int Run(IReadOnlyList<string> args)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            return RunAsync(options.ExtractTextPath, options.ExtractMaxChars).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return Failed;
        }
    }

    /// <summary>The child's run from parsed flags (also <c>App.OnStartup</c>'s fallback); returns the exit code.</summary>
    public static async Task<int> RunAsync(string? path, int? maxChars)
    {
        // Nothing the child does may appear on the desktop: no "program stopped working" or "insert a disk" dialogs.
        FilterTextExtractor.SuppressErrorDialogs();
        _selfTimeout = new Timer(_ => Environment.Exit(Failed), null, SelfTimeout, Timeout.InfiniteTimeSpan);

        await using var stdout = Console.OpenStandardOutput();
        await using var stderr = Console.OpenStandardError();
        return await RunAsync(path, maxChars, new FilterTextExtractor(), stdout, stderr);
    }

    internal static async Task<int> RunAsync(string? path, int? maxChars, ITextExtractor extractor, Stream stdout, Stream stderr)
    {
        // The parent always passes a full path and a count: anything else is a malformed command line.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || maxChars is not >= 0)
        {
            await WriteAsync(stderr, $"Usage: Cabinette.exe {Flag} <file> {MaxCharsFlag} <n>");
            return BadArguments;
        }

        try
        {
            var result = await extractor.ExtractAsync(path, maxChars.Value);
            if (result is null)
            {
                return NoExtractor;
            }

            await WriteAsync(stdout, result.Text);
            return result.Truncated ? Truncated : Complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteAsync(stderr, ex.Message);
            return Unreadable;
        }
        catch (InvalidDataException ex)
        {
            await WriteAsync(stderr, ex.Message);
            return Rejected;
        }
        catch (Exception ex)
        {
            await WriteAsync(stderr, $"{ex.GetType().Name}: {ex.Message}");
            return Failed;
        }
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        try
        {
            await stream.WriteAsync(Utf8.GetBytes(text));
            await stream.FlushAsync();
        }
        catch (IOException)
        {
            // The parent stopped listening (it was cancelled): nothing to do.
        }
    }
}

/// <summary>
/// <see cref="ITextExtractor"/> for the app: each extraction runs <c>Cabinette.exe --extract-text</c> in a hidden child
/// process (see <see cref="TextExtractionChild"/>) at Low integrity with CET shadow stacks off (see <see cref="ChildProcess"/>),
/// killed with its children when it takes longer than the timeout or the call is cancelled, so a filter that crashes or
/// hangs never takes Cabinette with it. <see cref="CanExtract"/> only reads the registry, in this process.
/// </summary>
public sealed class IsolatedTextExtractor : ITextExtractor
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string, int, ChildCommand> _command;
    private readonly Func<string, bool> _canExtract;

    /// <param name="cabinetteExe">Cabinette.exe, run as the child (see <see cref="DefaultExecutablePath"/>).</param>
    public IsolatedTextExtractor(string cabinetteExe, TimeSpan? timeout = null)
        : this((path, maxChars) => ChildCommandFor(cabinetteExe, path, maxChars), timeout, path => IFilterRegistry.FindFilterForPath(path) is not null)
    {
    }

    /// <summary>For tests: any child process and any rule for which files have a filter.</summary>
    internal IsolatedTextExtractor(Func<string, int, ChildCommand> command, TimeSpan? timeout, Func<string, bool> canExtract)
    {
        _command = command;
        _canExtract = canExtract;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; }

    /// <summary>For tests: called with the child's process id once it has started.</summary>
    internal Action<int>? Started { get; set; }

    /// <summary>
    /// Cabinette.exe next to the running app. <see cref="Environment.ProcessPath"/> is Cabinette.exe in the app; under a
    /// test host (or <c>dotnet Cabinette.dll</c>) it isn't, and the exe next to the assemblies is used.
    /// </summary>
    public static string DefaultExecutablePath()
    {
        var process = Environment.ProcessPath;
        return process is not null && string.Equals(Path.GetFileName(process), "Cabinette.exe", StringComparison.OrdinalIgnoreCase)
            ? process
            : Path.Combine(AppContext.BaseDirectory, "Cabinette.exe");
    }

    public bool CanExtract(string path) => _canExtract(path);

    public async Task<ExtractedText?> ExtractAsync(string path, int maxChars, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (!CanExtract(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The file doesn't exist.", fullPath);
        }

        // UTF-8 takes at most 3 bytes per UTF-16 character; more output than that is not an extraction.
        var maxOutputBytes = 3L * maxChars + 64 * 1024;
        var command = _command(fullPath, maxChars);
        var run = await ChildProcessRunner.RunAsync(command.Program, command.Arguments, command.WorkingDirectory, Timeout, maxOutputBytes, Started, ct)
            .ConfigureAwait(false);
        var message = run.Error.Trim();
        return run.ExitCode switch
        {
            TextExtractionChild.Complete => new ExtractedText(Decode(run.Output), Truncated: false),
            TextExtractionChild.Truncated => new ExtractedText(Decode(run.Output), Truncated: true),
            TextExtractionChild.NoExtractor => null,
            TextExtractionChild.Unreadable => throw new IOException(message.Length > 0 ? message : $"{Path.GetFileName(fullPath)} can't be read."),
            TextExtractionChild.Rejected => throw new InvalidDataException(message.Length > 0 ? message : "the file was rejected."),
            _ => throw new InvalidDataException(
                $"the text extractor for {Path.GetExtension(fullPath).ToLowerInvariant()} files stopped unexpectedly (exit code 0x{run.ExitCode:X8})."),
        };
    }

    /// <summary>The child for one file: Cabinette.exe with its child flags, in the system folder.</summary>
    internal static ChildCommand ChildCommandFor(string cabinetteExe, string path, int maxChars) => new(
        cabinetteExe,
        [TextExtractionChild.Flag, path, TextExtractionChild.MaxCharsFlag, maxChars.ToString(System.Globalization.CultureInfo.InvariantCulture)],

        // Filters may load DLLs from the current folder: never a user's folder.
        Environment.SystemDirectory);

    private static string Decode(byte[] output) => Encoding.UTF8.GetString(output);
}

/// <summary>
/// What a child process is started with: the program (a full path), its arguments one by one (quoted for it by
/// <see cref="ChildProcess"/>), and its working folder (null: this process's). Nothing else: unlike a
/// <c>ProcessStartInfo</c>, there is no second form of the arguments that could be filled and then silently ignored.
/// </summary>
internal sealed record ChildCommand(string Program, IReadOnlyList<string> Arguments, string? WorkingDirectory);

/// <summary>A started child as <see cref="ChildProcessRunner"/> uses it: <see cref="ChildProcess"/>, or a fake in tests.</summary>
internal interface IRunningChild : IDisposable
{
    int Id { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    /// <summary>The exit code (259, STILL_ACTIVE, while it runs).</summary>
    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Ends the child and everything it started. Safe to repeat, and after it exited.</summary>
    void Kill();
}

/// <summary>
/// Runs a hidden child process (<see cref="ChildProcess"/>) with its output captured, a timeout and cancellation: both
/// kill it and everything it started.
/// </summary>
internal static class ChildProcessRunner
{
    /// <summary>Standard error beyond this is dropped (it only carries a message).</summary>
    private const int MaxErrorBytes = 64 * 1024;

    public sealed record Result(int ExitCode, byte[] Output, string Error);

    /// <param name="program">The program, a full path.</param>
    /// <param name="arguments">Its arguments, one by one; the child gets exactly these.</param>
    /// <param name="workingDirectory">Its working folder; null for this process's.</param>
    /// <exception cref="TimeoutException">The child ran longer than <paramref name="timeout"/> (it was killed).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (the child was killed).</exception>
    /// <exception cref="InvalidDataException">The child couldn't be started, or wrote more than <paramref name="maxOutputBytes"/>.</exception>
    public static Task<Result> RunAsync(string program, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout,
        long maxOutputBytes, Action<int>? started, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        ArgumentNullException.ThrowIfNull(arguments);
        var folder = workingDirectory is { Length: > 0 } ? workingDirectory : null;
        return RunAsync(() => ChildProcess.Start(program, arguments, folder), timeout, maxOutputBytes, started, ct);
    }

    /// <summary>The run, with the child <paramref name="start"/> gives (tests pass a fake one).</summary>
    internal static async Task<Result> RunAsync(Func<IRunningChild> start, TimeSpan timeout, long maxOutputBytes, Action<int>? started,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IRunningChild child;
        try
        {
            child = start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidDataException($"Cabinette's text extractor could not be started ({ex.Message}).", ex);
        }

        using (child)
        {
            started?.Invoke(child.Id);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(timeout);

            // A child that writes too much is stopped at once; set before the cancel, so the cancelled wait sees it.
            var tooMuch = 0;
            var output = ReadAsync(child.StandardOutput, maxOutputBytes, () =>
            {
                Volatile.Write(ref tooMuch, 1);
                stop.Cancel();
            });
            var error = ReadAsync(child.StandardError, MaxErrorBytes, overflow: null);
            try
            {
                await child.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                child.Kill();
                Observe(output);
                Observe(error);
                if (Volatile.Read(ref tooMuch) != 0)
                {
                    throw TooMuchOutput();
                }

                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"The text extractor didn't finish within {timeout.TotalSeconds:0} seconds.");
            }

            // Anything the child left running would keep its output open.
            child.Kill();

            // Null when the output went past the limit: also when the child exited before the reader noticed.
            var bytes = await output.ConfigureAwait(false) ?? throw TooMuchOutput();
            var message = Encoding.UTF8.GetString(await error.ConfigureAwait(false) ?? []);
            return new Result(child.ExitCode, bytes, message);
        }
    }

    private static InvalidDataException TooMuchOutput() => new("The text extractor wrote more text than was asked for.");

    /// <summary>
    /// Everything the stream gives until it ends. Past <paramref name="maxBytes"/>: <paramref name="overflow"/> is called
    /// and null is returned, or (without one) the rest is read and dropped.
    /// </summary>
    private static async Task<byte[]?> ReadAsync(Stream stream, long maxBytes, Action? overflow)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                if (overflow is not null)
                {
                    overflow();
                    return null;
                }

                buffer.Write(chunk, 0, (int)Math.Max(0, maxBytes - buffer.Length));
                continue;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void Observe(Task task) => task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
