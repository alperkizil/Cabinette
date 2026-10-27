using CommunityToolkit.Mvvm.ComponentModel;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>What the view model needs from the view that currently shows it (scrolling, owner window, thread-owned dialogs).</summary>
public interface IThreadViewHost
{
    /// <summary>Handle of the window hosting the thread (owner of the shell context menu).</summary>
    IntPtr OwnerHandle { get; }

    void ScrollToEnd();

    /// <summary>
    /// A new Gallery order starts at its top. Only the Gallery list moves: the chat keeps the place you were reading (unlike
    /// <see cref="ScrollToEnd"/>, which also sends the chat to its newest message).
    /// </summary>
    void ScrollGalleryToStart()
    {
    }

    /// <summary>Items were added by a live update (not by the user in this thread); the view may count them if scrolled up.</summary>
    void NotifyNewItems(int count);

    /// <summary>Scrolls the item into view (centered) in the active view mode.</summary>
    void BringItemIntoView(ThreadItemViewModel item);

    /// <summary>Shows the tag editor modally. True when the user saved.</summary>
    bool ShowTagEditor(TagEditorViewModel editor);

    /// <summary>Opens the built-in image/video viewer (non-modal) at <paramref name="index"/>.</summary>
    void ShowMediaViewer(IReadOnlyList<ThreadItemViewModel> items, int index);
}

public enum DetailsColumn
{
    Name = 0,
    Type,
    Size,
    DateAdded,
    Mode,
    Tags,
}

/// <summary>Header of a sortable column in the details view.</summary>
public sealed partial class DetailsColumnHeader(DetailsColumn column, string title) : ObservableObject
{
    public DetailsColumn Column { get; } = column;

    /// <summary>The header text (the date column reads "Starred" in the Starred view).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial bool IsSorted { get; set; }

    [ObservableProperty]
    public partial bool IsAscending { get; set; }
}
