using System.Runtime.InteropServices;

namespace Cabinette.Mcp;

/// <summary>The few Win32 calls cabinette-mcp.exe needs: starting Cabinette detached, job checks and the pipe probe.</summary>
internal static partial class NativeMethods
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;

    public const uint CreateBreakawayFromJob = 0x01000000;
    public const uint CreateNewProcessGroup = 0x00000200;
    public const uint CreateNoWindow = 0x08000000;
    public const uint CreateUnicodeEnvironment = 0x00000400;

    public const uint JobObjectLimitBreakawayOk = 0x00000800;
    public const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CreateProcess(
        string? applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(nint job, int infoClass, void* info, int length, out int returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeoutMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    /// <summary>
    /// The job this process runs in, if any: whether it is in one, and that job's limit flags (null when they can't be
    /// read). Claude Code (Node/libuv) and other clients may start their servers inside a job object.
    /// </summary>
    public static unsafe (bool InJob, uint? LimitFlags) GetCurrentJob()
    {
        if (!IsProcessInJob(GetCurrentProcess(), 0, out var inJob) || !inJob)
        {
            return (false, null);
        }

        JobObjectExtendedLimitInformation info;
        return QueryInformationJobObject(0, JobObjectExtendedLimitInformationClass, &info, sizeof(JobObjectExtendedLimitInformation), out _)
            ? (true, info.BasicLimitInformation.LimitFlags)
            : (true, null);
    }

    /// <summary>
    /// True when a pipe of that name exists (some Cabinette serves it, even if all its instances are busy). Doesn't connect.
    /// </summary>
    public static bool PipeExists(string pipeName)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + pipeName, 1))
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() != ErrorFileNotFound;
    }
}
