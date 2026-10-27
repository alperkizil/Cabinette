using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// Bug hunt of the date-taken worker (feat/date-taken, docs/review/bughunt-2026-10-24-datetaken). Each F-test fails on that
/// branch because of the finding it names and passes once it is fixed; the Lead tests record scenarios that were checked and
/// hold (kept so they keep holding).
/// </summary>
public sealed class DateTakenReviewTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly DateTimeOffset July = new(2019, 7, 14, 12, 30, 45, TimeSpan.Zero);

    /// <summary>
    /// F1 (library side): the camera's wall-clock time must survive the database. The reader gives the date with the zone's
    /// offset; <c>item_dates.taken_at</c> folds it into UTC, so <see cref="LibraryItem.TakenAt"/> comes back as an instant and
    /// a Gallery showing it as local time drifts when this PC's zone is no longer the one the file was read in.
    /// </summary>
    [Fact]
    public async Task F1_ADateTaken_KeepsItsWallClock_ThroughTheDatabase()
    {
        var cameraTime = new DateTimeOffset(2019, 7, 14, 15, 30, 45, TimeSpan.FromHours(3));
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");

        var added = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(cameraTime)));
        await IdleAsync(t);

        var taken = (await t.ItemAsync(added.Id)).TakenAt;
        Assert.NotNull(taken);
        Assert.Equal(cameraTime, taken.Value); // the same instant...
        Assert.Equal(cameraTime.DateTime, taken.Value.DateTime); // ...and still the wall clock the camera showed
        Assert.Equal(cameraTime.Offset, taken.Value.Offset);
    }

    // ------------------------------------------------------------ leads checked (these pass)

    [Fact]
    public async Task Lead_VaultMovedWhileAFileIsBeingRead_TheDateIsReadAgainInTheNewVault()
    {
        using var gate = new ManualResetEventSlim(false);
        var reader = new FakeDateTakenReader { Gate = gate };
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        Assert.True(await reader.Entered.WaitAsync(Patience));

        var newVault = Path.Combine(t.Root, "new vault");
        await t.Library.MoveVaultAsync(newVault).WaitAsync(Patience);
        gate.Set();
        await IdleAsync(t);

        var moved = await t.ItemAsync(photo.Id);
        Assert.StartsWith(newVault, moved.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(July, moved.TakenAt);
        Assert.Equal(1, reader.ReadsOf(moved.FullPath));
        Assert.Equal(1, DateRows(t));
    }

    [Fact]
    public async Task Lead_ItemDeletedForGoodWhileBeingRead_AndItsIdReused_TheNewFileGetsItsOwnDate()
    {
        using var gate = new ManualResetEventSlim(false);
        var reader = new FakeDateTakenReader { Gate = gate };
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        Assert.True(await reader.Entered.WaitAsync(Patience));

        // Deleted and emptied while the reader holds the old file: its row is gone, the id is free again.
        await t.Library.DeleteItemsAsync([photo.Id]).WaitAsync(Patience);
        await t.Library.EmptyDeletedAsync().WaitAsync(Patience);
        var next = await t.AddOneAsync(images.Id, t.CreateFile(@"other\photo.jpg", "a screenshot, no date"));
        Assert.Equal(photo.Id, next.Id);
        Assert.Equal(photo.FullPath, next.FullPath, StringComparer.OrdinalIgnoreCase);

        gate.Set();
        await IdleAsync(t);

        Assert.Null((await t.ItemAsync(next.Id)).TakenAt);
        Assert.Equal(1, DateRows(t));
    }

    [Fact]
    public async Task Lead_DisposeWhileTheWorkerWaitsForTheWriteLock_StopsAtOnce()
    {
        using var gate = new ManualResetEventSlim(false);
        using var holdMove = new ManualResetEventSlim(false);
        using var moveHolding = new ManualResetEventSlim(false);
        var reader = new FakeDateTakenReader { Gate = gate };
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        Assert.True(await reader.Entered.WaitAsync(Patience));

        // A vault move holds the write lock (it is inside the copy) while the reader finishes and wants to store.
        t.Service.AfterMoveVaultFileCopied = _ =>
        {
            moveHolding.Set();
            holdMove.Wait(TimeSpan.FromMinutes(1));
        };
        var move = t.Library.MoveVaultAsync(Path.Combine(t.Root, "new vault"));
        Assert.True(moveHolding.Wait(Patience));
        Assert.True(t.Service.IsWriteLockHeld);
        gate.Set();

        var disposing = t.Library.DisposeAsync().AsTask();
        await t.Service.WhenDatesTakenIdleAsync().WaitAsync(Patience); // the worker gave up without the lock
        holdMove.Set();
        await move.WaitAsync(Patience);
        await disposing.WaitAsync(Patience);
    }

    [Fact]
    public async Task Lead_VaultOnAMissingDrive_OpensAndReadsNothing_UntilItIsBack()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await t.Library.DisposeAsync();

        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "QRSTUVWXYZ".First(c => !taken.Contains(c));
        var missingVault = $@"{letter}:\cabinette-review-missing";
        var realVault = t.Vault;
        SetVaultPath(t, missingVault);

        var reader = new FakeDateTakenReader();
        t.DateTakenReader = reader;
        t.ClearEvents();
        await t.OpenAsync();
        await IdleAsync(t);
        Assert.Empty(reader.Reads);
        Assert.Equal(0, DateRows(t));
        Assert.DoesNotContain(t.Events, e => e.ItemIds.Contains(photo.Id));
        await t.Library.DisposeAsync();

        SetVaultPath(t, realVault);
        await t.OpenAsync();
        await IdleAsync(t);
        Assert.Equal(July, (await t.ItemAsync(photo.Id)).TakenAt);
    }

    [Fact]
    public async Task Lead_AReaderThatThrows_LeavesTheFileForTheNextStart()
    {
        var throwing = new FakeDateTakenReader { OnRead = _ => throw new InvalidOperationException("COM is unhappy") };
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: throwing);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await IdleAsync(t);
        Assert.Null((await t.ItemAsync(photo.Id)).TakenAt);
        Assert.Equal(0, DateRows(t));

        var reader = new FakeDateTakenReader();
        t.DateTakenReader = reader;
        await t.ReopenAsync();
        await IdleAsync(t);

        Assert.Equal(July, (await t.ItemAsync(photo.Id)).TakenAt);
        Assert.Single(reader.Reads);
    }

    [Fact]
    public async Task Lead_ThePassRaisesFewEvents_EachNamingOnlyItems()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var paths = Enumerable.Range(0, 200).Select(i => t.CreateFile($"p{i:000}.jpg", FakeDateTakenReader.Photo(July.AddMinutes(i)))).ToArray();
        await t.Library.AddPathsAsync(paths, new AddRequest { TargetCategoryId = images.Id, Mode = ItemMode.Copy });
        t.DateTakenReader = new FakeDateTakenReader { Delay = TimeSpan.FromMilliseconds(2) };
        t.ClearEvents();

        var passing = System.Diagnostics.Stopwatch.StartNew();
        await t.ReopenAsync();
        await IdleAsync(t);
        passing.Stop();

        var dateEvents = t.Events.Where(e => e.Kind == LibraryChangeKind.Items).ToList();
        Assert.Equal(200, dateEvents.SelectMany(e => e.ItemIds).Distinct().Count());
        Assert.All(dateEvents, e => Assert.Empty(e.CategoryIds));

        // At most one event a second while the pass runs, plus the final flush (the pass is ~0.8 s unless the machine is busy).
        var allowed = (int)Math.Ceiling(passing.Elapsed.TotalSeconds) + 1;
        Assert.InRange(dateEvents.Count, 1, allowed);
    }

    private static async Task IdleAsync(TestLibrary t) => await t.Service.WhenDatesTakenIdleAsync().WaitAsync(Patience);

    private static long DateRows(TestLibrary t)
    {
        using var connection = OpenRaw(t);
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_dates");
    }

    private static void SetVaultPath(TestLibrary t, string vault)
    {
        using var connection = OpenRaw(t);
        connection.Execute("UPDATE settings SET value = json_set(value, '$.VaultPath', @vault) WHERE key = 'app_settings'", new { vault });
    }

    private static SqliteConnection OpenRaw(TestLibrary t)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(t.DataDirectory, Database.FileName),
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
