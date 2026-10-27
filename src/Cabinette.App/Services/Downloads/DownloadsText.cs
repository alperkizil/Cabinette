using System.Globalization;
using System.IO;
using Cabinette.App.Localization;
using Cabinette.Core.Downloads;

namespace Cabinette.App.Services.Downloads;

/// <summary>One grouped notification about Clean My Downloads mode.</summary>
/// <param name="Lines">"3 files from Downloads added to Images", files already in Cabinette, files that stay, failures.</param>
/// <param name="CategoryId">The one category every moved file went to (clicking opens it), else null.</param>
/// <param name="HasProblems">Some files stay in Downloads or couldn't be moved.</param>
public sealed record DownloadsMessage(string Title, IReadOnlyList<string> Lines, long? CategoryId, bool HasProblems)
{
    /// <summary>Body for the Windows toast (one line each).</summary>
    public string Body => string.Join("\n", Lines);

    /// <summary>Single line for the in-app notice.</summary>
    public string OneLine => string.Join(" · ", Lines);
}

/// <summary>
/// Texts for what Clean My Downloads mode did, grouped like the vault monitor's notifications. Files that stay in Downloads
/// and failures are always reported; the rest only while "Notifications when the vault changes" is on.
/// </summary>
public static class DownloadsText
{
    /// <param name="categoryName">Category id → name ("" when unknown).</param>
    /// <param name="includeSuccess">False when notifications are off: then only files that stay or failed are reported.</param>
    /// <returns>The message, or null when there is nothing to say.</returns>
    public static DownloadsMessage? Build(IReadOnlyList<DownloadOutcome> outcomes, Func<long, string> categoryName, bool includeSuccess)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(categoryName);
        var moved = outcomes.Where(o => o.Kind == DownloadOutcomeKind.Moved && o.Item is not null).ToList();
        var already = outcomes.Where(o => o.Kind == DownloadOutcomeKind.AlreadyInLibrary && o.Item is not null).ToList();
        var kept = outcomes.Where(o => o.Kind == DownloadOutcomeKind.Kept).ToList();
        var failed = outcomes.Where(o => o.Kind == DownloadOutcomeKind.Failed).ToList();

        var lines = new List<string>();
        if (includeSuccess)
        {
            AddMovedLines(lines, moved, categoryName);
            AddAlreadyLines(lines, already, categoryName);
        }

        AddKeptLines(lines, kept);
        AddFailedLines(lines, failed);
        if (lines.Count == 0)
        {
            return null;
        }

        long? single = null;
        if (includeSuccess && moved.Count > 0 && moved.All(o => o.Item!.HomeCategoryId == moved[0].Item!.HomeCategoryId))
        {
            single = moved[0].Item!.HomeCategoryId;
        }

        return new DownloadsMessage(L.Get("Downloads_Title"), lines, single, kept.Count > 0 || failed.Count > 0);
    }

    /// <summary>"3 files from Downloads added to Images", "2 files from Downloads added to Images and Audio".</summary>
    private static void AddMovedLines(List<string> lines, List<DownloadOutcome> moved, Func<long, string> categoryName)
    {
        var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        var unnamed = 0;
        foreach (var outcome in moved)
        {
            var name = categoryName(outcome.Item!.HomeCategoryId);
            if (string.IsNullOrEmpty(name))
            {
                unnamed++;
            }
            else
            {
                byCategory[name] = byCategory.GetValueOrDefault(name) + 1;
            }
        }

        var named = byCategory
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (named.Count > 0)
        {
            var total = named.Sum(kv => kv.Value);
            var where = named.Count switch
            {
                1 => named[0].Key,
                2 => L.Format("VaultNotify_TwoNames", named[0].Key, named[1].Key),
                _ => L.Format("VaultNotify_NamesAndMore", named[0].Key, named[1].Key, N(named.Count - 2)),
            };
            lines.Add(total == 1 ? L.Format("Downloads_AddedOne", where) : L.Format("Downloads_Added", N(total), where));
        }

        if (unnamed > 0)
        {
            lines.Add(unnamed == 1 ? L.Get("Downloads_AddedOneNoCategory") : L.Format("Downloads_AddedNoCategory", N(unnamed)));
        }
    }

    private static void AddAlreadyLines(List<string> lines, List<DownloadOutcome> already, Func<long, string> categoryName)
    {
        if (already.Count == 1)
        {
            var item = already[0].Item!;
            lines.Add(L.Format("Downloads_AlreadyOne", FileName(already[0]), categoryName(item.HomeCategoryId)));
        }
        else if (already.Count > 1)
        {
            lines.Add(L.Format("Downloads_AlreadyMany", N(already.Count)));
        }
    }

    private static void AddKeptLines(List<string> lines, List<DownloadOutcome> kept)
    {
        if (kept.Count == 1)
        {
            var reason = kept[0].Reason switch
            {
                DownloadKeptReason.LinkedAsPointer => L.Get("Downloads_ReasonPointer"),
                DownloadKeptReason.RecycleFailed => L.Get("Downloads_ReasonRecycleFailed"),
                DownloadKeptReason.VaultCopyUnchecked => L.Get("Downloads_ReasonVaultCopyUnchecked"),
                _ => L.Get("Downloads_ReasonNoRecycleBin"),
            };
            lines.Add(L.Format("Downloads_KeptOne", FileName(kept[0]), reason));
        }
        else if (kept.Count > 1)
        {
            lines.Add(kept.All(o => o.Reason == DownloadKeptReason.NoRecycleBin)
                ? L.Format("Downloads_KeptManyNoRecycleBin", N(kept.Count))
                : L.Format("Downloads_KeptMany", N(kept.Count)));
        }
    }

    private static void AddFailedLines(List<string> lines, List<DownloadOutcome> failed)
    {
        if (failed.Count == 1)
        {
            lines.Add(L.Format("Downloads_FailedOne", FileName(failed[0]), failed[0].Message ?? ""));
        }
        else if (failed.Count > 1)
        {
            lines.Add(L.Format("Downloads_FailedMany", N(failed.Count)));
        }
    }

    /// <summary>The name the file had in the Downloads folder.</summary>
    private static string FileName(DownloadOutcome outcome) => Path.GetFileName(outcome.SourcePath);

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
