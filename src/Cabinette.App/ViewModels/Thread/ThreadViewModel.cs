using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>
/// View model of the thread pane (right side). Owned by the thread workstream.
/// The shell creates one per opened category through <see cref="IThreadViewModelFactory"/>, calls <see cref="LoadAsync"/>,
/// shows it in a <see cref="Views.Thread.ThreadView"/>, and disposes it when another thread is opened.
/// </summary>
public partial class ThreadViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan QueryDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MarkViewedDebounce = TimeSpan.FromSeconds(1);

    private readonly ThreadServices _s;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _changeTimer;
    private readonly DispatcherTimer _queryTimer;
    private readonly DispatcherTimer _markViewedTimer;
    private CancellationTokenSource? _reloadCts;
    private int _reloadVersion;
    private bool _pendingItems;
    private bool _pendingHeader;
    private bool _pendingSettings;
    private bool _subscribed;
    private bool _disposed;
    private Dictionary<long, ThreadItemViewModel> _itemsById = [];
    private List<ThreadItemViewModel> _items = [];
    private List<ThreadItemViewModel>? _gridOrder;
    private volatile HashSet<long> _displayedIds = [];
    private volatile HashSet<long> _displayedCategoryIds = [];
    private volatile bool _isStarredView;
    private List<AlbumRow> _albums = [];
    private AppSettings? _settings;
    private string _pathText = "";
    private int _categoryItemCount;
    private long _categoryTotalSize;
    private long? _pendingFocusId;
    private IThreadViewHost? _host;
    private bool _scrollToEndOnArrival;
    private int _gridColumns = 4;
    private AudioPlayback? _audio;
    private DetailsColumn _sortColumn = DetailsColumn.DateAdded;
    private bool _sortAscending;

    public ThreadViewModel(ThreadServices services)
    {
        _s = services;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _changeTimer = CreateTimer(ChangeDebounce, OnChangeTimer);
        _queryTimer = CreateTimer(QueryDebounce, OnQueryTimer);
        _markViewedTimer = CreateTimer(MarkViewedDebounce, OnMarkViewedTimer);

        Filter = new ThreadFilterViewModel();
        Filter.Changed += OnFilterChanged;
        DetailsColumns =
        [
            new(DetailsColumn.Name, L.Get("Thread_ColumnName")),
            new(DetailsColumn.Type, L.Get("Thread_ColumnType")),
            new(DetailsColumn.Size, L.Get("Thread_ColumnSize")),
            new(DetailsColumn.DateAdded, L.Get("Thread_ColumnDateAdded")) { IsSorted = true },
            new(DetailsColumn.Mode, L.Get("Thread_ColumnMode")),
            new(DetailsColumn.Tags, L.Get("Thread_ColumnTags")),
        ];

        CategoryName = "";
        CategoryInitial = "";
        Subtitle = "";
        SearchText = "";
        ComposerText = "";
        CanAddFiles = true;
    }

    /// <summary><see cref="CategoryId"/> of the Starred view, which shows no category (category ids are positive).</summary>
    public const long StarredViewId = -1;

    public long CategoryId { get; private set; }

    /// <summary>
    /// The Starred view (SPEC.md section 4, opened with <see cref="LoadStarredAsync"/>): every starred file from every
    /// category, in the order they were starred. Read-only like "Deleted": no message box, paperclip, drops or pastes, and
    /// no "Move to category…" or "Remove from this category" (there is no category to leave). Unstarring a file removes it.
    /// </summary>
    public bool IsStarredView
    {
        get => _isStarredView;
        private set
        {
            if (_isStarredView != value)
            {
                // Volatile: library change events read it on a background thread.
                _isStarredView = value;
                OnPropertyChanged();
            }
        }
    }

    // ------------------------------------------------------------------ collections

    /// <summary>Flattened chat rows (date headers, bubbles, albums), oldest first.</summary>
    public RangeObservableCollection<ThreadRow> ChatRows { get; } = [];

    /// <summary>Grid gallery rows (month headers and tile rows), newest first. Only filled while the grid is shown.</summary>
    public RangeObservableCollection<ThreadRow> GridRows { get; } = [];

    /// <summary>Details list, sorted by <see cref="DetailsColumns"/>. Only filled while the details view is shown.</summary>
    public RangeObservableCollection<ThreadItemViewModel> DetailsItems { get; } = [];

    public IReadOnlyList<DetailsColumnHeader> DetailsColumns { get; }

    public ObservableCollection<SubcategoryChip> Subcategories { get; } = [];

    public ThreadFilterViewModel Filter { get; }

    // ------------------------------------------------------------------ header

    /// <summary>The banner at the top of the thread (SPEC.md section 4): color, watermark, name, file count and total size.</summary>
    public ThreadBanner Banner { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropText))]
    public partial string CategoryName { get; set; }

    [ObservableProperty]
    public partial string CategoryInitial { get; set; }

    [ObservableProperty]
    public partial ImageSource? CategoryIcon { get; set; }

    [ObservableProperty]
    public partial string Subtitle { get; set; }

    [ObservableProperty]
    public partial bool HasSubcategories { get; set; }

    /// <summary>
    /// False for the system "Deleted" category: files only arrive there when their category is deleted, so the thread
    /// takes no drops, paste, paperclip or notes.
    /// </summary>
    [ObservableProperty]
    public partial bool CanAddFiles { get; set; }

    public string DropText => L.Format("Thread_DropToAdd", CategoryName);

    // ------------------------------------------------------------------ view mode, search, filter

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatView), nameof(IsGridView), nameof(IsDetailsView), nameof(IsGalleryView))]
    public partial ThreadViewMode ViewMode { get; set; }

    public bool IsChatView
    {
        get => ViewMode == ThreadViewMode.Chat;
        set
        {
            if (value)
            {
                ViewMode = ThreadViewMode.Chat;
            }
        }
    }

    public bool IsGridView
    {
        get => ViewMode == ThreadViewMode.Grid;
        set
        {
            if (value)
            {
                ViewMode = ThreadViewMode.Grid;
            }
        }
    }

    public bool IsDetailsView
    {
        get => ViewMode == ThreadViewMode.Details;
        set
        {
            if (value)
            {
                ViewMode = ThreadViewMode.Details;
            }
        }
    }

    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    /// <summary>Thread-only search over file names and note text; "#tag" tokens filter by tags. Debounced.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial bool IsFilterOpen { get; set; }

    [ObservableProperty]
    public partial bool IsFilterActive { get; set; }

    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText) || IsFilterActive;

    // ------------------------------------------------------------------ state

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsNoResults))]
    public partial bool IsLoaded { get; set; }

    /// <summary>Number of items currently listed (after search and filters).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsNoResults), nameof(HasItems))]
    public partial int TotalCount { get; set; }

    public bool HasItems => TotalCount > 0;

    /// <summary>The thread has no files at all (friendly empty state).</summary>
    public bool IsEmpty => IsLoaded && TotalCount == 0 && !HasQuery;

    /// <summary>Search or filters matched nothing.</summary>
    public bool IsNoResults => IsLoaded && TotalCount == 0 && HasQuery;

    // ------------------------------------------------------------------ composer

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendNoteCommand))]
    public partial string ComposerText { get; set; }

    public bool CanSend => !string.IsNullOrWhiteSpace(ComposerText);

    // ------------------------------------------------------------------ services used by item view models and views

    internal ThumbnailLoader Loader => _s.Loader;

    internal CancellationToken Lifetime => _lifetime.Token;

    internal AudioPlayback Audio => _audio ??= new AudioPlayback(item =>
        _s.Notifications.Show(L.Format("Thread_CannotPlay", item.FileName), Services.NotificationKind.Warning, L.Get("Thread_OpenAction"), () => OpenExternally(item)));

    /// <summary>The newest-first media list used by the viewer, the grid and the dev harness.</summary>
    public IReadOnlyList<ThreadItemViewModel> Items => _items;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Loads the category's items (own + descendants) and marks it viewed.</summary>
    public virtual Task LoadAsync(long categoryId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsStarredView = false;
        SetDateColumnTitle("Thread_ColumnDateAdded");
        return LoadCoreAsync(categoryId);
    }

    /// <summary>The details view's date column: "Date added" in a thread, "Starred" (when they were starred) in the Starred view.</summary>
    private void SetDateColumnTitle(string key)
    {
        foreach (var column in DetailsColumns.Where(c => c.Column == DetailsColumn.DateAdded))
        {
            column.Title = L.Get(key);
        }
    }

    /// <summary>Loads the Starred view instead of a category (see <see cref="IsStarredView"/>).</summary>
    public virtual Task LoadStarredAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsStarredView = true;
        CanAddFiles = false;
        CategoryName = L.Get("Thread_StarredTitle");
        SetDateColumnTitle("Thread_ColumnStarred");
        return LoadCoreAsync(StarredViewId);
    }

    private async Task LoadCoreAsync(long categoryId)
    {
        CategoryId = categoryId;
        IsLoading = true;
        if (!_subscribed)
        {
            _s.Library.Changed += OnLibraryChanged;
            _subscribed = true;
        }

        try
        {
            // The view is known before the items arrive (a quick look at the category's path: Images opens in the Gallery).
            var viewMode = PrepareGalleryAsync(categoryId);
            var header = ReloadHeaderAsync();
            var settings = LoadSettingsAsync();
            ViewMode = await viewMode;
            await ReloadItemsAsync(reset: true, reportErrors: true);
            await header;
            await settings;
        }
        finally
        {
            IsLoading = false;
            IsLoaded = true;
            NotifyStateChanged();
        }

        _ = MarkViewedAsync();
        if (_pendingFocusId is { } focusId)
        {
            _pendingFocusId = null;
            FocusItem(focusId);
        }
    }

    /// <summary>Scrolls to and briefly highlights an item (used when opening a search result).</summary>
    public virtual void FocusItem(long itemId)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsLoaded || _host is null)
        {
            _pendingFocusId = itemId;
            return;
        }

        if (!_itemsById.TryGetValue(itemId, out var item))
        {
            if (HasQuery)
            {
                // The item is hidden by the search or filters: clear them and reload at once. The reload that applies next
                // brings the item into view instead of scrolling to the end (see TakePendingFocus in Apply), however long
                // it takes. Clearing the query restarted the debounced query reload; stop it, or a quarter of a second later
                // its reset would scroll to the end again (or, with a slow load, cancel this reload).
                _pendingFocusId = itemId;
                ClearQuery();
                _queryTimer.Stop();
                _ = ReloadItemsAsync(reset: true);
            }
            else if (_pendingFocusId is not null)
            {
                // That reload is still on its way (the list shown is the filtered one): it takes the latest request.
                _pendingFocusId = itemId;
            }

            return;
        }

        _pendingFocusId = null;
        Focus(item);
    }

    private void Focus(ThreadItemViewModel item)
    {
        _ = FlashAsync(item);
        _host?.BringItemIntoView(item);
    }

    /// <summary>
    /// The item a <see cref="FocusItem"/> call waits for, once a reload has applied and the view is attached: null when nothing
    /// waits. When the applied items don't list it (it left the thread, or a new search hides it), the request is dropped, so
    /// a later reload never jumps to it out of the blue. Before the first load has finished, <see cref="LoadCoreAsync"/> handles it.
    /// </summary>
    private ThreadItemViewModel? TakePendingFocus()
    {
        if (_pendingFocusId is not { } id || !IsLoaded || _host is null)
        {
            return null;
        }

        _pendingFocusId = null;
        return _itemsById.GetValueOrDefault(id);
    }

    /// <summary>The view calls this when it starts or stops showing this view model.</summary>
    public void AttachHost(IThreadViewHost? host)
    {
        _host = host;
        if (host is not null && IsLoaded && _pendingFocusId is { } focusId)
        {
            _pendingFocusId = null;
            _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusItem(focusId));
        }
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribed)
        {
            _s.Library.Changed -= OnLibraryChanged;
            _subscribed = false;
        }

        Filter.Changed -= OnFilterChanged;
        _lifetime.Cancel();
        _reloadCts?.Cancel();
        _changeTimer.Stop();
        _queryTimer.Stop();
        _markViewedTimer.Stop();
        _audio?.Dispose();
        _host = null;
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ view mode

    partial void OnViewModeChanged(ThreadViewMode value)
    {
        if (IsLoaded)
        {
            _s.ViewState.SetViewMode(CategoryId, value);
        }

        RebuildSecondaryViews();
    }

    /// <summary>The grid view reports how many tiles fit in a row.</summary>
    public void SetGridColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _gridColumns)
        {
            return;
        }

        _gridColumns = columns;
        if (ViewMode == ThreadViewMode.Grid)
        {
            RebuildGridRows();
        }
    }

    public void SortDetailsBy(DetailsColumn column)
    {
        if (column == _sortColumn)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = column is DetailsColumn.Name or DetailsColumn.Type or DetailsColumn.Mode or DetailsColumn.Tags;
        }

        foreach (var header in DetailsColumns)
        {
            header.IsSorted = header.Column == _sortColumn;
            header.IsAscending = _sortAscending;
        }

        RebuildDetails();
    }

    private void RebuildSecondaryViews()
    {
        if (ViewMode == ThreadViewMode.Grid)
        {
            RebuildGridRows();
        }
        else if (GridRows.Count > 0)
        {
            GridRows.ReplaceAll([]);
        }

        if (ViewMode == ThreadViewMode.Gallery)
        {
            RebuildGalleryRows();
        }
        else if (GalleryRows.Count > 0)
        {
            GalleryRows.ReplaceAll([]);
            _galleryPositions = null;
        }

        if (ViewMode == ThreadViewMode.Details)
        {
            RebuildDetails();
        }
        else if (DetailsItems.Count > 0)
        {
            DetailsItems.ReplaceAll([]);
        }
    }

    private List<ThreadItemViewModel> GetGridOrder() => _gridOrder ??= Enumerable.Reverse(_items).ToList();

    private void RebuildGridRows()
    {
        var order = GetGridOrder();
        var columns = Math.Max(1, _gridColumns);
        var rows = new List<ThreadRow>((order.Count / columns) + 16);
        var today = DateTime.Today;
        var i = 0;
        while (i < order.Count)
        {
            var local = order[i].ListedAt.ToLocalTime();
            var month = new DateTime(local.Year, local.Month, 1);
            rows.Add(new GridHeaderRow(month, ThreadFormat.MonthHeader(month, today)));
            var chunk = new List<ThreadItemViewModel>(columns);
            while (i < order.Count)
            {
                var added = order[i].ListedAt.ToLocalTime();
                if (added.Year != month.Year || added.Month != month.Month)
                {
                    break;
                }

                chunk.Add(order[i]);
                i++;
                if (chunk.Count == columns)
                {
                    rows.Add(new GridTileRow(chunk));
                    chunk = new List<ThreadItemViewModel>(columns);
                }
            }

            if (chunk.Count > 0)
            {
                rows.Add(new GridTileRow(chunk));
            }
        }

        GridRows.ReplaceAll(rows);
    }

    private void RebuildDetails()
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ThreadItemViewModel> sorted = _sortColumn switch
        {
            DetailsColumn.Name => _sortAscending ? _items.OrderBy(i => i.FileName, comparer) : _items.OrderByDescending(i => i.FileName, comparer),
            DetailsColumn.Type => _sortAscending ? _items.OrderBy(i => i.TypeText, comparer) : _items.OrderByDescending(i => i.TypeText, comparer),
            DetailsColumn.Size => _sortAscending ? _items.OrderBy(i => i.SizeBytes) : _items.OrderByDescending(i => i.SizeBytes),
            DetailsColumn.Mode => _sortAscending ? _items.OrderBy(i => i.IsPointer) : _items.OrderByDescending(i => i.IsPointer),
            DetailsColumn.Tags => _sortAscending ? _items.OrderBy(i => i.TagsText, comparer) : _items.OrderByDescending(i => i.TagsText, comparer),
            _ => _sortAscending ? _items.OrderBy(i => i.ListedAt) : _items.OrderByDescending(i => i.ListedAt), // the star time in the Starred view
        };

        DetailsItems.ReplaceAll(_sortColumn == DetailsColumn.Name ? sorted : sorted.ThenBy(i => i.FileName, comparer));
    }

    // ------------------------------------------------------------------ search & filter

    partial void OnSearchTextChanged(string value)
    {
        if (IsLoaded)
        {
            RestartTimer(_queryTimer);
        }
    }

    partial void OnIsSearchOpenChanged(bool value)
    {
        if (!value && SearchText.Length > 0)
        {
            SearchText = "";
        }
    }

    [RelayCommand]
    private void ClearQuery()
    {
        SearchText = "";
        IsSearchOpen = false;
        Filter.Reset();
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        IsFilterActive = Filter.IsActive;
        RestartTimer(_queryTimer);
    }

    private void OnQueryTimer(object? sender, EventArgs e)
    {
        _queryTimer.Stop();
        if (!_disposed && IsLoaded)
        {
            _ = ReloadItemsAsync(reset: true);
        }
    }

    // ------------------------------------------------------------------ loading

    private Task ReloadHeaderAsync() => IsStarredView ? LoadStarredHeaderAsync() : LoadHeaderAsync();

    /// <summary>
    /// The Starred view's header: a star banner in the accent, "Starred", the number of starred files and their total size.
    /// No subcategories.
    /// </summary>
    private async Task LoadStarredHeaderAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            var (count, totalSize) = await Task.Run(
                async () =>
                {
                    var starred = await _s.Library.GetStarredItemsAsync(null, null, ct).ConfigureAwait(false);
                    return (starred.Count, starred.Sum(i => i.SizeBytes ?? 0));
                },
                ct);
            if (_disposed || !IsStarredView)
            {
                return;
            }

            CategoryName = L.Get("Thread_StarredTitle");
            CategoryInitial = "";
            CategoryIcon = null;
            CanAddFiles = false;
            _categoryItemCount = count;
            _categoryTotalSize = totalSize;
            Banner.Apply(ThreadBanner.StarredLook);
            Banner.Title = CategoryName;
            Banner.ToolTipText = CategoryName;
            _pathText = "";
            Subcategories.Clear();
            HasSubcategories = false;
            UpdateSubtitle();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The header keeps its previous content; item errors are reported by the item load.
        }
    }

    private async Task LoadHeaderAsync()
    {
        var id = CategoryId;
        var ct = _lifetime.Token;
        try
        {
            var header = await Task.Run(
                async () =>
                {
                    var library = _s.Library;
                    var category = await library.GetCategoryAsync(id, ct).ConfigureAwait(false);
                    var path = await library.GetCategoryPathAsync(id, ct).ConfigureAwait(false);
                    var summary = await library.GetCategorySummaryAsync(id, ct).ConfigureAwait(false);
                    var children = await library.GetCategoriesAsync(id, CategorySort.Alphabetical, ct).ConfigureAwait(false);
                    var chips = children
                        .Select(c => new SubcategoryChip(c.Category.Id, c.Category.Name, c.ItemCount, c.UnreadCount, DecodePng(c.Category.IconPng, 48)))
                        .ToList();
                    var look = (category ?? summary?.Category) is { } shown ? ThreadBanner.ForCategory(shown, _s.IconGenerator) : null;
                    return (Category: category, Path: path, Summary: summary, Chips: chips, Icon: DecodePng(category?.IconPng, 96), Look: look);
                },
                ct);

            if (_disposed || id != CategoryId)
            {
                return;
            }

            var name = header.Category?.Name ?? header.Summary?.Category.Name ?? "";
            if ((header.Category ?? header.Summary?.Category) is { } category)
            {
                CanAddFiles = category.Kind != CategoryKind.System;
            }

            CategoryName = name;
            CategoryInitial = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "";
            CategoryIcon = header.Icon;
            _categoryItemCount = header.Summary?.ItemCount ?? 0;
            _categoryTotalSize = header.Summary?.TotalSizeBytes ?? 0;
            _pathText = header.Path.Count > 1 ? string.Join(" › ", header.Path.Select(c => c.Name)) : "";
            if (header.Look is { } look)
            {
                Banner.Apply(look);
            }

            Banner.Title = name;
            Banner.ToolTipText = _pathText.Length > 0 ? _pathText : name;
            Subcategories.Clear();
            foreach (var chip in header.Chips)
            {
                Subcategories.Add(chip);
            }

            HasSubcategories = Subcategories.Count > 0;
            UpdateSubtitle();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The header keeps its previous content; item errors are reported by the item load.
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await Task.Run(() => _s.Library.GetSettingsAsync(_lifetime.Token));
        }
        catch (Exception)
        {
            // Delete confirmations fall back to the defaults.
        }
    }

    private async Task ReloadItemsAsync(bool reset = false, bool reportErrors = false)
    {
        _reloadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _reloadCts = cts;
        var version = ++_reloadVersion;
        var id = CategoryId;
        var starred = IsStarredView;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var filter = Filter.Build();
        var snapshot = new Dictionary<long, ThreadItemViewModel>(_itemsById);
        var today = DateTime.Today;

        ThreadRowBuilder.Result result;
        try
        {
            result = await Task.Run(
                async () =>
                {
                    var query = filter.IsEmpty ? null : filter;
                    var items = starred
                        ? await _s.Library.GetStarredItemsAsync(search, query, cts.Token).ConfigureAwait(false)
                        : await _s.Library.GetThreadItemsAsync(id, search, query, cts.Token).ConfigureAwait(false);
                    cts.Token.ThrowIfCancellationRequested();
                    return ThreadRowBuilder.Build(items, snapshot, this, today, byStarredTime: starred);
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (reportErrors && !_disposed)
            {
                ShowError(ex);
            }

            return;
        }

        if (version != _reloadVersion || _disposed)
        {
            return;
        }

        Apply(result, reset);
    }

    private void Apply(ThreadRowBuilder.Result result, bool reset)
    {
        foreach (var (vm, item) in result.Updates)
        {
            vm.Update(item);
        }

        var previous = _itemsById;
        var arrivals = reset ? 0 : result.Items.Count(i => !previous.ContainsKey(i.Id));

        _items = result.Items;
        _itemsById = result.ById;
        _gridOrder = null;
        _displayedIds = [.. result.ById.Keys];
        if (IsStarredView)
        {
            // Renaming or moving one of these categories changes the paths of the files shown.
            _displayedCategoryIds = [.. result.Items.SelectMany(i => i.Item.CategoryIds)];
        }

        if (_audio?.Current is { } playing && !_itemsById.ContainsKey(playing.Id))
        {
            _audio.Stop();
        }

        PruneSelection();
        if (reset)
        {
            ChatRows.ReplaceAll(result.Rows);
            _albums = result.Albums;
        }
        else
        {
            // The diff keeps the rows already shown when their key is unchanged, so the albums on screen may be the
            // earlier instances: the album selection and FocusItem's "+N" flash must reach those.
            ThreadRowBuilder.ApplyDiff(ChatRows, result.Rows);
            _albums = ChatRows.OfType<AlbumRow>().ToList();
        }

        TotalCount = _items.Count;
        RebuildSecondaryViews();
        SelectionChanged();
        UpdateSubtitle();
        NotifyStateChanged();

        if (TakePendingFocus() is { } focus)
        {
            // FocusItem cleared the search or filters to show this item: scroll to it, not to the end.
            if (arrivals > 0)
            {
                RestartTimer(_markViewedTimer);
            }

            Focus(focus);
        }
        else if (reset)
        {
            _host?.ScrollToEnd();
        }
        else if (arrivals > 0)
        {
            RestartTimer(_markViewedTimer);
            if (_scrollToEndOnArrival)
            {
                _scrollToEndOnArrival = false;
                _host?.ScrollToEnd();
            }
            else
            {
                _host?.NotifyNewItems(arrivals);
            }
        }
    }

    private void UpdateSubtitle()
    {
        var count = HasQuery
            ? L.Format("Thread_ResultCount", TotalCount, Math.Max(_categoryItemCount, TotalCount))
            : ThreadFormat.FileCount(IsLoaded || TotalCount > 0 ? TotalCount : _categoryItemCount);
        Subtitle = _pathText.Length > 0 ? $"{count} · {_pathText}" : count;

        // The banner: the count of the files listed, like the old subtitle, then the size of the whole category's files (also
        // during a search), which the header reload refreshes whenever the category's files change.
        Banner.CountText = count;
        Banner.SizeText = ThreadBanner.FormatSize(_categoryTotalSize);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsNoResults));
        OnPropertyChanged(nameof(HasQuery));
    }

    // ------------------------------------------------------------------ live updates

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        // Background thread.
        if (_disposed)
        {
            return;
        }

        var (items, header, settings) = _isStarredView
            ? ClassifyStarredChange(e, _displayedIds, _displayedCategoryIds)
            : ClassifyChange(e, CategoryId, _displayedIds);
        if (items || header || settings)
        {
            _dispatcher.BeginInvoke(() => ScheduleRefresh(items, header, settings));
        }
    }

    /// <summary>
    /// What a library change means for the Starred view: reload items and header (the count) when a star was given or taken,
    /// a starred file shown changed, or a category of the files shown changed (their paths may have); settings as usual.
    /// </summary>
    internal static (bool Items, bool Header, bool Settings) ClassifyStarredChange(LibraryChangedEventArgs e, IReadOnlySet<long> shownItemIds,
        IReadOnlySet<long> shownCategoryIds)
    {
        if (e.Kind == LibraryChangeKind.Viewed)
        {
            return default;
        }

        var settings = (e.Kind & LibraryChangeKind.Settings) != 0;
        var items = (e.Kind & LibraryChangeKind.Starred) != 0
                    || e.ItemIds.Any(shownItemIds.Contains)
                    || ((e.Kind & LibraryChangeKind.Categories) != 0 && e.CategoryIds.Any(shownCategoryIds.Contains));
        return (items, items, settings);
    }

    /// <summary>What a library change means for the thread of <paramref name="categoryId"/>: reload items, header, settings.</summary>
    internal static (bool Items, bool Header, bool Settings) ClassifyChange(LibraryChangedEventArgs e, long categoryId, IReadOnlySet<long> shownItemIds)
    {
        // Only unread state changed (a thread was viewed, usually this one): nothing shown here changed.
        // MarkViewedAsync clears the subcategory badges itself.
        if (e.Kind == LibraryChangeKind.Viewed)
        {
            return default;
        }

        var items = false;
        var header = false;
        var settings = (e.Kind & LibraryChangeKind.Settings) != 0;
        if (e.CategoryIds.Contains(categoryId))
        {
            items = true;
            header = true;
        }
        else if (e.ItemIds.Count > 0)
        {
            foreach (var itemId in e.ItemIds)
            {
                if (shownItemIds.Contains(itemId))
                {
                    items = true;
                    break;
                }
            }
        }

        if ((e.Kind & LibraryChangeKind.Categories) != 0)
        {
            header = true;
        }

        return (items, header, settings);
    }

    private void ScheduleRefresh(bool items, bool header, bool settings)
    {
        if (_disposed)
        {
            return;
        }

        _pendingItems |= items;
        _pendingHeader |= header;
        _pendingSettings |= settings;
        RestartTimer(_changeTimer);
    }

    private void OnChangeTimer(object? sender, EventArgs e)
    {
        _changeTimer.Stop();
        if (_disposed || !IsLoaded)
        {
            return;
        }

        if (_pendingItems)
        {
            _pendingItems = false;
            _ = ReloadItemsAsync();
        }

        if (_pendingHeader)
        {
            _pendingHeader = false;
            _ = ReloadHeaderAsync();
        }

        if (_pendingSettings)
        {
            _pendingSettings = false;
            _ = LoadSettingsAsync();
        }
    }

    private void OnMarkViewedTimer(object? sender, EventArgs e)
    {
        _markViewedTimer.Stop();
        if (!_disposed)
        {
            _ = MarkViewedAsync();
        }
    }

    private async Task MarkViewedAsync()
    {
        if (IsStarredView)
        {
            // Not a category: there is nothing unread to clear.
            return;
        }

        try
        {
            var id = CategoryId;
            await Task.Run(() => _s.Library.MarkCategoryViewedAsync(id, _lifetime.Token));
            if (!_disposed && id == CategoryId)
            {
                ClearSubcategoryUnread();
            }
        }
        catch (Exception)
        {
            // Unread badges are cosmetic; never bother the user about them.
        }
    }

    /// <summary>The whole subtree was just marked viewed, so no subcategory has unread files now (no header reload needed).</summary>
    private void ClearSubcategoryUnread()
    {
        for (var i = 0; i < Subcategories.Count; i++)
        {
            if (Subcategories[i].UnreadCount > 0)
            {
                Subcategories[i] = Subcategories[i] with { UnreadCount = 0 };
            }
        }
    }

    private async Task FlashAsync(ThreadItemViewModel item)
    {
        // A file from the fifth on in an album has no cell of its own: the "+N" tile that stands in for it flashes instead.
        var standIn = _albums.Select(album => album.CellShowing(item)).FirstOrDefault(cell => cell is not null);
        if (ReferenceEquals(standIn?.Item, item))
        {
            standIn = null; // it has a cell, which flashes through item.IsHighlighted
        }

        item.IsHighlighted = false;
        item.IsHighlighted = true;
        if (standIn is not null)
        {
            standIn.IsHighlighted = false;
            standIn.IsHighlighted = true;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.2), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }

        item.IsHighlighted = false;
        if (standIn is not null)
        {
            standIn.IsHighlighted = false;
        }
    }

    // ------------------------------------------------------------------ helpers

    private DispatcherTimer CreateTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    private static void RestartTimer(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    internal static BitmapSource? DecodePng(byte[]? png, int sizePx)
    {
        if (png is null || png.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = sizePx;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public interface IThreadViewModelFactory
{
    ThreadViewModel Create();
}
