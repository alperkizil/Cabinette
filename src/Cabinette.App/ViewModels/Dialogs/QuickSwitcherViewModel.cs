using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>What the quick switcher's choice does (implemented by the main window's view model).</summary>
public interface IQuickSwitcherActions
{
    /// <summary>Opens the category's thread (with its subcategories); with <paramref name="focusItemId"/>, scrolled to that file and highlighted.</summary>
    Task OpenCategoryAsync(long categoryId, long? focusItemId);

    /// <summary>Opens the file in its default app ("Locate…" for a missing linked file).</summary>
    Task OpenFileAsync(LibraryItem item);
}

/// <summary>A row of the quick switcher: a category or a file.</summary>
public abstract class QuickSwitcherRow : ObservableObject
{
    protected QuickSwitcherRow(string name, string detail)
    {
        Name = name;
        Detail = detail;
    }

    public string Name { get; }

    /// <summary>A file's category path ("Code › Python"); a subcategory's parent path; empty for a top-level category.</summary>
    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => HasDetail ? $"{Name}, {Detail}" : Name;
}

public sealed partial class QuickSwitcherCategoryRow : QuickSwitcherRow
{
    public QuickSwitcherCategoryRow(Category category, string parentPath)
        : base(category.Name, parentPath)
    {
        Category = category;
    }

    public Category Category { get; }

    public long CategoryId => Category.Id;

    public string? IconKey => Category.IconKey;

    /// <summary>The category's PNG icon, decoded in the background; null shows the glyph avatar.</summary>
    [ObservableProperty]
    private ImageSource? _icon;
}

public sealed class QuickSwitcherFileRow : QuickSwitcherRow
{
    public QuickSwitcherFileRow(LibraryItem item, string categoryPath)
        : base(item.FileName, categoryPath)
    {
        Item = item;
        Symbol = Glyphs.ForItem(item.Kind, item.FileName);
    }

    public LibraryItem Item { get; }

    /// <summary>The file's type icon.</summary>
    public SymbolRegular Symbol { get; }

    public bool IsPointer => Item.Mode == ItemMode.Pointer;

    public bool IsMissing => Item.IsMissing;
}

/// <summary>The row the user acted on. The main window applies it once the switcher has closed.</summary>
public sealed record QuickSwitcherChoice(QuickSwitcherRow Row, bool OpenFile)
{
    /// <summary>
    /// A category opens its thread. A file opens its home category's thread, scrolled to the file (a file can be in several
    /// categories; its home is where it was first added), or with <see cref="OpenFile"/> (Shift+Enter) its default app.
    /// </summary>
    public Task ApplyAsync(IQuickSwitcherActions actions) => Row switch
    {
        QuickSwitcherFileRow file when OpenFile => actions.OpenFileAsync(file.Item),
        QuickSwitcherFileRow file => actions.OpenCategoryAsync(file.Item.HomeCategoryId, file.Item.Id),
        QuickSwitcherCategoryRow category => actions.OpenCategoryAsync(category.CategoryId, null),
        _ => Task.CompletedTask,
    };
}

/// <summary>
/// Ctrl+K (SPEC section 7): a search box over the window that finds categories and files by name with the main search's
/// matching (<see cref="ILibraryService.SearchAsync"/>), best matches first, as you type. An empty box lists the recently
/// opened categories. Arrow keys choose, Enter opens, Shift+Enter opens a file in its default app, Esc closes.
/// </summary>
public sealed partial class QuickSwitcherViewModel : DialogViewModelBase
{
    /// <summary>At most this many matches are listed.</summary>
    public const int MaxResults = 10;

    /// <summary>At most this many recently opened categories are listed in the empty box.</summary>
    public const int MaxRecent = 8;

    /// <summary>The search waits this long after the last key press.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly Func<string, CancellationToken, Task<SearchResults>> _search;
    private readonly CategoryDirectory _directory;
    private readonly CategoryIconService? _icons;
    private readonly IReadOnlyList<long> _recentCategoryIds;
    private readonly long? _openCategoryId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _queryCts;

    /// <summary>The (trimmed) query the listed rows belong to; null before anything was listed.</summary>
    private string? _shownQuery;

    /// <param name="search">The main search (library); runs on a background thread.</param>
    /// <param name="recentCategoryIds">Recently opened categories, the most recent first.</param>
    /// <param name="openCategoryId">The thread on screen: left out of the recent list (you are already there).</param>
    /// <param name="delay">The debounce wait (tests replace it).</param>
    public QuickSwitcherViewModel(
        Func<string, CancellationToken, Task<SearchResults>> search,
        CategoryDirectory directory,
        CategoryIconService? icons,
        IReadOnlyList<long> recentCategoryIds,
        long? openCategoryId,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(L.Get("QuickSwitcher_Title"))
    {
        _search = search;
        _directory = directory;
        _icons = icons;
        _recentCategoryIds = recentCategoryIds;
        _openCategoryId = openCategoryId;
        _delay = delay ?? Task.Delay;
    }

    public override double Width => 560;

    public override bool IsTopAligned => true;

    public ObservableCollection<QuickSwitcherRow> Rows { get; } = [];

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private QuickSwitcherRow? _selected;

    /// <summary>The box is empty and the rows are the recently opened categories.</summary>
    [ObservableProperty]
    private bool _isShowingRecent;

    /// <summary>Nothing to list: <see cref="EmptyText"/> says why.</summary>
    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyText = "";

    /// <summary>The search or list update in flight (tests await it).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Lists the recently opened categories. Call before showing the switcher.</summary>
    public Task LoadAsync() => Restart(debounce: false);

    /// <summary>
    /// The switcher's search over <paramref name="library"/>: the main search, which lists only the newest matching files when
    /// there are many (<see cref="SearchResults.HasMoreItems"/>), plus in that case the files named like the text
    /// (<see cref="ILibraryService.SearchNamePrefixAsync"/>), so the ranking also sees an older file whose name is the text
    /// or starts with it. Those are older than every file listed, so they go last and the files stay newest first.
    /// </summary>
    public static async Task<SearchResults> SearchLibraryAsync(ILibraryService library, string text, CancellationToken ct)
    {
        var results = await library.SearchAsync(text, null, ct);
        if (!results.HasMoreItems)
        {
            return results;
        }

        var named = await library.SearchNamePrefixAsync(text, MaxResults, null, ct);
        var listed = results.Items.Select(i => i.Id).ToHashSet();
        var older = named.Where(i => listed.Add(i.Id)).OrderByDescending(i => i.AddedAt).ThenByDescending(i => i.Id).ToList();
        return older.Count == 0 ? results : results with { Items = [.. results.Items, .. older] };
    }

    /// <summary>Up / Down: moves the selection, staying within the list.</summary>
    public void MoveSelection(int delta)
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Rows.IndexOf(Selected);
        Selected = Rows[Math.Clamp(index + delta, 0, Rows.Count - 1)];
    }

    /// <summary>Enter.</summary>
    public override void Accept() => _ = ActivateSelectedAsync(openFile: false);

    /// <summary>
    /// Enter, or Shift+Enter with <paramref name="openFile"/>: acts on the selected row. When the list hasn't caught up with
    /// the typing yet (Enter within the debounce), searches right away and takes the best match.
    /// </summary>
    public async Task ActivateSelectedAsync(bool openFile)
    {
        if (IsClosed)
        {
            return;
        }

        if (Query.Trim() != _shownQuery)
        {
            var text = Query.Trim();
            await Restart(debounce: false);
            if (text != _shownQuery || text != Query.Trim())
            {
                // The search failed, or the user typed on meanwhile.
                return;
            }
        }

        if (Selected is { } row)
        {
            Activate(row, openFile);
        }
    }

    /// <summary>A click on a row, or Enter / Shift+Enter: closes the switcher with the choice.</summary>
    public void Activate(QuickSwitcherRow row, bool openFile)
    {
        if (!IsClosed)
        {
            Close(new QuickSwitcherChoice(row, openFile));
        }
    }

    protected override void OnClosing() => _queryCts?.Cancel();

    partial void OnQueryChanged(string value)
    {
        if (value.Trim() == _shownQuery)
        {
            // Only spaces changed: the list already matches. Drop a search for older text that is still on its way.
            _queryCts?.Cancel();
            Pending = Task.CompletedTask;
            return;
        }

        _ = Restart(debounce: true);
    }

    /// <summary>Cancels the query in flight and starts one for the current text (the recent list when it is empty).</summary>
    private Task Restart(bool debounce)
    {
        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var text = Query.Trim();
        Pending = text.Length == 0 ? ShowRecentAsync(cts.Token) : SearchAsync(text, debounce, cts.Token);
        return Pending;
    }

    private async Task ShowRecentAsync(CancellationToken ct)
    {
        try
        {
            await _directory.EnsureLoadedAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        var rows = new List<QuickSwitcherRow>();
        foreach (var id in _recentCategoryIds)
        {
            if (rows.Count >= MaxRecent)
            {
                break;
            }

            // Deleted categories just drop out; the open thread is where you already are.
            if (id != _openCategoryId && _directory.Get(id) is { } category)
            {
                rows.Add(CreateCategoryRow(category));
            }
        }

        Show(rows, "", isRecent: true, L.Get("QuickSwitcher_NoRecent"));
    }

    private async Task SearchAsync(string text, bool debounce, CancellationToken ct)
    {
        try
        {
            if (debounce)
            {
                await _delay(Debounce, ct);
            }

            var results = await Task.Run(() => _search(text, ct), ct);
            await _directory.EnsureLoadedAsync(ct);
            ct.ThrowIfCancellationRequested();

            var ranked = QuickSwitcherRanking.Rank(
                text,
                results.Categories.Select(c => c.Category.Name).ToList(),
                results.Items.Select(i => i.FileName).ToList(),
                MaxResults);
            var rows = ranked
                .Select(hit => hit.IsCategory
                    ? CreateCategoryRow(results.Categories[hit.Index].Category)
                    : (QuickSwitcherRow)new QuickSwitcherFileRow(results.Items[hit.Index], _directory.GetPath(results.Items[hit.Index].HomeCategoryId)))
                .ToList();
            Show(rows, text, isRecent: false, L.Format("Search_NoResults", text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            AppLog.Warn("Quick switcher search failed.", ex);
            Show([], null, isRecent: false, ex is LibraryException library ? library.Message : L.Get("Search_Failed"));
        }
    }

    private void Show(List<QuickSwitcherRow> rows, string? query, bool isRecent, string emptyText)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        _shownQuery = query;
        IsShowingRecent = isRecent && rows.Count > 0;
        EmptyText = emptyText;
        IsEmpty = rows.Count == 0;
        Selected = rows.Count > 0 ? rows[0] : null;
    }

    private QuickSwitcherCategoryRow CreateCategoryRow(Category category)
    {
        var parentPath = category.ParentId is { } parentId ? _directory.GetPath(parentId) : "";
        var row = new QuickSwitcherCategoryRow(category, parentPath);
        if (_icons is not null && category.IconPng is { Length: > 0 } png)
        {
            row.Icon = _icons.TryGetCached(png);
            if (row.Icon is null)
            {
                _ = LoadIconAsync(row, png);
            }
        }

        return row;
    }

    private async Task LoadIconAsync(QuickSwitcherCategoryRow row, byte[] png)
    {
        if (_icons is not null)
        {
            row.Icon = await _icons.GetAsync(png);
        }
    }
}

/// <summary>Orders the main search's hits for the quick switcher: best name matches first.</summary>
internal static class QuickSwitcherRanking
{
    /// <summary>The name is the typed text (a file's name also without its extension).</summary>
    public const int Exact = 0;

    /// <summary>The name starts with the typed text.</summary>
    public const int Prefix = 1;

    /// <summary>A word in the name starts with the typed text ("2026 invoice.pdf" for "inv").</summary>
    public const int WordStart = 2;

    /// <summary>The typed text is somewhere in the name.</summary>
    public const int Contains = 3;

    /// <summary>The words match apart from each other, or only "#tag" tokens were typed.</summary>
    public const int Other = 4;

    /// <summary>
    /// Scores every hit, then sorts by score; equal scores keep categories before files and otherwise the library's order
    /// (categories A–Z, files newest first). Returns at most <paramref name="max"/> hits.
    /// </summary>
    public static List<(bool IsCategory, int Index)> Rank(string query, IReadOnlyList<string> categoryNames, IReadOnlyList<string> fileNames, int max)
    {
        var phrase = Phrase(query);
        return categoryNames.Select((name, index) => (IsCategory: true, Index: index, Score: Score(name, phrase, isFile: false)))
            .Concat(fileNames.Select((name, index) => (IsCategory: false, Index: index, Score: Score(name, phrase, isFile: true))))
            .OrderBy(hit => hit.Score)
            .ThenBy(hit => hit.IsCategory ? 0 : 1)
            .ThenBy(hit => hit.Index)
            .Take(max)
            .Select(hit => (hit.IsCategory, hit.Index))
            .ToList();
    }

    /// <summary>The free-text words of the query joined by single spaces ("#tag" tokens left out); "" when there are none.</summary>
    public static string Phrase(string query)
        => string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(token => !token.StartsWith('#')));

    public static int Score(string name, string phrase, bool isFile)
    {
        if (phrase.Length == 0)
        {
            return Other;
        }

        var score = ScoreName(name, phrase);
        if (isFile && score != Exact && Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem && Equal(stem, phrase))
        {
            score = Exact;
        }

        return score;
    }

    private static int ScoreName(string name, string phrase)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        if (Equal(name, phrase))
        {
            return Exact;
        }

        if (compare.IsPrefix(name, phrase, CompareOptions.IgnoreCase))
        {
            return Prefix;
        }

        var score = Other;
        for (var start = 0; start < name.Length;)
        {
            var index = compare.IndexOf(name, phrase, start, CompareOptions.IgnoreCase);
            if (index < 0)
            {
                break;
            }

            if (index > 0 && !char.IsLetterOrDigit(name[index - 1]))
            {
                return WordStart;
            }

            score = Contains;
            start = index + 1;
        }

        return score;
    }

    private static bool Equal(string a, string b) => CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase) == 0;
}
