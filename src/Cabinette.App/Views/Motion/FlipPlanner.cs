namespace Cabinette.App.Views.Motion;

/// <summary>
/// One row of a list that is shown on screen after a reorder. Tops are relative to the top of the visible area.
/// <paramref name="OldTop"/> is null when the row had no container before (it was scrolled away or virtualized);
/// <paramref name="OldIndex"/> is -1 for a row that is new.
/// </summary>
internal readonly record struct FlipRow<TKey>(TKey Key, int OldIndex, int NewIndex, double? OldTop, double NewTop, double Height);

/// <summary>
/// A row to animate: it is laid out at its new place already and starts drawn <paramref name="Offset"/> pixels away
/// (where it was), then glides to 0. Jumpers are the rows that changed order; the others only make room.
/// </summary>
internal readonly record struct FlipMove<TKey>(TKey Key, double Offset, bool IsJumper);

/// <summary>
/// The pure part of the chat list's FLIP animation ("First, Last, Invert, Play"): record where rows were, let the list
/// reorder, then start each moved row at its old place and glide it to the new one.
/// </summary>
internal static class FlipPlanner
{
    /// <summary>Smaller moves than this are not animated.</summary>
    public const double MinOffset = 0.5;

    /// <summary>
    /// Offsets for the rows that moved and are on screen before or after the change. A row that wasn't realized
    /// before enters from the edge of the visible area it came from, and no row starts further away than just
    /// outside the visible area, so even a long jump stays short. New rows are left to the caller (they fade in).
    /// </summary>
    public static List<FlipMove<TKey>> Plan<TKey>(IEnumerable<FlipRow<TKey>> rows, double viewportHeight, IReadOnlySet<TKey> jumpers)
    {
        var moves = new List<FlipMove<TKey>>();
        foreach (var row in rows)
        {
            if (row.OldIndex < 0)
            {
                continue;
            }

            double offset;
            if (row.OldTop is { } oldTop)
            {
                offset = oldTop - row.NewTop;
            }
            else if (row.OldIndex == row.NewIndex)
            {
                continue;
            }
            else if (row.OldIndex > row.NewIndex)
            {
                offset = viewportHeight - row.NewTop; // came up from below the visible area
            }
            else
            {
                offset = -(row.NewTop + row.Height); // came down from above it
            }

            // Start no further away than just outside the visible area.
            offset = Math.Clamp(offset, -(row.NewTop + row.Height), Math.Max(viewportHeight - row.NewTop, -(row.NewTop + row.Height)));
            if (Math.Abs(offset) < MinOffset)
            {
                continue;
            }

            var start = row.NewTop + offset;
            var visibleBefore = start < viewportHeight && start + row.Height > 0;
            var visibleAfter = row.NewTop < viewportHeight && row.NewTop + row.Height > 0;
            if (visibleBefore || visibleAfter)
            {
                moves.Add(new FlipMove<TKey>(row.Key, offset, jumpers.Contains(row.Key)));
            }
        }

        return moves;
    }

    /// <summary>
    /// The rows that changed their order relative to the others: everything outside a longest run that kept its
    /// order. One category moving to the top is one jumper (the rows it passed only shift down); re-sorting the
    /// whole list makes many. On a tie, the row that moved up counts as the jumper. Rows only in one list are ignored.
    /// </summary>
    public static HashSet<TKey> FindJumpers<TKey>(IReadOnlyList<TKey> before, IReadOnlyList<TKey> after)
        where TKey : notnull
    {
        var oldIndex = new Dictionary<TKey, int>(before.Count);
        for (var i = 0; i < before.Count; i++)
        {
            oldIndex.TryAdd(before[i], i);
        }

        var common = after.Where(oldIndex.ContainsKey).Distinct().ToList();
        var sequence = common.Select(k => oldIndex[k]).ToList();

        // Longest increasing subsequence (patience sorting); it prefers smaller old indexes, i.e. rows that stayed put.
        var tails = new List<int>(); // tails[length - 1] = position in sequence ending the best run of that length
        var previous = new int[sequence.Count];
        for (var i = 0; i < sequence.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (sequence[tails[mid]] < sequence[i])
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[lo] = i;
            }
        }

        var kept = new HashSet<int>();
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
        {
            kept.Add(i);
        }

        var jumpers = new HashSet<TKey>();
        for (var i = 0; i < common.Count; i++)
        {
            if (!kept.Contains(i))
            {
                jumpers.Add(common[i]);
            }
        }

        return jumpers;
    }
}
