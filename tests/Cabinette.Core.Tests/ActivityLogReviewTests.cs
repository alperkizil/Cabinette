using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// Review of the AI activity log (stage 4, SPEC.md section 10). The F-tests pinned the findings of the review; both are fixed,
/// and the tests now assert the fixed behavior as regression tests. The P-tests are the probes that held up: they pass because
/// the code is right, and stay as regression tests.
/// </summary>
public sealed class ActivityLogReviewTests
{
    // ================================================================ findings

    /// <summary>
    /// F1 (fixed). ActivityEntry.Id says "ids grow with time" and InMemoryActivityLog never hands out an id twice. activity_log
    /// was an INTEGER PRIMARY KEY without AUTOINCREMENT, so once the prune emptied the table (no AI call for a year, then the
    /// library opens) the next entry got id 1 again, and a reader that remembers the last id it saw (the viewer's "newer than"
    /// check, a BeforeId cursor kept across sessions) took new entries for old ones. The id is AUTOINCREMENT now; older tables are
    /// rebuilt with it when the library opens (ActivityLogTests, "backups and older libraries").
    /// </summary>
    [Fact]
    public async Task F1_IdsKeepGrowing_AfterThePruneEmptiesTheLog()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var old = await AddAsync(t.Library, "old", at: t.Time.GetUtcNow() - TimeSpan.FromDays(400));
        var older = await AddAsync(t.Library, "older", at: t.Time.GetUtcNow() - TimeSpan.FromDays(401));
        Assert.Equal(1, old.Id);
        Assert.Equal(2, older.Id);

        // Opening prunes both; the table is empty.
        await t.ReopenAsync();
        using (var connection = OpenRaw(t))
        {
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM activity_log"));
        }

        var fresh = await AddAsync(t.Library, "fresh");
        Assert.Equal(3, fresh.Id);
        Assert.True(fresh.Id > older.Id);

        // The in-memory log keeps counting.
        var memory = new InMemoryActivityLog(t.Time);
        var m1 = memory.Add(new ActivityEntry { At = t.Time.GetUtcNow() - TimeSpan.FromDays(400), Source = "Codex", Action = "old" });
        t.Time.Advance(TimeSpan.FromDays(1));
        var m2 = memory.Add(new ActivityEntry { Source = "Codex", Action = "fresh" });
        Assert.Equal([m2], memory.Get(new ActivityQuery()).Entries);
        Assert.True(m2.Id > m1.Id);
    }

    /// <summary>
    /// F2 (fixed). SPEC.md: "Entries older than a year are removed when the library opens and once a day." The daily prune ran
    /// only from an append, so a Cabinette that stays in the tray without AI calls kept showing entries past a year old until
    /// the next call or restart. A read now starts the due prune too, and no read returns an entry past the retention cutoff,
    /// whether the prune has removed it yet or not. InMemoryActivityLog follows the same rules.
    /// </summary>
    [Fact]
    public async Task F2_AReadRunsTheDailyPrune_AndNeverShowsEntriesPastAYear()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var now = t.Time.GetUtcNow();
        await AddAsync(t.Library, "expired", at: now - TimeSpan.FromDays(400));
        await AddAsync(t.Library, "aging", at: now - TimeSpan.FromDays(364));
        await t.Service.WhenActivityPrunedAsync();

        // The library pruned when it opened, before "expired" was added: it stays until the next prune, but no read shows it.
        Assert.Equal(["aging", "expired"], StoredActions(t));
        Assert.Equal(["aging"], await ActionsAsync(t.Library));

        // Three days pass with the library open and the viewer reading it, but no AI call: "aging" is past a year now...
        t.Time.Advance(TimeSpan.FromDays(3));
        var pruneBefore = t.Service.WhenActivityPrunedAsync();
        Assert.Empty(await ActionsAsync(t.Library));

        // ...and the read started the daily prune, which removes both.
        var prune = t.Service.WhenActivityPrunedAsync();
        Assert.NotSame(pruneBefore, prune);
        await prune;
        Assert.Empty(StoredActions(t));

        // Not again the same day.
        await ActionsAsync(t.Library);
        Assert.Same(prune, t.Service.WhenActivityPrunedAsync());

        // The in-memory log likewise.
        var memory = new InMemoryActivityLog(t.Time);
        memory.Add(new ActivityEntry { At = t.Time.GetUtcNow() - TimeSpan.FromDays(400), Source = "Codex", Action = "expired" });
        memory.Add(new ActivityEntry { At = t.Time.GetUtcNow() - TimeSpan.FromDays(364), Source = "Codex", Action = "aging" });
        Assert.Equal(["aging"], memory.Get(new ActivityQuery()).Entries.Select(e => e.Action));
        Assert.Equal(2, memory.Stored.Count);
        t.Time.Advance(TimeSpan.FromDays(3));
        Assert.Empty(memory.Get(new ActivityQuery()).Entries);
        Assert.Empty(memory.Stored);
    }

    // ================================================================ probes that held up (regression tests)

    /// <summary>
    /// P1. A restore renames the database right after the library is disposed. Disposing in the middle of a multi-batch prune
    /// must wait for the batch in flight and stop the rest, so nothing holds the file.
    /// </summary>
    [Fact]
    public async Task P1_DisposeDuringAMultiBatchPrune_LeavesNothingHoldingTheDatabase()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var now = t.Time.GetUtcNow();
        await t.Library.DisposeAsync();
        using (var connection = OpenRaw(t))
        using (var tx = connection.BeginTransaction())
        {
            for (var i = 0; i < 5 * LibraryService.ActivityPruneBatchSize + 1; i++)
            {
                connection.Execute("INSERT INTO activity_log(at, source, action) VALUES (@at, 'Codex', 'search')",
                    new { at = DbValues.ToText(now - TimeSpan.FromDays(400)) }, tx);
            }

            tx.Commit();
        }

        // Open (the prune starts in the background, six batches) and dispose at once.
        var library = await LibraryFactory.OpenAsync(t.Options, t.RecycleBin);
        await library.DisposeAsync();

        var db = Path.Combine(t.DataDirectory, Database.FileName);
        File.Move(db, db + ".moved");
        File.Move(db + ".moved", db);

        // Whatever batches ran, the rest is intact and a later open finishes the job.
        using (var connection = OpenRaw(t))
        {
            var left = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM activity_log");
            Assert.Equal(1, left % LibraryService.ActivityPruneBatchSize);
        }

        await t.OpenAsync();
        Assert.Empty((await t.Library.GetActivityAsync(new ActivityQuery())).Entries);
    }

    /// <summary>P2. A clock that steps back (NTP) triggers one extra prune per step, not one per append after it.</summary>
    [Fact]
    public async Task P2_AClockStep_RunsOneExtraPrune_ThenWaitsADayAgain()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var opened = t.Service.WhenActivityPrunedAsync();

        t.Time.Advance(-TimeSpan.FromTicks(1));
        await AddAsync(t.Library, "a");
        var afterStep = t.Service.WhenActivityPrunedAsync();
        Assert.NotSame(opened, afterStep);

        t.Time.Advance(TimeSpan.FromHours(1));
        await AddAsync(t.Library, "b");
        await AddAsync(t.Library, "c");
        Assert.Same(afterStep, t.Service.WhenActivityPrunedAsync());
    }

    // ================================================================ helpers

    private static Task<ActivityEntry> AddAsync(ILibraryService library, string action, string source = "Claude Code", DateTimeOffset at = default)
        => library.AddActivityAsync(new ActivityEntry { At = at, Source = source, Action = action });

    private static async Task<string[]> ActionsAsync(ILibraryService library, ActivityQuery? query = null)
        => (await library.GetActivityAsync(query ?? new ActivityQuery())).Entries.Select(e => e.Action).ToArray();

    private static string[] StoredActions(TestLibrary t)
    {
        using var connection = OpenRaw(t);
        return connection.Query<string>("SELECT action FROM activity_log ORDER BY id DESC").ToArray();
    }

    private static SqliteConnection OpenRaw(TestLibrary t)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(t.DataDirectory, Database.FileName),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
