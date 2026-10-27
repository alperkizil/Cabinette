using System.Globalization;
using System.IO;
using Cabinette.App.Services;
using Cabinette.App.Services.Downloads;
using Cabinette.App.Services.Monitor;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Downloads;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Tests;

/// <summary>
/// Which folder Clean My Downloads mode watches: dev runs never watch the real Downloads folder. Nothing here asks Windows
/// for it: the real folder is a fake that records whether it was asked.
/// </summary>
public sealed class DownloadsFolderChoiceTests
{
    private const string RealDownloads = @"C:\Users\Someone\Downloads";
    private int _asked;

    private string? Real()
    {
        _asked++;
        return RealDownloads;
    }

    [Fact]
    public void A_normal_run_watches_the_Windows_Downloads_folder()
    {
        Assert.Equal(RealDownloads, DownloadsFolderChoice.Resolve(CommandLineOptions.Parse([]), Real));
        Assert.Equal(RealDownloads, DownloadsFolderChoice.Resolve(CommandLineOptions.Parse(["--tray"]), Real));
    }

    [Theory]
    [InlineData("--data-dir", @"C:\Temp\cab-data")]
    [InlineData("--sample-data", null)]
    [InlineData("--screenshot", @"C:\Temp\shot.png")]
    public void Dev_runs_ignore_the_mode_without_asking_for_the_real_folder(string flag, string? value)
    {
        var options = CommandLineOptions.Parse(value is null ? [flag] : [flag, value]);

        Assert.Null(DownloadsFolderChoice.Resolve(options, Real));
        Assert.Equal(0, _asked);
        Assert.True(DownloadsFolderChoice.IsDeveloperRun(options));
    }

    [Fact]
    public void Downloads_dir_with_data_dir_watches_that_folder()
    {
        var options = CommandLineOptions.Parse(["--data-dir", @"C:\Temp\cab-data", "--downloads-dir", @"C:\Temp\fake-downloads\"]);

        Assert.Equal(@"C:\Temp\fake-downloads", options.DownloadsDirectory!.TrimEnd('\\'));
        Assert.Equal(@"C:\Temp\fake-downloads", DownloadsFolderChoice.Resolve(options, Real));
    }

    [Fact]
    public void Downloads_dir_without_data_dir_is_ignored_and_the_mode_does_not_run()
    {
        var options = CommandLineOptions.Parse(["--downloads-dir", @"C:\Temp\fake-downloads"]);

        Assert.Null(DownloadsFolderChoice.Resolve(options, Real));
        Assert.Equal(0, _asked);
        Assert.True(DownloadsFolderChoice.IsDeveloperRun(options));
    }

    [Fact]
    public void Downloads_dir_never_points_a_dev_run_at_the_real_Downloads_folder()
    {
        var options = CommandLineOptions.Parse(["--data-dir", @"C:\Temp\cab-data", "--downloads-dir", RealDownloads.ToUpperInvariant() + "\\"]);

        Assert.Null(DownloadsFolderChoice.Resolve(options, Real));
    }

    [Fact]
    public void Sample_data_and_screenshots_ignore_downloads_dir_too()
    {
        Assert.Null(DownloadsFolderChoice.Resolve(CommandLineOptions.Parse(["--sample-data", "--data-dir", @"C:\Temp\d", "--downloads-dir", @"C:\Temp\x"]), Real));
        Assert.Null(DownloadsFolderChoice.Resolve(CommandLineOptions.Parse(["--screenshot", "a.png", "--data-dir", @"C:\Temp\d", "--downloads-dir", @"C:\Temp\x"]), Real));
        Assert.Equal(0, _asked);
    }

    [Fact]
    public void Windows_without_a_Downloads_folder_is_not_a_developer_run()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.Null(DownloadsFolderChoice.Resolve(options, () => null));
        Assert.False(DownloadsFolderChoice.IsDeveloperRun(options));
    }

    [Fact]
    public void A_restart_keeps_the_dev_flags()
    {
        Assert.Equal(
            ["--data-dir", "d", "--downloads-dir", "x"],
            AppLifetime.RestartArguments(["--tray", "--data-dir", "d", "--downloads-dir", "x"]));
    }
}

/// <summary>Where the cleaner starts from, and when it holds with the vault monitor.</summary>
public sealed class DownloadsWatermarkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Switching_the_mode_on_starts_from_now()
    {
        var state = new AppState { CleanDownloadsSince = Now.AddDays(-3), CleanDownloadsFolder = @"C:\D" };

        Assert.Equal((Now, true), DownloadsWatermark.ForStart(state, @"C:\D", Now, switchedOn: true));
    }

    [Fact]
    public void A_start_with_the_mode_on_continues_from_the_remembered_watermark()
    {
        var since = Now.AddHours(-8);
        var state = new AppState { CleanDownloadsSince = since, CleanDownloadsFolder = @"c:\d\" };

        Assert.Equal((since, false), DownloadsWatermark.ForStart(state, @"C:\D", Now, switchedOn: false));
    }

    [Theory]
    [InlineData(null, @"C:\D")] // never remembered (or forgotten when switched off)
    [InlineData(-1.0, @"E:\Downloads")] // Downloads was moved: start afresh there
    [InlineData(5.0, @"C:\D")] // in the future: the clock was set back
    public void Otherwise_it_starts_from_now(double? hours, string folder)
    {
        var state = new AppState { CleanDownloadsSince = hours is { } h ? Now.AddHours(h) : null, CleanDownloadsFolder = folder };

        Assert.Equal((Now, true), DownloadsWatermark.ForStart(state, @"C:\D", Now, switchedOn: false));
    }

    [Theory]
    [InlineData(true, VaultMonitorState.Paused, true)]
    [InlineData(true, VaultMonitorState.VaultUnavailable, true)]
    [InlineData(true, VaultMonitorState.Idle, false)]
    [InlineData(true, VaultMonitorState.Syncing, false)]
    [InlineData(true, VaultMonitorState.Stopped, false)]
    [InlineData(false, VaultMonitorState.Paused, false)]
    public void Pausing_monitoring_holds_the_cleaner(bool available, VaultMonitorState state, bool holds)
    {
        Assert.Equal(holds, DownloadsCleanerHost.Holds(available, new VaultMonitorStatus(state, 0, null, null)));
    }

    [Fact]
    public void The_setting_is_off_by_default()
    {
        Assert.False(new AppSettings().CleanMyDownloads);
    }
}

/// <summary>The notice texts ("3 files from Downloads added to Images").</summary>
public sealed class DownloadsTextTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public DownloadsTextTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static string Name(long id) => id switch
    {
        1 => "Images",
        2 => "Audio",
        3 => "Video",
        4 => "Documents",
        _ => "",
    };

    private static LibraryItem Item(long category, string name = "x") => new() { Id = category * 100, FileName = name, FullPath = @"V:\" + name, HomeCategoryId = category };

    private static DownloadOutcome Moved(long category, string file = "a.jpg") => new(@"C:\Downloads\" + file, DownloadOutcomeKind.Moved, Item(category, file));

    [Fact]
    public void Files_moved_to_one_category()
    {
        var message = DownloadsText.Build([Moved(1), Moved(1, "b.jpg"), Moved(1, "c.png")], Name, includeSuccess: true)!;

        Assert.Equal(["3 files from Downloads added to Images"], message.Lines);
        Assert.Equal("Clean My Downloads", message.Title);
        Assert.Equal(1, message.CategoryId);
        Assert.False(message.HasProblems);
    }

    [Fact]
    public void One_file_and_several_categories()
    {
        Assert.Equal(["1 file from Downloads added to Audio"], DownloadsText.Build([Moved(2)], Name, true)!.Lines);

        var two = DownloadsText.Build([Moved(1), Moved(2), Moved(1, "b.jpg")], Name, true)!;
        Assert.Equal(["3 files from Downloads added to Images and Audio"], two.Lines);
        Assert.Null(two.CategoryId);

        var many = DownloadsText.Build([Moved(1), Moved(1, "b"), Moved(2), Moved(3), Moved(4)], Name, true)!;
        Assert.Equal(["5 files from Downloads added to Images, Audio and 2 more"], many.Lines);
    }

    [Fact]
    public void Files_already_in_Cabinette()
    {
        var one = new DownloadOutcome(@"C:\Downloads\invoice (1).pdf", DownloadOutcomeKind.AlreadyInLibrary, Item(4, "invoice.pdf"));
        var other = one with { SourcePath = @"C:\Downloads\other.pdf" };

        Assert.Equal(["“invoice (1).pdf” from Downloads was already in Cabinette (in: Documents)"], DownloadsText.Build([one], Name, true)!.Lines);
        Assert.Equal(["2 files from Downloads were already in Cabinette"], DownloadsText.Build([one, other], Name, true)!.Lines);
    }

    [Fact]
    public void Files_that_stay_in_Downloads_say_why()
    {
        DownloadOutcome Kept(string file, DownloadKeptReason reason) => new(@"C:\Downloads\" + file, DownloadOutcomeKind.Kept, Item(3, file), reason);

        Assert.Equal(["“big.iso” stays in Downloads: it's in your vault, but it can't go to the Recycle Bin"],
            DownloadsText.Build([Kept("big.iso", DownloadKeptReason.NoRecycleBin)], Name, true)!.Lines);
        Assert.Equal(["“song.mp3” stays in Downloads: Cabinette links to it as a pointer file, so the vault has no copy of it"],
            DownloadsText.Build([Kept("song.mp3", DownloadKeptReason.LinkedAsPointer)], Name, true)!.Lines);
        Assert.Equal(["“a.pdf” stays in Downloads: it's in your vault, but moving it to the Recycle Bin failed"],
            DownloadsText.Build([Kept("a.pdf", DownloadKeptReason.RecycleFailed)], Name, true)!.Lines);
        Assert.Equal(["“b.pdf” stays in Downloads: its copy in your vault doesn't match it, so Cabinette leaves it here"],
            DownloadsText.Build([Kept("b.pdf", DownloadKeptReason.VaultCopyUnchecked)], Name, true)!.Lines);
        Assert.Equal(["2 files stay in Downloads: they're in your vault, but they can't go to the Recycle Bin"],
            DownloadsText.Build([Kept("a", DownloadKeptReason.NoRecycleBin), Kept("b", DownloadKeptReason.NoRecycleBin)], Name, true)!.Lines);
        Assert.Equal(["2 files stay in Downloads"],
            DownloadsText.Build([Kept("a", DownloadKeptReason.NoRecycleBin), Kept("b", DownloadKeptReason.LinkedAsPointer)], Name, true)!.Lines);
    }

    [Fact]
    public void Failures()
    {
        var failed = new DownloadOutcome(@"C:\Downloads\photo.png", DownloadOutcomeKind.Failed, Message: "There is not enough space on the disk.");

        Assert.Equal(["Couldn't move “photo.png” from Downloads: There is not enough space on the disk."], DownloadsText.Build([failed], Name, true)!.Lines);
        Assert.Equal(["2 files couldn't be moved from Downloads. Cabinette tries again later."],
            DownloadsText.Build([failed, failed with { SourcePath = @"C:\Downloads\b.png" }], Name, true)!.Lines);
    }

    [Fact]
    public void With_notifications_off_only_problems_are_reported()
    {
        var failed = new DownloadOutcome(@"C:\Downloads\photo.png", DownloadOutcomeKind.Failed, Message: "Access denied.");
        var kept = new DownloadOutcome(@"C:\Downloads\big.iso", DownloadOutcomeKind.Kept, Item(3), DownloadKeptReason.NoRecycleBin);

        Assert.Null(DownloadsText.Build([Moved(1)], Name, includeSuccess: false));
        var message = DownloadsText.Build([Moved(1), kept, failed], Name, includeSuccess: false)!;
        Assert.Equal(2, message.Lines.Count);
        Assert.True(message.HasProblems);
        Assert.Null(message.CategoryId);
    }

    [Fact]
    public void Everything_together_in_order()
    {
        var already = new DownloadOutcome(@"C:\Downloads\dup.pdf", DownloadOutcomeKind.AlreadyInLibrary, Item(4, "dup.pdf"));
        var kept = new DownloadOutcome(@"C:\Downloads\big.iso", DownloadOutcomeKind.Kept, Item(3), DownloadKeptReason.NoRecycleBin);
        var message = DownloadsText.Build([Moved(1), already, kept], Name, includeSuccess: true)!;

        Assert.Equal(3, message.Lines.Count);
        Assert.StartsWith("1 file from Downloads added to Images · “dup.pdf”", message.OneLine, StringComparison.Ordinal);
        Assert.Equal(1, message.CategoryId);
        Assert.True(message.HasProblems);
    }
}

/// <summary>
/// The setting in Settings, its first-time confirm, and the host running the mode on a temp "Downloads" folder (a
/// <c>--data-dir --downloads-dir</c> dev run): only new files move, pausing monitoring holds them, switching off stops it.
/// </summary>
public sealed class CleanDownloadsSettingTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "downloads-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_confirm_shows_the_first_time_only()
    {
        var dialogs = new SwitchableDialogs();
        var store = new AppStateStore(new AppPaths(Path.Combine(_root, "data")));
        var confirmation = new DownloadsModeConfirmation(dialogs, store, CommandLineOptions.Parse(["--data-dir", Path.Combine(_root, "data")]));

        dialogs.Answer = false;
        Assert.False(await confirmation.ConfirmTurnOnAsync());
        Assert.False(store.Load().CleanDownloadsConfirmed);
        Assert.Equal("New downloads will be moved into your vault and the originals sent to the Recycle Bin.", dialogs.Messages.Single());

        dialogs.Answer = true;
        Assert.True(await confirmation.ConfirmTurnOnAsync());
        Assert.True(store.Load().CleanDownloadsConfirmed);

        dialogs.Answer = false;
        Assert.True(await new DownloadsModeConfirmation(dialogs, store, CommandLineOptions.Parse([])).ConfirmTurnOnAsync());
        Assert.Equal(2, dialogs.Messages.Count);
        await StateWrittenAsync(store, Path.Combine(_root, "data"));
        TryDelete(_root);
    }

    [Fact]
    public async Task Sample_data_runs_remember_the_confirm_in_memory_only()
    {
        var dialogs = new SwitchableDialogs { Answer = true };
        var store = new AppStateStore(new AppPaths(Path.Combine(_root, "data")));
        var confirmation = new DownloadsModeConfirmation(dialogs, store, CommandLineOptions.Parse(["--sample-data"]));

        Assert.True(await confirmation.ConfirmTurnOnAsync());
        Assert.True(await confirmation.ConfirmTurnOnAsync());

        Assert.Single(dialogs.Messages);
        Assert.False(store.Load().CleanDownloadsConfirmed);
        TryDelete(_root);
    }

    [Fact]
    public void The_toggle_asks_first_and_backs_off_when_declined() => Dispatched.Run(_root, async () =>
    {
        var library = await OpenLibraryAsync();
        var store = new AppStateStore(new AppPaths(Path.Combine(_root, "data")));
        try
        {
            var dialogs = new SwitchableDialogs { Answer = false };
            var vm = CreateSettings(library, dialogs, store);
            await vm.LoadAsync();
            Assert.False(vm.CleanMyDownloads);

            vm.CleanMyDownloads = true;
            await Until(() => !vm.CleanMyDownloads, "the declined toggle backs off");
            Assert.Single(dialogs.Messages);
            Assert.False((await library.GetSettingsAsync()).CleanMyDownloads);

            dialogs.Answer = true;
            vm.CleanMyDownloads = true;
            await Until(async () => (await library.GetSettingsAsync()).CleanMyDownloads, "the setting is saved on");
            Assert.True(vm.CleanMyDownloads);

            vm.CleanMyDownloads = false;
            await Until(async () => !(await library.GetSettingsAsync()).CleanMyDownloads, "the setting is saved off");
            vm.CleanMyDownloads = true;
            await Until(async () => (await library.GetSettingsAsync()).CleanMyDownloads, "the setting is saved on again");
            Assert.Equal(2, dialogs.Messages.Count); // confirmed once: not asked again
            Assert.True(store.Load().CleanDownloadsConfirmed);
        }
        finally
        {
            await library.DisposeAsync();
            await StateWrittenAsync(store, Path.Combine(_root, "data"));
        }
    });

    [Fact]
    public void A_dev_run_cleans_only_its_own_folder_holds_while_paused_and_stops_when_switched_off() => Dispatched.Run(_root, async () =>
    {
        var library = await OpenLibraryAsync();
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "fake downloads")).FullName;
        var before = Path.Combine(downloads, "was there before.pdf");
        File.WriteAllText(before, "old");
        File.SetCreationTimeUtc(before, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(before, DateTime.UtcNow.AddDays(-2));

        var data = Path.Combine(_root, "data");
        var options = CommandLineOptions.Parse(["--data-dir", data, "--downloads-dir", downloads]);
        var settings = new SettingsCache(library);
        settings.Initialize(await library.GetSettingsAsync() with { CleanMyDownloads = true });
        var store = new AppStateStore(new AppPaths(data));
        var monitor = new VaultMonitorHost(library);
        var askedForRealFolder = false;
        var host = new DownloadsCleanerHost(library, new MovingRecycleBin(Path.Combine(_root, "recycled")), settings, store, monitor,
            new NotificationService(), options, () =>
            {
                askedForRealFolder = true;
                return @"C:\Not\The\Real\Downloads";
            }, TimeProvider.System)
        {
            AdjustOptions = o => o with
            {
                QuietPeriod = TimeSpan.FromMilliseconds(300),
                EventDelay = TimeSpan.FromMilliseconds(50),
                RescanInterval = TimeSpan.FromMilliseconds(500),
            },
        };
        var batches = new List<DownloadsBatch>();
        host.Processed += (_, batch) => batches.Add(batch);
        try
        {
            host.Start();
            await Until(() => host.Cleaner is not null, "the cleaner started");
            Assert.True(askedForRealFolder); // only to make sure --downloads-dir isn't the real folder
            Assert.True(DownloadsFolderChoice.PathsEqual(downloads, store.Load().CleanDownloadsFolder));
            Assert.NotNull(store.Load().CleanDownloadsSince);

            await Task.Delay(50);
            var song = Path.Combine(downloads, "song.mp3");
            File.WriteAllText(song, "music");
            await Until(() => !File.Exists(song), "the new download moved");
            await Until(() => batches.Count > 0, "the batch reached the UI thread");
            var moved = Assert.Single(Assert.Single(batches).Outcomes);
            Assert.Equal(DownloadOutcomeKind.Moved, moved.Kind);
            Assert.Equal(AddedBy.Downloads, moved.Item!.AddedBy);
            Assert.True(File.Exists(before)); // there before the mode was on

            // "Pause monitoring" in the tray holds it; resuming catches up.
            monitor.ShowPreview(new VaultMonitorStatus(VaultMonitorState.Paused, 0, null, null));
            await Until(() => host.Cleaner!.IsPaused, "the cleaner holds with the monitor");
            var clip = Path.Combine(downloads, "clip.mp4");
            File.WriteAllText(clip, "video");
            await Task.Delay(TimeSpan.FromSeconds(1.2));
            Assert.True(File.Exists(clip));
            monitor.ShowPreview(new VaultMonitorStatus(VaultMonitorState.Idle, 0, DateTimeOffset.Now, null));
            await Until(() => !File.Exists(clip), "the held download moved after resuming");

            // Switched off: stops, and forgets the watermark.
            await host.ApplyAsync(on: false, switchedOn: false);
            Assert.Null(host.Cleaner);
            Assert.Null(store.Load().CleanDownloadsSince);
            var later = Path.Combine(downloads, "later.txt");
            File.WriteAllText(later, "later");
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.True(File.Exists(later));
        }
        finally
        {
            await host.DisposeAsync();
            settings.Dispose();
            await monitor.DisposeAsync();
            await library.DisposeAsync();
            await StateWrittenAsync(store, data);
        }
    });

    private async Task<ILibraryService> OpenLibraryAsync()
    {
        Directory.CreateDirectory(_root);
        var library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.Combine(_root, "data") }, new MovingRecycleBin(Path.Combine(_root, "recycled")));
        await library.InitializeVaultAsync(Path.Combine(_root, "vault"));
        return library;
    }

    private SettingsViewModel CreateSettings(ILibraryService library, IDialogService dialogs, AppStateStore store)
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));
        var directory = new CategoryDirectory(library);
        var dialogService = new DialogService(library, new CategoryIconService(library, new FakeIconGenerator()), directory);
        var notifications = new NotificationService();
        var monitor = new VaultMonitorHost(library);
        var recycleBin = new MovingRecycleBin(Path.Combine(_root, "recycled"));
        var shell = new Cabinette.Windows.ShellOperations();
        var operations = new VaultOperationsService(library, dialogService, notifications, recycleBin, shell, paths, monitor, new AppLifetime());
        var options = CommandLineOptions.Parse(["--data-dir", paths.DataDirectory]);
        return new SettingsViewModel(library, new FakeTheme(), notifications, shell, monitor, operations,
            new DownloadsModeConfirmation(dialogs, store, options));
    }

    /// <summary>
    /// <see cref="AppStateStore"/> writes state.json in the background: wait until the file holds the latest state, so
    /// deleting the temp folder afterwards isn't undone by a late write.
    /// </summary>
    private static async Task StateWrittenAsync(AppStateStore store, string dataDirectory)
    {
        var file = new AppPaths(dataDirectory).AppStateFile;
        var expected = System.Text.Json.JsonSerializer.Serialize(store.Load(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        bool Written()
        {
            try
            {
                return File.Exists(file) && File.ReadAllText(file) == expected;
            }
            catch (IOException)
            {
                return false; // Being written.
            }
        }

        // Each write saves the latest state, so once it is there only same-content writes can still be queued: give them a moment.
        await Until(Written, "state.json was written");
        await Task.Delay(300);
        await Until(Written, "state.json stayed written");
    }

    private static Task Until(Func<bool> condition, string what) => Until(() => Task.FromResult(condition()), what);

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    internal static void TryDelete(string root)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>A test Recycle Bin: "recycled" files are moved into a temp folder. Never the real one.</summary>
    private sealed class MovingRecycleBin(string folder) : IRecycleBin
    {
        public void SendToRecycleBin(IReadOnlyCollection<string> paths)
        {
            Directory.CreateDirectory(folder);
            foreach (var path in paths.Where(File.Exists))
            {
                File.Move(path, Path.Combine(folder, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path)));
            }
        }
    }

    private sealed class SwitchableDialogs : IDialogService
    {
        public bool Answer { get; set; }

        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
        {
            Messages.Add(message);
            return Task.FromResult(Answer);
        }

        public Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
            => Task.FromResult<string?>(null);

        public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options) => Task.FromResult<int?>(null);

        public Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true) => Task.FromResult<long?>(null);

        public Task<string?> PickFileToLocateAsync(string originalFileName) => Task.FromResult<string?>(null);
    }

    private sealed class FakeTheme : IThemeService
    {
        public ThemePreference Preference => ThemePreference.System;

        public bool IsDark => false;

        public event EventHandler? ThemeChanged
        {
            add { }
            remove { }
        }

        public void Apply(ThemePreference preference)
        {
        }
    }
}

/// <summary>Runs a test body on an STA thread with a dispatcher, so awaits come back to it as in the app.</summary>
internal static class Dispatched
{
    public static void Run(string root, Func<Task> body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var frame = new System.Windows.Threading.DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The test body did not finish.");
        CleanDownloadsSettingTests.TryDelete(root);
        failure?.Throw();
    }
}
