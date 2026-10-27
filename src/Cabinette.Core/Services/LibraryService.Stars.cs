using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Search;
using Dapper;

namespace Cabinette.Core.Services;

// Starred files (SPEC.md section 4). A star is a row in item_stars; the item keeps it through every change that keeps its id.
internal sealed partial class LibraryService
{
    public Task SetStarredAsync(IReadOnlyList<long> itemIds, bool starred, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return WriteAsync(ctx =>
        {
            var ids = DbValues.JsonArray(itemIds.Distinct());
            var changed = starred
                ? ctx.Query<long>(
                    """
                    INSERT INTO item_stars(item_id, starred_at)
                    SELECT i.id, @now FROM items i
                    WHERE i.id IN (SELECT value FROM json_each(@ids)) AND i.id NOT IN (SELECT item_id FROM item_stars)
                    RETURNING item_id
                    """,
                    new { ids, now = ctx.NowText })
                : ctx.Query<long>("DELETE FROM item_stars WHERE item_id IN (SELECT value FROM json_each(@ids)) RETURNING item_id", new { ids });
            if (changed.Count == 0)
            {
                return;
            }

            // Only the items changed: threads showing them redraw their bubbles, the Starred view and its row reload.
            ctx.Changes.Kind |= LibraryChangeKind.Starred;
            foreach (var id in changed)
            {
                ctx.Changes.Item(id);
            }
        }, ct);
    }

    public Task<IReadOnlyList<LibraryItem>> GetStarredItemsAsync(string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<LibraryItem>>(connection =>
        {
            var p = new DynamicParameters();
            var where = ItemQueryBuilder.BuildWhere(null, ParsedSearch.Parse(searchText), includeNoteText: true, filter, p);

            // Files starred together (one selection) keep their chat order among themselves.
            var rows = ItemReader.QueryRows(connection, null,
                $"JOIN item_stars s ON s.item_id = i.id WHERE {where} ORDER BY s.starred_at, i.added_at, i.id", p);
            return ItemReader.Materialize(connection, null, rows, CategoryTree.Load(connection), _settings.VaultPath);
        }, ct);

    public Task<StarredSummary> GetStarredSummaryAsync(CancellationToken ct = default)
        => ReadAsync(connection =>
        {
            var (count, last) = connection.QuerySingle<(long Count, string? Last)>(
                "SELECT COUNT(*), MAX(s.starred_at) FROM item_stars s JOIN items i ON i.id = s.item_id");
            return new StarredSummary((int)count, DbValues.ParseTimeOrNull(last));
        }, ct);

    /// <summary>
    /// Call before deleting item rows: when one of them is starred, the change also says <see cref="LibraryChangeKind.Starred"/>
    /// (the star goes with the row, through the foreign key).
    /// </summary>
    private static void NoteStarsLeaving(WriteContext ctx, IEnumerable<long> itemIds)
    {
        if ((ctx.Changes.Kind & LibraryChangeKind.Starred) != 0)
        {
            return;
        }

        var starred = ctx.Scalar<long>("SELECT COUNT(*) FROM item_stars WHERE item_id IN (SELECT value FROM json_each(@ids))",
            new { ids = DbValues.JsonArray(itemIds) });
        if (starred > 0)
        {
            ctx.Changes.Kind |= LibraryChangeKind.Starred;
        }
    }
}
