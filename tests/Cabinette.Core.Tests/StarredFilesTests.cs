using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>Starred files (SPEC.md section 4).</summary>
public sealed class StarredFilesTests
{
    [Fact]
    public async Task Star_ThenUnstar_ShowsOnTheItem_AndTellsOpenViews()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var report = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report"));
        var other = await t.AddOneAsync(docs.Id, t.CreateFile("other.pdf", "other"));
        Assert.Null(report.StarredAt);
        t.ClearEvents();

        await t.Library.SetStarredAsync([report.Id], true);

        var starred = await t.ItemAsync(report.Id);
        Assert.Equal(t.Time.GetUtcNow(), starred.StarredAt);
        Assert.True(starred.IsStarred);
        Assert.False((await t.ItemAsync(other.Id)).IsStarred);
        var thread = await t.Library.GetThreadItemsAsync(docs.Id);
        Assert.True(thread.Single(i => i.Id == report.Id).IsStarred);
        Assert.False(thread.Single(i => i.Id == other.Id).IsStarred);
        var change = Assert.Single(t.Events);
        Assert.Equal(LibraryChangeKind.Items | LibraryChangeKind.Starred, change.Kind);
        Assert.Equal([report.Id], change.ItemIds);

        t.ClearEvents();
        await t.Library.SetStarredAsync([report.Id], false);

        Assert.Null((await t.ItemAsync(report.Id)).StarredAt);
        Assert.Empty(await t.Library.GetStarredItemsAsync());
        Assert.Equal([report.Id], Assert.Single(t.Events).ItemIds);
    }

    [Fact]
    public async Task Starring_AnAlreadyStarredFile_KeepsItsTime_AndNothingChangedRaisesNothing()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var first = await t.AddOneAsync(docs.Id, t.CreateFile("first.pdf", "1"));
        var second = await t.AddOneAsync(docs.Id, t.CreateFile("second.pdf", "2"));
        await t.Library.SetStarredAsync([first.Id], true);
        var firstStarredAt = t.Time.GetUtcNow();
        t.Time.Advance(TimeSpan.FromMinutes(5));
        t.ClearEvents();

        // A mixed selection: the unstarred one gets a star now, the starred one keeps its time.
        await t.Library.SetStarredAsync([first.Id, second.Id, 999_999], true);

        Assert.Equal(firstStarredAt, (await t.ItemAsync(first.Id)).StarredAt);
        Assert.Equal(t.Time.GetUtcNow(), (await t.ItemAsync(second.Id)).StarredAt);
        Assert.Equal([second.Id], Assert.Single(t.Events).ItemIds);

        t.ClearEvents();
        await t.Library.SetStarredAsync([first.Id, second.Id], true);
        await t.Library.SetStarredAsync([999_999], false);
        Assert.Empty(t.Events);
    }

    [Fact]
    public async Task StarredItems_ComeInTheOrderTheyWereStarred_NewestLast_FromEveryCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var invoices = await t.Library.CreateCategoryAsync("Invoices", work.Id);
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "a"));
        var b = await t.AddOneAsync(images.Id, t.CreateFile("b.png", "b"));
        var c = await t.AddOneAsync(invoices.Id, t.CreateFile("c.pdf", "c"), ItemMode.Pointer);
        var d = await t.AddOneAsync(docs.Id, t.CreateFile("d.pdf", "d"));
        var e = await t.AddOneAsync(docs.Id, t.CreateFile("e.pdf", "e"));

        await t.Library.SetStarredAsync([c.Id], true);
        t.Time.Advance(TimeSpan.FromMinutes(1));
        await t.Library.SetStarredAsync([a.Id], true);
        t.Time.Advance(TimeSpan.FromMinutes(1));

        // Starred together (one selection, any order): they keep the order they were added in.
        await t.Library.SetStarredAsync([e.Id, b.Id], true);

        var starred = await t.Library.GetStarredItemsAsync();
        Assert.Equal([c.Id, a.Id, b.Id, e.Id], starred.Select(i => i.Id));
        Assert.DoesNotContain(d.Id, starred.Select(i => i.Id));
        Assert.Equal(invoices.Id, starred[0].HomeCategoryId);
        Assert.Equal(c.FullPath, starred[0].FullPath);
        Assert.All(starred, i => Assert.True(i.IsStarred));

        var summary = await t.Library.GetStarredSummaryAsync();
        Assert.Equal(4, summary.Count);
        Assert.Equal(t.Time.GetUtcNow(), summary.LastStarredAt);
    }

    [Fact]
    public async Task SearchAndFilters_InTheStarredView_WorkAsInAThread()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");
        var notes = await t.BuiltInAsync("notes");
        var invoice = await t.AddOneAsync(docs.Id, t.CreateFile("invoice march.pdf", "i"));
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("march trip.png", "p"));
        var note = await t.Library.CreateNoteAsync(notes.Id, "call the bank in march");
        var plain = await t.AddOneAsync(docs.Id, t.CreateFile("unstarred march.pdf", "u"));
        await t.Library.AddTagsAsync([invoice.Id], ["tax"]);
        await t.Library.SetStarredAsync([invoice.Id, photo.Id, note.Id], true);

        // Note text counts, like in a thread; files that aren't starred never show.
        Assert.Equal([invoice.Id, photo.Id, note.Id], (await t.Library.GetStarredItemsAsync("march")).Select(i => i.Id));
        Assert.Equal([note.Id], (await t.Library.GetStarredItemsAsync("bank")).Select(i => i.Id));
        Assert.Equal([invoice.Id], (await t.Library.GetStarredItemsAsync("#tax")).Select(i => i.Id));
        Assert.Equal([photo.Id], (await t.Library.GetStarredItemsAsync(null, new ItemFilter { TypeKeys = ["images"] })).Select(i => i.Id));
        Assert.Equal([invoice.Id], (await t.Library.GetStarredItemsAsync("march", new ItemFilter { Extensions = ["pdf"] })).Select(i => i.Id));
        Assert.DoesNotContain(plain.Id, (await t.Library.GetStarredItemsAsync("unstarred")).Select(i => i.Id));
    }

    [Fact]
    public async Task Summary_IsEmptyWhileNothingIsStarred()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "a"));

        Assert.Equal(new StarredSummary(0, null), await t.Library.GetStarredSummaryAsync());

        await t.Library.SetStarredAsync([item.Id], true);
        await t.Library.SetStarredAsync([item.Id], false);
        Assert.Equal(new StarredSummary(0, null), await t.Library.GetStarredSummaryAsync());
    }

    [Fact]
    public async Task Star_StaysThroughMoveRenameAndCategoryChanges()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var trips = await t.Library.CreateCategoryAsync("Trips", null);
        var copy = await t.AddOneAsync(images.Id, t.CreateFile("beach.png", "beach"));
        var pointer = await t.AddOneAsync(images.Id, t.CreateFile("linked.png", "linked"), ItemMode.Pointer);
        await t.Library.SetStarredAsync([copy.Id, pointer.Id], true);
        var starredAt = t.Time.GetUtcNow();
        t.Time.Advance(TimeSpan.FromHours(1));

        // "Move to category…" moves the vault file physically; its star goes along.
        await t.Library.MoveToCategoryAsync([copy.Id, pointer.Id], images.Id, trips.Id);
        await t.Library.RenameItemAsync(copy.Id, "sunset.png");
        await t.Library.RenameItemAsync(pointer.Id, "renamed link.png");
        await t.Library.AddToCategoryAsync([copy.Id], work.Id);
        await t.Library.RemoveFromCategoryAsync([copy.Id], work.Id);
        await t.Library.RenameCategoryAsync(trips.Id, "Trips 2026");

        var moved = await t.ItemAsync(copy.Id);
        Assert.Equal(t.VaultPath(@"Trips 2026\sunset.png"), moved.FullPath);
        Assert.True(File.Exists(moved.FullPath));
        Assert.Equal(starredAt, moved.StarredAt);
        Assert.Equal(starredAt, (await t.ItemAsync(pointer.Id)).StarredAt);
        var starred = await t.Library.GetStarredItemsAsync();
        Assert.Equal([copy.Id, pointer.Id], starred.Select(i => i.Id));
        Assert.Equal(moved.FullPath, starred[0].FullPath);
    }

    [Fact]
    public async Task FileInDeleted_KeepsItsStar_AndEmptyingDeletedRemovesIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var item = await t.AddOneAsync(work.Id, t.CreateFile("plan.txt", "plan"));
        await t.Library.SetStarredAsync([item.Id], true);

        await t.Library.DeleteCategoryAsync(work.Id, CategoryDeletePolicy.MoveToDeleted);

        var inDeleted = await t.ItemAsync(item.Id);
        Assert.True(inDeleted.IsStarred);
        Assert.Equal(Assert.Single(await t.Library.GetStarredItemsAsync()).Id, item.Id);
        Assert.Equal(1, (await t.Library.GetStarredSummaryAsync()).Count);
        t.ClearEvents();

        await t.Library.EmptyDeletedAsync();

        Assert.Empty(await t.Library.GetStarredItemsAsync());
        Assert.Equal(0, (await t.Library.GetStarredSummaryAsync()).Count);
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Starred));
    }

    [Fact]
    public async Task DeletingAStarredFile_RemovesTheStar_AndSaysSo()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var starred = await t.AddOneAsync(docs.Id, t.CreateFile("starred.pdf", "s"));
        var plain = await t.AddOneAsync(docs.Id, t.CreateFile("plain.pdf", "p"));
        await t.Library.SetStarredAsync([starred.Id], true);

        t.ClearEvents();
        await t.Library.DeleteItemsAsync([plain.Id]);
        Assert.False(Assert.Single(t.Events).Kind.HasFlag(LibraryChangeKind.Starred));

        t.ClearEvents();
        await t.Library.DeleteItemsAsync([starred.Id]);

        Assert.True(Assert.Single(t.Events).Kind.HasFlag(LibraryChangeKind.Starred));
        Assert.Empty(await t.Library.GetStarredItemsAsync());
        Assert.Equal(0, StarRows(t));
    }

    [Fact]
    public async Task VaultMonitor_RenameKeepsTheStar_DeleteRemovesIt()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var renamed = await t.AddOneAsync(docs.Id, t.CreateFile("draft.txt", "the draft"));
        var deleted = await t.AddOneAsync(docs.Id, t.CreateFile("gone.txt", "gone"));
        await t.Library.SetStarredAsync([renamed.Id, deleted.Id], true);
        File.Move(renamed.FullPath, t.VaultPath(@"Documents\final.txt"));
        File.Delete(deleted.FullPath);
        t.ClearEvents();

        var summary = await t.RescanAsync();

        Assert.Equal(1, summary.Renamed);
        Assert.Equal(1, summary.Removed);
        var item = Assert.Single(await t.Library.GetStarredItemsAsync());
        Assert.Equal(renamed.Id, item.Id);
        Assert.Equal("final.txt", item.FileName);
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Starred));
    }

    [Fact]
    public async Task BackgroundHashedDuplicate_HandsItsStarToTheFileItIsMergedInto()
    {
        await using var t = await TestLibrary.CreateAsync();
        t.Service.BackgroundHashThresholdBytes = 10;
        var docs = await t.BuiltInAsync("documents");
        var first = await t.AddOneAsync(docs.Id, t.CreateFile("big1.dat", "large identical content"), ItemMode.Pointer);
        await t.Service.WhenBackgroundHashingIdleAsync();

        // Starred while it is still being hashed, then found to be the same file as the first one.
        t.Service.AfterBackgroundHash = id => t.Library.SetStarredAsync([id], true);
        var second = await t.AddOneAsync(docs.Id, t.CreateFile("big2.dat", "large identical content"), ItemMode.Pointer);
        Assert.Null(second.Hash);
        await t.Service.WhenBackgroundHashingIdleAsync();

        Assert.Null(await t.Library.GetItemAsync(second.Id));
        Assert.Equal(first.Id, Assert.Single(await t.Library.GetStarredItemsAsync()).Id);
        Assert.Equal(1, StarRows(t));
    }

    [Fact]
    public async Task ExistingLibrary_FromBeforeStars_GainsThemWhenOpened()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("old.pdf", "old"));
        await t.Library.AddTagsAsync([item.Id], ["kept"]);
        await t.Library.DisposeAsync();

        // A library made before stars existed: the same schema (version 1) without the stars table.
        using (var connection = OpenRaw(t))
        {
            connection.Execute("DROP INDEX ix_item_stars_starred_at; DROP TABLE item_stars;");
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
        }

        await t.OpenAsync();

        var reopened = await t.ItemAsync(item.Id);
        Assert.Equal(["kept"], reopened.Tags);
        Assert.False(reopened.IsStarred);
        Assert.Equal(new StarredSummary(0, null), await t.Library.GetStarredSummaryAsync());
        await t.Library.SetStarredAsync([item.Id], true);
        Assert.Equal(item.Id, Assert.Single(await t.Library.GetStarredItemsAsync()).Id);
        using (var connection = OpenRaw(t))
        {
            // No version bump: earlier builds still open the library and ignore the table.
            Assert.Equal(Database.SchemaVersion, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
            Assert.Equal(1, Database.SchemaVersion);
        }
    }

    [Fact]
    public async Task StarOfAFileThatIsGone_IsDroppedWhenTheLibraryOpens()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "a"));
        await t.Library.SetStarredAsync([item.Id], true);
        await t.Library.DisposeAsync();

        // Rows deleted with foreign keys off (or a hand-edited database) leave a star behind, for an id a new file gets next.
        using (var connection = OpenRaw(t))
        {
            connection.Execute("PRAGMA foreign_keys = OFF; DELETE FROM items WHERE id = @id;", new { id = item.Id });
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_stars"));
        }

        await t.OpenAsync();

        Assert.Equal(0, StarRows(t));
        var next = await t.AddOneAsync(docs.Id, t.CreateFile("b.pdf", "b"));
        Assert.Equal(item.Id, next.Id);
        Assert.False(next.IsStarred);
        Assert.Equal(0, (await t.Library.GetStarredSummaryAsync()).Count);
    }

    [Fact]
    public async Task BackupAndRestore_KeepTheStars()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var images = await source.BuiltInAsync("images");
        var report = await source.AddOneAsync(docs.Id, source.CreateFile("report.pdf", "report"));
        var photo = await source.AddOneAsync(images.Id, source.CreateFile("photo.png", "photo"));
        var plain = await source.AddOneAsync(docs.Id, source.CreateFile("plain.pdf", "plain"));
        await source.Library.SetStarredAsync([photo.Id], true);
        source.Time.Advance(TimeSpan.FromMinutes(3));
        await source.Library.SetStarredAsync([report.Id], true);
        var zip = Path.Combine(source.Root, "backup.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));

        var before = await source.Library.GetStarredItemsAsync();
        var after = await target.Library.GetStarredItemsAsync();
        Assert.Equal([photo.Id, report.Id], after.Select(i => i.Id));
        Assert.Equal(before.Select(i => i.StarredAt), after.Select(i => i.StarredAt));
        Assert.False((await target.ItemAsync(plain.Id)).IsStarred);
        Assert.Equal(await source.Library.GetStarredSummaryAsync(), await target.Library.GetStarredSummaryAsync());
    }

    [Fact]
    public async Task Restore_OfABackupMadeBeforeStars_Works()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var report = await source.AddOneAsync(docs.Id, source.CreateFile("report.pdf", "report"));

        // The live library loses its stars table, as if it had been made by an earlier build; the backup has none either.
        using (var connection = OpenRaw(source))
        {
            connection.Execute("DROP INDEX ix_item_stars_starred_at; DROP TABLE item_stars;");
        }

        var zip = Path.Combine(source.Root, "old.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));

        Assert.Equal("report.pdf", (await target.ItemAsync(report.Id)).FileName);
        Assert.Empty(await target.Library.GetStarredItemsAsync());
        await target.Library.SetStarredAsync([report.Id], true);
        Assert.Single(await target.Library.GetStarredItemsAsync());
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

    private static long StarRows(TestLibrary t)
    {
        using var connection = OpenRaw(t);
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_stars");
    }
}
