using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Localization;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using Xunit.Abstractions;

namespace Cabinette.App.Tests;

/// <summary>The Gallery view (SPEC.md section 4, "Gallery"): where it is offered, its rows, sorts, sizes and keyboard.</summary>
public sealed class GalleryTests
{
    // ------------------------------------------------------------------ where the Gallery is offered, and the default view

    [Fact]
    public void Images_its_subcategories_and_a_renamed_Images_open_in_the_Gallery_other_threads_in_Chat() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var state = new ThreadViewState();
        var all = await library.GetAllCategoriesAsync();
        var images = all.Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var holidays = await library.CreateCategoryAsync("Holidays", images.Id);
        var beach = await library.CreateCategoryAsync("Beach", holidays.Id);

        foreach (var id in new[] { images.Id, holidays.Id, beach.Id })
        {
            using var vm = Create(library, state);
            await vm.LoadAsync(id);
            Assert.True(vm.IsGalleryOffered, $"category {id} offers the Gallery");
            Assert.Equal(ThreadViewMode.Gallery, vm.ViewMode);
            Assert.True(vm.IsGalleryView);
            Assert.False(vm.IsChatView);
        }

        // Found by its built-in key: renaming Images changes nothing.
        await library.RenameCategoryAsync(images.Id, "Photos");
        using (var renamed = Create(library, state))
        {
            await renamed.LoadAsync(images.Id);
            Assert.Equal("Photos", renamed.CategoryName);
            Assert.True(renamed.IsGalleryOffered);
            Assert.Equal(ThreadViewMode.Gallery, renamed.ViewMode);
            Assert.NotEmpty(renamed.GalleryRows);
        }

        // Nowhere else: a built-in category, a category of your own (with an image in it), Deleted and the Starred view.
        foreach (var id in new[] { all.Single(c => c.BuiltInKey == "documents").Id, all.Single(c => c.Name == "Work").Id, all.Single(c => c.BuiltInKey == ExtensionMap.DeletedKey).Id })
        {
            using var vm = Create(library, state);
            await vm.LoadAsync(id);
            Assert.False(vm.IsGalleryOffered, $"category {id} does not offer the Gallery");
            Assert.Equal(ThreadViewMode.Chat, vm.ViewMode);
            Assert.Empty(vm.GalleryRows);
        }

        using var starred = Create(library, state);
        await starred.LoadStarredAsync();
        Assert.False(starred.IsGalleryOffered);
        Assert.Equal(ThreadViewMode.Chat, starred.ViewMode);
    });

    [Fact]
    public void The_All_in_rows_of_Images_and_its_subcategories_open_in_the_Gallery() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var images = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var holidays = await library.CreateCategoryAsync("Holidays", images.Id);
        await library.CreateCategoryAsync("Beach", holidays.Id);
        var chat = new ChatListViewModel(library, new CategoryIconService(library, new FakeIconGenerator()));
        await chat.RefreshAsync();

        await chat.DrillInAsync(images.Id);
        var allInImages = chat.Rows.Single(r => r.IsAllInRow);
        await chat.DrillInAsync(holidays.Id);
        var allInHolidays = chat.Rows.Single(r => r.IsAllInRow);

        foreach (var row in new[] { allInImages, allInHolidays })
        {
            using var vm = Create(library, new ThreadViewState());
            await vm.LoadAsync(row.CategoryId);
            Assert.True(vm.IsGalleryOffered);
            Assert.Equal(ThreadViewMode.Gallery, vm.ViewMode);
        }

        Assert.Equal(images.Id, allInImages.CategoryId);
        Assert.Equal(holidays.Id, allInHolidays.CategoryId);
    });

    [Fact]
    public void A_view_chosen_in_Images_is_remembered_and_a_remembered_Gallery_never_opens_elsewhere() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var state = new ThreadViewState();
        var all = await library.GetAllCategoriesAsync();
        var images = all.Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var documents = all.Single(c => c.BuiltInKey == "documents");

        using (var first = Create(library, state))
        {
            await first.LoadAsync(images.Id);
            first.IsGridView = true;
            Assert.Empty(first.GalleryRows); // only filled while the Gallery is shown
            Assert.NotEmpty(first.GridRows);
        }

        using (var again = Create(library, state))
        {
            await again.LoadAsync(images.Id);
            Assert.Equal(ThreadViewMode.Grid, again.ViewMode);
            Assert.True(again.IsGalleryOffered); // still offered on the toggle
            again.IsGalleryView = true;
            Assert.NotEmpty(again.GalleryRows);
        }

        state.SetViewMode(documents.Id, ThreadViewMode.Gallery);
        using var docs = Create(library, state);
        await docs.LoadAsync(documents.Id);
        Assert.Equal(ThreadViewMode.Chat, docs.ViewMode);
    });

    // ------------------------------------------------------------------ rows: columns, sections, headers, last row

    [Theory]
    [InlineData(0, 128, 1)]
    [InlineData(100, 128, 1)]
    [InlineData(275, 128, 1)]
    [InlineData(276, 128, 2)] // 128 + 20 + 128
    [InlineData(1000, 128, 6)]
    [InlineData(1000, 96, 8)]
    [InlineData(1000, 176, 5)]
    public void Columns_follow_from_the_width_and_the_cell_size(double width, double cell, int expected)
        => Assert.Equal(expected, GalleryMetrics.Columns(width, cell));

    [Fact]
    public void Cells_share_the_row_and_sizes_follow_the_size_step()
    {
        Assert.Equal(166, GalleryMetrics.SlotWidth(1000, 6));
        Assert.Equal(1000, GalleryMetrics.SlotWidth(1000, 1));
        Assert.Equal([96d, 128d, 176d], new[] { GalleryCellSize.Small, GalleryCellSize.Medium, GalleryCellSize.Large }.Select(GalleryMetrics.CellSize));
    }

    [Fact]
    public void Prints_fit_inside_the_box_keep_their_shape_and_stand_on_its_bottom_line()
    {
        var box = new Size(128, 128);
        const double Frame = 4, Shadow = GalleryMetrics.ShadowSpace;

        var landscape = GalleryMetrics.PrintRect(box, 16d / 10, Frame);
        var portrait = GalleryMetrics.PrintRect(box, 3d / 4, Frame);
        var square = GalleryMetrics.PrintRect(box, 1, Frame);

        // Full width (less the shadow) for a landscape photo, full height for a portrait one; never cropped.
        Assert.Equal(box.Width - Shadow, landscape.Width);
        Assert.Equal(box.Height - Shadow, portrait.Height);
        Assert.Equal(square.Width, square.Height);
        Assert.Equal(16d / 10, (landscape.Width - (2 * Frame)) / (landscape.Height - (2 * Frame)), 1);
        Assert.Equal(3d / 4, (portrait.Width - (2 * Frame)) / (portrait.Height - (2 * Frame)), 1);

        // A common bottom line, centered across.
        Assert.All(new[] { landscape, portrait, square }, r => Assert.Equal(box.Height - Shadow, r.Bottom));
        Assert.True(Math.Abs(portrait.Left - (box.Width - Shadow - portrait.Right)) <= 1);

        // Until the shape is known nothing is drawn (the layout never waits for it).
        Assert.True(GalleryMetrics.PrintRect(box, 0, Frame).IsEmpty);
    }

    [Fact]
    public void Rows_start_a_section_per_day_under_its_header_and_a_short_last_row_ends_each() => WithCulture(() =>
    {
        var owner = DetachedOwner();
        var today = new DateTime(2026, 11, 12); // a Thursday
        var items = new List<ThreadItemViewModel>();
        var id = 1;
        foreach (var (date, count) in new[] { (today, 5), (today.AddDays(-1), 2), (today.AddDays(-3), 3), (new DateTime(2026, 11, 5), 1) })
        {
            for (var i = 0; i < count; i++)
            {
                items.Add(Vm(owner, Photo(id++, $"p{id}.jpg", Local(date.AddHours(12 - i)))));
            }
        }

        var order = GalleryBuilder.Order(items, GallerySort.Default, out var days);
        var rows = GalleryBuilder.BuildRows(order, days, columns: 3, today);

        Assert.Equal(
            ["Today", "3", "2", "Yesterday", "2", "Monday", "3", "5 November 2026", "1"],
            rows.Select(r => r is GalleryHeaderRow h ? h.Text : ((GalleryCellRow)r).Items.Count.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(order, rows.OfType<GalleryCellRow>().SelectMany(r => r.Items));
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());

        // One column: one cell per row; more columns than cells: one row per day.
        Assert.Equal(11, GalleryBuilder.BuildRows(order, days, 1, today).OfType<GalleryCellRow>().Count());
        Assert.Equal(4, GalleryBuilder.BuildRows(order, days, 50, today).OfType<GalleryCellRow>().Count());
        Assert.Empty(GalleryBuilder.BuildRows([], [], 3, today));
    });

    [Fact]
    public void The_view_reports_its_columns_and_the_rows_follow() => RunOnDispatcher(async () =>
    {
        var (vm, _, _) = await OpenGalleryAsync([.. Day(0, 7), .. Day(1, 2)]);
        vm.SetGalleryColumns(3);
        Assert.Equal([3, 3, 1, 2], vm.GalleryRows.OfType<GalleryCellRow>().Select(r => r.Items.Count));
        vm.SetGalleryColumns(4);
        Assert.Equal([4, 3, 2], vm.GalleryRows.OfType<GalleryCellRow>().Select(r => r.Items.Count));
        Assert.Equal(["Today", "Yesterday"], vm.GalleryRows.OfType<GalleryHeaderRow>().Select(h => h.Text));
        vm.Dispose();
    });

    // ------------------------------------------------------------------ sorting

    [Fact]
    public void Every_sort_and_its_reverse_sorts_within_the_day_sections() => RunOnDispatcher(async () =>
    {
        var today = Local(DateTime.Today);
        LibraryItem[] items =
        [
            Photo(1, "b.png", today.AddHours(9), size: 300),
            Photo(2, "A.jpg", today.AddHours(10), size: 100),
            Photo(3, "c.heic", today.AddHours(11), size: 200),
            Photo(4, "clip.mp4", today.AddHours(8), size: 900),
            Photo(5, "z older.jpg", today.AddDays(-1).AddHours(12), size: 5000),
            Photo(6, "a older.png", today.AddDays(-1).AddHours(11), size: 1),
        ];
        var (vm, _, _) = await OpenGalleryAsync(items);

        // Default: date added, newest first (newest day first, newest file first within it).
        Assert.Equal(GallerySort.Default, vm.GallerySort);
        Assert.Equal(["c.heic", "A.jpg", "b.png", "clip.mp4", "z older.jpg", "a older.png"], Names(vm));
        Assert.Equal([false, false, false, true], vm.GallerySortHeaders.Select(h => h.IsSorted));

        vm.SortGalleryBy(GallerySortKey.Date);
        Assert.Equal(["a older.png", "z older.jpg", "clip.mp4", "b.png", "A.jpg", "c.heic"], Names(vm)); // oldest day first

        vm.SortGalleryBy(GallerySortKey.Name);
        Assert.True(vm.GallerySort.Ascending);
        Assert.Equal(["A.jpg", "b.png", "c.heic", "clip.mp4", "a older.png", "z older.jpg"], Names(vm)); // within each day, newest day first
        vm.SortGalleryBy(GallerySortKey.Name);
        Assert.Equal(["clip.mp4", "c.heic", "b.png", "A.jpg", "z older.jpg", "a older.png"], Names(vm));
        Assert.False(vm.GallerySortHeaders.Single(h => h.Key == GallerySortKey.Name).IsAscending);

        vm.SortGalleryBy(GallerySortKey.Size);
        Assert.False(vm.GallerySort.Ascending); // largest first
        Assert.Equal(["clip.mp4", "b.png", "c.heic", "A.jpg", "z older.jpg", "a older.png"], Names(vm));
        vm.SortGalleryBy(GallerySortKey.Size);
        Assert.Equal(["A.jpg", "c.heic", "b.png", "clip.mp4", "a older.png", "z older.jpg"], Names(vm));

        // Type: by type name (Image before Video), then by extension.
        vm.SortGalleryBy(GallerySortKey.Type);
        Assert.True(vm.GallerySort.Ascending);
        Assert.Equal(["c.heic", "A.jpg", "b.png", "clip.mp4", "z older.jpg", "a older.png"], Names(vm));
        vm.SortGalleryBy(GallerySortKey.Type);
        Assert.Equal(["clip.mp4", "b.png", "A.jpg", "c.heic", "a older.png", "z older.jpg"], Names(vm));
        Assert.Equal([false, false, true, false], vm.GallerySortHeaders.Select(h => h.IsSorted));
        vm.Dispose();
    });

    [Fact]
    public void Date_taken_uses_the_photos_date_then_its_file_date_then_when_it_was_added() => RunOnDispatcher(async () =>
    {
        var today = Local(DateTime.Today);
        LibraryItem[] items =
        [
            Photo(1, "taken long ago.jpg", today.AddHours(9), taken: today.AddDays(-40).AddHours(15)),
            Photo(2, "screenshot.png", today.AddHours(10), modified: today.AddDays(-2).AddHours(8)),
            Photo(3, "download.jpg", today.AddHours(11)),
            Photo(4, "taken yesterday.jpg", today.AddDays(-3).AddHours(9), taken: today.AddDays(-1).AddHours(20), modified: today.AddDays(-30)),
        ];
        var (vm, _, _) = await OpenGalleryAsync(items);
        Assert.Equal(["Today"], vm.GalleryRows.OfType<GalleryHeaderRow>().Take(1).Select(h => h.Text));

        vm.SetGalleryDateKind(GalleryDateKind.Taken);
        Assert.Equal(new GallerySort(GallerySortKey.Date, false, GalleryDateKind.Taken), vm.GallerySort);
        Assert.Equal("Date taken", vm.GallerySortHeaders.Single(h => h.Key == GallerySortKey.Date).Title);
        Assert.True(vm.IsGalleryDateTaken);
        Assert.Equal(["download.jpg", "taken yesterday.jpg", "screenshot.png", "taken long ago.jpg"], Names(vm));
        var sections = vm.GalleryRows.OfType<GalleryHeaderRow>().Select(h => h.Day).ToList();
        Assert.Equal([today.Date, today.Date.AddDays(-1), today.Date.AddDays(-2), today.Date.AddDays(-40)], sections);
        Assert.Equal(today.AddDays(-2).AddHours(8), vm.Items.Single(i => i.Id == 2).DateTaken);
        Assert.Equal(today.AddHours(11), vm.Items.Single(i => i.Id == 3).DateTaken);

        // Name within the date-taken sections; the date in use stays when another key is chosen.
        vm.SortGalleryBy(GallerySortKey.Name);
        Assert.Equal(GalleryDateKind.Taken, vm.GallerySort.DateKind);
        Assert.Equal(4, vm.GalleryRows.OfType<GalleryHeaderRow>().Count());

        // Back to date added: one day, newest first.
        vm.SetGalleryDateKind(GalleryDateKind.Added);
        Assert.Equal(GallerySort.Default, vm.GallerySort);
        Assert.Equal("Date added", vm.GallerySortHeaders.Single(h => h.Key == GallerySortKey.Date).Title);
        Assert.Equal(2, vm.GalleryRows.OfType<GalleryHeaderRow>().Count());
        vm.Dispose();
    });

    /// <summary>
    /// Date-taken bug hunt F1: a date taken keeps the camera's offset, and its section is the day the camera showed, in any time
    /// zone this PC is in. 23:30 at −10:00 is 13 September in most zones (Istanbul's included), but the photo was taken on the 12th.
    /// </summary>
    [Fact]
    public void A_date_taken_is_sectioned_by_the_day_the_camera_showed() => RunOnDispatcher(async () =>
    {
        var taken = new DateTimeOffset(2026, 9, 12, 23, 30, 0, TimeSpan.FromHours(-10));
        var (vm, _, _) = await OpenGalleryAsync([Photo(1, "evening.jpg", Local(DateTime.Today).AddHours(9), taken: taken)]);

        vm.SetGalleryDateKind(GalleryDateKind.Taken);

        Assert.Equal([new DateTime(2026, 9, 12)], vm.GalleryRows.OfType<GalleryHeaderRow>().Select(h => h.Day));
        Assert.Equal(taken.Offset, vm.Items.Single().DateTaken.Offset);
        vm.Dispose();
    });

    [Fact]
    public void The_sort_is_remembered_per_category_while_the_app_runs() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var state = new ThreadViewState();
        var images = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var holidays = await library.CreateCategoryAsync("Holidays", images.Id);

        using (var first = Create(library, state))
        {
            await first.LoadAsync(images.Id);
            first.SetGalleryDateKind(GalleryDateKind.Taken);
            first.SortGalleryBy(GallerySortKey.Name);
        }

        using var again = Create(library, state);
        await again.LoadAsync(images.Id);
        Assert.Equal(new GallerySort(GallerySortKey.Name, true, GalleryDateKind.Taken), again.GallerySort);
        Assert.True(again.GallerySortHeaders.Single(h => h.Key == GallerySortKey.Name).IsSorted);

        using var other = Create(library, state);
        await other.LoadAsync(holidays.Id);
        Assert.Equal(GallerySort.Default, other.GallerySort);
    });

    // ------------------------------------------------------------------ cell sizes

    [Fact]
    public void Cell_sizes_step_between_three_sizes_and_are_stored() => RunOnDispatcher(async () =>
    {
        var saved = new List<GalleryCellSize>();
        var state = new ThreadViewState(() => GalleryCellSize.Large, saved.Add);
        await state.GallerySizeLoaded;
        var (vm, _, _) = await OpenGalleryAsync(Day(0, 3), state: state);

        Assert.Equal(GalleryCellSize.Large, vm.GalleryCellSize); // the stored size
        Assert.Equal(176, vm.GalleryCellPixels);
        Assert.False(vm.StepGalleryCellSize(1));
        Assert.False(vm.GrowGalleryCellsCommand.CanExecute(null));

        Assert.True(vm.StepGalleryCellSize(-1));
        Assert.Equal(128, vm.GalleryCellPixels);
        vm.ShrinkGalleryCellsCommand.Execute(null);
        Assert.Equal(GalleryCellSize.Small, vm.GalleryCellSize);
        Assert.Equal(96, vm.GalleryCellPixels);
        Assert.False(vm.StepGalleryCellSize(-5));
        Assert.False(vm.ShrinkGalleryCellsCommand.CanExecute(null));
        Assert.True(vm.GrowGalleryCellsCommand.CanExecute(null));

        Assert.Equal([GalleryCellSize.Medium, GalleryCellSize.Small], saved);
        Assert.Equal(GalleryCellSize.Small, state.GallerySize);
        vm.Dispose();
    });

    [Fact]
    public async Task The_cell_size_survives_a_restart_in_the_state_file_but_not_in_sandboxed_runs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CabinetteGalleryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var store = new AppStateStore(paths);
            var state = ThreadViewState.Create(store, new CommandLineOptions());
            await state.GallerySizeLoaded;
            Assert.Equal(GalleryCellSize.Medium, state.GallerySize); // nothing stored yet
            state.GallerySize = GalleryCellSize.Large;
            await store.WhenSavedAsync();
            Assert.Contains("\"GallerySize\": \"Large\"", await File.ReadAllTextAsync(paths.AppStateFile));

            var restarted = ThreadViewState.Create(new AppStateStore(paths), new CommandLineOptions());
            await restarted.GallerySizeLoaded;
            Assert.Equal(GalleryCellSize.Large, restarted.GallerySize);

            // Screenshot and sample-data runs keep it in memory: the stored size is neither read nor written.
            var sandboxStore = new AppStateStore(paths);
            var sandboxed = ThreadViewState.Create(sandboxStore, new CommandLineOptions { SampleData = true });
            await sandboxed.GallerySizeLoaded;
            Assert.Equal(GalleryCellSize.Medium, sandboxed.GallerySize);
            sandboxed.GallerySize = GalleryCellSize.Small;
            await sandboxStore.WhenSavedAsync();
            Assert.Contains("\"GallerySize\": \"Large\"", await File.ReadAllTextAsync(paths.AppStateFile));

            // A broken value falls back to the default.
            await File.WriteAllTextAsync(paths.AppStateFile, "{ \"GallerySize\": \"Huge\" }");
            var broken = ThreadViewState.Create(new AppStateStore(paths), new CommandLineOptions());
            await broken.GallerySizeLoaded;
            Assert.Equal(GalleryCellSize.Medium, broken.GallerySize);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ selection and keyboard order

    [Fact]
    public void Select_all_shift_click_and_the_viewer_follow_the_Gallery_order() => RunOnDispatcher(async () =>
    {
        var today = Local(DateTime.Today);
        LibraryItem[] items =
        [
            Photo(1, "d.jpg", today.AddHours(8)),
            Photo(2, "b.jpg", today.AddHours(9)),
            Photo(3, "a.jpg", today.AddHours(10)),
            Photo(4, "c.jpg", today.AddHours(11)),
        ];
        var (vm, host, _) = await OpenGalleryAsync(items);
        vm.SortGalleryBy(GallerySortKey.Name);
        var byName = vm.GalleryRows.OfType<GalleryCellRow>().SelectMany(r => r.Items).ToList();
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg", "d.jpg"], byName.Select(i => i.FileName));

        vm.SelectAll();
        Assert.Equal(byName, vm.SelectedItems);

        // Shift-click selects the range as shown (a…c), not as added (where c is newest and a sits between).
        vm.ClickItem(byName[0], ctrl: false, shift: false);
        vm.ClickItem(byName[2], ctrl: false, shift: true);
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], vm.SelectedItems.Select(i => i.FileName).Order());

        // The viewer steps through the files in the Gallery's order.
        vm.OpenInViewer(byName[1]);
        Assert.Equal(byName, host.ViewerItems);
        Assert.Equal(1, host.ViewerIndex);
        vm.Dispose();
    });

    [Fact]
    public void Arrow_keys_move_between_cells_by_row_and_column_across_sections() => RunOnDispatcher(async () =>
    {
        // Today: 5 files (rows of 3 and 2); yesterday: 3 files (one row).
        var (vm, _, _) = await OpenGalleryAsync([.. Day(0, 5), .. Day(1, 3)]);
        vm.SetGalleryColumns(3);
        var rows = vm.GalleryRows.OfType<GalleryCellRow>().Select(r => r.Items).ToList();
        Assert.Equal([3, 2, 3], rows.Select(r => r.Count));
        ThreadItemViewModel Cell(int row, int column) => rows[row][column];

        // The first press selects the cell to start from (the first on screen), without moving.
        Assert.Same(Cell(0, 1), vm.MoveGalleryCursor(GalleryMove.Right, start: Cell(0, 1)));
        Assert.Equal([Cell(0, 1)], vm.SelectedItems);

        Assert.Same(Cell(0, 2), vm.MoveGalleryCursor(GalleryMove.Right));
        Assert.Same(Cell(1, 0), vm.MoveGalleryCursor(GalleryMove.Right)); // wraps to the next row
        Assert.Same(Cell(0, 2), vm.MoveGalleryCursor(GalleryMove.Left));
        Assert.Same(Cell(1, 1), vm.MoveGalleryCursor(GalleryMove.Down)); // a shorter row: its last cell
        Assert.Same(Cell(2, 1), vm.MoveGalleryCursor(GalleryMove.Down)); // across the day header, same column
        Assert.Same(Cell(2, 1), vm.MoveGalleryCursor(GalleryMove.Down)); // the last row: stays
        Assert.Same(Cell(0, 1), vm.MoveGalleryCursor(GalleryMove.Up, count: 5)); // Page Up stops at the first row
        Assert.Same(Cell(0, 0), vm.MoveGalleryCursor(GalleryMove.First));
        Assert.Same(Cell(0, 0), vm.MoveGalleryCursor(GalleryMove.Left)); // the first cell: stays
        Assert.Same(Cell(2, 2), vm.MoveGalleryCursor(GalleryMove.Last));
        Assert.Equal([Cell(2, 2)], vm.SelectedItems);

        // Shift extends from the anchor, in the Gallery's order.
        vm.MoveGalleryCursor(GalleryMove.First);
        vm.MoveGalleryCursor(GalleryMove.Right, extend: true);
        vm.MoveGalleryCursor(GalleryMove.Down, extend: true);
        Assert.Equal([Cell(0, 0), Cell(0, 1), Cell(0, 2), Cell(1, 0), Cell(1, 1)], vm.SelectedItems.OrderBy(i => IndexIn(rows, i)));
        Assert.True(vm.IsSelectionActive);

        // A click moves the cursor too.
        vm.ClickItem(Cell(2, 0), ctrl: false, shift: false);
        Assert.Same(Cell(2, 1), vm.MoveGalleryCursor(GalleryMove.Right));
        vm.Dispose();
    });

    // ------------------------------------------------------------------ the print frame shows the mode

    [Fact]
    public void The_print_frame_is_green_for_copies_and_blue_for_pointers_and_follows_a_live_change() => RunOnDispatcher(async () =>
    {
        var today = Local(DateTime.Today);
        var linked = Photo(1, "linked.jpg", today.AddHours(8)) with { Mode = ItemMode.Pointer, FullPath = @"D:\Photos\linked.jpg" };
        var (vm, _, fake) = await OpenGalleryAsync([linked, Photo(2, "copied.jpg", today.AddHours(9))]);
        var light = LoadComponent<ResourceDictionary>("Themes/Light.xaml");
        var templates = LoadComponent<ResourceDictionary>("Views/Thread/ThreadTemplates.xaml");
        var pointer = vm.Items.Single(i => i.Id == 1);
        var copy = vm.Items.Single(i => i.Id == 2);

        var pointerPrint = RenderPrint(pointer, light, templates);
        var copyPrint = RenderPrint(copy, light, templates);
        Assert.Same(light["Cab.GalleryFramePointer"], pointerPrint.FrameBrush);
        Assert.Same(light["Cab.GalleryFrameCopy"], copyPrint.FrameBrush);
        Assert.NotEqual(((SolidColorBrush)light["Cab.GalleryFramePointer"]).Color, ((SolidColorBrush)light["Cab.GalleryFrameCopy"]).Color);

        // "Migrate pointers to copies" turns the pointer into a copy: the frame on screen turns green.
        fake.Set([linked with { Mode = ItemMode.Copy, FullPath = @"C:\Vault\Images\linked.jpg" }, Photo(2, "copied.jpg", today.AddHours(9))]);
        fake.RaiseChanged();
        await WaitUntilAsync(() => !pointer.IsPointer, "the file becomes a copy");
        Assert.Same(pointer, vm.Items.Single(i => i.Id == 1)); // the same cell, updated in place
        pointerPrint.UpdateLayout();
        Assert.Same(light["Cab.GalleryFrameCopy"], pointerPrint.FrameBrush);

        // And back: a copy that becomes a pointer turns blue.
        fake.Set([linked with { Mode = ItemMode.Copy, FullPath = @"C:\Vault\Images\linked.jpg" }, Photo(2, "copied.jpg", today.AddHours(9)) with { Mode = ItemMode.Pointer }]);
        fake.RaiseChanged();
        await WaitUntilAsync(() => copy.IsPointer, "the copy becomes a pointer");
        copyPrint.UpdateLayout();
        Assert.Same(light["Cab.GalleryFramePointer"], copyPrint.FrameBrush);

        // Other files have no frame at all; the dark theme defines the same keys (and no longer the plain frame).
        Assert.Null(FindVisual<GalleryPrint>(Render(vm.Items.Single(i => i.Id == 2), "GalleryIcon", light, templates)));
        var dark = LoadComponent<ResourceDictionary>("Themes/Dark.xaml");
        Assert.Equal(light.Keys.Cast<object>().Select(k => k.ToString()).Order(), dark.Keys.Cast<object>().Select(k => k.ToString()).Order());
        Assert.False(light.Contains("Cab.GalleryFrame"));
        Assert.IsType<SolidColorBrush>(dark["Cab.GalleryFrameCopy"]);
        Assert.IsType<SolidColorBrush>(dark["Cab.GalleryFramePointer"]);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ live updates and the tooltip

    [Fact]
    public void Files_added_or_changed_elsewhere_show_up_in_the_Gallery() => RunOnDispatcher(async () =>
    {
        var today = Local(DateTime.Today);
        var (vm, _, fake) = await OpenGalleryAsync([Photo(1, "one.jpg", today.AddHours(8)), Photo(2, "two.jpg", today.AddHours(9))]);
        vm.SetGalleryDateKind(GalleryDateKind.Taken);

        fake.Set([Photo(1, "one.jpg", today.AddHours(8), taken: today.AddDays(-9)), Photo(2, "two.jpg", today.AddHours(9)), Photo(3, "three.jpg", today.AddHours(10))]);
        fake.RaiseChanged();
        await WaitUntilAsync(() => vm.TotalCount == 3 && vm.GalleryRows.OfType<GalleryHeaderRow>().Count() == 2, "the new file and the new date taken arrive");
        Assert.Equal(["three.jpg", "two.jpg", "one.jpg"], Names(vm));
        Assert.Equal(today.AddDays(-9), vm.Items.Single(i => i.Id == 1).TakenAt);
        vm.Dispose();
    });

    [Fact]
    public void The_tooltip_has_the_name_size_date_added_and_date_taken() => WithCulture(() =>
    {
        var owner = DetachedOwner();
        var added = new DateTimeOffset(2026, 10, 24, 9, 39, 0, TimeSpan.Zero);
        var taken = new DateTimeOffset(2026, 9, 12, 17, 30, 0, TimeSpan.Zero);
        string Tip(LibraryItem item) => Vm(owner, item).GalleryToolTipText;
        string Short(DateTimeOffset value) => ThreadFormat.DateTimeShort(value);

        // A date taken is shown as the camera's wall-clock time (date-taken bug hunt F1); file dates in this PC's zone.
        Assert.Equal($"IMG_2041.jpg\n{ThreadFormat.Size(2 * 1024 * 1024)}\nDate added: {Short(added)}\nDate taken: {ThreadFormat.WallClockShort(taken)}",
            Tip(Photo(1, "IMG_2041.jpg", added, size: 2 * 1024 * 1024, taken: taken)));
        Assert.Equal($"shot.png\n{ThreadFormat.Size(1024)}\nDate added: {Short(added)}\nDate taken: not in the file (modified {Short(taken)})",
            Tip(Photo(2, "shot.png", added, size: 1024, modified: taken)));
        Assert.Equal($"x.jpg\n{ThreadFormat.Size(1024)}\nDate added: {Short(added)}\nDate taken: not in the file\n{L.Get("Thread_MissingTooltip")}",
            Tip(Photo(3, "x.jpg", added, size: 1024) with { IsMissing = true }));
    });

    // ------------------------------------------------------------------ helpers

    internal const long CategoryId = 3; // the sample library's built-in Images

    private static ThreadViewModel Create(ILibraryService library, ThreadViewState state, IThreadViewHost? host = null)
    {
        var vm = new ThreadViewModel(new ThreadServices(
            library,
            NullProxy.Create<IShellContextMenuService>(),
            NullProxy.Create<IShellOperations>(),
            NullProxy.Create<IFileIntake>(),
            NullProxy.Create<IDialogService>(),
            new NotificationService(),
            new ShellNavigator(),
            new ThumbnailLoader(NullProxy.Create<IThumbnailService>(), NullProxy.Create<IMediaInfoService>()),
            state));
        if (host is not null)
        {
            vm.AttachHost(host);
        }

        return vm;
    }

    /// <summary>The sample library's Images thread listing exactly <paramref name="items"/>, opened in the Gallery.</summary>
    private static async Task<(ThreadViewModel Vm, ViewerHost Host, GalleryLibrary Library)> OpenGalleryAsync(IEnumerable<LibraryItem> items, ThreadViewState? state = null)
    {
        var sample = SampleLibraryService.Create(withVault: true);
        var images = (await sample.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        Assert.Equal(CategoryId, images.Id);
        var library = GalleryLibrary.Wrap(sample, images.Id, items, out var fake);
        var host = new ViewerHost();
        var vm = Create(library, state ?? new ThreadViewState(), host);
        await vm.LoadAsync(images.Id);
        Assert.Equal(ThreadViewMode.Gallery, vm.ViewMode);
        return (vm, host, fake);
    }

    private static T LoadComponent<T>(string path) => (T)Application.LoadComponent(new Uri("/Cabinette;component/" + path, UriKind.Relative));

    /// <summary>The item through one of ThreadTemplates.xaml's templates as checked in, laid out in a cell's photo box (never shown).</summary>
    private static ContentPresenter Render(ThreadItemViewModel item, string templateKey, ResourceDictionary theme, ResourceDictionary templates)
    {
        var presenter = new ContentPresenter { Content = item, ContentTemplate = (DataTemplate)templates[templateKey] };
        var root = new Border { Width = 128, Height = 128, Child = presenter };
        root.Resources.MergedDictionaries.Add(theme);
        root.Resources.MergedDictionaries.Add(templates);
        root.Measure(new Size(128, 128));
        root.Arrange(new Rect(0, 0, 128, 128));
        root.UpdateLayout();
        return presenter;
    }

    private static GalleryPrint RenderPrint(ThreadItemViewModel item, ResourceDictionary theme, ResourceDictionary templates)
        => FindVisual<GalleryPrint>(Render(item, "GalleryMedia", theme, templates)) ?? throw new InvalidOperationException("No print.");

    private static T? FindVisual<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisual<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static List<string> Names(ThreadViewModel vm) => vm.GalleryRows.OfType<GalleryCellRow>().SelectMany(r => r.Items).Select(i => i.FileName).ToList();

    private static int IndexIn(List<IReadOnlyList<ThreadItemViewModel>> rows, ThreadItemViewModel item)
        => rows.SelectMany(r => r).ToList().IndexOf(item);

    /// <summary><paramref name="count"/> photos added <paramref name="daysAgo"/> days ago, an hour apart (ids from daysAgo × 100).</summary>
    private static List<LibraryItem> Day(int daysAgo, int count)
    {
        var day = Local(DateTime.Today.AddDays(-daysAgo));
        return Enumerable.Range(0, count).Select(i => Photo((daysAgo * 100) + i + 1, $"d{daysAgo}-{i}.jpg", day.AddHours(1 + i))).ToList();
    }

    internal static DateTimeOffset Local(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Local));

    internal static LibraryItem Photo(long id, string name, DateTimeOffset added, long size = 1000, DateTimeOffset? taken = null, DateTimeOffset? modified = null)
    {
        var extension = ExtensionMap.Normalize(Path.GetExtension(name));
        return new LibraryItem
        {
            Id = id,
            Mode = ItemMode.Copy,
            Kind = ItemKind.File,
            FileName = name,
            Extension = extension,
            FullPath = $@"C:\Vault\Images\{name}",
            HomeCategoryId = CategoryId,
            CategoryIds = [CategoryId],
            AddedAt = added,
            SizeBytes = size,
            TakenAt = taken,
            FileModifiedAt = modified,
            TypeKey = ExtensionMap.GetTypeKey(extension),
        };
    }

    /// <summary>An owner for item view models in tests that need no thread (nothing is loaded).</summary>
    internal static ThreadViewModel DetachedOwner() => Create(NullProxy.Create<ILibraryService>(), new ThreadViewState());

    internal static ThreadItemViewModel Vm(ThreadViewModel owner, LibraryItem item) => new(item, owner);

    /// <summary>Runs with English (UK) dates, as the strings in the tests are written.</summary>
    internal static void WithCulture(Action test)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        try
        {
            test();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(25);
        }
    }

    internal static void RunOnDispatcher(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "The test body did not finish.");
        failure?.Throw();
    }

    /// <summary>Records what the thread asks of its view: the viewer's list and start.</summary>
    private sealed class ViewerHost : IThreadViewHost
    {
        public IReadOnlyList<ThreadItemViewModel> ViewerItems { get; private set; } = [];

        public int ViewerIndex { get; private set; } = -1;

        public IntPtr OwnerHandle => IntPtr.Zero;

        public void ScrollToEnd()
        {
        }

        public void NotifyNewItems(int count)
        {
        }

        public void BringItemIntoView(ThreadItemViewModel item)
        {
        }

        public bool ShowTagEditor(TagEditorViewModel editor) => false;

        public void ShowMediaViewer(IReadOnlyList<ThreadItemViewModel> items, int index)
        {
            ViewerItems = items;
            ViewerIndex = index;
        }
    }

    /// <summary>
    /// Forwards to another library (categories, paths, settings), but lists its own items as the thread of one category and
    /// raises <see cref="ILibraryService.Changed"/> for it when the test says so.
    /// </summary>
    public class GalleryLibrary : DispatchProxy
    {
        private readonly Lock _gate = new();
        private List<LibraryItem> _items = [];
        private ILibraryService _inner = null!;
        private long _categoryId;
        private EventHandler<LibraryChangedEventArgs>? _changed;

        public static ILibraryService Wrap(ILibraryService inner, long categoryId, IEnumerable<LibraryItem> items)
            => Wrap(inner, categoryId, items, out _);

        public static ILibraryService Wrap(ILibraryService inner, long categoryId, IEnumerable<LibraryItem> items, out GalleryLibrary fake)
        {
            var proxy = Create<ILibraryService, GalleryLibrary>();
            fake = (GalleryLibrary)(object)proxy;
            fake._inner = inner;
            fake._categoryId = categoryId;
            fake._items = [.. items];
            return proxy;
        }

        public void Set(IEnumerable<LibraryItem> items)
        {
            lock (_gate)
            {
                _items = [.. items];
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
                    lock (_gate)
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
}

/// <summary>
/// The Gallery stays smooth with tens of thousands of files (SPEC.md section 4): its rows follow from the item count alone, so
/// sorting and cutting 25,000 files into rows takes tens of milliseconds. Timed, so it never runs in parallel with other tests.
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class GalleryPerformanceTests(ITestOutputHelper output)
{
    private const int Count = 25_000;

    [Fact]
    public void Rows_for_25000_files_are_built_in_tens_of_milliseconds_for_every_sort() => GalleryTests.WithCulture(() =>
    {
        var owner = GalleryTests.DetachedOwner();
        var items = Files().Select(item => GalleryTests.Vm(owner, item)).ToList();
        var today = DateTime.Today;
        GallerySort[] sorts =
        [
            GallerySort.Default,
            new(GallerySortKey.Date, true, GalleryDateKind.Taken),
            new(GallerySortKey.Name, true, GalleryDateKind.Added),
            new(GallerySortKey.Size, false, GalleryDateKind.Taken),
            new(GallerySortKey.Type, true, GalleryDateKind.Added),
        ];

        foreach (var sort in sorts)
        {
            var best = Best(() =>
            {
                var order = GalleryBuilder.Order(items, sort, out var days);
                var rows = GalleryBuilder.BuildRows(order, days, 6, today);
                Assert.Equal(Count, rows.OfType<GalleryCellRow>().Sum(r => r.Items.Count));
            });
            output.WriteLine($"{sort}: {best.TotalMilliseconds:0.0} ms");
            Assert.True(best < TimeSpan.FromMilliseconds(150), $"Sorting and building the rows of {Count} files by {sort} took {best.TotalMilliseconds:0} ms.");
        }

        // A new width or cell size only cuts the rows again.
        var sorted = GalleryBuilder.Order(items, GallerySort.Default, out var sortedDays);
        var chunk = Best(() => GalleryBuilder.BuildRows(sorted, sortedDays, 8, today));
        output.WriteLine($"rows only: {chunk.TotalMilliseconds:0.0} ms");
        Assert.True(chunk < TimeSpan.FromMilliseconds(30), $"Cutting {Count} files into rows took {chunk.TotalMilliseconds:0} ms.");
    });

    [Fact]
    public void A_thread_of_25000_files_resorts_and_reflows_quickly_on_the_UI_thread() => GalleryTests.RunOnDispatcher(async () =>
    {
        var sample = Cabinette.App.DesignTime.SampleLibraryService.Create(withVault: true);
        var library = GalleryTests.GalleryLibrary.Wrap(sample, GalleryTests.CategoryId, Files());
        using var vm = new ThreadViewModel(new ThreadServices(
            library,
            NullProxy.Create<Cabinette.Windows.IShellContextMenuService>(),
            NullProxy.Create<Cabinette.Windows.IShellOperations>(),
            NullProxy.Create<Cabinette.App.Services.IFileIntake>(),
            NullProxy.Create<Cabinette.App.Services.IDialogService>(),
            new Cabinette.App.Services.NotificationService(),
            new Cabinette.App.Services.ShellNavigator(),
            new ThumbnailLoader(NullProxy.Create<Cabinette.Windows.IThumbnailService>(), NullProxy.Create<Cabinette.Windows.IMediaInfoService>()),
            new ThreadViewState()));
        await vm.LoadAsync(GalleryTests.CategoryId);
        Assert.Equal(ThreadViewMode.Gallery, vm.ViewMode);
        Assert.Equal(Count, vm.TotalCount);

        // A new width or cell size gives a new column count: the rows are cut again (no sorting); a sort header click resorts.
        var columns = 6;
        var reflow = Best(() =>
        {
            columns = columns == 6 ? 9 : 6;
            vm.SetGalleryColumns(columns);
            Assert.Equal(columns, vm.GalleryRows.OfType<GalleryCellRow>().Max(r => r.Items.Count));
        });
        var resort = Best(() => vm.SortGalleryBy(GallerySortKey.Name));
        output.WriteLine($"reflow {reflow.TotalMilliseconds:0.0} ms, resort {resort.TotalMilliseconds:0.0} ms");
        Assert.True(reflow < TimeSpan.FromMilliseconds(60), $"Reflowing {Count} files took {reflow.TotalMilliseconds:0} ms.");
        Assert.True(resort < TimeSpan.FromMilliseconds(200), $"Resorting {Count} files took {resort.TotalMilliseconds:0} ms.");
        Assert.Equal(Count, vm.GalleryRows.OfType<GalleryCellRow>().Sum(r => r.Items.Count));
    });

    /// <summary>25,000 photos and videos over two years, with and without a date taken or a file date.</summary>
    private static List<LibraryItem> Files()
    {
        var random = new Random(Count);
        var now = GalleryTests.Local(DateTime.Today.AddHours(12));
        string[] extensions = ["jpg", "jpeg", "png", "heic", "mp4", "gif"];
        return Enumerable.Range(1, Count).Select(i => GalleryTests.Photo(
            i,
            $"IMG_{random.Next(100_000):00000} {i}.{extensions[i % extensions.Length]}",
            now.AddMinutes(-random.Next(0, 2 * 365 * 24 * 60)),
            size: random.Next(1, 20_000_000),
            taken: i % 3 == 0 ? null : now.AddMinutes(-random.Next(0, 5 * 365 * 24 * 60)),
            modified: i % 6 == 0 ? now.AddDays(-random.Next(0, 900)) : null)).ToList();
    }

    /// <summary>The fastest of a few runs after a warm-up (the first run pays for the JIT).</summary>
    private static TimeSpan Best(Action action)
    {
        action();
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            action();
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        return best;
    }
}
