using System.Data;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Dapper;

namespace Cabinette.Core.Data;

/// <summary>Builds <see cref="LibraryItem"/>s from item rows, loading memberships and tags in bulk (no N+1 queries).</summary>
internal static class ItemReader
{
    private const string MembershipsSql = """
        SELECT ic.item_id, ic.category_id, ic.added_at
        FROM item_categories ic
        WHERE ic.item_id IN (SELECT value FROM json_each(@ids))
        """;

    private const string TagsSql = """
        SELECT it.item_id, t.name
        FROM item_tags it JOIN tags t ON t.id = it.tag_id
        WHERE it.item_id IN (SELECT value FROM json_each(@ids))
        """;

    private const string StarsSql = """
        SELECT s.item_id, s.starred_at
        FROM item_stars s
        WHERE s.item_id IN (SELECT value FROM json_each(@ids))
        """;

    private const string DatesTakenSql = """
        SELECT d.item_id, d.taken_at
        FROM item_dates d
        WHERE d.item_id IN (SELECT value FROM json_each(@photoIds)) AND d.taken_at IS NOT NULL
        """;

    /// <summary>Item rows for <see cref="ItemRow.SelectSql"/> + <paramref name="whereAndOrder"/>. Hand-mapped: this is the hot path for big threads.</summary>
    public static List<ItemRow> QueryRows(IDbConnection connection, IDbTransaction? transaction, string whereAndOrder, object? param)
    {
        var result = new List<ItemRow>();
        using var reader = connection.ExecuteReader(ItemRow.SelectSql + " " + whereAndOrder, param, transaction);
        while (reader.Read())
        {
            result.Add(new ItemRow
            {
                Id = reader.GetInt64(0),
                Mode = reader.GetInt64(1),
                Kind = reader.GetInt64(2),
                FileName = reader.GetString(3),
                Extension = reader.GetString(4),
                PointerPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                HomeCategoryId = reader.GetInt64(6),
                SizeBytes = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Hash = reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8),
                FileModifiedAt = reader.IsDBNull(9) ? null : reader.GetString(9),
                AddedAt = reader.GetString(10),
                AddedBy = reader.GetInt64(11),
                BatchId = reader.IsDBNull(12) ? null : reader.GetString(12),
                OriginalPath = reader.IsDBNull(13) ? null : reader.GetString(13),
                IsMissing = reader.GetInt64(14),
                NotePreview = reader.IsDBNull(15) ? null : reader.GetString(15),
            });
        }

        return result;
    }

    public static ItemRow? LoadRow(IDbConnection connection, IDbTransaction? transaction, long itemId)
        => QueryRows(connection, transaction, "WHERE i.id = @itemId", new { itemId }).FirstOrDefault();

    public static LibraryItem? Load(IDbConnection connection, IDbTransaction? transaction, long itemId, CategoryTree tree, string? vaultPath)
    {
        var row = LoadRow(connection, transaction, itemId);
        return row is null ? null : Materialize(connection, transaction, [row], tree, vaultPath)[0];
    }

    public static List<LibraryItem> LoadMany(IDbConnection connection, IDbTransaction? transaction, IReadOnlyCollection<long> itemIds,
        CategoryTree tree, string? vaultPath)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var rows = QueryRows(connection, transaction,
            "WHERE i.id IN (SELECT value FROM json_each(@ids)) ORDER BY i.added_at, i.id",
            new { ids = DbValues.JsonArray(itemIds) });
        return Materialize(connection, transaction, rows, tree, vaultPath);
    }

    public static List<LibraryItem> Materialize(IDbConnection connection, IDbTransaction? transaction, List<ItemRow> rows,
        CategoryTree tree, string? vaultPath)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = DbValues.JsonArray(rows.Select(r => r.Id));

        // Memberships: most items have exactly one, so collect unsorted and only sort the few with several (oldest first).
        var memberships = new Dictionary<long, List<(string AddedAt, long CategoryId)>>(rows.Count);
        using (var reader = connection.ExecuteReader(MembershipsSql, new { ids }, transaction))
        {
            while (reader.Read())
            {
                var itemId = reader.GetInt64(0);
                if (!memberships.TryGetValue(itemId, out var list))
                {
                    memberships[itemId] = list = new List<(string, long)>(1);
                }

                list.Add((reader.GetString(2), reader.GetInt64(1)));
            }
        }

        var tags = new Dictionary<long, List<string>>();
        using (var reader = connection.ExecuteReader(TagsSql, new { ids }, transaction))
        {
            while (reader.Read())
            {
                var itemId = reader.GetInt64(0);
                if (!tags.TryGetValue(itemId, out var list))
                {
                    tags[itemId] = list = new List<string>(2);
                }

                list.Add(reader.GetString(1));
            }
        }

        Dictionary<long, string>? stars = null;
        using (var reader = connection.ExecuteReader(StarsSql, new { ids }, transaction))
        {
            while (reader.Read())
            {
                (stars ??= [])[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        // Dates taken: only photos and videos have one (a file renamed to another type keeps its row but shows none), so other
        // threads don't even ask.
        Dictionary<long, string>? datesTaken = null;
        var photoIds = rows.Where(r => DateTakenTypes.Applies(r.ItemKind, r.Extension)).Select(r => r.Id).ToList();
        if (photoIds.Count > 0)
        {
            using var reader = connection.ExecuteReader(DatesTakenSql, new { photoIds = DbValues.JsonArray(photoIds) }, transaction);
            while (reader.Read())
            {
                (datesTaken ??= [])[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        var folderCache = new Dictionary<long, string>();
        var result = new List<LibraryItem>(rows.Count);
        foreach (var row in rows)
        {
            long[] categoryIds;
            if (memberships.TryGetValue(row.Id, out var cats))
            {
                if (cats.Count > 1)
                {
                    cats.Sort(static (a, b) =>
                    {
                        var byTime = string.CompareOrdinal(a.AddedAt, b.AddedAt);
                        return byTime != 0 ? byTime : a.CategoryId.CompareTo(b.CategoryId);
                    });
                }

                categoryIds = new long[cats.Count];
                for (var i = 0; i < cats.Count; i++)
                {
                    categoryIds[i] = cats[i].CategoryId;
                }
            }
            else
            {
                categoryIds = [];
            }

            if (Array.IndexOf(categoryIds, row.HomeCategoryId) < 0)
            {
                categoryIds = [row.HomeCategoryId, .. categoryIds];
            }

            IReadOnlyList<string> itemTags = [];
            if (tags.TryGetValue(row.Id, out var tagList))
            {
                tagList.Sort(StringComparer.CurrentCultureIgnoreCase);
                itemTags = tagList;
            }

            var starredAt = stars is not null && stars.TryGetValue(row.Id, out var star) ? DbValues.ParseTime(star) : (DateTimeOffset?)null;
            var takenAt = datesTaken is not null && datesTaken.TryGetValue(row.Id, out var taken) ? DbValues.ParseTimeWithOffset(taken) : (DateTimeOffset?)null;
            result.Add(ToItem(row, ResolvePath(row, tree, vaultPath, folderCache), categoryIds, itemTags, starredAt, takenAt));
        }

        return result;
    }

    public static string ResolvePath(ItemRow row, CategoryTree tree, string? vaultPath, Dictionary<long, string>? folderCache = null)
    {
        if (row.ItemMode == ItemMode.Pointer)
        {
            return row.PointerPath ?? "";
        }

        if (folderCache is null || !folderCache.TryGetValue(row.HomeCategoryId, out var folder))
        {
            folder = tree.Find(row.HomeCategoryId) is null ? vaultPath ?? "" : tree.FolderPath(vaultPath ?? "", row.HomeCategoryId);
            if (folderCache is not null)
            {
                folderCache[row.HomeCategoryId] = folder;
            }
        }

        return Path.Combine(folder, row.FileName);
    }

    private static LibraryItem ToItem(ItemRow row, string fullPath, IReadOnlyList<long> categoryIds, IReadOnlyList<string> tags,
        DateTimeOffset? starredAt, DateTimeOffset? takenAt) => new()
    {
        StarredAt = starredAt,
        TakenAt = takenAt,
        Id = row.Id,
        Mode = row.ItemMode,
        Kind = row.ItemKind,
        FileName = row.FileName,
        Extension = row.Extension,
        FullPath = fullPath,
        HomeCategoryId = row.HomeCategoryId,
        CategoryIds = categoryIds,
        Tags = tags,
        SizeBytes = row.SizeBytes,
        Hash = row.Hash,
        FileModifiedAt = DbValues.ParseTimeOrNull(row.FileModifiedAt),
        AddedAt = DbValues.ParseTime(row.AddedAt),
        AddedBy = (AddedBy)row.AddedBy,
        BatchId = row.BatchId,
        OriginalPath = row.OriginalPath,
        IsMissing = row.IsMissing != 0,
        NotePreview = row.NotePreview,
        TypeKey = ExtensionMap.GetTypeKey(row.ItemKind, row.Extension),
    };
}
