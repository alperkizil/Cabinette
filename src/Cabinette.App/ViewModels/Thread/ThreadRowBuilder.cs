using Cabinette.Core.Categorization;
using Cabinette.Core.Models;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>Turns a thread's items (oldest first) into flattened chat rows. Pure and thread-safe: runs on a background thread.</summary>
internal static class ThreadRowBuilder
{
    internal sealed record Result(
        List<ThreadRow> Rows,
        List<ThreadItemViewModel> Items,
        Dictionary<long, ThreadItemViewModel> ById,
        List<(ThreadItemViewModel Vm, LibraryItem Item)> Updates,
        List<AlbumRow> Albums);

    /// <param name="byStarredTime">
    /// The Starred view: <paramref name="items"/> are in star order, date headers follow when they were starred, and every file
    /// gets a bubble of its own (no albums: files of one batch are starred one by one).
    /// </param>
    public static Result Build(IReadOnlyList<LibraryItem> items, IReadOnlyDictionary<long, ThreadItemViewModel> existing, ThreadViewModel owner, DateTime today,
        bool byStarredTime = false)
    {
        var rows = new List<ThreadRow>(items.Count + 32);
        var vms = new List<ThreadItemViewModel>(items.Count);
        var byId = new Dictionary<long, ThreadItemViewModel>(items.Count);
        var updates = new List<(ThreadItemViewModel, LibraryItem)>();
        var albums = new List<AlbumRow>();

        ThreadItemViewModel GetVm(LibraryItem item)
        {
            if (existing.TryGetValue(item.Id, out var vm))
            {
                if (!ThreadItemViewModel.SameContent(vm.Item, item))
                {
                    updates.Add((vm, item));
                }
            }
            else
            {
                vm = new ThreadItemViewModel(item, owner);
            }

            if (byId.TryAdd(item.Id, vm))
            {
                vms.Add(vm);
            }

            return vm;
        }

        DateTime? lastDate = null;
        bool? lastSide = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (byId.ContainsKey(item.Id))
            {
                continue; // defensive: a duplicate id would break keys
            }

            var date = (byStarredTime && item.StarredAt is { } starred ? starred : item.AddedAt).ToLocalTime().Date;
            if (date != lastDate)
            {
                rows.Add(new DateHeaderRow(date, ThreadFormat.DateHeader(date, today)));
                lastDate = date;
                lastSide = null;
            }

            var isPointer = item.Mode == ItemMode.Pointer;
            var end = byStarredTime ? i + 1 : AlbumEnd(items, i, date);
            BubbleRow row;
            if (end - i >= 2)
            {
                var albumItems = new List<ThreadItemViewModel>(end - i);
                for (var k = i; k < end; k++)
                {
                    if (!byId.ContainsKey(items[k].Id))
                    {
                        albumItems.Add(GetVm(items[k]));
                    }
                }

                var album = new AlbumRow(albumItems, isPointer);
                albums.Add(album);
                row = album;
                i = end - 1;
            }
            else
            {
                row = new ItemRow(GetVm(item));
            }

            row.IsFirstInRun = lastSide != isPointer;
            lastSide = isPointer;
            rows.Add(row);
        }

        return new Result(rows, vms, byId, updates, albums);
    }

    /// <summary>Exclusive end index of the album starting at <paramref name="start"/> (same batch, side and day; images/videos only).</summary>
    private static int AlbumEnd(IReadOnlyList<LibraryItem> items, int start, DateTime date)
    {
        var first = items[start];
        if (!IsAlbumCandidate(first))
        {
            return start + 1;
        }

        var end = start + 1;
        while (end < items.Count)
        {
            var next = items[end];
            if (!IsAlbumCandidate(next) || next.BatchId != first.BatchId || next.Mode != first.Mode ||
                next.AddedAt.ToLocalTime().Date != date)
            {
                break;
            }

            end++;
        }

        return end;
    }

    private static bool IsAlbumCandidate(LibraryItem item)
        => item.BatchId is not null && !item.IsMissing && item.Kind is ItemKind.File or ItemKind.PastedImage &&
           (item.TypeKey is ExtensionMap.ImagesKey or "video" || item.Kind == ItemKind.PastedImage);

    /// <summary>
    /// Applies <paramref name="fresh"/> to <paramref name="target"/> keeping unchanged rows (common prefix and suffix),
    /// so appends, deletes and in-place edits don't reset the scroll position or the containers.
    /// </summary>
    public static void ApplyDiff(RangeObservableCollection<ThreadRow> target, IReadOnlyList<ThreadRow> fresh)
    {
        var start = 0;
        while (start < target.Count && start < fresh.Count && target[start].Key == fresh[start].Key)
        {
            target[start].UpdateFrom(fresh[start]);
            start++;
        }

        int endOld = target.Count - 1, endNew = fresh.Count - 1;
        while (endOld >= start && endNew >= start && target[endOld].Key == fresh[endNew].Key)
        {
            target[endOld].UpdateFrom(fresh[endNew]);
            endOld--;
            endNew--;
        }

        var removeCount = endOld - start + 1;
        var insertCount = endNew - start + 1;
        if (removeCount + insertCount > 200)
        {
            var merged = new List<ThreadRow>(fresh.Count);
            merged.AddRange(target.Take(start));
            for (var i = start; i <= endNew; i++)
            {
                merged.Add(fresh[i]);
            }

            merged.AddRange(target.Skip(endOld + 1));
            target.ReplaceAll(merged);
            return;
        }

        for (var i = 0; i < removeCount; i++)
        {
            target.RemoveAt(start);
        }

        for (var i = 0; i < insertCount; i++)
        {
            target.Insert(start + i, fresh[start + i]);
        }
    }
}
