using System.Globalization;
using Cabinette.App.Localization;
using Cabinette.Core.Models;

namespace Cabinette.App.Services.Monitor;

/// <summary>One grouped notification about vault changes.</summary>
/// <param name="Title">Notification title ("Changes in your vault").</param>
/// <param name="Lines">One line per kind of change ("3 files added to Images", "1 duplicate kept").</param>
/// <param name="CategoryName">The only category involved, when exactly one was (clicking opens it).</param>
public sealed record VaultChangeMessage(string Title, IReadOnlyList<string> Lines, string? CategoryName)
{
    /// <summary>Body for the tray notification (one line each).</summary>
    public string Body => string.Join("\n", Lines);

    /// <summary>Single line for the in-app snackbar.</summary>
    public string OneLine => string.Join(" · ", Lines);
}

/// <summary>Builds the grouped notification texts for <see cref="VaultChangeSummary"/> batches and merges batches.</summary>
public static class VaultChangeText
{
    /// <summary>Batches arriving within this window are merged into one notification.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);

    /// <summary>A notification goes out this long after the last batch if nothing else arrives.</summary>
    public static readonly TimeSpan QuietDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>When to show the pending notification: shortly after the last batch, but at most <see cref="MergeWindow"/> after the first.</summary>
    public static DateTimeOffset FlushAt(DateTimeOffset firstBatchAt, DateTimeOffset lastBatchAt)
    {
        var quiet = lastBatchAt + QuietDelay;
        var latest = firstBatchAt + MergeWindow;
        return quiet < latest ? quiet : latest;
    }

    public static VaultChangeSummary Merge(VaultChangeSummary first, VaultChangeSummary second)
    {
        var added = new Dictionary<string, int>(first.AddedByCategory, StringComparer.Ordinal);
        foreach (var (category, count) in second.AddedByCategory)
        {
            added[category] = added.GetValueOrDefault(category) + count;
        }

        return new VaultChangeSummary
        {
            AddedByCategory = added,
            Removed = first.Removed + second.Removed,
            Renamed = first.Renamed + second.Renamed,
            Moved = first.Moved + second.Moved,
            Modified = first.Modified + second.Modified,
            CategoriesCreated = first.CategoriesCreated.Concat(second.CategoriesCreated).Distinct(StringComparer.Ordinal).ToList(),
            CategoriesRemoved = first.CategoriesRemoved.Concat(second.CategoriesRemoved).Distinct(StringComparer.Ordinal).ToList(),
            Duplicates = first.Duplicates.Concat(second.Duplicates).ToList(),
        };
    }

    /// <summary>
    /// Saving a file that is already in the vault only updates it; that alone is not worth a notification.
    /// Everything else (added, removed, renamed, moved, categories, duplicates) is.
    /// </summary>
    public static bool IsWorthNotifying(VaultChangeSummary summary)
        => !summary.IsEmpty && (summary.AddedByCategory.Values.Any(v => v > 0) || summary.Removed > 0 || summary.Renamed > 0 ||
                                summary.Moved > 0 || summary.CategoriesCreated.Count > 0 || summary.CategoriesRemoved.Count > 0 ||
                                summary.Duplicates.Count > 0);

    /// <summary>The notification for a (merged) batch, or null when it is not worth one.</summary>
    public static VaultChangeMessage? Build(VaultChangeSummary summary)
    {
        if (!IsWorthNotifying(summary))
        {
            return null;
        }

        var lines = Lines(summary);
        return lines.Count == 0 ? null : new VaultChangeMessage(L.Get("VaultNotify_Title"), lines, SingleCategory(summary));
    }

    public static IReadOnlyList<string> Lines(VaultChangeSummary summary)
    {
        var lines = new List<string>();

        var added = summary.AddedByCategory
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (added.Count > 0)
        {
            var total = added.Sum(kv => kv.Value);
            var where = added.Count switch
            {
                1 => added[0].Key,
                2 => L.Format("VaultNotify_TwoNames", added[0].Key, added[1].Key),
                _ => L.Format("VaultNotify_NamesAndMore", added[0].Key, added[1].Key, N(added.Count - 2)),
            };
            lines.Add(total == 1 ? L.Format("VaultNotify_AddedOne", where) : L.Format("VaultNotify_Added", N(total), where));
        }

        switch (summary.CategoriesCreated.Count)
        {
            case 0:
                break;
            case 1:
                lines.Add(L.Format("VaultNotify_NewCategory", summary.CategoriesCreated[0]));
                break;
            case 2:
                lines.Add(L.Format("VaultNotify_NewCategoriesTwo", L.Format("VaultNotify_TwoNames", summary.CategoriesCreated[0], summary.CategoriesCreated[1])));
                break;
            default:
                lines.Add(L.Format("VaultNotify_NewCategoriesMany", N(summary.CategoriesCreated.Count)));
                break;
        }

        AddCount(lines, summary.Moved, "VaultNotify_MovedOne", "VaultNotify_Moved");
        AddCount(lines, summary.Renamed, "VaultNotify_RenamedOne", "VaultNotify_Renamed");
        AddCount(lines, summary.Removed, "VaultNotify_RemovedOne", "VaultNotify_Removed");

        switch (summary.CategoriesRemoved.Count)
        {
            case 0:
                break;
            case 1:
                lines.Add(L.Format("VaultNotify_CategoryRemoved", summary.CategoriesRemoved[0]));
                break;
            default:
                lines.Add(L.Format("VaultNotify_CategoriesRemoved", N(summary.CategoriesRemoved.Count)));
                break;
        }

        AddCount(lines, summary.Modified, "VaultNotify_UpdatedOne", "VaultNotify_Updated");
        AddCount(lines, summary.Duplicates.Count, "VaultNotify_DuplicateOne", "VaultNotify_Duplicates");
        return lines;
    }

    /// <summary>The one category the batch touched (files added to it and/or it was created), else null.</summary>
    public static string? SingleCategory(VaultChangeSummary summary)
    {
        var names = summary.AddedByCategory.Where(kv => kv.Value > 0).Select(kv => kv.Key)
            .Concat(summary.CategoriesCreated)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return names.Count == 1 ? names[0] : null;
    }

    private static void AddCount(List<string> lines, int count, string oneKey, string manyKey)
    {
        if (count == 1)
        {
            lines.Add(L.Get(oneKey));
        }
        else if (count > 1)
        {
            lines.Add(L.Format(manyKey, N(count)));
        }
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
