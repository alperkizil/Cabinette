using System.Diagnostics;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>Date taken of photos and videos (SPEC.md section 4, "Gallery"), read in the background with a fake reader.</summary>
public sealed class DateTakenTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly DateTimeOffset July = new(2019, 7, 14, 12, 30, 45, TimeSpan.Zero);
    private static readonly DateTimeOffset March = new(2021, 3, 2, 8, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task AddedPhoto_GetsItsDateTaken_FromTheVaultCopy_AndOpenThreadsAreTold()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");

        var added = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await IdleAsync(t);

        Assert.Equal(July, (await t.ItemAsync(added.Id)).TakenAt);
        Assert.Equal(July, Assert.Single(await t.Library.GetThreadItemsAsync(images.Id)).TakenAt);

        // The copy in the vault was read (it keeps the source's metadata), once.
        Assert.Equal([added.FullPath], reader.Reads);
        Assert.StartsWith(t.Vault, added.FullPath, StringComparison.OrdinalIgnoreCase);

        // Only the item changed: threads showing it redraw it.
        var change = t.Events.Last();
        Assert.Equal(LibraryChangeKind.Items, change.Kind);
        Assert.Equal([added.Id], change.ItemIds);
    }

    [Fact]
    public async Task PointerPhotoAndVideo_AreReadWhereTheyAre()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var photo = t.CreateFile("linked.heic", FakeDateTakenReader.Photo(July));
        var video = t.CreateFile("clip.mp4", FakeDateTakenReader.Photo(March));

        var result = await t.AddAsync(null, ItemMode.Pointer, photo, video);
        await IdleAsync(t);

        Assert.Equal(2, result.Added.Count);
        Assert.Equal(July, (await t.ItemAsync(result.Added.Single(i => i.FileName == "linked.heic").Id)).TakenAt);
        Assert.Equal(March, (await t.ItemAsync(result.Added.Single(i => i.FileName == "clip.mp4").Id)).TakenAt);
        Assert.Equal(1, reader.ReadsOf(photo));
        Assert.Equal(1, reader.ReadsOf(video));
    }

    [Fact]
    public async Task NotesLinksPastedImagesAndOtherFiles_AreNeverRead()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var docs = await t.BuiltInAsync("documents");
        var pdf = await t.AddOneAsync(docs.Id, t.CreateFile("scan.pdf", FakeDateTakenReader.Photo(July)));
        var text = await t.AddOneAsync(docs.Id, t.CreateFile("notes.txt", FakeDateTakenReader.Photo(July, "txt")));
        var note = await t.Library.CreateNoteAsync(docs.Id, FakeDateTakenReader.Photo(July, "note"));
        var pasted = Assert.Single((await t.Library.AddPastedImageAsync(docs.Id, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3])).Added);
        var link = Assert.Single((await t.Library.AddPastedTextAsync(docs.Id, "https://example.com/photo.jpg")).Added);
        await IdleAsync(t);

        Assert.Empty(reader.Reads);
        foreach (var id in new[] { pdf.Id, text.Id, note.Id, pasted.Id, link.Id })
        {
            Assert.Null((await t.ItemAsync(id)).TakenAt);
        }

        Assert.Equal(0, DateRows(t));
    }

    [Fact]
    public async Task RenamedToAnotherType_ShowsNoDateTaken_AndBackToAPhoto_ShowsItAgain()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await IdleAsync(t);

        await t.Library.RenameItemAsync(photo.Id, "photo.txt");
        await IdleAsync(t);
        Assert.Null((await t.ItemAsync(photo.Id)).TakenAt);

        await t.Library.RenameItemAsync(photo.Id, "photo.jpeg");
        await IdleAsync(t);
        Assert.Equal(July, (await t.ItemAsync(photo.Id)).TakenAt);

        // Renames keep the file as it was: it was read once.
        Assert.Single(reader.Reads);
    }

    [Fact]
    public async Task PhotoWithoutADate_IsReadOnce_NotOnEveryStart()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var screenshot = await t.AddOneAsync(images.Id, t.CreateFile("screenshot.png", "no metadata"));
        await IdleAsync(t);
        Assert.Null((await t.ItemAsync(screenshot.Id)).TakenAt);
        Assert.Equal(1, DateRows(t));
        t.ClearEvents();

        await t.ReopenAsync();
        await IdleAsync(t);

        Assert.Single(reader.Reads);
        Assert.Null((await t.ItemAsync(screenshot.Id)).TakenAt);
        Assert.Empty(t.Events); // nothing changed, nobody is told
    }

    [Fact]
    public async Task WithoutAReader_NothingIsRead()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");

        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));

        Assert.True(t.Service.WhenDatesTakenIdleAsync().IsCompleted);
        Assert.Null((await t.ItemAsync(photo.Id)).TakenAt);
        await t.ReopenAsync();
        Assert.True(t.Service.WhenDatesTakenIdleAsync().IsCompleted);
        Assert.Null((await t.ItemAsync(photo.Id)).TakenAt);
        Assert.Equal(0, DateRows(t));
    }

    [Fact]
    public async Task ExistingPhotos_AreReadWhenTheLibraryOpens()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var paths = Enumerable.Range(0, 70)
            .Select(i => t.CreateFile($"photo{i:00}.jpg", FakeDateTakenReader.Photo(July.AddDays(i))))
            .ToArray();
        var added = await t.Library.AddPathsAsync(paths, new AddRequest { TargetCategoryId = images.Id, Mode = ItemMode.Copy });
        Assert.Equal(70, added.Added.Count);
        var reader = new FakeDateTakenReader();
        t.DateTakenReader = reader;

        await t.ReopenAsync();
        await IdleAsync(t);

        Assert.Equal(70, reader.ReadCount);
        var thread = await t.Library.GetThreadItemsAsync(images.Id);
        Assert.All(thread, item => Assert.Equal(July.AddDays(int.Parse(item.FileName[5..7])), item.TakenAt));
        Assert.Equal(70, t.Events.Where(e => e.Kind == LibraryChangeKind.Items).SelectMany(e => e.ItemIds).Distinct().Count());
    }

    [Fact]
    public async Task ThePassOverExistingPhotos_StopsOnDispose_AndGoesOnAfterTheNextStart()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var paths = Enumerable.Range(0, 70).Select(i => t.CreateFile($"photo{i:00}.jpg", FakeDateTakenReader.Photo(July.AddDays(i)))).ToArray();
        await t.Library.AddPathsAsync(paths, new AddRequest { TargetCategoryId = images.Id, Mode = ItemMode.Copy });

        // The first session's reader gets stuck after 40 files (a file on a drive that doesn't answer).
        using var stuck = new ManualResetEventSlim(false);
        var first = new FakeDateTakenReader();
        first.OnRead = _ =>
        {
            if (first.ReadCount > 40)
            {
                stuck.Wait(TimeSpan.FromMinutes(1));
            }
        };
        t.DateTakenReader = first;
        await t.ReopenAsync();
        Assert.True(SpinWait.SpinUntil(() => first.ReadCount > 40, Patience));

        // Disposing doesn't wait for the stuck read.
        var closing = Stopwatch.StartNew();
        await t.Library.DisposeAsync();
        Assert.True(closing.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {closing.Elapsed}.");
        var stored = DateRows(t);
        Assert.InRange(stored, 1, 69);
        stuck.Set();

        var second = new FakeDateTakenReader();
        t.DateTakenReader = second;
        await t.OpenAsync();
        await IdleAsync(t);

        Assert.Equal(70 - stored, second.ReadCount);
        Assert.Equal(70, DateRows(t));
        Assert.All(await t.Library.GetThreadItemsAsync(images.Id), item => Assert.NotNull(item.TakenAt));
    }

    [Fact]
    public async Task ChangedPointerFile_IsReadAgainWhenTheLibraryOpens_AnUnchangedOneIsNot()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var changed = t.CreateFile("edited.jpg", FakeDateTakenReader.Photo(July), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var same = t.CreateFile("same.jpg", FakeDateTakenReader.Photo(July, "same"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await t.AddAsync(null, ItemMode.Pointer, changed, same);
        await IdleAsync(t);
        var editedId = result.Added.Single(i => i.FileName == "edited.jpg").Id;
        Assert.Equal(July, (await t.ItemAsync(editedId)).TakenAt);
        await t.Library.DisposeAsync();

        // Another program fixes the photo's date while Cabinette is closed (pointer files aren't watched).
        File.WriteAllText(changed, FakeDateTakenReader.Photo(March));
        File.SetLastWriteTimeUtc(changed, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        t.ClearEvents();
        await t.OpenAsync();
        await IdleAsync(t);

        Assert.Equal(March, (await t.ItemAsync(editedId)).TakenAt);
        Assert.Equal(2, reader.ReadsOf(changed));
        Assert.Equal(1, reader.ReadsOf(same));
        Assert.Contains(t.Events, e => e.Kind == LibraryChangeKind.Items && e.ItemIds.Contains(editedId));
    }

    [Fact]
    public async Task PointerRelocatedToAnotherFile_IsReadThere()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var original = t.CreateFile(@"a\photo.jpg", FakeDateTakenReader.Photo(July));
        var item = await t.AddOneAsync(null, original, ItemMode.Pointer);
        await IdleAsync(t);
        File.Delete(original);
        var elsewhere = t.CreateFile(@"b\photo.jpg", FakeDateTakenReader.Photo(March, "edited"));

        Assert.Equal(RelocateOutcome.Relocated, await t.Library.RelocateItemAsync(item.Id, elsewhere, acceptHashMismatch: true));
        await IdleAsync(t);

        Assert.Equal(March, (await t.ItemAsync(item.Id)).TakenAt);
        Assert.Equal(1, reader.ReadsOf(elsewhere));
    }

    [Fact]
    public async Task VaultFileChangedByAnotherProgram_IsReadAgain()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await IdleAsync(t);

        t.WriteVaultFile(@"Images\photo.jpg", FakeDateTakenReader.Photo(March, "re-saved by a photo editor"),
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, (await t.RescanAsync()).Modified);
        await IdleAsync(t);

        Assert.Equal(March, (await t.ItemAsync(photo.Id)).TakenAt);
        Assert.Equal(2, reader.ReadsOf(photo.FullPath));
    }

    [Fact]
    public async Task FileRenamedWhileBeingRead_IsReadAgainAtItsNewPlace()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        LibraryItem? photo = null;
        using var added = new ManualResetEventSlim(false);
        reader.OnRead = _ =>
        {
            // The first read: the user renames the file just before the reader opens it.
            if (reader.ReadCount == 1)
            {
                Assert.True(added.Wait(Patience));
                t.Library.RenameItemAsync(photo!.Id, "renamed.jpg").GetAwaiter().GetResult();
            }
        };
        photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        added.Set();
        await IdleAsync(t);

        var renamed = await t.ItemAsync(photo.Id);
        Assert.Equal("renamed.jpg", renamed.FileName);
        Assert.Equal(July, renamed.TakenAt);
        Assert.Equal(1, reader.ReadsOf(renamed.FullPath));
    }

    [Fact]
    public async Task ReadingNeverHoldsTheWriteLock_SoAddsAndEditsGoOnMeanwhile()
    {
        using var gate = new ManualResetEventSlim(false);
        var reader = new FakeDateTakenReader { Gate = gate };
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var first = await t.AddOneAsync(images.Id, t.CreateFile("first.jpg", FakeDateTakenReader.Photo(July)));

        // The worker is now inside the reader, which doesn't return until the gate opens.
        Assert.True(await reader.Entered.WaitAsync(Patience));
        Assert.False(t.Service.IsWriteLockHeld);
        var second = await t.AddOneAsync(images.Id, t.CreateFile("second.jpg", FakeDateTakenReader.Photo(March))).WaitAsync(Patience);
        await t.Library.AddTagsAsync([first.Id], ["holiday"]).WaitAsync(Patience);
        await t.Library.SetStarredAsync([first.Id], true).WaitAsync(Patience);
        await t.Library.RenameItemAsync(second.Id, "second-renamed.jpg").WaitAsync(Patience);
        Assert.Null((await t.ItemAsync(first.Id)).TakenAt);

        gate.Set();
        await IdleAsync(t);

        Assert.Equal(July, (await t.ItemAsync(first.Id)).TakenAt);
        Assert.Equal(March, (await t.ItemAsync(second.Id)).TakenAt);
    }

    [Fact]
    public async Task ASlowReader_DoesNotSlowAdds()
    {
        var reader = new FakeDateTakenReader { Delay = TimeSpan.FromMilliseconds(750) };
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var paths = Enumerable.Range(0, 8).Select(i => t.CreateFile($"p{i}.jpg", FakeDateTakenReader.Photo(July.AddHours(i)))).ToArray();

        var adding = Stopwatch.StartNew();
        var result = await t.Library.AddPathsAsync(paths, new AddRequest { TargetCategoryId = images.Id, Mode = ItemMode.Copy });
        adding.Stop();

        // Reading all eight takes 6 s; the add is done long before.
        Assert.Equal(8, result.Added.Count);
        Assert.True(adding.Elapsed < TimeSpan.FromSeconds(4), $"Adding took {adding.Elapsed}.");
        await IdleAsync(t);
        Assert.All(await t.Library.GetThreadItemsAsync(images.Id), item => Assert.NotNull(item.TakenAt));
    }

    [Fact]
    public async Task DeletingTheItem_RemovesItsDateTaken()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await IdleAsync(t);
        Assert.Equal(1, DateRows(t));

        await t.Library.DeleteItemsAsync([photo.Id]);

        Assert.Equal(0, DateRows(t));
    }

    [Fact]
    public async Task ADateTakenOfAnItemThatIsGone_IsDroppedWhenTheLibraryOpens()
    {
        var reader = new FakeDateTakenReader();
        await using var t = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await IdleAsync(t);
        await t.Library.DisposeAsync();

        // Rows deleted with foreign keys off (an older tool, a hand-edited database) leave the date behind.
        using (var connection = OpenRaw(t))
        {
            connection.Execute("PRAGMA foreign_keys = OFF; DELETE FROM item_categories WHERE item_id = @id; DELETE FROM items WHERE id = @id;",
                new { id = photo.Id });
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_dates"));
        }

        t.DateTakenReader = null;
        await t.OpenAsync();
        Assert.Equal(0, DateRows(t));
        var next = await t.AddOneAsync(images.Id, t.CreateFile("other.jpg", "no date"));
        Assert.Equal(photo.Id, next.Id);
        Assert.Null(next.TakenAt);
    }

    [Fact]
    public async Task BackupAndRestore_KeepTheDatesTaken()
    {
        var reader = new FakeDateTakenReader();
        await using var source = await TestLibrary.CreateAsync(dateTakenReader: reader);
        var images = await source.BuiltInAsync("images");
        var photo = await source.AddOneAsync(images.Id, source.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        var plain = await source.AddOneAsync(images.Id, source.CreateFile("plain.png", "no date"));
        await IdleAsync(source);
        var zip = Path.Combine(source.Root, "backup.zip");
        await source.Library.CreateBackupAsync(zip);

        var restoredReader = new FakeDateTakenReader();
        await using var target = await TestLibrary.CreateAsync(dateTakenReader: restoredReader);
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));
        await IdleAsync(target);

        Assert.Equal(July, (await target.ItemAsync(photo.Id)).TakenAt);
        Assert.Null((await target.ItemAsync(plain.Id)).TakenAt);
        Assert.Empty(restoredReader.Reads); // the backup had them: nothing is read again
        Assert.Equal(2, DateRows(target));
    }

    [Fact]
    public async Task ExistingLibrary_FromBeforeDatesTaken_GainsThemWhenOpened()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));
        await t.Library.SetStarredAsync([photo.Id], true);
        await t.Library.DisposeAsync();

        // A library made before dates taken existed: the same schema (version 1) without the table.
        using (var connection = OpenRaw(t))
        {
            connection.Execute("DROP TABLE item_dates;");
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
        }

        var reader = new FakeDateTakenReader();
        t.DateTakenReader = reader;
        await t.OpenAsync();
        await IdleAsync(t);

        var reopened = await t.ItemAsync(photo.Id);
        Assert.True(reopened.IsStarred);
        Assert.Equal(July, reopened.TakenAt);
        using (var connection = OpenRaw(t))
        {
            // No version bump: earlier builds still open the library and ignore the table.
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
            Assert.Equal(1, Database.SchemaVersion);
        }
    }

    [Fact]
    public async Task Restore_OfABackupMadeBeforeDatesTaken_ReadsThemInTheRestoredVault()
    {
        await using var source = await TestLibrary.CreateAsync();
        var images = await source.BuiltInAsync("images");
        var photo = await source.AddOneAsync(images.Id, source.CreateFile("photo.jpg", FakeDateTakenReader.Photo(July)));

        // The live library loses the table, as if an earlier build had made it; so has its backup.
        using (var connection = OpenRaw(source))
        {
            connection.Execute("DROP TABLE item_dates;");
        }

        var zip = Path.Combine(source.Root, "old.zip");
        await source.Library.CreateBackupAsync(zip);

        var reader = new FakeDateTakenReader();
        await using var target = await TestLibrary.CreateAsync(dateTakenReader: reader);
        await target.Library.DisposeAsync();
        var restoredVault = Path.Combine(target.Root, "restored");
        target.Use(await LibraryFactory.RestoreAsync(zip, restoredVault, target.Options, target.RecycleBin));
        await IdleAsync(target);

        Assert.Equal(July, (await target.ItemAsync(photo.Id)).TakenAt);
        Assert.StartsWith(restoredVault, Assert.Single(reader.Reads), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task IdleAsync(TestLibrary t) => await t.Service.WhenDatesTakenIdleAsync().WaitAsync(Patience);

    private static long DateRows(TestLibrary t)
    {
        using var connection = OpenRaw(t);
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_dates");
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
