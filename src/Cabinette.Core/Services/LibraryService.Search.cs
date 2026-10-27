using System.Globalization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Search;
using Dapper;

namespace Cabinette.Core.Services;

internal sealed partial class LibraryService
{
    /// <summary>Maximum number of items returned by the main-window search.</summary>
    internal const int GlobalSearchItemLimit = 500;

    public Task<SearchResults> SearchAsync(string searchText, ItemFilter? filter = null, CancellationToken ct = default)
        => ReadAsync(connection =>
        {
            var search = ParsedSearch.Parse(searchText);
            if (search.IsEmpty && (filter is null || filter.IsEmpty))
            {
                return new SearchResults([], []);
            }

            var tree = CategoryTree.Load(connection);

            // Categories: every free-text term must appear in the name (case-insensitive, culture-aware).
            IReadOnlyList<CategorySummary> categories = [];
            if (search.Terms.Count > 0)
            {
                var compare = CultureInfo.CurrentCulture.CompareInfo;
                var matches = tree.All
                    .Where(c => search.Terms.All(t => compare.IndexOf(c.Name, t, CompareOptions.IgnoreCase) >= 0))
                    .ToList();
                categories = SortSummaries(
                    BuildSummaries(connection, tree, matches).Where(s => s.Category.Kind != CategoryKind.System || s.ItemCount > 0),
                    CategorySort.Alphabetical);
            }

            // One row more than the limit tells whether there are more matches.
            var p = new DynamicParameters();
            var where = ItemQueryBuilder.BuildWhere(null, search, includeNoteText: false, filter, p);
            p.Add("limit", GlobalSearchItemLimit + 1);
            var rows = ItemReader.QueryRows(connection, null, $"WHERE {where} ORDER BY i.added_at DESC, i.id DESC LIMIT @limit", p);
            var hasMore = rows.Count > GlobalSearchItemLimit;
            if (hasMore)
            {
                rows.RemoveRange(GlobalSearchItemLimit, rows.Count - GlobalSearchItemLimit);
            }

            var items = ItemReader.Materialize(connection, null, rows, tree, _settings.VaultPath);
            return new SearchResults(categories, items) { HasMoreItems = hasMore };
        }, ct);

    public Task<IReadOnlyList<LibraryItem>> SearchNamePrefixAsync(string searchText, int limit, ItemFilter? filter = null, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<LibraryItem>>(connection =>
        {
            var search = ParsedSearch.Parse(searchText);
            var phrase = string.Join(' ', search.Terms);
            if (phrase.Length == 0 || limit <= 0)
            {
                return [];
            }

            // The search's own conditions plus "starts with the phrase", so every row also matches SearchAsync. The prefix goes
            // through the trigram index (initial-token phrase) like the terms do, so it costs about as much as the search itself.
            var p = new DynamicParameters();
            var where = ItemQueryBuilder.BuildWhere(null, search, includeNoteText: false, filter, p, namePrefix: phrase);

            // Exact names first: the whole name (the prefix plus nothing), or the name without its extension (the prefix,
            // a dot, then no further dot). SQLite's length() and substr() count characters, so count the phrase's too.
            p.Add("phraseLength", phrase.EnumerateRunes().Count());
            p.Add("limit", limit);
            const string Exact = "(length(i.file_name) = @phraseLength OR (substr(i.file_name, @phraseLength + 1, 1) = '.' AND instr(substr(i.file_name, @phraseLength + 2), '.') = 0))";
            var rows = ItemReader.QueryRows(connection, null, $"WHERE {where} ORDER BY {Exact} DESC, i.added_at DESC, i.id DESC LIMIT @limit", p);
            return rows.Count == 0 ? [] : ItemReader.Materialize(connection, null, rows, CategoryTree.Load(connection), _settings.VaultPath);
        }, ct);
}
