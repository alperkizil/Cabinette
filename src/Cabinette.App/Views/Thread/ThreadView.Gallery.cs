using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.ViewModels.Thread;

namespace Cabinette.App.Views.Thread;

// The Gallery's view-only parts (SPEC.md section 4, "Gallery"): the cell layout from the pane width, Ctrl+mouse wheel sizes,
// the date choice of the sort header, and the arrow keys.
public partial class ThreadView
{
    /// <summary>Cells per row and the inherited sizes the Gallery templates read; from the pane width and the cell size only.</summary>
    private void UpdateGalleryMetrics(double available)
    {
        if (_vm is null || available <= 0)
        {
            return;
        }

        var cell = _vm.GalleryCellPixels;
        var columns = GalleryMetrics.Columns(available, cell);
        var slot = GalleryMetrics.SlotWidth(available, columns);
        var indent = Math.Max(0, Math.Floor((slot - cell) / 2));
        ThreadLayout.SetGalleryCellSize(ContentHost, cell);
        ThreadLayout.SetGallerySlotWidth(ContentHost, slot);
        ThreadLayout.SetGalleryHeaderIndent(ContentHost, new Thickness(indent, 0, indent, 0));
        ThreadLayout.SetGalleryIconSize(ContentHost, GalleryMetrics.IconSize(cell));
        _vm.SetGalleryColumns(columns);
    }

    /// <summary>A new cell size: the cell that was first on screen stays first on screen (the top stays the top).</summary>
    private void OnGalleryCellSizeChanged()
    {
        ThreadItemViewModel? anchor = null;
        var atTop = true;
        if (_vm?.ViewMode == ThreadViewMode.Gallery)
        {
            anchor = FirstVisibleGalleryItem(out atTop);
        }

        UpdateLayoutMetrics();
        if (anchor is not null && !atTop)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollGalleryItemTo(anchor, top: true));
        }
    }

    private void GallerySortBar_SizeChanged(object sender, SizeChangedEventArgs e) => FitGallerySortBar();

    /// <summary>The sort buttons come first: in a narrow pane the cell size buttons step aside rather than cut them off.</summary>
    private void FitGallerySortBar()
    {
        if (GallerySortBar.ActualWidth <= 0)
        {
            return;
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        GallerySortButtons.Measure(infinite);
        GalleryZoom.Visibility = Visibility.Visible;
        GalleryZoom.Measure(infinite);
        var fits = GallerySortButtons.DesiredSize.Width + GalleryZoom.DesiredSize.Width <= GallerySortBar.ActualWidth;
        GalleryZoom.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GalleryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _vm is null || e.Delta == 0)
        {
            return;
        }

        e.Handled = true;
        _vm.StepGalleryCellSize(e.Delta > 0 ? 1 : -1);
    }

    /// <summary>The Date button's chevron: date added or date taken (the one in use is checked).</summary>
    private void GalleryDateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || sender is not FrameworkElement button)
        {
            return;
        }

        var menu = BuildGalleryDateMenu(vm);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The Date choice menu (also rendered by the screenshot runner). The app's implicit menu styles apply.</summary>
    internal static ContextMenu BuildGalleryDateMenu(ThreadViewModel vm)
    {
        var menu = new ContextMenu();
        foreach (var (kind, key) in new[] { (GalleryDateKind.Added, "Thread_ColumnDateAdded"), (GalleryDateKind.Taken, "Thread_GalleryDateTaken") })
        {
            var current = vm.GallerySort.DateKind == kind;
            var item = new MenuItem
            {
                Header = L.Get(key),
                IsCheckable = true,
                IsChecked = current,
                Icon = current ? new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark20, FontSize = 16 } : null,
            };
            item.Click += (_, args) =>
            {
                args.Handled = true;
                vm.SetGalleryDateKind(kind);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// The Gallery's keys while it has the keyboard: arrows move between cells (Shift extends the selection), Home and End go
    /// to the first and last cell, Page Up and Page Down move a screen of rows, Ctrl+plus and Ctrl+minus change the cell size.
    /// </summary>
    private bool HandleGalleryKey(Key key, ModifierKeys mods)
    {
        if (_vm is not { ViewMode: ThreadViewMode.Gallery } vm || !GalleryList.IsKeyboardFocusWithin)
        {
            return false;
        }

        if (mods == ModifierKeys.Control && key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)
        {
            vm.StepGalleryCellSize(key is Key.OemPlus or Key.Add ? 1 : -1);
            return true;
        }

        if ((mods & ~ModifierKeys.Shift) != ModifierKeys.None)
        {
            return false;
        }

        var (move, count) = key switch
        {
            Key.Left => (GalleryMove.Left, 1),
            Key.Right => (GalleryMove.Right, 1),
            Key.Up => (GalleryMove.Up, 1),
            Key.Down => (GalleryMove.Down, 1),
            Key.PageUp => (GalleryMove.Up, GalleryRowsPerPage()),
            Key.PageDown => (GalleryMove.Down, GalleryRowsPerPage()),
            Key.Home => (GalleryMove.First, 1),
            Key.End => (GalleryMove.Last, 1),
            _ => ((GalleryMove?)null, 0),
        };
        if (move is not { } direction)
        {
            return false;
        }

        if (vm.MoveGalleryCursor(direction, extend: mods == ModifierKeys.Shift, count, FirstVisibleGalleryItem(out _)) is { } target)
        {
            ScrollGalleryItemTo(target, top: false);
        }

        return true;
    }

    /// <summary>The first cell of the topmost row on screen; <paramref name="atTop"/> when the list is scrolled to its top.</summary>
    private ThreadItemViewModel? FirstVisibleGalleryItem(out bool atTop)
    {
        atTop = true;
        if (FindVisualChild<VirtualizingStackPanel>(GalleryList) is not { } panel || FindScrollViewer(GalleryList) is not { } scroll)
        {
            return null;
        }

        atTop = scroll.VerticalOffset < 1;
        ThreadItemViewModel? best = null;
        var bestTop = double.MaxValue;
        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement { IsVisible: true, DataContext: GalleryCellRow row } container || row.Items.Count == 0 || !container.IsDescendantOf(scroll))
            {
                continue;
            }

            var top = container.TranslatePoint(default, scroll).Y;
            if (top + container.ActualHeight > 1 && top < scroll.ViewportHeight && top < bestTop)
            {
                bestTop = top;
                best = row.Items[0];
            }
        }

        return best;
    }

    private int GalleryRowsPerPage()
    {
        if (FindVisualChild<VirtualizingStackPanel>(GalleryList) is not { } panel || FindScrollViewer(GalleryList) is not { } scroll)
        {
            return 1;
        }

        var height = panel.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.DataContext is GalleryCellRow && c.ActualHeight > 0)?.ActualHeight ?? 0;
        return height > 0 ? Math.Max(1, (int)Math.Floor(scroll.ViewportHeight / height)) : 1;
    }

    /// <summary>Scrolls the row of <paramref name="item"/> to the top, or (for the keyboard) just far enough to show it whole.</summary>
    private void ScrollGalleryItemTo(ThreadItemViewModel item, bool top)
    {
        if (_vm is null)
        {
            return;
        }

        var index = IndexOfRow(_vm.GalleryRows, item);
        if (index < 0 || FindVisualChild<VirtualizingStackPanel>(GalleryList) is not { } panel || FindScrollViewer(GalleryList) is not { } scroll)
        {
            return;
        }

        panel.BringIndexIntoViewPublic(index);
        GalleryList.UpdateLayout();
        if (GalleryList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container || !container.IsDescendantOf(scroll))
        {
            return;
        }

        var y = container.TransformToAncestor(scroll).Transform(default).Y;
        if (top)
        {
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + y));
        }
        else if (y < 0)
        {
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + y));
        }
        else if (y + container.ActualHeight > scroll.ViewportHeight)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + y + container.ActualHeight - scroll.ViewportHeight);
        }
    }
}
