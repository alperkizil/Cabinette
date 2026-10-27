using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinette.Ipc;
using Cabinette.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace Cabinette.Mcp.Tests;

/// <summary>Cabinette's replies become what the model reads: JSON text, text pages, images and errors.</summary>
public sealed class ResultMappingTests
{
    private static readonly AiFile SampleFile = new()
    {
        Id = 42,
        Name = "Ödev – ünite 3 <draft>.py",
        Path = @"C:\Vault\Code\Ödev – ünite 3 <draft>.py",
        Type = "code.python",
        SizeBytes = 120,
        AddedBy = "ai",
        HomeCategoryId = 9,
        CategoryIds = [9],
    };

    [Fact]
    public void AResultBecomesItsRecordAsReadableJsonText()
    {
        var result = ToolResults.FromReply(AiTools.Search, InstanceReply.WithResult(new SearchResult([], [SampleFile], HasMore: true)));

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("\"name\":\"Ödev – ünite 3 <draft>.py\"", text); // not \u00D6dev ... \u003C
        Assert.Contains("\"hasMore\":true", text);
        var parsed = JsonSerializer.Deserialize<SearchResult>(text, AiProtocol.JsonOptions)!;
        Assert.Equal(42, Assert.Single(parsed.Files).Id);
    }

    [Fact]
    public void ATextPageComesAsMetadataThenTheTextItself()
    {
        const string code = "def main():\n    print(\"hi\\n\")  # \"quoted\"\n";
        var page = new ReadFileResult
        {
            Kind = "text",
            File = SampleFile,
            Text = code,
            Offset = 0,
            NextOffset = 262144,
            Total = 600000,
            Unit = "bytes",
            Encoding = "utf-8",
            Source = "file",
        };

        var result = ToolResults.FromReply(AiTools.ReadFile, InstanceReply.WithResult(page));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.Content.Count);
        Assert.Equal(code, TestTools.Text(result, 1)); // raw, not JSON-escaped
        var metadata = JsonNode.Parse(TestTools.Text(result, 0))!.AsObject();
        Assert.Null(metadata["text"]);
        Assert.Equal(42, (long)metadata["file"]!["id"]!);
        Assert.Equal(0, (long)metadata["offset"]!);
        Assert.Equal(262144, (long)metadata["nextOffset"]!);
        Assert.Equal(600000, (long)metadata["total"]!);
        Assert.Equal("bytes", (string)metadata["unit"]!);
        Assert.Equal("utf-8", (string)metadata["encoding"]!);
    }

    [Fact]
    public void AnEmptyLastPageStillHasBothBlocks()
    {
        var result = ToolResults.FromReply(AiTools.ReadFile, InstanceReply.WithResult(new ReadFileResult { Kind = "text", Text = null, Unit = "chars" }));

        Assert.Equal(2, result.Content.Count);
        Assert.Equal("", TestTools.Text(result, 1));
        Assert.Null(JsonNode.Parse(TestTools.Text(result, 0))!["nextOffset"]);
    }

    [Fact]
    public void AnImageComesAsAnImageBlockPlusMetadataWithoutTheBase64()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var base64 = Convert.ToBase64String(bytes);
        var page = new ReadFileResult
        {
            Kind = "image",
            File = SampleFile,
            ImageBase64 = base64,
            MimeType = "image/png",
            Width = 1568,
            Height = 1000,
            OriginalWidth = 4000,
            OriginalHeight = 2551,
        };

        var result = ToolResults.FromReply(AiTools.ReadFile, InstanceReply.WithResult(page));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.Content.Count);
        var metadataText = TestTools.Text(result, 0);
        Assert.DoesNotContain(base64, metadataText);
        var metadata = JsonNode.Parse(metadataText)!;
        Assert.Null(metadata["imageBase64"]);
        Assert.Equal("image/png", (string)metadata["mimeType"]!);
        Assert.Equal(1568, (int)metadata["width"]!);
        Assert.Equal(4000, (int)metadata["originalWidth"]!);

        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(base64, Encoding.ASCII.GetString(image.Data.Span));
        Assert.Equal(bytes, image.DecodedData.ToArray());
    }

    [Theory]
    [InlineData(null, "image/png")]
    [InlineData("not base64!", "image/png")]
    [InlineData("iVBORw0KGgo=", null)]
    public void AnImageWithoutValidDataIsAnError(string? base64, string? mimeType)
    {
        var result = ToolResults.FromReply(AiTools.ReadFile, InstanceReply.WithResult(new ReadFileResult { Kind = "image", ImageBase64 = base64, MimeType = mimeType }));

        Assert.True(result.IsError);
        Assert.StartsWith("failed: ", TestTools.Text(result));
    }

    [Theory]
    [InlineData(AiErrorCodes.NotReady, "Finish first-run setup in Cabinette's window.")]
    [InlineData(AiErrorCodes.Busy, "A vault operation is running.")]
    [InlineData(AiErrorCodes.NotFound, "No file has id 99.")]
    [InlineData(AiErrorCodes.InvalidArgument, "Paths must be full paths.")]
    [InlineData(AiErrorCodes.Refused, "Videos can't be read.")]
    [InlineData(AiErrorCodes.RecycleBinUnavailable, "\"a.iso\" is too large for the Recycle Bin. Nothing was deleted.")]
    [InlineData(AiErrorCodes.Failed, "Something went wrong.")]
    public void EveryErrorCodeBecomesAToolErrorWithCodeAndMessage(string code, string message)
    {
        var result = ToolResults.FromReply(AiTools.DeleteFiles, InstanceReply.Failed(code, message));

        Assert.True(result.IsError);
        Assert.Equal($"{code}: {message}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void UnsupportedSaysToUpdateBoth()
    {
        var result = ToolResults.FromReply(AiTools.Search, InstanceReply.Failed(AiErrorCodes.Unsupported, "This Cabinette speaks AI protocol 2"));

        Assert.True(result.IsError);
        var text = TestTools.Text(result);
        Assert.StartsWith("unsupported: This Cabinette speaks AI protocol 2.", text);
        Assert.Contains("update both", text);
    }

    [Fact]
    public void ACabinetteWithoutTheAiCommandIsUnsupported()
    {
        // What a Cabinette from before stage 4 answers (InstanceMessageDispatcher).
        var result = ToolResults.FromReply(AiTools.Search, InstanceReply.Failed("unknown command 'ai'"));

        Assert.True(result.IsError);
        Assert.StartsWith("unsupported: unknown command 'ai'.", TestTools.Text(result));
        Assert.Contains("update both", TestTools.Text(result));
    }

    [Fact]
    public void AFailureWithoutCodeOrMessageIsStillReadable()
    {
        var result = ToolResults.FromReply(AiTools.Search, new InstanceReply { Ok = false });

        Assert.True(result.IsError);
        Assert.Equal("failed: (Cabinette gave no details)", TestTools.Text(result));
    }

    [Fact]
    public void SuccessWithoutAResultIsAnError()
    {
        var result = ToolResults.FromReply(AiTools.Search, InstanceReply.Success);

        Assert.True(result.IsError);
        Assert.StartsWith("failed: ", TestTools.Text(result));
    }

    [Fact]
    public async Task CabinetteThatCantBeStartedIsAToolErrorNotAnException()
    {
        var tool = new CabinetteTool(
            ToolCatalog.Find(AiTools.Search)!,
            new CabinetteConnection(FakeCabinette.UniquePipeName(), new RefusingLauncher(), TestTools.FastTimings),
            () => TestTools.WorkingDirectory);

        var result = await tool.CallAsync("Codex", TestTools.Args("""{"query":"x"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("unavailable: test: there is no Cabinette to start", TestTools.Text(result));
    }

    // ------------------------------------------------ nextOffset on the last page (found by a Codex session)

    /// <summary>
    /// The protocol leaves nulls out, so on the last page nextOffset used to be missing although the descriptions of
    /// list_files and read_file say it is null there. The model now reads an explicit null.
    /// </summary>
    [Fact]
    public void TheLastPageOfListFilesHasAnExplicitNullNextOffset()
    {
        var reply = InstanceReply.WithResult(new ListFilesResult([SampleFile], Total: 1, NextOffset: null));
        Assert.DoesNotContain("nextOffset", reply.Result!.Value.GetRawText()); // what Cabinette sends

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(TestTools.Text(ToolResults.FromReply(AiTools.ListFiles, reply))));

        Assert.True(json.ContainsKey(ToolResults.NextOffsetProperty));
        Assert.Null(json[ToolResults.NextOffsetProperty]);
        Assert.Equal(1, (int)json["total"]!);
    }

    [Fact]
    public void AnEarlierPageOfListFilesKeepsItsNextOffset()
    {
        var reply = InstanceReply.WithResult(new ListFilesResult([SampleFile], Total: 300, NextOffset: 100));

        var text = TestTools.Text(ToolResults.FromReply(AiTools.ListFiles, reply));

        Assert.Equal(100, (int)JsonNode.Parse(text)![ToolResults.NextOffsetProperty]!);
        Assert.Contains("\"name\":\"Ödev – ünite 3 <draft>.py\"", text); // still readable, not \u escapes
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image")]
    public void TheLastPageOfReadFileHasAnExplicitNullNextOffsetInItsMetadata(string kind)
    {
        var page = kind == "text"
            ? new ReadFileResult { Kind = "text", Text = "the end", Unit = "bytes", Offset = 262144, Total = 262151 }
            : new ReadFileResult { Kind = "image", ImageBase64 = Convert.ToBase64String([1, 2, 3]), MimeType = "image/png", Width = 1, Height = 1 };

        var metadata = Assert.IsType<JsonObject>(JsonNode.Parse(TestTools.Text(ToolResults.FromReply(AiTools.ReadFile, InstanceReply.WithResult(page)), 0)));

        Assert.True(metadata.ContainsKey(ToolResults.NextOffsetProperty));
        Assert.Null(metadata[ToolResults.NextOffsetProperty]);
        Assert.Equal(kind, (string)metadata["kind"]!);
    }

    [Fact]
    public void TheNextOffsetPropertyIsTheRecordsOwnName()
    {
        Assert.Contains($"\"{ToolResults.NextOffsetProperty}\":5", JsonSerializer.Serialize(new ListFilesResult([], 6, 5), AiProtocol.JsonOptions));
        Assert.Contains($"\"{ToolResults.NextOffsetProperty}\":5", JsonSerializer.Serialize(new ReadFileResult { Kind = "text", NextOffset = 5 }, AiProtocol.JsonOptions));
    }

    [Fact]
    public async Task ErrorRepliesTravelThroughThePipeUnchanged()
    {
        await using var cabinette = new FakeCabinette(reply: _ => InstanceReply.Failed(AiErrorCodes.Busy, "Cabinette is moving its vault."));

        var result = await TestTools.Tool(AiTools.DeleteFiles, cabinette).CallAsync("Codex", TestTools.Args("""{"fileIds":[1]}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("busy: Cabinette is moving its vault.", TestTools.Text(result));
    }
}
