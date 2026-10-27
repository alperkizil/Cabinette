using System.Windows.Media;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>A chat-list row (also used for category hits in search). Updated in place on refresh.</summary>
public sealed partial class CategoryRowViewModel : ObservableObject
{
    private readonly CategoryIconService _icons;
    private byte[]? _iconBytes;

    public CategoryRowViewModel(CategorySummary summary, CategoryIconService icons, bool isAllInRow = false)
    {
        _icons = icons;
        IsAllInRow = isAllInRow;
        _summary = summary;
        Apply(summary);
    }

    /// <summary><see cref="CategoryId"/> of the "Starred" row, which opens the Starred view rather than a category.</summary>
    public const long StarredRowId = ThreadViewModel.StarredViewId;

    /// <summary>The "All in &lt;parent&gt;" row at the top of a drilled-in level.</summary>
    public bool IsAllInRow { get; }

    /// <summary>
    /// The "Starred" row at the top of the chat list (SPEC.md section 4). It acts like a system row: no drops, no pin, and its
    /// menu only opens it.
    /// </summary>
    public bool IsStarredRow => !IsAllInRow && CategoryId == StarredRowId;

    public long CategoryId => Summary.Category.Id;

    public Category Category => Summary.Category;

    [ObservableProperty]
    private CategorySummary _summary;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string? _iconKey;

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private string _timeText = "";

    [ObservableProperty]
    private string _previewText = "";

    [ObservableProperty]
    private SymbolRegular _previewSymbol;

    [ObservableProperty]
    private bool _hasPreviewSymbol;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private string _unreadText = "";

    [ObservableProperty]
    private bool _hasSubcategories;

    [ObservableProperty]
    private bool _isSystem;

    /// <summary>Drag-and-drop target highlight.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Second line for the drill-in header: "3 subcategories · 120 files".</summary>
    public string LevelSubtitle
    {
        get
        {
            var subs = Summary.SubcategoryCount == 1
                ? L.Get("ChatList_SubcategoryCount_One")
                : L.Format("ChatList_SubcategoryCount", Summary.SubcategoryCount);
            var files = Summary.ItemCount == 1 ? L.Get("ChatList_FileCount_One") : L.Format("ChatList_FileCount", Summary.ItemCount);
            return $"{subs} · {files}";
        }
    }

    public void Update(CategorySummary summary)
    {
        Summary = summary;
        Apply(summary);
        OnPropertyChanged(nameof(LevelSubtitle));
    }

    /// <summary>Only the unread badge changed (a thread was viewed): no icon or text work.</summary>
    public void UpdateUnread(int unreadCount)
    {
        if (unreadCount == UnreadCount)
        {
            return;
        }

        Summary = Summary with { UnreadCount = unreadCount };
        UnreadCount = unreadCount;
        UnreadText = UnreadLabel(unreadCount);
    }

    /// <summary>
    /// Refreshes the unread badges of the rows whose category is in <paramref name="categoryIds"/>, with one small summary
    /// query per category instead of reloading the list (and every row's icon).
    /// </summary>
    public static async Task RefreshUnreadAsync(IEnumerable<CategoryRowViewModel> rows, IReadOnlySet<long> categoryIds, ILibraryService library)
    {
        foreach (var group in rows.Where(r => categoryIds.Contains(r.CategoryId)).GroupBy(r => r.CategoryId).ToList())
        {
            var summary = await library.GetCategorySummaryAsync(group.Key);
            if (summary is null)
            {
                continue;
            }

            foreach (var row in group)
            {
                row.UpdateUnread(summary.UnreadCount);
            }
        }
    }

    /// <summary>
    /// The chat-list row of the Starred view, as a row summary: a system row with the star avatar, the number of starred files
    /// and the time of the latest star.
    /// </summary>
    public static CategorySummary StarredRowSummary(StarredSummary starred) => new()
    {
        Category = new Category
        {
            Id = StarredRowId,
            Name = L.Get("ChatList_Starred"),
            FolderName = "",
            Kind = CategoryKind.System,
            IconKey = StarredIconKey,
            LastUploadAt = starred.LastStarredAt,
        },
        ItemCount = starred.Count,
    };

    /// <summary>Icon key of the Starred row's avatar (a star in the accent color, see Styles/Controls.xaml).</summary>
    public const string StarredIconKey = "starred";

    /// <summary>Re-evaluates the time label (e.g. after midnight).</summary>
    public void RefreshTime() => TimeText = TimeLabel(Summary);

    /// <summary>The last upload's time; none once every file has left ("No files yet"), like a never-used category.</summary>
    private static string TimeLabel(CategorySummary summary)
        => summary.ItemCount == 0 ? "" : Glyphs.ChatTime(summary.Category.LastUploadAt, DateTimeOffset.Now);

    private static string UnreadLabel(int count) => count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    private void Apply(CategorySummary summary)
    {
        var category = summary.Category;
        Name = IsAllInRow ? L.Format("ChatList_AllIn", category.Name) : category.Name;
        IconKey = IsAllInRow ? "allin" : category.IconKey;
        IsPinned = !IsAllInRow && category.IsPinned;
        IsSystem = category.Kind == CategoryKind.System;
        UnreadCount = summary.UnreadCount;
        UnreadText = UnreadLabel(summary.UnreadCount);
        HasSubcategories = !IsAllInRow && summary.SubcategoryCount > 0;
        TimeText = TimeLabel(summary);

        if (IsStarredRow)
        {
            // "3 files": how many are starred; the time is that of the latest star.
            PreviewText = summary.ItemCount == 1 ? L.Get("ChatList_FileCount_One") : L.Format("ChatList_FileCount", summary.ItemCount);
            HasPreviewSymbol = false;
        }
        else if (string.IsNullOrEmpty(summary.LastItemName))
        {
            PreviewText = L.Get("ChatList_NoFilesYet");
            HasPreviewSymbol = false;
        }
        else
        {
            PreviewText = summary.LastItemName;
            PreviewSymbol = Glyphs.ForItem(summary.LastItemKind, summary.LastItemName);
            HasPreviewSymbol = true;
        }

        UpdateIcon(IsAllInRow ? null : category.IconPng);
    }

    private void UpdateIcon(byte[]? png)
    {
        if (png is null || png.Length == 0)
        {
            _iconBytes = null;
            Icon = null;
            return;
        }

        if (_iconBytes is not null && _iconBytes.AsSpan().SequenceEqual(png))
        {
            return;
        }

        _iconBytes = png;
        var cached = _icons.TryGetCached(png);
        if (cached is not null)
        {
            Icon = cached;
            return;
        }

        _ = LoadIconAsync(png);
    }

    private async Task LoadIconAsync(byte[] png)
    {
        var image = await _icons.GetAsync(png);
        if (ReferenceEquals(_iconBytes, png))
        {
            Icon = image;
        }
    }
}
