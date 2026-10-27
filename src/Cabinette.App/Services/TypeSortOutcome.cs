using Cabinette.Core.Models;

namespace Cabinette.App.Services;

/// <summary>A type category that received sorted files, and how many.</summary>
public sealed record SortedDestination(long CategoryId, int Count);

/// <summary>
/// What the UI does after an add into a type category sorted files into their own type categories (SPEC.md section 5,
/// "Type categories sort by type").
/// </summary>
/// <param name="OpenCategoryId">
/// Set when every file that arrived went to this one category and the target's thread doesn't show it: that thread opens.
/// Null: stay, and one notice lists <see cref="Destinations"/>.
/// </param>
/// <param name="Destinations">Where the sorted files went, most files first.</param>
/// <param name="Stayed">Files that arrived in the target itself (or a folder added there).</param>
public sealed record TypeSortOutcome(long? OpenCategoryId, IReadOnlyList<SortedDestination> Destinations, int Stayed)
{
    /// <param name="targetSubtree">The target and its subcategories. A file sorted into one of these still shows in the thread.</param>
    public static TypeSortOutcome From(AddResult result, IReadOnlySet<long> targetSubtree)
    {
        var destinations = result.SortedByType
            .GroupBy(s => s.CategoryId)
            .Select(g => new SortedDestination(g.Key, g.Count()))
            .OrderByDescending(d => d.Count)
            .ToList();

        // Every file that arrived is in Added (new or found again) or Duplicates exactly once.
        var stayed = Math.Max(0, result.Added.Count + result.Duplicates.Count - result.SortedByType.Count);
        long? open = stayed == 0 && destinations.Count == 1 && !targetSubtree.Contains(destinations[0].CategoryId)
            ? destinations[0].CategoryId
            : null;
        return new TypeSortOutcome(open, destinations, stayed);
    }
}
