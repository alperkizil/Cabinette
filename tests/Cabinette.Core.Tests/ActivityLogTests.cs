using System.IO.Compression;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// The AI activity log (SPEC.md section 10): <see cref="ILibraryService.AddActivityAsync"/> and
/// <see cref="ILibraryService.GetActivityAsync"/> over the activity_log table, and <see cref="InMemoryActivityLog"/> (the sample
/// library and fakes), which follows the same rules. The theories run against both.
/// </summary>
public sealed class ActivityLogTests
{
    private const string LibraryKind = "library";
    private const string MemoryKind = "memory";

    /// <summary>Generous: only reached when something waits that shouldn't.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly string[] ActivityIndexes = ["ix_activity_log_action", "ix_activity_log_at", "ix_activity_log_source"];

    public static TheoryData<string> Stores => new() { LibraryKind, MemoryKind };

    // ================================================================ appending

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Add_StoresTheEntryTrimmed_WithItsTimeAsAUtcInstant(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);

        // 12:26:53 in Istanbul (+03:00) is 09:26:53 UTC; the ticks survive the round trip.
        var at = new DateTimeOffset(2026, 3, 14, 12, 26, 53, TimeSpan.FromHours(3)).AddTicks(1234567);
        var stored = await store.AddAsync(new ActivityEntry
        {
            Id = 99,
            At = at,
            Source = "  Claude Code ",
            Action = "\tsearch\n",
            DetailsJson = """{"ok":true,"files":[{"id":12,"name":"report.pdf"}]}""",
        });
        var second = await store.AddAsync(new ActivityEntry { At = at, Source = "Codex", Action = "list_categories" });

        Assert.True(stored.Id > 0);
        Assert.True(second.Id > stored.Id);
        Assert.Equal("Claude Code", stored.Source);
        Assert.Equal("search", stored.Action);
        Assert.Equal(at, stored.At);
        Assert.Equal(TimeSpan.Zero, stored.At.Offset);
        Assert.Equal("""{"ok":true,"files":[{"id":12,"name":"report.pdf"}]}""", stored.DetailsJson);
        Assert.Null(second.DetailsJson);

        var page = await store.GetAsync(new ActivityQuery());
        Assert.Equal([second, stored], page.Entries);
        Assert.All(page.Entries, e => Assert.Equal(TimeSpan.Zero, e.At.Offset));
        Assert.False(page.HasMore);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Add_WithoutATime_TakesTheClocksTime(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        store.Time.Advance(TimeSpan.FromMinutes(5));

        var stored = await store.AddAsync(new ActivityEntry { Source = "Codex", Action = "search" });

        Assert.Equal(store.Time.GetUtcNow(), stored.At);
        Assert.Equal(stored, Assert.Single((await store.GetAsync(new ActivityQuery())).Entries));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Add_RefusesABlankClientOrTool(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);

        foreach (var blank in new[] { "", "   ", "\t\n", null })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(new ActivityEntry { Source = blank!, Action = "search" }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(new ActivityEntry { Source = "Codex", Action = blank! }));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AddAsync(null!));
        Assert.Empty((await store.GetAsync(new ActivityQuery())).Entries);
    }

    // ================================================================ filters

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TimeFilters_CompareInstants_AcrossOffsetsAndDst(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);

        // 01:30 on 2025-11-02 happened twice in New York: before the clocks went back (EDT, -04:00) and an hour later (EST, -05:00).
        var edt = new DateTimeOffset(2025, 11, 2, 1, 30, 0, TimeSpan.FromHours(-4)); // 05:30 UTC
        var est = new DateTimeOffset(2025, 11, 2, 1, 30, 0, TimeSpan.FromHours(-5)); // 06:30 UTC

        // Local times in the opposite order of the instants: 10:00 in Kiribati was the evening before in UTC, 23:00 on Baker
        // Island the next morning.
        var kiribati = new DateTimeOffset(2025, 11, 2, 10, 0, 0, TimeSpan.FromHours(14)); // 2025-11-01 20:00 UTC
        var baker = new DateTimeOffset(2025, 11, 1, 23, 0, 0, TimeSpan.FromHours(-12)); // 2025-11-02 11:00 UTC
        foreach (var (name, at) in new[] { ("edt", edt), ("est", est), ("kiribati", kiribati), ("baker", baker) })
        {
            await store.AddAsync(new ActivityEntry { At = at, Source = "Codex", Action = name });
        }

        // From is inclusive, To exclusive.
        Assert.Equal(["baker", "est"], await ActionsAsync(store, new ActivityQuery { From = est }));
        Assert.Equal(["kiribati", "edt"], await ActionsAsync(store, new ActivityQuery { To = est.ToOffset(TimeSpan.FromHours(3)) }));
        Assert.Equal(["edt"], await ActionsAsync(store, new ActivityQuery { From = edt, To = est }));

        // The same instant as edt, in Tokyo.
        Assert.Equal(["baker", "est", "edt"], await ActionsAsync(store, new ActivityQuery { From = edt.ToOffset(TimeSpan.FromHours(9)) }));
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { From = est, To = est }));
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { From = baker, To = kiribati }));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ToolFilter_MatchesAnyOfTheNames_NullOrEmptyMeansAll(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        foreach (var tool in new[] { "search", "add_files", "delete_files", "search", "rename_file" })
        {
            await store.AddAsync(new ActivityEntry { Source = "Codex", Action = tool });
        }

        string[] all = ["rename_file", "search", "delete_files", "add_files", "search"];
        Assert.Equal(all, await ActionsAsync(store, new ActivityQuery()));
        Assert.Equal(all, await ActionsAsync(store, new ActivityQuery { Actions = [] }));
        Assert.Equal(["delete_files", "add_files"], await ActionsAsync(store, new ActivityQuery { Actions = ["add_files", "delete_files"] }));
        Assert.Equal(["search", "search"], await ActionsAsync(store, new ActivityQuery { Actions = ["search", "search"] }));

        // Tool names are exact; they are compared trimmed, as they are stored.
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { Actions = ["SEARCH"] }));
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { Actions = ["delete"] }));
        Assert.Equal(["search", "search"], await ActionsAsync(store, new ActivityQuery { Actions = [" search "] }));
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { Actions = [""] }));
        var none = await store.GetAsync(new ActivityQuery { Actions = ["nope"] });
        Assert.Empty(none.Entries);
        Assert.False(none.HasMore);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ClientFilter_IsExact_ButIgnoresCase(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        foreach (var client in new[] { "Claude Code", "Codex", "Çursor", "claude-code", "CODEX", "Claude Code" })
        {
            await store.AddAsync(new ActivityEntry { Source = client, Action = "search" });
        }

        Assert.Equal(["Claude Code", "Claude Code"], await SourcesAsync(store, "claude code"));
        Assert.Equal(["Claude Code", "Claude Code"], await SourcesAsync(store, "CLAUDE CODE"));
        Assert.Equal(["CODEX", "Codex"], await SourcesAsync(store, "codex"));
        Assert.Equal(["claude-code"], await SourcesAsync(store, "Claude-Code"));

        // Beyond ASCII, which SQLite's NOCASE leaves alone.
        Assert.Equal(["Çursor"], await SourcesAsync(store, "çURSOR"));

        // Whole names only.
        Assert.Empty(await SourcesAsync(store, "Claude"));
        Assert.Empty(await SourcesAsync(store, "nobody"));

        // A blank client means every client, like none.
        Assert.Equal(6, (await SourcesAsync(store, "  ")).Length);
        Assert.Equal(6, (await SourcesAsync(store, null)).Length);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Filters_Combine_AndPageTogether(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        var start = store.Time.GetUtcNow() - TimeSpan.FromDays(2);
        var ids = new List<long>();
        for (var i = 0; i < 12; i++)
        {
            var entry = await store.AddAsync(new ActivityEntry
            {
                At = start.AddHours(i),
                Source = i % 2 == 0 ? "Claude Code" : "Codex",
                Action = i % 3 == 0 ? "delete_files" : "search",
            });
            ids.Add(entry.Id);
        }

        // Hours 1 to 10, Claude Code, searches: entries 2, 4, 8 and 10.
        var query = new ActivityQuery
        {
            From = start.AddHours(1),
            To = start.AddHours(11).ToOffset(TimeSpan.FromHours(-7)),
            Source = "claude code",
            Actions = ["search"],
            Limit = 2,
        };
        var first = await store.GetAsync(query);
        Assert.Equal([10, 8], first.Entries.Select(e => ids.IndexOf(e.Id)));
        Assert.True(first.HasMore);

        var second = await store.GetAsync(query with { BeforeId = first.Entries[^1].Id });
        Assert.Equal([4, 2], second.Entries.Select(e => ids.IndexOf(e.Id)));
        Assert.False(second.HasMore);
    }

    // ================================================================ paging and the limit

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Pages_GoNewestFirst_WithBeforeId(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        var ids = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            ids.Add((await store.AddAsync(new ActivityEntry { Source = "Codex", Action = "search" })).Id);
        }

        var first = await store.GetAsync(new ActivityQuery { Limit = 3 });
        Assert.Equal([ids[6], ids[5], ids[4]], first.Entries.Select(e => e.Id));
        Assert.True(first.HasMore);

        var second = await store.GetAsync(new ActivityQuery { Limit = 3, BeforeId = first.Entries[^1].Id });
        Assert.Equal([ids[3], ids[2], ids[1]], second.Entries.Select(e => e.Id));
        Assert.True(second.HasMore);

        var third = await store.GetAsync(new ActivityQuery { Limit = 3, BeforeId = second.Entries[^1].Id });
        Assert.Equal([ids[0]], third.Entries.Select(e => e.Id));
        Assert.False(third.HasMore);

        // Exactly the limit left: no more.
        var exact = await store.GetAsync(new ActivityQuery { Limit = 4, BeforeId = ids[4] });
        Assert.Equal([ids[3], ids[2], ids[1], ids[0]], exact.Entries.Select(e => e.Id));
        Assert.False(exact.HasMore);
        Assert.False((await store.GetAsync(new ActivityQuery { Limit = 7 })).HasMore);

        Assert.Empty((await store.GetAsync(new ActivityQuery { BeforeId = ids[0] })).Entries);
        Assert.Equal(7, (await store.GetAsync(new ActivityQuery { BeforeId = ids[6] + 100 })).Entries.Count);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Limit_IsClampedTo1Through1000(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        var ids = new List<long>();
        for (var i = 0; i < 1001; i++)
        {
            ids.Add((await store.AddAsync(new ActivityEntry { Source = "Codex", Action = "t" + i })).Id);
        }

        foreach (var limit in new[] { 1000, 1001, 5000, int.MaxValue })
        {
            var page = await store.GetAsync(new ActivityQuery { Limit = limit });
            Assert.Equal(1000, page.Entries.Count);
            Assert.Equal(ids[1000], page.Entries[0].Id);
            Assert.True(page.HasMore);
        }

        foreach (var limit in new[] { 1, 0, -1, int.MinValue })
        {
            var page = await store.GetAsync(new ActivityQuery { Limit = limit });
            Assert.Equal(ids[1000], Assert.Single(page.Entries).Id);
            Assert.True(page.HasMore);
        }

        Assert.Equal(200, (await store.GetAsync(new ActivityQuery())).Entries.Count);
        var rest = await store.GetAsync(new ActivityQuery { Limit = 1000, BeforeId = ids[1000] });
        Assert.Equal(ids[999], rest.Entries[0].Id);
        Assert.Equal(ids[0], rest.Entries[^1].Id);
        Assert.False(rest.HasMore);
    }

    // ================================================================ retention

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reads_LeaveOutEntriesPastAYear_BeforeThePruneRemovesThem(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        var now = store.Time.GetUtcNow();
        var year = ILibraryService.ActivityRetention;
        await AddAsync(store, "ancient", now - TimeSpan.FromDays(400));
        var exactlyAYear = await AddAsync(store, "exactlyAYear", now - year);
        await AddAsync(store, "aTickOlder", now - year - TimeSpan.FromTicks(1));
        var recent = await AddAsync(store, "recent", now - TimeSpan.FromDays(300));
        await store.SettleAsync();

        // The log was pruned when it opened, before these were added, so all four are kept; reads leave out what is past a year.
        Assert.Equal(["recent", "aTickOlder", "exactlyAYear", "ancient"], await store.StoredActionsAsync());
        Assert.Equal(["recent", "exactlyAYear"], await ActionsAsync(store));

        // No query brings them back: not an earlier From, a To before the cutoff, a tool filter or paging past the kept ones.
        Assert.Equal(["recent", "exactlyAYear"], await ActionsAsync(store, new ActivityQuery { From = now - TimeSpan.FromDays(1000) }));
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { To = now - year }));
        Assert.Empty(await ActionsAsync(store, new ActivityQuery { Actions = ["ancient", "aTickOlder"] }));
        var none = await store.GetAsync(new ActivityQuery { BeforeId = exactlyAYear.Id });
        Assert.Empty(none.Entries);
        Assert.False(none.HasMore);

        // They don't count as more, either.
        var first = await store.GetAsync(new ActivityQuery { Limit = 1 });
        Assert.Equal([recent], first.Entries);
        Assert.True(first.HasMore);
        var last = await store.GetAsync(new ActivityQuery { Limit = 1, BeforeId = recent.Id });
        Assert.Equal([exactlyAYear], last.Entries);
        Assert.False(last.HasMore);
        Assert.False((await store.GetAsync(new ActivityQuery { Limit = 2 })).HasMore);

        // The cutoff moves with the clock, though the next prune is still a day away.
        store.Time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(["recent"], await ActionsAsync(store));
        await store.SettleAsync();
        Assert.Equal(["recent", "aTickOlder", "exactlyAYear", "ancient"], await store.StoredActionsAsync());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task OldEntries_GoAtMostOnceADay_WhenSomethingIsAddedOrRead(string kind)
    {
        await using var store = await ActivityStore.CreateAsync(kind);
        var opened = store.Time.GetUtcNow();
        var aDayLater = opened + TimeSpan.FromDays(1);
        var year = ILibraryService.ActivityRetention;
        Assert.Equal(TimeSpan.FromDays(365), year);
        await AddAsync(store, "ancient", opened - TimeSpan.FromDays(400));
        await AddAsync(store, "exactlyAYear", aDayLater - year);
        await AddAsync(store, "aTickOlder", aDayLater - year - TimeSpan.FromTicks(1));
        await AddAsync(store, "recent", opened - TimeSpan.FromDays(300));
        await store.SettleAsync();

        // The log was pruned when it opened, less than a day ago: neither an append nor a read prunes it again yet.
        store.Time.Set(aDayLater - TimeSpan.FromTicks(1));
        await AddAsync(store, "almostADay");
        Assert.Equal(["almostADay", "recent", "aTickOlder", "exactlyAYear"], await ActionsAsync(store));
        await store.SettleAsync();
        Assert.Equal(["almostADay", "recent", "aTickOlder", "exactlyAYear", "ancient"], await store.StoredActionsAsync());

        // A day later an append removes what is older than a year.
        store.Time.Set(aDayLater);
        await AddAsync(store, "aDayLater");
        await store.SettleAsync();
        Assert.Equal(["aDayLater", "almostADay", "recent", "exactlyAYear"], await store.StoredActionsAsync());

        // Not again the same day, whatever is added or read...
        store.Time.Advance(TimeSpan.FromHours(23));
        await AddAsync(store, "lateArrival", opened - TimeSpan.FromDays(500));
        Assert.Equal(["aDayLater", "almostADay", "recent"], await ActionsAsync(store));
        await store.SettleAsync();
        Assert.Equal(["lateArrival", "aDayLater", "almostADay", "recent", "exactlyAYear"], await store.StoredActionsAsync());

        // ...but the next day a read does it, without an append.
        store.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(["aDayLater", "almostADay", "recent"], await ActionsAsync(store));
        await store.SettleAsync();
        Assert.Equal(["aDayLater", "almostADay", "recent"], await store.StoredActionsAsync());
    }

    [Fact]
    public async Task OldEntries_GoWhenTheLibraryOpens()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var now = t.Time.GetUtcNow();
        await AddAsync(t.Library, "old", at: now - TimeSpan.FromDays(365) - TimeSpan.FromTicks(1));
        await AddAsync(t.Library, "kept", at: now - TimeSpan.FromDays(364));
        await AddAsync(t.Library, "new", at: now);
        await t.Service.WhenActivityPrunedAsync();
        Assert.Equal(["new", "kept", "old"], StoredActions(t));

        await t.ReopenAsync();
        Assert.Equal(["new", "kept"], StoredActions(t));

        // Closed for two days: what turned a year old meanwhile goes when it opens again.
        await t.Library.DisposeAsync();
        t.Time.Advance(TimeSpan.FromDays(2));
        await t.OpenAsync();
        Assert.Equal(["new"], StoredActions(t));
        Assert.Equal(["new"], await ActionsAsync(t.Library));
    }

    [Fact]
    public async Task AFailingPrune_NeverFailsTheOpen_AndIsTriedAgainTheNextDay()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        await AddAsync(t.Library, "old", at: t.Time.GetUtcNow() - TimeSpan.FromDays(400));
        await AddAsync(t.Library, "new");
        await t.Library.DisposeAsync();
        using (var connection = OpenRaw(t))
        {
            connection.Execute("CREATE TRIGGER test_keep_activity BEFORE DELETE ON activity_log BEGIN SELECT RAISE(ABORT, 'kept by the test'); END;");
        }

        // The prune failed: the old entry is still there, though no read shows it.
        await t.OpenAsync();
        Assert.Equal(["new", "old"], StoredActions(t));
        Assert.Equal(["new"], await ActionsAsync(t.Library));
        await AddAsync(t.Library, "afterOpen");

        using (var connection = OpenRaw(t))
        {
            connection.Execute("DROP TRIGGER test_keep_activity");
        }

        t.Time.Advance(TimeSpan.FromDays(1));
        await AddAsync(t.Library, "nextDay");
        await t.Service.WhenActivityPrunedAsync();
        Assert.Equal(["nextDay", "afterOpen", "new"], StoredActions(t));
    }

    [Fact]
    public async Task TheLog_RaisesNoChangedEvent()
    {
        await using var t = await TestLibrary.CreateAsync();
        await AddAsync(t.Library, "old", at: t.Time.GetUtcNow() - TimeSpan.FromDays(400));
        await AddAsync(t.Library, "search");
        await t.Library.GetActivityAsync(new ActivityQuery());
        t.Time.Advance(TimeSpan.FromDays(1));
        await AddAsync(t.Library, "nextDay");
        await t.Service.WhenActivityPrunedAsync();

        Assert.Equal(["nextDay", "search"], await ActionsAsync(t.Library));
        Assert.Empty(t.Events);
    }

    // ================================================================ never waiting behind long library writes

    [Fact]
    public async Task AnAppend_DoesNotWaitForABackup_WhichKeepsItsSnapshot()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report"));
        await AddAsync(t.Library, "before");
        var pause = new Pause();
        var zip = Path.Combine(t.Root, "backup.zip");

        // The backup holds the write lock from its snapshot until the zip is written; it pauses at the first file.
        var backup = t.Library.CreateBackupAsync(zip, new PauseAtStage(pause, "zipping"));
        Task write;
        try
        {
            await pause.Reached.WaitAsync(Timeout);
            write = t.Library.SetCategoryPinnedAsync(docs.Id, true);

            // A library write waits for the backup; an append doesn't, and neither does reading the log.
            await AddAsync(t.Library, "during").WaitAsync(Timeout);
            Assert.Equal(["during", "before"], await ActionsAsync(t.Library).WaitAsync(Timeout));
            Assert.False(backup.IsCompleted);
            Assert.False(write.IsCompleted);
        }
        finally
        {
            pause.Release();
        }

        await backup;
        await write;
        Assert.True((await t.CategoryAsync(docs.Id)).IsPinned);

        // The backup holds what was logged before its snapshot.
        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));
        Assert.Equal(["before"], await ActionsAsync(target.Library));
    }

    [Fact]
    public async Task AnAppend_DoesNotWaitForMoveVault()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.pdf", "b"));
        await AddAsync(t.Library, "before");
        var pause = new Pause();
        t.Service.AfterMoveVaultFileCopied = _ => pause.HoldOnce();
        var oldVault = t.Library.VaultPath;

        // Move vault holds the write lock while it copies and verifies the whole vault; it pauses after the first file.
        var move = t.Library.MoveVaultAsync(Path.Combine(t.Root, "moved vault"));
        Task write;
        try
        {
            await pause.Reached.WaitAsync(Timeout);
            write = t.Library.SetCategoryPinnedAsync(docs.Id, true);
            await AddAsync(t.Library, "during").WaitAsync(Timeout);
            Assert.Equal(["during", "before"], await ActionsAsync(t.Library).WaitAsync(Timeout));
            Assert.False(move.IsCompleted);
            Assert.False(write.IsCompleted);
        }
        finally
        {
            pause.Release();
        }

        var result = await move;
        await write;
        Assert.Equal(2, result.FilesCopied);
        Assert.NotEqual(oldVault, t.Library.VaultPath);
        await AddAsync(t.Library, "after");
        Assert.Equal(["after", "during", "before"], await ActionsAsync(t.Library));
    }

    [Fact]
    public async Task AnAppend_DuringAMigrate_LeavesItCorrect()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var first = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"), ItemMode.Pointer);
        var second = await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"), ItemMode.Pointer);
        var pause = new Pause();
        t.Service.DuringMigrateCopy = _ => pause.HoldOnce();

        var migrate = t.Library.MigratePointersAsync();
        try
        {
            await pause.Reached.WaitAsync(Timeout);
            await AddAsync(t.Library, "during").WaitAsync(Timeout);
            Assert.False(migrate.IsCompleted);
        }
        finally
        {
            pause.Release();
        }

        var result = await migrate;
        Assert.Equal(2, result.Migrated);
        Assert.Empty(result.Failures);
        Assert.Equal(ItemMode.Copy, (await t.ItemAsync(first.Id)).Mode);
        Assert.Equal(ItemMode.Copy, (await t.ItemAsync(second.Id)).Mode);
        Assert.Equal(["during"], await ActionsAsync(t.Library));
    }

    [Fact]
    public async Task AnAppend_WaitsOutAShortWriteTransaction_InsteadOfFailing()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        Task<ActivityEntry> append;
        using (var connection = OpenRaw(t))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            // Another connection holds SQLite's write lock, as a library write in progress does: the busy timeout waits for it.
            append = AddAsync(t.Library, "waited");
            await Task.Delay(300);
            Assert.False(append.IsCompleted);
            tx.Commit();
        }

        Assert.Equal("waited", (await append.WaitAsync(Timeout)).Action);
    }

    [Fact]
    public async Task Dispose_WaitsForAppendsInFlight_ThenNothingHoldsTheDatabase()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var appends = Enumerable.Range(0, 50).Select(i => Task.Run(() => AddAsync(t.Library, "call" + i))).ToList();

        await t.Library.DisposeAsync();

        foreach (var append in appends)
        {
            try
            {
                await append;
            }
            catch (ObjectDisposedException)
            {
                // Started after the library closed.
            }
        }

        // A restore renames the database right after closing the library.
        var db = Path.Combine(t.DataDirectory, Database.FileName);
        File.Move(db, db + ".moved");
        File.Move(db + ".moved", db);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => AddAsync(t.Library, "late"));
    }

    // ================================================================ backups and older libraries

    [Fact]
    public async Task ABackup_HoldsTheLog_AndTheRestoredLibraryAnswersTheSameQueries()
    {
        await using var source = await TestLibrary.CreateAsync();
        var now = source.Time.GetUtcNow();
        var entries = new (string Source, string Action, DateTimeOffset At)[]
        {
            ("Claude Code", "search", now - TimeSpan.FromDays(30)),
            ("Codex", "add_files", (now - TimeSpan.FromDays(20)).ToOffset(TimeSpan.FromHours(-5))),
            ("Çursor", "delete_files", (now - TimeSpan.FromDays(10)).ToOffset(TimeSpan.FromHours(3))),
            ("Claude Code", "delete_files", now - TimeSpan.FromDays(5)),
            ("Codex", "read_file", now - TimeSpan.FromHours(1)),
        };
        var ids = new List<long>();
        foreach (var (client, tool, at) in entries)
        {
            var stored = await source.Library.AddActivityAsync(new ActivityEntry
            {
                At = at,
                Source = client,
                Action = tool,
                DetailsJson = $$"""{"tool":"{{tool}}"}""",
            });
            ids.Add(stored.Id);
        }

        var zip = Path.Combine(source.Root, "backup.zip");
        await source.Library.CreateBackupAsync(zip);
        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));

        ActivityQuery[] queries =
        [
            new(),
            new() { Source = "çursor" },
            new() { Actions = ["delete_files", "read_file"] },
            new() { From = now - TimeSpan.FromDays(15), To = now },
            new() { Limit = 2 },
            new() { Limit = 2, BeforeId = ids[2] },
        ];
        foreach (var query in queries)
        {
            var expected = await source.Library.GetActivityAsync(query);
            var actual = await target.Library.GetActivityAsync(query);
            Assert.NotEmpty(actual.Entries);
            Assert.Equal(expected.Entries, actual.Entries);
            Assert.Equal(expected.HasMore, actual.HasMore);
        }

        // New entries come after the restored ones.
        var next = await AddAsync(target.Library, "after");
        Assert.True(next.Id > ids.Max());
    }

    [Fact]
    public async Task ALibraryFromBeforeTheIndexes_GetsThemWhenItOpens()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        await AddAsync(t.Library, "search", "Codex");
        await t.Library.DisposeAsync();

        // A library made by the previous schema: version 1, the activity_log table without its indexes.
        using (var connection = OpenRaw(t))
        {
            DropActivityIndexes(connection);
            Assert.Empty(ActivityIndexNames(connection));
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
        }

        await t.OpenAsync();

        using (var connection = OpenRaw(t))
        {
            Assert.Equal(ActivityIndexes, ActivityIndexNames(connection));

            // No version bump: earlier builds still open the library (and its backups) and ignore the indexes.
            Assert.Equal(1, Database.SchemaVersion);
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
        }

        Assert.Equal(["search"], await ActionsAsync(t.Library, new ActivityQuery { Source = "codex", Actions = ["search"] }));
    }

    [Fact]
    public async Task ARestoredBackupFromBeforeTheIndexes_GetsThem()
    {
        await using var source = await TestLibrary.CreateAsync();
        await AddAsync(source.Library, "search", "Codex");

        // The live library loses its indexes, as if an earlier build had made it; its backup has none either.
        using (var connection = OpenRaw(source))
        {
            DropActivityIndexes(connection);
        }

        var zip = Path.Combine(source.Root, "old.zip");
        await source.Library.CreateBackupAsync(zip);
        var snapshot = Path.Combine(source.Root, "snapshot.db");
        using (var archive = ZipFile.OpenRead(zip))
        {
            archive.GetEntry(Database.FileName)!.ExtractToFile(snapshot);
        }

        using (var connection = OpenRaw(snapshot, readOnly: true))
        {
            Assert.Empty(ActivityIndexNames(connection));
        }

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));

        using (var connection = OpenRaw(target))
        {
            Assert.Equal(ActivityIndexes, ActivityIndexNames(connection));
        }

        Assert.Equal(["search"], await ActionsAsync(target.Library, new ActivityQuery { Source = "CODEX" }));
    }

    [Fact]
    public async Task ANewLibrary_NumbersTheLogWithAutoincrement()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var first = await AddAsync(t.Library, "search");

        using var connection = OpenRaw(t);
        Assert.Contains("AUTOINCREMENT", ActivityLogSql(connection));
        Assert.Equal(first.Id, ActivitySequence(connection));
        Assert.Equal(ActivityIndexes, ActivityIndexNames(connection));
        using var tx = connection.BeginTransaction();
        Assert.False(Database.RebuildActivityLogWithAutoincrement(connection, tx));
    }

    [Fact]
    public async Task ALibraryFromBeforeAutoincrement_IsRebuiltWhenItOpens_KeepingEveryRowAndId()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var now = t.Time.GetUtcNow();
        string[] newIndexSql;
        string[] newColumns;
        await t.Library.DisposeAsync();

        // A library made by the first stage 4 builds: the id without AUTOINCREMENT, and gaps where entries were pruned.
        ActivityRowData[] before;
        using (var connection = OpenRaw(t))
        {
            newIndexSql = ActivityIndexSql(connection);
            newColumns = ActivityColumns(connection);
            MakePreAutoincrementActivityLog(connection);
            InsertActivityRows(connection, now, 1, 2, 5, 9);
            before = ActivityRows(connection);
            Assert.Equal([1, 2, 5, 9], before.Select(r => r.Id));
        }

        await t.OpenAsync();

        using (var connection = OpenRaw(t))
        {
            Assert.Contains("AUTOINCREMENT", ActivityLogSql(connection));
            Assert.Equal(newColumns, ActivityColumns(connection));
            Assert.Equal(newIndexSql, ActivityIndexSql(connection));
            Assert.Equal(before, ActivityRows(connection));
            Assert.Equal(9, ActivitySequence(connection));
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_info"));
            Assert.Equal("ok", connection.ExecuteScalar<string>("PRAGMA integrity_check"));
        }

        Assert.Equal([9, 5, 2, 1], (await t.Library.GetActivityAsync(new ActivityQuery())).Entries.Select(e => e.Id));
        Assert.Equal([5], (await t.Library.GetActivityAsync(new ActivityQuery { Source = "codex", Actions = ["tool5"] }))
            .Entries.Select(e => e.Id));

        // Two years later the prune empties the log when it opens; the next id is still past every earlier one.
        await t.Library.DisposeAsync();
        t.Time.Advance(TimeSpan.FromDays(730));
        await t.OpenAsync();
        using (var connection = OpenRaw(t))
        {
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM activity_log"));
        }

        Assert.Equal(10, (await AddAsync(t.Library, "after")).Id);
    }

    [Fact]
    public async Task ARestoredBackupFromBeforeAutoincrement_IsRebuilt_KeepingEveryRowAndId()
    {
        await using var source = await TestLibrary.CreateAsync();
        var now = source.Time.GetUtcNow();

        // The live library gets the first stage 4 builds' table, as if one of them had made it; so does its backup.
        ActivityRowData[] before;
        using (var connection = OpenRaw(source))
        {
            MakePreAutoincrementActivityLog(connection);
            InsertActivityRows(connection, now, 3, 4, 7);
            before = ActivityRows(connection);
        }

        var zip = Path.Combine(source.Root, "old.zip");
        await source.Library.CreateBackupAsync(zip);
        var snapshot = Path.Combine(source.Root, "snapshot.db");
        using (var archive = ZipFile.OpenRead(zip))
        {
            archive.GetEntry(Database.FileName)!.ExtractToFile(snapshot);
        }

        using (var connection = OpenRaw(snapshot, readOnly: true))
        {
            Assert.DoesNotContain("AUTOINCREMENT", ActivityLogSql(connection));
            Assert.Equal(before, ActivityRows(connection));
        }

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "restored"), target.Options, target.RecycleBin));
        await target.Service.WhenActivityPrunedAsync();

        using (var connection = OpenRaw(target))
        {
            Assert.Contains("AUTOINCREMENT", ActivityLogSql(connection));
            Assert.Equal(ActivityIndexes, ActivityIndexNames(connection));
            Assert.Equal(before, ActivityRows(connection));
            Assert.Equal(7, ActivitySequence(connection));
        }

        Assert.Equal([7, 4, 3], (await target.Library.GetActivityAsync(new ActivityQuery())).Entries.Select(e => e.Id));

        // Once the prune has emptied it, ids still go on from the largest.
        await target.Library.DisposeAsync();
        target.Time.Advance(TimeSpan.FromDays(730));
        await target.OpenAsync();
        Assert.Equal(8, (await AddAsync(target.Library, "after")).Id);
    }

    [Fact]
    public async Task TheRebuild_IsIdempotent_AndNeedsNoSqliteSequenceBeforehand()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var now = t.Time.GetUtcNow();
        var folder = Path.Combine(t.Root, "old library");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Database.FileName);

        // A database made from scratch by the first stage 4 schema, so it never had a table with AUTOINCREMENT.
        var script = Database.LoadSchemaScript();
        Assert.Equal(1, script.Split("PRIMARY KEY AUTOINCREMENT").Length - 1);
        using (var connection = OpenRaw(path, create: true))
        {
            connection.Execute(script.Replace("PRIMARY KEY AUTOINCREMENT", "PRIMARY KEY"));
            connection.Execute("INSERT INTO schema_info(version) VALUES (1)");
            InsertActivityRows(connection, now, 4, 8);
            Assert.DoesNotContain("AUTOINCREMENT", ActivityLogSql(connection));
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'sqlite_sequence'"));
        }

        var db = new Database(folder);
        try
        {
            db.Initialize(now);
            SchemaState first;
            using (var connection = OpenRaw(path))
            {
                Assert.Contains("AUTOINCREMENT", ActivityLogSql(connection));
                Assert.Equal(ActivityIndexes, ActivityIndexNames(connection));
                Assert.Equal([4, 8], ActivityRows(connection).Select(r => r.Id));
                Assert.Equal(8, ActivitySequence(connection));
                first = SchemaState.Read(connection);

                // Run again directly: nothing to do.
                using var tx = connection.BeginTransaction();
                Assert.False(Database.RebuildActivityLogWithAutoincrement(connection, tx));
                tx.Commit();
                Assert.Equal(first, SchemaState.Read(connection));
            }

            // And through a second open.
            Assert.False(db.Initialize(now));
            using (var connection = OpenRaw(path))
            {
                Assert.Equal(first, SchemaState.Read(connection));
            }
        }
        finally
        {
            db.ClearPool();
        }
    }

    // ================================================================ helpers

    private static Task<ActivityEntry> AddAsync(ILibraryService library, string action, string source = "Claude Code", DateTimeOffset at = default)
        => library.AddActivityAsync(new ActivityEntry { At = at, Source = source, Action = action });

    private static Task<ActivityEntry> AddAsync(ActivityStore store, string action, DateTimeOffset at = default)
        => store.AddAsync(new ActivityEntry { At = at, Source = "Claude Code", Action = action });

    private static async Task<string[]> ActionsAsync(ILibraryService library, ActivityQuery? query = null)
        => (await library.GetActivityAsync(query ?? new ActivityQuery())).Entries.Select(e => e.Action).ToArray();

    private static async Task<string[]> ActionsAsync(ActivityStore store, ActivityQuery? query = null)
        => (await store.GetAsync(query ?? new ActivityQuery())).Entries.Select(e => e.Action).ToArray();

    private static async Task<string[]> SourcesAsync(ActivityStore store, string? source)
        => (await store.GetAsync(new ActivityQuery { Source = source })).Entries.Select(e => e.Source).ToArray();

    /// <summary>Every entry's tool in the table, newest first, including those past a year that no read returns.</summary>
    private static string[] StoredActions(TestLibrary t)
    {
        using var connection = OpenRaw(t);
        return connection.Query<string>("SELECT action FROM activity_log ORDER BY id DESC").ToArray();
    }

    private static SqliteConnection OpenRaw(TestLibrary t) => OpenRaw(Path.Combine(t.DataDirectory, Database.FileName));

    private static SqliteConnection OpenRaw(string path, bool readOnly = false, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void DropActivityIndexes(SqliteConnection connection)
        => connection.Execute("DROP INDEX ix_activity_log_at; DROP INDEX ix_activity_log_action; DROP INDEX ix_activity_log_source;");

    private static string[] ActivityIndexNames(SqliteConnection connection)
        => connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'activity_log' ORDER BY name").ToArray();

    private static string[] ActivityIndexSql(SqliteConnection connection)
        => connection.Query<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'activity_log' ORDER BY name").ToArray();

    private static string ActivityLogSql(SqliteConnection connection)
        => connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'activity_log'")!;

    /// <summary>Each column's name, type, NOT NULL and primary-key position.</summary>
    private static string[] ActivityColumns(SqliteConnection connection)
        => connection.Query<string>(
            """SELECT name || ' ' || type || ' ' || "notnull" || ' ' || pk || ' ' || hidden FROM pragma_table_xinfo('activity_log') ORDER BY cid""")
            .ToArray();

    /// <summary>The last id AUTOINCREMENT handed out (null: none yet).</summary>
    private static long? ActivitySequence(SqliteConnection connection)
        => connection.ExecuteScalar<long?>("SELECT seq FROM sqlite_sequence WHERE name = 'activity_log'");

    private static ActivityRowData[] ActivityRows(SqliteConnection connection)
        => connection.Query<ActivityRowData>("SELECT id, at, source, action, details_json FROM activity_log ORDER BY id").ToArray();

    /// <summary>Entries with these ids, a few hours apart and half a year old: client, tool and details differ with the id.</summary>
    private static void InsertActivityRows(SqliteConnection connection, DateTimeOffset now, params long[] ids)
    {
        foreach (var id in ids)
        {
            connection.Execute(
                "INSERT INTO activity_log(id, at, source, action, details_json) VALUES (@id, @at, @source, @action, @details)",
                new
                {
                    id,
                    at = DbValues.ToText(now - TimeSpan.FromDays(200) + TimeSpan.FromHours(id)),
                    source = id % 2 == 0 ? "Claude Code" : "Codex",
                    action = "tool" + id,
                    details = id % 2 == 0 ? null : $$"""{"id":{{id}}}""",
                });
        }
    }

    /// <summary>
    /// Gives activity_log the definition of the first stage 4 builds (the id without AUTOINCREMENT), keeping its rows and indexes.
    /// </summary>
    private static void MakePreAutoincrementActivityLog(SqliteConnection connection)
    {
        using (var tx = connection.BeginTransaction())
        {
            connection.Execute(
                """
                CREATE TABLE activity_log_old (
                  id           INTEGER PRIMARY KEY,
                  at           TEXT NOT NULL,
                  source       TEXT NOT NULL,
                  action       TEXT NOT NULL,
                  details_json TEXT
                );
                INSERT INTO activity_log_old SELECT id, at, source, action, details_json FROM activity_log;
                DROP TABLE activity_log;
                ALTER TABLE activity_log_old RENAME TO activity_log;
                CREATE INDEX ix_activity_log_at ON activity_log(at);
                CREATE INDEX ix_activity_log_action ON activity_log(action, id);
                CREATE INDEX ix_activity_log_source ON activity_log(source, id);
                """, transaction: tx);
            tx.Commit();
        }

        Assert.DoesNotContain("AUTOINCREMENT", ActivityLogSql(connection));
        Assert.Equal(ActivityIndexes, ActivityIndexNames(connection));
        Assert.Null(ActivitySequence(connection));
    }

    private sealed record ActivityRowData(long Id, string At, string Source, string Action, string? Details_Json);

    /// <summary>Everything a rebuild could change: the schema (without root pages), the log's rows and its sequence.</summary>
    private sealed record SchemaState(string Schema, string Rows, long? Sequence)
    {
        public static SchemaState Read(SqliteConnection connection) => new(
            string.Join("\n", connection.Query<string>(
                "SELECT type || '|' || name || '|' || tbl_name || '|' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name")),
            string.Join("\n", ActivityRows(connection).Select(r => r.ToString())),
            ActivitySequence(connection));
    }

    /// <summary>The activity log under test: the library's, or an <see cref="InMemoryActivityLog"/> on the same kind of clock.</summary>
    private abstract class ActivityStore : IAsyncDisposable
    {
        public abstract FakeTimeProvider Time { get; }

        public static async Task<ActivityStore> CreateAsync(string kind) => kind switch
        {
            LibraryKind => new LibraryStore(await TestLibrary.CreateAsync(initializeVault: false)),
            MemoryKind => new MemoryStore(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        public abstract Task<ActivityEntry> AddAsync(ActivityEntry entry);

        public abstract Task<ActivityPage> GetAsync(ActivityQuery query);

        /// <summary>Every entry's tool as stored, newest first: also those past a year that the prune hasn't removed yet.</summary>
        public abstract Task<string[]> StoredActionsAsync();

        /// <summary>Waits for a prune an append or a read started (the library prunes in the background).</summary>
        public abstract Task SettleAsync();

        public abstract ValueTask DisposeAsync();
    }

    private sealed class LibraryStore(TestLibrary t) : ActivityStore
    {
        public override FakeTimeProvider Time => t.Time;

        public override Task<ActivityEntry> AddAsync(ActivityEntry entry) => t.Library.AddActivityAsync(entry);

        public override Task<ActivityPage> GetAsync(ActivityQuery query) => t.Library.GetActivityAsync(query);

        public override Task<string[]> StoredActionsAsync() => Task.FromResult(StoredActions(t));

        public override Task SettleAsync() => t.Service.WhenActivityPrunedAsync();

        public override ValueTask DisposeAsync() => t.DisposeAsync();
    }

    private sealed class MemoryStore : ActivityStore
    {
        private readonly InMemoryActivityLog _log;

        public MemoryStore()
        {
            // The same start as TestLibrary's clock.
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 14, 9, 26, 53, TimeSpan.Zero));
            _log = new InMemoryActivityLog(Time);
        }

        public override FakeTimeProvider Time { get; }

        public override Task<ActivityEntry> AddAsync(ActivityEntry entry) => Task.FromResult(_log.Add(entry));

        public override Task<ActivityPage> GetAsync(ActivityQuery query) => Task.FromResult(_log.Get(query));

        public override Task<string[]> StoredActionsAsync() => Task.FromResult(_log.Stored.Reverse().Select(e => e.Action).ToArray());

        public override Task SettleAsync() => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Holds a long library operation at a point (a test hook or a progress report) until the test releases it.</summary>
    private sealed class Pause
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new();
        private int _held;

        public Task Reached => _reached.Task;

        /// <summary>The first call blocks the calling thread (the operation's) until <see cref="Release"/>; later calls go on.</summary>
        public void HoldOnce()
        {
            if (Interlocked.Exchange(ref _held, 1) == 0)
            {
                _reached.TrySetResult();
                _released.Wait(Timeout);
            }
        }

        public void Release() => _released.Set();
    }

    /// <summary>Pauses an operation at its first progress report of a stage (reports arrive synchronously, on its thread).</summary>
    private sealed class PauseAtStage(Pause pause, string stage) : IProgress<VaultOperationProgress>
    {
        public void Report(VaultOperationProgress value)
        {
            if (value.Stage == stage)
            {
                pause.HoldOnce();
            }
        }
    }
}
