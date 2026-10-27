using System.Runtime.InteropServices;

namespace Cabinette.Mcp.Tests;

/// <summary>The command line given to Cabinette.exe, and the job-object decision.</summary>
public sealed partial class LauncherTests
{
    [Theory]
    [InlineData("--tray")]
    [InlineData(@"C:\Users\Me\AppData\Local\Cabinette")]
    [InlineData(@"C:\Dev data\Cabinette")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\with space\")]
    [InlineData("say \"hi\"")]
    [InlineData(@"back\\slashes\\""quote")]
    [InlineData("")]
    [InlineData("tab\there")]
    public void ArgumentsSurviveTheWindowsCommandLine(string argument)
    {
        var exe = @"C:\Program Files\Cabinette\Cabinette.exe";
        var commandLine = CommandLineText.Build(exe, ["--tray", "--data-dir", argument]);

        Assert.Equal([exe, "--tray", "--data-dir", argument], Split(commandLine));
    }

    [Theory]
    [InlineData(false, null, nameof(JobPlacement.NoJob))]
    [InlineData(true, null, nameof(JobPlacement.Unknown))]
    [InlineData(true, NativeMethods.JobObjectLimitSilentBreakawayOk | NativeMethods.JobObjectLimitKillOnJobClose, nameof(JobPlacement.SilentBreakaway))]
    [InlineData(true, NativeMethods.JobObjectLimitBreakawayOk | NativeMethods.JobObjectLimitKillOnJobClose, nameof(JobPlacement.Breakaway))]
    [InlineData(true, NativeMethods.JobObjectLimitKillOnJobClose, nameof(JobPlacement.StaysInJob))]
    [InlineData(true, 0u, nameof(JobPlacement.StaysInJob))]
    public void TheJobDecidesHowCabinetteLeavesIt(bool inJob, uint? flags, string expected)
        => Assert.Equal(Enum.Parse<JobPlacement>(expected), JobPolicy.Decide(inJob, flags));

    [Fact]
    public void OnlyAKillOnCloseJobEndsCabinetteWithTheClient()
    {
        Assert.True(JobPolicy.KillsOnClose(NativeMethods.JobObjectLimitKillOnJobClose));
        Assert.False(JobPolicy.KillsOnClose(NativeMethods.JobObjectLimitBreakawayOk));
        Assert.False(JobPolicy.KillsOnClose(null));
    }

    [Fact]
    public void TheJobOfThisProcessCanBeRead()
    {
        var (inJob, flags) = NativeMethods.GetCurrentJob();
        if (!inJob)
        {
            Assert.Null(flags);
        }
    }

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgv(commandLine, out var count);
        Assert.NotEqual(0, argv);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgv(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
