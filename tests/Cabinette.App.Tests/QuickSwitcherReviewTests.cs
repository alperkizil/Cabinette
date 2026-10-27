using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Dialogs;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using Microsoft.Data.Sqlite;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests from the bug hunt of the Ctrl+K quick switcher branch (e7132df, repro tests in 87d53cf). The reviewer's
/// repro tests asserted the reviewed behavior; the first tests of findings 1 and 2 are those, turned around to assert the
/// fix. The other tests were added with the fixes.
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class QuickSwitcherReviewTests
{
    // ------------------------------------------------------------------ finding: FocusItem vs the thread's own query debounce

    /// <summary>
    /// Enter on a file in the quick switcher (or the main search) while the open thread has a search or filter:
    /// ThreadViewModel.FocusItem clears the query and reloads at once, scrolls to the file and flashes it. Reviewed code:
    /// clearing the query also restarted the thread's 250 ms query timer, whose reload is a full reset ending with
    /// host.ScrollToEnd(), so about a quarter of a second after the thread scrolled to the file it jumped to the bottom
    /// again. Fixed: FocusItem stops that timer, and its own reload scrolls to the file instead of to the end.
    /// </summary>
    [Fact]
    public void Focus_on_a_file_hidden_by_the_thread_search_is_not_undone_by_the_debounced_reload() => RunSta(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Documents");
        var taxReturn = (await library.GetThreadItemsAsync(documents.Id)).Single(i => i.FileName == "Tax return 2025.pdf");
        using var thread = new ThreadViewModel(Services(library));
        var host = new LoggingHost();
        thread.AttachHost(host);
        await thread.LoadAsync(documents.Id);
        thread.IsSearchOpen = true;
        thread.SearchText = "minutes";
        await WaitForAsync(() => thread.TotalCount == 1);
        host.Log.Clear();

        thread.FocusItem(taxReturn.Id);
        await WaitForAsync(() => host.Log.Contains("bring:" + taxReturn.Id));
        var brought = host.Log.IndexOf("bring:" + taxReturn.Id);
        await Task.Delay(TimeSpan.FromSeconds(1)); // the query timer (250 ms) and its reload
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("", thread.SearchText);
        Assert.True(thread.TotalCount > 1, "the search was cleared");
        var after = host.Log.Skip(brought + 1).ToList();

        // Reviewed code: the timer's reset reload scrolled to the end after the file was brought into view.
        Assert.DoesNotContain("scroll-end", after);
        Assert.Equal(["bring:" + taxReturn.Id], host.Log); // and not to the end before it either: one scroll, to the file
        Assert.True(thread.Items.Single(i => i.Id == taxReturn.Id).IsHighlighted);
    });

    /// <summary>
    /// The same scenario when the item load takes longer than the query debounce (a big thread, a busy library). Reviewed
    /// code: the timer's reload cancelled FocusItem's reload, FocusItem's continuation found the file still missing, cleared
    /// _pendingFocusId and gave up, so the search was cleared but the thread never scrolled to the file nor flashed it.
    /// Fixed: the file is brought into view once the reload that clears the query has applied, however long it takes.
    /// </summary>
    [Fact]
    public void Focus_on_a_file_hidden_by_the_thread_search_waits_for_a_slow_item_load() => RunSta(async () =>
    {
        var library = SlowLibrary.Wrap(SampleLibraryService.Create(withVault: true), TimeSpan.FromMilliseconds(400));
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Documents");
        var taxReturn = (await library.GetThreadItemsAsync(documents.Id)).Single(i => i.FileName == "Tax return 2025.pdf");
        using var thread = new ThreadViewModel(Services(library));
        var host = new LoggingHost();
        thread.AttachHost(host);
        await thread.LoadAsync(documents.Id);
        thread.IsSearchOpen = true;
        thread.SearchText = "minutes";
        await WaitForAsync(() => thread.TotalCount == 1);
        host.Log.Clear();

        thread.FocusItem(taxReturn.Id);
        await WaitForAsync(() => thread.TotalCount > 1 && !thread.IsLoading); // the query was cleared and reloaded
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("", thread.SearchText);
        // Reviewed code: nothing was ever brought into view.
        Assert.Equal(["bring:" + taxReturn.Id], host.Log);
    });

    /// <summary>
    /// Two files picked one after the other while the reload that clears the search is still on its way (the second one is
    /// hidden by the old, still shown, search results too): the thread goes to the latest one, once.
    /// </summary>
    [Fact]
    public void Focus_requests_made_while_that_reload_is_on_its_way_go_to_the_latest() => RunSta(async () =>
    {
        var library = SlowLibrary.Wrap(SampleLibraryService.Create(withVault: true), TimeSpan.FromMilliseconds(400));
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Documents");
        var items = await library.GetThreadItemsAsync(documents.Id);
        var taxReturn = items.Single(i => i.FileName == "Tax return 2025.pdf");
        var lease = items.Single(i => i.FileName == "Lease agreement.docx");
        using var thread = new ThreadViewModel(Services(library));
        var host = new LoggingHost();
        thread.AttachHost(host);
        await thread.LoadAsync(documents.Id);
        thread.IsSearchOpen = true;
        thread.SearchText = "minutes";
        await WaitForAsync(() => thread.TotalCount == 1);
        host.Log.Clear();

        thread.FocusItem(taxReturn.Id);
        thread.FocusItem(lease.Id);
        await WaitForAsync(() => host.Log.Count > 0);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal(["bring:" + lease.Id], host.Log);
    });

    /// <summary>
    /// A file that isn't in the thread (or no longer is) while a search hides most of it: the search is cleared, the thread
    /// scrolls to the end as after any search change, and the request is dropped instead of waiting for a later reload.
    /// </summary>
    [Fact]
    public void Focus_on_a_file_that_is_not_in_the_thread_clears_the_search_and_ends_at_the_bottom_as_usual() => RunSta(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var categories = await library.GetAllCategoriesAsync();
        var documents = categories.Single(c => c.Name == "Documents");
        var budget = (await library.GetThreadItemsAsync(categories.Single(c => c.Name == "Spreadsheets").Id)).Single(i => i.FileName == "Budget 2026.xlsx");
        using var thread = new ThreadViewModel(Services(library));
        var host = new LoggingHost();
        thread.AttachHost(host);
        await thread.LoadAsync(documents.Id);
        thread.IsSearchOpen = true;
        thread.SearchText = "minutes";
        await WaitForAsync(() => thread.TotalCount == 1);
        host.Log.Clear();

        thread.FocusItem(budget.Id);
        await WaitForAsync(() => host.Log.Count > 0);
        await Task.Delay(TimeSpan.FromSeconds(0.6));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("", thread.SearchText);
        Assert.Equal(["scroll-end"], host.Log);

        // A later search change reloads as usual: nothing jumps to the old request.
        thread.SearchText = "q3";
        await WaitForAsync(() => thread.TotalCount == 1);
        Assert.DoesNotContain(host.Log, entry => entry.StartsWith("bring:", StringComparison.Ordinal));
    });

    // ------------------------------------------------------------------ finding: album "+N" photos have no cell to flash

    /// <summary>
    /// An album bubble shows at most four cells (AlbumRow.BuildCells); the fourth stands in for the rest with "+N".
    /// The branch flashed the album cell whose Item.IsHighlighted turned true (ThreadTemplates.xaml, AlbumCellTemplate),
    /// but a photo from the fifth on has no cell, so Enter on it in the quick switcher scrolled to the album and nothing lit
    /// up. Fixed: the "+N" tile stands in for those photos (AlbumRow.CellShowing) and flashes for them.
    /// </summary>
    [Fact]
    public void A_photo_behind_the_album_plus_N_tile_is_shown_by_that_tile()
    {
        var library = SampleLibraryService.Create(withVault: true);
        using var owner = new ThreadViewModel(Services(library));
        var photos = Photos(categoryId: 1, count: 6);

        var result = ThreadRowBuilder.Build(photos, new Dictionary<long, ThreadItemViewModel>(), owner, photos[0].AddedAt.Date);

        var album = Assert.Single(result.Albums);
        Assert.Equal(6, album.Items.Count);
        var hidden = album.Items[4]; // IMG_0005.jpg: behind the "+3" tile
        Assert.True(album.Cells[3].HasMore);
        Assert.Equal("+3", album.Cells[3].MoreText);
        Assert.DoesNotContain(album.Cells, cell => ReferenceEquals(cell.Item, hidden)); // still no cell of its own...
        Assert.Same(album.Cells[3], album.CellShowing(hidden)); // ...but the "+3" tile shows it
        Assert.Same(album.Cells[3], album.CellShowing(album.Items[5]));
        Assert.Same(album.Cells[3], album.CellShowing(album.Items[3])); // the tile's own photo
        Assert.Same(album.Cells[1], album.CellShowing(album.Items[1]));
        Assert.Null(album.CellShowing(new ThreadItemViewModel(photos[0] with { Id = 99 }, owner)));
    }

    /// <summary>
    /// FocusItem on a photo behind the "+N" tile: the thread scrolls to the album, the photo itself is highlighted (the grid
    /// and details views flash it) and so is the tile standing in for it, which AlbumCellTemplate flashes.
    /// </summary>
    [Fact]
    public void Focus_on_a_photo_behind_the_plus_N_tile_flashes_that_tile() => RunSta(async () =>
    {
        var sample = SampleLibraryService.Create(withVault: true);
        var images = (await sample.GetAllCategoriesAsync()).Single(c => c.Name == "Images");
        var (library, _) = ThreadLibrary.Wrap(sample, images.Id, Photos(images.Id, count: 6));
        using var thread = new ThreadViewModel(Services(library));
        var host = new LoggingHost();
        thread.AttachHost(host);
        await thread.LoadAsync(images.Id);
        var album = Assert.Single(thread.ChatRows.OfType<AlbumRow>());
        var hidden = album.Items[4];

        thread.FocusItem(hidden.Id);

        Assert.Contains("bring:" + hidden.Id, host.Log);
        Assert.True(hidden.IsHighlighted);
        Assert.Equal([false, false, false, true], album.Cells.Select(c => c.IsHighlighted));
        await WaitForAsync(() => !album.Cells[3].IsHighlighted); // the flash ends
        Assert.False(hidden.IsHighlighted);

        // A photo with a cell of its own flashes through its Item; the tile's own flag stays off.
        thread.FocusItem(album.Items[1].Id);
        Assert.True(album.Items[1].IsHighlighted);
        Assert.All(album.Cells, c => Assert.False(c.IsHighlighted));
    });

    /// <summary>
    /// A live update (a file added elsewhere in the thread) reloads without a reset: the diff keeps the album row already on
    /// screen. The "+N" flash and the album's selection check mark must reach that row, not the fresh copy the reload built.
    /// (The selection part was broken before this fix too: SelectionChanged updated the fresh, unshown copies.)
    /// </summary>
    [Fact]
    public void After_a_live_update_the_album_on_screen_still_flashes_and_shows_its_selection() => RunSta(async () =>
    {
        var sample = SampleLibraryService.Create(withVault: true);
        var images = (await sample.GetAllCategoriesAsync()).Single(c => c.Name == "Images");
        var photos = Photos(images.Id, count: 6);
        var (library, fake) = ThreadLibrary.Wrap(sample, images.Id, photos);
        using var thread = new ThreadViewModel(Services(library));
        var host = new LoggingHost();
        thread.AttachHost(host);
        await thread.LoadAsync(images.Id);
        var shown = Assert.Single(thread.ChatRows.OfType<AlbumRow>());

        fake.Add(photos[0] with { Id = 50, FileName = "Scan.pdf", Extension = "pdf", BatchId = "later", TypeKey = "documents", AddedAt = photos[^1].AddedAt.AddMinutes(5) });
        fake.RaiseChanged();
        await WaitForAsync(() => thread.TotalCount == 7);
        Assert.Same(shown, Assert.Single(thread.ChatRows.OfType<AlbumRow>())); // the diff kept the row on screen

        thread.FocusItem(shown.Items[5].Id);
        Assert.True(shown.Cells[3].IsHighlighted);

        thread.SelectOnly(shown.Items);
        Assert.True(shown.IsSelected);
    });

    /// <summary>
    /// The markup side of the "+N" flash, checked on ThreadTemplates.xaml as checked in: AlbumCellTemplate starts the flash
    /// when the cell's own IsHighlighted turns true (besides its photo's), and the flash is drawn above the "+N" overlay.
    /// </summary>
    [Fact]
    public void The_album_cell_template_flashes_the_plus_N_tile_for_the_photos_behind_it()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Cabinette.App", "Views", "Thread", "ThreadTemplates.xaml"));
        var template = XDocument.Load(path).Descendants(wpf + "DataTemplate").Single(t => (string?)t.Attribute(x + "Key") == "AlbumCellTemplate");

        var triggers = template.Element(wpf + "DataTemplate.Triggers")!.Elements(wpf + "DataTrigger").ToList();
        var flashes = triggers
            .Where(t => (string?)t.Attribute("Value") == "True"
                        && t.Descendants(wpf + "BeginStoryboard").Any(b => (string?)b.Attribute("Storyboard") == "{StaticResource FlashStoryboard}"))
            .Select(t => (string?)t.Attribute("Binding"))
            .ToList();
        Assert.Contains("{Binding Item.IsHighlighted}", flashes); // a photo with a cell of its own
        Assert.Contains("{Binding IsHighlighted}", flashes); // the "+N" tile for the photos behind it

        var cell = template.Element(wpf + "Grid")!;
        var flash = cell.Elements().Last();
        Assert.Equal("Flash", (string?)flash.Attribute(x + "Name"));
        Assert.Contains(cell.Elements(), e => ((string?)e.Attribute("Visibility"))?.Contains("HasMore", StringComparison.Ordinal) == true);
    }

    // ------------------------------------------------------------------ finding: ranking only saw the newest 500 matches

    /// <summary>
    /// The switcher ranks what the main search returns, and that search lists only the newest 500 matching files. In a large
    /// library an older file named exactly like the text went missing while weaker, newer matches were listed. Fixed: when the
    /// search says there are more (HasMoreItems), the switcher also asks for the files whose name starts with the text.
    /// Checked on a real library: an old "Report.pdf" among 600 newer files that contain "report".
    /// </summary>
    [Fact]
    public async Task The_switcher_lists_an_old_exact_match_first_among_hundreds_of_newer_files_that_contain_the_text()
    {
        await using var fixture = await RealLibrary.CreateAsync();
        var library = fixture.Library;
        var documents = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        fixture.Seed(documents.Id, ["Report.pdf"], start);
        fixture.Seed(documents.Id, Enumerable.Range(0, 600).Select(i => $"annual_report_{i:D4}.txt"), start.AddDays(1));
        var directory = new CategoryDirectory(library);

        // The reviewed wiring, the main search alone: its newest 500 don't include the old file.
        var reviewed = new QuickSwitcherViewModel((text, ct) => library.SearchAsync(text, null, ct), directory, null, [], null, NoDelay);
        reviewed.Query = "report";
        await reviewed.Pending;
        Assert.DoesNotContain(reviewed.Rows, row => row.Name == "Report.pdf");

        var (counted, calls) = CountingLibrary.Wrap(library);
        var switcher = new QuickSwitcherViewModel((text, ct) => QuickSwitcherViewModel.SearchLibraryAsync(counted, text, ct), directory, null, [], null, NoDelay);
        switcher.Query = "report";
        await switcher.Pending;

        var best = Assert.IsType<QuickSwitcherFileRow>(switcher.Rows[0]);
        Assert.Equal("Report.pdf", best.Name);
        Assert.Equal("Documents", best.Detail);
        Assert.Equal(QuickSwitcherViewModel.MaxResults, switcher.Rows.Count);
        Assert.Equal(1, calls.Count(nameof(ILibraryService.SearchNamePrefixAsync)));

        // When the search lists every match, there is nothing more to fetch.
        calls.Clear();
        switcher.Query = "annual_report_0042";
        await switcher.Pending;
        Assert.Equal("annual_report_0042.txt", Assert.Single(switcher.Rows).Name);
        Assert.Equal(0, calls.Count(nameof(ILibraryService.SearchNamePrefixAsync)));
    }

    /// <summary>
    /// The extra matches go after the listed ones, newest first, without duplicates: the ranking breaks ties by that order
    /// (the library's newest-first order), and they are older than every listed file.
    /// </summary>
    [Fact]
    public async Task The_extra_name_matches_follow_the_listed_files_newest_first_without_duplicates()
    {
        var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        LibraryItem File(long id, string name, int day) => Photos(1, 1)[0] with { Id = id, FileName = name, AddedAt = start.AddDays(day), BatchId = null };
        var listed = new List<LibraryItem> { File(9, "new report.txt", 9), File(8, "report draft.txt", 8) };
        var named = new List<LibraryItem> { File(1, "Report.pdf", 1), File(8, "report draft.txt", 8), File(3, "Report 2.pdf", 3) };
        var library = FakeSearch.Create(new SearchResults([], listed) { HasMoreItems = true }, named);

        var results = await QuickSwitcherViewModel.SearchLibraryAsync(library, "report", CancellationToken.None);

        Assert.Equal([9L, 8, 3, 1], results.Items.Select(i => i.Id));
        Assert.True(results.HasMoreItems);
    }

    // ------------------------------------------------------------------ finding: Ctrl+K left the thread's filter popup open

    /// <summary>
    /// Reviewed code: Ctrl+K closed only the main window's filter flyout; the thread's filter popup (StaysOpen=False, so it
    /// only closes on a click elsewhere) floated over the switcher. Fixed: it closes too. The switcher Ctrl+K opens also uses
    /// the search that sees old exact matches (finding 3).
    /// </summary>
    [Fact]
    public void Ctrl_K_closes_the_thread_filter_popup_and_the_filter_flyout() => RunSta(async () =>
    {
        await using var fixture = await RealLibrary.CreateAsync();
        var library = fixture.Library;
        var documents = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        fixture.Seed(documents.Id, ["Report.pdf"], start);
        fixture.Seed(documents.Id, Enumerable.Range(0, 600).Select(i => $"annual_report_{i:D4}.txt"), start.AddDays(1));
        using var shell = CreateShell(library);
        await shell.OpenCategoryAsync(documents.Id, null);
        var thread = shell.CurrentThread!;
        thread.IsFilterOpen = true;
        shell.IsFilterOpen = true;

        shell.OpenQuickSwitcherCommand.Execute(null);
        await WaitForAsync(() => shell.Dialogs.Top is QuickSwitcherViewModel);

        Assert.False(thread.IsFilterOpen);
        Assert.False(shell.IsFilterOpen);

        var switcher = (QuickSwitcherViewModel)shell.Dialogs.Top!;
        switcher.Query = "report";
        await switcher.Pending;
        Assert.Equal("Report.pdf", switcher.Rows[0].Name);
        switcher.Dismiss();
        await WaitForAsync(() => !shell.Dialogs.IsAnyOpen);
    });

    /// <summary>
    /// The same for every dialog, also those opened from the keyboard in the thread (Delete, F2) or by Ctrl+N: opening one
    /// closes the filter popups, so none floats over a dialog.
    /// </summary>
    [Fact]
    public void Every_dialog_closes_the_filter_popups() => RunSta(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Documents");
        using var shell = CreateShell(library);
        await shell.OpenCategoryAsync(documents.Id, null);
        var thread = shell.CurrentThread!;
        thread.IsFilterOpen = true;
        shell.IsFilterOpen = true;

        var confirm = new ConfirmDialogViewModel("Delete?", "Sure?", "Delete", isDestructive: true);
        var shown = shell.Dialogs.ShowAsync(confirm);

        Assert.False(thread.IsFilterOpen);
        Assert.False(shell.IsFilterOpen);

        // Closing a dialog doesn't touch them.
        thread.IsFilterOpen = true;
        confirm.Close(null);
        await shown;
        Assert.True(thread.IsFilterOpen);
    });

    // ------------------------------------------------------------------ disproof: the dialog host restyle keeps the height clamp

    /// <summary>
    /// DialogHost.xaml moved the card's VerticalAlignment and MaxHeight (an ElementName binding to the host) into an
    /// inline style so the quick switcher can trigger top alignment. Checked on the real markup (the XAML file, parsed
    /// without its code-behind and with stand-ins for the app resources) in a hidden window: an ordinary dialog still
    /// gets host height - 48 and stays centered; the switcher gets Top, a 56 px margin and host height - 80. No regression.
    /// </summary>
    [Fact]
    public void Dialog_cards_keep_their_height_clamp_and_the_switcher_sits_at_the_top() => RunSta(() =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var icons = new CategoryIconService(library, new FakeIconGenerator());
        var directory = new CategoryDirectory(library);
        var service = new DialogService(library, icons, directory);
        var host = LoadDialogHostMarkup();
        host.DataContext = service;
        host.Width = 900;
        host.Height = 600;
        using var source = HiddenSource(host, 900, 600);
        host.UpdateLayout();
        Assert.Equal(600, host.ActualHeight);

        var confirm = new ConfirmDialogViewModel("Empty Deleted?", "Sure?", "Empty Deleted", isDestructive: true);
        _ = service.ShowAsync(confirm);
        Assert.True(PumpUntil(() => FindCard(host) is not null, TimeSpan.FromSeconds(3)), "the confirm card never appeared");
        host.UpdateLayout();
        var card = FindCard(host)!;
        Assert.Equal(VerticalAlignment.Center, card.VerticalAlignment);
        Assert.Equal(600 - 48, card.MaxHeight);
        Assert.Equal(900 - 48, card.MaxWidth);
        confirm.Close(null);
        Assert.True(PumpUntil(() => service.OpenDialogs.Count == 0 && FindCard(host) is null, TimeSpan.FromSeconds(3)), "the confirm card never went away");

        var switcher = new QuickSwitcherViewModel((text, ct) => library.SearchAsync(text, null, ct), directory, icons, [], null);
        _ = service.ShowAsync(switcher);
        Assert.True(PumpUntil(() => FindCard(host) is not null, TimeSpan.FromSeconds(3)), "the switcher card never appeared");
        host.UpdateLayout();
        card = FindCard(host)!;
        Assert.Equal(VerticalAlignment.Top, card.VerticalAlignment);
        Assert.Equal(new Thickness(0, 56, 0, 0), card.Margin);
        Assert.Equal(600 - 80, card.MaxHeight);
        Assert.Equal(560, card.Width);
        switcher.Dismiss();
        Assert.True(PumpUntil(() => service.OpenDialogs.Count == 0, TimeSpan.FromSeconds(3)));
    });

    // ------------------------------------------------------------------ helpers

    private static ThreadServices Services(ILibraryService library) => new(
        library,
        NullProxy.Create<IShellContextMenuService>(),
        NullProxy.Create<IShellOperations>(),
        NullProxy.Create<IFileIntake>(),
        NullProxy.Create<IDialogService>(),
        NullProxy.Create<INotificationService>(),
        NullProxy.Create<IShellNavigator>(),
        new ThumbnailLoader(NullProxy.Create<IThumbnailService>(), NullProxy.Create<IMediaInfoService>()),
        new ThreadViewState())
    {
        Directory = new CategoryDirectory(library),
    };

    private static Task NoDelay(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Photos dropped together (one batch, one day), so they make one album: IMG_0001.jpg … IMG_000N.jpg, ids 1 … N.</summary>
    private static List<LibraryItem> Photos(long categoryId, int count)
    {
        var start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(1, count).Select(i => new LibraryItem
        {
            Id = i,
            Mode = ItemMode.Copy,
            Kind = ItemKind.File,
            FileName = $"IMG_{i:0000}.jpg",
            Extension = "jpg",
            FullPath = $@"C:\Vault\Images\IMG_{i:0000}.jpg",
            HomeCategoryId = categoryId,
            CategoryIds = [categoryId],
            AddedAt = start.AddMinutes(i),
            BatchId = "one-drop",
            TypeKey = ExtensionMap.ImagesKey,
        }).ToList();
    }

    /// <summary>The main window's view model with stand-ins for the services these tests don't use.</summary>
    private static MainViewModel CreateShell(ILibraryService library)
    {
        var icons = new CategoryIconService(library, new FakeIconGenerator());
        var directory = new CategoryDirectory(library);
        var dialogs = new DialogService(library, icons, directory);
        var notifications = new NotificationService();
        var shellOperations = NullProxy.Create<IShellOperations>();
        return new MainViewModel(
            library,
            new ThreadFactory(() => new ThreadViewModel(Services(library))),
            new ShellNavigator(),
            NullProxy.Create<IFileIntake>(),
            dialogs,
            notifications,
            icons,
            directory,
            shellOperations,
            NullProxy.Create<IThumbnailService>(),
            () => throw new InvalidOperationException("Settings are not used here."),
            new RecentCategories(load: null, save: null),
            new ItemOpener(library, shellOperations, dialogs, notifications));
    }

    private sealed class ThreadFactory(Func<ThreadViewModel> create) : IThreadViewModelFactory
    {
        public ThreadViewModel Create() => create();
    }

    /// <summary>A real (SQLite) library in a temp folder, with files seeded straight into the database.</summary>
    private sealed class RealLibrary : IAsyncDisposable
    {
        private readonly string _root;

        private RealLibrary(string root, ILibraryService library)
        {
            _root = root;
            Library = library;
        }

        public ILibraryService Library { get; }

        public static async Task<RealLibrary> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "switcher-" + Guid.NewGuid().ToString("N"));
            var library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.Combine(root, "data") }, new NoRecycleBin());
            return new RealLibrary(root, library);
        }

        /// <summary>Linked files in <paramref name="categoryId"/>, all added at <paramref name="at"/> (hundreds in a few ms).</summary>
        public void Seed(long categoryId, IEnumerable<string> names, DateTimeOffset at)
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "data", "cabinette.db")};Pooling=False");
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO items(mode, kind, file_name, extension, pointer_path, home_category_id, size_bytes, added_at, added_by, is_missing)
                VALUES (1, 0, $name, $ext, $path, $cat, 1, $at, 0, 0);
                INSERT INTO item_categories(item_id, category_id, added_at, added_by) VALUES (last_insert_rowid(), $cat, $at, 0);
                """;
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var ext = insert.Parameters.Add("$ext", SqliteType.Text);
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            insert.Parameters.AddWithValue("$cat", categoryId);
            insert.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            foreach (var fileName in names)
            {
                name.Value = fileName;
                ext.Value = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
                path.Value = Path.Combine(_root, "source", fileName);
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }

        public async ValueTask DisposeAsync()
        {
            await Library.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp folder; never fail a test over it.
            }
        }

        private sealed class NoRecycleBin : Cabinette.Core.Abstractions.IRecycleBin
        {
            public void SendToRecycleBin(IReadOnlyCollection<string> paths) => throw new IOException("not used");
        }
    }

    /// <summary>Answers the switcher's two queries with fixed results.</summary>
    public class FakeSearch : DispatchProxy
    {
        private SearchResults _results = null!;
        private IReadOnlyList<LibraryItem> _named = [];

        public static ILibraryService Create(SearchResults results, IReadOnlyList<LibraryItem> named)
        {
            var proxy = Create<ILibraryService, FakeSearch>();
            var fake = (FakeSearch)(object)proxy;
            fake._results = results;
            fake._named = named;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            nameof(ILibraryService.SearchAsync) => Task.FromResult(_results),
            nameof(ILibraryService.SearchNamePrefixAsync) => Task.FromResult(_named),
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }

    /// <summary>
    /// Forwards to another library, but lists its own items as the thread of one category (any search or filter is ignored),
    /// and raises <see cref="ILibraryService.Changed"/> for that category when the test says so.
    /// </summary>
    public class ThreadLibrary : DispatchProxy
    {
        private readonly List<LibraryItem> _items = [];
        private ILibraryService _inner = null!;
        private long _categoryId;
        private EventHandler<LibraryChangedEventArgs>? _changed;

        public static (ILibraryService Library, ThreadLibrary Fake) Wrap(ILibraryService inner, long categoryId, IEnumerable<LibraryItem> items)
        {
            var proxy = Create<ILibraryService, ThreadLibrary>();
            var fake = (ThreadLibrary)(object)proxy;
            fake._inner = inner;
            fake._categoryId = categoryId;
            fake._items.AddRange(items);
            return (proxy, fake);
        }

        public void Add(LibraryItem item)
        {
            lock (_items)
            {
                _items.Add(item);
            }
        }

        /// <summary>As the library does, on a background thread.</summary>
        public void RaiseChanged()
            => Task.Run(() => _changed?.Invoke(this, new LibraryChangedEventArgs { Kind = LibraryChangeKind.Items, CategoryIds = [_categoryId] }));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "add_Changed":
                    _changed += (EventHandler<LibraryChangedEventArgs>)args![0]!;
                    return null;
                case "remove_Changed":
                    _changed -= (EventHandler<LibraryChangedEventArgs>)args![0]!;
                    return null;
                case nameof(ILibraryService.GetThreadItemsAsync) when (long)args![0]! == _categoryId:
                    lock (_items)
                    {
                        return Task.FromResult<IReadOnlyList<LibraryItem>>([.. _items.OrderBy(i => i.AddedAt)]);
                    }
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }
    }

    /// <summary>
    /// Views/Dialogs/DialogHost.xaml as checked in, parsed as loose XAML: no x:Class or code-behind handlers (they only
    /// manage focus and the fade-in), and the three app resources the card uses (the Add converter, Shell.Card, the
    /// scrim brush) supplied as stand-ins, since StaticResource inside a template needs an Application otherwise.
    /// </summary>
    private static UserControl LoadDialogHostMarkup()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Cabinette.App", "Views", "Dialogs", "DialogHost.xaml"));
        var xaml = File.ReadAllText(path);
        Assert.Contains("x:Class=\"Cabinette.App.Views.Dialogs.DialogHost\"", xaml);
        xaml = xaml.Replace("x:Class=\"Cabinette.App.Views.Dialogs.DialogHost\"", "xmlns:conv=\"clr-namespace:Cabinette.App.Converters;assembly=Cabinette\"")
            .Replace(" Loaded=\"OnLayerLoaded\"", "")
            .Replace(" Unloaded=\"OnLayerUnloaded\"", "");
        const string Resources = """
            <UserControl.Resources>
                <conv:AddConverter x:Key="Add" />
                <Style x:Key="Shell.Card" TargetType="Border">
                    <Setter Property="Background" Value="White" />
                </Style>
                <SolidColorBrush x:Key="Cab.OverlayBackground" Color="#80000000" />
            </UserControl.Resources>
            """;
        var openTagEnd = xaml.IndexOf('>', xaml.IndexOf("<UserControl", StringComparison.Ordinal)) + 1;
        xaml = xaml.Insert(openTagEnd, Resources);
        return (UserControl)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static Border? FindCard(DependencyObject node)
    {
        if (node is Border { Name: "Card" } border)
        {
            return border;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindCard(VisualTreeHelper.GetChild(node, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A window far off-screen and never shown (no WS_VISIBLE), so layout works without anything appearing.</summary>
    private static HwndSource HiddenSource(Visual root, int width, int height)
    {
        const int WsPopup = unchecked((int)0x80000000);
        var parameters = new HwndSourceParameters("Cabinette switcher review") { Width = width, Height = height, PositionX = -20000, PositionY = -20000, WindowStyle = WsPopup };
        return new HwndSource(parameters) { RootVisual = root };
    }

    private static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var elapsed = Stopwatch.StartNew();
        while (!done())
        {
            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        return true;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private static void RunSta(Action action) => RunSta(() =>
    {
        action();
        return Task.CompletedTask;
    });

    /// <summary>Runs an async test body on a fresh STA thread whose dispatcher keeps running until the body ends.</summary>
    private static void RunSta(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Task task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                task = Task.FromException(ex);
            }

            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            if (task.Exception is { } error)
            {
                failure = ExceptionDispatchInfo.Capture(error.InnerException ?? error);
            }

            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(1)))
        {
            throw new TimeoutException("STA test body did not finish.");
        }

        failure?.Throw();
    }

    /// <summary>Records the host calls in order: "scroll-end" and "bring:&lt;item id&gt;".</summary>
    private sealed class LoggingHost : IThreadViewHost
    {
        public List<string> Log { get; } = [];

        public IntPtr OwnerHandle => IntPtr.Zero;

        public void ScrollToEnd() => Log.Add("scroll-end");

        public void NotifyNewItems(int count)
        {
        }

        public void BringItemIntoView(ThreadItemViewModel item) => Log.Add("bring:" + item.Id);

        public bool ShowTagEditor(TagEditorViewModel editor) => false;

        public void ShowMediaViewer(IReadOnlyList<ThreadItemViewModel> items, int index)
        {
        }
    }

    /// <summary>Forwards everything to the sample library; GetThreadItemsAsync takes an extra while (honouring cancellation).</summary>
    public class SlowLibrary : DispatchProxy
    {
        private ILibraryService _inner = null!;
        private TimeSpan _delay;

        public static ILibraryService Wrap(ILibraryService inner, TimeSpan delay)
        {
            var proxy = Create<ILibraryService, SlowLibrary>();
            var slow = (SlowLibrary)(object)proxy;
            slow._inner = inner;
            slow._delay = delay;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var result = targetMethod!.Invoke(_inner, args);
                return targetMethod.Name == nameof(ILibraryService.GetThreadItemsAsync)
                    ? SlowAsync((Task<IReadOnlyList<LibraryItem>>)result!, (CancellationToken)args![3]!)
                    : result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }

        private async Task<IReadOnlyList<LibraryItem>> SlowAsync(Task<IReadOnlyList<LibraryItem>> inner, CancellationToken ct)
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
            return await inner.ConfigureAwait(false);
        }
    }
}
