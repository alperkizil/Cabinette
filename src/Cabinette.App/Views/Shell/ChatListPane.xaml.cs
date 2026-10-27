using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Motion;
using Cabinette.App.Views.Thread;

namespace Cabinette.App.Views.Shell;

/// <summary>Left pane: header, search, chat list / search results, drag-and-drop targets, context menus.</summary>
public partial class ChatListPane : UserControl
{
    private const string RowDropHighlightName = "DropHighlight";

    private readonly DropHighlightWatchdog _dropWatchdog;
    private readonly ChatListMotion _listMotion;
    private MainViewModel? _vm;
    private CategoryRowViewModel? _dropRow;

    public ChatListPane()
    {
        InitializeComponent();
        _dropWatchdog = new DropHighlightWatchdog(ClearDropTarget);
        _listMotion = new ChatListMotion(CategoryList);
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? Vm => _vm;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Opens the context menu of the first row whose category matches (used by the screenshot tool).</summary>
    public ContextMenu? BuildMenuFor(CategoryRowViewModel row) => Vm is null ? null : CategoryMenuBuilder.Build(Vm, row);

    public FrameworkElement? GetRowContainer(object item) => CategoryList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ChatList.PropertyChanged -= OnChatListPropertyChanged;
            _vm.ChatList.RowsChanging -= OnRowsChanging;
            _vm.ChatList.RowsChanged -= OnRowsChanged;
            _vm.Search.TagApplied -= OnTagApplied;
            _vm.HomeRequested -= OnHomeRequested;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.ChatList.PropertyChanged += OnChatListPropertyChanged;
            _vm.ChatList.RowsChanging += OnRowsChanging;
            _vm.ChatList.RowsChanged += OnRowsChanged;
            _vm.Search.TagApplied += OnTagApplied;
            _vm.HomeRequested += OnHomeRequested;
        }
    }

    // A refresh (files added from anywhere) reorders rows: glide them, pop growing badges.
    private void OnRowsChanging(object? sender, EventArgs e) => _listMotion.Capture(((ChatListViewModel)sender!).Rows);

    private void OnRowsChanged(object? sender, EventArgs e) => _listMotion.Play(((ChatListViewModel)sender!).Rows);

    private void OnChatListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatListViewModel.LevelChangeCount) && Vm is { } vm)
        {
            AnimateLevel(vm.ChatList.LastLevelDirection);
        }
    }

    private void AnimateLevel(int direction)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        LevelShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction >= 0 ? 40 : -40, 0, duration) { EasingFunction = ease });
        CategoryList.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, duration) { EasingFunction = ease });
        LevelBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    private void OnHomeRequested(object? sender, EventArgs e)
    {
        if (CategoryList.Items.Count > 0)
        {
            CategoryList.ScrollIntoView(CategoryList.Items[0]);
        }
    }

    private void OnTagApplied(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    // ------------------------------------------------------------------ clicks & keys

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            Activate(item);
        }
    }

    private void Activate(ListBoxItem item)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var owner = ItemsControl.ItemsControlFromItemContainer(item);
        if (ReferenceEquals(owner, SearchList))
        {
            _ = vm.ActivateSearchResultAsync(item.DataContext);
        }
        else if (item.DataContext is CategoryRowViewModel row)
        {
            _ = vm.ActivateRowAsync(row);
        }
    }

    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep the selection on the open thread; the menu opens on button up.
        e.Handled = true;
    }

    private void OnRowRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: CategoryRowViewModel row } item && Vm is { } vm)
        {
            CategoryMenuBuilder.Open(CategoryMenuBuilder.Build(vm, row), item, atMouse: true);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is null)
        {
            return;
        }

        var container = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem;
        if (container is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            Activate(container);
            e.Handled = true;
        }
        else if ((e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) &&
                 list.SelectedItem is CategoryRowViewModel row && Vm is { } vm)
        {
            CategoryMenuBuilder.Open(CategoryMenuBuilder.Build(vm, row), container, atMouse: false);
            e.Handled = true;
        }
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when vm.Search.HasQuery:
                vm.Search.Query = "";
                e.Handled = true;
                break;
            case Key.Down:
                MoveIntoList();
                e.Handled = true;
                break;
            case Key.Enter when vm.Search.IsActive:
                var first = vm.Search.Results.FirstOrDefault(r => r is not SearchSectionHeader);
                if (first is not null)
                {
                    vm.Search.SelectedResult = first;
                    _ = vm.ActivateSearchResultAsync(first);
                }

                e.Handled = true;
                break;
        }
    }

    private void MoveIntoList()
    {
        var list = Vm?.Search.IsActive == true ? SearchList : CategoryList;
        if (list.Items.Count == 0)
        {
            return;
        }

        if (list.SelectedItem is null || list.SelectedItem is SearchSectionHeader)
        {
            list.SelectedItem = list.Items.Cast<object>().FirstOrDefault(i => i is not SearchSectionHeader);
        }

        if (list.SelectedItem is null)
        {
            return;
        }

        list.ScrollIntoView(list.SelectedItem);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem)?.Focus();
        });
    }

    private void OnSearchBackClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        vm.IsFilterOpen = false;
        vm.Search.Query = "";
        if (!vm.Search.Filter.IsEmpty)
        {
            vm.Search.Filter.ResetCommand.Execute(null);
        }

        CategoryList.Focus();
    }

    // ------------------------------------------------------------------ drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var row = FindRow(e.OriginalSource as DependencyObject);
        var bubbles = e.Data.GetDataPresent(ThreadView.InternalDragFormat);
        if (row is { IsSystem: true } || (bubbles && row is null))
        {
            // "Deleted" takes no files: no highlight, and the cursor says so. Bubbles from a thread can go onto a row
            // (added to that category), but not onto the list background (they are already in the library).
            e.Effects = DragDropEffects.None;
            _dropWatchdog.Stop();
            ClearDropTarget();
            return;
        }

        e.Effects = DragDropEffects.Copy;
        SetDropTarget(row);
        _dropWatchdog.Touch();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(Body);
        if (position.X <= 0 || position.Y <= 0 || position.X >= Body.ActualWidth || position.Y >= Body.ActualHeight)
        {
            _dropWatchdog.Stop();
            ClearDropTarget();
            return;
        }

        // Moving between rows also raises DragLeave; if no DragOver follows, the watchdog clears the highlight.
        _dropWatchdog.Touch();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var container = FindRowContainer(e.OriginalSource as DependencyObject);
        var row = container?.DataContext as CategoryRowViewModel;
        _dropWatchdog.Stop();
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var bubbleIds = e.Data.GetDataPresent(ThreadView.InternalDragFormat) ? e.Data.GetData(ThreadView.InternalDragFormat) as long[] ?? [] : null;
        var accepted = Vm is not null && row is not { IsSystem: true } && paths is { Length: > 0 }
            && (bubbleIds is null || (row is not null && bubbleIds.Length > 0));
        if (accepted)
        {
            // The highlight that received the files pulses and fades (the add itself starts right away).
            var zone = row is null ? BackgroundDropZone : container is null ? null : VisualTreeSearch.FindByName(container, RowDropHighlightName);
            if (zone is not null)
            {
                DropZone.Receive(zone);
            }
        }

        ClearDropTarget();
        if (accepted && bubbleIds is not null)
        {
            _ = Vm!.AddItemsToCategoryAsync(bubbleIds, row!);
        }
        else if (accepted)
        {
            _ = Vm!.DropAsync(paths!, row?.CategoryId);
        }
    }

    private void SetDropTarget(CategoryRowViewModel? row)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (!ReferenceEquals(row, _dropRow))
        {
            if (_dropRow is not null)
            {
                _dropRow.IsDropTarget = false;
            }

            _dropRow = row;
            if (row is not null)
            {
                row.IsDropTarget = true;
            }
        }

        vm.ChatList.IsBackgroundDropTarget = row is null;
    }

    private void ClearDropTarget()
    {
        if (_dropRow is not null)
        {
            _dropRow.IsDropTarget = false;
            _dropRow = null;
        }

        Vm?.ChatList.ClearDropTargets();
    }

    private static CategoryRowViewModel? FindRow(DependencyObject? source) => FindRowContainer(source)?.DataContext as CategoryRowViewModel;

    private static ListBoxItem? FindRowContainer(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ListBoxItem item)
            {
                return item;
            }
        }

        return null;
    }
}
