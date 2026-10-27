using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Motion;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// The thread pane. View-only concerns live here: scroll anchoring (open at the bottom, follow new items only when already
/// at the bottom), mouse selection and drag-out, the shell context menu position, keyboard shortcuts, the drop overlay
/// and the inherited layout metrics used by the templates.
/// </summary>
public partial class ThreadView : UserControl, IThreadViewHost
{
    /// <summary>
    /// Marks drags that start in a thread (its data: the dragged item ids, a <c>long[]</c>). Dropping them back on a thread or
    /// the window is ignored; a chat-list row adds those items to its category (like "Add to category…").
    /// </summary>
    public const string InternalDragFormat = "Cabinette.ThreadDrag";

    private const double MinTileSize = 150;
    private const double TileGap = 10;

    private readonly DispatcherTimer _dropHideTimer;
    private readonly DispatcherTimer _stickyDateTimer;
    private ThreadViewModel? _vm;
    private ScrollViewer? _chatScroll;
    private bool _stickToBottom = true;
    private Press? _press;
    private bool _stickyDateShown;
    private DateTime _suppressStickyDateUntil;
    private int _unseenCount;

    static ThreadView()
    {
        // Bold text and the font change the width of every label without changing the pane's size: fit the bars to it again.
        FontWeightProperty.OverrideMetadata(typeof(ThreadView), new FrameworkPropertyMetadata(OnTextLookChanged));
        FontFamilyProperty.OverrideMetadata(typeof(ThreadView), new FrameworkPropertyMetadata(OnTextLookChanged));
    }

    public ThreadView()
    {
        InitializeComponent();
        _dropHideTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _dropHideTimer.Tick += (_, _) =>
        {
            _dropHideTimer.Stop();
            DropZone.SetIsActive(DropOverlay, false);
        };

        // The floating date fades out 1.5 s after scrolling stops.
        _stickyDateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _stickyDateTimer.Tick += (_, _) =>
        {
            _stickyDateTimer.Stop();
            FadeStickyDate(show: false, fast: false);
        };

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewDragEnter += OnPreviewDragOver;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDragLeave += OnPreviewDragLeave;
        PreviewDrop += OnPreviewDrop;
        DetailsList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));
    }

    // ------------------------------------------------------------------ IThreadViewHost

    public IntPtr OwnerHandle => Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

    public void ScrollToEnd()
    {
        _stickToBottom = true;
        _unseenCount = 0;
        SuppressStickyDate();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_vm?.ViewMode == ThreadViewMode.Grid)
            {
                FindScrollViewer(GridList)?.ScrollToTop();
            }
            else if (_vm?.ViewMode == ThreadViewMode.Gallery)
            {
                // The Gallery starts at the top of its order (newest first by default).
                FindScrollViewer(GalleryList)?.ScrollToTop();
            }

            _chatScroll?.ScrollToEnd();
            UpdateScrollDownButton();
        });
    }

    public void ScrollGalleryToStart()
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindScrollViewer(GalleryList)?.ScrollToTop());

    public void NotifyNewItems(int count)
    {
        // Items arrived while the user reads older ones: count them on the jump-to-latest button.
        if (!_stickToBottom && _vm?.ViewMode == ThreadViewMode.Chat)
        {
            _unseenCount += count;
            UpdateScrollDownButton();
        }
    }

    public void BringItemIntoView(ThreadItemViewModel item)
    {
        _stickToBottom = false;
        SuppressStickyDate();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_vm is null)
            {
                return;
            }

            switch (_vm.ViewMode)
            {
                case ThreadViewMode.Details:
                    DetailsList.ScrollIntoView(item);
                    break;
                case ThreadViewMode.Grid:
                    ScrollIndexIntoView(GridList, IndexOfRow(_vm.GridRows, item));
                    break;
                case ThreadViewMode.Gallery:
                    ScrollIndexIntoView(GalleryList, IndexOfRow(_vm.GalleryRows, item));
                    break;
                default:
                    ScrollIndexIntoView(ChatList, IndexOfRow(_vm.ChatRows, item));
                    break;
            }
        });
    }

    public bool ShowTagEditor(TagEditorViewModel editor)
    {
        var owner = Window.GetWindow(this);
        var dialog = new TagEditorDialog(editor) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    public void ShowMediaViewer(IReadOnlyList<ThreadItemViewModel> items, int index)
    {
        var owner = Window.GetWindow(this);
        var viewer = new MediaViewerWindow(items, index, item => _vm?.OpenExternally(item)) { Owner = owner };
        viewer.Show();
    }

    // ------------------------------------------------------------------ lifecycle

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ThreadViewModel old)
        {
            old.AttachHost(null);
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _press = null;
        _unseenCount = 0;
        FadeStickyDate(show: false, fast: true);
        _vm = e.NewValue as ThreadViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        var columns = new[] { NameColumn, TypeColumn, SizeColumn, DateColumn, ModeColumn, TagsColumn };
        for (var i = 0; i < columns.Length && i < _vm.DetailsColumns.Count; i++)
        {
            columns[i].Header = _vm.DetailsColumns[i];
        }

        _vm.AttachHost(this);
        UpdateLayoutMetrics();
        ScrollToEnd();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_chatScroll is null)
        {
            ChatList.ApplyTemplate();
            _chatScroll = ChatList.Template.FindName("PART_Scroll", ChatList) as ScrollViewer;
            if (_chatScroll is not null)
            {
                _chatScroll.ScrollChanged += OnChatScrollChanged;
            }
        }

        UpdateLayoutMetrics();
        if (_stickToBottom)
        {
            ScrollToEnd();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_chatScroll is not null)
        {
            _chatScroll.ScrollChanged -= OnChatScrollChanged;
            _chatScroll = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ThreadViewModel.IsSearchOpen) when _vm?.IsSearchOpen == true:
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(SearchBox));
                break;
            case nameof(ThreadViewModel.IsSearchOpen):
                if (SearchBox.IsKeyboardFocusWithin)
                {
                    ActiveList().Focus();
                }

                break;
            case nameof(ThreadViewModel.ViewMode):
                UpdateLayoutMetrics();
                UpdateScrollDownButton();
                if (_vm?.ViewMode == ThreadViewMode.Chat && _stickToBottom)
                {
                    ScrollToEnd();
                }

                break;
            case nameof(ThreadViewModel.GalleryCellSize):
                OnGalleryCellSizeChanged();
                break;
            case nameof(ThreadViewModel.GallerySort):
                // "Date added" and "Date taken" differ in width.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitGallerySortBar);
                break;
        }
    }

    // ------------------------------------------------------------------ layout metrics

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMetrics();

    private static void OnTextLookChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is ThreadView { IsLoaded: true } view)
        {
            // After the layout pass that measures the text at its new weight.
            view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                view.FitSelectionBar();
                view.FitGallerySortBar();
            });
        }
    }

    /// <summary>
    /// The bulk action pills show their labels while they fit next to "N selected" and "Select all". In a narrow pane they
    /// show only their icons, so none is cut off; their tooltips and accessible names keep the labels. Where even that is
    /// too wide (large text, a wide font, the narrowest window), the bar takes the next step until everything fits: the
    /// count drops the word "selected", "Select all" becomes "All" (its tooltip and accessible name keep the whole text),
    /// and last the icon pills get narrower.
    /// </summary>
    private void SelectionBar_SizeChanged(object sender, SizeChangedEventArgs e) => FitSelectionBar();

    private enum PillLook
    {
        Labels,
        Icons,
        TightIcons,
    }

    private static readonly (PillLook Pills, bool ShortCount, bool ShortSelectAll)[] SelectionBarSteps =
    [
        (PillLook.Labels, false, false),
        (PillLook.Icons, false, false),
        (PillLook.Icons, true, false),
        (PillLook.Icons, true, true),
        (PillLook.TightIcons, true, true),
    ];

    private void FitSelectionBar()
    {
        if (SelectionBar.ActualWidth <= 0 || !SelectAllButton.IsVisible)
        {
            return;
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);

        // The count starts after the cancel button; what follows is measured for each step (DesiredSize includes margins).
        var start = SelectionCount.TranslatePoint(new Point(-SelectionCount.Margin.Left, 0), SelectionBar).X;
        foreach (var (pills, shortCount, shortSelectAll) in SelectionBarSteps)
        {
            SetSelectionPills(pills);
            SetShortCount(shortCount);
            SetShortSelectAll(shortSelectAll);

            // Measure again for real: with the same constraint as last time, an element returns its old size even when a
            // label, padding or text inside it changed, so everything here is marked to be measured again first.
            InvalidateMeasureDeep(SelectionCount);
            InvalidateMeasureDeep(SelectAllButton);
            InvalidateMeasureDeep(SelectionActions);
            SelectionCount.Measure(infinite);
            SelectAllButton.Measure(infinite);
            SelectionActions.Measure(infinite);
            if (start + SelectionCount.DesiredSize.Width + SelectAllButton.DesiredSize.Width + SelectionActions.DesiredSize.Width + 8 <= SelectionBar.ActualWidth)
            {
                return;
            }
        }
    }

    private static void InvalidateMeasureDeep(DependencyObject node)
    {
        (node as UIElement)?.InvalidateMeasure();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            InvalidateMeasureDeep(VisualTreeHelper.GetChild(node, i));
        }
    }

    private void SetShortCount(bool shortCount)
    {
        var path = shortCount ? nameof(ThreadViewModel.SelectedCount) : nameof(ThreadViewModel.SelectionText);
        if (BindingOperations.GetBinding(SelectionCount, TextBlock.TextProperty)?.Path.Path != path)
        {
            SelectionCount.SetBinding(TextBlock.TextProperty, new Binding(path));
        }
    }

    private void SetShortSelectAll(bool shortText)
    {
        var text = L.Get(shortText ? "Thread_SelectAllShort" : "Thread_SelectAll");
        if (!Equals(SelectAllButton.Content, text))
        {
            SelectAllButton.Content = text;
            SelectAllButton.ToolTip = shortText ? L.Get("Thread_SelectAll") : null;
            System.Windows.Automation.AutomationProperties.SetName(SelectAllButton, L.Get("Thread_SelectAll"));
        }
    }

    private void SetSelectionPills(PillLook look)
    {
        foreach (var button in SelectionActions.Children.OfType<Button>())
        {
            if (button.Content is StackPanel content && content.Children.Count == 2
                && content.Children[0] is FrameworkElement icon && content.Children[1] is TextBlock label)
            {
                label.Visibility = look == PillLook.Labels ? Visibility.Visible : Visibility.Collapsed;
                icon.Margin = look == PillLook.Labels ? new Thickness(0, 0, 6, 0) : new Thickness(0);

                // An icon alone sits in a round pill (36 high), or a narrower one where even that doesn't fit.
                switch (look)
                {
                    case PillLook.Labels:
                        button.ClearValue(PaddingProperty);
                        break;
                    case PillLook.Icons:
                        button.Padding = new Thickness(9.5, 7, 9.5, 7);
                        break;
                    default:
                        button.Padding = new Thickness(5.5, 7, 5.5, 7);
                        break;
                }
            }
        }
    }

    private void UpdateLayoutMetrics()
    {
        var width = ContentHost.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var side = Math.Clamp(Math.Round(width * 0.045), 12, 60);
        var padding = new Thickness(side, 0, side, 0);
        ThreadLayout.SetRowPadding(ContentHost, padding);

        // Bubbles: ~60% of the row, never narrower than an image bubble, capped for readability.
        var usable = width - (2 * side) - 36;
        ThreadLayout.SetBubbleMaxWidth(ContentHost, Math.Clamp(usable * 0.62, 342, 440));

        // Grid: as many columns of at least MinTileSize as fit.
        var available = width - (2 * side) + TileGap;
        var columns = Math.Max(2, (int)Math.Floor(available / (MinTileSize + TileGap)));
        var tile = Math.Floor((available / columns) - TileGap);
        ThreadLayout.SetTileSize(ContentHost, Math.Max(80, tile));
        _vm?.SetGridColumns(columns);
        UpdateGalleryMetrics(width - (2 * side));

        // Details: the Name column takes the space the fixed-width columns leave.
        var fixedColumns = TypeColumn.Width + SizeColumn.Width + DateColumn.Width + ModeColumn.Width + TagsColumn.Width;
        NameColumn.Width = Math.Max(220, Math.Floor(width - (2 * side) - fixedColumns - 14));
    }

    // ------------------------------------------------------------------ scrolling

    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            // A jump away from the end in the same pass is the user's (a jump to the top re-estimates the rows' heights, so
            // the extent changes with it): it lets go of the bottom instead of being pulled back. Rows growing or shrinking
            // while stuck only move the offset within the last screen.
            if (_stickToBottom && e.VerticalChange < 0 && scroll.VerticalOffset < scroll.ScrollableHeight - scroll.ViewportHeight)
            {
                _stickToBottom = false;
            }
            else if (_stickToBottom)
            {
                SuppressStickyDate();
                scroll.ScrollToEnd();
            }
        }
        else if (e.VerticalChange != 0)
        {
            _stickToBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 2;
        }

        if (_stickToBottom)
        {
            _unseenCount = 0;
        }

        if (e.VerticalChange != 0 && DateTime.UtcNow >= _suppressStickyDateUntil)
        {
            UpdateStickyDate(scroll);
        }

        UpdateScrollDownButton();
    }

    /// <summary>The jump button appears about a screen up from the bottom, or whenever unseen items arrived.</summary>
    private void UpdateScrollDownButton()
    {
        var show = false;
        if (_vm?.ViewMode == ThreadViewMode.Chat && _chatScroll is { } scroll)
        {
            var distance = scroll.ScrollableHeight - scroll.VerticalOffset;
            show = distance > Math.Max(240, scroll.ViewportHeight) || (_unseenCount > 0 && distance > 2);
        }

        JumpHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UnseenBadge.Visibility = show && _unseenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnseenText.Text = _unseenCount > 99 ? "99+" : _unseenCount.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Shows the date of the topmost visible row in the floating pill. Hidden while an inline date pill is at the top
    /// (no duplicate), and faded out shortly after scrolling stops.
    /// </summary>
    private void UpdateStickyDate(ScrollViewer scroll)
    {
        if (_vm is null || _vm.ViewMode != ThreadViewMode.Chat || FindVisualChild<VirtualizingStackPanel>(ChatList) is not { } panel)
        {
            return;
        }

        var topIndex = -1;
        var headerAtTop = false;
        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement { IsVisible: true } container || !container.IsDescendantOf(scroll))
            {
                continue;
            }

            var y = container.TranslatePoint(default, scroll).Y;
            if (y + container.ActualHeight <= 0 || y >= scroll.ViewportHeight)
            {
                continue;
            }

            if (container.DataContext is DateHeaderRow && y < 40)
            {
                headerAtTop = true;
            }

            var index = ChatList.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0 && (topIndex < 0 || index < topIndex))
            {
                topIndex = index;
            }
        }

        string? text = null;
        for (var i = Math.Min(topIndex, _vm.ChatRows.Count - 1); i >= 0 && text is null; i--)
        {
            text = (_vm.ChatRows[i] as DateHeaderRow)?.Text;
        }

        if (text is null || headerAtTop)
        {
            FadeStickyDate(show: false, fast: true);
            return;
        }

        StickyDateText.Text = text;
        FadeStickyDate(show: true, fast: true);
        _stickyDateTimer.Stop();
        _stickyDateTimer.Start();
    }

    private void SuppressStickyDate()
    {
        // Programmatic scrolls (open at the bottom, follow new items, focus an item) don't flash the pill.
        _suppressStickyDateUntil = DateTime.UtcNow.AddMilliseconds(500);
        FadeStickyDate(show: false, fast: true);
    }

    private void FadeStickyDate(bool show, bool fast)
    {
        if (show == _stickyDateShown)
        {
            return;
        }

        _stickyDateShown = show;
        var duration = TimeSpan.FromMilliseconds(show ? 120 : fast ? 90 : 350);
        StickyDate.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, duration));
    }

    private void ScrollDown_Click(object sender, RoutedEventArgs e) => ScrollToEnd();

    private void ChatList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            _stickToBottom = false;
        }
    }

    private void Subcategories_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static int IndexOfRow(IList<ThreadRow> rows, ThreadItemViewModel item)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            switch (rows[i])
            {
                case ItemRow row when ReferenceEquals(row.Item, item):
                case AlbumRow album when album.Items.Contains(item):
                case GridTileRow tiles when tiles.Items.Contains(item):
                case GalleryCellRow cells when cells.Items.Contains(item):
                    return i;
            }
        }

        return -1;
    }

    private void ScrollIndexIntoView(ItemsControl list, int index)
    {
        if (index < 0)
        {
            return;
        }

        var panel = FindVisualChild<VirtualizingStackPanel>(list);
        var scroll = FindScrollViewer(list);
        if (panel is null || scroll is null)
        {
            return;
        }

        panel.BringIndexIntoViewPublic(index);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container && container.IsDescendantOf(scroll))
        {
            var top = container.TransformToAncestor(scroll).Transform(default).Y;
            var target = scroll.VerticalOffset + top - ((scroll.ViewportHeight - container.ActualHeight) / 2);
            scroll.ScrollToVerticalOffset(Math.Max(0, target));
        }
    }

    // ------------------------------------------------------------------ mouse: selection, open, drag out, context menu

    private void Content_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _press = null;
        if (_vm is null)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ScrollBar>(source) is not null)
        {
            if (IsDescendant(source, ChatList))
            {
                _stickToBottom = false;
            }

            return;
        }

        if (IsInteractive(source))
        {
            return; // buttons, sliders and text boxes handle their own clicks
        }

        var list = ActiveList();
        var hit = HitTest(source);
        if (hit.Items.Count == 0 || (!hit.InsideBubble && !_vm.IsSelectionActive))
        {
            if (IsDescendant(source, list))
            {
                _vm.ClickEmptyArea();
                list.Focus();
            }

            return;
        }

        list.Focus();
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.ClickCount >= 2)
        {
            if (hit.Cell is not null)
            {
                _vm.OpenInViewer(hit.Cell.Item);
            }
            else if (hit.Items.Count == 1)
            {
                _ = _vm.OpenItemAsync(hit.Items[0]);
            }
            else
            {
                _vm.OpenInViewer(hit.Items[0]);
            }

            e.Handled = true;
            return;
        }

        // Clicks on a selected item, and plain clicks on album cells, are resolved on mouse-up so they can start a drag.
        var alreadySelected = hit.Items.All(i => i.IsSelected);
        var deferred = !shift && (alreadySelected || (hit.Cell is not null && !ctrl && !_vm.IsSelectionActive));
        if (!deferred)
        {
            _vm.ClickItems(hit.Items, ctrl, shift);
        }

        _press = new Press(list, e.GetPosition(this), hit.Items, hit.Cell, deferred, ctrl);
        if (ReferenceEquals(list, DetailsList))
        {
            e.Handled = true; // our selection model replaces the ListView's
        }
    }

    private void Content_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_press is not { } press || _vm is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _press = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - press.Position.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - press.Position.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _press = null;
        var items = press.Items.Any(i => i.IsSelected) ? _vm.SelectedItems.ToList() : press.Items;
        var paths = items.Where(i => !i.IsMissing).Select(i => i.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        // The files for drops outside Cabinette; inside it, the item ids (a chat-list row adds them to its category).
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData(InternalDragFormat, items.Where(i => !i.IsMissing).Select(i => i.Id).Distinct().ToArray());
        try
        {
            DragDrop.DoDragDrop(press.List, data, DragDropEffects.Copy);
        }
        catch (COMException)
        {
            // The drop target failed; nothing to clean up.
        }
    }

    private void Content_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var press = _press;
        _press = null;
        if (press is null || _vm is null || !press.Deferred)
        {
            return;
        }

        if (press.Cell is not null && !press.Ctrl && !_vm.IsSelectionActive)
        {
            _vm.OpenInViewer(press.Cell.Item);
            return;
        }

        _vm.ClickItems(press.Items, press.Ctrl, shift: false);
    }

    private void Content_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ScrollBar>(source) is not null || FindAncestor<TextBoxBase>(source) is not null)
        {
            return;
        }

        var hit = HitTest(source);
        if (hit.Items.Count == 0)
        {
            return;
        }

        if (!hit.Items.All(i => i.IsSelected))
        {
            _vm.SelectOnly(hit.Items);
        }

        ActiveList().Focus();
        e.Handled = true;
    }

    private void Content_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ScrollBar>(source) is not null || FindAncestor<TextBoxBase>(source) is not null)
        {
            return;
        }

        if (HitTest(source).Items.Count == 0 || !_vm.HasSelection)
        {
            return;
        }

        e.Handled = true;
        var screenPoint = PointToScreen(e.GetPosition(this));
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+right-click goes straight to the Windows classic menu, like Windows 11.
            _vm.ShowShellContextMenu(screenPoint);
            return;
        }

        ShowStyledMenu(screenPoint, relativeToContent: null);
    }

    private void ShowStyledMenu(Point screenPointPx, Point? relativeToContent)
    {
        if (_vm is not null && ThreadItemMenuBuilder.Build(_vm, screenPointPx) is { } menu)
        {
            ThreadItemMenuBuilder.Open(menu, ContentHost, relativeToContent);
        }
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: DetailsColumnHeader header })
        {
            _vm?.SortDetailsBy(header.Column);
        }
    }

    /// <summary>What a click landed on: the items it targets, the album cell (if any) and whether it was on the bubble/tile itself.</summary>
    private Hit HitTest(DependencyObject? source)
    {
        AlbumCell? cell = null;
        ThreadItemViewModel? item = null;
        ThreadRow? row = null;
        var insideBubble = false;
        for (var d = source; d is not null && !ReferenceEquals(d, ContentHost); d = GetParent(d))
        {
            if (d is ListViewItem { DataContext: ThreadItemViewModel rowItem })
            {
                return new Hit([rowItem], null, true);
            }

            if (d is not FrameworkElement fe)
            {
                continue;
            }

            if (fe.Name is "Chrome" or "Tile")
            {
                insideBubble = true;
            }

            switch (fe.DataContext)
            {
                case AlbumCell c:
                    cell ??= c;
                    break;
                case ThreadItemViewModel vm:
                    item ??= vm;
                    break;
                case ThreadRow r:
                    row ??= r;
                    break;
            }
        }

        return row switch
        {
            ItemRow itemRow => new Hit([itemRow.Item], null, insideBubble),
            AlbumRow when cell is not null => new Hit([cell.Item], cell, insideBubble),
            AlbumRow album => new Hit(album.Items, null, insideBubble),
            GridTileRow when item is not null => new Hit([item], null, insideBubble),
            GalleryCellRow when item is not null => new Hit([item], null, insideBubble),
            _ => Hit.None,
        };
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var d = source; d is not null; d = GetParent(d))
        {
            switch (d)
            {
                case ButtonBase or Slider or Thumb or TextBoxBase:
                    return true;
                case ItemsControl or ListViewItem:
                    return false;
            }
        }

        return false;
    }

    private Control ActiveList() => _vm?.ViewMode switch
    {
        ThreadViewMode.Grid => GridList,
        ThreadViewMode.Gallery => GalleryList,
        ThreadViewMode.Details => DetailsList,
        _ => ChatList,
    };

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var inText = focused is TextBoxBase;
        if (!inText && HandleGalleryKey(key, mods))
        {
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.V when mods == ModifierKeys.Control:
                HandlePaste(inText, e);
                break;
            case Key.A when mods == ModifierKeys.Control && !inText:
                _vm.SelectAll();
                ActiveList().Focus();
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control && !inText && _vm.HasSelection:
                _ = _vm.CopySelectedAsync();
                e.Handled = true;
                break;
            case Key.F when mods == ModifierKeys.Control:
                _vm.IsSearchOpen = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(SearchBox));
                e.Handled = true;
                break;
            case Key.Delete when mods == ModifierKeys.None && !inText && _vm.HasSelection:
                _ = _vm.DeleteSelectedAsync();
                e.Handled = true;
                break;
            case Key.F2 when mods == ModifierKeys.None && !inText && _vm.SelectedCount == 1:
                _ = _vm.RenameSelectedAsync();
                e.Handled = true;
                break;
            case Key.Enter when mods == ModifierKeys.None && !inText && _vm.HasSelection:
                _ = _vm.OpenSelectedAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                if (ReferenceEquals(focused, SearchBox))
                {
                    _vm.IsSearchOpen = false;
                    e.Handled = true;
                }
                else if (_vm.IsSelectionActive || _vm.HasSelection)
                {
                    _vm.CancelSelectionCommand.Execute(null);
                    e.Handled = true;
                }

                break;
            case Key.Apps when !inText && _vm.HasSelection:
            case Key.F10 when mods == ModifierKeys.Shift && !inText && _vm.HasSelection:
                {
                    var at = new Point(ContentHost.ActualWidth / 2, ContentHost.ActualHeight / 3);
                    ShowStyledMenu(ContentHost.PointToScreen(at), relativeToContent: at);
                }

                e.Handled = true;
                break;
            case Key.PageUp or Key.Up or Key.Home when !inText:
                _stickToBottom = false;
                break;
        }
    }

    /// <summary>
    /// A focused text box with text on the clipboard pastes the text (also when there is a bitmap too, as with Excel cells).
    /// Otherwise files or an image paste into the thread, and outside text boxes text does as well. See <see cref="ThreadPasteRouting"/>.
    /// </summary>
    private void HandlePaste(bool inText, KeyEventArgs e)
    {
        bool hasText;
        bool filesOrImage;
        try
        {
            hasText = inText && Clipboard.ContainsText(TextDataFormat.UnicodeText);
            filesOrImage = Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsData("PNG");
        }
        catch (COMException)
        {
            hasText = inText;
            filesOrImage = false;
        }

        switch (ThreadPasteRouting.Route(inText, hasText, filesOrImage, _vm!.CanAddFiles))
        {
            case ThreadPasteTarget.Thread:
                e.Handled = true;
                _ = _vm.PasteAsync();
                break;
            case ThreadPasteTarget.Nothing:
                e.Handled = true;
                break;
        }
    }

    private void NoteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_vm?.SendNoteCommand.CanExecute(null) == true)
            {
                _vm.SendNoteCommand.Execute(null);
            }
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter)
        {
            ActiveList().Focus();
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SearchText = "";
        }

        SearchBox.Focus();
    }

    // ------------------------------------------------------------------ drag & drop in

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (e.Data.GetDataPresent(InternalDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return; // text drags into the note box keep working
        }

        if (!_vm.CanAddFiles)
        {
            // "Deleted" takes no files: no highlight, and the cursor says so.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            DropZone.SetIsActive(DropOverlay, false);
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _dropHideTimer.Stop();
        DropZone.SetIsActive(DropOverlay, true);
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between children; hide only if no DragOver follows shortly.
        _dropHideTimer.Stop();
        _dropHideTimer.Start();
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        _dropHideTimer.Stop();
        var paths = _vm is null || e.Data.GetDataPresent(InternalDragFormat) ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is { Length: > 0 } && _vm!.CanAddFiles)
        {
            // The icon dips into the tray and the overlay shrinks away; the add below starts right away.
            DropZone.Receive(DropOverlay);
        }

        DropZone.SetIsActive(DropOverlay, false);
        if (_vm is null || paths is not { Length: > 0 })
        {
            return;
        }

        // Handled even for "Deleted", so the drop never falls through to the main window's auto-categorize.
        e.Handled = true;
        if (_vm.CanAddFiles)
        {
            _ = _vm.AddDroppedPathsAsync(paths);
        }
    }

    // ------------------------------------------------------------------ tree helpers

    private static DependencyObject? GetParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static T? FindAncestor<T>(DependencyObject? d)
        where T : DependencyObject
    {
        for (; d is not null; d = GetParent(d))
        {
            if (d is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsDescendant(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d is not null; d = GetParent(d))
        {
            if (ReferenceEquals(d, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent) => FindVisualChild<ScrollViewer>(parent);

    private sealed record Press(Control List, Point Position, IReadOnlyList<ThreadItemViewModel> Items, AlbumCell? Cell, bool Deferred, bool Ctrl);

    private sealed record Hit(IReadOnlyList<ThreadItemViewModel> Items, AlbumCell? Cell, bool InsideBubble)
    {
        public static Hit None { get; } = new([], null, false);
    }
}
