using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Services;

// "Move to category…" (SPEC.md section 6).
internal sealed partial class LibraryService
{
    /// <summary>
    /// One write: every vault file that has to move is checked first (missing or in use fails the whole call before anything
    /// changes), then each item leaves the source thread's categories, joins the target, and a copy-mode file whose home it
    /// left moves into the target's folder through the write's file journal (undone if anything later fails). The vault
    /// monitor needs nothing special: it reconciles under the same write lock and compares the database with the disk, and
    /// after this commit both agree, so the watcher events of these moves change nothing (as for a category rename).
    /// </summary>
    public Task MoveToCategoryAsync(IReadOnlyList<long> itemIds, long fromCategoryId, long toCategoryId, AddedBy addedBy = AddedBy.AppUi,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return WriteAsync(ctx =>
        {
            var tree = ctx.Tree;
            tree.Get(fromCategoryId);
            var target = tree.Get(toCategoryId);
            if (toCategoryId == fromCategoryId)
            {
                return;
            }

            EnsureCanAddTo(target);
            var leaving = tree.SubtreeIds(fromCategoryId).ToHashSet();
            var deletedId = tree.DeletedId;

            var moves = new List<PlannedMove>();
            foreach (var itemId in itemIds.Distinct())
            {
                if (ItemReader.LoadRow(ctx.Connection, ctx.Transaction, itemId) is not { } item)
                {
                    continue;
                }

                var memberships = MembershipsOf(ctx, itemId);
                var leave = memberships.Where(c => leaving.Contains(c) && c != toCategoryId).ToList();
                if (leave.Count == 0 && memberships.Contains(toCategoryId))
                {
                    continue;
                }

                // Its home goes when it leaves it, or when joining the target takes it out of "Deleted".
                var rehome = leave.Contains(item.HomeCategoryId) || item.HomeCategoryId == deletedId;
                if (rehome && item.ItemMode == ItemMode.Copy)
                {
                    EnsureVaultFileCanMove(ctx, item);
                }

                moves.Add(new PlannedMove(item, memberships, leave, rehome));
            }

            ctx.Changes.Source = addedBy;
            foreach (var move in moves)
            {
                var item = move.Item;
                ctx.Changes.Item(item.Id, tree, move.Memberships);
                ctx.Changes.Categories(tree, move.Leave);
                RemoveMemberships(ctx, item.Id, move.Leave);
                AddMembership(ctx, item.Id, toCategoryId, addedBy);
                if (move.Rehome)
                {
                    // AddMembership has already re-homed an item that only left "Deleted" by joining the target.
                    var current = ItemReader.LoadRow(ctx.Connection, ctx.Transaction, item.Id)!;
                    try
                    {
                        Rehome(ctx, current, toCategoryId);
                    }
                    catch (Exception ex) when (FileOps.IsSharingViolation(ex))
                    {
                        // Open without delete sharing (an editor holding it): the write is undone, files moved so far go back.
                        throw InUse(current.FileName, ex);
                    }
                }
            }
        }, ct);
    }

    private sealed record PlannedMove(ItemRow Item, List<long> Memberships, List<long> Leave, bool Rehome);

    /// <summary>
    /// A vault file that has to move must be at its place. Whether it can move is left to the move itself: a program that
    /// only reads it (the vault monitor hashing it, a viewer) opens it with delete sharing, so it moves like a rename does;
    /// one that holds it without fails the move with "in use", and the write is undone.
    /// </summary>
    private void EnsureVaultFileCanMove(WriteContext ctx, ItemRow item)
    {
        var path = Path.Combine(ctx.Tree.FolderPath(RequireVault(), item.HomeCategoryId), item.FileName);
        if (!File.Exists(path))
        {
            throw new LibraryException(LibraryErrorCode.NotFound, $"\"{item.FileName}\" could not be found. It may have been moved or deleted.");
        }
    }
}
