using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinette.Ipc;
using Cabinette.Mcp.Tools;

namespace Cabinette.Mcp.Tests;

/// <summary>Every tool's MCP arguments become the right "ai" message (tool, client, version, args record) on the pipe.</summary>
public sealed class ToolMappingTests
{
    public static TheoryData<string, string, object> Cases => new()
    {
        {
            AiTools.Search,
            """{"query":"invoice #tax","filter":{"types":["documents"],"extensions":[".pdf"],"mode":"copy","missingOnly":true,"addedFrom":"2026-01-01T00:00:00+00:00","addedTo":"2026-10-01T12:00:00+02:00","minSizeBytes":10,"maxSizeBytes":5000},"limit":50}""",
            new SearchArgs("invoice #tax", new AiFilter
            {
                Types = ["documents"],
                Extensions = [".pdf"],
                Mode = "copy",
                MissingOnly = true,
                AddedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                AddedTo = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)),
                MinSizeBytes = 10,
                MaxSizeBytes = 5000,
            }, 50)
        },
        { AiTools.ListCategories, """{"parentId":7}""", new ListCategoriesArgs(7) },
        { AiTools.ListFiles, """{"categoryId":3,"starred":false,"query":"report","offset":100,"limit":20}""", new ListFilesArgs(3, false, "report", null, 100, 20) },
        { AiTools.GetFiles, """{"fileIds":[1,2,3]}""", new GetFilesArgs([1, 2, 3]) },
        { AiTools.ReadFile, """{"fileId":42,"offset":262144}""", new ReadFileArgs(42, 262144) },
        { AiTools.ListTags, "{}", new ListTagsArgs() },
        { AiTools.GetOperationStatus, """{"operationId":"op-1"}""", new GetOperationStatusArgs("op-1") },
        {
            AiTools.AddFiles,
            """{"paths":["C:\\Data\\a.txt","docs\\b.pdf","..\\c.png","\\\\server\\share\\d.txt","~\\e.txt"],"categoryId":5,"mode":"pointer"}""",
            new AddFilesArgs([@"C:\Data\a.txt", @"C:\Work\Project\docs\b.pdf", @"C:\Work\c.png", @"\\server\share\d.txt", @"C:\Work\Project\~\e.txt"], 5, "pointer")
        },
        { AiTools.CreateNote, """{"text":"# Hi\n\nSome *Markdown*.","categoryId":9}""", new CreateNoteArgs("# Hi\n\nSome *Markdown*.", 9) },
        { AiTools.TagFiles, """{"fileIds":[1],"tags":["tax","2026"]}""", new TagFilesArgs([1], ["tax", "2026"]) },
        { AiTools.UntagFiles, """{"fileIds":[1,2],"tags":["old"]}""", new TagFilesArgs([1, 2], ["old"]) },
        { AiTools.AddToCategory, """{"fileIds":[1,2],"categoryId":4}""", new FilesCategoryArgs([1, 2], 4) },
        { AiTools.RemoveFromCategory, """{"fileIds":[3],"categoryId":4}""", new FilesCategoryArgs([3], 4) },
        { AiTools.MoveFiles, """{"fileIds":[1],"fromCategoryId":2,"toCategoryId":3}""", new MoveFilesArgs([1], 2, 3) },
        { AiTools.RenameFile, """{"fileId":1,"newName":"report 2026.pdf"}""", new RenameFileArgs(1, "report 2026.pdf") },
        { AiTools.DeleteFiles, """{"fileIds":[5,6]}""", new FileIdsArgs([5, 6]) },
        { AiTools.StarFiles, """{"fileIds":[7]}""", new FileIdsArgs([7]) },
        { AiTools.UnstarFiles, """{"fileIds":[7,8]}""", new FileIdsArgs([7, 8]) },
        { AiTools.CreateCategory, """{"name":"Taxes","parentId":2}""", new CreateCategoryArgs("Taxes", 2) },
        { AiTools.RenameCategory, """{"categoryId":2,"newName":"Old taxes"}""", new RenameCategoryArgs(2, "Old taxes") },
        { AiTools.DeleteCategory, """{"categoryId":2}""", new DeleteCategoryArgs(2) },
        { AiTools.MoveVault, """{"newPath":"vault2"}""", new MoveVaultArgs(@"C:\Work\Project\vault2") },
        { AiTools.MigratePointers, "{}", new MigratePointersArgs() },
        { AiTools.CreateBackup, """{"zipPath":"D:\\Backups\\cabinette.zip"}""", new CreateBackupArgs(@"D:\Backups\cabinette.zip") },
        { AiTools.ShowInCabinette, """{"fileId":12}""", new ShowArgs(12) },
    };

    [Fact]
    public void EveryToolHasAMappingCase()
    {
        var covered = Cases.Select(row => (string)row[0]).ToHashSet();
        Assert.Equal(AiTools.All.OrderBy(t => t), covered.OrderBy(t => t));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ArgumentsBecomeTheArgsRecordOnThePipe(string tool, string argumentsJson, object expected)
    {
        await using var cabinette = new FakeCabinette();
        var result = await TestTools.Tool(tool, cabinette).CallAsync("Claude Code", TestTools.Args(argumentsJson), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        var message = Assert.Single(cabinette.Messages);
        Assert.Equal(InstanceCommands.Ai, message.Command);
        Assert.Equal(AiProtocol.Version, message.Version);
        Assert.Equal("Claude Code", message.Client);
        Assert.Equal(tool, message.Tool);

        var expectedJson = JsonSerializer.Serialize(expected, expected.GetType(), AiProtocol.JsonOptions);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(cabinette.LastArgsJson())),
            $"expected {expectedJson}\nactual   {cabinette.LastArgsJson()}");

        // What Cabinette reads back is the same record.
        var roundTrip = message.Args!.Value.Deserialize(expected.GetType(), AiProtocol.JsonOptions);
        Assert.Equal(expectedJson, JsonSerializer.Serialize(roundTrip, expected.GetType(), AiProtocol.JsonOptions));
    }

    [Fact]
    public async Task OmittedAndNullOptionalParametersTakeTheRecordDefaults()
    {
        await using var cabinette = new FakeCabinette();
        await TestTools.Tool(AiTools.ListFiles, cabinette).CallAsync("Codex", TestTools.Args("""{"categoryId":3,"query":null,"filter":null}"""), CancellationToken.None);

        Assert.Equal(TestTools.ArgsJson(new ListFilesArgs(3)), cabinette.LastArgsJson());
    }

    [Fact]
    public async Task ParameterNamesAreMatchedWithoutCase()
    {
        await using var cabinette = new FakeCabinette();
        var result = await TestTools.Tool(AiTools.MoveFiles, cabinette)
            .CallAsync("Codex", TestTools.Args("""{"FileIds":[1],"FROMCATEGORYID":2,"toCategoryId":3}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(TestTools.ArgsJson(new MoveFilesArgs([1], 2, 3)), cabinette.LastArgsJson());
    }

    [Theory]
    [InlineData(AiTools.DeleteFiles, """{"fileIds":[1],"permanent":true}""", "unknown parameter \"permanent\"")]
    [InlineData(AiTools.AddFiles, """{"paths":["C:\\a.txt"],"category_id":5}""", "unknown parameter \"category_id\"")]
    [InlineData(AiTools.Search, """{"query":"x","filter":{"type":["images"]}}""", "unknown parameter \"filter.type\"")]
    [InlineData(AiTools.ListTags, """{"all":true}""", "it takes no parameters")]
    [InlineData(AiTools.DeleteFiles, "{}", "\"fileIds\" is required")]
    [InlineData(AiTools.MoveFiles, """{"fileIds":[1],"fromCategoryId":2,"toCategoryId":null}""", "\"toCategoryId\" is required")]
    [InlineData(AiTools.GetFiles, """{"fileIds":["one"]}""", "\"fileIds[0]\" must be an integer")]
    [InlineData(AiTools.ReadFile, """{"fileId":true}""", "\"fileId\" must be an integer")]
    [InlineData(AiTools.Search, """{"query":"x","filter":{"addedFrom":"last week"}}""", "\"filter.addedFrom\" must be a date and time")]
    [InlineData(AiTools.TagFiles, """{"fileIds":7,"tags":["a"]}""", "\"fileIds\" must be a list of integers")]
    public async Task BadArgumentsAreRefusedWithoutCallingCabinette(string tool, string argumentsJson, string expected)
    {
        await using var cabinette = new FakeCabinette();
        var result = await TestTools.Tool(tool, cabinette).CallAsync("Codex", TestTools.Args(argumentsJson), CancellationToken.None);

        Assert.True(result.IsError);
        var text = TestTools.Text(result);
        Assert.StartsWith("invalid_argument: ", text);
        Assert.Contains(expected, text);
        Assert.Empty(cabinette.Messages);
        Assert.Equal(0, cabinette.ConnectionCount);
    }

    [Theory]
    [InlineData(@"C:\Data\a.txt", @"C:\Data\a.txt")]
    [InlineData(@"\\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"a.txt", @"C:\Work\Project\a.txt")]
    [InlineData(@".\sub\a.txt", @"C:\Work\Project\sub\a.txt")]
    [InlineData(@"..\a.txt", @"C:\Work\a.txt")]
    [InlineData(@"sub/a.txt", @"C:\Work\Project\sub\a.txt")]
    [InlineData(@"~\a.txt", @"C:\Work\Project\~\a.txt")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void RelativePathsAreResolvedAgainstTheWorkingFolder(string path, string expected)
        => Assert.Equal(expected, ToolArguments.ResolvePath(path, TestTools.WorkingDirectory));
}
