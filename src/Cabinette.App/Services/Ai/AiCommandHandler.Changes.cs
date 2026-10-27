using System.IO;
using System.Text;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

// Changing tools (SPEC.md section 10). Like the UI: nothing goes into Deleted directly and Deleted can't be deleted. Deletes
// always go to the Recycle Bin and fail rather than delete permanently. Each successful change is reported for the toasts.
public sealed partial class AiCommandHandler
{
    /// <summary>The most tags one tag_files or untag_files call may name.</summary>
    internal const int MaxTagsPerCall = 100;

    /// <summary>The longest tag name an AI may give (after normalizing).</summary>
    internal const int MaxTagLength = 200;

    /// <summary>How an AI deletes: to the Recycle Bin whatever the setting says, linked files as the user's setting says, never permanently.</summary>
    internal static readonly DeleteItemsOptions RecycleOnly = new()
    {
        RecycleVaultFiles = true,
        DeleteRealPointerFiles = null,
        PermanentWhenNoRecycleBin = false,
    };

    // ==================================================================== adding

    private async Task<object> AddFilesAsync(AiCall call)
    {
        var args = Args<AddFilesArgs>(call);
        if (args.Paths is null || args.Paths.Count == 0)
        {
            throw AiCallException.Invalid("paths must name at least one file or folder.");
        }

        if (args.Paths.Count > AiProtocol.MaxPathsPerCall)
        {
            throw AiCallException.Invalid($"At most {AiProtocol.MaxPathsPerCall} paths per call ({args.Paths.Count} given).");
        }

        call.Details = call.Details with { Paths = args.Paths.Take(AiActivityDetails.MaxRefs).ToList() };
        foreach (var path in args.Paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw AiCallException.Invalid($"\"{path}\" is not a full path. Give full paths such as C:\\Users\\you\\file.txt.");
            }
        }

        ItemMode? mode = null;
        if (args.Mode is not null)
        {
            mode = AiCommandConversions.ParseMode(args.Mode) ?? throw AiCallException.Invalid($"Unknown mode \"{args.Mode}\": use \"copy\" or \"pointer\".");
        }

        var effectiveMode = mode ?? (await _library.GetSettingsAsync().ConfigureAwait(false)).DefaultAddMode;
        call.Details = call.Details with { Mode = AiCommandConversions.ModeName(effectiveMode) };

        Category? target = null;
        if (args.CategoryId is long categoryId)
        {
            target = await LoadCategoryAsync(call, categoryId).ConfigureAwait(false);
            EnsureCanAddTo(target);
        }

        // Into the category the AI named, without sorting by type; without one, sorted by type like a drop on the main window.
        var result = await _library.AddPathsAsync(args.Paths, new AddRequest
        {
            TargetCategoryId = target?.Id,
            Mode = effectiveMode,
            AddedBy = AddedBy.Ai,
            SortIntoTypeCategories = false,
        }).ConfigureAwait(false);

        var named = result.Added.Concat(result.Duplicates.Select(d => d.Existing)).ToList();
        call.Details = call.Details with { Files = Refs(named), FileCount = named.Count };
        if (result.Added.Count + result.Duplicates.Count + result.Failures.Count > 0)
        {
            Report(await AddedChangeAsync(call.Client, result, target).ConfigureAwait(false));
        }

        return new AddFilesResult(
            result.Added.Select(i => AiCommandConversions.ToAiFile(i)).ToList(),
            result.Duplicates.Select(d => new AiDuplicate(d.SourcePath, AiCommandConversions.ToAiFile(d.Existing), d.AddedToTargetCategory)).ToList(),
            result.Failures.Select(f => new AiFailure(f.SourcePath, f.Message)).ToList(),
            result.CreatedCategoryIds);
    }

    /// <summary>"Claude Code added 3 files to Images", or per category when they went to several.</summary>
    private async Task<AiChange> AddedChangeAsync(string client, AddResult result, Category? target)
    {
        var change = new AiChange(client, AiChangeKind.FilesAdded, result.Added.Count)
        {
            Duplicates = result.Duplicates.Count,
            Failures = result.Failures.Count,
        };

        var homes = result.Added.Select(i => i.HomeCategoryId).Distinct().ToList();
        if (homes.Count == 0)
        {
            return target is null ? change : change with { CategoryId = target.Id, CategoryName = target.Name };
        }

        var names = (await _library.GetAllCategoriesAsync().ConfigureAwait(false)).ToDictionary(c => c.Id, c => c.Name);
        if (homes.Count == 1)
        {
            return change with { CategoryId = homes[0], CategoryName = names.GetValueOrDefault(homes[0]) };
        }

        var byCategory = new Dictionary<string, int>(StringComparer.CurrentCulture);
        foreach (var item in result.Added)
        {
            var name = names.GetValueOrDefault(item.HomeCategoryId) ?? "";
            byCategory[name] = byCategory.GetValueOrDefault(name) + 1;
        }

        return change with { AddedByCategory = byCategory };
    }

    private async Task<object> CreateNoteAsync(AiCall call)
    {
        var args = Args<CreateNoteArgs>(call);
        if (string.IsNullOrWhiteSpace(args.Text))
        {
            throw AiCallException.Invalid("A note needs some text.");
        }

        Category category;
        if (args.CategoryId is long categoryId)
        {
            category = await LoadCategoryAsync(call, categoryId).ConfigureAwait(false);
            EnsureCanAddTo(category);
        }
        else
        {
            category = await _library.GetOrCreateBuiltInCategoryAsync(ExtensionMap.NotesKey).ConfigureAwait(false);
            call.AddCategory(category);
        }

        var note = await _library.CreateNoteAsync(category.Id, args.Text, AddedBy.Ai).ConfigureAwait(false);
        call.Details = call.Details with { Files = Refs([note]), FileCount = 1 };
        Report(new AiChange(call.Client, AiChangeKind.NoteCreated) { CategoryId = category.Id, CategoryName = category.Name });
        return new FileResult(AiCommandConversions.ToAiFile(note));
    }

    // ==================================================================== tags, stars

    private async Task<object> ChangeTagsAsync(AiCall call, bool add)
    {
        var args = Args<TagFilesArgs>(call);
        var ids = FileIds(args.FileIds);
        if (args.Tags is null || args.Tags.Count == 0)
        {
            throw AiCallException.Invalid("tags must name at least one tag.");
        }

        if (args.Tags.Count > MaxTagsPerCall)
        {
            throw AiCallException.Invalid($"At most {MaxTagsPerCall} tags per call.");
        }

        // Normalized like the tag editor, so the AI's tags are the user's: trimmed, no leading '#', inner spaces become '-'.
        var tags = new List<string>();
        foreach (var tag in args.Tags)
        {
            var name = TagName(tag);
            if (name.Length == 0)
            {
                throw AiCallException.Invalid($"\"{tag}\" is not a tag name.");
            }

            if (name.Length > MaxTagLength)
            {
                throw AiCallException.Invalid($"A tag can have at most {MaxTagLength} characters ({name.Length} given).");
            }

            if (!tags.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(name);
            }
        }

        call.Details = call.Details with { Tags = tags };
        var items = await LoadItemsAsync(call, ids).ConfigureAwait(false);
        if (add)
        {
            await _library.AddTagsAsync(ids, tags).ConfigureAwait(false);
        }
        else
        {
            await _library.RemoveTagsAsync(ids, tags).ConfigureAwait(false);
        }

        var result = await ChangedAsync(items, SameTags).ConfigureAwait(false);
        ReportChanged(result, new AiChange(call.Client, add ? AiChangeKind.FilesTagged : AiChangeKind.FilesUntagged, result.Count));
        return result;
    }

    /// <summary>
    /// A tag as the tag editor stores it (<see cref="TagEditorViewModel.Normalize"/>). Control characters other than white space
    /// are dropped first (white space becomes '-' there); format characters stay, since the joiners of emoji sequences are part of
    /// the user's tags. Empty when nothing is left.
    /// </summary>
    internal static string TagName(string? text)
    {
        var kept = new StringBuilder((text ?? "").Length);
        foreach (var c in text ?? "")
        {
            if (!char.IsControl(c) || char.IsWhiteSpace(c))
            {
                kept.Append(c);
            }
        }

        return TagEditorViewModel.Normalize(kept.ToString());
    }

    private async Task<object> SetStarredAsync(AiCall call, bool starred)
    {
        var ids = FileIds(Args<FileIdsArgs>(call).FileIds);
        var items = await LoadItemsAsync(call, ids).ConfigureAwait(false);
        await _library.SetStarredAsync(ids, starred).ConfigureAwait(false);
        var result = await ChangedAsync(items, (before, after) => (before.StarredAt is null) == (after.StarredAt is null)).ConfigureAwait(false);
        ReportChanged(result, new AiChange(call.Client, starred ? AiChangeKind.FilesStarred : AiChangeKind.FilesUnstarred, result.Count));
        return result;
    }

    // ==================================================================== categories of files

    private async Task<object> AddToCategoryAsync(AiCall call)
    {
        var args = Args<FilesCategoryArgs>(call);
        var ids = FileIds(args.FileIds);
        var category = await LoadCategoryAsync(call, args.CategoryId).ConfigureAwait(false);
        EnsureCanAddTo(category);
        var items = await LoadItemsAsync(call, ids).ConfigureAwait(false);
        await _library.AddToCategoryAsync(ids, category.Id, AddedBy.Ai).ConfigureAwait(false);
        var result = await ChangedAsync(items, SameCategories).ConfigureAwait(false);
        ReportChanged(result, new AiChange(call.Client, AiChangeKind.FilesAddedToCategory, result.Count) { CategoryId = category.Id, CategoryName = category.Name });
        return result;
    }

    private async Task<object> RemoveFromCategoryAsync(AiCall call)
    {
        var args = Args<FilesCategoryArgs>(call);
        var ids = FileIds(args.FileIds);
        var category = await LoadCategoryAsync(call, args.CategoryId).ConfigureAwait(false);
        var items = await LoadItemsAsync(call, ids).ConfigureAwait(false);

        // Like the UI, which offers "Remove from this category" only for files that are in other categories too (and asks first
        // about the others): a file that is only in this category and its subcategories would end up in Deleted.
        var subtree = await SubtreeAsync(category.Id).ConfigureAwait(false);
        var onlyHere = items.Where(i => ThreadViewModel.IsOnlyInThread(i, subtree)).Select(i => i.Id).ToList();
        if (onlyHere.Count > 0)
        {
            var one = onlyHere.Count == 1;
            throw AiCallException.Refused(
                $"{IdsText(onlyHere)} {(one ? "is" : "are")} only in \"{category.Name}\"{(subtree.Count > 1 ? " and its subcategories" : "")}: "
                + $"removing {(one ? "it" : "them")} would send {(one ? "it" : "them")} to Deleted. Nothing was changed. Use delete_files "
                + "to delete files (to the Recycle Bin) or move_files to move them to another category.");
        }

        await _library.RemoveFromCategoryAsync(ids, category.Id).ConfigureAwait(false);
        var result = await ChangedAsync(items, SameCategories).ConfigureAwait(false);
        ReportChanged(result, new AiChange(call.Client, AiChangeKind.FilesRemovedFromCategory, result.Count) { CategoryId = category.Id, CategoryName = category.Name });
        return result;
    }

    private async Task<object> MoveFilesAsync(AiCall call)
    {
        var args = Args<MoveFilesArgs>(call);
        var ids = FileIds(args.FileIds);
        var from = await LoadCategoryAsync(call, args.FromCategoryId).ConfigureAwait(false);
        var to = await LoadCategoryAsync(call, args.ToCategoryId).ConfigureAwait(false);
        if (to.Kind == CategoryKind.System)
        {
            throw AiCallException.Refused($"Files can't be moved into \"{to.Name}\". Delete them instead (they go to the Recycle Bin).");
        }

        var items = await LoadItemsAsync(call, ids).ConfigureAwait(false);

        // The library moves files out of the thread they are shown in (the UI's case). A file that isn't in that category or a
        // subcategory of it would only be added to the target, so the call is refused before anything changes.
        var leaving = await SubtreeAsync(from.Id).ConfigureAwait(false);
        var elsewhere = items.Where(i => !i.CategoryIds.Any(leaving.Contains)).Select(i => i.Id).ToList();
        if (elsewhere.Count > 0)
        {
            throw AiCallException.Invalid(
                $"{IdsText(elsewhere)} {(elsewhere.Count == 1 ? "isn't" : "aren't")} in \"{from.Name}\" or its subcategories, so "
                + $"{(elsewhere.Count == 1 ? "it" : "they")} can't be moved out of it. Nothing was changed. fromCategoryId must be a "
                + "category the files are in (see their categoryIds); add_to_category adds files to another category.");
        }

        await _library.MoveToCategoryAsync(ids, from.Id, to.Id, AddedBy.Ai).ConfigureAwait(false);
        var result = await ChangedAsync(items, SameCategories).ConfigureAwait(false);
        ReportChanged(result, new AiChange(call.Client, AiChangeKind.FilesMoved, result.Count) { CategoryId = to.Id, CategoryName = to.Name });
        return result;
    }

    // ==================================================================== what really changed

    /// <summary>
    /// The files as they are now, counting those that differ from <paramref name="before"/> in what the tool changes: a call
    /// that changed nothing (the files already had the tag, were already in the category...) says 0.
    /// </summary>
    private async Task<AiChangeResult> ChangedAsync(IReadOnlyList<LibraryItem> before, Func<LibraryItem, LibraryItem, bool> unchanged)
    {
        var files = new List<AiFile>(before.Count);
        var changed = 0;
        foreach (var old in before)
        {
            if (await _library.GetItemAsync(old.Id).ConfigureAwait(false) is not { } now)
            {
                continue;
            }

            if (!unchanged(old, now))
            {
                changed++;
            }

            files.Add(AiCommandConversions.ToAiFile(now));
        }

        return new AiChangeResult { Count = changed, Files = files };
    }

    /// <summary>A toast only for a call that changed something.</summary>
    private void ReportChanged(AiChangeResult result, AiChange change)
    {
        if (result.Count > 0)
        {
            Report(change);
        }
    }

    private static bool SameTags(LibraryItem before, LibraryItem after)
        => before.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(after.Tags);

    private static bool SameCategories(LibraryItem before, LibraryItem after)
        => before.HomeCategoryId == after.HomeCategoryId && before.CategoryIds.ToHashSet().SetEquals(after.CategoryIds);

    /// <summary>The category and all its subcategories (what its thread shows).</summary>
    private async Task<IReadOnlySet<long>> SubtreeAsync(long categoryId)
    {
        var all = await _library.GetAllCategoriesAsync().ConfigureAwait(false);
        var subtree = new HashSet<long> { categoryId };
        bool grew;
        do
        {
            grew = false;
            foreach (var category in all)
            {
                if (category.ParentId is long parent && subtree.Contains(parent) && subtree.Add(category.Id))
                {
                    grew = true;
                }
            }
        }
        while (grew);

        return subtree;
    }

    /// <summary>"The file with id 5" or "The files with ids 5, 6, 7" (at most 20 ids listed).</summary>
    private static string IdsText(IReadOnlyList<long> ids)
    {
        const int Listed = 20;
        if (ids.Count == 1)
        {
            return $"The file with id {ids[0]}";
        }

        var list = string.Join(", ", ids.Take(Listed));
        return ids.Count > Listed ? $"The files with ids {list} and {ids.Count - Listed} more" : $"The files with ids {list}";
    }

    /// <summary>Nothing goes into Deleted directly (SPEC.md section 3).</summary>
    private static void EnsureCanAddTo(Category category)
    {
        if (category.Kind == CategoryKind.System)
        {
            throw AiCallException.Refused($"Files can't be added to \"{category.Name}\": it only holds files whose category was deleted.");
        }
    }

    // ==================================================================== rename, delete

    private async Task<object> RenameFileAsync(AiCall call)
    {
        var args = Args<RenameFileArgs>(call);
        var newName = RequiredName(args.NewName, "newName");
        call.Details = call.Details with { NewName = newName };
        var item = await LoadItemAsync(call, args.FileId).ConfigureAwait(false);
        await _library.RenameItemAsync(item.Id, newName).ConfigureAwait(false);

        // The library keeps names unique in their folder ("name (1).ext"): report the name it got.
        var renamed = await _library.GetItemAsync(item.Id).ConfigureAwait(false) ?? item with { FileName = newName };
        call.Details = call.Details with { NewName = renamed.FileName };
        if (!string.Equals(renamed.FileName, item.FileName, StringComparison.Ordinal))
        {
            Report(new AiChange(call.Client, AiChangeKind.FileRenamed) { Detail = renamed.FileName });
        }

        return new FileResult(AiCommandConversions.ToAiFile(renamed));
    }

    private async Task<object> DeleteFilesAsync(AiCall call)
    {
        var ids = FileIds(Args<FileIdsArgs>(call).FileIds);
        await LoadItemsAsync(call, ids).ConfigureAwait(false);
        try
        {
            await _library.DeleteItemsAsync(ids, RecycleOnly).ConfigureAwait(false);
        }
        catch (LibraryException ex) when (ex.Code == LibraryErrorCode.RecycleBinUnavailable)
        {
            throw await RecycleBinRefusedAsync(ex, ids).ConfigureAwait(false);
        }

        Report(new AiChange(call.Client, AiChangeKind.FilesDeleted, ids.Count));
        return new AiChangeResult { Count = ids.Count };
    }

    /// <summary>
    /// recycle_bin_unavailable: nothing is deleted permanently. Usually nothing changed; when Windows refused files only after the
    /// delete went through (SPEC.md section 6), their library entries are gone but the files stay on disk, and the message says so.
    /// </summary>
    private async Task<AiCallException> RecycleBinRefusedAsync(LibraryException ex, IReadOnlyList<long> ids)
    {
        var gone = 0;
        foreach (var id in ids)
        {
            if (await _library.GetItemAsync(id).ConfigureAwait(false) is null)
            {
                gone++;
            }
        }

        var message = ex.Message + " Nothing was deleted permanently: the files are still on disk.";
        if (gone > 0)
        {
            message += $" Windows refused them only after the delete went through, so {gone} of the {ids.Count} files left the library;"
                       + " vault files come back as new items at the next rescan.";
        }

        AppLog.Info($"AI delete: the Recycle Bin refused files ({gone} of {ids.Count} entries already removed).");
        return new AiCallException(AiErrorCodes.RecycleBinUnavailable, message);
    }

    // ==================================================================== categories

    private async Task<object> CreateCategoryAsync(AiCall call)
    {
        var args = Args<CreateCategoryArgs>(call);
        var name = RequiredName(args.Name, "name");
        call.Details = call.Details with { NewName = name };
        if (args.ParentId is long parentId)
        {
            var parent = await LoadCategoryAsync(call, parentId).ConfigureAwait(false);
            if (parent.Kind == CategoryKind.System)
            {
                throw AiCallException.Refused($"\"{parent.Name}\" can't have subcategories.");
            }
        }

        var created = await _library.CreateCategoryAsync(name, args.ParentId).ConfigureAwait(false);
        call.AddCategory(created);
        call.Details = call.Details with { NewName = created.Name };
        Report(new AiChange(call.Client, AiChangeKind.CategoryCreated) { CategoryId = created.Id, CategoryName = created.Name });
        return new CategoryResult(await AiCategoryAsync(created).ConfigureAwait(false));
    }

    private async Task<object> RenameCategoryAsync(AiCall call)
    {
        var args = Args<RenameCategoryArgs>(call);
        var newName = RequiredName(args.NewName, "newName");
        call.Details = call.Details with { NewName = newName };
        var category = await LoadCategoryAsync(call, args.CategoryId).ConfigureAwait(false);
        if (category.Kind == CategoryKind.System)
        {
            // Like the UI, which offers no Rename for it: the user couldn't rename it back.
            throw AiCallException.Refused($"\"{category.Name}\" can't be renamed: it is Cabinette's own category for files whose category was deleted.");
        }

        await _library.RenameCategoryAsync(category.Id, newName).ConfigureAwait(false);
        var renamed = await _library.GetCategoryAsync(category.Id).ConfigureAwait(false) ?? category with { Name = newName };
        call.Details = call.Details with { NewName = renamed.Name };
        if (!string.Equals(renamed.Name, category.Name, StringComparison.Ordinal))
        {
            Report(new AiChange(call.Client, AiChangeKind.CategoryRenamed) { CategoryId = renamed.Id, CategoryName = renamed.Name, Detail = renamed.Name });
        }

        return new CategoryResult(await AiCategoryAsync(renamed).ConfigureAwait(false));
    }

    /// <summary>
    /// Follows the user's "Deleting a category" setting (SPEC.md section 10): "Ask each time" moves the files to Deleted, and
    /// files that are deleted go to the Recycle Bin, never permanently.
    /// </summary>
    private async Task<object> DeleteCategoryAsync(AiCall call)
    {
        var args = Args<DeleteCategoryArgs>(call);
        var category = await LoadCategoryAsync(call, args.CategoryId).ConfigureAwait(false);
        if (category.Kind == CategoryKind.System)
        {
            throw AiCallException.Refused($"\"{category.Name}\" can't be deleted. Only the user can empty it, in Cabinette.");
        }

        var all = await _library.GetAllCategoriesAsync().ConfigureAwait(false);
        var categories = 1 + Descendants(all, category.Id);
        var files = (await _library.GetCategorySummaryAsync(category.Id).ConfigureAwait(false))?.ItemCount ?? 0;
        call.Details = call.Details with { FileCount = files };

        var settings = await _library.GetSettingsAsync().ConfigureAwait(false);
        var policy = settings.CategoryDeletePolicy == CategoryDeletePolicy.RecycleFiles ? CategoryDeletePolicy.RecycleFiles : CategoryDeletePolicy.MoveToDeleted;
        try
        {
            await _library.DeleteCategoryAsync(category.Id, policy, policy == CategoryDeletePolicy.RecycleFiles ? RecycleOnly : null).ConfigureAwait(false);
        }
        catch (LibraryException ex) when (ex.Code == LibraryErrorCode.RecycleBinUnavailable)
        {
            var gone = await _library.GetCategoryAsync(category.Id).ConfigureAwait(false) is null;
            AppLog.Info($"AI category delete: the Recycle Bin refused files (category {(gone ? "already deleted" : "kept")}).");
            throw new AiCallException(AiErrorCodes.RecycleBinUnavailable, ex.Message + (gone
                ? " Nothing was deleted permanently. Windows refused the files only after the delete went through: the category is gone, but those files are still on disk."
                : " Nothing was deleted: the category and its files are as they were."));
        }

        Report(new AiChange(call.Client, AiChangeKind.CategoryDeleted, categories) { CategoryId = category.Id, CategoryName = category.Name });
        return new DeleteCategoryResult(policy == CategoryDeletePolicy.RecycleFiles ? "recycled" : "moved_to_deleted", categories);
    }

    private static int Descendants(IReadOnlyList<Category> all, long id)
    {
        var children = all.Where(c => c.ParentId == id).ToList();
        return children.Count + children.Sum(c => Descendants(all, c.Id));
    }

    private async Task<AiCategory> AiCategoryAsync(Category category)
    {
        var paths = await CategoryPathsAsync().ConfigureAwait(false);
        var summary = await _library.GetCategorySummaryAsync(category.Id).ConfigureAwait(false) ?? new CategorySummary { Category = category };
        return AiCommandConversions.ToAiCategory(summary, paths.GetValueOrDefault(category.Id) ?? category.Name);
    }
}
