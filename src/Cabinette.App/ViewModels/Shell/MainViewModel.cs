using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>
/// The main window: chat list (left), global search, the open thread (right), live updates from the library,
/// and the category actions of the context menu (see MainViewModel.Categories.cs).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable, IQuickSwitcherActions
{
    private readonly ILibraryService _library;
    private readonly IThreadViewModelFactory _threadFactory;
    private readonly ShellNavigator _navigator;
    private readonly IFileIntake _intake;
    private readonly CategoryIconService _icons;
    private readonly CategoryDirectory _directory;
    private readonly IShellOperations _shell;
    private readonly Func<SettingsViewModel> _settingsFactory;
    private readonly RecentCategories _recent;
    private readonly ItemOpener _opener;
    private readonly DispatcherTimer _changeDebounce;
    private readonly DispatcherTimer _clock;
    private readonly Lock _pendingGate = new();
    private readonly HashSet<long> _pendingViewedIds = [];
    private readonly HashSet<long> _pendingChangedCategoryIds = [];
    private LibraryChangeKind _pendingChangeKinds;
    private int _openVersion;
    private bool _initialized;
    private bool _disposed;
    private bool _quickSwitcherStarting;

    public MainViewModel(
        ILibraryService library,
        IThreadViewModelFactory threadFactory,
        ShellNavigator navigator,
        IFileIntake intake,
        DialogService dialogs,
        NotificationService notifications,
        CategoryIconService icons,
        CategoryDirectory directory,
        IShellOperations shell,
        IThumbnailService thumbnails,
        Func<SettingsViewModel> settingsFactory,
        RecentCategories recent,
        ItemOpener opener)
    {
        _library = library;
        _threadFactory = threadFactory;
        _navigator = navigator;
        _intake = intake;
        _icons = icons;
        _directory = directory;
        _shell = shell;
        _settingsFactory = settingsFactory;
        _recent = recent;
        _opener = opener;
        Dialogs = dialogs;
        Dialogs.OpenDialogs.CollectionChanged += OnDialogsChanged;
        Notifications = notifications;
        ChatList = new ChatListViewModel(library, icons);
        Search = new SearchViewModel(library, icons, thumbnails, directory);
        Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchViewModel.IsActive) && !Search.IsActive)
            {
                ChatList.SyncSelection();
            }
        };

        _navigator.SetHandler(OpenCategoryAsync);

        _changeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _changeDebounce.Tick += async (_, _) =>
        {
            _changeDebounce.Stop();
            await ApplyPendingChangesAsync();
        };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => ChatList.RefreshTimes();
    }

    public ChatListViewModel ChatList { get; }

    public SearchViewModel Search { get; }

    public DialogService Dialogs { get; }

    public NotificationService Notifications { get; }

    /// <summary>The thread shown on the right; null shows the empty state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThread))]
    private ThreadViewModel? _currentThread;

    public bool HasThread => CurrentThread is not null;

    [ObservableProperty]
    private bool _isFilterOpen;

    /// <summary>Drop highlight on the empty state.</summary>
    [ObservableProperty]
    private bool _isEmptyStateDropTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortTooltip))]
    private CategorySort _sort = CategorySort.LastUpload;

    public string SortTooltip => L.Get(Sort == CategorySort.LastUpload ? "ChatList_SortByLastUpload" : "ChatList_SortAlphabetical");

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var settings = await _library.GetSettingsAsync();
            Sort = settings.ChatListSort;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings could not be read; using defaults.", ex);
        }

        ChatList.Sort = Sort;
        _library.Changed += OnLibraryChanged;
        await RunSafeAsync(ChatList.RefreshAsync);
        _ = _directory.EnsureLoadedAsync();
        _clock.Start();
        _ = Task.Run(() => _icons.EnsureDefaultIconsAsync());
    }

    // ------------------------------------------------------------------ threads

    /// <summary>Opens a category's thread on the right (also the <see cref="IShellNavigator"/> handler).</summary>
    public Task OpenCategoryAsync(long categoryId, long? focusItemId)
    {
        if (CurrentThread is { } current && _navigator.CurrentCategoryId == categoryId)
        {
            if (focusItemId is { } id)
            {
                current.FocusItem(id);
            }

            return Task.CompletedTask;
        }

        return ShowThreadAsync(thread => thread.LoadAsync(categoryId), categoryId, focusItemId);
    }

    /// <summary>
    /// Opens the Starred view on the right (SPEC.md section 4): every starred file, read-only like "Deleted". The navigator
    /// then reports no open category; the chat list keeps its Starred row selected.
    /// </summary>
    public Task OpenStarredAsync(long? focusItemId = null)
    {
        if (CurrentThread is { IsStarredView: true } current)
        {
            if (focusItemId is { } id)
            {
                current.FocusItem(id);
            }

            return Task.CompletedTask;
        }

        return ShowThreadAsync(thread => thread.LoadStarredAsync(), null, focusItemId);
    }

    /// <summary>Loads a new thread view model with <paramref name="load"/> and shows it instead of the current one.</summary>
    /// <param name="categoryId">The category it shows, or null for the Starred view.</param>
    private async Task ShowThreadAsync(Func<ThreadViewModel, Task> load, long? categoryId, long? focusItemId)
    {
        var version = ++_openVersion;
        ThreadViewModel? thread = null;
        try
        {
            thread = _threadFactory.Create();
            await load(thread);
        }
        catch (Exception ex)
        {
            thread?.Dispose();
            ReportError("Opening a thread failed.", ex);
            return;
        }

        if (version != _openVersion || _disposed)
        {
            thread.Dispose();
            return;
        }

        var previous = CurrentThread;
        CurrentThread = thread;
        _navigator.CurrentCategoryId = categoryId;
        ChatList.OpenCategoryId = categoryId ?? CategoryRowViewModel.StarredRowId;
        ChatList.SyncSelection();
        previous?.Dispose();
        if (categoryId is { } opened)
        {
            _recent.Record(opened); // Ctrl+K's recent list holds categories, not the Starred view
        }

        if (focusItemId is { } focus)
        {
            // Let the thread view bind first.
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (ReferenceEquals(CurrentThread, thread))
            {
                thread.FocusItem(focus);
            }
        }
    }

    public void CloseThread()
    {
        _openVersion++;
        var previous = CurrentThread;
        CurrentThread = null;
        _navigator.CurrentCategoryId = null;
        ChatList.OpenCategoryId = null;
        ChatList.SyncSelection();
        previous?.Dispose();
    }

    /// <summary>Click / Enter on a chat-list row: drill into parents, open the others.</summary>
    public async Task ActivateRowAsync(CategoryRowViewModel row)
    {
        if (row.IsStarredRow)
        {
            await OpenStarredAsync();
            return;
        }

        if (!row.IsAllInRow && row.HasSubcategories)
        {
            await RunSafeAsync(() => ChatList.DrillInAsync(row.CategoryId));
            return;
        }

        await OpenCategoryAsync(row.CategoryId, null);
    }

    /// <summary>Click / Enter on a search result.</summary>
    public async Task ActivateSearchResultAsync(object? result)
    {
        switch (result)
        {
            case CategoryRowViewModel row:
                await OpenCategoryAsync(row.CategoryId, null);
                break;
            case FileResultViewModel file:
                await OpenCategoryAsync(file.Item.HomeCategoryId, file.Item.Id);
                break;
            case TagSuggestionViewModel tag:
                Search.ApplyTagCommand.Execute(tag);
                break;
        }
    }

    // ------------------------------------------------------------------ quick switcher (Ctrl+K)

    /// <summary>
    /// Ctrl+K: the quick switcher (SPEC section 7). Does nothing while a dialog is open. The choice is applied after the
    /// switcher has closed, so focus is back where it was and a "Locate…" prompt doesn't stack on top of it.
    /// </summary>
    [RelayCommand]
    private Task OpenQuickSwitcherAsync() => RunSafeAsync(async () =>
    {
        if (_quickSwitcherStarting || Dialogs.IsAnyOpen)
        {
            return;
        }

        _quickSwitcherStarting = true;
        QuickSwitcherViewModel switcher;
        try
        {
            CloseFilterPopups();
            switcher = new QuickSwitcherViewModel(
                (text, ct) => QuickSwitcherViewModel.SearchLibraryAsync(_library, text, ct),
                _directory,
                _icons,
                _recent.Snapshot(),
                _navigator.CurrentCategoryId);
            await switcher.LoadAsync();
            if (Dialogs.IsAnyOpen || _disposed)
            {
                return;
            }
        }
        finally
        {
            _quickSwitcherStarting = false;
        }

        if (await Dialogs.ShowAsync(switcher) is QuickSwitcherChoice choice)
        {
            await choice.ApplyAsync(this);
        }
    });

    /// <summary>Shift+Enter on a file in the quick switcher.</summary>
    public Task OpenFileAsync(LibraryItem item) => _opener.OpenAsync(item);

    // ------------------------------------------------------------------ header commands

    [RelayCommand]
    private Task NewCategoryAsync() => RunSafeAsync(async () =>
    {
        var created = await Dialogs.ShowNewCategoryAsync(ChatList.CurrentParentId);
        if (created is not null)
        {
            Notifications.Show(L.Format("Category_Created", created.Name), NotificationKind.Success);
            await OpenCategoryAsync(created.Id, null);
        }
    });

    [RelayCommand]
    private Task AddFilesAsync() => _intake.PickAndAddFilesAsync(null);

    [RelayCommand]
    private Task PasteAsync() => _intake.PasteAsync(null);

    [RelayCommand]
    private Task ToggleSortAsync() => RunSafeAsync(async () =>
    {
        Sort = Sort == CategorySort.LastUpload ? CategorySort.Alphabetical : CategorySort.LastUpload;
        ChatList.Sort = Sort;
        await ChatList.RefreshAsync();
        var settings = await _library.GetSettingsAsync();
        if (settings.ChatListSort != Sort)
        {
            await _library.SaveSettingsAsync(settings with { ChatListSort = Sort });
        }
    });

    [RelayCommand]
    private Task OpenSettingsAsync() => OpenSettingsAsync(null);

    public async Task OpenSettingsAsync(string? section)
    {
        var settings = _settingsFactory();
        await settings.LoadAsync();
        if (section is not null)
        {
            settings.SelectSection(section);
        }

        settings.Saved += (_, saved) => _ = ApplySortAsync(saved.ChatListSort);
        await Dialogs.ShowAsync(settings);
    }

    /// <summary>Raised after <see cref="GoHomeCommand"/> so the view can scroll the chat list back to the top.</summary>
    public event EventHandler? HomeRequested;

    /// <summary>
    /// Home button / Ctrl+Home: back to how the app looks when it starts — no open thread (so no thread search or
    /// filters either), top level of the chat list, empty search box and every search filter reset.
    /// </summary>
    [RelayCommand]
    private Task GoHomeAsync() => RunSafeAsync(async () =>
    {
        IsFilterOpen = false;
        Search.Query = "";
        Search.Filter.ResetCommand.Execute(null);
        CloseThread();
        await ChatList.GoToTopAsync();
        HomeRequested?.Invoke(this, EventArgs.Empty);
    });

    [RelayCommand]
    private Task GoUpAsync() => RunSafeAsync(ChatList.GoUpAsync);

    [RelayCommand]
    private void ToggleFilter() => IsFilterOpen = !IsFilterOpen;

    [RelayCommand]
    private void CloseFilter() => IsFilterOpen = false;

    /// <summary>
    /// Closes the filter flyout and the open thread's filter popup. A popup only closes itself on a click elsewhere, so a
    /// dialog opened from the keyboard (Ctrl+K, Ctrl+N, Delete, F2) would otherwise leave it floating over the dialog.
    /// </summary>
    private void CloseFilterPopups()
    {
        IsFilterOpen = false;
        if (CurrentThread is { } thread)
        {
            thread.IsFilterOpen = false;
        }
    }

    /// <summary>Every dialog closes the filter popups first (see <see cref="CloseFilterPopups"/>).</summary>
    private void OnDialogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            CloseFilterPopups();
        }
    }

    /// <summary>Esc: close the filter flyout, clear the search, or go up one level. Returns true if handled.</summary>
    public bool HandleEscape()
    {
        if (IsFilterOpen)
        {
            IsFilterOpen = false;
            return true;
        }

        if (Search.HasQuery)
        {
            Search.Query = "";
            return true;
        }

        if (Search.IsActive)
        {
            Search.Filter.ResetCommand.Execute(null);
            return true;
        }

        if (ChatList.IsDrilledIn)
        {
            _ = RunSafeAsync(ChatList.GoUpAsync);
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ drag & drop

    public Task DropAsync(IReadOnlyList<string> paths, long? targetCategoryId)
        => _intake.AddPathsAsync(paths, targetCategoryId);

    /// <summary>
    /// Bubbles dragged out of a thread onto a chat-list row: those items join that category, like "Add to category…"
    /// (not re-added as files, so "sort by type" doesn't apply: the row is where you put them).
    /// </summary>
    public Task AddItemsToCategoryAsync(IReadOnlyList<long> itemIds, CategoryRowViewModel row)
        => RunSafeAsync(async () =>
        {
            await Task.Run(() => _library.AddToCategoryAsync(itemIds, row.CategoryId, AddedBy.AppUi));
            var name = row.Category.Name;
            Notifications.Show(
                itemIds.Count == 1 ? L.Format("ChatList_BubblesAddedOne", name) : L.Format("ChatList_BubblesAddedMany", itemIds.Count, name),
                NotificationKind.Success);
        });

    // ------------------------------------------------------------------ live updates

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        lock (_pendingGate)
        {
            _pendingChangeKinds |= e.Kind;
            if ((e.Kind & LibraryChangeKind.Viewed) != 0)
            {
                _pendingViewedIds.UnionWith(e.CategoryIds);
            }

            if ((e.Kind & LibraryChangeKind.Categories) != 0)
            {
                _pendingChangedCategoryIds.UnionWith(e.CategoryIds);
            }
        }

        if (e.MergedDuplicates.Count > 0)
        {
            _ = ReportMergedDuplicatesAsync(e.MergedDuplicates);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _changeDebounce.Stop();
            _changeDebounce.Start();
        });
    }

    private async Task ApplyPendingChangesAsync()
    {
        LibraryChangeKind kinds;
        HashSet<long> viewed;
        HashSet<long> changedCategories;
        lock (_pendingGate)
        {
            kinds = _pendingChangeKinds;
            viewed = [.. _pendingViewedIds];
            changedCategories = [.. _pendingChangedCategoryIds];
            _pendingChangeKinds = LibraryChangeKind.None;
            _pendingViewedIds.Clear();
            _pendingChangedCategoryIds.Clear();
        }

        if (_disposed || kinds == LibraryChangeKind.None)
        {
            return;
        }

        if (kinds == LibraryChangeKind.Viewed)
        {
            // Only unread state changed (a thread was opened): refresh those badges, nothing else (no list reload,
            // no icons, no category directory).
            await RunSafeAsync(() => ChatList.RefreshUnreadAsync(viewed));
            await RunSafeAsync(() => Search.RefreshUnreadAsync(viewed));
            return;
        }

        if (kinds.HasFlag(LibraryChangeKind.Categories))
        {
            _directory.Invalidate();
            _ = Task.Run(() => _icons.EnsureDefaultIconsAsync(changedCategories));
        }

        if (kinds.HasFlag(LibraryChangeKind.Tags))
        {
            Search.InvalidateTags();
        }

        if (kinds.HasFlag(LibraryChangeKind.Settings))
        {
            try
            {
                var settings = await _library.GetSettingsAsync();
                if (settings.ChatListSort != Sort)
                {
                    Sort = settings.ChatListSort;
                    ChatList.Sort = Sort;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Reloading settings failed.", ex);
            }
        }

        await RunSafeAsync(ChatList.RefreshAsync);
        Search.Refresh();

        // The open category may have been deleted.
        if (kinds.HasFlag(LibraryChangeKind.Categories) && _navigator.CurrentCategoryId is { } open)
        {
            try
            {
                if (await _library.GetCategoryAsync(open) is null)
                {
                    CloseThread();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Checking the open category failed.", ex);
            }
        }
    }

    private async Task ReportMergedDuplicatesAsync(IReadOnlyList<DuplicateInfo> duplicates)
    {
        await _directory.EnsureLoadedAsync();
        foreach (var duplicate in duplicates.Take(3))
        {
            var existing = duplicate.Existing;
            Notifications.Show(
                L.Format("Intake_Duplicate", existing.FileName, _directory.GetName(existing.HomeCategoryId)),
                NotificationKind.Info,
                L.Get("Intake_Show"),
                () => _navigator.OpenCategory(existing.HomeCategoryId, existing.Id));
        }
    }

    private async Task ApplySortAsync(CategorySort sort)
    {
        if (sort == Sort)
        {
            return;
        }

        Sort = sort;
        ChatList.Sort = sort;
        await RunSafeAsync(ChatList.RefreshAsync);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs an action; library errors become notices, anything else is logged and reported politely.</summary>
    public async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError("Shell action failed.", ex);
        }
    }

    private void ReportError(string context, Exception ex)
    {
        if (ex is LibraryException libraryException)
        {
            Notifications.Show(libraryException.Message, NotificationKind.Error);
            return;
        }

        AppLog.Error(context, ex);
        Notifications.Show(L.Get("Notify_Error"), NotificationKind.Error, L.Get("Notify_OpenLog"), FileIntake.OpenLogFolder);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _library.Changed -= OnLibraryChanged;
        Dialogs.OpenDialogs.CollectionChanged -= OnDialogsChanged;
        _changeDebounce.Stop();
        _clock.Stop();
        CurrentThread?.Dispose();
        CurrentThread = null;
    }
}
