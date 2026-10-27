using System.IO;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

// Reading tools: search, listings, file details, contents, tags, and "show in Cabinette". They work while a vault operation runs.
public sealed partial class AiCommandHandler
{
    private async Task<object> SearchAsync(AiCall call)
    {
        var args = Args<SearchArgs>(call);
        var query = (args.Query ?? "").Trim();
        var filter = AiCommandConversions.ToItemFilter(args.Filter);
        call.Details = call.Details with { Query = query };
        if (query.Length == 0 && filter is null)
        {
            throw AiCallException.Invalid("Give a query, a filter, or both.");
        }

        var limit = ClampLimit(args.Limit);
        var results = await _library.SearchAsync(query, filter).ConfigureAwait(false);
        var paths = results.Categories.Count > 0 ? await CategoryPathsAsync().ConfigureAwait(false) : null;
        var files = results.Items.Take(limit).Select(i => AiCommandConversions.ToAiFile(i)).ToList();
        var categories = results.Categories
            .Select(c => AiCommandConversions.ToAiCategory(c, paths?.GetValueOrDefault(c.Category.Id) ?? c.Category.Name))
            .ToList();
        call.Details = call.Details with { FileCount = files.Count };
        return new SearchResult(categories, files, results.Items.Count > limit || results.HasMoreItems);
    }

    private async Task<object> ListCategoriesAsync(AiCall call)
    {
        var args = OptionalArgs(call, new ListCategoriesArgs());
        var list = new List<AiCategory>();
        if (args.ParentId is long parentId)
        {
            await LoadCategoryAsync(call, parentId).ConfigureAwait(false);
            var paths = await CategoryPathsAsync().ConfigureAwait(false);
            await WalkCategoriesAsync(parentId, paths.GetValueOrDefault(parentId) ?? "", list, depth: 0).ConfigureAwait(false);
        }
        else
        {
            await WalkCategoriesAsync(null, "", list, depth: 0).ConfigureAwait(false);
        }

        return new ListCategoriesResult(list);
    }

    /// <summary>The categories below <paramref name="parentId"/>, parents before their children, A–Z (pinned first) at each level.</summary>
    private async Task WalkCategoriesAsync(long? parentId, string prefix, List<AiCategory> into, int depth)
    {
        foreach (var summary in await _library.GetCategoriesAsync(parentId, CategorySort.Alphabetical).ConfigureAwait(false))
        {
            var path = prefix.Length == 0 ? summary.Category.Name : prefix + "/" + summary.Category.Name;
            into.Add(AiCommandConversions.ToAiCategory(summary, path));
            if (summary.SubcategoryCount > 0 && depth < 100)
            {
                await WalkCategoriesAsync(summary.Category.Id, path, into, depth + 1).ConfigureAwait(false);
            }
        }
    }

    private async Task<object> ListFilesAsync(AiCall call)
    {
        var args = Args<ListFilesArgs>(call);
        if (args.Starred == args.CategoryId.HasValue)
        {
            throw AiCallException.Invalid("Give a categoryId, or starred: true for the Starred view (not both).");
        }

        if (args.Offset < 0)
        {
            throw AiCallException.Invalid("offset can't be negative.");
        }

        var limit = ClampLimit(args.Limit);
        var query = string.IsNullOrWhiteSpace(args.Query) ? null : args.Query.Trim();
        var filter = AiCommandConversions.ToItemFilter(args.Filter);
        call.Details = call.Details with { Query = query, Offset = args.Offset };

        // Both come oldest first (chat order; Starred by when they were starred): the AI gets the newest first.
        IReadOnlyList<LibraryItem> items;
        if (args.CategoryId is long categoryId)
        {
            await LoadCategoryAsync(call, categoryId).ConfigureAwait(false);
            items = await _library.GetThreadItemsAsync(categoryId, query, filter).ConfigureAwait(false);
        }
        else
        {
            items = await _library.GetStarredItemsAsync(query, filter).ConfigureAwait(false);
        }

        var total = items.Count;
        var page = new List<AiFile>(Math.Min(limit, Math.Max(0, total - args.Offset)));
        for (var i = total - 1 - args.Offset; i >= 0 && page.Count < limit; i--)
        {
            page.Add(AiCommandConversions.ToAiFile(items[i]));
        }

        var next = args.Offset + page.Count;
        call.Details = call.Details with { FileCount = page.Count };
        return new ListFilesResult(page, total, page.Count > 0 && next < total ? next : null);
    }

    private async Task<object> GetFilesAsync(AiCall call)
    {
        var ids = FileIds(Args<GetFilesArgs>(call).FileIds);
        var paths = await CategoryPathsAsync().ConfigureAwait(false);
        var found = new List<LibraryItem>(ids.Count);
        var notFound = new List<long>();
        foreach (var id in ids)
        {
            if (await _library.GetItemAsync(id).ConfigureAwait(false) is { } item)
            {
                found.Add(item);
            }
            else
            {
                notFound.Add(id);
            }
        }

        call.Details = call.Details with { Files = Refs(found), FileCount = found.Count };
        return new GetFilesResult(found.Select(i => AiCommandConversions.ToAiFile(i, paths)).ToList(), notFound);
    }

    private async Task<object> ReadFileAsync(AiCall call)
    {
        var args = Args<ReadFileArgs>(call);
        if (args.Offset < 0)
        {
            throw AiCallException.Invalid("offset can't be negative.");
        }

        var item = await LoadItemAsync(call, args.FileId).ConfigureAwait(false);
        call.Details = call.Details with { Offset = args.Offset };
        var reader = _contentReader ?? throw new AiCallException(AiErrorCodes.Failed, "Reading contents isn't available in this build of Cabinette.");
        if (!File.Exists(item.FullPath))
        {
            throw AiCallException.Refused(MissingFileMessage(item));
        }

        ReadFileResult page;
        try
        {
            page = await Task.Run(() => reader.ReadAsync(item, args.Offset)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw AiCallException.Refused(MissingFileMessage(item));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"read_file: \"{item.FileName}\" could not be read ({ex.GetType().Name}: {ex.Message}).");
            throw new AiCallException(AiErrorCodes.Failed, $"\"{item.FileName}\" can't be read: {ex.Message}");
        }

        return page with { File = AiCommandConversions.ToAiFile(item) };
    }

    private static string MissingFileMessage(LibraryItem item)
        => $"\"{item.FileName}\" can't be read: the file is missing ({item.FullPath}).";

    private async Task<object> ListTagsAsync(AiCall call)
    {
        var tags = await _library.GetTagsAsync().ConfigureAwait(false);
        return new ListTagsResult(tags.Select(t => new AiTag(t.Name, t.ItemCount)).ToList());
    }

    private Task<object> GetOperationStatusAsync(AiCall call)
    {
        var args = OptionalArgs(call, new GetOperationStatusArgs());
        var id = string.IsNullOrWhiteSpace(args.OperationId) ? null : args.OperationId.Trim();
        call.Details = call.Details with { OperationId = id };
        var status = _operations.Find(id) ?? throw new AiCallException(AiErrorCodes.NotFound, id is null
            ? "No AI client has started a vault operation since Cabinette started."
            : $"No vault operation has the id \"{id}\" (Cabinette only remembers the ones started since it started).");
        call.Details = call.Details with { OperationId = status.OperationId };
        return Task.FromResult<object>(status);
    }

    /// <summary>Opens the window at a file (its home category, scrolled to it) or a category; both: that category, at the file.</summary>
    private async Task<object> ShowInCabinetteAsync(AiCall call)
    {
        var args = Args<ShowArgs>(call);
        if (args.FileId is null && args.CategoryId is null)
        {
            throw AiCallException.Invalid("Give a fileId or a categoryId.");
        }

        var item = args.FileId is long fileId ? await LoadItemAsync(call, fileId).ConfigureAwait(false) : null;
        var categoryId = args.CategoryId is long id ? (await LoadCategoryAsync(call, id).ConfigureAwait(false)).Id : item!.HomeCategoryId;
        await _host.ShowAsync(categoryId, item?.Id).ConfigureAwait(false);
        return new ShowResult(true);
    }
}
