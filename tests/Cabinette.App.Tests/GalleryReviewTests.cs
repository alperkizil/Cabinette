using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt of the Gallery view (docs/review/bughunt-2026-10-24-gallery): each F test fails on feat/gallery because of the
/// finding it describes and passes once it is fixed. The view tests build the real ThreadView in a hidden window (never
/// shown); they take the keyboard focus, so they never run in parallel with other keyboard tests (the MotionTiming collection).
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class GalleryReviewTests
{
    /// <summary>
    /// F1: a sort click in the Gallery asks the view to "scroll to the end" (ApplyGallerySort → IThreadViewHost.ScrollToEnd), which
    /// the view implements as "scroll the Gallery to its top" but also as "stick the chat to its bottom and scroll it there". So a
    /// user who was reading older files in the chat, switched to the Gallery and sorted it, comes back to the chat at its very end.
    /// The Grid has no such side effect: a trip to the Grid and back keeps the place.
    /// </summary>
    [Fact]
    public void F1_a_sort_click_in_the_Gallery_loses_the_place_in_the_chat() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync(GalleryReviewSupport.Files(300));
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);

        vm.IsChatView = true;
        await GalleryReviewSupport.SettleAsync(view);
        var chat = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.ChatList)!;
        Assert.True(chat.ScrollableHeight > 100, $"scrollable {chat.ScrollableHeight}");
        chat.ScrollToTop();
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Equal(0, chat.VerticalOffset);

        // Baseline: a trip to the Grid and back keeps the place in the chat.
        vm.IsGridView = true;
        await GalleryReviewSupport.SettleAsync(view);
        vm.IsChatView = true;
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Equal(0, chat.VerticalOffset);

        // A trip to the Gallery with a sort click: the Gallery goes to its top, the chat should stay where it was.
        vm.IsGalleryView = true;
        await GalleryReviewSupport.SettleAsync(view);
        vm.SortGalleryBy(GallerySortKey.Name);
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Equal(0, GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.GalleryList)!.VerticalOffset);
        vm.IsChatView = true;
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Equal(0, chat.VerticalOffset);
        vm.Dispose();
    });

    /// <summary>
    /// F2: the branch frames each print in its mode's color (Cab.GalleryFrameCopy, green; Cab.GalleryFramePointer, blue) and
    /// removed Cab.GalleryFrame, but SPEC.md (the source of truth) still says "a thin light frame" (section 4) and lists
    /// "Gallery print frame #FFFFFF / #B9B4AA" (section 11). The next agent that reads the SPEC builds the wrong thing.
    /// Passes once section 11 lists the four frame colors the themes define and the old dimmed value is gone.
    /// </summary>
    [Fact]
    public void F2_SPEC_still_describes_a_white_print_frame()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var spec = File.ReadAllText(Path.Combine(root, "SPEC.md"));
        var lookAndFeel = spec[spec.IndexOf("## 11. Look and feel", StringComparison.Ordinal)..];
        lookAndFeel = lookAndFeel[..lookAndFeel.IndexOf("### Motion", StringComparison.Ordinal)];

        foreach (var theme in new[] { "Light.xaml", "Dark.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(root, "src", "Cabinette.App", "Themes", theme));
            foreach (var key in new[] { "Cab.GalleryFrameCopy", "Cab.GalleryFramePointer" })
            {
                var color = Regex.Match(xaml, $"x:Key=\"{Regex.Escape(key)}\" Color=\"(#[0-9A-Fa-f]{{6}})\"").Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(color), $"{theme} defines {key}");
                Assert.True(lookAndFeel.Contains(color, StringComparison.OrdinalIgnoreCase), $"SPEC.md section 11 lists {key}'s {theme} color {color}");
            }
        }

        Assert.DoesNotContain("#B9B4AA", lookAndFeel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thin light frame", spec, StringComparison.Ordinal);
    }
}

/// <summary>Leads of the same bug hunt that held: kept as regression coverage of the Gallery through the real view.</summary>
[Collection(MotionTiming.Name)]
public sealed class GalleryReviewChecks
{
    [Fact]
    public void A_gallery_of_25000_files_realizes_only_the_rows_on_screen_and_draws_no_effects() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync(GalleryReviewSupport.Files(25_000));
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);

        var panel = GalleryReviewSupport.FindVisualChild<VirtualizingStackPanel>(view.GalleryList);
        Assert.NotNull(panel);
        var rows = vm.GalleryRows.Count;
        Assert.True(rows > 4000, $"rows {rows}");
        Assert.Equal(6, vm.GalleryRows.OfType<GalleryCellRow>().Max(r => r.Items.Count));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(view.GalleryList));
        Assert.True(panel.Children.Count < 60, $"realized {panel.Children.Count} of {rows}");
        Assert.All(GalleryReviewSupport.FindVisuals<UIElement>(panel), element => Assert.Null(element.Effect));

        // A print per photo, asking for a thumbnail of the cell's size (the loader doubles it for high-DPI screens).
        var print = GalleryReviewSupport.FindVisualChild<GalleryPrint>(panel);
        Assert.NotNull(print);
        Assert.Equal(128, GalleryReviewSupport.FindVisualChild<AsyncThumbnail>(print)!.RequestSize);
        Assert.False(print.PrintBounds.IsEmpty);

        // After a trip to the end and back, still only a screen's worth of rows exists.
        var scroll = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.GalleryList)!;
        scroll.ScrollToEnd();
        await GalleryReviewSupport.SettleAsync(view);
        scroll.ScrollToTop();
        await GalleryReviewSupport.SettleAsync(view);
        Assert.True(panel.Children.Count < 60, $"realized {panel.Children.Count} after scrolling");
        vm.Dispose();
    });

    [Fact]
    public void Arrow_Home_and_End_keys_reach_the_view_model_through_the_view() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync([.. GalleryReviewSupport.Day(0, 7), .. GalleryReviewSupport.Day(1, 5)]);
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        var cells = vm.GalleryRows.OfType<GalleryCellRow>().Select(r => r.Items).ToList();
        Assert.Equal([6, 1, 5], cells.Select(c => c.Count));

        Assert.True(view.GalleryList.Focus());
        Assert.True(view.GalleryList.IsKeyboardFocusWithin);
        GalleryReviewSupport.Press(view, source, Key.Right);
        Assert.Equal([cells[0][0]], vm.SelectedItems);
        GalleryReviewSupport.Press(view, source, Key.Right);
        Assert.Equal([cells[0][1]], vm.SelectedItems);
        GalleryReviewSupport.Press(view, source, Key.Down);
        Assert.Equal([cells[1][0]], vm.SelectedItems);
        GalleryReviewSupport.Press(view, source, Key.Down);
        Assert.Equal([cells[2][0]], vm.SelectedItems);
        GalleryReviewSupport.Press(view, source, Key.End);
        Assert.Equal([cells[2][4]], vm.SelectedItems);
        GalleryReviewSupport.Press(view, source, Key.Home);
        Assert.Equal([cells[0][0]], vm.SelectedItems);
        vm.Dispose();
    });

    [Fact]
    public void Focusing_a_file_and_the_End_key_scroll_the_gallery_to_it() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync(GalleryReviewSupport.Files(2000));
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        var scroll = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.GalleryList)!;
        Assert.Equal(0, scroll.VerticalOffset);

        var target = vm.GalleryRows.OfType<GalleryCellRow>().ElementAt(150).Items[0];
        vm.FocusItem(target.Id);
        await GalleryReviewSupport.SettleAsync(view);
        Assert.True(scroll.VerticalOffset > 1000, $"offset {scroll.VerticalOffset}");
        Assert.True(target.IsHighlighted);

        Assert.True(view.GalleryList.Focus());
        GalleryReviewSupport.Press(view, source, Key.End);
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Equal([vm.GalleryRows.OfType<GalleryCellRow>().Last().Items[^1]], vm.SelectedItems);
        Assert.True(scroll.VerticalOffset >= scroll.ScrollableHeight - 1, $"offset {scroll.VerticalOffset} of {scroll.ScrollableHeight}");
        vm.Dispose();
    });

    [Fact]
    public void The_thread_search_filters_the_gallery_and_clearing_it_restores_the_sections() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync([.. GalleryReviewSupport.Day(0, 3), .. GalleryReviewSupport.Day(1, 2)]);
        vm.IsSearchOpen = true;
        vm.SearchText = "d1-";
        await GalleryReviewSupport.WaitUntilAsync(() => vm.TotalCount == 2, "the search applies");
        Assert.Equal(["d1-0.jpg", "d1-1.jpg"], GalleryReviewSupport.Names(vm).Order());
        Assert.Single(vm.GalleryRows.OfType<GalleryHeaderRow>());

        vm.SearchText = "nothing";
        await GalleryReviewSupport.WaitUntilAsync(() => vm.TotalCount == 0, "nothing matches");
        Assert.True(vm.IsNoResults);
        Assert.Empty(vm.GalleryRows);

        vm.IsSearchOpen = false;
        await GalleryReviewSupport.WaitUntilAsync(() => vm.TotalCount == 5, "the search is cleared");
        Assert.Equal(2, vm.GalleryRows.OfType<GalleryHeaderRow>().Count());
        vm.Dispose();
    });

    [Fact]
    public void The_keyboard_cursor_recovers_when_its_file_leaves_the_thread() => GalleryTests.RunOnDispatcher(async () =>
    {
        var items = GalleryReviewSupport.Day(0, 6);
        var (vm, fake) = await GalleryReviewSupport.OpenAsync(items);
        vm.SetGalleryColumns(3);
        var rows = vm.GalleryRows.OfType<GalleryCellRow>().Select(r => r.Items).ToList();
        var cursor = rows[0][1];
        vm.MoveGalleryCursor(GalleryMove.Right, start: cursor);
        Assert.Equal([cursor], vm.SelectedItems);

        fake.Set(items.Where(i => i.Id != cursor.Id));
        fake.RaiseChanged();
        await GalleryReviewSupport.WaitUntilAsync(() => vm.TotalCount == 5, "the file is gone");
        Assert.Empty(vm.SelectedItems);

        var next = vm.MoveGalleryCursor(GalleryMove.Down);
        Assert.NotNull(next);
        Assert.Contains(next, vm.Items);
        Assert.Equal([next], vm.SelectedItems);
        vm.Dispose();
    });

    [Fact]
    public void Files_arriving_live_keep_the_name_order_and_unique_row_keys() => GalleryTests.RunOnDispatcher(async () =>
    {
        var today = GalleryTests.Local(DateTime.Today);
        var (vm, fake) = await GalleryReviewSupport.OpenAsync([GalleryTests.Photo(1, "b.jpg", today.AddHours(8)), GalleryTests.Photo(2, "d.jpg", today.AddHours(9))]);
        vm.SortGalleryBy(GallerySortKey.Name);
        Assert.Equal(["b.jpg", "d.jpg"], GalleryReviewSupport.Names(vm));
        fake.Set([GalleryTests.Photo(1, "b.jpg", today.AddHours(8)), GalleryTests.Photo(2, "d.jpg", today.AddHours(9)), GalleryTests.Photo(3, "c.jpg", today.AddHours(10)), GalleryTests.Photo(4, "a.jpg", today.AddDays(-1))]);
        fake.RaiseChanged();
        await GalleryReviewSupport.WaitUntilAsync(() => vm.TotalCount == 4, "the files arrive");
        Assert.Equal(["b.jpg", "c.jpg", "d.jpg", "a.jpg"], GalleryReviewSupport.Names(vm));
        Assert.Equal(vm.GalleryRows.Count, vm.GalleryRows.Select(r => r.Key).Distinct().Count());
        vm.Dispose();
    });

    [Fact]
    public void Frames_show_the_mode_after_recycling_and_follow_a_live_theme_switch() => GalleryTests.RunOnDispatcher(async () =>
    {
        var items = GalleryReviewSupport.Files(1200).Select(i => i.Id % 3 == 0 ? i with { Mode = ItemMode.Pointer, FullPath = $@"D:\Photos\{i.FileName}" } : i).ToList();
        var (vm, _) = await GalleryReviewSupport.OpenAsync(items);
        var view = new ThreadView { DataContext = vm };
        var light = GalleryReviewSupport.LoadComponent<ResourceDictionary>("Themes/Light.xaml");
        view.Resources.MergedDictionaries.Add(light);
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        var scroll = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.GalleryList)!;

        CheckFrames(view, light);
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
        await GalleryReviewSupport.SettleAsync(view);
        CheckFrames(view, light);
        scroll.ScrollToEnd();
        await GalleryReviewSupport.SettleAsync(view);
        CheckFrames(view, light);
        scroll.ScrollToTop();
        await GalleryReviewSupport.SettleAsync(view);
        CheckFrames(view, light);

        var dark = GalleryReviewSupport.LoadComponent<ResourceDictionary>("Themes/Dark.xaml");
        view.Resources.MergedDictionaries[view.Resources.MergedDictionaries.IndexOf(light)] = dark;
        await GalleryReviewSupport.SettleAsync(view);
        CheckFrames(view, dark);
        vm.Dispose();
    });

    [Fact]
    public void A_cell_size_step_keeps_the_top_row_and_the_first_arrow_starts_on_screen() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync(GalleryReviewSupport.Files(2000));
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        var scroll = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.GalleryList)!;
        scroll.ScrollToVerticalOffset(4000);
        await GalleryReviewSupport.SettleAsync(view);
        var before = FirstVisibleRow(view)!.Items[0];

        vm.GalleryCellSize = GalleryCellSize.Large;
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Contains(before, FirstVisibleRow(view)!.Items);

        vm.GalleryCellSize = GalleryCellSize.Small;
        await GalleryReviewSupport.SettleAsync(view);
        Assert.Contains(before, FirstVisibleRow(view)!.Items);

        Assert.True(view.GalleryList.Focus());
        GalleryReviewSupport.Press(view, source, Key.Right);
        Assert.Equal([FirstVisibleRow(view)!.Items[0]], vm.SelectedItems);
        vm.Dispose();
    });

    [Fact]
    public void Clicks_select_cells_and_the_space_between_them_clears() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync([.. GalleryReviewSupport.Day(0, 4), .. GalleryReviewSupport.Day(1, 2)]);
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        var cells = vm.GalleryRows.OfType<GalleryCellRow>().Select(r => r.Items).ToList();
        var panel = GalleryReviewSupport.FindVisualChild<VirtualizingStackPanel>(view.GalleryList)!;

        var name = GalleryReviewSupport.FindVisuals<TextBlock>(panel).Single(t => t.Text == cells[0][1].FileName);
        Click(name, MouseButton.Left);
        Assert.Equal([cells[0][1]], vm.SelectedItems);

        var tile = GalleryReviewSupport.FindVisuals<Grid>(panel).Single(g => g.Name == "Tile" && ReferenceEquals(g.DataContext, cells[0][2]));
        Click((UIElement)VisualTreeHelper.GetParent(tile), MouseButton.Left);
        Assert.Empty(vm.SelectedItems);

        var other = GalleryReviewSupport.FindVisuals<TextBlock>(panel).Single(t => t.Text == cells[1][0].FileName);
        Click(other, MouseButton.Right);
        Assert.Equal([cells[1][0]], vm.SelectedItems);
        vm.Dispose();
    });

    [Fact]
    public void Dates_far_in_the_past_or_future_and_missing_file_dates_sort_and_label_without_error() => GalleryTests.RunOnDispatcher(async () =>
    {
        var today = GalleryTests.Local(DateTime.Today);
        LibraryItem[] items =
        [
            GalleryTests.Photo(1, "future.jpg", today.AddHours(9), taken: today.AddDays(3).AddHours(10)),
            GalleryTests.Photo(2, "added only.jpg", today.AddHours(10)),
            GalleryTests.Photo(3, "old camera.jpg", today.AddHours(11), taken: new DateTimeOffset(1980, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            GalleryTests.Photo(4, "epoch.jpg", today.AddHours(12), taken: DateTimeOffset.MinValue, modified: DateTimeOffset.MaxValue),
        ];
        var (vm, _) = await GalleryReviewSupport.OpenAsync(items);
        vm.SetGalleryDateKind(GalleryDateKind.Taken);
        Assert.Equal(["future.jpg", "added only.jpg", "old camera.jpg", "epoch.jpg"], GalleryReviewSupport.Names(vm));
        var headers = vm.GalleryRows.OfType<GalleryHeaderRow>().Select(h => h.Text).ToList();
        Assert.Equal(4, headers.Count);
        Assert.Equal("Today", headers[1]);
        Assert.Equal(today.AddDays(3).ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB")), headers[0]);
        Assert.Equal("1 January 1980", headers[2]);

        vm.SortGalleryBy(GallerySortKey.Date);
        Assert.Equal(["epoch.jpg", "old camera.jpg", "added only.jpg", "future.jpg"], GalleryReviewSupport.Names(vm));
        Assert.All(vm.Items, i => Assert.Contains("Date taken", i.GalleryToolTipText));
        vm.Dispose();
    });

    private static void CheckFrames(ThreadView view, ResourceDictionary theme)
    {
        var panel = GalleryReviewSupport.FindVisualChild<VirtualizingStackPanel>(view.GalleryList)!;
        var prints = GalleryReviewSupport.FindVisuals<GalleryPrint>(panel).Where(p => p.IsVisible).ToList();
        Assert.True(prints.Count > 6, $"prints {prints.Count}");
        foreach (var print in prints)
        {
            var item = (ThreadItemViewModel)print.DataContext;
            var expected = theme[item.IsPointer ? "Cab.GalleryFramePointer" : "Cab.GalleryFrameCopy"];
            Assert.True(ReferenceEquals(expected, print.FrameBrush), $"{item.FileName} pointer={item.IsPointer}: frame {((SolidColorBrush?)print.FrameBrush)?.Color}");
        }
    }

    /// <summary>The topmost row of cells on screen, as ThreadView.FirstVisibleGalleryItem sees it.</summary>
    private static GalleryCellRow? FirstVisibleRow(ThreadView view)
    {
        var panel = GalleryReviewSupport.FindVisualChild<VirtualizingStackPanel>(view.GalleryList)!;
        var scroll = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.GalleryList)!;
        GalleryCellRow? best = null;
        var bestTop = double.MaxValue;
        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement { IsVisible: true, DataContext: GalleryCellRow row } container || row.Items.Count == 0)
            {
                continue;
            }

            var top = container.TranslatePoint(default, scroll).Y;
            if (top + container.ActualHeight > 1 && top < scroll.ViewportHeight && top < bestTop)
            {
                bestTop = top;
                best = row;
            }
        }

        return best;
    }

    /// <summary>A press and release on the element, tunneling from the root as real mouse input does.</summary>
    private static void Click(UIElement element, MouseButton button)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = Mouse.PreviewMouseUpEvent });
    }
}

/// <summary>A Gallery thread of the sample library's Images with chosen files, thumbnails for every photo, and a view in a hidden window.</summary>
internal static class GalleryReviewSupport
{
    public static async Task<(ThreadViewModel Vm, ReviewLibrary Library)> OpenAsync(IEnumerable<LibraryItem> items, ThreadViewState? state = null)
    {
        var sample = SampleLibraryService.Create(withVault: true);
        var images = (await sample.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var library = ReviewLibrary.Wrap(sample, images.Id, items, out var fake);
        var vm = new ThreadViewModel(new ThreadServices(
            library,
            NullProxy.Create<IShellContextMenuService>(),
            NullProxy.Create<IShellOperations>(),
            NullProxy.Create<IFileIntake>(),
            NullProxy.Create<IDialogService>(),
            new NotificationService(),
            new ShellNavigator(),
            new ThumbnailLoader(new Thumbnails(), NullProxy.Create<IMediaInfoService>()),
            state ?? new ThreadViewState()));
        await vm.LoadAsync(images.Id);
        Assert.Equal(ThreadViewMode.Gallery, vm.ViewMode);
        return (vm, fake);
    }

    /// <summary>A key press as the view sees it (Keyboard.Modifiers is the real keyboard's: none).</summary>
    public static void Press(UIElement target, PresentationSource source, Key key)
        => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    /// <summary>Lets layout, the Loaded-priority scroll requests and the thumbnail continuations run.</summary>
    public static async Task SettleAsync(FrameworkElement view)
    {
        for (var i = 0; i < 4; i++)
        {
            view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            HiddenWpf.Pump(TimeSpan.FromMilliseconds(30));
        }

        view.UpdateLayout();
    }

    public static T LoadComponent<T>(string path) => (T)Application.LoadComponent(new Uri("/Cabinette;component/" + path, UriKind.Relative));

    public static List<string> Names(ThreadViewModel vm) => vm.GalleryRows.OfType<GalleryCellRow>().SelectMany(r => r.Items).Select(i => i.FileName).ToList();

    /// <summary><paramref name="count"/> photos added <paramref name="daysAgo"/> days ago, an hour apart.</summary>
    public static List<LibraryItem> Day(int daysAgo, int count)
    {
        var day = GalleryTests.Local(DateTime.Today.AddDays(-daysAgo));
        return Enumerable.Range(0, count).Select(i => GalleryTests.Photo((daysAgo * 100) + i + 1, $"d{daysAgo}-{i}.jpg", day.AddHours(1 + i))).ToList();
    }

    /// <summary><paramref name="count"/> photos added over the last year.</summary>
    public static List<LibraryItem> Files(int count)
    {
        var random = new Random(count);
        var now = GalleryTests.Local(DateTime.Today.AddHours(12));
        return Enumerable.Range(1, count).Select(i => GalleryTests.Photo(i, $"IMG_{i:00000}.jpg", now.AddMinutes(-random.Next(0, 365 * 24 * 60)), size: random.Next(1, 20_000_000))).ToList();
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(25);
        }
    }

    public static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
        => FindVisuals<T>(parent).FirstOrDefault();

    public static IEnumerable<T> FindVisuals<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisuals<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>A thumbnail for every file (a 16:9 bitmap), so the Gallery shows prints as it does with real photos.</summary>
    private sealed class Thumbnails : IThumbnailService
    {
        private readonly BitmapSource _image;

        public Thumbnails()
        {
            var bitmap = new WriteableBitmap(160, 90, 96, 96, PixelFormats.Bgra32, null);
            bitmap.Freeze();
            _image = bitmap;
        }

        public Task<BitmapSource?> GetThumbnailAsync(string path, int sizePx, CancellationToken ct = default) => Task.FromResult<BitmapSource?>(_image);

        public Task<BitmapSource?> GetFileIconAsync(string pathOrExtension, int sizePx, CancellationToken ct = default) => Task.FromResult<BitmapSource?>(null);
    }

    /// <summary>
    /// Forwards to the sample library (categories, paths, settings), but lists its own files as the thread of one category,
    /// applies the thread search to their names, and raises <see cref="ILibraryService.Changed"/> when the test says so.
    /// </summary>
    public class ReviewLibrary : DispatchProxy
    {
        private readonly Lock _gate = new();
        private List<LibraryItem> _items = [];
        private ILibraryService _inner = null!;
        private long _categoryId;
        private EventHandler<LibraryChangedEventArgs>? _changed;

        public static ILibraryService Wrap(ILibraryService inner, long categoryId, IEnumerable<LibraryItem> items, out ReviewLibrary fake)
        {
            var proxy = Create<ILibraryService, ReviewLibrary>();
            fake = (ReviewLibrary)(object)proxy;
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
                    {
                        var search = args[1] as string;
                        lock (_gate)
                        {
                            return Task.FromResult<IReadOnlyList<LibraryItem>>([.. _items
                                .Where(i => search is null || i.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(i => i.AddedAt)]);
                        }
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
