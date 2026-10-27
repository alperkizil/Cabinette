using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Cabinette.App.Services;
using Microsoft.Win32.SafeHandles;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// A hidden child process for running third-party in-process code (IFilters), started with CreateProcess directly
/// because <see cref="System.Diagnostics.Process"/> can't set what it needs:
/// <list type="bullet">
/// <item>A job object that kills the child and anything it starts when it is killed, or when Cabinette exits.</item>
/// <item>No "app starting" cursor (STARTF_FORCEOFFFEEDBACK), no window (CREATE_NO_WINDOW), below-normal priority.</item>
/// <item>Only its own standard output and error handles are inherited (no other handle of Cabinette leaks into it).</item>
/// <item>
/// Low integrity, without privileges (Cabinette's token with every privilege but SeChangeNotifyPrivilege removed, lowered
/// to S-1-16-4096, started with CreateProcessAsUser): the child can read the user's files but can't write to normal
/// folders or the registry, send messages to Cabinette's windows, or enable a privilege (even if Cabinette ran elevated).
/// </item>
/// <item>
/// User-mode CET shadow stacks off (decided 2026-10-24): Office's filter loads the .NET Framework, which crashes
/// (0xC0000409) under them. Only this child; Cabinette.exe itself stays CET compatible.
/// </item>
/// </list>
/// </summary>
internal sealed class ChildProcess : IRunningChild
{
    /// <summary>The most memory the child may use (a runaway filter fails instead of slowing down the PC).</summary>
    private const long MaxProcessMemory = 2L * 1024 * 1024 * 1024;

    /// <summary>The Low mandatory level (SECURITY_MANDATORY_LOW_RID), the child's integrity.</summary>
    internal static readonly SecurityIdentifier LowIntegrity = new("S-1-16-4096");

    private readonly SafeProcessHandle _process;
    private readonly KernelHandle? _job;
    private int _disposed;

    private ChildProcess(SafeProcessHandle process, KernelHandle? job, int id, Stream output, Stream error)
    {
        _process = process;
        _job = job;
        Id = id;
        StandardOutput = output;
        StandardError = error;
    }

    public int Id { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    /// <exception cref="Win32Exception">The process couldn't be started (for example the program doesn't exist).</exception>
    /// <exception cref="ArgumentException">The program is Cabinette.exe, and the arguments don't make it the extraction child.</exception>
    public static ChildProcess Start(string program, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        // Cabinette.exe without a child flag is a normal app start (the real data folder, the Run key): never from here.
        // The same test as Program.Main's, so anything that passes it runs as the child there.
        if (IsCabinette(program) && !arguments.Any(CommandLineOptions.IsTextExtractionArgument))
        {
            throw new ArgumentException("Cabinette.exe is only started as a hidden child with its --extract-text flag.", nameof(arguments));
        }

        var commandLine = new StringBuilder();
        AppendArgument(commandLine, program, force: true);
        foreach (var argument in arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument, force: false);
        }

        using var token = CreateSandboxToken();
        CreateInheritablePipe(out var outputRead, out var outputWrite);
        CreateInheritablePipe(out var errorRead, out var errorWrite);
        try
        {
            if (!TryCreate(token, program, commandLine, workingDirectory, outputWrite, errorWrite, out var info, out var error))
            {
                throw new Win32Exception(error);
            }

            var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            using var thread = new KernelHandle(info.hThread);
            var job = CreateKillOnCloseJob(process);
            if (Native.ResumeThread(thread) == uint.MaxValue)
            {
                var resumeError = Marshal.GetLastWin32Error();
                Native.TerminateProcess(process, 1);
                job?.Dispose();
                process.Dispose();
                throw new Win32Exception(resumeError);
            }

            return new ChildProcess(
                process,
                job,
                info.dwProcessId,
                new FileStream(outputRead, FileAccess.Read, 1, isAsync: false),
                new FileStream(errorRead, FileAccess.Read, 1, isAsync: false));
        }
        catch
        {
            outputRead.Dispose();
            errorRead.Dispose();
            throw;
        }
        finally
        {
            // The child has its own copies; with ours closed, its output ends when it exits.
            outputWrite.Dispose();
            errorWrite.Dispose();
        }
    }

    /// <summary>Cabinette.exe by its name, or the program this process runs (Cabinette.exe renamed).</summary>
    internal static bool IsCabinette(string program)
    {
        if (string.Equals(Path.GetFileName(program), "Cabinette.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return Environment.ProcessPath is { } self && string.Equals(Path.GetFullPath(program), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public async Task WaitForExitAsync(CancellationToken ct)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waitHandle = new ProcessWaitHandle(_process);
        var registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            exited, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await using (ct.Register(() => exited.TrySetCanceled(ct)))
            {
                await exited.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>The exit code (259, STILL_ACTIVE, while it runs).</summary>
    public int ExitCode => Native.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

    /// <summary>Ends the child and everything it started. Safe to repeat, and after it exited.</summary>
    public void Kill()
    {
        if (_job is not null && Native.TerminateJobObject(_job, 1))
        {
            return;
        }

        Native.TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StandardOutput.Dispose();
        StandardError.Dispose();

        // Closing the job ends anything still running in it (kill on close).
        _job?.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// The child's token: this process's token without its privileges (DISABLE_MAX_PRIVILEGE keeps only
    /// SeChangeNotifyPrivilege), then lowered to Low integrity. Neither step needs a privilege, and the result can be
    /// given to CreateProcessAsUser because it is derived from this process's own token.
    /// </summary>
    private static KernelHandle CreateSandboxToken()
    {
        const uint access = Native.TokenDuplicate | Native.TokenQuery | Native.TokenAssignPrimary | Native.TokenAdjustDefault;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), access, out var selfHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var self = new KernelHandle(selfHandle);
        if (!Native.CreateRestrictedToken(self, Native.DisableMaxPrivilege, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var lowHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var low = new KernelHandle(lowHandle);
        var sid = new byte[LowIntegrity.BinaryLength];
        LowIntegrity.GetBinaryForm(sid, 0);
        var labelSize = Marshal.SizeOf<Native.TOKEN_MANDATORY_LABEL>();
        var label = Marshal.AllocHGlobal(labelSize + sid.Length);
        try
        {
            Marshal.Copy(sid, 0, label + labelSize, sid.Length);
            Marshal.StructureToPtr(new Native.TOKEN_MANDATORY_LABEL { Sid = label + labelSize, Attributes = Native.SeGroupIntegrity }, label, fDeleteOld: false);
            if (!Native.SetTokenInformation(low, Native.TokenIntegrityLevel, label, labelSize + sid.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return low;
        }
        catch
        {
            low.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(label);
        }
    }

    private static bool TryCreate(KernelHandle token, string program, StringBuilder commandLine, string? workingDirectory, SafeFileHandle output,
        SafeFileHandle error, out Native.PROCESS_INFORMATION info, out int lastError)
    {
        // CET's creation policy exists from Windows 10 2004 (19041); before it there are no shadow stacks to turn off.
        var cetOff = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        var attributeCount = cetOff ? 2 : 1;
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
        var policy = Marshal.AllocHGlobal(2 * sizeof(ulong));
        var initialized = false;
        var addedOutput = false;
        var addedError = false;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            initialized = true;
            output.DangerousAddRef(ref addedOutput);
            error.DangerousAddRef(ref addedError);
            Marshal.WriteIntPtr(handles, 0, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, error.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, Native.ProcThreadAttributeHandleList, handles, 2 * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (cetOff)
            {
                // Two DWORD64s: the first mitigation flags (none) and the second, with user shadow stacks always off.
                Marshal.WriteInt64(policy, 0, 0);
                Marshal.WriteInt64(policy, sizeof(ulong), unchecked((long)Native.MitigationPolicy2CetUserShadowStacksAlwaysOff));
                if (!Native.UpdateProcThreadAttribute(attributes, 0, Native.ProcThreadAttributeMitigationPolicy, policy, 2 * sizeof(ulong), IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            var startup = new Native.STARTUPINFOEXW
            {
                StartupInfo = new Native.STARTUPINFOW
                {
                    cb = Marshal.SizeOf<Native.STARTUPINFOEXW>(),
                    dwFlags = Native.StartfUseStdHandles | Native.StartfForceOffFeedback,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = output.DangerousGetHandle(),
                    hStdError = error.DangerousGetHandle(),
                },
                lpAttributeList = attributes,
            };

            const uint flags = Native.ExtendedStartupInfoPresent | Native.CreateNoWindow | Native.CreateSuspended | Native.BelowNormalPriorityClass;
            if (Native.CreateProcessAsUserW(token, program, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, workingDirectory, ref startup,
                    out info))
            {
                lastError = 0;
                return true;
            }

            lastError = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            if (addedOutput)
            {
                output.DangerousRelease();
            }

            if (addedError)
            {
                error.DangerousRelease();
            }

            if (initialized)
            {
                Native.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>A job that kills its processes when closed; null when the child can't be put in one (it still runs).</summary>
    private static KernelHandle? CreateKillOnCloseJob(SafeProcessHandle process)
    {
        var job = new KernelHandle(Native.CreateJobObjectW(IntPtr.Zero, null));
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Native.JobObjectLimitKillOnJobClose | Native.JobObjectLimitDieOnUnhandledException | Native.JobObjectLimitProcessMemory,
            },
            ProcessMemoryLimit = (UIntPtr)MaxProcessMemory,
        };

        if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()) ||
            !Native.AssignProcessToJobObject(job, process))
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    private static void CreateInheritablePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!Native.CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Only the child's end is inherited.
        if (!Native.SetHandleInformation(read, Native.HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>Quotes an argument the way CommandLineToArgvW (and .NET's argument parsing) reads it back.</summary>
    internal static void AppendArgument(StringBuilder line, string argument, bool force)
    {
        if (!force && argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            line.Append(c);
            backslashes = 0;
        }

        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle(IntPtr handle)
            : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static class Native
    {
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const uint CreateNoWindow = 0x08000000;
        public const uint CreateSuspended = 0x00000004;
        public const uint BelowNormalPriorityClass = 0x00004000;
        public const int StartfUseStdHandles = 0x00000100;
        public const int StartfForceOffFeedback = 0x00000080;
        public const uint HandleFlagInherit = 0x00000001;
        public static readonly IntPtr ProcThreadAttributeHandleList = 0x00020002;
        public static readonly IntPtr ProcThreadAttributeMitigationPolicy = 0x00020007;

        /// <summary>PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF (in the second DWORD64).</summary>
        public const ulong MitigationPolicy2CetUserShadowStacksAlwaysOff = 0x00000002UL << 28;

        public const uint TokenAssignPrimary = 0x0001;
        public const uint TokenDuplicate = 0x0002;
        public const uint TokenQuery = 0x0008;
        public const uint TokenAdjustDefault = 0x0080;
        public const uint DisableMaxPrivilege = 0x1;
        public const int TokenIntegrityLevel = 25;
        public const uint SeGroupIntegrity = 0x00000020;

        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JobObjectLimitProcessMemory = 0x00000100;
        public const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
        public const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        /// <summary>TOKEN_MANDATORY_LABEL: its one SID_AND_ATTRIBUTES.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_MANDATORY_LABEL
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEXW
        {
            public STARTUPINFOW StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessAsUserW(KernelHandle token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
            ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateRestrictedToken(KernelHandle existingToken, uint flags, int disableSidCount, IntPtr sidsToDisable,
            int deletePrivilegeCount, IntPtr privilegesToDelete, int restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetTokenInformation(KernelHandle token, int informationClass, IntPtr information, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
            IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SECURITY_ATTRIBUTES attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(KernelHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(KernelHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(KernelHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(KernelHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
