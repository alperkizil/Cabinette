using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>"Type categories sort by type" (SPEC.md section 5).</summary>
public sealed class TypeSortingTests
{
    // ------------------------------------------------------------------ the rule

    [Theory]
    [InlineData("images", "png", null)]
    [InlineData("images", ".JPG", null)]
    [InlineData("spreadsheets", "png", "images")]
    [InlineData("spreadsheets", "xlsx", null)]
    [InlineData("code.java", "py", "code.python")]
    [InlineData("code.java", "java", null)]
    [InlineData("code.java", "json", "code")]
    [InlineData("code", "json", null)]
    [InlineData("code", "py", "code.python")]
    [InlineData("other", "png", "images")]
    [InlineData("other", "weird", null)]
    [InlineData("documents", "weird", null)]
    [InlineData("documents", "", null)]
    [InlineData("notes", "md", null)] // Notes keeps text files
    [InlineData("notes", "txt", null)]
    [InlineData("notes", "png", "images")]
    [InlineData("text", "md", null)]
    [InlineData(null, "png", null)]
    public void Destination_FollowsTheExtensionMap(string? sortingKey, string extension, string? expected)
        => Assert.Equal(expected, TypeSorting.Destination(sortingKey, extension));

    [Fact]
    public void OnlyBuiltInTypeCategoriesSort()
    {
        Assert.Equal("images", TypeSorting.SortingKey(CategoryKind.BuiltIn, "images"));
        Assert.Equal("code.java", TypeSorting.SortingKey(CategoryKind.BuiltIn, "code.java"));
        Assert.Equal("other", TypeSorting.SortingKey(CategoryKind.BuiltIn, "other"));
        Assert.Null(TypeSorting.SortingKey(CategoryKind.User, null));
        Assert.Null(TypeSorting.SortingKey(CategoryKind.System, "deleted"));
        Assert.Null(TypeSorting.SortingKey(CategoryKind.BuiltIn, null));
    }

    // ------------------------------------------------------------------ adding

    private static AddRequest Sorted(long targetId, ItemMode mode = ItemMode.Copy)
        => new() { TargetCategoryId = targetId, Mode = mode, SortIntoTypeCategories = true };

    [Fact]
    public async Task KnownTypesGoToTheirCategory_UnknownTypesAndFoldersStay()
    {
        await using var t = await TestLibrary.CreateAsync();
        var sheets = await t.BuiltInAsync("spreadsheets");
        var images = await t.BuiltInAsync("images");
        var python = await t.BuiltInAsync("code.python");
        var photo = t.CreateFile("photo.png", "photo");
        var budget = t.CreateFile("budget.xlsx", "budget");
        var script = t.CreateFile("calc.py", "print(2)");
        var odd = t.CreateFile("data.zzq", "odd");
        t.CreateFile(@"Folder\inner.png", "inner");

        var result = await t.Library.AddPathsAsync([photo, budget, script, odd, Path.Combine(t.Source, "Folder")], Sorted(sheets.Id));

        Assert.Empty(result.Failures);
        Assert.Equal(5, result.Added.Count);
        Assert.Equal([new SortedFile(photo, images.Id), new SortedFile(script, python.Id)], result.SortedByType);
        Assert.Equal([images.Id], result.Added.Single(i => i.FileName == "photo.png").CategoryIds);
        Assert.Equal(t.VaultPath(@"Images\photo.png"), result.Added.Single(i => i.FileName == "photo.png").FullPath);
        Assert.Equal(t.VaultPath(@"Code\Python\calc.py"), result.Added.Single(i => i.FileName == "calc.py").FullPath);
        Assert.Equal([sheets.Id], result.Added.Single(i => i.FileName == "budget.xlsx").CategoryIds);
        Assert.Equal([sheets.Id], result.Added.Single(i => i.FileName == "data.zzq").CategoryIds);

        // The folder became a subcategory of Spreadsheets with its image, as before.
        var folder = Assert.Single(await t.Library.GetCategoriesAsync(sheets.Id, CategorySort.Alphabetical)).Category;
        Assert.Equal("Folder", folder.Name);
        Assert.Equal([folder.Id], result.Added.Single(i => i.FileName == "inner.png").CategoryIds);

        // The batch's album bubbles and the unread state work as before (the user's own add).
        Assert.Single(result.Added.Select(i => i.BatchId).Distinct());
        Assert.Equal(0, (await t.SummaryAsync(images.Id)).UnreadCount);
    }

    [Fact]
    public async Task WithoutTheOption_EverythingStays()
    {
        await using var t = await TestLibrary.CreateAsync();
        var sheets = await t.BuiltInAsync("spreadsheets");
        var photo = t.CreateFile("photo.png", "photo");

        var result = await t.Library.AddPathsAsync([photo], new AddRequest { TargetCategoryId = sheets.Id, Mode = ItemMode.Copy });

        Assert.Empty(result.SortedByType);
        Assert.Equal([sheets.Id], Assert.Single(result.Added).CategoryIds);
    }

    [Fact]
    public async Task UserCategories_AndUserSubcategoriesOfABuiltIn_KeepEverything()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var holidays = await t.Library.CreateCategoryAsync("Holidays", images.Id);

        var toWork = await t.Library.AddPathsAsync([t.CreateFile("report.pdf", "report")], Sorted(work.Id));
        var toHolidays = await t.Library.AddPathsAsync([t.CreateFile("tickets.pdf", "tickets")], Sorted(holidays.Id, ItemMode.Pointer));

        Assert.Empty(toWork.SortedByType);
        Assert.Equal([work.Id], Assert.Single(toWork.Added).CategoryIds);
        Assert.Empty(toHolidays.SortedByType);
        Assert.Equal([holidays.Id], Assert.Single(toHolidays.Added).CategoryIds);
    }

    [Fact]
    public async Task RenamedBuiltInCategories_StillSort()
    {
        await using var t = await TestLibrary.CreateAsync();
        var sheets = await t.BuiltInAsync("spreadsheets");
        var images = await t.BuiltInAsync("images");
        await t.Library.RenameCategoryAsync(sheets.Id, "Tables");
        await t.Library.RenameCategoryAsync(images.Id, "Photos");

        var result = await t.Library.AddPathsAsync([t.CreateFile("pic.jpg", "pic"), t.CreateFile("sheet.csv", "a,b")], Sorted(sheets.Id));

        Assert.Equal(images.Id, Assert.Single(result.SortedByType).CategoryId);
        Assert.Equal(t.VaultPath(@"Photos\pic.jpg"), result.Added.Single(i => i.FileName == "pic.jpg").FullPath);
        Assert.Equal(t.VaultPath(@"Tables\sheet.csv"), result.Added.Single(i => i.FileName == "sheet.csv").FullPath);
    }

    [Fact]
    public async Task ADeletedTypeCategory_IsCreatedAgain()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");
        await t.Library.DeleteCategoryAsync(images.Id, CategoryDeletePolicy.MoveToDeleted);

        var result = await t.Library.AddPathsAsync([t.CreateFile("scan.png", "scan")], Sorted(docs.Id));

        var sorted = Assert.Single(result.SortedByType);
        var recreated = await t.CategoryAsync(sorted.CategoryId);
        Assert.Equal("images", recreated.BuiltInKey);
        Assert.Contains(recreated.Id, result.CreatedCategoryIds);
        Assert.Equal([recreated.Id], Assert.Single(result.Added).CategoryIds);
    }

    [Fact]
    public async Task Duplicates_AndFoundAgain_JoinTheTypeCategory_NotTheTarget()
    {
        await using var t = await TestLibrary.CreateAsync();
        var sheets = await t.BuiltInAsync("spreadsheets");
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var existing = await t.AddOneAsync(work.Id, t.CreateFile(@"old\logo.png", "logo"));
        var missing = await t.AddOneAsync(work.Id, t.CreateFile(@"old\chart.png", "chart"));
        File.Delete(missing.FullPath);

        var result = await t.Library.AddPathsAsync(
            [t.CreateFile(@"new\logo.png", "logo"), t.CreateFile(@"new\chart.png", "chart")], Sorted(sheets.Id));

        Assert.Equal(2, result.SortedByType.Count);
        Assert.All(result.SortedByType, s => Assert.Equal(images.Id, s.CategoryId));
        var duplicate = Assert.Single(result.Duplicates);
        Assert.Equal(existing.Id, duplicate.Existing.Id);
        Assert.True(duplicate.AddedToTargetCategory);
        Assert.Equal(new[] { work.Id, images.Id }.Order(), (await t.ItemAsync(existing.Id)).CategoryIds.Order());
        var relinked = Assert.Single(result.Added);
        Assert.Equal(missing.Id, relinked.Id);
        Assert.Equal(new[] { work.Id, images.Id }.Order(), relinked.CategoryIds.Order());
        Assert.Equal(0, (await t.SummaryAsync(sheets.Id)).ItemCount);
    }

    [Fact]
    public async Task Failures_AreNotListedAsSorted()
    {
        await using var t = await TestLibrary.CreateAsync();
        var sheets = await t.BuiltInAsync("spreadsheets");

        var result = await t.Library.AddPathsAsync([Path.Combine(t.Source, "nowhere.png")], Sorted(sheets.Id));

        Assert.Single(result.Failures);
        Assert.Empty(result.SortedByType);
    }

    // ------------------------------------------------------------------ the setting

    [Fact]
    public async Task Setting_IsOnByDefault_AndStoredLikeTheOthers()
    {
        await using var t = await TestLibrary.CreateAsync();
        Assert.True((await t.Library.GetSettingsAsync()).SortFilesByTypeInTypeCategories);

        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { SortFilesByTypeInTypeCategories = false });
        await t.ReopenAsync();
        Assert.False((await t.Library.GetSettingsAsync()).SortFilesByTypeInTypeCategories);

        // Settings saved before the option existed don't have it: it is on.
        await t.Library.DisposeAsync();
        using (var connection = new Database(t.DataDirectory).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE settings SET value = json_remove(value, '$.SortFilesByTypeInTypeCategories') WHERE key = 'app_settings'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        await t.OpenAsync();
        Assert.True((await t.Library.GetSettingsAsync()).SortFilesByTypeInTypeCategories);
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }
}
