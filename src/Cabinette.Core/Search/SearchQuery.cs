using System.Text;
using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Dapper;

namespace Cabinette.Core.Search;

/// <summary>Search text split into free-text terms and "#tag" tokens.</summary>
internal sealed class ParsedSearch
{
    /// <summary>Minimum length (in Unicode code points) the trigram index can match.</summary>
    public const int TrigramMinLength = 3;

    private ParsedSearch(IReadOnlyList<string> terms, IReadOnlyList<string> tags)
    {
        Terms = terms;
        Tags = tags;
    }

    public IReadOnlyList<string> Terms { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool IsEmpty => Terms.Count == 0 && Tags.Count == 0;

    public static ParsedSearch Parse(string? text)
    {
        var terms = new List<string>();
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('#'))
                {
                    var tag = NormalizeTag(token);
                    if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(tag);
                    }
                }
                else
                {
                    terms.Add(token);
                }
            }
        }

        return new ParsedSearch(terms, tags);
    }

    /// <summary>Trims and removes leading '#'. Returns "" for an empty tag.</summary>
    public static string NormalizeTag(string? tag) => (tag ?? "").Trim().TrimStart('#').Trim();

    public static bool UsesTrigramIndex(string term) => term.EnumerateRunes().Count() >= TrigramMinLength;
}

/// <summary>Builds the WHERE clause for item queries (scope, text, tags, filter). Table alias for items is "i".</summary>
internal static class ItemQueryBuilder
{
    private static readonly string[] AllMappedExtensions =
        ExtensionMap.BuiltInCategories.SelectMany(d => d.Extensions).Distinct(StringComparer.Ordinal).ToArray();

    /// <param name="scopeCategoryIds">Category ids whose items are included (a subtree), or null for the whole library.</param>
    /// <param name="includeNoteText">True: free text matches file names and note text (thread search). False: file names only.</param>
    /// <param name="namePrefix">When set, the file name must also start with it (case-insensitive like the terms).</param>
    public static string BuildWhere(IReadOnlyCollection<long>? scopeCategoryIds, ParsedSearch search, bool includeNoteText,
        ItemFilter? filter, DynamicParameters p, string? namePrefix = null)
    {
        var where = new List<string>();

        if (scopeCategoryIds is not null)
        {
            p.Add("scope", DbValues.JsonArray(scopeCategoryIds));
            where.Add("i.id IN (SELECT ic.item_id FROM item_categories ic WHERE ic.category_id IN (SELECT value FROM json_each(@scope)))");
        }

        AddTextConditions(search, includeNoteText, namePrefix, where, p);

        for (var k = 0; k < search.Tags.Count; k++)
        {
            p.Add($"tag{k}", search.Tags[k]);
            where.Add($"i.id IN (SELECT it.item_id FROM item_tags it JOIN tags t ON t.id = it.tag_id WHERE t.name = @tag{k})");
        }

        if (filter is not null && !filter.IsEmpty)
        {
            AddFilterConditions(filter, where, p);
        }

        return where.Count == 0 ? "1 = 1" : string.Join(" AND ", where);
    }

    private static void AddTextConditions(ParsedSearch search, bool includeNoteText, string? namePrefix, List<string> where, DynamicParameters p)
    {
        var fts = new StringBuilder();
        var likeIndex = 0;
        if (!string.IsNullOrEmpty(namePrefix))
        {
            if (ParsedSearch.UsesTrigramIndex(namePrefix))
            {
                // An initial-token phrase: with the trigram tokenizer, the name's first trigrams are the prefix's.
                fts.Append("{file_name} : ^ ").Append(DbValues.FtsPhrase(namePrefix));
            }
            else
            {
                p.Add("namePrefix", DbValues.LikeStartsWith(namePrefix));
                where.Add("i.file_name LIKE @namePrefix ESCAPE '\\'");
            }
        }

        foreach (var term in search.Terms)
        {
            if (ParsedSearch.UsesTrigramIndex(term))
            {
                if (fts.Length > 0)
                {
                    fts.Append(" AND ");
                }

                if (!includeNoteText)
                {
                    fts.Append("{file_name} : ");
                }

                fts.Append(DbValues.FtsPhrase(term));
            }
            else
            {
                var name = $"like{likeIndex++}";
                p.Add(name, DbValues.LikeContains(term));
                where.Add(includeNoteText
                    ? $"(i.file_name LIKE @{name} ESCAPE '\\' OR (i.kind IN (1, 3) AND EXISTS (SELECT 1 FROM search_fts f WHERE f.rowid = i.id AND f.note_text LIKE @{name} ESCAPE '\\')))"
                    : $"i.file_name LIKE @{name} ESCAPE '\\'");
            }
        }

        if (fts.Length > 0)
        {
            p.Add("fts", fts.ToString());
            where.Add("i.id IN (SELECT rowid FROM search_fts WHERE search_fts MATCH @fts)");
        }
    }

    private static void AddFilterConditions(ItemFilter filter, List<string> where, DynamicParameters p)
    {
        if (filter.TypeKeys is { Count: > 0 } typeKeys)
        {
            var keys = typeKeys.Select(k => k.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var extensions = ExtensionMap.BuiltInCategories
                .Where(d => keys.Contains(TopLevelKey(d.Key)))
                .SelectMany(d => d.Extensions)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            p.Add("typeExts", DbValues.JsonArray(extensions));
            var byExtension = "i.extension IN (SELECT value FROM json_each(@typeExts))";
            if (keys.Contains(ExtensionMap.OtherKey))
            {
                p.Add("mappedExts", DbValues.JsonArray(AllMappedExtensions));
                byExtension = $"({byExtension} OR i.extension NOT IN (SELECT value FROM json_each(@mappedExts)))";
            }

            // Notes, pasted images and links made in Cabinette get their type from their kind (a note is a .md file, but not "text").
            var kinds = Enum.GetValues<ItemKind>()
                .Where(k => k != ItemKind.File && keys.Contains(ExtensionMap.GetTypeKey(k, null)))
                .Select(k => (long)k)
                .ToList();
            var clause = $"(i.kind = {(int)ItemKind.File} AND {byExtension})";
            if (kinds.Count > 0)
            {
                p.Add("typeKinds", DbValues.JsonArray(kinds));
                clause = $"({clause} OR i.kind IN (SELECT value FROM json_each(@typeKinds)))";
            }

            where.Add(clause);
        }

        if (filter.AddedFrom is { } from)
        {
            p.Add("addedFrom", DbValues.ToText(from));
            where.Add("i.added_at >= @addedFrom");
        }

        if (filter.AddedTo is { } to)
        {
            p.Add("addedTo", DbValues.ToText(to));
            where.Add("i.added_at <= @addedTo");
        }

        if (filter.MinSizeBytes is { } min)
        {
            p.Add("minSize", min);
            where.Add("i.size_bytes >= @minSize");
        }

        if (filter.MaxSizeBytes is { } max)
        {
            p.Add("maxSize", max);
            where.Add("i.size_bytes <= @maxSize");
        }

        if (filter.Mode is { } mode)
        {
            p.Add("mode", (int)mode);
            where.Add("i.mode = @mode");
        }

        if (filter.MissingOnly)
        {
            where.Add("i.is_missing = 1");
        }

        if (filter.Extensions is { Count: > 0 } exts)
        {
            p.Add("exts", DbValues.JsonArray(exts.Select(ExtensionMap.Normalize).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal)));
            where.Add("i.extension IN (SELECT value FROM json_each(@exts))");
        }
    }

    private static string TopLevelKey(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot];
    }
}
