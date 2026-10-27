using Cabinette.Core.Categorization;
using Cabinette.Core.Downloads;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>
/// Clean My Downloads mode (SPEC.md section 5) against a temp "Downloads" folder, a real library and the fake Recycle Bin.
/// Most tests drive single passes with the fake clock and set the files' timestamps to it; the last ones run the real
/// watcher and timers with short delays.
/// </summary>
public sealed class DownloadsCleanerTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InUseRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(5);

    // ------------------------------------------------------------ the quiet period

    [Fact]
    public async Task A_new_download_moves_after_a_quiet_minute_and_the_original_goes_to_the_Recycle_Bin()
    {
        await using var f = await Fixture.CreateAsync();
        var arrived = f.Now;
        var photo = f.Arrive("photo.jpg", "jpeg bytes");

        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.Equal(arrived + Quiet, f.Cleaner.NextDue);
        f.Advance(TimeSpan.FromSeconds(59));
        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(photo));

        f.Advance(TimeSpan.FromSeconds(1));
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal(photo, outcome.SourcePath);
        var images = await f.T.BuiltInAsync(ExtensionMap.ImagesKey);
        var item = outcome.Item!;
        Assert.Equal(images.Id, item.HomeCategoryId); // auto-categorized, like a drop onto the main window
        Assert.Equal(ItemMode.Copy, item.Mode);
        Assert.Equal(AddedBy.Downloads, item.AddedBy);
        Assert.Equal("jpeg bytes", File.ReadAllText(item.FullPath));
        Assert.False(File.Exists(photo));
        Assert.True(f.T.RecycleBin.WasRecycled(photo));
        Assert.Equal(1, (await f.T.SummaryAsync(images.Id)).UnreadCount); // an outside add: unread
        Assert.Single(f.Batches);
    }

    [Fact]
    public async Task A_change_or_a_watcher_event_starts_the_quiet_period_again()
    {
        await using var f = await Fixture.CreateAsync();
        var report = f.Arrive("report.pdf", "v1");
        Assert.True((await f.PassAsync()).IsEmpty);

        // Saved again 50 s later: a minute from then.
        f.Advance(TimeSpan.FromSeconds(50));
        File.WriteAllText(report, "version 2");
        File.SetLastWriteTimeUtc(report, f.Now.UtcDateTime);
        Assert.True((await f.PassAsync()).IsEmpty);
        f.Advance(TimeSpan.FromSeconds(50));
        Assert.True((await f.PassAsync()).IsEmpty);

        // Touched without a visible change (an event only), 5 s before the minute is up.
        f.Advance(TimeSpan.FromSeconds(5));
        f.Cleaner.NoteChanged(report);
        f.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(report));

        f.Advance(TimeSpan.FromSeconds(55));
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal("version 2", File.ReadAllText(outcome.Item!.FullPath));
    }

    [Fact]
    public async Task Several_ready_files_move_as_one_batch()
    {
        await using var f = await Fixture.CreateAsync();
        var a = f.Arrive("a.jpg", "a");
        var b = f.Arrive("b.jpg", "b");
        var c = f.Arrive("notes.txt", "c");
        f.Advance(Quiet);

        var batch = await f.PassAsync();

        Assert.Equal(3, batch.Outcomes.Count);
        Assert.All(batch.Outcomes, o => Assert.Equal(DownloadOutcomeKind.Moved, o.Kind));
        Assert.Single(batch.Outcomes.Select(o => o.Item!.BatchId).Distinct()); // album bubbles, like one drop
        Assert.False(File.Exists(a) || File.Exists(b) || File.Exists(c));
    }

    // ------------------------------------------------------------ partial and temporary files

    [Fact]
    public async Task Partial_and_temporary_files_are_never_moved()
    {
        await using var f = await Fixture.CreateAsync();
        string[] names = ["movie.mp4.crdownload", "Unconfirmed 123456.crdownload", "setup.exe.part", "song.mp3.partial", "file.tmp", "~$letter.docx"];
        var paths = names.Select(name => f.Arrive(name, "partial")).ToList();
        f.Advance(TimeSpan.FromMinutes(10));

        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Empty(f.T.RecycleBin.Recycled);
    }

    [Fact]
    public async Task A_finished_download_moves_a_minute_after_it_completed()
    {
        await using var f = await Fixture.CreateAsync();
        var started = f.Now;
        var partial = f.Arrive("movie.mp4.crdownload", "half");
        f.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await f.PassAsync()).IsEmpty);

        // The browser writes the last bytes and renames it (the creation time stays the download's start).
        File.WriteAllText(partial, "the whole movie");
        File.SetCreationTimeUtc(partial, started.UtcDateTime);
        File.SetLastWriteTimeUtc(partial, f.Now.UtcDateTime);
        var movie = Path.Combine(f.Downloads, "movie.mp4");
        File.Move(partial, movie);
        Assert.True((await f.PassAsync()).IsEmpty);

        f.Advance(Quiet);
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal("the whole movie", File.ReadAllText(outcome.Item!.FullPath));
    }

    [Fact]
    public async Task Firefox_placeholder_waits_while_its_part_file_exists()
    {
        await using var f = await Fixture.CreateAsync();
        var placeholder = f.Arrive("manual.pdf", "");
        var part = f.Arrive("manual.pdf.part", "downloading");
        f.Advance(TimeSpan.FromMinutes(3));
        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(placeholder));

        File.Delete(part);
        File.WriteAllText(placeholder, "the manual");
        File.SetLastWriteTimeUtc(placeholder, f.Now.UtcDateTime);
        Assert.True((await f.PassAsync()).IsEmpty); // complete now: its quiet minute starts
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal("the manual", File.ReadAllText(outcome.Item!.FullPath));
    }

    // ------------------------------------------------------------ files open in another program

    [Fact]
    public async Task A_file_open_in_another_program_is_retried_later()
    {
        await using var f = await Fixture.CreateAsync();
        var sheet = f.Arrive("budget.xlsx", "cells");
        f.Advance(Quiet);

        using (new FileStream(sheet, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.True((await f.PassAsync()).IsEmpty);
            Assert.Equal(f.Now + InUseRetry, f.Cleaner.NextDue);
        }

        Assert.True(File.Exists(sheet));
        f.Advance(InUseRetry - TimeSpan.FromSeconds(1));
        Assert.True((await f.PassAsync()).IsEmpty); // not before its retry

        f.Advance(TimeSpan.FromSeconds(1));
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.False(File.Exists(sheet));
    }

    [Fact]
    public async Task A_file_that_changes_while_it_is_added_stays_and_moves_once_quiet_again()
    {
        await using var f = await Fixture.CreateAsync();
        var notes = f.Arrive("notes.txt", "AAAA");
        f.Advance(Quiet);

        // Same length, other content, written while the library copies it (after it read it).
        f.T.Service.DuringAddCopy = _ =>
        {
            f.T.Service.DuringAddCopy = null;
            using var stream = new FileStream(notes, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write("BBBB"u8);
        };
        Assert.True((await f.PassAsync()).IsEmpty); // the vault's copy doesn't match the file any more: not recycled
        Assert.True(File.Exists(notes));
        Assert.False(f.T.RecycleBin.WasRecycled(notes)); // the original stays (only the stale vault copy is recycled)

        f.Advance(Quiet);
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal("BBBB", File.ReadAllText(outcome.Item!.FullPath));
        Assert.True(f.T.RecycleBin.WasRecycled(notes));
    }

    // ------------------------------------------------------------ the watermark

    [Fact]
    public async Task Files_that_were_there_before_the_mode_was_on_are_never_touched()
    {
        await using var f = await Fixture.CreateAsync();
        var before = f.Cleaner.Watermark - TimeSpan.FromDays(3);
        var old = f.Arrive("old.pdf", "old", before);
        var edited = f.Arrive("edited.docx", "edited", before);
        File.SetLastWriteTimeUtc(edited, f.Now.UtcDateTime); // saved again after the mode was switched on
        var renamed = f.Arrive("before rename.zip", "zip", before);
        File.Move(renamed, Path.Combine(f.Downloads, "after rename.zip"));
        var fresh = f.Arrive("new.pdf", "new");
        f.Cleaner.NoteChanged(edited);
        f.Advance(TimeSpan.FromHours(1));

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(fresh, outcome.SourcePath);
        Assert.True(File.Exists(old));
        Assert.True(File.Exists(edited));
        Assert.True(File.Exists(Path.Combine(f.Downloads, "after rename.zip")));
    }

    [Fact]
    public async Task At_start_files_that_arrived_while_Cabinette_was_closed_move_right_away()
    {
        await using var f = await Fixture.CreateAsync();
        var watermark = f.Now; // remembered when Cabinette quit
        var before = f.Arrive("before.pdf", "before", watermark - TimeSpan.FromMinutes(1));
        var whileClosed = f.Arrive("while closed.pdf", "while closed", watermark + TimeSpan.FromMinutes(10));
        f.Advance(TimeSpan.FromHours(5)); // next start

        await using var restarted = f.NewCleaner(watermark);
        var outcome = Assert.Single((await restarted.RunPassAsync()).Outcomes);

        Assert.Equal(whileClosed, outcome.SourcePath);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.True(File.Exists(before));
    }

    [Fact]
    public async Task The_watermark_never_passes_a_file_still_to_be_handled()
    {
        await using var f = await Fixture.CreateAsync();
        var start = f.Cleaner.Watermark;
        var busy = f.Arrive("busy.pdf", "busy");
        f.Advance(TimeSpan.FromSeconds(1));
        var large = f.Arrive("large.iso", "too large for the Recycle Bin");
        f.T.RecycleBin.TooLarge.Add(large);
        f.Advance(Quiet);

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var kept = Assert.Single((await f.PassAsync()).Outcomes);
            Assert.Equal(DownloadOutcomeKind.Kept, kept.Kind);
        }

        Assert.Equal(start, f.Cleaner.Watermark); // busy.pdf is older and not handled yet
        Assert.Empty(f.Watermarks);

        f.Advance(InUseRetry);
        Assert.Equal(DownloadOutcomeKind.Moved, Assert.Single((await f.PassAsync()).Outcomes).Kind);

        var expected = new DateTimeOffset(File.GetCreationTimeUtc(large));
        Assert.Equal(expected, f.Cleaner.Watermark);
        Assert.Equal([expected], f.Watermarks);
    }

    [Fact]
    public async Task A_download_in_progress_holds_the_watermark_back()
    {
        await using var f = await Fixture.CreateAsync();
        var start = f.Cleaner.Watermark;
        var partial = f.Arrive("big.iso.crdownload", "half");
        f.Advance(TimeSpan.FromSeconds(1));
        var large = f.Arrive("large.mkv", "too large");
        f.T.RecycleBin.TooLarge.Add(large);
        f.Advance(Quiet);

        Assert.Equal(DownloadOutcomeKind.Kept, Assert.Single((await f.PassAsync()).Outcomes).Kind);
        Assert.Equal(start, f.Cleaner.Watermark);

        // Cabinette quits and starts again; meanwhile the download finished.
        File.SetLastWriteTimeUtc(partial, f.Now.UtcDateTime);
        File.Move(partial, Path.Combine(f.Downloads, "big.iso"));
        f.Advance(TimeSpan.FromHours(2));
        await using var restarted = f.NewCleaner(f.Cleaner.Watermark);
        var outcomes = (await restarted.RunPassAsync()).Outcomes;

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(DownloadOutcomeKind.Moved, outcomes.Single(o => o.SourcePath.EndsWith("big.iso", StringComparison.Ordinal)).Kind);
        Assert.Equal(DownloadOutcomeKind.Kept, outcomes.Single(o => o.SourcePath == large).Kind); // said again after a restart: it was held back too
    }

    // ------------------------------------------------------------ duplicates

    [Fact]
    public async Task A_duplicate_still_sends_the_original_to_the_Recycle_Bin()
    {
        await using var f = await Fixture.CreateAsync();
        var existing = await f.T.AddOneAsync(null, f.T.CreateFile("invoice.pdf", "same content"));
        var again = f.Arrive("invoice (1).pdf", "same content");
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.AlreadyInLibrary, outcome.Kind);
        Assert.Equal(existing.Id, outcome.Item!.Id);
        Assert.True(f.T.RecycleBin.WasRecycled(again));
        Assert.Single(await f.T.AllItemsAsync()); // no second copy
    }

    [Fact]
    public async Task Content_linked_only_as_a_pointer_keeps_the_original()
    {
        await using var f = await Fixture.CreateAsync();
        var song = f.Arrive("song.mp3", "music");
        await f.T.Library.AddPathsAsync([song], new AddRequest { Mode = ItemMode.Pointer });
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Kept, outcome.Kind);
        Assert.Equal(DownloadKeptReason.LinkedAsPointer, outcome.Reason);
        Assert.True(File.Exists(song));
        Assert.Empty(f.T.RecycleBin.Recycled);
        f.Advance(TimeSpan.FromHours(1));
        Assert.True((await f.PassAsync()).IsEmpty); // not tried again
    }

    // ------------------------------------------------------------ the Recycle Bin can't take it

    [Fact]
    public async Task Without_a_Recycle_Bin_the_original_stays_and_is_never_deleted()
    {
        await using var f = await Fixture.CreateAsync();
        f.T.RecycleBin.FoldersWithoutRecycleBin.Add(f.Downloads);
        var archive = f.Arrive("photos.zip", "zip bytes");
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Kept, outcome.Kind);
        Assert.Equal(DownloadKeptReason.NoRecycleBin, outcome.Reason);
        Assert.True(File.Exists(archive)); // never deleted permanently
        Assert.Equal("zip bytes", File.ReadAllText(outcome.Item!.FullPath)); // but it is in the vault
        Assert.Equal(new DateTimeOffset(File.GetCreationTimeUtc(archive)), f.Cleaner.Watermark);

        // Not tried again, also after a restart.
        f.Advance(TimeSpan.FromHours(1));
        Assert.True((await f.PassAsync()).IsEmpty);
        await using var restarted = f.NewCleaner(f.Cleaner.Watermark);
        Assert.True((await restarted.RunPassAsync()).IsEmpty);
        Assert.True(File.Exists(archive));
        Assert.Single(await f.T.AllItemsAsync());
    }

    [Fact]
    public async Task A_late_Recycle_Bin_refusal_keeps_the_original_too()
    {
        await using var f = await Fixture.CreateAsync();
        var video = f.Arrive("talk.mp4", "video");
        f.T.RecycleBin.Refused.Add(video); // CanRecycle says yes, then the Recycle Bin refuses it
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Kept, outcome.Kind);
        Assert.Equal(DownloadKeptReason.NoRecycleBin, outcome.Reason);
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task A_failed_add_is_reported_once_and_tried_again_later()
    {
        await using var f = await Fixture.CreateAsync();
        var images = Path.Combine(f.T.Vault, "Images");
        Directory.Delete(images);
        File.WriteAllText(images, "a file where the Images folder should be");
        var photo = f.Arrive("photo.png", "png");
        f.Advance(Quiet);

        var failed = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Failed, failed.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
        Assert.True(File.Exists(photo));
        Assert.Equal(f.Now + FailureRetry, f.Cleaner.NextDue);

        f.Advance(FailureRetry);
        Assert.True((await f.PassAsync()).IsEmpty); // fails again, quietly
        Assert.True(File.Exists(photo));

        File.Delete(images);
        Directory.CreateDirectory(images);
        f.Advance(FailureRetry);
        Assert.Equal(DownloadOutcomeKind.Moved, Assert.Single((await f.PassAsync()).Outcomes).Kind);
        Assert.False(File.Exists(photo));
    }

    // ------------------------------------------------------------ what is left alone

    [Fact]
    public async Task Subfolders_and_new_folders_are_left_alone()
    {
        await using var f = await Fixture.CreateAsync();
        var inner = f.Arrive(@"Project\inner.pdf", "inner");
        Directory.CreateDirectory(Path.Combine(f.Downloads, "New folder"));
        var hidden = f.Arrive("hidden.pdf", "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        f.Advance(TimeSpan.FromHours(1));

        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(inner));
        Assert.True(File.Exists(hidden));
        Assert.True(Directory.Exists(Path.Combine(f.Downloads, "New folder")));
    }

    [Fact]
    public async Task A_folder_inside_the_vault_is_never_cleaned()
    {
        await using var f = await Fixture.CreateAsync();
        await using var cleaner = f.NewCleaner(f.Now - TimeSpan.FromHours(1), folder: f.T.Vault);
        var loose = Path.Combine(f.T.Vault, "loose.txt");
        File.WriteAllText(loose, "the vault monitor's business");
        Fixture.Stamp(loose, f.Now);
        f.Advance(TimeSpan.FromHours(1));

        Assert.True((await cleaner.RunPassAsync()).IsEmpty);
        Assert.True(File.Exists(loose));
    }

    // ------------------------------------------------------------ pause and resume

    [Fact]
    public async Task While_paused_nothing_moves_and_resuming_catches_up()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Cleaner.PauseAsync();
        var song = f.Arrive("song.flac", "flac");
        f.Advance(TimeSpan.FromMinutes(10));

        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(song));

        await f.Cleaner.ResumeAsync();
        Assert.Equal(DownloadOutcomeKind.Moved, Assert.Single((await f.PassAsync()).Outcomes).Kind);
    }

    [Fact]
    public async Task The_watcher_moves_new_downloads_and_pausing_holds_them_until_resumed()
    {
        await using var f = await Fixture.CreateAsync();
        await using var cleaner = new DownloadsCleaner(f.T.Library, f.T.RecycleBin, new DownloadsCleanerOptions
        {
            Folder = f.Downloads,
            Watermark = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            QuietPeriod = TimeSpan.FromMilliseconds(300),
            EventDelay = TimeSpan.FromMilliseconds(50),
            InUseRetryDelay = TimeSpan.FromMilliseconds(100),
            RescanInterval = TimeSpan.FromSeconds(1),
        });
        cleaner.Start();

        var song = Path.Combine(f.Downloads, "song.mp3");
        File.WriteAllText(song, "music");
        await VaultTestHelpers.WaitUntilAsync(() => !File.Exists(song), "the download moved");
        Assert.True(f.T.RecycleBin.WasRecycled(song));

        await cleaner.PauseAsync();
        var clip = Path.Combine(f.Downloads, "clip.mp4");
        File.WriteAllText(clip, "video");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.True(File.Exists(clip));

        await cleaner.ResumeAsync();
        await VaultTestHelpers.WaitUntilAsync(() => !File.Exists(clip), "the paused download moved after resuming");
        Assert.Equal(2, (await f.T.AllItemsAsync()).Count);
    }

    // ------------------------------------------------------------ the setting

    [Fact]
    public async Task The_setting_is_off_by_default_and_stored_like_the_others()
    {
        await using var t = await TestLibrary.CreateAsync();
        Assert.False((await t.Library.GetSettingsAsync()).CleanMyDownloads);

        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { CleanMyDownloads = true });
        await t.ReopenAsync();
        Assert.True((await t.Library.GetSettingsAsync()).CleanMyDownloads);

        // Settings saved before the option existed don't have it: it is off.
        await t.Library.DisposeAsync();
        using (var connection = new Cabinette.Core.Data.Database(t.DataDirectory).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE settings SET value = json_remove(value, '$.CleanMyDownloads') WHERE key = 'app_settings'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        await t.OpenAsync();
        Assert.False((await t.Library.GetSettingsAsync()).CleanMyDownloads);
    }

    // ------------------------------------------------------------ fixture

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<DownloadsCleaner> _cleaners = [];

        private Fixture(TestLibrary t)
        {
            T = t;
            Downloads = Path.Combine(t.Root, "downloads");
            Directory.CreateDirectory(Downloads);
        }

        public TestLibrary T { get; }

        public string Downloads { get; }

        public DownloadsCleaner Cleaner { get; private set; } = null!;

        public DateTimeOffset Now => T.Time.GetUtcNow();

        public List<DownloadsBatch> Batches { get; } = [];

        public List<DateTimeOffset> Watermarks { get; } = [];

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await TestLibrary.CreateAsync());
            fixture.Cleaner = fixture.NewCleaner(fixture.Now);
            fixture.Cleaner.Processed += (_, batch) => fixture.Batches.Add(batch);
            fixture.Cleaner.WatermarkChanged += (_, watermark) => fixture.Watermarks.Add(watermark);

            // Files that arrive from now on are newer than the watermark.
            fixture.Advance(TimeSpan.FromSeconds(1));
            return fixture;
        }

        public DownloadsCleaner NewCleaner(DateTimeOffset watermark, string? folder = null)
        {
            var cleaner = new DownloadsCleaner(T.Library, T.RecycleBin, new DownloadsCleanerOptions
            {
                Folder = folder ?? Downloads,
                Watermark = watermark,
                QuietPeriod = Quiet,
                InUseRetryDelay = InUseRetry,
                FailureRetryDelay = FailureRetry,
            }, T.Time);
            _cleaners.Add(cleaner);
            return cleaner;
        }

        /// <summary>A file that appears in the Downloads folder now (or at <paramref name="at"/>), by the fake clock.</summary>
        public string Arrive(string relativePath, string content, DateTimeOffset? at = null)
        {
            var path = Path.Combine(Downloads, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            Stamp(path, at ?? Now);
            return path;
        }

        public static void Stamp(string path, DateTimeOffset at)
        {
            File.SetCreationTimeUtc(path, at.UtcDateTime);
            File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        }

        public void Advance(TimeSpan by) => T.Time.Advance(by);

        public Task<DownloadsBatch> PassAsync() => Cleaner.RunPassAsync();

        public async ValueTask DisposeAsync()
        {
            foreach (var cleaner in _cleaners)
            {
                await cleaner.DisposeAsync();
            }

            await T.DisposeAsync();
        }
    }
}
