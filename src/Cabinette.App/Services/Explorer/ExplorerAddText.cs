using System.Globalization;
using System.IO;
using Cabinette.App.Localization;
using Cabinette.App.Services.Monitor;
using Cabinette.Core.Models;

namespace Cabinette.App.Services.Explorer;

/// <summary>The Windows toast for one Explorer batch.</summary>
/// <param name="Title">The entry that was used ("Copy to Cabinette" / "Add to Cabinette (Pointer)").</param>
/// <param name="Lines">"3 files added to Images", duplicates, failures (one line each).</param>
/// <param name="CategoryId">The one category all added files went to (clicking opens it), else null.</param>
public sealed record ExplorerAddMessage(string Title, IReadOnlyList<string> Lines, long? CategoryId)
{
    public string Body => string.Join("\n", Lines);
}

/// <summary>
/// Texts for an Explorer batch's toast, grouped like the vault monitor's ("3 files added to Images", "New category:
/// Holiday"), plus "already in Cabinette" and "couldn't add" lines.
/// </summary>
public static class ExplorerAddText
{
    public static string Title(ItemMode mode) => L.Get(mode == ItemMode.Pointer ? "Explorer_PointerEntry" : "Explorer_CopyEntry");

    /// <param name="categoryName">Category id → name ("" when unknown).</param>
    /// <param name="includeSuccess">
    /// False when "Notifications when the vault changes" is off: then only failures are reported, and null is returned
    /// when nothing failed.
    /// </param>
    public static ExplorerAddMessage? Build(ItemMode mode, AddResult result, Func<long, string> categoryName, bool includeSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>();
        if (includeSuccess)
        {
            AddAddedLines(lines, result, categoryName);
            AddDuplicateLines(lines, result, categoryName);
        }

        AddFailureLines(lines, result);
        if (lines.Count == 0)
        {
            if (!includeSuccess)
            {
                return null;
            }

            lines.Add(L.Get("Intake_NothingAdded"));
        }

        return new ExplorerAddMessage(Title(mode), lines, includeSuccess ? SingleCategory(result) : null);
    }

    /// <summary>The toast when the batch failed as a whole (for example the vault can't be reached).</summary>
    public static ExplorerAddMessage Failed(ItemMode mode, string message) => new(Title(mode), [message], null);

    /// <summary>The home category shared by every added file, else null.</summary>
    public static long? SingleCategory(AddResult result)
    {
        if (result.Added.Count == 0)
        {
            return null;
        }

        var first = result.Added[0].HomeCategoryId;
        return result.Added.All(i => i.HomeCategoryId == first) ? first : null;
    }

    private static void AddAddedLines(List<string> lines, AddResult result, Func<long, string> categoryName)
    {
        var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        var unnamed = 0;
        foreach (var item in result.Added)
        {
            var name = categoryName(item.HomeCategoryId);
            if (string.IsNullOrEmpty(name))
            {
                unnamed++;
            }
            else
            {
                byCategory[name] = byCategory.GetValueOrDefault(name) + 1;
            }
        }

        lines.AddRange(VaultChangeText.Lines(new VaultChangeSummary { AddedByCategory = byCategory }));
        if (unnamed > 0)
        {
            lines.Add(unnamed == 1 ? L.Get("Intake_AddedOne") : L.Format("Intake_Added", N(unnamed)));
        }

        var created = result.CreatedCategoryIds
            .Select(categoryName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        lines.AddRange(VaultChangeText.Lines(new VaultChangeSummary { CategoriesCreated = created }));
    }

    private static void AddDuplicateLines(List<string> lines, AddResult result, Func<long, string> categoryName)
    {
        if (result.Duplicates.Count == 1)
        {
            var existing = result.Duplicates[0].Existing;
            lines.Add(L.Format("Intake_Duplicate", existing.FileName, categoryName(existing.HomeCategoryId)));
        }
        else if (result.Duplicates.Count > 1)
        {
            lines.Add(L.Format("Intake_DuplicatesMany", N(result.Duplicates.Count)));
        }
    }

    private static void AddFailureLines(List<string> lines, AddResult result)
    {
        if (result.Failures.Count == 1)
        {
            var failure = result.Failures[0];
            lines.Add(L.Format("Intake_Failed", Path.GetFileName(failure.SourcePath.TrimEnd('\\', '/')), failure.Message));
        }
        else if (result.Failures.Count > 1)
        {
            lines.Add(L.Format("Intake_FailedMany", N(result.Failures.Count)));
        }
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
