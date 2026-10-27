using System.Diagnostics;
using Cabinette.Core.Downloads;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Tests;

/// <summary>
/// Bug hunt of the Clean My Downloads branch (reviewer: Fable), turned into regression tests: each test first asserted the
/// behavior the review found, and now asserts the fix. Temp folders, the fake clock and the fake Recycle Bin only: the real
/// Downloads folder and Recycle Bin are never touched.
/// </summary>
public sealed class BugHuntDownloadsTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InUseRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(5);
    private const string Zone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/setup.exe\r\n";

    // ------------------------------------------------------------ F1: Mark of the Web

    /// <summary>
    /// Browsers tag every download with a Zone.Identifier stream (Mark of the Web). SmartScreen, Office Protected View and
    /// the "blocked macros from the internet" rule all key off it. The vault copy now carries it, so recycling the marked
    /// original doesn't strip the file of it.
    /// </summary>
    [Fact]
    public async Task The_Mark_of_the_Web_goes_along_into_the_vault_copy_before_the_original_is_recycled()
    {
        await using var f = await Fixture.CreateAsync();
        var setup = f.Arrive("setup.exe", "an installer from the internet");
        File.WriteAllText(setup + ":Zone.Identifier", Zone);
        File.SetLastWriteTimeUtc(setup, f.Now.UtcDateTime); // the browser writes the mark as the download completes
        Assert.Equal(Zone, File.ReadAllText(setup + ":Zone.Identifier")); // the original carries the mark
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        var copy = outcome.Item!.FullPath;
        Assert.Equal("an installer from the internet", File.ReadAllText(copy));
        Assert.Equal(Zone, File.ReadAllText(copy + ":Zone.Identifier")); // the vault copy is marked too
        Assert.True(f.T.RecycleBin.WasRecycled(setup));
    }

    // ------------------------------------------------------------ F2: the check trusts the stored hash

    /// <summary>
    /// A download whose content is "already in the library" was recycled after a check against the item's stored size and
    /// hash only. When the vault file was changed in place without the library noticing (same size, same modification time,
    /// which even a full rescan doesn't see through), the download went to the Recycle Bin although no file in the vault has
    /// its content. The vault file is read back now, and a mismatch keeps the original.
    /// </summary>
    [Fact]
    public async Task A_duplicate_whose_vault_copy_no_longer_has_that_content_keeps_the_original()
    {
        await using var f = await Fixture.CreateAsync();
        var existing = await f.T.AddOneAsync(null, f.T.CreateFile("song.mp3", "same content, v1"));
        var vaultCopy = existing.FullPath;

        // Edited in place with the same length and the modification time put back (a tag editor's "preserve file
        // modification time"): size and time still match the row, so neither a rescan nor the monitor rehashes it.
        var stamp = File.GetLastWriteTimeUtc(vaultCopy);
        File.WriteAllText(vaultCopy, "same content, v2");
        File.SetLastWriteTimeUtc(vaultCopy, stamp);
        await f.T.RescanAsync();
        Assert.Equal(existing.Hash, (await f.T.ItemAsync(existing.Id)).Hash); // the stored hash is stale

        var download = f.Arrive("song (1).mp3", "same content, v1");
        f.Advance(Quiet);
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("couldn't be checked", outcome.Message);
        Assert.Empty(f.T.RecycleBin.Recycled); // not recycled
        Assert.Equal("same content, v1", File.ReadAllText(download)); // the only file with this content stays
        Assert.Equal("same content, v2", File.ReadAllText(vaultCopy)); // and the vault file is left as it is
    }

    // ------------------------------------------------------------ F3: not only downloads are "new" (decided: keep)

    /// <summary>
    /// "Arrived after the mode was switched on" is judged by creation time only, so a file copied into Downloads from another
    /// drive (Explorer, a USB stick) or extracted there is moved like a download. Decided by the user (2026-10-22): keep it.
    /// The quiet minute counts from the creation time.
    /// </summary>
    [Fact]
    public async Task A_file_copied_in_from_another_drive_is_moved_and_recycled_like_a_download()
    {
        await using var f = await Fixture.CreateAsync();
        var copied = f.Arrive("holiday photos.zip", "from the usb stick");
        File.SetLastWriteTimeUtc(copied, (f.Now - TimeSpan.FromDays(400)).UtcDateTime); // Explorer keeps the modification time

        Assert.True((await f.PassAsync()).IsEmpty); // the quiet minute runs from the creation time
        f.Advance(Quiet);
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.True(f.T.RecycleBin.WasRecycled(copied));
    }

    // ------------------------------------------------------------ F4: in-place downloads and download-manager suffixes

    /// <summary>
    /// aria2 (and tools built on it) writes the download in place under its final name, next to a "name.aria2" control file.
    /// When it stalled or was paused for a minute, both were moved half-done. The file waits while its control file exists, and
    /// the control file itself is never moved; once the download is done (the control file goes), the file moves.
    /// </summary>
    [Fact]
    public async Task An_aria2_download_waits_for_its_control_file_and_moves_once_it_is_done()
    {
        await using var f = await Fixture.CreateAsync();
        var movie = f.Arrive("movie.mkv", "first half of the movie");
        var control = f.Arrive("movie.mkv.aria2", "control file of the download manager");
        f.Advance(Quiet); // paused, or no peers, for a minute

        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(movie)); // the downloader resumes: its file is still there
        Assert.True(File.Exists(control));
        Assert.Empty(await f.T.AllItemsAsync());

        File.WriteAllText(movie, "the whole movie");
        File.SetLastWriteTimeUtc(movie, f.Now.UtcDateTime);
        File.Delete(control);
        Assert.True((await f.PassAsync()).IsEmpty); // complete now: its quiet minute starts
        f.Advance(Quiet);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal("the whole movie", File.ReadAllText(outcome.Item!.FullPath));
    }

    /// <summary>The incomplete-file suffixes of common download managers and torrent clients are never moved.</summary>
    [Theory]
    [InlineData("movie.mkv.!qB")] // qBittorrent
    [InlineData("movie.mkv.!ut")] // µTorrent
    [InlineData("movie.mkv.!bt")] // BitTorrent
    [InlineData("movie.mkv.bc!")] // BitComet
    [InlineData("movie.mkv.az!")] // Vuze
    [InlineData("setup.exe.fdmdownload")] // Free Download Manager
    [InlineData("setup.exe.jc!")] // FlashGet
    [InlineData("setup.exe.td")] // Thunder
    public async Task Incomplete_files_of_download_managers_and_torrent_clients_are_never_moved(string name)
    {
        await using var f = await Fixture.CreateAsync();
        var partial = f.Arrive(name, "half");
        f.Advance(TimeSpan.FromMinutes(10));

        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.True(File.Exists(partial));
        Assert.Empty(f.T.RecycleBin.Recycled);
    }

    // ------------------------------------------------------------ F5: a change during the add left a stale item

    /// <summary>
    /// When the original changed while it was being copied, the copy stayed in the vault as an item (with the old content and
    /// this download's original path) and the file was added again once quiet: two items for one download. The stale item,
    /// which nobody was told about, is removed now, and its vault copy goes to the Recycle Bin (never deleted permanently:
    /// if something had saved a different file over the original, that copy would be the only one left).
    /// </summary>
    [Fact]
    public async Task A_file_changed_during_its_add_leaves_no_stale_item_behind()
    {
        await using var f = await Fixture.CreateAsync();
        var notes = f.Arrive("notes.txt", "AAAA");
        f.Advance(Quiet);
        f.T.Service.DuringAddCopy = _ =>
        {
            f.T.Service.DuringAddCopy = null;
            using var stream = new FileStream(notes, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write("BBBB"u8);
        };
        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.Empty(await f.T.AllItemsAsync()); // the stale copy went at once...
        var staleCopy = Assert.Single(f.T.RecycleBin.Recycled); // ...to the Recycle Bin
        Assert.StartsWith(f.T.Vault, staleCopy, StringComparison.OrdinalIgnoreCase);
        f.Advance(Quiet);
        Assert.Equal(DownloadOutcomeKind.Moved, Assert.Single((await f.PassAsync()).Outcomes).Kind);

        var item = Assert.Single(await f.T.AllItemsAsync()); // one download, one item
        Assert.Equal("BBBB", File.ReadAllText(item.FullPath));
        Assert.Equal(notes, item.OriginalPath);
        Assert.DoesNotContain(Directory.EnumerateFiles(f.T.Vault, "*", SearchOption.AllDirectories), path => File.ReadAllText(path) == "AAAA");
        Assert.Equal([staleCopy, notes], f.T.RecycleBin.Recycled); // the stale copy, then the original
    }

    /// <summary>
    /// The stale item goes only while it is exactly as the add stored it, and its vault file goes to the Recycle Bin, never
    /// deleted permanently. An item someone already tagged, starred, filed elsewhere or edited stays.
    /// </summary>
    [Fact]
    public async Task A_stale_item_goes_only_while_untouched_and_only_to_the_Recycle_Bin()
    {
        await using var t = await TestLibrary.CreateAsync();
        var untouched = await t.AddOneAsync(null, t.CreateFile("a.txt", "AAAA"), addedBy: AddedBy.Downloads);
        var tagged = await t.AddOneAsync(null, t.CreateFile("b.txt", "BBBB"), addedBy: AddedBy.Downloads);
        await t.Library.SetItemTagsAsync(tagged.Id, ["keep"]);
        var starred = await t.AddOneAsync(null, t.CreateFile("c.txt", "CCCC"), addedBy: AddedBy.Downloads);
        await t.Library.SetStarredAsync([starred.Id], true);
        var edited = await t.AddOneAsync(null, t.CreateFile("d.txt", "DDDD"), addedBy: AddedBy.Downloads);
        File.WriteAllText(edited.FullPath, "DDDD, edited");
        var editedInPlace = await t.AddOneAsync(null, t.CreateFile("e.txt", "EEEE"), addedBy: AddedBy.Downloads);
        var stamp = File.GetLastWriteTimeUtc(editedInPlace.FullPath);
        File.WriteAllText(editedInPlace.FullPath, "eeee"); // same size, date put back
        File.SetLastWriteTimeUtc(editedInPlace.FullPath, stamp);

        Assert.True(await t.Service.DiscardStaleAddAsync(untouched));
        Assert.False(await t.Service.DiscardStaleAddAsync(tagged));
        Assert.False(await t.Service.DiscardStaleAddAsync(starred));
        Assert.False(await t.Service.DiscardStaleAddAsync(edited));
        Assert.False(await t.Service.DiscardStaleAddAsync(editedInPlace));

        Assert.Null(await t.Library.GetItemAsync(untouched.Id));
        Assert.False(File.Exists(untouched.FullPath));
        Assert.Equal(4, (await t.AllItemsAsync()).Count);
        Assert.All([tagged, starred, edited, editedInPlace], item => Assert.True(File.Exists(item.FullPath)));
        Assert.Equal([untouched.FullPath], t.RecycleBin.Recycled);
    }

    /// <summary>Where the Recycle Bin can't take the stale copy, the item stays (nothing is ever deleted permanently).</summary>
    [Fact]
    public async Task A_stale_item_stays_where_its_copy_cannot_go_to_the_Recycle_Bin()
    {
        await using var t = await TestLibrary.CreateAsync();
        var item = await t.AddOneAsync(null, t.CreateFile("a.txt", "AAAA"), addedBy: AddedBy.Downloads);
        t.RecycleBin.TooLarge.Add(item.FullPath);

        Assert.False(await t.Service.DiscardStaleAddAsync(item));
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
        Assert.True(File.Exists(item.FullPath));
        Assert.Empty(t.RecycleBin.Recycled);
    }

    /// <summary>
    /// An item stored by an earlier pass (which stopped because the file was in use) is compared with what that add stored,
    /// not with what the library has now: filed in a second category meanwhile, it is the user's and stays.
    /// </summary>
    [Fact]
    public async Task A_stored_item_used_while_its_file_was_in_use_stays_when_the_file_then_changes()
    {
        await using var f = await Fixture.CreateAsync();
        var notes = f.Arrive("notes.txt", "first version");
        f.Advance(Quiet);
        FileStream? viewer = null;
        f.T.Service.DuringAddCopy = _ => viewer ??= new FileStream(notes, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.True((await f.PassAsync()).IsEmpty); // in use
        viewer!.Dispose();
        var stored = Assert.Single(await f.T.AllItemsAsync());
        var work = await f.T.Library.CreateCategoryAsync("Work", null);
        await f.T.Library.AddToCategoryAsync([stored.Id], work.Id);

        File.WriteAllText(notes, "second version, longer");
        File.SetLastWriteTimeUtc(notes, f.Now.UtcDateTime);
        Assert.True((await f.PassAsync()).IsEmpty); // seen changing: its quiet minute starts again
        f.Advance(Quiet);
        Assert.True((await f.PassAsync()).IsEmpty); // the stored item no longer matches the file: it waits again

        Assert.NotNull(await f.T.Library.GetItemAsync(stored.Id));
        Assert.Equal("first version", File.ReadAllText(stored.FullPath));
        f.Advance(Quiet);
        Assert.Equal(DownloadOutcomeKind.Moved, Assert.Single((await f.PassAsync()).Outcomes).Kind);
        Assert.Equal(2, (await f.T.AllItemsAsync()).Count);
    }

    // ------------------------------------------------------------ F6: a stray partial file pinned the watermark

    /// <summary>
    /// A partial download that is never finished (an abandoned "Unconfirmed 123.crdownload" after a browser crash) held the
    /// watermark forever, so every file kept after it (no Recycle Bin, pointer) was "dealt with" again at each start, with
    /// its notice. A partial nobody wrote to or held open for a day no longer holds the watermark.
    /// </summary>
    [Fact]
    public async Task An_abandoned_partial_download_stops_holding_the_watermark_after_a_day()
    {
        await using var f = await Fixture.CreateAsync();
        var start = f.Cleaner.Watermark;
        var partial = f.Arrive("Unconfirmed 481516.crdownload", "the browser crashed here");
        f.Advance(TimeSpan.FromMinutes(1));
        var large = f.Arrive("large.iso", "too large for the Recycle Bin");
        f.T.RecycleBin.TooLarge.Add(large);
        f.Advance(Quiet);
        Assert.Equal(DownloadOutcomeKind.Kept, Assert.Single((await f.PassAsync()).Outcomes).Kind);
        Assert.Equal(start, f.Cleaner.Watermark); // a download in progress holds it back

        f.Advance(TimeSpan.FromDays(1));
        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.Equal(new DateTimeOffset(File.GetCreationTimeUtc(large)), f.Cleaner.Watermark); // abandoned: no longer
        Assert.True(File.Exists(partial)); // and never moved

        for (var restart = 0; restart < 3; restart++)
        {
            f.Advance(TimeSpan.FromDays(1));
            await using var restarted = f.NewCleaner(f.Cleaner.Watermark);
            Assert.True((await restarted.RunPassAsync()).IsEmpty); // not said again
        }
    }

    /// <summary>When Cabinette restarts after the partial was abandoned, the kept file is said once more, and never again.</summary>
    [Fact]
    public async Task After_a_restart_a_file_kept_behind_an_abandoned_partial_is_reported_once_more_at_most()
    {
        await using var f = await Fixture.CreateAsync();
        f.Arrive("Unconfirmed 481516.crdownload", "the browser crashed here");
        f.Advance(TimeSpan.FromMinutes(1));
        var large = f.Arrive("large.iso", "too large for the Recycle Bin");
        f.T.RecycleBin.TooLarge.Add(large);
        f.Advance(Quiet);
        Assert.Equal(DownloadOutcomeKind.Kept, Assert.Single((await f.PassAsync()).Outcomes).Kind);

        var watermark = f.Cleaner.Watermark;
        f.Advance(TimeSpan.FromDays(1));
        await using (var first = f.NewCleaner(watermark))
        {
            Assert.Equal(DownloadOutcomeKind.Kept, Assert.Single((await first.RunPassAsync()).Outcomes).Kind);
            watermark = first.Watermark;
        }

        for (var restart = 0; restart < 3; restart++)
        {
            f.Advance(TimeSpan.FromDays(1));
            await using var restarted = f.NewCleaner(watermark);
            Assert.True((await restarted.RunPassAsync()).IsEmpty);
        }

        Assert.Single(await f.T.AllItemsAsync());
    }

    /// <summary>A partial download that is old but still open in the downloader (a slow or paused download) keeps holding it.</summary>
    [Fact]
    public async Task A_partial_download_held_open_keeps_holding_the_watermark_however_old()
    {
        await using var f = await Fixture.CreateAsync();
        var start = f.Cleaner.Watermark;
        var partial = f.Arrive("huge.iso.!qB", "still downloading");
        f.Advance(TimeSpan.FromMinutes(1));
        var large = f.Arrive("large.iso", "too large for the Recycle Bin");
        f.T.RecycleBin.TooLarge.Add(large);
        f.Advance(TimeSpan.FromDays(3));

        using (new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.Equal(DownloadOutcomeKind.Kept, Assert.Single((await f.PassAsync()).Outcomes).Kind);
            Assert.Equal(start, f.Cleaner.Watermark);
        }
    }

    // ------------------------------------------------------------ F7: a transient Recycle Bin failure was final

    /// <summary>
    /// The Recycle Bin can fail for a moment (the shell busy, a virus scanner holding the file between the lock check and
    /// the move). Such a failure kept the file for good. Now it is said once, the file keeps holding the watermark back and is
    /// tried again later, from the item already stored (no second copy). Only a real refusal keeps a file for good.
    /// </summary>
    [Fact]
    public async Task A_one_off_Recycle_Bin_failure_is_tried_again_later_without_a_second_copy()
    {
        await using var f = await Fixture.CreateAsync();
        var start = f.Cleaner.Watermark;
        var copies = 0;
        f.T.Service.DuringAddCopy = _ => copies++;
        var video = f.Arrive("talk.mp4", "video");
        f.Advance(Quiet);
        f.T.RecycleBin.FailNext = true; // one simulated shell failure

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(DownloadOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("Simulated shell failure", outcome.Message);
        Assert.NotNull(outcome.Item); // it is in the vault
        Assert.True(File.Exists(video));
        Assert.Equal(start, f.Cleaner.Watermark); // not passed over
        Assert.Equal(f.Now + FailureRetry, f.Cleaner.NextDue);

        f.Advance(FailureRetry);
        var moved = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, moved.Kind);
        Assert.Equal(outcome.Item!.Id, moved.Item!.Id);
        Assert.True(f.T.RecycleBin.WasRecycled(video));
        Assert.Equal(1, copies);
        Assert.Single(await f.T.AllItemsAsync());
    }

    /// <summary>A Recycle Bin failure that lasts is tried again with a doubling delay and said only once; a restart tries it too.</summary>
    [Fact]
    public async Task A_lasting_Recycle_Bin_failure_backs_off_and_a_restart_tries_again()
    {
        await using var f = await Fixture.CreateAsync();
        var video = f.Arrive("talk.mp4", "video");
        f.Advance(Quiet);
        f.T.RecycleBin.BeforeRecycle = _ => f.T.RecycleBin.FailNext = true;

        Assert.Equal(DownloadOutcomeKind.Failed, Assert.Single((await f.PassAsync()).Outcomes).Kind);
        Assert.Equal(f.Now + FailureRetry, f.Cleaner.NextDue);
        f.Advance(FailureRetry);
        Assert.True((await f.PassAsync()).IsEmpty); // quietly
        Assert.Equal(f.Now + 2 * FailureRetry, f.Cleaner.NextDue);
        f.Advance(2 * FailureRetry);
        Assert.True((await f.PassAsync()).IsEmpty);
        Assert.Equal(f.Now + 4 * FailureRetry, f.Cleaner.NextDue);

        f.T.RecycleBin.BeforeRecycle = null;
        f.Advance(TimeSpan.FromHours(1));
        await using var restarted = f.NewCleaner(f.Cleaner.Watermark);
        Assert.Equal(DownloadOutcomeKind.AlreadyInLibrary, Assert.Single((await restarted.RunPassAsync()).Outcomes).Kind);
        Assert.True(f.T.RecycleBin.WasRecycled(video));
    }

    // ------------------------------------------------------------ F8: the vault copy failing the check

    /// <summary>
    /// A duplicate whose row doesn't match its vault file (here: a wrong stored size) was failed, reported once and retried
    /// every 5 minutes forever, each retry copying the whole file into the staging folder again. Now the delay doubles, and
    /// after three attempts the original stays for good, with a notice.
    /// </summary>
    [Fact]
    public async Task A_vault_copy_that_fails_the_check_is_retried_with_a_doubling_delay_and_then_kept()
    {
        await using var f = await Fixture.CreateAsync();
        var existing = await f.T.AddOneAsync(null, f.T.CreateFile("invoice.pdf", "same content"));
        using (var connection = new Cabinette.Core.Data.Database(f.T.DataDirectory).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE items SET size_bytes = size_bytes + 1 WHERE id = {existing.Id}";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var copies = 0;
        f.T.Service.DuringAddCopy = _ => copies++;
        var again = f.Arrive("invoice (1).pdf", "same content");
        f.Advance(Quiet);

        var failed = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Failed, failed.Kind);
        Assert.Contains("couldn't be checked", failed.Message);
        Assert.Equal(f.Now + FailureRetry, f.Cleaner.NextDue);

        f.Advance(FailureRetry);
        Assert.True((await f.PassAsync()).IsEmpty); // quietly
        Assert.Equal(f.Now + 2 * FailureRetry, f.Cleaner.NextDue); // doubled

        f.Advance(2 * FailureRetry);
        var kept = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Kept, kept.Kind);
        Assert.Equal(DownloadKeptReason.VaultCopyUnchecked, kept.Reason);
        Assert.Equal(existing.Id, kept.Item!.Id);
        Assert.Equal(3, copies);

        f.Advance(TimeSpan.FromHours(5));
        Assert.True((await f.PassAsync()).IsEmpty);
        await using var restarted = f.NewCleaner(f.Cleaner.Watermark);
        Assert.True((await restarted.RunPassAsync()).IsEmpty);
        Assert.Equal(3, copies); // no more attempts
        Assert.True(File.Exists(again));
        Assert.Empty(f.T.RecycleBin.Recycled);
    }

    // ------------------------------------------------------------ F9: a lock that appears during the add

    /// <summary>
    /// When another program opened the file while the library was copying it, the pass backed off (right), but the next pass
    /// added the file again from scratch: a second full copy, dropped as a duplicate, and a notice saying the file "was already
    /// in Cabinette". Now the next pass finishes from the item already stored.
    /// </summary>
    [Fact]
    public async Task A_lock_that_appears_during_the_add_finishes_from_the_stored_item()
    {
        await using var f = await Fixture.CreateAsync();
        var video = f.Arrive("talk.mp4", "a long talk");
        f.Advance(Quiet);
        FileStream? viewer = null;
        var copies = 0;
        f.T.Service.DuringAddCopy = _ =>
        {
            copies++;
            viewer ??= new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); // opened while copying
        };

        Assert.True((await f.PassAsync()).IsEmpty); // in use: backs off
        var stored = Assert.Single(await f.T.AllItemsAsync()); // the vault copy is stored already
        viewer!.Dispose();
        f.Advance(InUseRetry);
        var outcome = Assert.Single((await f.PassAsync()).Outcomes);

        Assert.Equal(1, copies); // copied once
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind); // "added", as it was
        Assert.Equal(stored.Id, outcome.Item!.Id);
        Assert.True(f.T.RecycleBin.WasRecycled(video));
        Assert.Single(await f.T.AllItemsAsync());
    }

    /// <summary>A file deleted by someone else while it waited for its lock to go is reported as moved: the vault has it.</summary>
    [Fact]
    public async Task A_stored_file_deleted_while_it_waits_is_reported_as_moved()
    {
        await using var f = await Fixture.CreateAsync();
        var video = f.Arrive("talk.mp4", "a long talk");
        f.Advance(Quiet);
        FileStream? viewer = null;
        f.T.Service.DuringAddCopy = _ => viewer ??= new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.True((await f.PassAsync()).IsEmpty);
        viewer!.Dispose();
        File.Delete(video);
        f.Advance(InUseRetry);

        var outcome = Assert.Single((await f.PassAsync()).Outcomes);
        Assert.Equal(DownloadOutcomeKind.Moved, outcome.Kind);
        Assert.Equal("a long talk", File.ReadAllText(outcome.Item!.FullPath));
    }

    // ------------------------------------------------------------ the vault copy reaches the disk

    /// <summary>
    /// The vault copy was flushed only to the Windows cache before the original went to the Recycle Bin. Every copy into the
    /// vault (and its Mark of the Web) is now flushed to the disk before the add stores it. A test can't pull the plug: this
    /// pins that the flush to disk happens, on the staged copy, before the add returns.
    /// </summary>
    [Fact]
    public async Task Every_copy_into_the_vault_is_flushed_to_the_disk_before_the_add_stores_it()
    {
        await using var t = await TestLibrary.CreateAsync();
        var flushed = new List<string>();
        FileOps.FlushedToDisk.Value = path =>
        {
            lock (flushed)
            {
                flushed.Add(path);
            }
        };
        try
        {
            var source = t.CreateFile("setup.exe", "an installer");
            File.WriteAllText(source + ":Zone.Identifier", Zone);
            var item = await t.AddOneAsync(null, source);

            var staging = NameRules.StagingFolder(t.Vault);
            Assert.Equal(2, flushed.Count);
            Assert.StartsWith(staging, flushed[0], StringComparison.OrdinalIgnoreCase); // the copy, while it was still staged
            Assert.EndsWith(":Zone.Identifier", flushed[1], StringComparison.Ordinal); // and its mark
            Assert.Equal("an installer", File.ReadAllText(item.FullPath));
        }
        finally
        {
            FileOps.FlushedToDisk.Value = null;
        }
    }

    // ------------------------------------------------------------ the vault through a junction

    /// <summary>
    /// A Downloads folder that is the vault (or inside it) is never cleaned: the vault monitor owns those files. That was
    /// compared by spelling only; a junction to the vault got through. Both sides are resolved to their final paths now.
    /// The junction is made in this test's temp folder (mklink /J needs no privilege).
    /// </summary>
    [Fact]
    public async Task A_Downloads_folder_that_reaches_the_vault_through_a_junction_is_never_cleaned()
    {
        await using var f = await Fixture.CreateAsync();
        var inside = Directory.CreateDirectory(Path.Combine(f.T.Vault, "Inbox")).FullName;
        var link = Path.Combine(f.T.Root, "dl-link");
        MakeJunction(link, inside);
        try
        {
            await using var cleaner = f.NewCleaner(f.Now - TimeSpan.FromHours(1), folder: link);
            var loose = Path.Combine(link, "loose.txt");
            File.WriteAllText(loose, "the vault monitor's business");
            Fixture.Stamp(loose, f.Now);
            f.Advance(TimeSpan.FromHours(1));

            Assert.True((await cleaner.RunPassAsync()).IsEmpty);
            Assert.True(File.Exists(Path.Combine(inside, "loose.txt")));
            Assert.Empty(f.T.RecycleBin.Recycled);
        }
        finally
        {
            Directory.Delete(link); // removes the junction only
        }
    }

    internal static void MakeJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        mklink!.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction could not be created on this machine");
    }

    // ------------------------------------------------------------ fixture (a copy of the author's)

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

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await TestLibrary.CreateAsync());
            fixture.Cleaner = fixture.NewCleaner(fixture.Now);
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
