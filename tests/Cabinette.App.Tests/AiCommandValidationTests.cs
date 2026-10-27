using System.IO;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// The "ai" command's checks before anything runs (stage 4, SPEC.md section 10): versions, tools, arguments, limits, unknown
/// ids, the client's name, "busy" while a vault operation runs, "not_ready" before Cabinette is ready and while it closes.
/// </summary>
public sealed class AiCommandValidationTests : AiCommandTestBase
{
    [Fact]
    public void Every_tool_of_the_protocol_has_a_handler()
    {
        Assert.Equal(AiTools.All.Order(StringComparer.Ordinal), Handler.Tools.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(null)]
    public async Task Another_protocol_version_is_unsupported_and_changes_nothing(int? version)
    {
        var notes = await BuiltInAsync("notes");

        var reply = await CallAsync(AiTools.CreateNote, new CreateNoteArgs("hello", notes.Id), version: version);

        Assert.Equal("Cabinette and cabinette-mcp.exe are different versions; update both.", Failed(reply, AiErrorCodes.Unsupported));
        Assert.Empty(await Library.GetThreadItemsAsync(notes.Id));
        Assert.Empty(Changes.Reported);
        Assert.Equal(AiErrorCodes.Unsupported, Log.Last.Details.ErrorCode);
    }

    [Fact]
    public async Task An_unknown_tool_is_unsupported()
    {
        var reply = await CallAsync("format_disk", new { drive = "C" });

        Assert.Contains("format_disk", Failed(reply, AiErrorCodes.Unsupported));
        Assert.Equal("format_disk", Log.Last.Tool);
        Assert.False(Log.Last.Details.Ok);
    }

    [Fact]
    public async Task Missing_or_malformed_arguments_are_invalid()
    {
        Failed(await CallAsync(AiTools.GetFiles), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.GetFiles, new { fileIds = "seven" }), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.GetFiles, new { fileIds = Array.Empty<long>() }), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.RenameFile, new RenameFileArgs(1, "  ")), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.CreateCategory, new CreateCategoryArgs("")), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.CreateNote, new CreateNoteArgs(" \n ")), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.ShowInCabinette, new ShowArgs()), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.ListFiles, new ListFilesArgs()), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.ListFiles, new ListFilesArgs(CategoryId: 1, Offset: -1)), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.Search, new SearchArgs("")), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.Search, new SearchArgs("x", new AiFilter { Types = ["pictures"] })), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.Search, new SearchArgs("x", new AiFilter { Mode = "linked" })), AiErrorCodes.InvalidArgument);
        Failed(await CallAsync(AiTools.TagFiles, new TagFilesArgs([1], ["#"])), AiErrorCodes.InvalidArgument);
        Assert.Empty(Changes.Reported);
        Assert.All(Log.Entries, e => Assert.Equal(AiErrorCodes.InvalidArgument, e.Details.ErrorCode));
    }

    [Fact]
    public async Task Too_many_ids_or_paths_are_invalid()
    {
        var ids = Enumerable.Range(1, AiProtocol.MaxIdsPerCall + 1).Select(i => (long)i).ToList();
        var paths = Enumerable.Range(1, AiProtocol.MaxPathsPerCall + 1).Select(i => $@"C:\nowhere\{i}.txt").ToList();

        Assert.Contains("1000", Failed(await CallAsync(AiTools.DeleteFiles, new FileIdsArgs(ids)), AiErrorCodes.InvalidArgument));
        Assert.Contains("1000", Failed(await CallAsync(AiTools.AddFiles, new AddFilesArgs(paths)), AiErrorCodes.InvalidArgument));
    }

    [Theory]
    [InlineData(@"relative\file.txt")]
    [InlineData(@"C:file.txt")]
    [InlineData(@"\rooted\but\no\drive.txt")]
    [InlineData("")]
    public async Task Paths_to_add_must_be_full_paths(string path)
    {
        var real = SourceFile("real.txt");

        var reply = await CallAsync(AiTools.AddFiles, new AddFilesArgs([real, path]));

        Failed(reply, AiErrorCodes.InvalidArgument);
        Assert.Empty((await Library.SearchAsync("real")).Items);
    }

    [Fact]
    public async Task An_unknown_mode_is_invalid()
    {
        var reply = await CallAsync(AiTools.AddFiles, new AddFilesArgs([SourceFile("a.txt")], Mode: "link"));

        Failed(reply, AiErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task List_limits_are_clamped()
    {
        var docs = await BuiltInAsync("documents");
        for (var i = 0; i < 3; i++)
        {
            await AddAsync(docs.Id, $"report {i}.pdf");
        }

        var none = Ok<ListFilesResult>(await CallAsync(AiTools.ListFiles, new ListFilesArgs(docs.Id, Limit: 0)));
        var huge = Ok<SearchResult>(await CallAsync(AiTools.Search, new SearchArgs("report", Limit: 1_000_000)));

        Assert.Single(none.Files);
        Assert.Equal(1, none.NextOffset);
        Assert.Equal(3, huge.Files.Count);
    }

    // ------------------------------------------------------------------ unknown ids change nothing

    [Fact]
    public async Task One_unknown_file_id_fails_the_whole_call_and_names_it()
    {
        var docs = await BuiltInAsync("documents");
        var item = await AddAsync(docs.Id, "keep.pdf");

        var delete = await CallAsync(AiTools.DeleteFiles, new FileIdsArgs([item.Id, 987654]));
        var tag = await CallAsync(AiTools.TagFiles, new TagFilesArgs([item.Id, 987654], ["x"]));
        var star = await CallAsync(AiTools.StarFiles, new FileIdsArgs([987654, item.Id, 987655]));

        Assert.Contains("987654", Failed(delete, AiErrorCodes.NotFound));
        Assert.Contains("987654", Failed(tag, AiErrorCodes.NotFound));
        var both = Failed(star, AiErrorCodes.NotFound);
        Assert.Contains("987654", both);
        Assert.Contains("987655", both);

        var after = await Library.GetItemAsync(item.Id);
        Assert.NotNull(after);
        Assert.Empty(after.Tags);
        Assert.Null(after.StarredAt);
        Assert.True(File.Exists(item.FullPath));
        Assert.Empty(RecycleBin.Recycled);
        Assert.Empty(Changes.Reported);

        // The log names the files that do exist, and how many were named.
        var logged = Log.Entries[0].Details;
        Assert.False(logged.Ok);
        Assert.Equal(AiErrorCodes.NotFound, logged.ErrorCode);
        Assert.Equal([new AiActivityRef(item.Id, "keep.pdf")], logged.Files);
        Assert.Equal(2, logged.FileCount);
    }

    [Fact]
    public async Task An_unknown_category_id_fails_and_changes_nothing()
    {
        var docs = await BuiltInAsync("documents");
        var item = await AddAsync(docs.Id, "keep.pdf");
        var source = SourceFile("new.txt");

        Failed(await CallAsync(AiTools.AddToCategory, new FilesCategoryArgs([item.Id], 424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([item.Id], docs.Id, 424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.MoveFiles, new MoveFilesArgs([item.Id], 424242, docs.Id)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.AddFiles, new AddFilesArgs([source], 424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("text", 424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.DeleteCategory, new DeleteCategoryArgs(424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.RenameCategory, new RenameCategoryArgs(424242, "New")), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.CreateCategory, new CreateCategoryArgs("Child", 424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.ListFiles, new ListFilesArgs(424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.ListCategories, new ListCategoriesArgs(424242)), AiErrorCodes.NotFound);
        Failed(await CallAsync(AiTools.ShowInCabinette, new ShowArgs(CategoryId: 424242)), AiErrorCodes.NotFound);

        Assert.Equal([docs.Id], (await Library.GetItemAsync(item.Id))!.CategoryIds);
        Assert.Empty((await Library.SearchAsync("new")).Items);
        Assert.Empty(Changes.Reported);
        Assert.Empty(Host.Shown);
    }

    // ------------------------------------------------------------------ client names

    [Theory]
    [InlineData("Claude Code", "Claude Code")]
    [InlineData("  Codex  ", "Codex")]
    [InlineData("", "AI client")]
    [InlineData(null, "AI client")]
    [InlineData("\u0007\u202e", "AI client")]
    [InlineData("Evil\r\nClient\u202e", "EvilClient")]
    public async Task Client_names_are_cleaned(string? name, string expected)
    {
        await CallAsync(AiTools.ListTags, new ListTagsArgs(), client: name);

        Assert.Equal(expected, Log.Last.Client);
    }

    [Fact]
    public async Task Client_names_are_at_most_64_characters()
    {
        await CallAsync(AiTools.ListTags, new ListTagsArgs(), client: new string('x', 70) + "😀");
        await CallAsync(AiTools.ListTags, new ListTagsArgs(), client: new string('y', 63) + "😀");

        Assert.Equal(new string('x', 64), Log.Entries[0].Client);
        Assert.Equal(new string('y', 63), Log.Entries[1].Client); // a character is never cut in half
    }

    [Fact]
    public async Task The_client_name_goes_into_the_change_reports()
    {
        var notes = await BuiltInAsync("notes");

        Ok<FileResult>(await CallAsync(AiTools.CreateNote, new CreateNoteArgs("hi", notes.Id), client: "Codex"));

        Assert.Equal("Codex", Assert.Single(Changes.Reported).Client);
    }

    // ------------------------------------------------------------------ busy, not ready

    [Fact]
    public async Task While_a_vault_operation_runs_changes_are_busy_but_reading_and_show_work()
    {
        var notes = await BuiltInAsync("notes");
        var item = await AddAsync(notes.Id, "a.txt");
        Host.IsOperationRunning = true;

        foreach (var (tool, args) in new (string, object)[]
                 {
                     (AiTools.CreateNote, new CreateNoteArgs("x", notes.Id)),
                     (AiTools.DeleteFiles, new FileIdsArgs([item.Id])),
                     (AiTools.StarFiles, new FileIdsArgs([item.Id])),
                     (AiTools.CreateCategory, new CreateCategoryArgs("New")),
                     (AiTools.CreateBackup, new CreateBackupArgs(@"C:\backups\b.zip")),
                     (AiTools.MigratePointers, new MigratePointersArgs()),
                 })
        {
            Failed(await CallAsync(tool, args), AiErrorCodes.Busy);
        }

        Ok<ListTagsResult>(await CallAsync(AiTools.ListTags, new ListTagsArgs()));
        Ok<GetFilesResult>(await CallAsync(AiTools.GetFiles, new GetFilesArgs([item.Id])));
        Ok<ShowResult>(await CallAsync(AiTools.ShowInCabinette, new ShowArgs(FileId: item.Id)));
        Assert.True(File.Exists(item.FullPath));
        Assert.Null((await Library.GetItemAsync(item.Id))!.StarredAt);
        Assert.Empty(VaultOperations.Calls);
        Assert.Empty(Changes.Reported);
        Assert.Equal(6, Log.Entries.Count(e => e.Details.ErrorCode == AiErrorCodes.Busy));
    }

    [Fact]
    public async Task While_quitting_calls_are_not_ready_and_not_logged()
    {
        Host.IsQuitting = true;

        var reply = await CallAsync(AiTools.ListTags, new ListTagsArgs());

        Assert.Equal("Cabinette is closing.", Failed(reply, AiErrorCodes.NotReady));
        Assert.Empty(Log.Entries);
    }

    [Fact]
    public async Task The_gate_answers_not_ready_until_it_opens_and_after_it_closes()
    {
        var gate = new AiCommandGate();
        var message = Wire(AiTools.ListTags, new ListTagsArgs());

        Assert.Equal("Cabinette is starting. Try again in a moment.", Failed(await gate.HandleAsync(message), AiErrorCodes.NotReady));

        gate.WaitForFirstRun();
        Assert.Equal("Cabinette needs its first-run setup: choose a vault folder in the Cabinette window.",
            Failed(await gate.HandleAsync(message), AiErrorCodes.NotReady));

        gate.WaitForStartup();
        Failed(await gate.HandleAsync(Wire(AiTools.ListTags, new ListTagsArgs(), version: 99)), AiErrorCodes.Unsupported);
        Assert.False(gate.IsOpen);

        gate.Open(Handler);
        Assert.True(gate.IsOpen);
        Ok<ListTagsResult>(await gate.HandleAsync(message));

        gate.Close();
        Assert.Equal("Cabinette is closing.", Failed(await gate.HandleAsync(message), AiErrorCodes.NotReady));

        // Once closing, it stays closed.
        gate.WaitForStartup();
        gate.Open(Handler);
        Assert.False(gate.IsOpen);
        Assert.Equal("Cabinette is closing.", Failed(await gate.HandleAsync(message), AiErrorCodes.NotReady));
        Assert.Single(Log.Entries); // only the call that reached the handler
    }

    // ------------------------------------------------------------------ error codes

    [Theory]
    [InlineData(LibraryErrorCode.RecycleBinUnavailable, AiErrorCodes.RecycleBinUnavailable)]
    [InlineData(LibraryErrorCode.NotFound, AiErrorCodes.NotFound)]
    [InlineData(LibraryErrorCode.InvalidName, AiErrorCodes.Refused)]
    [InlineData(LibraryErrorCode.NameAlreadyExists, AiErrorCodes.Refused)]
    [InlineData(LibraryErrorCode.FileInUse, AiErrorCodes.Refused)]
    [InlineData(LibraryErrorCode.AccessDenied, AiErrorCodes.Refused)]
    [InlineData(LibraryErrorCode.NotAllowed, AiErrorCodes.Refused)]
    [InlineData(LibraryErrorCode.VaultNotConfigured, AiErrorCodes.NotReady)]
    [InlineData(LibraryErrorCode.Unknown, AiErrorCodes.Failed)]
    public void Library_errors_map_to_ai_error_codes(LibraryErrorCode code, string expected)
    {
        Assert.Equal(expected, AiCommandHandler.ErrorCodeFor(code));
    }

    [Fact]
    public async Task A_library_refusal_comes_back_as_refused_with_its_message()
    {
        await Library.CreateCategoryAsync("Work", null);

        var reply = await CallAsync(AiTools.CreateCategory, new CreateCategoryArgs("work"));

        Assert.Equal("A category named \"work\" already exists here.", Failed(reply, AiErrorCodes.Refused));
        Assert.Equal("A category named \"work\" already exists here.", Log.Last.Details.Error);
        Assert.Equal("work", Log.Last.Details.NewName);
    }

    [Fact]
    public async Task A_closed_library_is_not_ready()
    {
        await Library.DisposeAsync();

        var reply = await CallAsync(AiTools.ListTags, new ListTagsArgs());

        Failed(reply, AiErrorCodes.NotReady);
    }
}
