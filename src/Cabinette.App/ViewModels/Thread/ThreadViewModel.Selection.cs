using Cabinette.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Thread;

// Selection: click, Ctrl/Shift-click, Ctrl+A, select mode. The item view models carry IsSelected; this list is the source of truth.
public partial class ThreadViewModel
{
    private readonly List<ThreadItemViewModel> _selection = [];
    private ThreadItemViewModel? _anchor;

    /// <summary>Select mode shows checkboxes and the bulk action bar even with one or no selected item.</summary>
    [ObservableProperty]
    public partial bool IsSelectMode { get; set; }

    public int SelectedCount => _selection.Count;

    public bool HasSelection => _selection.Count > 0;

    /// <summary>Checkboxes and the bulk action bar are visible (select mode, or more than one item selected).</summary>
    public bool IsSelectionActive => IsSelectMode || _selection.Count > 1;

    public string SelectionText => _selection.Count == 1 ? L.Get("Thread_SelectedOne") : L.Format("Thread_SelectedMany", _selection.Count);

    /// <summary>Every selected file is starred, so the star action unstars them. A mixed selection gets starred.</summary>
    public bool IsSelectionStarred => _selection.Count > 0 && _selection.TrueForAll(i => i.IsStarred);

    /// <summary>Label of the selection bar's star action: "Star", or "Unstar" when every selected file is starred.</summary>
    public string StarActionText => L.Get(IsSelectionStarred ? "Thread_BulkUnstar" : "Thread_BulkStar");

    public IReadOnlyList<ThreadItemViewModel> SelectedItems => _selection;

    /// <summary>A click on a bubble, tile or row. In select mode (or with Ctrl) it toggles.</summary>
    public void ClickItem(ThreadItemViewModel item, bool ctrl, bool shift) => ClickItems([item], ctrl, shift);

    /// <summary>A click on a group (an album bubble): the whole group is selected or toggled together.</summary>
    public void ClickItems(IReadOnlyList<ThreadItemViewModel> group, bool ctrl, bool shift)
    {
        if (group.Count == 0)
        {
            return;
        }

        if (shift && _anchor is not null)
        {
            SelectRange(_anchor, group[^1], additive: ctrl);
        }
        else if (ctrl || IsSelectionActive)
        {
            var select = !group.All(i => i.IsSelected);
            foreach (var item in group)
            {
                SetSelected(item, select);
            }

            _anchor = group[0];
        }
        else
        {
            ClearSelectionCore();
            foreach (var item in group)
            {
                SetSelected(item, true);
            }

            _anchor = group[0];
        }

        _cursor = group[^1];
        SelectionChanged();
    }

    /// <summary>Makes <paramref name="group"/> the selection (right-click on an unselected item, deferred click).</summary>
    public void SelectOnly(IReadOnlyList<ThreadItemViewModel> group)
    {
        ClearSelectionCore();
        foreach (var item in group)
        {
            SetSelected(item, true);
        }

        _anchor = group.Count > 0 ? group[0] : null;
        _cursor = group.Count > 0 ? group[^1] : null;
        SelectionChanged();
    }

    public void ClearSelection()
    {
        ClearSelectionCore();
        SelectionChanged();
    }

    /// <summary>A click on empty space clears a plain selection (not in select mode).</summary>
    public void ClickEmptyArea()
    {
        if (!IsSelectionActive && _selection.Count > 0)
        {
            ClearSelection();
        }
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in CurrentOrder())
        {
            SetSelected(item, true);
        }

        SelectionChanged();
    }

    [RelayCommand]
    private void CancelSelection()
    {
        ClearSelectionCore();
        IsSelectMode = false;
        SelectionChanged();
    }

    [RelayCommand]
    private void ToggleSelectMode() => IsSelectMode = !IsSelectMode;

    partial void OnIsSelectModeChanged(bool value)
    {
        if (!value)
        {
            ClearSelectionCore();
        }

        SelectionChanged();
    }

    private void SelectRange(ThreadItemViewModel from, ThreadItemViewModel to, bool additive)
    {
        var order = CurrentOrder();
        var a = order.IndexOf(from);
        var b = order.IndexOf(to);
        if (!additive)
        {
            ClearSelectionCore();
        }

        if (a < 0 || b < 0)
        {
            SetSelected(to, true);
            return;
        }

        for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
        {
            SetSelected(order[i], true);
        }
    }

    private void SetSelected(ThreadItemViewModel item, bool selected)
    {
        if (item.IsSelected == selected)
        {
            return;
        }

        item.IsSelected = selected;
        if (selected)
        {
            _selection.Add(item);
        }
        else
        {
            _selection.Remove(item);
        }
    }

    private void ClearSelectionCore()
    {
        foreach (var item in _selection)
        {
            item.IsSelected = false;
        }

        _selection.Clear();
    }

    private void PruneSelection()
    {
        _selection.RemoveAll(item =>
        {
            if (_itemsById.TryGetValue(item.Id, out var current) && ReferenceEquals(current, item))
            {
                return false;
            }

            item.IsSelected = false;
            return true;
        });

        if (_anchor is not null && !_itemsById.ContainsKey(_anchor.Id))
        {
            _anchor = null;
        }

        if (_cursor is not null && !(_itemsById.TryGetValue(_cursor.Id, out var cursor) && ReferenceEquals(cursor, _cursor)))
        {
            _cursor = null;
        }
    }

    private void SelectionChanged()
    {
        foreach (var album in _albums)
        {
            album.IsSelected = album.Items.All(i => i.IsSelected);
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectionActive));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsSelectionStarred));
        OnPropertyChanged(nameof(StarActionText));
    }

    /// <summary>Snapshot of the selection in display order (actions run on this; in the Gallery, its own order).</summary>
    private List<ThreadItemViewModel> SelectedTargets()
    {
        if (_selection.Count <= 1)
        {
            return [.. _selection];
        }

        var set = new HashSet<ThreadItemViewModel>(_selection);
        var order = ViewMode == ThreadViewMode.Gallery ? GetGalleryOrder() : _items;
        return order.Where(set.Contains).ToList();
    }

    private List<ThreadItemViewModel> CurrentOrder() => ViewMode switch
    {
        ThreadViewMode.Grid => GetGridOrder(),
        ThreadViewMode.Gallery => GetGalleryOrder(),
        ThreadViewMode.Details => [.. DetailsItems],
        _ => _items,
    };
}
