using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>Section title in the search results ("Categories", "Files").</summary>
public sealed record SearchSectionHeader(string Title, string? Note = null);

/// <summary>A "#tag" suggestion row.</summary>
public sealed partial class TagSuggestionViewModel : ObservableObject
{
    public TagSuggestionViewModel(TagInfo tag)
    {
        Tag = tag;
    }

    public TagInfo Tag { get; }

    public string Name => "#" + Tag.Name;

    public string CountText => Tag.ItemCount == 1 ? L.Get("ChatList_FileCount_One") : L.Format("ChatList_FileCount", Tag.ItemCount);
}

/// <summary>A file hit in the global search. The thumbnail loads lazily when the row is first shown.</summary>
public sealed partial class FileResultViewModel : ObservableObject
{
    private const int ThumbnailPx = 80;
    private readonly IThumbnailService _thumbnails;
    private bool _thumbnailRequested;
    private ImageSource? _thumbnail;

    public FileResultViewModel(LibraryItem item, string categoryPath, IThumbnailService thumbnails)
    {
        Item = item;
        CategoryPath = categoryPath;
        _thumbnails = thumbnails;
        Symbol = Glyphs.ForItem(item.Kind, item.FileName);
        DateText = Glyphs.ChatTime(item.AddedAt, DateTimeOffset.Now);
        Detail = string.IsNullOrEmpty(categoryPath) ? Glyphs.FileSize(item.SizeBytes) : categoryPath;
    }

    public LibraryItem Item { get; }

    public string Name => Item.FileName;

    public string CategoryPath { get; }

    public string Detail { get; }

    public string DateText { get; }

    public SymbolRegular Symbol { get; }

    public bool IsPointer => Item.Mode == ItemMode.Pointer;

    public bool IsMissing => Item.IsMissing;

    public string? NotePreview => Item.NotePreview;

    /// <summary>Shell thumbnail or file icon; null shows the glyph tile.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    private async Task LoadThumbnailAsync()
    {
        try
        {
            ImageSource? image = null;
            if (!Item.IsMissing && Item.Kind != ItemKind.Note)
            {
                image = await _thumbnails.GetThumbnailAsync(Item.FullPath, ThumbnailPx);
            }

            image ??= await _thumbnails.GetFileIconAsync(Item.IsMissing ? "." + Item.Extension : Item.FullPath, 32);
            if (image is not null)
            {
                _thumbnail = image;
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Thumbnail for '{Item.FileName}' failed.", ex);
        }
    }
}

/// <summary>
/// Global search in the left pane: categories plus file names across the library, "#tag" suggestions, filters.
/// Debounced while typing; results are grouped in Categories and Files sections.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int MaxFileResults = 500;
    private readonly ILibraryService _library;
    private readonly CategoryIconService _icons;
    private readonly IThumbnailService _thumbnails;
    private readonly CategoryDirectory _directory;
    private readonly DispatcherTimer _debounce;
    private IReadOnlyList<TagInfo>? _tags;
    private int _version;

    public SearchViewModel(ILibraryService library, CategoryIconService icons, IThumbnailService thumbnails, CategoryDirectory directory)
    {
        _library = library;
        _icons = icons;
        _thumbnails = thumbnails;
        _directory = directory;
        Filter = new SearchFilterViewModel();
        Filter.Changed += (_, _) =>
        {
            UpdateActive();
            Restart();
        };
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunAsync();
        };
    }

    public SearchFilterViewModel Filter { get; }

    public ObservableCollection<object> Results { get; } = [];

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasNoResults;

    [ObservableProperty]
    private string _noResultsText = "";

    [ObservableProperty]
    private object? _selectedResult;

    public bool HasQuery => !string.IsNullOrEmpty(Query);

    /// <summary>Re-runs the current search (after library changes).</summary>
    public void Refresh()
    {
        if (IsActive)
        {
            Restart();
        }
    }

    public void InvalidateTags() => _tags = null;

    /// <summary>Only unread state changed (a thread was viewed): refreshes the badges of the category hits, without searching again.</summary>
    public Task RefreshUnreadAsync(IReadOnlySet<long> categoryIds)
        => IsActive ? CategoryRowViewModel.RefreshUnreadAsync(Results.OfType<CategoryRowViewModel>().ToList(), categoryIds, _library) : Task.CompletedTask;

    /// <summary>
    /// The note under "Files" when the library returned only part of the matches (it stops at a fixed number and says
    /// so with <see cref="SearchResults.HasMoreItems"/>), or null.
    /// </summary>
    internal static string? MoreResultsNote(SearchResults results)
        => results.HasMoreItems || results.Items.Count > MaxFileResults
            ? L.Format("Search_MoreResults", Math.Min(results.Items.Count, MaxFileResults))
            : null;

    [RelayCommand]
    private void Clear()
    {
        Query = "";
    }

    [RelayCommand]
    private void ApplyTag(TagSuggestionViewModel? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        var text = Query;
        var start = LastTokenStart(text);
        Query = text[..start] + "#" + suggestion.Tag.Name + " ";
        TagApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after a tag suggestion was applied, so the view can return focus to the box.</summary>
    public event EventHandler? TagApplied;

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        UpdateActive();
        Restart();
    }

    private void UpdateActive() => IsActive = !string.IsNullOrWhiteSpace(Query) || !Filter.IsEmpty;

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RunAsync()
    {
        var version = ++_version;
        if (!IsActive)
        {
            Results.Clear();
            HasNoResults = false;
            return;
        }

        IsBusy = true;
        try
        {
            var text = Query.Trim();
            var filter = Filter.ToItemFilter();

            // Tag suggestions for a trailing "#..." token.
            var suggestions = new List<TagSuggestionViewModel>();
            var searchText = text;
            var lastToken = text.Length == 0 ? "" : text[LastTokenStart(text)..];
            var typingTag = Query.Length > 0 && !char.IsWhiteSpace(Query[^1]) && lastToken.StartsWith('#');
            if (typingTag)
            {
                var prefix = lastToken[1..];
                _tags ??= await _library.GetTagsAsync();
                suggestions = _tags
                    .Where(t => t.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase) &&
                                !string.Equals(t.Name, prefix, StringComparison.CurrentCultureIgnoreCase))
                    .Take(8)
                    .Select(t => new TagSuggestionViewModel(t))
                    .ToList();
                if (prefix.Length == 0)
                {
                    searchText = text[..LastTokenStart(text)].Trim();
                }
            }

            SearchResults? results = null;
            if (searchText.Length > 0 || !filter.IsEmpty)
            {
                results = await _library.SearchAsync(searchText, filter.IsEmpty ? null : filter);
                await _directory.EnsureLoadedAsync();
            }

            if (version != _version)
            {
                return;
            }

            var items = new List<object>();
            if (suggestions.Count > 0)
            {
                items.Add(new SearchSectionHeader(L.Get("Search_SectionTags")));
                items.AddRange(suggestions);
            }

            if (results is not null)
            {
                // Category hits only make sense for text queries, not for pure filters.
                if (results.Categories.Count > 0 && searchText.Length > 0)
                {
                    items.Add(new SearchSectionHeader(L.Get("Search_SectionCategories")));
                    items.AddRange(results.Categories.Select(c => new CategoryRowViewModel(c, _icons)));
                }

                if (results.Items.Count > 0)
                {
                    items.Add(new SearchSectionHeader(L.Get("Search_SectionFiles"), MoreResultsNote(results)));
                    items.AddRange(results.Items.Take(MaxFileResults)
                        .Select(i => new FileResultViewModel(i, _directory.GetPath(i.HomeCategoryId), _thumbnails)));
                }
            }

            Results.Clear();
            foreach (var item in items)
            {
                Results.Add(item);
            }

            HasNoResults = Results.Count == 0;
            NoResultsText = text.Length > 0 ? L.Format("Search_NoResults", text) : L.Get("Search_NoResultsFiltered");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Search failed.", ex);
            if (version == _version)
            {
                Results.Clear();
                HasNoResults = true;
                NoResultsText = ex is LibraryException le ? le.Message : L.Get("Search_Failed");
            }
        }
        finally
        {
            if (version == _version)
            {
                IsBusy = false;
            }
        }
    }

    private static int LastTokenStart(string text)
    {
        var index = text.TrimEnd().LastIndexOf(' ');
        return index < 0 ? 0 : index + 1;
    }
}
