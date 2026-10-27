using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Cabinette.Mcp;

/// <summary>Starts a program. Abstracted so the retry logic can be tested without starting anything.</summary>
internal interface IProcessStarter
{
    /// <summary>Starts the program and returns its process id.</summary>
    /// <exception cref="Win32Exception">Windows refused to start it.</exception>
    int Start(string exePath, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>How a started program relates to the job object this process runs in (if any).</summary>
internal enum JobPlacement
{
    /// <summary>This process isn't in a job: the child is in none either.</summary>
    NoJob,

    /// <summary>The job lets children leave silently: the child is outside it without asking.</summary>
    SilentBreakaway,

    /// <summary>The job allows breakaway: the child is started with CREATE_BREAKAWAY_FROM_JOB.</summary>
    Breakaway,

    /// <summary>The job's limits couldn't be read: breakaway is tried, and the start retried without it if refused.</summary>
    Unknown,

    /// <summary>The job allows no breakaway: the child stays in it (see <see cref="JobPolicy"/>).</summary>
    StaysInJob,
}

/// <summary>
/// Whether Cabinette, started by this process, can outlive the AI client. Clients may run their MCP servers inside a
/// job object, and a job with "kill on close" ends every process in it when the client exits. Cabinette must leave
/// that job: <c>CREATE_BREAKAWAY_FROM_JOB</c> when the job allows breakaway (or nothing, when it lets children leave
/// silently). When the job allows neither, Cabinette stays in it: if the job also kills on close, Cabinette closes when
/// the AI client exits (it is started again on the next tool call, and a Cabinette the user started is never
/// affected); otherwise it simply keeps running.
/// </summary>
internal static class JobPolicy
{
    public static JobPlacement Decide(bool inJob, uint? limitFlags)
    {
        if (!inJob)
        {
            return JobPlacement.NoJob;
        }

        if (limitFlags is not { } flags)
        {
            return JobPlacement.Unknown;
        }

        if ((flags & NativeMethods.JobObjectLimitSilentBreakawayOk) != 0)
        {
            return JobPlacement.SilentBreakaway;
        }

        return (flags & NativeMethods.JobObjectLimitBreakawayOk) != 0 ? JobPlacement.Breakaway : JobPlacement.StaysInJob;
    }

    /// <summary>True when a child that stays in the job is ended together with the AI client.</summary>
    public static bool KillsOnClose(uint? limitFlags) => limitFlags is { } flags && (flags & NativeMethods.JobObjectLimitKillOnJobClose) != 0;
}

/// <summary>
/// Starts a program detached from this process, with CreateProcess:
/// <list type="bullet">
/// <item>No handle inheritance (<c>bInheritHandles = FALSE</c>). .NET's <c>Process.Start</c> passes TRUE, so a long-running
/// Cabinette would keep the AI client's stdio pipes open and the client could hang waiting for them to close.</item>
/// <item>Outside the AI client's job object where the job allows it (<see cref="JobPolicy"/>).</item>
/// <item>Its working folder is the program's own folder, not the AI client's project folder (which it would keep busy).</item>
/// <item>No console window (<c>CREATE_NO_WINDOW</c>; ignored for a windowed program like Cabinette.exe) and its own
/// process group, so Ctrl+C in the client's console never reaches it.</item>
/// </list>
/// </summary>
internal sealed class DetachedProcessStarter : IProcessStarter
{
    private readonly Action<string>? _warn;

    public DetachedProcessStarter(Action<string>? warn = null)
    {
        _warn = warn;
    }

    /// <summary>How the last started program related to this process's job (for tests and the log).</summary>
    public JobPlacement LastPlacement { get; private set; }

    public int Start(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var (inJob, limitFlags) = NativeMethods.GetCurrentJob();
        var placement = JobPolicy.Decide(inJob, limitFlags);
        if (placement == JobPlacement.StaysInJob && JobPolicy.KillsOnClose(limitFlags))
        {
            _warn?.Invoke("This process runs in a job that allows no breakaway and kills on close: the Cabinette it starts closes when the AI client exits.");
        }

        var commandLine = CommandLineText.Build(exePath, arguments);
        var flags = NativeMethods.CreateNewProcessGroup | NativeMethods.CreateNoWindow | NativeMethods.CreateUnicodeEnvironment;
        var breakaway = placement is JobPlacement.Breakaway or JobPlacement.Unknown;
        if (TryCreate(exePath, commandLine, flags | (breakaway ? NativeMethods.CreateBreakawayFromJob : 0), workingDirectory, out var processId, out var error))
        {
            LastPlacement = placement;
            return processId;
        }

        if (breakaway && error == NativeMethods.ErrorAccessDenied)
        {
            // The job (or a job above it) refused the breakaway: start it inside the job rather than not at all.
            _warn?.Invoke("The AI client's job refused to let Cabinette leave it; Cabinette may close when the client exits.");
            if (TryCreate(exePath, commandLine, flags, workingDirectory, out processId, out error))
            {
                LastPlacement = JobPlacement.StaysInJob;
                return processId;
            }
        }

        throw new Win32Exception(error);
    }

    private static unsafe bool TryCreate(string exePath, string commandLine, uint flags, string workingDirectory, out int processId, out int error)
    {
        var startupInfo = new NativeMethods.StartupInfo { Cb = sizeof(NativeMethods.StartupInfo) };

        // CreateProcessW may write into the command line buffer, so pass a private, null-terminated copy.
        var buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(buffer);
        bool created;
        NativeMethods.ProcessInformation info;
        fixed (char* line = buffer)
        {
            created = NativeMethods.CreateProcess(
                exePath,
                line,
                processAttributes: 0,
                threadAttributes: 0,
                inheritHandles: false,
                flags,
                environment: 0,
                Directory.Exists(workingDirectory) ? workingDirectory : null,
                ref startupInfo,
                out info);
        }

        if (!created)
        {
            error = Marshal.GetLastPInvokeError();
            processId = 0;
            return false;
        }

        NativeMethods.CloseHandle(info.Thread);
        NativeMethods.CloseHandle(info.Process);
        error = 0;
        processId = info.ProcessId;
        return true;
    }
}

/// <summary>Builds a Windows command line that <c>CommandLineToArgvW</c> and the C runtime split back into the same arguments.</summary>
internal static class CommandLineText
{
    public static string Build(string exePath, IReadOnlyList<string> arguments)
    {
        // The program name follows simpler rules: quotes only delimit it (a path never contains a quote).
        var builder = new StringBuilder().Append('"').Append(exePath).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    public static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: then each one is doubled and the quote escaped.
            builder.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            builder.Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so the quote still closes the argument.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }
}

/// <summary>Starts Cabinette when it isn't running.</summary>
internal interface ICabinetteLauncher
{
    /// <summary>Starts Cabinette quietly in the tray.</summary>
    /// <exception cref="CabinetteUnavailableException">Cabinette.exe isn't there, or Windows refused to start it.</exception>
    void Start();
}

/// <summary>
/// Starts <c>Cabinette.exe --tray</c> (plus <c>--data-dir &lt;dir&gt;</c> for a dev run), from <c>--app</c> or from next to
/// cabinette-mcp.exe, detached from this process (<see cref="DetachedProcessStarter"/>).
/// </summary>
internal sealed class CabinetteLauncher : ICabinetteLauncher
{
    private readonly IProcessStarter _starter;

    /// <param name="exePath">Full path of Cabinette.exe: it is never looked for in the current folder or on PATH.</param>
    public CabinetteLauncher(string exePath, string? devDataDirectory, IProcessStarter starter)
    {
        if (!Path.IsPathFullyQualified(exePath))
        {
            throw new ArgumentException($"Cabinette.exe must be given by its full path, not \"{exePath}\".", nameof(exePath));
        }

        ExePath = exePath;
        _starter = starter;
        Arguments = devDataDirectory is null ? ["--tray"] : ["--tray", "--data-dir", devDataDirectory];
    }

    public string ExePath { get; }

    public IReadOnlyList<string> Arguments { get; }

    public void Start()
    {
        if (!File.Exists(ExePath))
        {
            throw new CabinetteUnavailableException(
                $"Cabinette isn't running, and it couldn't be started: Cabinette.exe isn't at \"{ExePath}\". " +
                "Ask the user to start Cabinette (or to reinstall it so cabinette-mcp.exe sits next to Cabinette.exe).");
        }

        try
        {
            _starter.Start(ExePath, Arguments, Path.GetDirectoryName(ExePath) ?? McpPaths.ProgramDirectory);
        }
        catch (Win32Exception ex)
        {
            throw new CabinetteUnavailableException($"Cabinette isn't running, and starting \"{ExePath}\" failed: {ex.Message}");
        }
    }
}
