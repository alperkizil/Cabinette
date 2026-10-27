using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace Cabinette.Mcp.Tests;

/// <summary>
/// The started Cabinette must not inherit this process's handles (Claude Code's stdio pipes would stay open in it), and
/// must leave the AI client's job object where the job allows it. A harmless, windowless console child
/// (Cabinette.Mcp.TestHelper "sleep") stands in for Cabinette: nothing with a window is ever started.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed partial class DetachedStartTests(ITestOutputHelper output)
{
    private const uint JobLimitBreakawayOk = 0x00000800;
    private const uint JobLimitSilentBreakawayOk = 0x00001000;
    private const uint JobLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly string Helper = Path.Combine(AppContext.BaseDirectory, "Cabinette.Mcp.TestHelper.exe");

    /// <summary>
    /// An inheritable pipe write end exists while the child starts. Once this process closes its copy, the read end sees
    /// end-of-file at once only if the child holds no copy.
    /// </summary>
    [Fact]
    public async Task TheStartedProgramInheritsNoHandles()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var pid = new DetachedProcessStarter().Start(Helper, ["sleep", "30"], AppContext.BaseDirectory);
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();
            var read = Task.Run(() => pipe.Read(new byte[1], 0, 1));

            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(read, first);
            Assert.Equal(0, await read); // end of file: nobody else holds the write end
            using var child = Process.GetProcessById(pid);
            Assert.False(child.HasExited, "the child should still be running, holding whatever it inherited");
        }
        finally
        {
            Kill(pid);
        }
    }

    [Fact]
    public async Task TheCheckSeesInheritanceWhenItHappens()
    {
        // Control: .NET's Process.Start passes bInheritHandles = TRUE, so this child does hold the write end.
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var info = new ProcessStartInfo(Helper) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("sleep");
        info.ArgumentList.Add("30");
        using var child = Process.Start(info)!;
        Task<int> read;
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();
            read = Task.Run(() => pipe.Read(new byte[1], 0, 1));

            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.NotSame(read, first);
        }
        finally
        {
            child.Kill();
            await child.WaitForExitAsync();
        }

        // With the child gone, the pipe closes.
        Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AStartedProgramHasNoWindow()
    {
        var pid = new DetachedProcessStarter().Start(Helper, ["sleep", "30"], AppContext.BaseDirectory);
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.Equal(nint.Zero, child.MainWindowHandle);
        }
        finally
        {
            Kill(pid);
        }
    }

    /// <summary>
    /// The helper is put into a job with "kill on close" (as an AI client may do with its MCP server) and starts the
    /// stand-in through DetachedProcessStarter. Closing the job (the client exiting) must not end the stand-in when the
    /// job allows breakaway. When it allows none, the stand-in stays in the job and ends with it: the documented case.
    /// </summary>
    [Theory]
    [InlineData(JobLimitBreakawayOk | JobLimitKillOnJobClose, nameof(JobPlacement.Breakaway), true)]
    [InlineData(JobLimitSilentBreakawayOk | JobLimitKillOnJobClose, nameof(JobPlacement.SilentBreakaway), true)]
    [InlineData(JobLimitKillOnJobClose, nameof(JobPlacement.StaysInJob), false)]
    public async Task TheStartedProgramOutlivesAKillOnCloseJobWhenTheJobAllows(uint limits, string expectedPlacement, bool survives)
    {
        var (testInJob, testLimits) = NativeMethods.GetCurrentJob();
        output.WriteLine($"test process in a job: {testInJob}, limits: {testLimits:X}");

        var job = CreateJob(limits);
        var jobOpen = true;
        var childPid = 0;
        try
        {
            var info = new ProcessStartInfo(Helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[] { "start", Helper, "sleep", "60" })
            {
                info.ArgumentList.Add(argument);
            }

            using var helper = Process.Start(info)!;
            Assert.True(AssignProcessToJobObject(job, helper.Handle), $"AssignProcessToJobObject failed ({Marshal.GetLastPInvokeError()})");
            await helper.StandardInput.WriteLineAsync("go");
            await helper.StandardInput.FlushAsync();

            // The stand-in inherits nothing, so the helper's stdout closes when the helper exits.
            var line = (await helper.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20))).Trim();
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            output.WriteLine($"helper: {line}");
            var parts = line.Split(' ');
            childPid = int.Parse(parts[0], CultureInfo.InvariantCulture);
            Assert.Equal(expectedPlacement, parts[1]);

            using var child = Process.GetProcessById(childPid);
            Assert.True(IsProcessInJob(child.Handle, job, out var childInJob));
            Assert.Equal(!survives, childInJob);

            // The AI client exits: the last handle to its job closes.
            CloseHandle(job);
            jobOpen = false;
            var ended = child.WaitForExit(3000);

            Assert.Equal(!survives, ended);
        }
        finally
        {
            if (jobOpen)
            {
                CloseHandle(job);
            }

            if (childPid != 0)
            {
                Kill(childPid);
            }
        }
    }

    private static unsafe nint CreateJob(uint limits)
    {
        var job = CreateJobObject(0, null);
        Assert.NotEqual(0, job);
        var info = new JobExtendedLimits { LimitFlags = limits };
        Assert.True(
            SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, &info, sizeof(JobExtendedLimits)),
            $"SetInformationJobObject failed ({Marshal.GetLastPInvokeError()})");
        return job;
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION, flattened (only the flags are set).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
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
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
