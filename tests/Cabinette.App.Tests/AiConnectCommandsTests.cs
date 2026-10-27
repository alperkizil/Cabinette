using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>Settings › AI's registration commands (SPEC.md section 10) and their copy buttons, with a fake clipboard.</summary>
public sealed class AiConnectCommandsTests
{
    private const string Exe = @"C:\Program Files\Cabinette\cabinette-mcp.exe";

    [Fact]
    public void Installed_copy_registers_as_cabinette()
    {
        var commands = AiConnectCommands.Build(Exe, dev: null);

        Assert.Equal("cabinette", commands.ServerName);
        Assert.Equal(@"claude mcp add --scope user cabinette -- ""C:\Program Files\Cabinette\cabinette-mcp.exe""", commands.ClaudeAdd);
        Assert.Equal(@"codex mcp add cabinette -- ""C:\Program Files\Cabinette\cabinette-mcp.exe""", commands.CodexAdd);
        Assert.Equal("claude mcp remove cabinette --scope user", commands.ClaudeRemove);
        Assert.Equal("codex mcp remove cabinette", commands.CodexRemove);
    }

    /// <summary>
    /// Like the README's developer section: "cabinette-dev" in Claude Code's local scope, with the data folder and the
    /// Cabinette.exe to start as full paths (a dev cabinette-mcp.exe refuses relative ones and only starts the app next to
    /// itself or the one given by --app).
    /// </summary>
    [Fact]
    public void Dev_run_registers_as_cabinette_dev_locally_with_its_data_folder_and_app()
    {
        var commands = AiConnectCommands.Build(
            @"D:\src\Cabinette\src\Cabinette.Mcp\bin\Debug\net10.0-windows\cabinette-mcp.exe",
            new AiDevRun(@"C:\Temp\cab dev", @"D:\src\Cabinette\src\Cabinette.App\bin\Debug\net10.0-windows10.0.19041.0\Cabinette.exe"));

        const string Server = @"""D:\src\Cabinette\src\Cabinette.Mcp\bin\Debug\net10.0-windows\cabinette-mcp.exe"" --data-dir ""C:\Temp\cab dev"" --app ""D:\src\Cabinette\src\Cabinette.App\bin\Debug\net10.0-windows10.0.19041.0\Cabinette.exe""";
        Assert.Equal("cabinette-dev", commands.ServerName);
        Assert.Equal("claude mcp add --scope local cabinette-dev -- " + Server, commands.ClaudeAdd);
        Assert.Equal("codex mcp add cabinette-dev -- " + Server, commands.CodexAdd);
        Assert.Equal("claude mcp remove cabinette-dev --scope local", commands.ClaudeRemove);
        Assert.Equal("codex mcp remove cabinette-dev", commands.CodexRemove);
    }

    [Theory]
    [InlineData(@"C:\Temp\dev\", @"""C:\Temp\dev""")]
    [InlineData(@"C:\Temp\dev", @"""C:\Temp\dev""")]
    [InlineData(@"  C:\Temp\dev  ", @"""C:\Temp\dev""")]
    [InlineData(@"D:\", @"""D:\.""")]
    [InlineData(@"\\server\share\", @"""\\server\share""")]
    public void Quoting_never_ends_with_a_backslash_before_the_quote(string path, string expected)
        => Assert.Equal(expected, AiConnectCommands.Quote(path));

    [Fact]
    public void A_quoted_path_reads_back_unchanged_by_the_windows_rules()
    {
        // CommandLineToArgvW's rules: 2n backslashes before a quote are n backslashes; the quotes group the spaces.
        foreach (var path in new[] { @"C:\Program Files\Cabinette\cabinette-mcp.exe", @"C:\Temp\dev\", @"D:\" })
        {
            var parsed = SplitWindowsArguments("x " + AiConnectCommands.Quote(path))[1];
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(parsed)));
        }
    }

    [Fact]
    public void The_default_path_is_next_to_Cabinette_exe()
        => Assert.Equal(Path.Combine(AppContext.BaseDirectory, "cabinette-mcp.exe"), AiConnectCommands.DefaultExePath);

    [Fact]
    public void Settings_say_when_the_exe_is_missing() => RunUi(async () =>
    {
        var missing = Path.Combine(Path.GetTempPath(), "cabinette-tests-" + Guid.NewGuid().ToString("N"), "cabinette-mcp.exe");
        using var settings = CreateSettings(missing, dev: null);

        await settings.LoadAsync();

        Assert.Equal($"cabinette-mcp.exe wasn't found in {Path.GetDirectoryName(missing)}. Reinstall Cabinette before running these commands.", settings.ExeMissingText);
        Assert.Null(settings.DevNote);
    });

    [Fact]
    public void Settings_show_no_hint_when_the_exe_is_there() => RunUi(async () =>
    {
        var folder = Directory.CreateTempSubdirectory("cabinette-tests-").FullName;
        try
        {
            var exe = Path.Combine(folder, "cabinette-mcp.exe");
            await File.WriteAllBytesAsync(exe, []);
            using var settings = CreateSettings(exe, dev: new AiDevRun(@"C:\Temp\dev", @"C:\Temp\app\Cabinette.exe"), devSearchStart: folder);

            await settings.LoadAsync();

            Assert.Null(settings.ExeMissingText);
            Assert.Equal($@"claude mcp add --scope local cabinette-dev -- ""{exe}"" --data-dir ""C:\Temp\dev"" --app ""C:\Temp\app\Cabinette.exe""", settings.ClaudeCode.Text);
            Assert.Equal("This dev run registers as “cabinette-dev” with its own data folder and this Cabinette.exe, so it never replaces your real registration. Claude Code keeps it only for the folder you run the command in.", settings.DevNote);
            Assert.Equal("claude mcp remove cabinette-dev --scope local", settings.ClaudeRemove);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void A_dev_run_is_one_with_a_data_folder()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var log = new StoredActivityLog();
        var navigator = new FakeNavigator();
        var clipboard = new FakeClipboard();
        var paths = new AppPaths(@"C:\Temp\cab data");

        using var dev = new AiSettingsViewModel(log, library, navigator, clipboard, CommandLineOptions.Parse(["--data-dir", @"C:\Temp\cab data"]), paths);
        using var installed = new AiSettingsViewModel(log, library, navigator, clipboard, CommandLineOptions.Parse([]), paths);

        Assert.Equal("cabinette-dev", dev.Commands.ServerName);
        Assert.EndsWith($@"--data-dir ""C:\Temp\cab data"" --app ""{AiConnectCommands.RunningAppPath}""", dev.Codex.Text, StringComparison.Ordinal);
        Assert.Equal("cabinette", installed.Commands.ServerName);
        Assert.DoesNotContain("--data-dir", installed.Codex.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_puts_the_command_on_the_clipboard_and_says_so() => RunUi(async () =>
    {
        var clipboard = new FakeClipboard();
        using var settings = CreateSettings(Exe, null, clipboard);

        var copying = settings.Codex.CopyCommand.ExecuteAsync(null);

        Assert.Equal([settings.Codex.Text], clipboard.Texts);
        Assert.Equal(CopyState.Copied, settings.Codex.State);
        Assert.Equal("Copied", settings.Codex.CopyLabel);
        await copying;
        Assert.Equal(CopyState.Ready, settings.Codex.State);
        Assert.Equal("Copy", settings.Codex.CopyLabel);
    });

    [Fact]
    public void A_busy_clipboard_says_it_couldnt_copy() => RunUi(async () =>
    {
        var clipboard = new FakeClipboard { Succeeds = false };
        using var settings = CreateSettings(Exe, null, clipboard);

        var copying = settings.ClaudeCode.CopyCommand.ExecuteAsync(null);

        Assert.Equal(CopyState.Failed, settings.ClaudeCode.State);
        Assert.Equal("Couldn't copy", settings.ClaudeCode.CopyLabel);
        await copying;
    });

    internal static AiSettingsViewModel CreateSettings(string exe, AiDevRun? dev, FakeClipboard? clipboard = null, string? devSearchStart = null)
    {
        var library = SampleLibraryService.Create(withVault: true);
        var log = new StoredActivityLog();
        var navigator = new FakeNavigator();
        var activity = new AiActivityViewModel(log, library, navigator, () => { }, Dispatcher.CurrentDispatcher, TimeProvider.System);
        return new AiSettingsViewModel(log, library, navigator, clipboard ?? new FakeClipboard(), exe, dev, activity, devSearchStart);
    }

    /// <summary>Runs an async test on an STA thread with a WPF dispatcher as its synchronization context.</summary>
    internal static void RunUi(Func<Task> test) => HiddenWpf.RunSta(() =>
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = test();
        if (!HiddenWpf.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("The UI test did not finish.");
        }

        task.GetAwaiter().GetResult();
    });

    /// <summary>The way Windows programs split their command line (CommandLineToArgvW, without the program-name rule).</summary>
    private static List<string> SplitWindowsArguments(string line)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var start = i;
                while (i < line.Length && line[i] == '\\')
                {
                    i++;
                }

                var count = i - start;
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        any = true;
                        continue;
                    }

                    i--;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }

                any = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}

/// <summary>An activity log in memory; <see cref="Gate"/> holds reads back; entries can be announced from another thread.</summary>
internal sealed class StoredActivityLog : IAiActivityLog
{
    private readonly Cabinette.Core.Services.InMemoryActivityLog _store = new();

    public event EventHandler<ActivityEntry>? EntryAdded;

    /// <summary>While set, reads wait for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<ActivityQuery> Queries { get; } = [];

    public bool Fails { get; set; }

    public ActivityEntry Add(DateTimeOffset at, string client, string tool, AiActivityDetails? details = null)
        => _store.Add(new ActivityEntry { At = at, Source = client, Action = tool, DetailsJson = details?.ToJson() });

    /// <summary>Stores an entry and raises <see cref="EntryAdded"/> on a thread-pool thread, like the real log.</summary>
    public ActivityEntry AddLive(DateTimeOffset at, string client, string tool, AiActivityDetails? details = null)
    {
        var entry = Add(at, client, tool, details);
        Task.Run(() => EntryAdded?.Invoke(this, entry)).Wait();
        return entry;
    }

    public Task<ActivityEntry?> LogAsync(string client, string tool, AiActivityDetails details)
        => Task.FromResult<ActivityEntry?>(AddLive(DateTimeOffset.Now, client, tool, details));

    public async Task<ActivityPage> GetAsync(ActivityQuery query, CancellationToken ct = default)
    {
        lock (Queries)
        {
            Queries.Add(query);
        }

        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (Fails)
        {
            throw new Cabinette.Core.Services.LibraryException(Cabinette.Core.Services.LibraryErrorCode.Unknown, "The library is closed.");
        }

        return _store.Get(query);
    }
}

internal sealed class FakeNavigator : IShellNavigator
{
    public List<(long CategoryId, long? ItemId)> Opened { get; } = [];

    public long? CurrentCategoryId => null;

    public void OpenCategory(long categoryId, long? focusItemId = null) => Opened.Add((categoryId, focusItemId));
}

internal sealed class FakeClipboard : IClipboardWriter
{
    public bool Succeeds { get; set; } = true;

    public List<string> Texts { get; } = [];

    public bool TrySetText(string text)
    {
        if (Succeeds)
        {
            Texts.Add(text);
        }

        return Succeeds;
    }
}
