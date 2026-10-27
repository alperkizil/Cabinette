using System.Globalization;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>The activity log's sentences (Settings › AI), from the details JSON the "ai" handler writes.</summary>
public sealed class AiActivityTextTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public AiActivityTextTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static readonly AiActivityRef A = new(1, "a.pdf");
    private static readonly AiActivityRef B = new(2, "b.pdf");
    private static readonly AiActivityRef C = new(3, "c.pdf");
    private static readonly AiActivityRef Images = new(10, "Images");
    private static readonly AiActivityRef Work = new(11, "Work");

    /// <summary>Through JSON, as the log stores it.</summary>
    private static AiActivityDescription Describe(string tool, AiActivityDetails details)
        => AiActivityText.Describe(new ActivityEntry { Source = "Claude Code", Action = tool, DetailsJson = details.ToJson() });

    private static string Sentence(string tool, AiActivityDetails details) => Describe(tool, details).Sentence;

    [Fact]
    public void Search()
    {
        Assert.Equal("Searched for “invoice” (12 results)", Sentence(AiTools.Search, new() { Query = "invoice", FileCount = 12 }));
        Assert.Equal("Searched for “invoice” (1 result)", Sentence(AiTools.Search, new() { Query = "invoice", FileCount = 1 }));
        Assert.Equal("Searched for “x” (no results)", Sentence(AiTools.Search, new() { Query = " x ", FileCount = 0 }));
        Assert.Equal("Searched for “invoice”", Sentence(AiTools.Search, new() { Query = "invoice" }));
        Assert.Equal("Searched (3 results)", Sentence(AiTools.Search, new() { FileCount = 3 }));
    }

    [Fact]
    public void Listings()
    {
        Assert.Equal("Listed the categories", Sentence(AiTools.ListCategories, new() { FileCount = 21 }));
        Assert.Equal("Listed the categories in Work", Sentence(AiTools.ListCategories, new() { Categories = [Work] }));
        Assert.Equal("Listed the files in Work (40 files)", Sentence(AiTools.ListFiles, new() { Categories = [Work], FileCount = 40 }));
        Assert.Equal("Listed files matching “tax” (1 file)", Sentence(AiTools.ListFiles, new() { Query = "tax", FileCount = 1 }));
        Assert.Equal("Listed the tags", Sentence(AiTools.ListTags, new()));
        Assert.Equal("Checked on a vault operation", Sentence(AiTools.GetOperationStatus, new() { OperationId = "op-1" }));
    }

    [Fact]
    public void Reading_and_looking_up()
    {
        Assert.Equal("Read report.pdf", Sentence(AiTools.ReadFile, new() { Files = [new(5, "report.pdf")], FileCount = 1, Offset = 0 }));
        Assert.Equal("Read more of report.pdf", Sentence(AiTools.ReadFile, new() { Files = [new(5, "report.pdf")], Offset = 262144 }));
        Assert.Equal("Looked up 2 files: a.pdf, b.pdf", Sentence(AiTools.GetFiles, new() { Files = [A, B], FileCount = 2 }));
        Assert.Equal("Looked up a.pdf", Sentence(AiTools.GetFiles, new() { Files = [A], FileCount = 1 }));
    }

    [Fact]
    public void Deletes_name_the_files_as_plain_text()
    {
        var description = Describe(AiTools.DeleteFiles, new() { Files = [A, B, C], FileCount = 3 });

        Assert.Equal("Deleted 3 files: a.pdf, b.pdf, c.pdf", description.Sentence);
        Assert.DoesNotContain(description.Segments, s => s.IsLink);
        Assert.False(description.Failed);
        Assert.Null(description.FailureText);
        Assert.Equal("Deleted a.pdf", Sentence(AiTools.DeleteFiles, new() { Files = [A], FileCount = 1 }));
    }

    [Fact]
    public void Many_files_are_cut_after_five_names()
    {
        var files = Enumerable.Range(1, 50).Select(i => new AiActivityRef(i, $"f{i}.txt")).ToList();

        Assert.Equal(
            "Starred 120 files: f1.txt, f2.txt, f3.txt, f4.txt, f5.txt and 115 more",
            Sentence(AiTools.StarFiles, new() { Files = files, FileCount = 120 }));
    }

    [Fact]
    public void Adds()
    {
        var added = Describe(AiTools.AddFiles, new() { Files = [A, B], FileCount = 2, Categories = [Images], Mode = "copy" });

        Assert.Equal("Added 2 files to Images: a.pdf, b.pdf", added.Sentence);
        Assert.Equal(
            [
                new AiActivitySegment("Added 2 files to "), new AiActivitySegment("Images", AiActivityLinkKind.Category, 10),
                new AiActivitySegment(": "), new AiActivitySegment("a.pdf", AiActivityLinkKind.File, 1),
                new AiActivitySegment(", "), new AiActivitySegment("b.pdf", AiActivityLinkKind.File, 2),
            ],
            added.Segments);
        Assert.Equal("Added a.pdf to Images as links", Sentence(AiTools.AddFiles, new() { Files = [A], FileCount = 1, Categories = [Images], Mode = "pointer" }));
        Assert.Equal("Added 3 files", Sentence(AiTools.AddFiles, new() { FileCount = 3, Paths = ["C:\\x"] }));
        Assert.Equal("Added no files", Sentence(AiTools.AddFiles, new() { FileCount = 0 }));
        Assert.Equal("Created a note in Notes", Sentence(AiTools.CreateNote, new() { Files = [A], Categories = [new(12, "Notes")] }));
        Assert.Equal("Created a note", Sentence(AiTools.CreateNote, new()));
    }

    [Fact]
    public void Tags_stars_and_categories_of_files()
    {
        Assert.Equal("Tagged 2 files with #paid, #2026: a.pdf, b.pdf", Sentence(AiTools.TagFiles, new() { Files = [A, B], FileCount = 2, Tags = ["paid", "#2026"] }));
        Assert.Equal("Removed #draft from a.pdf", Sentence(AiTools.UntagFiles, new() { Files = [A], FileCount = 1, Tags = ["draft"] }));
        Assert.Equal("Removed tags from a.pdf", Sentence(AiTools.UntagFiles, new() { Files = [A], FileCount = 1 }));
        Assert.Equal("Unstarred a.pdf", Sentence(AiTools.UnstarFiles, new() { Files = [A], FileCount = 1 }));
        Assert.Equal("Also added 2 files to Work: a.pdf, b.pdf", Sentence(AiTools.AddToCategory, new() { Files = [A, B], FileCount = 2, Categories = [Work] }));
        Assert.Equal("Removed a.pdf from Work", Sentence(AiTools.RemoveFromCategory, new() { Files = [A], FileCount = 1, Categories = [Work] }));
        Assert.Equal("Moved 2 files to Work: a.pdf, b.pdf", Sentence(AiTools.MoveFiles, new() { Files = [A, B], FileCount = 2, Categories = [Work, Images] }));
    }

    [Fact]
    public void Renames()
    {
        Assert.Equal("Renamed a.pdf to “x.pdf”", Sentence(AiTools.RenameFile, new() { Files = [A], FileCount = 1, NewName = "x.pdf" }));
        Assert.Equal("Renamed a file to x.pdf", Sentence(AiTools.RenameFile, new() { Files = [new(1, "x.pdf")], NewName = "x.pdf" }));
        Assert.Equal("Renamed the category Work to “Jobs”", Sentence(AiTools.RenameCategory, new() { Categories = [Work], NewName = "Jobs" }));
        Assert.Equal("Renamed a category to Jobs", Sentence(AiTools.RenameCategory, new() { NewName = "Jobs" }));
    }

    [Fact]
    public void Categories()
    {
        var created = Describe(AiTools.CreateCategory, new() { Categories = [Work], NewName = "Work" });
        Assert.Equal("Created the category Work", created.Sentence);
        Assert.Contains(created.Segments, s => s is { Link: AiActivityLinkKind.Category, Id: 11 });

        var deleted = Describe(AiTools.DeleteCategory, new() { Categories = [Work], FileCount = 4 });
        Assert.Equal("Deleted the category Work", deleted.Sentence);
        Assert.DoesNotContain(deleted.Segments, s => s.IsLink);
    }

    [Fact]
    public void Vault_operations_and_showing()
    {
        Assert.Equal(@"Started moving the vault to D:\Vault", Sentence(AiTools.MoveVault, new() { Paths = [@"D:\Vault"], OperationId = "op-1" }));
        Assert.Equal("Started copying linked files into the vault", Sentence(AiTools.MigratePointers, new() { OperationId = "op-2" }));
        Assert.Equal(@"Started a backup to D:\b.zip", Sentence(AiTools.CreateBackup, new() { Paths = [@"D:\b.zip"] }));
        Assert.Equal("Showed a.pdf in Cabinette", Sentence(AiTools.ShowInCabinette, new() { Files = [A] }));
        Assert.Equal("Showed Work in Cabinette", Sentence(AiTools.ShowInCabinette, new() { Categories = [Work] }));
        Assert.Equal("Opened Cabinette", Sentence(AiTools.ShowInCabinette, new()));
    }

    [Fact]
    public void A_failed_call_names_the_tool_its_targets_and_the_error()
    {
        var failed = Describe(AiTools.DeleteFiles, new()
        {
            Ok = false,
            ErrorCode = AiErrorCodes.RecycleBinUnavailable,
            Error = "“a.pdf” is on a drive without a Recycle Bin.",
            Files = [A, B],
            FileCount = 2,
        });

        Assert.True(failed.Failed);
        Assert.Equal("Delete files: a.pdf, b.pdf", failed.Sentence);
        Assert.Equal("Failed: “a.pdf” is on a drive without a Recycle Bin.", failed.FailureText);
        // The files are still there: their names open them.
        Assert.Equal(2, failed.Segments.Count(s => s.Link == AiActivityLinkKind.File));

        Assert.Equal("Search: “x”", Sentence(AiTools.Search, new() { Ok = false, Query = "x" }));
        Assert.Equal(@"Move vault: D:\Vault", Sentence(AiTools.MoveVault, new() { Ok = false, Paths = [@"D:\Vault"], Error = "Busy." }));
        Assert.Equal("Failed", Describe(AiTools.CreateCategory, new() { Ok = false }).FailureText);
    }

    [Fact]
    public void Every_tool_has_a_name_and_a_sentence()
    {
        foreach (var tool in AiTools.All)
        {
            Assert.DoesNotContain("[", AiActivityText.ToolName(tool), StringComparison.Ordinal);
            foreach (var details in new AiActivityDetails[] { new(), new() { Files = [A, B], FileCount = 2, Categories = [Work], Query = "q", Paths = ["C:\\p"], NewName = "n", Tags = ["t"] }, new() { Ok = false } })
            {
                var sentence = Sentence(tool, details);
                Assert.False(string.IsNullOrWhiteSpace(sentence));
                Assert.DoesNotContain("[AiActivity_", sentence, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Missing_or_unreadable_details_show_the_tools_name()
    {
        foreach (var json in new string?[] { null, "", "not json", "[1,2]", "{\"files\": 3}" })
        {
            var description = AiActivityText.Describe(new ActivityEntry { Source = "Codex", Action = AiTools.DeleteFiles, DetailsJson = json });

            Assert.Equal("Delete files", description.Sentence);
            Assert.False(description.Failed);
            Assert.DoesNotContain(description.Segments, s => s.IsLink);
        }

        Assert.Equal("future_tool", AiActivityText.Describe(new ActivityEntry { Source = "Codex", Action = "future_tool", DetailsJson = "{}" }).Sentence);
    }

    [Fact]
    public void Details_from_another_version_with_extra_fields_still_read()
    {
        const string json = """{"ok":true,"files":[{"id":4,"name":"x.txt"}],"fileCount":1,"somethingNew":{"a":1}}""";

        Assert.Equal("Read x.txt", AiActivityText.Describe(new ActivityEntry { Source = "Codex", Action = AiTools.ReadFile, DetailsJson = json }).Sentence);
    }
}
