using Cabinette.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>Arrow-key moves between Gallery cells (<see cref="ThreadViewModel.MoveGalleryCursor"/>).</summary>
public enum GalleryMove
{
    Left = 0,
    Right,
    Up,
    Down,
    First,
    Last,
}

// The Gallery view (SPEC.md section 4, "Gallery"): offered only in the Images category, its subcategories and their "All in"
// rows. Its rows are same-size cells under day headers, built from the item count alone (see GalleryBuilder).
public partial class ThreadViewModel
{
    private List<ThreadItemViewModel> _galleryOrder = [];
    private DateTime[] _galleryDays = [];
    private IReadOnlyList<ThreadItemViewModel>? _galleryOrderSource;
    private GallerySort _galleryOrderSort;
    private int _galleryColumns = 4;
    private bool _isGalleryOffered;
    private GallerySort _gallerySort = GallerySort.Default;
    private ThreadItemViewModel? _cursor;
    private Dictionary<ThreadItemViewModel, (int Row, int Column)>? _galleryPositions;
    private IReadOnlyList<GallerySortHeader>? _gallerySortHeaders;

    /// <summary>Gallery rows (day headers and rows of cells). Only filled while the Gallery is shown.</summary>
    public RangeObservableCollection<ThreadRow> GalleryRows { get; } = [];

    /// <summary>
    /// The thread is the Images category, one of its subcategories or their "All in" row (found by the built-in key, so
    /// renaming doesn't matter): the view toggle offers the Gallery, and the thread opens in it unless another view was chosen.
    /// </summary>
    public bool IsGalleryOffered
    {
        get => _isGalleryOffered;
        private set => SetProperty(ref _isGalleryOffered, value);
    }

    public bool IsGalleryView
    {
        get => ViewMode == ThreadViewMode.Gallery;
        set
        {
            if (value)
            {
                ViewMode = ThreadViewMode.Gallery;
            }
        }
    }

    // ------------------------------------------------------------------ sort header

    /// <summary>The sort header's buttons: Name, Size, Type and Date (titled with the date in use).</summary>
    public IReadOnlyList<GallerySortHeader> GallerySortHeaders => _gallerySortHeaders ??= CreateGallerySortHeaders();

    /// <summary>The Gallery's sort, remembered per category while the app runs. The default is date added, newest first.</summary>
    public GallerySort GallerySort => _gallerySort;

    public bool IsGalleryDateTaken => _gallerySort.DateKind == GalleryDateKind.Taken;

    /// <summary>A click on a sort header button: sort by it, or reverse when it already sorts.</summary>
    [RelayCommand]
    public void SortGalleryBy(GallerySortKey key)
        => ApplyGallerySort(key == _gallerySort.Key
            ? _gallerySort with { Ascending = !_gallerySort.Ascending }
            : _gallerySort with { Key = key, Ascending = GallerySort.StartsAscending(key) });

    /// <summary>The Date button's choice: sections (and the Date sort) by date added or date taken. Sorts by that date.</summary>
    [RelayCommand]
    public void SetGalleryDateKind(GalleryDateKind kind)
    {
        if (_gallerySort.Key == GallerySortKey.Date)
        {
            ApplyGallerySort(_gallerySort with { DateKind = kind });
        }
        else
        {
            ApplyGallerySort(new GallerySort(GallerySortKey.Date, GallerySort.StartsAscending(GallerySortKey.Date), kind));
        }
    }

    // ------------------------------------------------------------------ cell size

    /// <summary>The Gallery's cell size (Ctrl+mouse wheel), remembered across restarts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GalleryCellPixels))]
    [NotifyCanExecuteChangedFor(nameof(GrowGalleryCellsCommand), nameof(ShrinkGalleryCellsCommand))]
    public partial GalleryCellSize GalleryCellSize { get; set; } = GalleryCellSize.Medium;

    /// <summary>The photo box of a cell in device-independent pixels (96, 128 or 176).</summary>
    public double GalleryCellPixels => GalleryMetrics.CellSize(GalleryCellSize);

    /// <summary>One size step: positive grows the cells, negative shrinks them. False when already at that end.</summary>
    public bool StepGalleryCellSize(int steps)
    {
        var next = (GalleryCellSize)Math.Clamp((int)GalleryCellSize + Math.Sign(steps), (int)GalleryCellSize.Small, (int)GalleryCellSize.Large);
        if (steps == 0 || next == GalleryCellSize)
        {
            return false;
        }

        GalleryCellSize = next;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanGrowGalleryCells))]
    private void GrowGalleryCells() => StepGalleryCellSize(1);

    [RelayCommand(CanExecute = nameof(CanShrinkGalleryCells))]
    private void ShrinkGalleryCells() => StepGalleryCellSize(-1);

    private bool CanGrowGalleryCells() => GalleryCellSize < GalleryCellSize.Large;

    private bool CanShrinkGalleryCells() => GalleryCellSize > GalleryCellSize.Small;

    partial void OnGalleryCellSizeChanged(GalleryCellSize value)
    {
        if (_s.ViewState.GallerySize != value)
        {
            _s.ViewState.GallerySize = value;
        }
    }

    /// <summary>The Gallery view reports how many cells fit in a row (it follows from the pane width and the cell size).</summary>
    public void SetGalleryColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _galleryColumns)
        {
            return;
        }

        _galleryColumns = columns;
        if (ViewMode == ThreadViewMode.Gallery)
        {
            RebuildGalleryRows();
        }
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>
    /// The arrow keys in the Gallery: moves to the next cell in that direction (Up and Down keep the column, across day
    /// sections), selects it (with <paramref name="extend"/>, the range from the anchor, like Shift-click) and returns it so
    /// the view can bring it into view. With no cell to start from, the first press picks <paramref name="start"/> (the first
    /// cell on screen) or the first cell. Null when the Gallery is empty.
    /// </summary>
    public ThreadItemViewModel? MoveGalleryCursor(GalleryMove move, bool extend = false, int count = 1, ThreadItemViewModel? start = null)
    {
        var order = GetGalleryOrder();
        if (order.Count == 0)
        {
            return null;
        }

        var positions = GetGalleryPositions();
        ThreadItemViewModel target;
        if (_cursor is not { } current || !positions.ContainsKey(current))
        {
            target = move switch
            {
                GalleryMove.First => order[0],
                GalleryMove.Last => order[^1],
                _ => start is not null && positions.ContainsKey(start) ? start : order[0],
            };
        }
        else
        {
            target = Neighbor(current, move, Math.Max(1, count), order, positions);
        }

        if (extend)
        {
            var anchor = _anchor is not null && positions.ContainsKey(_anchor) ? _anchor : _cursor ?? target;
            SelectRange(anchor, target, additive: false);
            _anchor = anchor;
            SelectionChanged();
        }
        else
        {
            SelectOnly([target]);
        }

        _cursor = target;
        return target;
    }

    private ThreadItemViewModel Neighbor(ThreadItemViewModel current, GalleryMove move, int count,
        List<ThreadItemViewModel> order, Dictionary<ThreadItemViewModel, (int Row, int Column)> positions)
    {
        switch (move)
        {
            case GalleryMove.First:
                return order[0];
            case GalleryMove.Last:
                return order[^1];
            case GalleryMove.Left or GalleryMove.Right:
                {
                    var index = order.IndexOf(current);
                    index = Math.Clamp(index + (move == GalleryMove.Right ? count : -count), 0, order.Count - 1);
                    return order[index];
                }
        }

        // Up and Down: the cell in the same column of the row above or below (or the last cell of a shorter row).
        var (row, column) = positions[current];
        var step = move == GalleryMove.Down ? 1 : -1;
        var found = row;
        for (var i = row + step; i >= 0 && i < GalleryRows.Count && count > 0; i += step)
        {
            if (GalleryRows[i] is GalleryCellRow)
            {
                found = i;
                count--;
            }
        }

        if (found == row || GalleryRows[found] is not GalleryCellRow cells)
        {
            return current;
        }

        return cells.Items[Math.Min(column, cells.Items.Count - 1)];
    }

    // ------------------------------------------------------------------ loading and building

    /// <summary>
    /// Whether the thread offers the Gallery, and the view it opens in: the one chosen for it in this session, else the
    /// Gallery where offered and Chat elsewhere. Also restores the Gallery's sort and cell size.
    /// </summary>
    private async Task<ThreadViewMode> PrepareGalleryAsync(long categoryId)
    {
        var offered = !IsStarredView && categoryId != StarredViewId && await IsGalleryThreadAsync(categoryId);
        IsGalleryOffered = offered;
        _gallerySort = _s.ViewState.GetGallerySort(categoryId);
        UpdateGallerySortHeaders();
        if (offered)
        {
            await _s.ViewState.GallerySizeLoaded;
            GalleryCellSize = _s.ViewState.GallerySize;
        }

        var mode = _s.ViewState.GetViewMode(categoryId, offered ? ThreadViewMode.Gallery : ThreadViewMode.Chat);
        return mode == ThreadViewMode.Gallery && !offered ? ThreadViewMode.Chat : mode;
    }

    private async Task<bool> IsGalleryThreadAsync(long categoryId)
    {
        try
        {
            var ct = _lifetime.Token;
            var path = await Task.Run(() => _s.Library.GetCategoryPathAsync(categoryId, ct), ct);
            return path is not null && GalleryBuilder.IsGalleryPath(path);
        }
        catch (Exception)
        {
            // No path (the category is gone, or the load was cancelled): the thread opens like any other.
            return false;
        }
    }

    private void ApplyGallerySort(GallerySort sort)
    {
        if (sort == _gallerySort)
        {
            return;
        }

        _gallerySort = sort;
        if (IsLoaded)
        {
            _s.ViewState.SetGallerySort(CategoryId, sort);
        }

        UpdateGallerySortHeaders();
        OnPropertyChanged(nameof(GallerySort));
        OnPropertyChanged(nameof(IsGalleryDateTaken));
        if (ViewMode == ThreadViewMode.Gallery)
        {
            RebuildGalleryRows();

            // A new order starts at its beginning; the chat keeps its place.
            _host?.ScrollGalleryToStart();
        }
    }

    private List<GallerySortHeader> CreateGallerySortHeaders()
    {
        List<GallerySortHeader> headers =
        [
            new(GallerySortKey.Name, L.Get("Thread_ColumnName")),
            new(GallerySortKey.Size, L.Get("Thread_ColumnSize")),
            new(GallerySortKey.Type, L.Get("Thread_ColumnType")),
            new(GallerySortKey.Date, L.Get("Thread_ColumnDateAdded")),
        ];
        _gallerySortHeaders = headers;
        UpdateGallerySortHeaders();
        return headers;
    }

    private void UpdateGallerySortHeaders()
    {
        if (_gallerySortHeaders is null)
        {
            return;
        }

        foreach (var header in _gallerySortHeaders)
        {
            header.IsSorted = header.Key == _gallerySort.Key;
            header.IsAscending = _gallerySort.Ascending;
            if (header.Key == GallerySortKey.Date)
            {
                header.Title = L.Get(_gallerySort.DateKind == GalleryDateKind.Taken ? "Thread_GalleryDateTaken" : "Thread_ColumnDateAdded");
            }
        }
    }

    /// <summary>The items in Gallery order (sorted again only when the items or the sort changed).</summary>
    private List<ThreadItemViewModel> GetGalleryOrder()
    {
        if (!ReferenceEquals(_galleryOrderSource, _items) || _galleryOrderSort != _gallerySort)
        {
            _galleryOrder = GalleryBuilder.Order(_items, _gallerySort, out _galleryDays);
            _galleryOrderSource = _items;
            _galleryOrderSort = _gallerySort;
            _galleryPositions = null;
        }

        return _galleryOrder;
    }

    private void RebuildGalleryRows()
    {
        var order = GetGalleryOrder();
        var rows = GalleryBuilder.BuildRows(order, _galleryDays, _galleryColumns, DateTime.Today);
        _galleryPositions = null;
        ThreadRowBuilder.ApplyDiff(GalleryRows, rows);
    }

    private Dictionary<ThreadItemViewModel, (int Row, int Column)> GetGalleryPositions()
    {
        if (_galleryPositions is not null)
        {
            return _galleryPositions;
        }

        var positions = new Dictionary<ThreadItemViewModel, (int, int)>(_galleryOrder.Count, ReferenceEqualityComparer.Instance);
        for (var row = 0; row < GalleryRows.Count; row++)
        {
            if (GalleryRows[row] is GalleryCellRow cells)
            {
                for (var column = 0; column < cells.Items.Count; column++)
                {
                    positions[cells.Items[column]] = (row, column);
                }
            }
        }

        _galleryPositions = positions;
        return positions;
    }
}
