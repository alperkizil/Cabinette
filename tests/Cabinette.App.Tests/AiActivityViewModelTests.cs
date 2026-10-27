using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Services.Ai;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Shell;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>The activity log viewer in Settings › AI: filters, paging, live entries and links, with fakes.</summary>
public sealed class AiActivityViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 15, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void No_filter_reads_everything_newest_first() => Ui(async t =>
    {
        var query = t.Vm.BuildQuery(Now);

        Assert.Null(query.From);
        Assert.Null(query.To);
        Assert.Null(query.Actions);
        Assert.Null(query.Source);
        Assert.Null(query.BeforeId);
        Assert.Equal(AiActivityViewModel.PageSize, query.Limit);
        Assert.False(t.Vm.IsFiltered);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(DateRangeOption.Today, "2026-10-24T00:00:00+03:00")]
    [InlineData(DateRangeOption.Week, "2026-10-17T15:30:00+03:00")]
    [InlineData(DateRangeOption.Month, "2026-09-24T15:30:00+03:00")]
    [InlineData(DateRangeOption.Year, "2025-10-24T15:30:00+03:00")]
    public void Date_presets_are_the_main_filters(DateRangeOption range, string expected) => Ui(async t =>
    {
        Assert.True(t.Vm.DateFilter.Select(range));

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), t.Vm.BuildQuery(Now).From);
        Assert.True(t.Vm.IsFiltered);

        // The same start as the main search filter's preset.
        var now = DateTimeOffset.Now;
        var main = new SearchFilterViewModel { DateRange = range }.ToItemFilter().AddedFrom!.Value;
        Assert.True((main - AiActivityViewModel.DateFrom(range, now)!.Value).Duration() < TimeSpan.FromSeconds(5));
        await Task.CompletedTask;
    });

    [Fact]
    public void Tool_and_client_filters_map_to_the_query() => Ui(async t =>
    {
        t.Log.Add(Now.AddHours(-1), "Codex", AiTools.Search);
        await t.Vm.LoadAsync();

        t.Vm.ToolFilter.Select(AiActivityViewModel.ChangesOnly);
        Assert.Equal(AiTools.Changes, t.Vm.BuildQuery(Now).Actions);

        t.Vm.ToolFilter.Select(AiTools.DeleteFiles);
        Assert.Equal([AiTools.DeleteFiles], t.Vm.BuildQuery(Now).Actions);

        Assert.True(t.Vm.ClientFilter.Select("Codex"));
        Assert.Equal("Codex", t.Vm.BuildQuery(Now).Source);

        t.Vm.ClientFilter.Select(AiActivityViewModel.AllClients);
        t.Vm.ToolFilter.Select(AiActivityViewModel.AllTools);
        Assert.Null(t.Vm.BuildQuery(Now).Source);
        Assert.Null(t.Vm.BuildQuery(Now).Actions);
    });

    [Fact]
    public void The_tool_filter_offers_every_tool_by_its_friendly_name() => Ui(async t =>
    {
        var options = t.Vm.ToolFilter.Options;

        Assert.Equal(["All tools", "Changes only"], options.Take(2).Select(o => o.Label));
        Assert.Equal(AiTools.All.Order(), options.Where(o => !o.IsHeader && o.Value is string v && AiTools.All.Contains(v)).Select(o => (string)o.Value!).Order());
        Assert.Contains(options, o => o.Label == "Delete files" && (string?)o.Value == AiTools.DeleteFiles);
        Assert.Equal(3, options.Count(o => o.IsHeader));

        // A heading can't be chosen.
        var heading = options.First(o => o.IsHeader);
        t.Vm.ToolFilter.ChooseCommand.Execute(heading);
        Assert.Equal("All tools", t.Vm.ToolFilter.Selected.Label);
        Assert.True(t.Vm.ToolFilter.Selected.IsSelected);
        await Task.CompletedTask;
    });

    [Fact]
    public void Changing_a_filter_reloads_with_it() => Ui(async t =>
    {
        t.Log.Add(Now.AddHours(-2), "Claude Code", AiTools.Search);
        t.Log.Add(Now.AddHours(-1), "Claude Code", AiTools.DeleteFiles, new() { Files = [new(1, "a.pdf")], FileCount = 1 });
        await t.Vm.LoadAsync();
        Assert.Equal(2, t.Vm.Entries.Count);

        t.Vm.ToolFilter.IsOpen = true;
        t.Vm.ToolFilter.ChooseCommand.Execute(t.Vm.ToolFilter.Options.First(o => (string?)o.Value == AiActivityViewModel.ChangesOnly));
        Assert.False(t.Vm.ToolFilter.IsOpen);
        await t.UntilAsync(() => !t.Vm.IsLoading);

        Assert.Equal([AiTools.DeleteFiles], t.Vm.Entries.Select(e => e.Tool));
        Assert.Equal(AiTools.Changes, t.Log.Queries[^1].Actions);
    });

    [Fact]
    public void Pages_of_a_hundred_until_no_more() => Ui(async t =>
    {
        for (var i = 0; i < 250; i++)
        {
            t.Log.Add(Now.AddMinutes(-250 + i), i % 2 == 0 ? "Claude Code" : "Codex", AiTools.Search, new() { Query = $"q{i}", FileCount = i });
        }

        await t.Vm.LoadAsync();
        Assert.Equal(100, t.Vm.Entries.Count);
        Assert.True(t.Vm.HasMore);
        Assert.True(t.Vm.ShowOlderCommand.CanExecute(null));

        var oldestShown = t.Vm.Entries[^1].Id;
        await t.Vm.ShowOlderCommand.ExecuteAsync(null);
        Assert.Equal(200, t.Vm.Entries.Count);
        Assert.Equal(oldestShown, t.Log.Queries[^1].BeforeId);

        await t.Vm.ShowOlderCommand.ExecuteAsync(null);
        Assert.Equal(250, t.Vm.Entries.Count);
        Assert.False(t.Vm.HasMore);
        Assert.False(t.Vm.ShowOlderCommand.CanExecute(null));

        var ids = t.Vm.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.OrderByDescending(id => id), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(["All clients", "Claude Code", "Codex"], t.Vm.ClientFilter.Options.Select(o => o.Label));
    });

    [Fact]
    public void Rows_are_worded_and_grouped_by_day() => Ui(async t =>
    {
        t.Log.Add(Now.AddDays(-1), "Codex", AiTools.ReadFile, new() { Files = [new(5, "report.pdf")], FileCount = 1 });
        t.Log.Add(Now.AddHours(-2), "Claude Code", AiTools.Search, new() { Query = "invoice", FileCount = 12 });
        t.Log.Add(Now.AddHours(-1), "Claude Code", AiTools.DeleteFiles, new() { Ok = false, Error = "Busy.", Files = [new(1, "a.pdf")] });
        await t.Vm.LoadAsync();

        Assert.Equal(["Delete files: a.pdf", "Searched for “invoice” (12 results)", "Read report.pdf"], t.Vm.Entries.Select(e => e.Sentence));
        Assert.Equal([true, false, true], t.Vm.Entries.Select(e => e.ShowDayHeader));
        Assert.Equal("Today", t.Vm.Entries[0].DayHeader);
        Assert.Equal("Yesterday", t.Vm.Entries[2].DayHeader);
        Assert.True(t.Vm.Entries[0].IsFailed);
        Assert.Equal("Failed: Busy.", t.Vm.Entries[0].FailureText);
        Assert.Equal("Codex", t.Vm.Entries[2].Client);
    });

    [Fact]
    public void New_entries_appear_at_the_top_when_they_match() => Ui(async t =>
    {
        t.Log.Add(Now.AddHours(-1), "Claude Code", AiTools.Search);
        await t.Vm.LoadAsync();
        var firstTop = t.Vm.Entries[0];

        var added = t.Log.AddLive(Now, "Claude Code", AiTools.AddFiles, new() { Files = [new(1, "a.pdf")], FileCount = 1 });
        await t.UntilAsync(() => t.Vm.Entries.Count == 2);

        Assert.Equal(added.Id, t.Vm.Entries[0].Id);
        Assert.True(t.Vm.Entries[0].ShowDayHeader);
        Assert.False(firstTop.ShowDayHeader); // Same day: the heading moved to the new top row.

        // Another client's entry while filtered to Claude Code: not shown, but the client is offered.
        t.Vm.ClientFilter.Select("Claude Code");
        await t.UntilAsync(() => !t.Vm.IsLoading);
        t.Log.AddLive(Now.AddMinutes(1), "Codex", AiTools.Search);
        await t.UntilAsync(() => t.Vm.ClientFilter.Options.Any(o => o.Label == "Codex"));
        Assert.Equal(2, t.Vm.Entries.Count);
    });

    [Fact]
    public void An_entry_logged_while_the_page_is_read_shows_once() => Ui(async t =>
    {
        t.Log.Add(Now.AddHours(-1), "Claude Code", AiTools.Search);
        t.Log.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = t.Vm.LoadAsync();
        await t.UntilAsync(() => t.Log.Queries.Count >= 2);

        // Stored before the page is read (so the page has it too) and announced while it is read.
        var live = t.Log.AddLive(Now, "Claude Code", AiTools.ReadFile);
        await Task.Delay(100); // The announcement reaches the view model while it is still loading.
        Assert.True(t.Vm.IsLoading);
        t.Log.Gate.SetResult();
        await loading;

        Assert.Equal([live.Id, live.Id - 1], t.Vm.Entries.Select(e => e.Id));
    });

    [Fact]
    public void Opening_a_file_goes_to_its_thread_and_closes_settings() => Ui(async t =>
    {
        var item = (await t.Library.SearchAsync("Tax return")).Items.First();
        var segment = new AiActivitySegment(item.FileName, AiActivityLinkKind.File, item.Id);

        await t.Vm.OpenLinkCommand.ExecuteAsync(segment);

        Assert.Equal([(item.HomeCategoryId, (long?)item.Id)], t.Navigator.Opened);
        Assert.Equal(1, t.Closed);
        Assert.Null(t.Vm.Message);
    });

    [Fact]
    public void A_file_that_is_gone_says_so_and_stays() => Ui(async t =>
    {
        await t.Vm.OpenLinkCommand.ExecuteAsync(new AiActivitySegment("Draft 1.docx", AiActivityLinkKind.File, 987654));

        Assert.Equal("“Draft 1.docx” is no longer in Cabinette.", t.Vm.Message);
        Assert.Empty(t.Navigator.Opened);
        Assert.Equal(0, t.Closed);
    });

    [Fact]
    public void Categories_open_too_and_a_deleted_one_says_so() => Ui(async t =>
    {
        var work = (await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical)).First(c => c.Category.Name == "Work").Category;

        await t.Vm.OpenLinkCommand.ExecuteAsync(new AiActivitySegment("Old", AiActivityLinkKind.Category, 987654));
        Assert.Equal("The category “Old” no longer exists.", t.Vm.Message);

        await t.Vm.OpenLinkCommand.ExecuteAsync(new AiActivitySegment("Work", AiActivityLinkKind.Category, work.Id));
        Assert.Equal([(work.Id, (long?)null)], t.Navigator.Opened);
        Assert.Equal(1, t.Closed);
        Assert.Null(t.Vm.Message);
    });

    [Fact]
    public void Empty_states() => Ui(async t =>
    {
        Assert.False(t.Vm.IsEmpty); // Not loaded yet.

        await t.Vm.LoadAsync();
        Assert.True(t.Vm.IsEmpty);
        Assert.Equal("No AI activity yet", t.Vm.EmptyTitle);

        t.Vm.DateFilter.Select(DateRangeOption.Today);
        await t.UntilAsync(() => !t.Vm.IsLoading);
        Assert.True(t.Vm.IsEmpty);
        Assert.Equal("Nothing matches these filters", t.Vm.EmptyTitle);
    });

    [Fact]
    public void A_log_that_cant_be_read_says_so() => Ui(async t =>
    {
        t.Log.Fails = true;

        await t.Vm.LoadAsync();

        Assert.Equal("The activity log couldn't be read: The library is closed.", t.Vm.Message);
        Assert.Empty(t.Vm.Entries);
        Assert.False(t.Vm.IsLoading);
    });

    [Fact]
    public void After_dispose_new_entries_are_ignored() => Ui(async t =>
    {
        await t.Vm.LoadAsync();
        t.Vm.Dispose();

        t.Log.AddLive(Now, "Claude Code", AiTools.Search);
        await Task.Delay(100);

        Assert.Empty(t.Vm.Entries);
    });

    [Fact]
    public void The_sample_library_has_a_dozen_entries_for_screenshots() => Ui(async t =>
    {
        var log = new AiActivityLog(t.Library);
        using var vm = new AiActivityViewModel(log, t.Library, t.Navigator, () => { }, Dispatcher.CurrentDispatcher, TimeProvider.System);

        await vm.LoadAsync();

        Assert.InRange(vm.Entries.Count, 12, 20);
        Assert.Equal(["All clients", "Claude Code", "Codex"], vm.ClientFilter.Options.Select(o => o.Label));
        Assert.Contains(vm.Entries, e => e.IsFailed);
        Assert.Contains(vm.Entries, e => e.Tool == AiTools.Search);
        Assert.Contains(vm.Entries, e => e.Tool == AiTools.ReadFile);
        Assert.Contains(vm.Entries, e => e.Tool == AiTools.AddFiles);
        Assert.Contains(vm.Entries, e => e.Tool == AiTools.DeleteFiles && !e.IsFailed);
        Assert.DoesNotContain(vm.Entries, e => e.Sentence.Contains('[', StringComparison.Ordinal));
    });

    // ------------------------------------------------------------------ harness

    private static void Ui(Func<Harness, Task> test) => AiConnectCommandsTests.RunUi(async () =>
    {
        using var harness = new Harness();
        await test(harness);
    });

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Vm = new AiActivityViewModel(Log, Library, Navigator, () => Closed++, Dispatcher, new FixedClock(Now));
        }

        public StoredActivityLog Log { get; } = new();

        public SampleLibraryService Library { get; } = SampleLibraryService.Create(withVault: true);

        public FakeNavigator Navigator { get; } = new();

        public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;

        public AiActivityViewModel Vm { get; }

        public int Closed { get; private set; }

        /// <summary>Lets the dispatcher run (live entries, reloads) until the condition holds.</summary>
        public async Task UntilAsync(Func<bool> done)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!done())
            {
                if (DateTime.UtcNow > timeout)
                {
                    throw new TimeoutException("The condition never held.");
                }

                await Task.Delay(10);
            }
        }

        public void Dispose() => Vm.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test+3", now.Offset, "Test+3", "Test+3");
    }
}
