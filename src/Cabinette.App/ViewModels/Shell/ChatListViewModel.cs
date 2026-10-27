using System.Collections.ObjectModel;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>
/// The chat list: one level of categories at a time. Clicking a category with subcategories drills in:
/// the level shows "All in &lt;parent&gt;" followed by the subcategories.
/// Refreshes merge into the existing rows so selection and scroll position survive live updates.
/// </summary>
public sealed partial class ChatListViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly CategoryIconService _icons;
    private readonly List<long> _path = [];
    private int _refreshVersion;

    public ChatListViewModel(ILibraryService library, CategoryIconService icons)
    {
        _library = library;
        _icons = icons;
    }

    public ObservableCollection<CategoryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private CategoryRowViewModel? _selectedRow;

    /// <summary>Row describing the drilled-in parent (header with back arrow), or null at the top level.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrilledIn))]
    private CategoryRowViewModel? _levelHeader;

    [ObservableProperty]
    private CategorySort _sort = CategorySort.LastUpload;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Drag-and-drop onto the list background (auto-categorize).</summary>
    [ObservableProperty]
    private bool _isBackgroundDropTarget;

    /// <summary>Incremented on every level change so the view can play its slide transition.</summary>
    [ObservableProperty]
    private int _levelChangeCount;

    /// <summary>+1 when drilling in, -1 when going back.</summary>
    public int LastLevelDirection { get; private set; }

    /// <summary>
    /// Raised right before a refresh merges into the rows already shown (a live update: files added from anywhere,
    /// a rename, a re-sort), and <see cref="RowsChanged"/> right after. Not raised when a level is (re)loaded from
    /// empty. The chat list uses them to glide moved rows to their new places.
    /// </summary>
    public event EventHandler? RowsChanging;

    /// <inheritdoc cref="RowsChanging"/>
    public event EventHandler? RowsChanged;

    public bool IsDrilledIn => LevelHeader is not null;

    public long? CurrentParentId => _path.Count > 0 ? _path[^1] : null;

    /// <summary>The open thread, used to keep the matching row selected.</summary>
    public long? OpenCategoryId { get; set; }

    public async Task DrillInAsync(long categoryId)
    {
        _path.Add(categoryId);
        LastLevelDirection = 1;
        await ReloadLevelAsync();
    }

    public async Task GoUpAsync()
    {
        if (_path.Count == 0)
        {
            return;
        }

        var leaving = _path[^1];
        _path.RemoveAt(_path.Count - 1);
        LastLevelDirection = -1;
        await ReloadLevelAsync();
        SelectedRow = Rows.FirstOrDefault(r => r.CategoryId == leaving && !r.IsAllInRow) ?? SelectedRow;
    }

    public async Task GoToTopAsync()
    {
        if (_path.Count == 0)
        {
            return;
        }

        _path.Clear();
        LastLevelDirection = -1;
        await ReloadLevelAsync();
    }

    /// <summary>Shows the level that contains <paramref name="categoryPath"/>'s last entry (ancestors first).</summary>
    public async Task ShowLevelOfAsync(IReadOnlyList<Category> categoryPath)
    {
        var parents = categoryPath.Take(Math.Max(0, categoryPath.Count - 1)).Select(c => c.Id).ToList();
        if (parents.SequenceEqual(_path))
        {
            return;
        }

        _path.Clear();
        _path.AddRange(parents);
        LastLevelDirection = 1;
        await ReloadLevelAsync();
    }

    public void SyncSelection()
    {
        if (OpenCategoryId is not { } open)
        {
            SelectedRow = null;
            return;
        }

        var match = Rows.FirstOrDefault(r => r.CategoryId == open && (r.IsAllInRow || !r.HasSubcategories))
                    ?? Rows.FirstOrDefault(r => r.CategoryId == open);
        SelectedRow = match;
    }

    public void RefreshTimes()
    {
        foreach (var row in Rows)
        {
            row.RefreshTime();
        }
    }

    public void ClearDropTargets()
    {
        IsBackgroundDropTarget = false;
        foreach (var row in Rows.Where(r => r.IsDropTarget))
        {
            row.IsDropTarget = false;
        }
    }

    private async Task ReloadLevelAsync()
    {
        Rows.Clear();
        LevelHeader = null;
        await RefreshAsync();
        LevelChangeCount++;
    }

    /// <summary>Reloads the current level and merges the result into <see cref="Rows"/>.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;

        // If the drilled-in category disappeared, climb up to the nearest one that still exists.
        CategorySummary? header = null;
        while (_path.Count > 0)
        {
            header = await _library.GetCategorySummaryAsync(_path[^1]);
            if (header is not null && header.SubcategoryCount > 0)
            {
                break;
            }

            header = null;
            _path.RemoveAt(_path.Count - 1);
        }

        var parentId = CurrentParentId;
        var summaries = await _library.GetCategoriesAsync(parentId, Sort);

        // The "Starred" row sits at the very top of the top level, above pinned categories, while anything is starred.
        var starred = header is null ? await _library.GetStarredSummaryAsync() : null;
        if (version != _refreshVersion)
        {
            return;
        }

        if (header is not null)
        {
            if (LevelHeader is not null && LevelHeader.CategoryId == header.Category.Id)
            {
                LevelHeader.Update(header);
            }
            else
            {
                LevelHeader = new CategoryRowViewModel(header, _icons);
            }
        }
        else
        {
            LevelHeader = null;
        }

        var desired = new List<(CategorySummary Summary, bool AllIn)>();
        if (header is not null)
        {
            desired.Add((header, true));
        }
        else if (starred is { Count: > 0 })
        {
            desired.Add((CategoryRowViewModel.StarredRowSummary(starred), false));
        }

        desired.AddRange(summaries.Select(s => (s, false)));
        var live = Rows.Count > 0;
        if (live)
        {
            RowsChanging?.Invoke(this, EventArgs.Empty);
        }

        Merge(desired);
        if (live)
        {
            RowsChanged?.Invoke(this, EventArgs.Empty);
        }

        IsEmpty = Rows.Count == 0;
        SyncSelection();

        // A category shown without an icon (new, or reset elsewhere) gets its generated default in the background.
        var withoutIcon = desired
            .Where(d => d.Summary.Category.IconPng is null && d.Summary.Category.Id != CategoryRowViewModel.StarredRowId)
            .Select(d => d.Summary.Category.Id)
            .ToHashSet();
        if (withoutIcon.Count > 0)
        {
            _icons.Forget(withoutIcon);
            _ = Task.Run(() => _icons.EnsureDefaultIconsAsync(withoutIcon));
        }
    }

    /// <summary>
    /// Only unread state changed for these categories (a thread was viewed): refreshes just the matching rows' badges.
    /// No list reload, so no icon data is loaded or decoded again.
    /// </summary>
    public Task RefreshUnreadAsync(IReadOnlySet<long> categoryIds)
    {
        var rows = LevelHeader is null ? Rows.ToList() : [.. Rows, LevelHeader];
        return CategoryRowViewModel.RefreshUnreadAsync(rows, categoryIds, _library);
    }

    private void Merge(List<(CategorySummary Summary, bool AllIn)> desired)
    {
        var existing = Rows.ToDictionary(r => (r.CategoryId, r.IsAllInRow));
        var keep = desired.Select(d => (d.Summary.Category.Id, d.AllIn)).ToHashSet();

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains((Rows[i].CategoryId, Rows[i].IsAllInRow)))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var target = 0; target < desired.Count; target++)
        {
            var (summary, allIn) = desired[target];
            if (existing.TryGetValue((summary.Category.Id, allIn), out var row))
            {
                row.Update(summary);
                var current = Rows.IndexOf(row);
                if (current != target)
                {
                    Rows.Move(current, target);
                }
            }
            else
            {
                Rows.Insert(target, new CategoryRowViewModel(summary, _icons, allIn));
            }
        }
    }
}
