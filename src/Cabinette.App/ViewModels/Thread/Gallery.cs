using System.Globalization;
using System.Windows;
using Cabinette.App.Localization;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cabinette.App.ViewModels.Thread;

// The Gallery view (SPEC.md section 4, "Gallery"): a Longhorn-style photo gallery, offered only in the Images category, its
// subcategories and their "All in" rows. Same-size cells under day headers, so the layout follows from the number of files.

/// <summary>What the Gallery's sort header sorts by.</summary>
public enum GallerySortKey
{
    Name = 0,
    Size,
    Type,
    Date,
}

/// <summary>Which date the Gallery's day sections (and its Date sort) use.</summary>
public enum GalleryDateKind
{
    /// <summary>When the file was added to Cabinette.</summary>
    Added = 0,

    /// <summary>When the photo or video was taken; files without one use their last-modified date (see <see cref="GalleryBuilder.DateTaken"/>).</summary>
    Taken,
}

/// <summary>The Gallery's three cell sizes (about 96, 128 and 176 px), changed with Ctrl+mouse wheel and remembered across restarts.</summary>
public enum GalleryCellSize
{
    Small = 0,
    Medium,
    Large,
}

/// <summary>The Gallery's sort: the key, its direction and the date used for the sections.</summary>
public readonly record struct GallerySort(GallerySortKey Key, bool Ascending, GalleryDateKind DateKind)
{
    /// <summary>Date added, newest first.</summary>
    public static GallerySort Default => new(GallerySortKey.Date, false, GalleryDateKind.Added);

    /// <summary>The direction a key starts in when it is chosen: A–Z for names and types, largest and newest first otherwise.</summary>
    public static bool StartsAscending(GallerySortKey key) => key is GallerySortKey.Name or GallerySortKey.Type;

    /// <summary>
    /// Sections run newest first, except when sorting by date oldest first. Name, Size and Type sort within each section
    /// (SPEC.md: "Sorting by Name, Size or Type sorts within each section").
    /// </summary>
    public bool SectionsAscending => Key == GallerySortKey.Date && Ascending;
}

/// <summary>One button of the Gallery's sort header.</summary>
public sealed partial class GallerySortHeader(GallerySortKey key, string title) : ObservableObject
{
    public GallerySortKey Key { get; } = key;

    /// <summary>"Name", "Size", "Type", or the date in use ("Date added" or "Date taken").</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial bool IsSorted { get; set; }

    [ObservableProperty]
    public partial bool IsAscending { get; set; }

    /// <summary>The Date button also opens a choice between "Date added" and "Date taken".</summary>
    public bool HasDateMenu => Key == GallerySortKey.Date;
}

/// <summary>A day header of the Gallery ("Today", "Yesterday", "Monday", "5 November 2026").</summary>
public sealed partial class GalleryHeaderRow : ThreadRow
{
    public GalleryHeaderRow(DateTime day, string text)
    {
        Day = day;
        Text = text;
    }

    public DateTime Day { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public override string Key => "gd:" + Day.Ticks.ToString(CultureInfo.InvariantCulture);

    internal override void UpdateFrom(ThreadRow fresh) => Text = ((GalleryHeaderRow)fresh).Text;
}

/// <summary>One row of same-size Gallery cells (the last row of a section may be shorter).</summary>
public sealed partial class GalleryCellRow : ThreadRow
{
    public GalleryCellRow(IReadOnlyList<ThreadItemViewModel> items)
    {
        Items = items;
    }

    [ObservableProperty]
    public partial IReadOnlyList<ThreadItemViewModel> Items { get; set; }

    public override string Key => "gc:" + Items[0].Id.ToString(CultureInfo.InvariantCulture);

    internal override void UpdateFrom(ThreadRow fresh)
    {
        var items = ((GalleryCellRow)fresh).Items;
        if (!items.SequenceEqual(Items))
        {
            Items = items;
        }
    }
}

/// <summary>Sizes and positions in the Gallery. Everything follows from the pane width and the cell size, never from the photos.</summary>
internal static class GalleryMetrics
{
    /// <summary>The smallest space between two cells (the cells of a row share what is left over; a selected cell's backdrop takes 10).</summary>
    public const double MinGap = 20;

    /// <summary>Room below and right of a print for its fake shadow.</summary>
    public const double ShadowSpace = 3;

    /// <summary>The photo box of a cell (square), in device-independent pixels.</summary>
    public static double CellSize(GalleryCellSize size) => size switch
    {
        GalleryCellSize.Small => 96,
        GalleryCellSize.Large => 176,
        _ => 128,
    };

    /// <summary>How many cells fit in <paramref name="availableWidth"/> with at least <see cref="MinGap"/> between them (at least one).</summary>
    public static int Columns(double availableWidth, double cellSize)
    {
        if (availableWidth <= 0 || cellSize <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Floor((availableWidth + MinGap) / (cellSize + MinGap)));
    }

    /// <summary>The width each cell gets: the row shared out evenly (whole pixels), the cell centered in it.</summary>
    public static double SlotWidth(double availableWidth, int columns)
        => Math.Max(1, Math.Floor(Math.Max(0, availableWidth) / Math.Max(1, columns)));

    /// <summary>The white border of a print: thin, growing a little with the cell.</summary>
    public static double FrameThickness(double cellSize) => Math.Max(3, Math.Round(cellSize * 0.03));

    /// <summary>The type icon of a cell that holds no photo or video.</summary>
    public static double IconSize(double cellSize) => Math.Round(cellSize * 0.5);

    /// <summary>
    /// Where the print (frame included) sits in a cell's photo box: the photo fitted inside keeping its shape (never cropped),
    /// centered, on the box's bottom line, leaving <paramref name="shadow"/> below and right for the shadow. Empty while the
    /// shape is unknown (<paramref name="aspectRatio"/> 0 or less).
    /// </summary>
    public static Rect PrintRect(Size box, double aspectRatio, double frame, double shadow = ShadowSpace)
    {
        if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio) || box.Width <= 0 || box.Height <= 0)
        {
            return Rect.Empty;
        }

        var maxPhotoWidth = Math.Max(1, box.Width - shadow - (2 * frame));
        var maxPhotoHeight = Math.Max(1, box.Height - shadow - (2 * frame));
        var photoWidth = maxPhotoWidth;
        var photoHeight = photoWidth / aspectRatio;
        if (photoHeight > maxPhotoHeight)
        {
            photoHeight = maxPhotoHeight;
            photoWidth = photoHeight * aspectRatio;
        }

        var width = Math.Round(Math.Max(1, photoWidth) + (2 * frame));
        var height = Math.Round(Math.Max(1, photoHeight) + (2 * frame));
        var x = Math.Round((box.Width - shadow - width) / 2);
        var y = Math.Round(box.Height - shadow - height);
        return new Rect(x, y, width, height);
    }
}

/// <summary>Sorts a thread's items into Gallery order and cuts them into day sections and rows. Pure; fast for tens of thousands.</summary>
internal static class GalleryBuilder
{
    /// <summary>The "Date taken" of a file: from its own data, else its last-modified date, else when it was added.</summary>
    /// <remarks>
    /// <see cref="LibraryItem.TakenAt"/> keeps the camera's offset, so its <see cref="DateTimeOffset.DateTime"/> is the time the camera
    /// showed; the file and added dates are instants, shown in this PC's zone. Sections and labels use the wall-clock time, sorting the instant.
    /// </remarks>
    public static DateTimeOffset DateTaken(LibraryItem item) => item.TakenAt ?? (item.FileModifiedAt ?? item.AddedAt).ToLocalTime();

    /// <summary>The date the sections and the Date sort use.</summary>
    public static DateTimeOffset DateOf(LibraryItem item, GalleryDateKind kind) => kind == GalleryDateKind.Taken ? DateTaken(item) : item.AddedAt.ToLocalTime();

    /// <summary>True when a category path (top-level first) lies in the Images category, found by its built-in key, so renaming doesn't matter.</summary>
    public static bool IsGalleryPath(IReadOnlyList<Category> path) => path.Any(c => c.BuiltInKey == ExtensionMap.ImagesKey);

    /// <summary>
    /// The items in Gallery order: by day of the chosen date (newest first unless sorting by date oldest first), then by the
    /// sort key within each day, then by name and id so the order is stable. <paramref name="days"/> gets each item's day.
    /// </summary>
    public static List<ThreadItemViewModel> Order(IReadOnlyList<ThreadItemViewModel> items, GallerySort sort, out DateTime[] days)
    {
        var count = items.Count;
        var entries = new Entry[count];
        var typeNames = new Dictionary<(string, ItemKind), string>();
        var needsType = sort.Key == GallerySortKey.Type;
        for (var i = 0; i < count; i++)
        {
            var vm = items[i];
            var item = vm.Item;
            var when = DateOf(item, sort.DateKind);
            var type = "";
            if (needsType && !typeNames.TryGetValue((item.TypeKey, item.Kind), out type!))
            {
                type = ThreadItemViewModel.TypeName(item.TypeKey, item.Kind);
                typeNames[(item.TypeKey, item.Kind)] = type;
            }

            entries[i] = new Entry(vm, when.DateTime.Date.Ticks, when.UtcTicks, item.SizeBytes ?? -1, type, i);
        }

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var sectionSign = sort.SectionsAscending ? 1 : -1;
        var keySign = sort.Ascending ? 1 : -1;
        Array.Sort(entries, (a, b) =>
        {
            var c = a.Day.CompareTo(b.Day) * sectionSign;
            if (c != 0)
            {
                return c;
            }

            c = sort.Key switch
            {
                GallerySortKey.Name => compare.Compare(a.Vm.FileName, b.Vm.FileName, CompareOptions.IgnoreCase),
                GallerySortKey.Size => a.Size.CompareTo(b.Size),
                GallerySortKey.Type => TypeCompare(compare, a, b),
                _ => a.Time.CompareTo(b.Time),
            } * keySign;
            if (c != 0)
            {
                return c;
            }

            if (sort.Key != GallerySortKey.Name)
            {
                c = compare.Compare(a.Vm.FileName, b.Vm.FileName, CompareOptions.IgnoreCase);
                if (c != 0)
                {
                    return c;
                }
            }

            c = a.Vm.Id.CompareTo(b.Vm.Id);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

        var ordered = new List<ThreadItemViewModel>(count);
        days = new DateTime[count];
        for (var i = 0; i < count; i++)
        {
            ordered.Add(entries[i].Vm);
            days[i] = new DateTime(entries[i].Day, DateTimeKind.Unspecified);
        }

        return ordered;
    }

    /// <summary>
    /// Day headers and rows of <paramref name="columns"/> cells for items already in Gallery order (<see cref="Order"/>):
    /// each day starts a new row, so a section's last row may be shorter.
    /// </summary>
    public static List<ThreadRow> BuildRows(IReadOnlyList<ThreadItemViewModel> ordered, IReadOnlyList<DateTime> days, int columns, DateTime today)
    {
        columns = Math.Max(1, columns);
        var rows = new List<ThreadRow>((ordered.Count / columns) + 64);
        var i = 0;
        while (i < ordered.Count)
        {
            var day = days[i];
            rows.Add(new GalleryHeaderRow(day, ThreadFormat.DateHeader(day, today)));
            while (i < ordered.Count && days[i] == day)
            {
                var length = 0;
                while (length < columns && i + length < ordered.Count && days[i + length] == day)
                {
                    length++;
                }

                var cells = new ThreadItemViewModel[length];
                for (var k = 0; k < length; k++)
                {
                    cells[k] = ordered[i + k];
                }

                rows.Add(new GalleryCellRow(cells));
                i += length;
            }
        }

        return rows;
    }

    private static int TypeCompare(CompareInfo compare, in Entry a, in Entry b)
    {
        var c = compare.Compare(a.Type, b.Type, CompareOptions.IgnoreCase);
        return c != 0 ? c : string.Compare(a.Vm.Extension, b.Vm.Extension, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Entry(ThreadItemViewModel Vm, long Day, long Time, long Size, string Type, int Index);
}
