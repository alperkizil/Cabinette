using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>One choice of an activity filter (or a heading between choices, which can't be chosen).</summary>
public sealed partial class AiFilterOption : ObservableObject
{
    /// <param name="value">A <see cref="DateRangeOption"/>, a tool key or a client name.</param>
    public AiFilterOption(string label, object? value, bool isHeader = false)
    {
        Label = label;
        Value = value;
        IsHeader = isHeader;
    }

    public string Label { get; }

    public object? Value { get; }

    public bool IsHeader { get; }

    /// <summary>The filter's current choice (shows a check).</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A drop-down filter of the activity log: its choices, the chosen one, and whether its list is open.</summary>
public sealed partial class AiFilterChoice : ObservableObject
{
    private AiFilterOption _selected;

    public AiFilterChoice(string name, IEnumerable<AiFilterOption> options)
    {
        Name = name;
        Options = new ObservableCollection<AiFilterOption>(options);
        _selected = Options.First(o => !o.IsHeader);
        _selected.IsSelected = true;
    }

    /// <summary>Raised after the user (or code) chose another option.</summary>
    public event EventHandler? Changed;

    /// <summary>What the filter is about ("Date"), for screen readers.</summary>
    public string Name { get; }

    public ObservableCollection<AiFilterOption> Options { get; }

    /// <summary>The chosen option. Headings and options that aren't in the list are ignored.</summary>
    public AiFilterOption Selected
    {
        get => _selected;
        set
        {
            if (value is null || value.IsHeader || !Options.Contains(value))
            {
                // Keep the list's selection in step with the real one.
                OnPropertyChanged();
                return;
            }

            IsOpen = false;
            var previous = _selected;
            if (SetProperty(ref _selected, value))
            {
                previous.IsSelected = false;
                value.IsSelected = true;
                OnPropertyChanged(nameof(IsDefault));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The first option ("Any time", "All tools", "All clients") is chosen.</summary>
    public bool IsDefault => ReferenceEquals(Selected, Options.FirstOrDefault(o => !o.IsHeader));

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Chooses an option from the open list (closing it).</summary>
    [RelayCommand]
    private void Choose(AiFilterOption? option)
    {
        if (option is null)
        {
            IsOpen = false;
            return;
        }

        Selected = option;
    }

    /// <summary>Chooses the option with that value; false when there is none.</summary>
    public bool Select(object? value)
    {
        var option = Options.FirstOrDefault(o => !o.IsHeader && Equals(o.Value, value));
        if (option is null)
        {
            return false;
        }

        Selected = option;
        return true;
    }
}

/// <summary>One entry of the activity log in Settings › AI.</summary>
public sealed partial class AiActivityRowViewModel : ObservableObject
{
    /// <param name="timeZone">The local time zone (the day headings and times).</param>
    /// <param name="today">Today in that zone.</param>
    public AiActivityRowViewModel(ActivityEntry entry, IRelayCommand<AiActivitySegment?> openLink, TimeZoneInfo timeZone, DateTime today)
        : this(entry, openLink, timeZone, today, AiActivityText.Describe)
    {
    }

    /// <param name="describe">Puts the entry in words (tests: one that fails).</param>
    internal AiActivityRowViewModel(ActivityEntry entry, IRelayCommand<AiActivitySegment?> openLink, TimeZoneInfo timeZone, DateTime today, Func<ActivityEntry, AiActivityDescription> describe)
    {
        Entry = entry;
        OpenLinkCommand = openLink;
        DateTimeOffset local;
        try
        {
            local = TimeZoneInfo.ConvertTime(entry.At, timeZone);
        }
        catch (ArgumentException)
        {
            // A time at the very edge of the calendar can't move to the zone: shown as stored.
            local = entry.At;
        }

        Day = local.Date;
        DayHeader = ThreadFormat.DateHeader(local.Date, today);
        Time = local.ToString(L.Get("Thread_TimeFormat"), CultureInfo.CurrentCulture);
        Client = string.IsNullOrWhiteSpace(entry.Source) ? L.Get("AiNotify_UnknownClient") : entry.Source.Trim();
        var description = DescribeSafely(entry, describe);
        Segments = description.Segments;
        Sentence = description.Sentence;
        IsFailed = description.Failed;
        FailureText = description.FailureText;
        Symbol = IsFailed ? SymbolRegular.ErrorCircle20 : SymbolFor(entry.Action);
    }

    public ActivityEntry Entry { get; }

    public long Id => Entry.Id;

    public string Tool => Entry.Action;

    /// <summary>The local day, for the day headings ("Today", "Yesterday", "Monday").</summary>
    public DateTime Day { get; }

    public string DayHeader { get; }

    /// <summary>Local time of day.</summary>
    public string Time { get; }

    public string Client { get; }

    /// <summary>The sentence, with file and category names as links.</summary>
    public IReadOnlyList<AiActivitySegment> Segments { get; }

    /// <summary>The sentence as plain text (screen readers, tests).</summary>
    public string Sentence { get; }

    public bool IsFailed { get; }

    /// <summary>"Failed: …" for a failed call, else null.</summary>
    public string? FailureText { get; }

    public SymbolRegular Symbol { get; }

    /// <summary>Opens a file or category named in the sentence.</summary>
    public IRelayCommand<AiActivitySegment?> OpenLinkCommand { get; }

    /// <summary>The first entry of its day: shows the day heading above it.</summary>
    [ObservableProperty]
    private bool _showDayHeader;

    /// <summary>
    /// The entry in words; one that can't be described shows the tool's name (the row still shows the client and time),
    /// like an entry whose details can't be read, so one bad entry never takes the whole page down.
    /// </summary>
    private static AiActivityDescription DescribeSafely(ActivityEntry entry, Func<ActivityEntry, AiActivityDescription> describe)
    {
        try
        {
            return describe(entry);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Describing AI activity entry {entry.Id} ({entry.Action}) failed.", ex);
            return new AiActivityDescription([new AiActivitySegment(AiActivityText.ToolName(entry.Action ?? ""))], Failed: false, Error: null);
        }
    }

    public static SymbolRegular SymbolFor(string tool) => tool switch
    {
        AiTools.Search => SymbolRegular.Search20,
        AiTools.ListCategories => SymbolRegular.TextBulletListTree20,
        AiTools.ListFiles => SymbolRegular.DocumentBulletList20,
        AiTools.GetFiles => SymbolRegular.Info20,
        AiTools.ReadFile => SymbolRegular.DocumentText20,
        AiTools.ListTags or AiTools.TagFiles or AiTools.UntagFiles => SymbolRegular.Tag20,
        AiTools.GetOperationStatus => SymbolRegular.ArrowSync20,
        AiTools.AddFiles => SymbolRegular.DocumentAdd20,
        AiTools.CreateNote => SymbolRegular.NoteAdd20,
        AiTools.AddToCategory or AiTools.CreateCategory => SymbolRegular.FolderAdd20,
        AiTools.RemoveFromCategory => SymbolRegular.FolderProhibited20,
        AiTools.MoveFiles or AiTools.MoveVault => SymbolRegular.FolderArrowRight20,
        AiTools.RenameFile or AiTools.RenameCategory => SymbolRegular.Rename20,
        AiTools.DeleteFiles or AiTools.DeleteCategory => SymbolRegular.Delete20,
        AiTools.StarFiles => SymbolRegular.Star20,
        AiTools.UnstarFiles => SymbolRegular.StarOff20,
        AiTools.MigratePointers => SymbolRegular.ArrowDownload20,
        AiTools.CreateBackup => SymbolRegular.Archive20,
        AiTools.ShowInCabinette => SymbolRegular.Open20,
        _ => SymbolRegular.Bot20,
    };
}

/// <summary>
/// The activity log in Settings › AI (SPEC.md section 10): newest first, in pages, filtered by date (the main filter's
/// presets), tool ("Changes only" or one tool) and client. New entries appear at the top as they are logged. Clicking a
/// file or category opens it and closes Settings. Every library call runs off the UI thread; use on the UI thread.
/// </summary>
public sealed partial class AiActivityViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 100;

    /// <summary>How many recent entries are read to list the clients seen in the client filter.</summary>
    public const int ClientScanLimit = 1000;

    /// <summary>The tool filter's values besides tool names.</summary>
    public const string AllTools = "";
    public const string ChangesOnly = "*changes";

    /// <summary>The client filter's "All clients".</summary>
    public const string AllClients = "";

    private readonly IAiActivityLog _log;
    private readonly ILibraryService _library;
    private readonly IShellNavigator _navigator;
    private readonly Action _closeDialog;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly HashSet<long> _ids = [];
    private readonly List<ActivityEntry> _arrivedWhileLoading = [];
    private ActivityQuery? _shownQuery;
    private int _loadVersion;
    private bool _subscribed;
    private bool _clientsScanned;
    private bool _disposed;

    public AiActivityViewModel(IAiActivityLog log, ILibraryService library, IShellNavigator navigator, Action closeDialog)
        : this(log, library, navigator, closeDialog, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher, TimeProvider.System)
    {
    }

    internal AiActivityViewModel(IAiActivityLog log, ILibraryService library, IShellNavigator navigator, Action closeDialog, Dispatcher dispatcher, TimeProvider time)
    {
        _log = log;
        _library = library;
        _navigator = navigator;
        _closeDialog = closeDialog;
        _dispatcher = dispatcher;
        _time = time;

        DateFilter = new AiFilterChoice(L.Get("AiFilter_DateLabel"),
        [
            new(L.Get("Filter_Date_Any"), DateRangeOption.Any),
            new(L.Get("Filter_Date_Today"), DateRangeOption.Today),
            new(L.Get("Filter_Date_Week"), DateRangeOption.Week),
            new(L.Get("Filter_Date_Month"), DateRangeOption.Month),
            new(L.Get("Filter_Date_Year"), DateRangeOption.Year),
        ]);
        ToolFilter = new AiFilterChoice(L.Get("AiFilter_ToolLabel"), ToolOptions());
        ClientFilter = new AiFilterChoice(L.Get("AiFilter_ClientLabel"), [new(L.Get("AiFilter_AllClients"), AllClients)]);
        DateFilter.Changed += OnFilterChanged;
        ToolFilter.Changed += OnFilterChanged;
        ClientFilter.Changed += OnFilterChanged;
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<AiActivityRowViewModel> Entries { get; } = [];

    public AiFilterChoice DateFilter { get; }

    public AiFilterChoice ToolFilter { get; }

    public AiFilterChoice ClientFilter { get; }

    /// <summary>The first page is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    /// <summary>A page was shown at least once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    /// <summary>Older entries match too ("Show older").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowOlderCommand))]
    private bool _hasMore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowOlderCommand))]
    private bool _isLoadingOlder;

    /// <summary>A note above the list: an item that no longer exists, or a log that couldn't be read.</summary>
    [ObservableProperty]
    private string? _message;

    /// <summary>Nothing to show (after loading).</summary>
    public bool IsEmpty => IsLoaded && !IsLoading && Entries.Count == 0;

    /// <summary>A filter other than "everything" is chosen.</summary>
    public bool IsFiltered => !DateFilter.IsDefault || !ToolFilter.IsDefault || !ClientFilter.IsDefault;

    public string EmptyTitle => L.Get(IsFiltered ? "AiActivity_EmptyFilteredTitle" : "AiActivity_EmptyTitle");

    public string EmptyHint => L.Get(IsFiltered ? "AiActivity_EmptyFilteredHint" : "AiActivity_EmptyHint");

    /// <summary>The query for the current filters (the first page, or the page before <paramref name="beforeId"/>).</summary>
    /// <param name="now">Local now: the date presets count from it, like the main filter's.</param>
    public ActivityQuery BuildQuery(DateTimeOffset now, long? beforeId = null)
    {
        var tool = ToolFilter.Selected.Value as string ?? AllTools;
        var client = ClientFilter.Selected.Value as string ?? AllClients;
        return new ActivityQuery
        {
            From = DateFrom(DateFilter.Selected.Value is DateRangeOption range ? range : DateRangeOption.Any, now),
            Actions = tool switch
            {
                AllTools => null,
                ChangesOnly => AiTools.Changes,
                _ => [tool],
            },
            Source = client == AllClients ? null : client,
            BeforeId = beforeId,
            Limit = PageSize,
        };
    }

    /// <summary>Where a date preset starts: the same presets as the main search filter (SearchFilterViewModel).</summary>
    public static DateTimeOffset? DateFrom(DateRangeOption range, DateTimeOffset now) => range switch
    {
        DateRangeOption.Today => new DateTimeOffset(now.Date, now.Offset),
        DateRangeOption.Week => now.AddDays(-7),
        DateRangeOption.Month => now.AddDays(-30),
        DateRangeOption.Year => now.AddYears(-1),
        _ => null,
    };

    /// <summary>Whether an entry belongs to the query's results (ignoring paging); the activity log's own rules.</summary>
    public static bool Matches(ActivityEntry entry, ActivityQuery query)
        => (query.From is not { } from || entry.At >= from) &&
           (query.To is not { } to || entry.At < to) &&
           (query.Actions is not { Count: > 0 } actions || actions.Contains(entry.Action, StringComparer.Ordinal)) &&
           (query.Source is not { } source || string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the first page for the current filters (and, the first time, the clients seen). Starts the live updates.</summary>
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        Subscribe();
        var version = ++_loadVersion;
        _arrivedWhileLoading.Clear();
        IsLoading = true;
        Message = null;
        var query = BuildQuery(_time.GetLocalNow());
        try
        {
            var pageTask = Task.Run(() => _log.GetAsync(query));
            if (!_clientsScanned)
            {
                _clientsScanned = true;
                await ScanClientsAsync();
            }

            var page = await pageTask;
            if (version != _loadVersion || _disposed)
            {
                return;
            }

            Entries.Clear();
            _ids.Clear();
            _shownQuery = query;
            HasMore = page.HasMore;
            Append(page.Entries);

            // Entries logged while the page was read: those it doesn't have yet go on top.
            foreach (var entry in _arrivedWhileLoading.OrderBy(e => e.Id))
            {
                Insert(entry);
            }
        }
        catch (Exception ex)
        {
            if (version != _loadVersion || _disposed)
            {
                return;
            }

            AppLog.Warn("Reading the AI activity log failed.", ex);
            Entries.Clear();
            _ids.Clear();
            _shownQuery = null;
            HasMore = false;
            Message = L.Format("AiActivity_LoadFailed", Reason(ex));
        }
        finally
        {
            if (version == _loadVersion && !_disposed)
            {
                _arrivedWhileLoading.Clear();
                IsLoading = false;
                IsLoaded = true;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadVersion++;
        if (_subscribed)
        {
            _log.EntryAdded -= OnEntryAdded;
        }

        DateFilter.Changed -= OnFilterChanged;
        ToolFilter.Changed -= OnFilterChanged;
        ClientFilter.Changed -= OnFilterChanged;
    }

    private bool CanShowOlder => HasMore && !IsLoadingOlder;

    [RelayCommand(CanExecute = nameof(CanShowOlder))]
    private async Task ShowOlderAsync()
    {
        if (_shownQuery is null || Entries.Count == 0)
        {
            return;
        }

        var version = _loadVersion;
        var query = _shownQuery with { BeforeId = Entries[^1].Id };
        IsLoadingOlder = true;
        try
        {
            var page = await Task.Run(() => _log.GetAsync(query));
            if (version != _loadVersion || _disposed)
            {
                return;
            }

            HasMore = page.HasMore;
            Append(page.Entries);
        }
        catch (Exception ex)
        {
            if (version == _loadVersion && !_disposed)
            {
                AppLog.Warn("Reading older AI activity failed.", ex);
                Message = L.Format("AiActivity_LoadFailed", Reason(ex));
            }
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    /// <summary>Opens the file (in its thread, scrolled to it) or the category, and closes Settings; says so when it is gone.</summary>
    [RelayCommand]
    private async Task OpenLinkAsync(AiActivitySegment? segment)
    {
        if (segment is not { IsLink: true } || _disposed)
        {
            return;
        }

        Message = null;
        try
        {
            if (segment.Link == AiActivityLinkKind.File)
            {
                var item = await Task.Run(() => _library.GetItemAsync(segment.Id));
                if (item is null)
                {
                    Message = L.Format("AiActivity_FileGone", segment.Text);
                    return;
                }

                _navigator.OpenCategory(item.HomeCategoryId, item.Id);
            }
            else
            {
                var category = await Task.Run(() => _library.GetCategoryAsync(segment.Id));
                if (category is null)
                {
                    Message = L.Format("AiActivity_CategoryGone", segment.Text);
                    return;
                }

                _navigator.OpenCategory(category.Id);
            }

            _closeDialog();
        }
        catch (Exception ex)
        {
            // Only a missing item is "gone"; a library that can't answer now (closed during a vault move, busy, a database
            // error) says so, with its reason.
            AppLog.Warn($"Opening '{segment.Text}' from the AI activity log failed.", ex);
            Message = L.Format(segment.Link == AiActivityLinkKind.File ? "AiActivity_FileOpenFailed" : "AiActivity_CategoryOpenFailed", segment.Text, Reason(ex));
        }
    }

    /// <summary>A failure's reason for the note above the list: a library error's own message, else the error's kind.</summary>
    private static string Reason(Exception ex) => ex is LibraryException ? ex.Message : ex.GetType().Name;

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        _ = LoadAsync();
    }

    private void Subscribe()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _log.EntryAdded += OnEntryAdded;
        }
    }

    /// <summary>Raised on a background thread after an entry was stored.</summary>
    private void OnEntryAdded(object? sender, ActivityEntry entry)
    {
        if (_disposed || entry is null)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => AcceptLive(entry));
    }

    private void AcceptLive(ActivityEntry entry)
    {
        if (_disposed)
        {
            return;
        }

        AddClient(entry.Source);
        if (IsLoading)
        {
            _arrivedWhileLoading.Add(entry);
        }
        else if (IsLoaded)
        {
            Insert(entry);
        }
    }

    /// <summary>A newly logged entry, at its place among the newest (normally the top), when it matches the filters.</summary>
    private void Insert(ActivityEntry entry)
    {
        if (_shownQuery is null || _ids.Contains(entry.Id) || !Matches(entry, _shownQuery))
        {
            return;
        }

        // Older than everything shown while older pages remain: "Show older" brings it.
        if (HasMore && Entries.Count > 0 && entry.Id < Entries[^1].Id)
        {
            return;
        }

        var index = 0;
        while (index < Entries.Count && Entries[index].Id > entry.Id)
        {
            index++;
        }

        Entries.Insert(index, CreateRow(entry));
        _ids.Add(entry.Id);
        RefreshDayHeaders(index, index + 1);
    }

    private void Append(IReadOnlyList<ActivityEntry> entries)
    {
        var start = Entries.Count;
        foreach (var entry in entries.Where(e => _ids.Add(e.Id)))
        {
            Entries.Add(CreateRow(entry));
            AddClient(entry.Source);
        }

        RefreshDayHeaders(start, Entries.Count - 1);
    }

    private AiActivityRowViewModel CreateRow(ActivityEntry entry) => new(entry, OpenLinkCommand, _time.LocalTimeZone, _time.GetLocalNow().Date);

    private void RefreshDayHeaders(int from, int to)
    {
        for (var i = Math.Max(0, from); i <= Math.Min(to, Entries.Count - 1); i++)
        {
            Entries[i].ShowDayHeader = i == 0 || Entries[i - 1].Day != Entries[i].Day;
        }
    }

    private async Task ScanClientsAsync()
    {
        try
        {
            var recent = await Task.Run(() => _log.GetAsync(new ActivityQuery { Limit = ClientScanLimit }));
            foreach (var entry in recent.Entries)
            {
                AddClient(entry.Source);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Listing the AI clients of the activity log failed.", ex);
        }
    }

    /// <summary>Adds a client to the client filter (sorted after "All clients"), keeping the choice.</summary>
    private void AddClient(string? client)
    {
        if (string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        var name = client.Trim();
        var options = ClientFilter.Options;
        if (options.Any(o => !o.IsHeader && o.Value is string known && known != AllClients && string.Equals(known, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var index = 1;
        while (index < options.Count && string.Compare(options[index].Label, name, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            index++;
        }

        options.Insert(index, new AiFilterOption(name, name));
    }

    private static IEnumerable<AiFilterOption> ToolOptions()
    {
        yield return new AiFilterOption(L.Get("AiFilter_AllTools"), AllTools);
        yield return new AiFilterOption(L.Get("AiFilter_ChangesOnly"), ChangesOnly);
        yield return new AiFilterOption(L.Get("AiFilter_ChangesHeader"), null, isHeader: true);
        foreach (var tool in AiTools.Changes)
        {
            yield return new AiFilterOption(AiActivityText.ToolName(tool), tool);
        }

        yield return new AiFilterOption(L.Get("AiFilter_ReadingHeader"), null, isHeader: true);
        foreach (var tool in AiTools.Reading)
        {
            yield return new AiFilterOption(AiActivityText.ToolName(tool), tool);
        }

        yield return new AiFilterOption(L.Get("AiFilter_OtherHeader"), null, isHeader: true);
        foreach (var tool in AiTools.All.Except(AiTools.Changes).Except(AiTools.Reading))
        {
            yield return new AiFilterOption(AiActivityText.ToolName(tool), tool);
        }
    }
}
