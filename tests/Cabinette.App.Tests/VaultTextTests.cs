using System.Globalization;
using Cabinette.App.Services.Monitor;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

public sealed class VaultChangeTextTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public VaultChangeTextTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static Dictionary<string, int> Added(params (string Name, int Count)[] entries) => entries.ToDictionary(e => e.Name, e => e.Count);

    private static DuplicateInfo Duplicate() => new(@"C:\Vault\Images\a (1).jpg", new LibraryItem { Id = 1, FileName = "a.jpg", FullPath = @"C:\Vault\Images\a.jpg" }, false);

    [Fact]
    public void Files_added_to_one_category()
    {
        var message = VaultChangeText.Build(new VaultChangeSummary { AddedByCategory = Added(("Images", 3)) });

        Assert.NotNull(message);
        Assert.Equal(["3 files added to Images"], message.Lines);
        Assert.Equal("Images", message.CategoryName);
        Assert.Equal("Changes in your vault", message.Title);
    }

    [Fact]
    public void One_file_is_singular()
    {
        Assert.Equal(["1 file added to Audio"], VaultChangeText.Lines(new VaultChangeSummary { AddedByCategory = Added(("Audio", 1)) }));
    }

    [Fact]
    public void Files_added_to_two_categories_largest_first()
    {
        var message = VaultChangeText.Build(new VaultChangeSummary { AddedByCategory = Added(("Audio", 1), ("Images", 1)) })!;

        Assert.Equal(["2 files added to Audio and Images"], message.Lines);
        Assert.Null(message.CategoryName);
    }

    [Fact]
    public void Many_categories_are_summarized()
    {
        var lines = VaultChangeText.Lines(new VaultChangeSummary { AddedByCategory = Added(("Images", 5), ("Audio", 2), ("Video", 1), ("Text", 1)) });

        Assert.Equal(["9 files added to Images, Audio and 2 more"], lines);
    }

    [Fact]
    public void Removed_new_category_and_duplicates()
    {
        var summary = new VaultChangeSummary
        {
            AddedByCategory = Added(("Travel", 4)),
            CategoriesCreated = ["Travel"],
            Removed = 5,
            Duplicates = [Duplicate()],
        };

        var message = VaultChangeText.Build(summary)!;

        Assert.Equal(["4 files added to Travel", "New category: Travel", "5 files removed", "1 duplicate kept"], message.Lines);
        Assert.Equal("Travel", message.CategoryName);
        Assert.Equal("4 files added to Travel\nNew category: Travel\n5 files removed\n1 duplicate kept", message.Body);
        Assert.Equal("4 files added to Travel · New category: Travel · 5 files removed · 1 duplicate kept", message.OneLine);
    }

    [Fact]
    public void Renamed_moved_and_removed_categories()
    {
        var lines = VaultChangeText.Lines(new VaultChangeSummary
        {
            Renamed = 1,
            Moved = 2,
            CategoriesRemoved = ["Old stuff"],
            CategoriesCreated = ["A", "B", "C"],
            Duplicates = [Duplicate(), Duplicate()],
        });

        Assert.Equal(["3 new categories", "2 files moved to another category", "1 file renamed", "Category removed: Old stuff", "2 duplicates kept"], lines);
    }

    [Fact]
    public void Edits_of_existing_files_alone_are_not_announced()
    {
        Assert.Null(VaultChangeText.Build(new VaultChangeSummary { Modified = 3 }));
        Assert.Null(VaultChangeText.Build(new VaultChangeSummary()));

        var mixed = VaultChangeText.Build(new VaultChangeSummary { Modified = 2, Removed = 1 })!;
        Assert.Equal(["1 file removed", "2 files updated"], mixed.Lines);
    }

    [Fact]
    public void Batches_merge()
    {
        var merged = VaultChangeText.Merge(
            new VaultChangeSummary { AddedByCategory = Added(("Images", 2)), Removed = 1, CategoriesCreated = ["Travel"] },
            new VaultChangeSummary { AddedByCategory = Added(("Images", 1), ("Audio", 1)), Removed = 2, CategoriesCreated = ["Travel", "Work"], Duplicates = [Duplicate()] });

        Assert.Equal(3, merged.AddedByCategory["Images"]);
        Assert.Equal(1, merged.AddedByCategory["Audio"]);
        Assert.Equal(3, merged.Removed);
        Assert.Equal(["Travel", "Work"], merged.CategoriesCreated);
        Assert.Single(merged.Duplicates);
        Assert.Equal("4 files added to Images and Audio", VaultChangeText.Lines(merged)[0]);
    }

    [Fact]
    public void Notifications_wait_for_a_quiet_moment_but_at_most_five_seconds()
    {
        var start = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(start.AddSeconds(1.5), VaultChangeText.FlushAt(start, start));
        Assert.Equal(start.AddSeconds(4.5), VaultChangeText.FlushAt(start, start.AddSeconds(3)));
        Assert.Equal(start.AddSeconds(5), VaultChangeText.FlushAt(start, start.AddSeconds(4)));
    }
}

public class VaultStatusTextTests
{
    private static VaultMonitorStatus Status(VaultMonitorState state, int pending = 0) => new(state, pending, null, null);

    [Fact]
    public void Tooltips()
    {
        Assert.Equal("Cabinette · up to date", VaultStatusText.Tooltip(Status(VaultMonitorState.Idle), true));
        Assert.Equal("Cabinette · syncing 12 changes…", VaultStatusText.Tooltip(Status(VaultMonitorState.Syncing, 12), true));
        Assert.Equal("Cabinette · syncing 1 change…", VaultStatusText.Tooltip(Status(VaultMonitorState.Syncing, 1), true));
        Assert.Equal("Cabinette · monitoring paused", VaultStatusText.Tooltip(Status(VaultMonitorState.Paused), true));
        Assert.Equal("Cabinette · vault not found", VaultStatusText.Tooltip(Status(VaultMonitorState.VaultUnavailable), true));
        Assert.Equal("Cabinette · not watching the vault", VaultStatusText.Tooltip(null, false));
    }

    [Fact]
    public void Indicators()
    {
        Assert.Equal(MonitorIndicator.Idle, VaultStatusText.Indicator(Status(VaultMonitorState.Idle), true));
        Assert.Equal(MonitorIndicator.Syncing, VaultStatusText.Indicator(Status(VaultMonitorState.Syncing), true));
        Assert.Equal(MonitorIndicator.Syncing, VaultStatusText.Indicator(Status(VaultMonitorState.Stopped), true));
        Assert.Equal(MonitorIndicator.Paused, VaultStatusText.Indicator(Status(VaultMonitorState.Paused), true));
        Assert.Equal(MonitorIndicator.Error, VaultStatusText.Indicator(Status(VaultMonitorState.VaultUnavailable), true));
        Assert.Equal(MonitorIndicator.Off, VaultStatusText.Indicator(Status(VaultMonitorState.Idle), false));
    }

    [Fact]
    public void Settings_line_shows_the_last_sync_time()
    {
        var now = DateTimeOffset.Now;
        var status = new VaultMonitorStatus(VaultMonitorState.Idle, 0, now.AddMinutes(-1), null);

        Assert.StartsWith("Watching · last sync ", VaultStatusText.Line(status, true, now));
        Assert.Equal("Watching for changes", VaultStatusText.Line(Status(VaultMonitorState.Idle), true, now));
        Assert.Equal("Vault not found", VaultStatusText.Line(Status(VaultMonitorState.VaultUnavailable), true, now));
    }

    [Fact]
    public void Tooltips_fit_the_notification_area_limit()
    {
        Assert.True(VaultStatusText.Tooltip(Status(VaultMonitorState.Syncing, int.MaxValue), true).Length <= VaultStatusText.MaxTooltipLength);
    }
}
