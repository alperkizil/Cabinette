using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Search;
using Dapper;

namespace Cabinette.Core.Services;

internal sealed partial class LibraryService
{
    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<TagInfo>>(connection =>
            connection.Query<TagCountRow>(
                    "SELECT t.name AS Name, COUNT(it.item_id) AS ItemCount FROM tags t LEFT JOIN item_tags it ON it.tag_id = t.id GROUP BY t.id")
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new TagInfo(t.Name, (int)t.ItemCount))
                .ToList(), ct);

    public Task SetItemTagsAsync(long itemId, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var names = NormalizeTags(tags);
        return WriteAsync(ctx =>
        {
            if (ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId) is null)
            {
                throw new LibraryException(LibraryErrorCode.NotFound, "The item no longer exists.");
            }

            ctx.Execute("DELETE FROM item_tags WHERE item_id = @itemId", new { itemId });
            foreach (var name in names)
            {
                AttachTag(ctx, itemId, name);
            }

            DeleteUnusedTags(ctx);
            ctx.Changes.Kind |= LibraryChangeKind.Tags;
            ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, itemId));
        }, ct);
    }

    public Task AddTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(tags);
        var names = NormalizeTags(tags);
        return WriteAsync(ctx =>
        {
            foreach (var itemId in itemIds.Distinct())
            {
                if (ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId) is null)
                {
                    continue;
                }

                var changed = false;
                foreach (var name in names)
                {
                    changed |= AttachTag(ctx, itemId, name);
                }

                if (changed)
                {
                    ctx.Changes.Kind |= LibraryChangeKind.Tags;
                    ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, itemId));
                }
            }
        }, ct);
    }

    public Task RemoveTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(tags);
        var names = NormalizeTags(tags);
        return WriteAsync(ctx =>
        {
            var namesJson = DbValues.JsonArray(names);
            foreach (var itemId in itemIds.Distinct())
            {
                // tags.name is COLLATE NOCASE, so the IN comparison is case-insensitive.
                var removed = ctx.Execute(
                    """
                    DELETE FROM item_tags
                    WHERE item_id = @itemId AND tag_id IN (SELECT id FROM tags WHERE name IN (SELECT value FROM json_each(@namesJson)))
                    """, new { itemId, namesJson });
                if (removed > 0)
                {
                    ctx.Changes.Kind |= LibraryChangeKind.Tags;
                    ctx.Changes.Item(itemId, ctx.Tree, MembershipsOf(ctx, itemId));
                }
            }

            DeleteUnusedTags(ctx);
        }, ct);
    }

    /// <summary>Trimmed, without leading '#', non-empty, unique ignoring case (first spelling wins).</summary>
    internal static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var name = ParsedSearch.NormalizeTag(tag);
            if (name.Length > 0 && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>Creates the tag if needed and attaches it. Returns true when the item did not have it yet.</summary>
    private static bool AttachTag(WriteContext ctx, long itemId, string name)
    {
        ctx.Execute("INSERT OR IGNORE INTO tags(name) VALUES (@name)", new { name });
        var tagId = ctx.Scalar<long>("SELECT id FROM tags WHERE name = @name", new { name });
        return ctx.Execute("INSERT OR IGNORE INTO item_tags(item_id, tag_id) VALUES (@itemId, @tagId)", new { itemId, tagId }) > 0;
    }
}
