using Cabinette.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>A row of a flattened, virtualized list (chat or grid). Rows with equal keys are reused when the list is refreshed.</summary>
public abstract partial class ThreadRow : ObservableObject
{
    public abstract string Key { get; }

    /// <summary>Copies mutable state from a freshly built row with the same key (keeps containers and scroll position stable).</summary>
    internal virtual void UpdateFrom(ThreadRow fresh)
    {
    }
}

/// <summary>A centered date pill ("Today", "Yesterday", "Monday", "5 November 2026").</summary>
public sealed partial class DateHeaderRow : ThreadRow
{
    public DateHeaderRow(DateTime date, string text)
    {
        Date = date;
        Text = text;
    }

    public DateTime Date { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public override string Key => "d:" + Date.Ticks;

    internal override void UpdateFrom(ThreadRow fresh) => Text = ((DateHeaderRow)fresh).Text;
}

/// <summary>A chat bubble row. Copy-mode items sit on the right, pointers on the left.</summary>
public abstract partial class BubbleRow : ThreadRow
{
    protected BubbleRow(bool isPointer)
    {
        IsPointer = isPointer;
    }

    public bool IsPointer { get; }

    /// <summary>First bubble of a run from the same side: gets extra top spacing.</summary>
    [ObservableProperty]
    public partial bool IsFirstInRun { get; set; }

    internal override void UpdateFrom(ThreadRow fresh) => IsFirstInRun = ((BubbleRow)fresh).IsFirstInRun;
}

public sealed class ItemRow : BubbleRow
{
    public ItemRow(ThreadItemViewModel item)
        : base(item.IsPointer)
    {
        Item = item;
    }

    public ThreadItemViewModel Item { get; }

    public override string Key => (IsPointer ? "p:" : "c:") + Item.Id;
}

/// <summary>2+ images/videos added together, shown as one bubble with a 2×2 grid and a "+N" overlay.</summary>
public sealed partial class AlbumRow : BubbleRow
{
    public const double AlbumWidth = 300;
    public const double Gap = 3;

    public AlbumRow(IReadOnlyList<ThreadItemViewModel> items, bool isPointer)
        : base(isPointer)
    {
        Items = items;
        Cells = BuildCells(items);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText), nameof(TimeText), nameof(Last), nameof(HasOtherStarred))]
    public partial IReadOnlyList<ThreadItemViewModel> Items { get; set; }

    /// <summary>
    /// A file of the album other than the newest is starred: the footer shows the star (the newest file's own footer meta
    /// shows it when that one is), so a starred album has exactly one star.
    /// </summary>
    public bool HasOtherStarred => !Last.IsStarred && Items.Any(i => i.IsStarred);

    [ObservableProperty]
    public partial IReadOnlyList<AlbumCell> Cells { get; set; }

    /// <summary>True when every item of the album is selected.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string Key => (IsPointer ? "ap:" : "ac:") + Items[0].Id;

    /// <summary>The newest item; its time and flags are shown in the album footer.</summary>
    public ThreadItemViewModel Last => Items[^1];

    public string TimeText => Last.TimeText;

    public string CaptionText
    {
        get
        {
            var videos = Items.Count(i => i.VisualKind == ItemVisualKind.Video);
            return videos == 0 ? L.Format("Thread_AlbumPhotos", Items.Count)
                : videos == Items.Count ? L.Format("Thread_AlbumVideos", Items.Count)
                : L.Format("Thread_AlbumItems", Items.Count);
        }
    }

    internal override void UpdateFrom(ThreadRow fresh)
    {
        base.UpdateFrom(fresh);
        var album = (AlbumRow)fresh;
        if (!album.Items.SequenceEqual(Items))
        {
            Items = album.Items;
            Cells = album.Cells;
        }

        // The same files, but one may have been starred or unstarred.
        OnPropertyChanged(nameof(HasOtherStarred));
    }

    /// <summary>
    /// The cell that shows <paramref name="item"/>: its own, or for a file from the fifth on (no cell of its own) the "+N"
    /// tile that stands in for it. Null when the item isn't in this album.
    /// </summary>
    public AlbumCell? CellShowing(ThreadItemViewModel item)
    {
        foreach (var cell in Cells)
        {
            if (ReferenceEquals(cell.Item, item))
            {
                return cell;
            }
        }

        return Cells.Count > 0 && Cells[^1].HasMore && Items.Contains(item) ? Cells[^1] : null;
    }

    private static List<AlbumCell> BuildCells(IReadOnlyList<ThreadItemViewModel> items)
    {
        const double Half = (AlbumWidth - Gap) / 2;
        var cells = new List<AlbumCell>(4);
        switch (items.Count)
        {
            case 2:
                cells.Add(new AlbumCell(items[0], Half, 200, new(0, 0, Gap, 0)));
                cells.Add(new AlbumCell(items[1], Half, 200, default));
                break;
            case 3:
                cells.Add(new AlbumCell(items[0], AlbumWidth, 170, new(0, 0, 0, Gap)));
                cells.Add(new AlbumCell(items[1], Half, Half, new(0, 0, Gap, 0)));
                cells.Add(new AlbumCell(items[2], Half, Half, default));
                break;
            default:
                cells.Add(new AlbumCell(items[0], Half, Half, new(0, 0, Gap, Gap)));
                cells.Add(new AlbumCell(items[1], Half, Half, new(0, 0, 0, Gap)));
                cells.Add(new AlbumCell(items[2], Half, Half, new(0, 0, Gap, 0)));
                cells.Add(new AlbumCell(items[3], Half, Half, default)
                {
                    // The 4th tile stands in for everything that doesn't fit.
                    MoreCount = items.Count > 4 ? items.Count - 3 : 0,
                });
                break;
        }

        return cells;
    }
}

/// <summary>One tile of an album bubble. The last of four stands in for the files that don't fit ("+N").</summary>
public sealed partial class AlbumCell(ThreadItemViewModel item, double width, double height, System.Windows.Thickness margin) : ObservableObject
{
    public ThreadItemViewModel Item { get; } = item;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public System.Windows.Thickness Margin { get; } = margin;

    /// <summary>
    /// Set briefly by <see cref="ThreadViewModel.FocusItem"/> on the "+N" tile when the focused file is one it stands in for
    /// (a file shown in a cell flashes through its own <see cref="ThreadItemViewModel.IsHighlighted"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    public int MoreCount { get; init; }

    public bool HasMore => MoreCount > 0;

    public string MoreText => "+" + MoreCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public double RequestSize => Math.Max(Width, Height);
}

/// <summary>Month header of the grid gallery.</summary>
public sealed class GridHeaderRow(DateTime month, string text) : ThreadRow
{
    public string Text { get; } = text;

    public override string Key => "m:" + month.Ticks;
}

/// <summary>One row of tiles in the grid gallery.</summary>
public sealed class GridTileRow(IReadOnlyList<ThreadItemViewModel> items) : ThreadRow
{
    public IReadOnlyList<ThreadItemViewModel> Items { get; } = items;

    public override string Key => "g:" + Items[0].Id;
}

/// <summary>A subcategory chip under the header.</summary>
public sealed record SubcategoryChip(long Id, string Name, int ItemCount, int UnreadCount, System.Windows.Media.ImageSource? Icon)
{
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    public bool HasUnread => UnreadCount > 0;

    public string CountText => ItemCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
}
