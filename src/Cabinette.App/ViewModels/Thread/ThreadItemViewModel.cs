using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Cabinette.App.Localization;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>How an item is drawn in a bubble or tile.</summary>
public enum ItemVisualKind
{
    Document = 0,
    Image,
    Video,
    Audio,
    Note,
    Link,
}

/// <summary>
/// One library item in a thread. Shared by the chat, grid and details views. Expensive things (thumbnails, media info,
/// link targets, full note text) are only loaded when a realized container asks for them.
/// </summary>
public sealed partial class ThreadItemViewModel : ObservableObject
{
    public const double MaxThumbWidth = 300;
    public const double MaxThumbHeight = 330;
    public const double MinThumbWidth = 150;
    public const double MinThumbHeight = 110;
    private const int NotePreviewMaxLines = 12;

    private LibraryItem _item;
    private bool _mediaInfoRequested;
    private bool _linkRequested;
    private bool _aspectFromThumbnail;
    private bool _positionFromPlayer;

    internal ThreadItemViewModel(LibraryItem item, ThreadViewModel owner)
    {
        _item = item;
        Owner = owner;
        VisualKind = GetVisualKind(item);
    }

    public ThreadViewModel Owner { get; }

    public LibraryItem Item => _item;

    public long Id => _item.Id;

    public string FileName => _item.FileName;

    public string FullPath => _item.FullPath;

    public string Extension => _item.Extension;

    public string TypeKey => _item.TypeKey;

    public DateTimeOffset AddedAt => _item.AddedAt;

    public long SizeBytes => _item.SizeBytes ?? -1;

    public bool IsPointer => _item.Mode == ItemMode.Pointer;

    public bool IsMissing => _item.IsMissing;

    public bool CanLocate => _item.IsMissing && _item.Mode == ItemMode.Pointer;

    /// <summary>Starred (SPEC.md section 4): the bubble, tile or row shows a small star.</summary>
    public bool IsStarred => _item.IsStarred;

    /// <summary>
    /// The time the thread is ordered and grouped by: when the file was added, or in the Starred view when it was starred
    /// (that view lists stars like messages, the newest at the bottom).
    /// </summary>
    public DateTimeOffset ListedAt => Owner.IsStarredView && _item.StarredAt is { } starred ? starred : _item.AddedAt;

    public IReadOnlyList<string> Tags => _item.Tags;

    public bool HasTags => _item.Tags.Count > 0;

    public string TagsText => string.Join("  ", _item.Tags.Select(t => "#" + t));

    /// <summary>Changes only when the item changes (cache key component so edited files get fresh thumbnails).</summary>
    public string ContentVersion => (_item.FileModifiedAt?.UtcTicks ?? _item.SizeBytes ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ItemVisualKind VisualKind { get; private set; }

    /// <summary>What the templates draw: media that can't be previewed (missing, no thumbnail) falls back to a document tile.</summary>
    public ItemVisualKind DisplayKind
    {
        get
        {
            if (_item.IsMissing && VisualKind is ItemVisualKind.Image or ItemVisualKind.Video or ItemVisualKind.Audio)
            {
                return ItemVisualKind.Document;
            }

            return VisualKind == ItemVisualKind.Image && ThumbnailFailed ? ItemVisualKind.Document : VisualKind;
        }
    }

    public string ExtensionLabel => _item.Extension.ToUpperInvariant();

    public string SizeText => ThreadFormat.Size(_item.SizeBytes);

    public string TypeSizeText
    {
        get
        {
            var size = SizeText;
            var ext = ExtensionLabel;
            return ext.Length == 0 ? size : size.Length == 0 ? ext : $"{ext} · {size}";
        }
    }

    /// <summary>Document details line: "64 pages · PDF · 780 KB" (pages when the shell knows them). Reading it starts the lookup.</summary>
    public string DocumentInfoText
    {
        get
        {
            EnsureMediaInfo();
            if (PageCount is not { } pages)
            {
                return TypeSizeText;
            }

            var pageText = pages == 1 ? L.Get("Thread_PageCountOne") : L.Format("Thread_PageCountMany", pages);
            var rest = TypeSizeText;
            return rest.Length == 0 ? pageText : $"{pageText} · {rest}";
        }
    }

    public string TimeText => ThreadFormat.Time(ListedAt);

    /// <summary>The details view's date column: when it was added, or in the Starred view when it was starred.</summary>
    public string DateAddedText => ThreadFormat.DateTimeShort(ListedAt);

    public string ModeText => L.Get(IsPointer ? "Thread_ModePointer" : "Thread_ModeCopy");

    public string TypeText => TypeName(_item.TypeKey, _item.Kind);

    public string ToolTipText => _item.IsMissing ? $"{_item.FullPath}\n{L.Get("Thread_MissingTooltip")}" : _item.FullPath;

    /// <summary>When the photo or video was taken, from its own data; null when unknown (SPEC.md section 4, "Gallery").</summary>
    public DateTimeOffset? TakenAt => _item.TakenAt;

    /// <summary>The Gallery's "Date taken": <see cref="TakenAt"/>, else the file's last-modified date, else when it was added.</summary>
    public DateTimeOffset DateTaken => GalleryBuilder.DateTaken(_item);

    /// <summary>The Gallery cell's tooltip: the full name, size, date added and date taken (or the date used instead).</summary>
    public string GalleryToolTipText
    {
        get
        {
            var lines = new List<string>(5) { _item.FileName };
            if (SizeText is { Length: > 0 } size)
            {
                lines.Add(size);
            }

            lines.Add(L.Format("Thread_GalleryTipAdded", ThreadFormat.DateTimeShort(_item.AddedAt)));
            lines.Add(_item.TakenAt is { } taken ? L.Format("Thread_GalleryTipTaken", ThreadFormat.WallClockShort(taken))
                : _item.FileModifiedAt is { } modified ? L.Format("Thread_GalleryTipTakenModified", ThreadFormat.DateTimeShort(modified))
                : L.Get("Thread_GalleryTipTakenNone"));
            if (_item.IsMissing)
            {
                lines.Add(L.Get("Thread_MissingTooltip"));
            }

            return string.Join('\n', lines);
        }
    }

    // ------------------------------------------------------------------ selection

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Set briefly by <see cref="ThreadViewModel.FocusItem"/>; the templates flash a highlight while it is true.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    // ------------------------------------------------------------------ media

    /// <summary>Width / height of the image or video; 0 while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbWidth), nameof(ThumbHeight))]
    public partial double AspectRatio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(HasDuration), nameof(ElapsedText), nameof(PlaybackDurationSeconds))]
    public partial TimeSpan? Duration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayKind))]
    public partial bool ThumbnailFailed { get; set; }

    /// <summary>Pages of a document (System.Document.PageCount), or null while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentInfoText))]
    public partial int? PageCount { get; set; }

    /// <summary>Bubble image width (logical px), from the aspect ratio. Reading it starts the media-info lookup.</summary>
    public double ThumbWidth
    {
        get
        {
            EnsureMediaInfo();
            return ComputeThumbSize().Width;
        }
    }

    public double ThumbHeight => ComputeThumbSize().Height;

    public string DurationText
    {
        get
        {
            EnsureMediaInfo();
            return ThreadFormat.Duration(Duration);
        }
    }

    public bool HasDuration => Duration is not null;

    // ------------------------------------------------------------------ audio

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    public partial bool IsPlaying { get; set; }

    /// <summary>Seconds. Two-way bound to the voice-note slider; user changes seek the player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText), nameof(PlaybackDurationSeconds))]
    public partial double PlaybackPosition { get; set; }

    /// <summary>Slider maximum. Until the duration is known, keeps the current position in range.</summary>
    public double PlaybackDurationSeconds
    {
        get
        {
            EnsureMediaInfo();
            return Duration is { } duration ? Math.Max(0.1, duration.TotalSeconds) : Math.Max(1, PlaybackPosition);
        }
    }

    /// <summary>Duration while idle, elapsed time while playing (like a voice note).</summary>
    public string ElapsedText => IsPlaying || PlaybackPosition > 0.05 ? ThreadFormat.Duration(PlaybackPosition) : DurationText;

    // ------------------------------------------------------------------ notes

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteText), nameof(IsNoteTruncated))]
    public partial bool IsNoteExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteText))]
    public partial string? FullNoteText { get; set; }

    public string NoteText => IsNoteExpanded && FullNoteText is not null ? FullNoteText.TrimEnd() : NotePreviewText;

    public bool IsNoteTruncated
    {
        get
        {
            if (IsNoteExpanded || _item.Kind != ItemKind.Note)
            {
                return false;
            }

            var preview = _item.NotePreview ?? "";
            if (CountLines(preview) > NotePreviewMaxLines)
            {
                return true;
            }

            // The preview is the first ~300 characters; anything beyond means the file is longer.
            return _item.SizeBytes is { } size && size > Encoding.UTF8.GetByteCount(preview.TrimEnd()) + 4;
        }
    }

    private string NotePreviewText
    {
        get
        {
            var preview = (_item.NotePreview ?? "").TrimEnd();
            if (CountLines(preview) <= NotePreviewMaxLines)
            {
                if (preview.Length == 0)
                {
                    return _item.FileName;
                }

                return IsNoteTruncated && !preview.EndsWith('…') ? preview + "…" : preview;
            }

            var lines = preview.Split('\n');
            return string.Join('\n', lines.Take(NotePreviewMaxLines)).TrimEnd() + "…";
        }
    }

    // ------------------------------------------------------------------ links

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkHost))]
    public partial string? LinkUrl { get; set; }

    /// <summary>The URL of a .url shortcut (read lazily), or the file name until it is known.</summary>
    public string LinkDisplay
    {
        get
        {
            EnsureLink();
            return LinkUrl ?? _item.FileName;
        }
    }

    public string LinkHost
    {
        get
        {
            EnsureLink();
            return LinkUrl is not null && Uri.TryCreate(LinkUrl, UriKind.Absolute, out var uri)
                ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
                : L.Get("Thread_Link");
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void PlayPause() => Owner.Audio.Toggle(this);

    [RelayCommand]
    private Task LocateAsync() => Owner.LocateAsync(this, prompt: false);

    [RelayCommand]
    private void OpenInViewer() => Owner.OpenInViewer(this);

    [RelayCommand]
    private Task OpenAsync() => Owner.OpenItemAsync(this);

    [RelayCommand]
    private async Task ReadMoreAsync()
    {
        FullNoteText ??= await Owner.ReadNoteTextAsync(this);
        IsNoteExpanded = FullNoteText is not null;
    }

    // ------------------------------------------------------------------ internals

    internal static ItemVisualKind GetVisualKind(LibraryItem item)
    {
        if (item.Kind == ItemKind.Note)
        {
            return ItemVisualKind.Note;
        }

        if (item.Kind == ItemKind.Link || item.Extension is "url" or "website")
        {
            return ItemVisualKind.Link;
        }

        if (item.Kind == ItemKind.PastedImage || item.TypeKey == ExtensionMap.ImagesKey)
        {
            return ItemVisualKind.Image;
        }

        return item.TypeKey switch
        {
            "video" => ItemVisualKind.Video,
            "audio" => ItemVisualKind.Audio,
            _ => ItemVisualKind.Document,
        };
    }

    internal static string TypeName(string typeKey, ItemKind kind)
    {
        if (kind == ItemKind.Note)
        {
            return L.Get("Thread_Type_note");
        }

        var key = "Thread_Type_" + typeKey;
        var text = L.Get(key);
        return text.StartsWith('[') ? L.Get("Thread_Type_other") : text;
    }

    /// <summary>True when the visible data is the same (records compare lists by reference, so do it by hand).</summary>
    internal static bool SameContent(LibraryItem a, LibraryItem b)
        => a.FileName == b.FileName && a.FullPath == b.FullPath && a.Mode == b.Mode && a.Kind == b.Kind &&
           a.IsMissing == b.IsMissing && a.SizeBytes == b.SizeBytes && a.NotePreview == b.NotePreview &&
           a.TypeKey == b.TypeKey && a.AddedAt == b.AddedAt && a.BatchId == b.BatchId &&
           a.FileModifiedAt == b.FileModifiedAt && a.HomeCategoryId == b.HomeCategoryId && a.StarredAt == b.StarredAt && a.TakenAt == b.TakenAt &&
           a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal) && a.CategoryIds.SequenceEqual(b.CategoryIds);

    internal void Update(LibraryItem item)
    {
        var pathChanged = !string.Equals(item.FullPath, _item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                          item.FileModifiedAt != _item.FileModifiedAt;
        var foundAgain = _item.IsMissing && !item.IsMissing;
        _item = item;
        VisualKind = GetVisualKind(item);
        if (pathChanged || foundAgain)
        {
            _mediaInfoRequested = false;
            _linkRequested = false;
            _aspectFromThumbnail = false;
            ThumbnailFailed = false;
            if (item.Kind == ItemKind.Note)
            {
                FullNoteText = null;
                IsNoteExpanded = false;
            }
        }

        OnPropertyChanged(string.Empty);
    }

    internal void SetMissing(bool missing)
    {
        if (_item.IsMissing != missing)
        {
            Update(_item with { IsMissing = missing });
        }
    }

    /// <summary>Called by the thumbnail control when a load finishes.</summary>
    internal void OnThumbnailLoaded(BitmapSource? image)
    {
        if (image is null)
        {
            if (VisualKind == ItemVisualKind.Image)
            {
                ThumbnailFailed = true;
            }

            return;
        }

        if (VisualKind is ItemVisualKind.Image or ItemVisualKind.Video && image.PixelHeight > 0)
        {
            _aspectFromThumbnail = true;
            var aspect = (double)image.PixelWidth / image.PixelHeight;
            if (Math.Abs(aspect - AspectRatio) > 0.01)
            {
                AspectRatio = aspect;
            }
        }
    }

    internal void SetPositionFromPlayer(double seconds)
    {
        _positionFromPlayer = true;
        try
        {
            PlaybackPosition = seconds;
        }
        finally
        {
            _positionFromPlayer = false;
        }
    }

    internal void SetPlayerDuration(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero && (Duration is null || Math.Abs((Duration.Value - duration).TotalSeconds) > 0.5))
        {
            Duration = duration;
        }
    }

    partial void OnPlaybackPositionChanged(double value)
    {
        if (!_positionFromPlayer)
        {
            Owner.Audio.Seek(this, value);
        }
    }

    private (double Width, double Height) ComputeThumbSize()
    {
        var aspect = AspectRatio;
        if (aspect <= 0)
        {
            return (MaxThumbWidth, VisualKind == ItemVisualKind.Video ? Math.Round(MaxThumbWidth * 9 / 16) : 225);
        }

        double width = MaxThumbWidth, height = width / aspect;
        if (height > MaxThumbHeight)
        {
            height = MaxThumbHeight;
            width = Math.Max(MinThumbWidth, height * aspect);
        }

        height = Math.Max(MinThumbHeight, height);
        return (Math.Round(width), Math.Round(height));
    }

    private void EnsureMediaInfo()
    {
        if (_mediaInfoRequested || _item.IsMissing || VisualKind is ItemVisualKind.Note or ItemVisualKind.Link)
        {
            return;
        }

        _mediaInfoRequested = true;
        _ = LoadMediaInfoAsync();
    }

    private async Task LoadMediaInfoAsync()
    {
        var info = await Owner.Loader.GetMediaInfoAsync(_item.FullPath, Owner.Lifetime);
        if (info is null)
        {
            return;
        }

        if (info.Duration is { } duration && duration > TimeSpan.Zero && VisualKind != ItemVisualKind.Image)
        {
            Duration = duration;
        }

        if (info.PageCount is > 0)
        {
            PageCount = info.PageCount;
        }

        if (!_aspectFromThumbnail && VisualKind is ItemVisualKind.Image or ItemVisualKind.Video && info.Width is > 0 && info.Height is > 0)
        {
            AspectRatio = (double)info.Width.Value / info.Height.Value;
        }
    }

    private void EnsureLink()
    {
        if (_linkRequested || VisualKind != ItemVisualKind.Link)
        {
            return;
        }

        _linkRequested = true;
        _ = LoadLinkAsync();
    }

    private async Task LoadLinkAsync()
    {
        var path = _item.FullPath;
        var url = await Task.Run(() => ReadUrlShortcut(path));
        if (url is not null)
        {
            LinkUrl = url;
            OnPropertyChanged(nameof(LinkDisplay));
        }
    }

    private static string? ReadUrlShortcut(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(path).Take(64))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    return url.Length > 0 ? url : null;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable shortcut: the bubble keeps showing the file name.
        }

        return null;
    }

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
