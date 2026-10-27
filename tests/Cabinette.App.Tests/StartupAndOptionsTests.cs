using Cabinette.App.Services;

namespace Cabinette.App.Tests;

/// <summary>Only the command-line logic: nothing here touches the real registry.</summary>
public class StartupCommandTests
{
    private const string Exe = @"C:\Program Files\Cabinette\Cabinette.exe";

    [Fact]
    public void The_run_command_quotes_the_path_and_starts_in_the_tray()
    {
        Assert.Equal("\"C:\\Program Files\\Cabinette\\Cabinette.exe\" --tray", StartupCommand.Build(Exe));
        Assert.Equal("\"C:\\Program Files\\Cabinette\\Cabinette.exe\" --tray", StartupCommand.Build("\"" + Exe + "\""));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Cabinette\\Cabinette.exe\" --tray", true)]
    [InlineData("\"c:\\program files\\cabinette\\CABINETTE.EXE\"   --TRAY ", true)]
    [InlineData("\"C:\\Program Files\\Cabinette\\Cabinette.exe\"", false)]
    [InlineData("\"C:\\Program Files\\Cabinette\\Cabinette.exe\" --tray --sample-data", false)]
    [InlineData("\"D:\\Old\\Cabinette.exe\" --tray", false)]
    [InlineData("C:\\Program Files\\Cabinette\\Cabinette.exe --tray", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_exact_match_counts_as_current(string? existing, bool expected)
    {
        Assert.Equal(expected, StartupCommand.IsCurrent(existing, Exe));
    }

    [Fact]
    public void Split_handles_quoted_and_unquoted_paths()
    {
        Assert.Equal((Exe, "--tray"), StartupCommand.Split($"\"{Exe}\" --tray"));
        Assert.Equal((@"C:\Apps\x.exe", "-a -b"), StartupCommand.Split(@"C:\Apps\x.exe -a -b"));
        Assert.Equal((@"C:\Apps\x.exe", ""), StartupCommand.Split(@"C:\Apps\x.exe"));
    }

    [Fact]
    public async Task The_manager_adds_fixes_and_removes_the_entry()
    {
        var registration = new RecordingRegistration();
        var manager = new StartupManager(registration, Exe);

        await manager.ApplyAsync(enabled: true);
        Assert.Equal(StartupCommand.Build(Exe), registration.Value);
        Assert.Equal(1, registration.Writes);

        await manager.ApplyAsync(enabled: true);
        Assert.Equal(1, registration.Writes); // already current: untouched

        registration.Value = "\"D:\\Old\\Cabinette.exe\" --tray";
        await manager.ApplyAsync(enabled: true);
        Assert.Equal(StartupCommand.Build(Exe), registration.Value);
        Assert.Equal(2, registration.Writes);

        await manager.ApplyAsync(enabled: false);
        Assert.Null(registration.Value);
        Assert.Equal(1, registration.Removes);

        await manager.ApplyAsync(enabled: false);
        Assert.Equal(1, registration.Removes); // nothing to remove
    }

    private sealed class RecordingRegistration : IStartupRegistration
    {
        public string? Value { get; set; }

        public int Writes { get; private set; }

        public int Removes { get; private set; }

        public string? Read() => Value;

        public void Write(string command)
        {
            Value = command;
            Writes++;
        }

        public void Remove()
        {
            Value = null;
            Removes++;
        }
    }
}

public class CommandLineOptionsTests
{
    [Fact]
    public void Tray_flag_is_parsed()
    {
        Assert.True(CommandLineOptions.Parse(["--tray"]).StartInTray);
        Assert.True(CommandLineOptions.Parse(["--TRAY"]).StartInTray);
        Assert.False(CommandLineOptions.Parse([]).StartInTray);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--tray" }, false)]
    [InlineData(new[] { "--data-dir", @"C:\Temp\dev" }, true)]
    [InlineData(new[] { "--sample-data" }, true)]
    [InlineData(new[] { "--screenshot", "x.png" }, true)]
    public void Dev_runs_never_touch_the_windows_setup(string[] args, bool isDevRun)
    {
        Assert.Equal(isDevRun, CommandLineOptions.Parse(args).IsDevRun);
    }

    [Fact]
    public void Only_screenshots_and_sample_data_skip_the_single_instance()
    {
        Assert.False(CommandLineOptions.Parse(["--data-dir", @"C:\Temp\dev"]).IsSandboxed);
        Assert.True(CommandLineOptions.Parse(["--sample-data"]).IsSandboxed);
        Assert.True(CommandLineOptions.Parse(["--screenshot", "x.png"]).IsSandboxed);
    }
}
