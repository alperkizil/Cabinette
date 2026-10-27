using System.Globalization;
using Cabinette.App.Services.Ai;

namespace Cabinette.App.Tests;

/// <summary>The grouped AI toasts' wording (SPEC.md section 10, "Toasts").</summary>
public sealed class AiChangeTextTests : IDisposable
{
    private const string Claude = "Claude Code";
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public AiChangeTextTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static AiChange Change(AiChangeKind kind, int count = 1, string? category = null, long? categoryId = null, string? detail = null)
        => new(Claude, kind, count) { CategoryName = category, CategoryId = categoryId, Detail = detail };

    private static IReadOnlyList<string> Lines(params AiChange[] changes) => AiChangeText.Build(Claude, changes)!.Lines;

    [Fact]
    public void Files_added_to_one_category_like_the_vault_monitor()
    {
        var message = AiChangeText.Build(Claude, [Change(AiChangeKind.FilesAdded, 3, "Images", 7)]);

        Assert.NotNull(message);
        Assert.Equal("Claude Code", message.Title);
        Assert.Equal(["3 files added to Images"], message.Lines);
        Assert.Equal("Claude Code added 3 files to Images", message.OneLine);
        Assert.Equal(new AiCategoryTarget(7, "Images"), message.Category);
        Assert.False(message.HasProblems);
    }

    [Fact]
    public void One_file_is_singular() => Assert.Equal(["1 file added to Audio"], Lines(Change(AiChangeKind.FilesAdded, 1, "Audio")));

    [Fact]
    public void Merged_adds_to_the_same_category_are_summed()
    {
        var message = AiChangeText.Build(Claude, [Change(AiChangeKind.FilesAdded, 2, "Images"), Change(AiChangeKind.FilesAdded, 1, "images")])!;

        Assert.Equal(["3 files added to Images"], message.Lines);
        Assert.Equal("Images", message.Category?.Name);
    }

    [Fact]
    public void Files_sorted_into_several_categories_get_a_line_each_largest_first()
    {
        var added = new AiChange(Claude, AiChangeKind.FilesAdded, 4)
        {
            AddedByCategory = new Dictionary<string, int> { ["Documents"] = 1, ["Images"] = 3 },
        };

        var message = AiChangeText.Build(Claude, [added])!;

        Assert.Equal(["3 files added to Images", "1 file added to Documents"], message.Lines);
        Assert.Equal("Claude Code added 3 files to Images · added 1 file to Documents", message.OneLine);
        Assert.Null(message.Category);
    }

    [Fact]
    public void Many_categories_are_summed_up_after_the_first_ones()
    {
        var added = new AiChange(Claude, AiChangeKind.FilesAdded, 15)
        {
            AddedByCategory = new Dictionary<string, int> { ["Images"] = 6, ["Audio"] = 4, ["Video"] = 3, ["Code"] = 1, ["Text"] = 1 },
        };

        Assert.Equal(
            ["6 files added to Images", "4 files added to Audio", "3 files added to Video", "2 files added to 2 other categories"],
            Lines(added));
    }

    [Fact]
    public void Adds_without_a_category_duplicates_and_failures()
    {
        var added = new AiChange(Claude, AiChangeKind.FilesAdded, 2) { Duplicates = 1, Failures = 2 };

        var message = AiChangeText.Build(Claude, [added])!;

        Assert.Equal(["2 files added", "1 file was already in Cabinette", "2 files couldn't be added"], message.Lines);
        Assert.Equal("Claude Code added 2 files · found 1 file already in Cabinette · couldn't add 2 files", message.OneLine);
        Assert.True(message.HasProblems);
        Assert.Null(message.Category);
    }

    [Fact]
    public void Several_duplicates_and_one_failure()
        => Assert.Equal(
            ["3 files were already in Cabinette", "1 file couldn't be added"],
            Lines(new AiChange(Claude, AiChangeKind.FilesAdded, 0) { Duplicates = 3, Failures = 1 }));

    [Fact]
    public void Notes()
    {
        Assert.Equal(["Note added to Notes"], Lines(Change(AiChangeKind.NoteCreated, 1, "Notes")));
        Assert.Equal(["2 notes added to Notes"], Lines(Change(AiChangeKind.NoteCreated, 1, "Notes"), Change(AiChangeKind.NoteCreated, 1, "Notes")));
        Assert.Equal(["Note added"], Lines(Change(AiChangeKind.NoteCreated)));
    }

    [Fact]
    public void Tags_and_stars()
    {
        Assert.Equal(["3 files tagged"], Lines(Change(AiChangeKind.FilesTagged, 3)));
        Assert.Equal(["Tags removed from 1 file"], Lines(Change(AiChangeKind.FilesUntagged)));
        Assert.Equal(["1 file starred"], Lines(Change(AiChangeKind.FilesStarred)));
        Assert.Equal(["4 files unstarred"], Lines(Change(AiChangeKind.FilesUnstarred, 4)));
    }

    [Fact]
    public void Categories_of_files()
    {
        Assert.Equal(["2 files also added to Work"], Lines(Change(AiChangeKind.FilesAddedToCategory, 2, "Work")));
        Assert.Equal(["1 file removed from Work"], Lines(Change(AiChangeKind.FilesRemovedFromCategory, 1, "Work")));
        Assert.Equal(["2 files moved to Travel 2026"], Lines(Change(AiChangeKind.FilesMoved, 2, "Travel 2026")));
        Assert.Equal(["1 file moved to another category"], Lines(Change(AiChangeKind.FilesMoved)));
    }

    [Fact]
    public void Renames_name_the_new_name_of_a_single_file()
    {
        Assert.Equal(["1 file renamed to x.pdf"], Lines(Change(AiChangeKind.FileRenamed, detail: "x.pdf")));
        Assert.Equal(["2 files renamed"], Lines(Change(AiChangeKind.FileRenamed, detail: "x.pdf"), Change(AiChangeKind.FileRenamed, detail: "y.pdf")));
    }

    [Fact]
    public void Deletes_say_where_the_files_went()
    {
        var message = AiChangeText.Build(Claude, [Change(AiChangeKind.FilesDeleted, 2)])!;

        Assert.Equal(["2 files deleted (Recycle Bin)"], message.Lines);
        Assert.Equal("Claude Code deleted 2 files (Recycle Bin)", message.OneLine);
    }

    [Fact]
    public void Categories_created_renamed_and_deleted()
    {
        Assert.Equal(["Category Work created"], Lines(Change(AiChangeKind.CategoryCreated, category: "Work", categoryId: 5)));
        Assert.Equal(["Categories A and B created"], Lines(Change(AiChangeKind.CategoryCreated, category: "A"), Change(AiChangeKind.CategoryCreated, category: "B")));
        Assert.Equal(
            ["3 categories created"],
            Lines(Change(AiChangeKind.CategoryCreated, category: "A"), Change(AiChangeKind.CategoryCreated, category: "B"), Change(AiChangeKind.CategoryCreated, category: "C")));
        Assert.Equal(["Category renamed to Jobs"], Lines(Change(AiChangeKind.CategoryRenamed, category: "Jobs", categoryId: 5)));
        Assert.Equal(["Category Old deleted"], Lines(Change(AiChangeKind.CategoryDeleted, category: "Old", categoryId: 6)));
    }

    [Fact]
    public void Vault_operations()
    {
        Assert.Equal(["Vault moved to D:\\Vault"], Lines(Change(AiChangeKind.VaultMoved, detail: "D:\\Vault")));
        Assert.Equal(["5 linked files copied into the vault"], Lines(Change(AiChangeKind.PointersMigrated, 5)));
        Assert.Equal(["No linked files needed copying into the vault"], Lines(Change(AiChangeKind.PointersMigrated, 0)));
        Assert.Equal(["Backup written to D:\\Backups\\b.zip"], Lines(Change(AiChangeKind.BackupCreated, detail: "D:\\Backups\\b.zip")));
    }

    [Fact]
    public void A_failed_vault_operation_comes_first_and_is_a_problem()
    {
        var message = AiChangeText.Build(Claude, [Change(AiChangeKind.FilesTagged, 2), Change(AiChangeKind.OperationFailed, detail: "Not enough space on D:.")])!;

        Assert.Equal(["Vault operation stopped: Not enough space on D:.", "2 files tagged"], message.Lines);
        Assert.Equal("Claude Code couldn't finish a vault operation: Not enough space on D:. · tagged 2 files", message.OneLine);
        Assert.True(message.HasProblems);
    }

    [Fact]
    public void Every_kind_is_worded_with_existing_texts()
    {
        foreach (var kind in Enum.GetValues<AiChangeKind>())
        {
            foreach (var change in new[] { Change(kind, 1, "Images", 3, "detail"), Change(kind, 2), Change(kind, 1) })
            {
                var message = AiChangeText.Build(Claude, [change]);

                Assert.NotNull(message);
                Assert.NotEmpty(message.Lines);
                // L.Get returns "[key]" for a key that isn't in Strings.resx.
                Assert.DoesNotContain(message.Lines, line => line.Contains("[AiNotify_", StringComparison.Ordinal));
                Assert.DoesNotContain("[AiNotify_", message.OneLine, StringComparison.Ordinal);
                Assert.StartsWith("Claude Code ", message.OneLine, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void With_notifications_off_only_failed_operations_are_shown()
    {
        AiChange[] changes = [Change(AiChangeKind.FilesAdded, 3, "Images"), Change(AiChangeKind.OperationFailed, detail: "Cancelled.")];

        var message = AiChangeText.Build(Claude, changes, includeSuccess: false)!;

        Assert.Equal(["Vault operation stopped: Cancelled."], message.Lines);
        Assert.Null(message.Category);
        Assert.Null(AiChangeText.Build(Claude, [Change(AiChangeKind.FilesAdded, 3, "Images")], includeSuccess: false));
    }

    [Fact]
    public void Nothing_to_say_is_no_message()
    {
        Assert.Null(AiChangeText.Build(Claude, []));
        Assert.Null(AiChangeText.Build(Claude, [Change(AiChangeKind.FilesAdded, 0)]));
    }

    [Fact]
    public void Long_batches_are_capped()
    {
        AiChange[] changes =
        [
            Change(AiChangeKind.FilesAdded, 1, "A"), Change(AiChangeKind.NoteCreated, 1, "Notes"), Change(AiChangeKind.FilesTagged),
            Change(AiChangeKind.FilesUntagged), Change(AiChangeKind.FilesStarred), Change(AiChangeKind.FilesUnstarred),
            Change(AiChangeKind.FilesDeleted), Change(AiChangeKind.CategoryCreated, category: "B"),
        ];

        var lines = Lines(changes);

        Assert.Equal(AiChangeText.MaxLines, lines.Count);
        Assert.Equal("…and 3 more", lines[^1]);
    }

    [Fact]
    public void A_missing_client_name_still_reads()
    {
        var message = AiChangeText.Build("  ", [Change(AiChangeKind.FilesDeleted)])!;

        Assert.Equal("An AI app", message.Title);
        Assert.Equal("An AI app deleted 1 file (Recycle Bin)", message.OneLine);
    }

    [Fact]
    public void One_category_is_found_by_id_or_by_name()
    {
        // Files added to Work, then Work renamed to Jobs (the same id), and files deleted (no category).
        AiChange[] changes =
        [
            Change(AiChangeKind.FilesAdded, 2, "Work", 5),
            Change(AiChangeKind.CategoryRenamed, category: "Jobs", categoryId: 5),
            Change(AiChangeKind.NoteCreated, 1, "Jobs", 5),
            Change(AiChangeKind.FilesDeleted, 3),
        ];

        Assert.Equal(new AiCategoryTarget(5, "Jobs"), AiChangeText.SingleCategory(changes));

        // By name only: the first spelling stays.
        Assert.Equal(new AiCategoryTarget(null, "Images"), AiChangeText.SingleCategory([Change(AiChangeKind.FilesAdded, 1, "Images"), Change(AiChangeKind.FilesMoved, 1, "images")]));
    }

    [Fact]
    public void Two_categories_or_an_unnamed_one_offer_none()
    {
        Assert.Null(AiChangeText.SingleCategory([Change(AiChangeKind.FilesAdded, 1, "Images"), Change(AiChangeKind.FilesMoved, 1, "Work")]));

        // Files sorted by type into categories the change doesn't name, plus a note in Notes: not "Notes" alone.
        Assert.Null(AiChangeText.SingleCategory([Change(AiChangeKind.FilesAdded, 3), Change(AiChangeKind.NoteCreated, 1, "Notes")]));

        var partlyNamed = new AiChange(Claude, AiChangeKind.FilesAdded, 3) { AddedByCategory = new Dictionary<string, int> { ["Images"] = 2 } };
        Assert.Null(AiChangeText.SingleCategory([partlyNamed]));
    }

    [Fact]
    public void A_deleted_category_cant_be_opened()
    {
        Assert.Null(AiChangeText.SingleCategory([Change(AiChangeKind.CategoryDeleted, category: "Old", categoryId: 4)]));
        Assert.Null(AiChangeText.SingleCategory([Change(AiChangeKind.FilesMoved, 2, "Old", 4), Change(AiChangeKind.CategoryDeleted, category: "Old", categoryId: 4)]));
    }

    [Fact]
    public void Merging_uses_the_vault_monitors_windows()
    {
        var first = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(first + TimeSpan.FromSeconds(1.5), AiChangeText.FlushAt(first, first));
        Assert.Equal(first + TimeSpan.FromSeconds(4.5), AiChangeText.FlushAt(first, first.AddSeconds(3)));
        Assert.Equal(first + TimeSpan.FromSeconds(5), AiChangeText.FlushAt(first, first.AddSeconds(4)));
    }
}
