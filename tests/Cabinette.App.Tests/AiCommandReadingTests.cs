using System.IO;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// The reading tools against a real library (stage 4, SPEC.md section 10): search, listings, details, contents, tags, the
/// operation status and "show in Cabinette". Reads are logged and make no change reports.
/// </summary>
public sealed class AiCommandReadingTests : AiCommandTestBase
{
    [Fact]
    public async Task Search_finds_categories_and_files_newest_first()
    {
        var docs = await BuiltInAsync("documents");
        var invoices = await Library.CreateCategoryAsync("Invoices", docs.Id);
        var older = await AddAsync(invoices.Id, "invoice march.pdf");
        var newer = await AddAsync(docs.Id, "invoice april.pdf");
        await Library.SetItemTagsAsync(newer.Id, ["paid"]);

        var result = Ok<SearchResult>(await CallAsync(AiTools.Search, new SearchArgs("invoice")));

        var category = Assert.Single(result.Categories);
        Assert.Equal(invoices.Id, category.Id);
        Assert.Equal("Documents/Invoices", category.Path);
        Assert.Equal("user", category.Kind);
        Assert.Equal(docs.Id, category.ParentId);
        Assert.Equal(1, category.FileCount);
        Assert.Equal([newer.Id, older.Id], result.Files.Select(f => f.Id));
        Assert.False(result.HasMore);

        var file = result.Files[0];
        Assert.Equal("invoice april.pdf", file.Name);
        Assert.Equal(newer.FullPath, file.Path);
        Assert.Equal("copy", file.Mode);
        Assert.Equal("file", file.Kind);
        Assert.Equal("documents", file.Type);
        Assert.Equal("app", file.AddedBy);
        Assert.Equal(["paid"], file.Tags);
        Assert.Equal([docs.Id], file.CategoryIds);
        Assert.Null(file.Hash); // details only in get_files
        Assert.Null(file.CategoryPaths);

        var tagged = Ok<SearchResult>(await CallAsync(AiTools.Search, new SearchArgs("#paid")));
        Assert.Equal([newer.Id], tagged.Files.Select(f => f.Id));

        var logged = Log.Entries[0];
        Assert.Equal((Client, AiTools.Search), (logged.Client, logged.Tool));
        Assert.True(logged.Details.Ok);
        Assert.Equal("invoice", logged.Details.Query);
        Assert.Equal(2, logged.Details.FileCount);
        Assert.Empty(Changes.Reported);
    }

    [Fact]
    public async Task Search_with_only_a_filter_lists_matching_files_and_says_when_there_are_more()
    {
        var images = await BuiltInAsync("images");
        var docs = await BuiltInAsync("documents");
        await AddAsync(images.Id, "a.png");
        await AddAsync(images.Id, "b.jpg");
        await AddAsync(docs.Id, "c.pdf");

        var result = Ok<SearchResult>(await CallAsync(AiTools.Search, new SearchArgs("", new AiFilter { Types = ["images"] }, Limit: 1)));
        var byExtension = Ok<SearchResult>(await CallAsync(AiTools.Search, new SearchArgs(" ", new AiFilter { Extensions = ["*.PDF"] })));

        Assert.Equal("b.jpg", Assert.Single(result.Files).Name);
        Assert.True(result.HasMore);
        Assert.Equal("c.pdf", Assert.Single(byExtension.Files).Name);
    }

    [Fact]
    public async Task List_categories_walks_the_tree_parents_first_with_paths()
    {
        var code = await BuiltInAsync("code");
        var python = await BuiltInAsync("code.python");
        var scripts = await Library.CreateCategoryAsync("Scripts", python.Id);

        var all = Ok<ListCategoriesResult>(await CallAsync(AiTools.ListCategories, new ListCategoriesArgs())).Categories;
        var noArgs = Ok<ListCategoriesResult>(await CallAsync(AiTools.ListCategories)).Categories;
        var subtree = Ok<ListCategoriesResult>(await CallAsync(AiTools.ListCategories, new ListCategoriesArgs(code.Id))).Categories;

        var index = all.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        Assert.True(index[code.Id] < index[python.Id]);
        Assert.True(index[python.Id] < index[scripts.Id]);
        Assert.Equal("Code/Python/Scripts", all[index[scripts.Id]].Path);
        Assert.Equal("builtin", all[index[python.Id]].Kind);
        Assert.Equal(1, all[index[python.Id]].SubcategoryCount);
        Assert.All(all.Where(c => c.ParentId is not null), c => Assert.True(index[c.ParentId!.Value] < index[c.Id]));
        Assert.DoesNotContain(all, c => c.Kind == "deleted"); // Deleted shows only when it holds files
        Assert.Equal(all.Select(c => c.Id), noArgs.Select(c => c.Id));

        Assert.DoesNotContain(subtree, c => c.Id == code.Id);
        Assert.Contains(subtree, c => c.Id == scripts.Id && c.Path == "Code/Python/Scripts");
        Assert.All(subtree, c => Assert.StartsWith("Code/", c.Path));
        Assert.Equal([new AiActivityRef(code.Id, "Code")], Log.Last.Details.Categories);
    }

    [Fact]
    public async Task List_files_pages_a_thread_newest_first_with_its_subcategories()
    {
        var code = await BuiltInAsync("code");
        var python = await BuiltInAsync("code.python");
        var first = await AddAsync(code.Id, "one.json");
        var second = await AddAsync(python.Id, "two.py");
        var third = await AddAsync(code.Id, "three.json");

        var page1 = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(code.Id, Limit: 2)));
        var page2 = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(code.Id, Offset: page1.NextOffset!.Value, Limit: 2)));
        var beyond = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(code.Id, Offset: 10)));
        var searched = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(code.Id, Query: "two")));
        var filtered = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(code.Id, Filter: new AiFilter { Extensions = ["json"] })));

        Assert.Equal([third.Id, second.Id], page1.Files.Select(f => f.Id));
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.NextOffset);
        Assert.Equal([first.Id], page2.Files.Select(f => f.Id));
        Assert.Null(page2.NextOffset);
        Assert.Empty(beyond.Files);
        Assert.Null(beyond.NextOffset);
        Assert.Equal([second.Id], searched.Files.Select(f => f.Id));
        Assert.Equal([third.Id, first.Id], filtered.Files.Select(f => f.Id));

        var logged = Log.Entries[1].Details;
        Assert.Equal(2, logged.Offset);
        Assert.Equal(1, logged.FileCount);
        Assert.Equal([new AiActivityRef(code.Id, "Code")], logged.Categories);
    }

    [Fact]
    public async Task List_files_of_the_starred_view_newest_star_first()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");
        var b = await AddAsync(docs.Id, "b.pdf");
        await AddAsync(docs.Id, "c.pdf");
        await Library.SetStarredAsync([b.Id], true);
        await Task.Delay(20);
        await Library.SetStarredAsync([a.Id], true);

        var result = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(Starred: true)));

        Assert.Equal([a.Id, b.Id], result.Files.Select(f => f.Id));
        Assert.All(result.Files, f => Assert.NotNull(f.StarredAt));
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task Get_files_returns_details_and_lists_unknown_ids()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var item = await AddAsync(docs.Id, "report.pdf", "report body");
        await Library.AddToCategoryAsync([item.Id], work.Id);
        var note = await Library.CreateNoteAsync((await BuiltInAsync("notes")).Id, "Buy milk\nand bread");

        var result = Ok<GetFilesResult>(await CallAsync(AiTools.GetFiles, new GetFilesArgs([item.Id, 999999, note.Id, item.Id])));

        Assert.Equal([999999L], result.NotFound);
        Assert.Equal([item.Id, note.Id], result.Files.Select(f => f.Id));
        var file = result.Files[0];
        var stored = (await Library.GetItemAsync(item.Id))!;
        Assert.Equal(Convert.ToHexStringLower(stored.Hash!), file.Hash);
        Assert.Matches("^[0-9a-f]{32}$", file.Hash!);
        Assert.Equal(stored.OriginalPath, file.OriginalPath);
        Assert.NotNull(file.ModifiedAt);
        Assert.Equal(stored.CategoryIds, file.CategoryIds);
        Assert.Equal(stored.CategoryIds.Select(id => id == docs.Id ? "Documents" : "Work"), file.CategoryPaths!);
        Assert.Equal(11, file.SizeBytes);
        Assert.Equal("note", result.Files[1].Kind);
        Assert.Equal("notes", result.Files[1].Type);
        Assert.StartsWith("Buy milk", result.Files[1].NotePreview);

        var logged = Log.Last.Details;
        Assert.True(logged.Ok);
        Assert.Equal(2, logged.FileCount);
        Assert.Equal(["report.pdf", note.FileName], logged.Files!.Select(r => r.Name));
    }

    [Fact]
    public async Task Read_file_without_a_content_reader_fails_clearly()
    {
        var item = await AddAsync((await BuiltInAsync("text")).Id, "a.txt");

        var message = Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id)), AiErrorCodes.Failed);

        Assert.Equal("Reading contents isn't available in this build of Cabinette.", message);
        Assert.Equal([new AiActivityRef(item.Id, "a.txt")], Log.Last.Details.Files);
    }

    [Fact]
    public async Task Read_file_pages_through_the_reader_and_fills_in_the_file()
    {
        var reader = new FakeContentReader();
        ContentReader = reader;
        var item = await AddAsync((await BuiltInAsync("text")).Id, "a.txt", "hello world");

        var page = Ok<ReadFileResult>(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id, 6)));

        Assert.Equal("world", page.Text);
        Assert.Equal(6, page.Offset);
        Assert.Equal(item.Id, page.File!.Id);
        Assert.Equal("a.txt", page.File.Name);
        Assert.Equal([(item.Id, 6L)], reader.Reads);
        Assert.Equal(6, Log.Last.Details.Offset);
        Assert.Empty(Changes.Reported);
    }

    [Fact]
    public async Task Read_file_refusals_and_failures()
    {
        var reader = new FakeContentReader();
        ContentReader = reader;
        var item = await AddAsync((await BuiltInAsync("other")).Id, "blob.bin");

        reader.Throw = new FileContentRefusedException("\"blob.bin\" is a binary file.");
        Assert.Equal("\"blob.bin\" is a binary file.", Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id)), AiErrorCodes.Refused));

        reader.Throw = new IOException("The process cannot access the file because it is being used by another process.");
        Assert.Contains("being used", Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id)), AiErrorCodes.Failed));

        reader.Throw = new FileNotFoundException("gone");
        Assert.Contains("missing", Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id)), AiErrorCodes.Refused));

        reader.Throw = null;
        File.Delete(item.FullPath);
        Assert.Contains("missing", Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id)), AiErrorCodes.Refused));

        Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(item.Id, -1)), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.ReadFile, new ReadFileArgs(123456)), AiErrorCodes.NotFound);
    }

    [Fact]
    public async Task List_tags_counts_files()
    {
        var docs = await BuiltInAsync("documents");
        var a = await AddAsync(docs.Id, "a.pdf");
        var b = await AddAsync(docs.Id, "b.pdf");
        await Library.AddTagsAsync([a.Id, b.Id], ["work"]);
        await Library.AddTagsAsync([a.Id], ["urgent"]);

        var tags = Ok<ListTagsResult>(await CallAsync(AiTools.ListTags, new ListTagsArgs())).Tags;

        Assert.Equal([new AiTag("urgent", 1), new AiTag("work", 2)], tags);
    }

    [Fact]
    public async Task Show_opens_a_file_in_its_home_category_or_a_category()
    {
        var docs = await BuiltInAsync("documents");
        var work = await Library.CreateCategoryAsync("Work", null);
        var item = await AddAsync(docs.Id, "a.pdf");
        await Library.AddToCategoryAsync([item.Id], work.Id);

        Assert.True(Ok<ShowResult>(await CallAsync(AiTools.ShowInCabinette, new ShowArgs(FileId: item.Id))).Shown);
        Ok<ShowResult>(await CallAsync(AiTools.ShowInCabinette, new ShowArgs(CategoryId: work.Id)));
        Ok<ShowResult>(await CallAsync(AiTools.ShowInCabinette, new ShowArgs(item.Id, work.Id)));
        Failed(await CallAsync(AiTools.ShowInCabinette, new ShowArgs(FileId: 777777)), AiErrorCodes.NotFound);

        Assert.Equal([(docs.Id, (long?)item.Id), (work.Id, null), (work.Id, item.Id)], Host.Shown);
        Assert.Empty(Changes.Reported);
        Assert.Equal(4, Log.Entries.Count(e => e.Tool == AiTools.ShowInCabinette));
    }

    [Fact]
    public async Task Get_operation_status_without_any_operation_is_not_found()
    {
        Assert.Contains("No AI client", Failed(await CallAsync(AiTools.GetOperationStatus), AiErrorCodes.NotFound));
        Assert.Contains("op-nope", Failed(await CallAsync(AiTools.GetOperationStatus, new GetOperationStatusArgs("op-nope")), AiErrorCodes.NotFound));
    }

    [Fact]
    public async Task Files_added_by_the_ai_say_so()
    {
        var notes = await BuiltInAsync("notes");

        var note = Ok<FileResult>(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("from the AI", notes.Id))).File;

        Assert.Equal("ai", note.AddedBy);
        Assert.Equal("note", note.Kind);
    }
}
