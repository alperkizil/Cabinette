using Cabinette.Core.Categorization;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>
/// The built-in subcategories added on 2026-10-24 (SPEC.md Appendix A): Documents › PDF and Word, Executable › MSI and Exe,
/// E-books › EPUB and PDF. New libraries seed them; existing ones get them once at their next open; files stay where they are.
/// </summary>
public sealed class LaterBuiltInsTests
{
    private static readonly string[] Expected =
        ["documents.pdf", "documents.word", "executable.msi", "executable.exe", "ebooks.epub", "ebooks.pdf"];

    [Fact]
    public async Task ANewLibrary_HasAllSix_UnderTheirParents()
    {
        await using var t = await TestLibrary.CreateAsync();
        var all = await t.Library.GetAllCategoriesAsync();

        foreach (var key in Expected)
        {
            var category = Assert.Single(all, c => c.BuiltInKey == key);
            var parentKey = ExtensionMap.GetDefinition(key)!.ParentKey;
            Assert.Equal(parentKey, all.Single(c => c.Id == category.ParentId).BuiltInKey);
            Assert.Equal(CategoryKind.BuiltIn, category.Kind);
        }

        Assert.Equal(["PDF", "Word"], ChildNames(all, "documents"));
        Assert.Equal(["Exe", "MSI"], ChildNames(all, "executable"));
        Assert.Equal(["EPUB", "PDF"], ChildNames(all, "ebooks"));
    }

    [Fact]
    public async Task AnExistingLibrary_GetsThemOnce_AndItsFilesStayWhereTheyAre()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { SortFilesByTypeInTypeCategories = false });
        var docs = await t.BuiltInAsync("documents");
        var oldPdf = await t.AddOneAsync(docs.Id, t.CreateFile("old.pdf", "old pdf"));
        await MakeItAnOlderLibraryAsync(t);

        await t.OpenAsync();

        var all = await t.Library.GetAllCategoriesAsync();
        Assert.All(Expected, key => Assert.Single(all, c => c.BuiltInKey == key));
        var kept = await t.ItemAsync(oldPdf.Id);
        Assert.Equal([docs.Id], kept.CategoryIds);
        Assert.Equal(oldPdf.FullPath, kept.FullPath);
        Assert.True(File.Exists(kept.FullPath));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Documents", "PDF")));

        await t.ReopenAsync();
        Assert.Equal(all.Count, (await t.Library.GetAllCategoriesAsync()).Count);
    }

    [Fact]
    public async Task AParentYouDeleted_DoesNotComeBack()
    {
        await using var t = await TestLibrary.CreateAsync();
        var executable = await t.BuiltInAsync("executable");
        await t.Library.DeleteCategoryAsync(executable.Id, CategoryDeletePolicy.MoveToDeleted);
        await MakeItAnOlderLibraryAsync(t);

        await t.OpenAsync();

        var all = await t.Library.GetAllCategoriesAsync();
        Assert.DoesNotContain(all, c => c.BuiltInKey is "executable" or "executable.msi" or "executable.exe");
        Assert.Single(all, c => c.BuiltInKey == "documents.pdf");
        Assert.Single(all, c => c.BuiltInKey == "ebooks.epub");
    }

    [Fact]
    public async Task DeletingOneOfThem_IsFinal()
    {
        await using var t = await TestLibrary.CreateAsync();
        var word = await t.BuiltInAsync("documents.word");

        await t.Library.DeleteCategoryAsync(word.Id, CategoryDeletePolicy.MoveToDeleted);
        await t.ReopenAsync();

        Assert.DoesNotContain(await t.Library.GetAllCategoriesAsync(), c => c.BuiltInKey == "documents.word");
    }

    [Fact]
    public async Task YourOwnSubcategoryWithTheSameName_TakesTheRole_InsteadOfATwin()
    {
        await using var t = await TestLibrary.CreateAsync();
        var mine = await t.BuiltInAsync("documents.pdf");
        await t.Library.DisposeAsync();
        Execute(t, "DELETE FROM settings WHERE key = 'builtins-added-2026-10-24'");
        Execute(t, $"UPDATE categories SET builtin_key = NULL, kind = {(int)CategoryKind.User} WHERE id = {mine.Id}");

        await t.OpenAsync();

        var under = (await t.Library.GetAllCategoriesAsync()).Where(c => c.ParentId == mine.ParentId).ToList();
        var pdf = Assert.Single(under, c => c.Name.StartsWith("PDF", StringComparison.Ordinal));
        Assert.Equal(mine.Id, pdf.Id);
        Assert.Equal("documents.pdf", pdf.BuiltInKey);
    }

    [Theory]
    [InlineData("documents", "pdf", "documents.pdf")]
    [InlineData("ebooks", "pdf", "documents.pdf")]
    [InlineData("ebooks.pdf", "pdf", null)]
    [InlineData("ebooks.pdf", ".PDF", null)]
    [InlineData("ebooks.pdf", "epub", "ebooks.epub")]
    [InlineData("documents.pdf", "docx", "documents.word")]
    [InlineData("executable", "msi", "executable.msi")]
    [InlineData("executable.exe", "exe", null)]
    public void TypeSorting_SendsFilesToTheNewSubcategories_AndEbooksPdfKeepsPdfs(string sortingKey, string extension, string? expected)
        => Assert.Equal(expected, TypeSorting.Destination(sortingKey, extension));

    [Fact]
    public async Task APdfDroppedOnEbooksPdf_StaysThere_AndOneDroppedOnEbooks_GoesToDocumentsPdf()
    {
        await using var t = await TestLibrary.CreateAsync();
        var ebooks = await t.BuiltInAsync("ebooks");
        var ebooksPdf = await t.BuiltInAsync("ebooks.pdf");
        var documentsPdf = await t.BuiltInAsync("documents.pdf");

        var kept = await DropAsync(t, ebooksPdf.Id, t.CreateFile("novel.pdf", "a novel"));
        var sorted = await DropAsync(t, ebooks.Id, t.CreateFile("invoice.pdf", "an invoice"));

        Assert.Equal([ebooksPdf.Id], kept.CategoryIds);
        Assert.Equal([documentsPdf.Id], sorted.CategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "E-books", "PDF", "novel.pdf"), kept.FullPath);
    }

    [Fact]
    public async Task TheDocumentsTypeFilter_StillFindsPdfsAndWordFiles()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.pdf", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.docx", "b"));
        await t.AddOneAsync(docs.Id, t.CreateFile("c.rtf", "c"));

        var found = await t.Library.SearchAsync("", new ItemFilter { TypeKeys = ["documents"] });

        Assert.Equal(["a.pdf", "b.docx", "c.rtf"], found.Items.Select(i => i.FileName).Order(StringComparer.Ordinal));
    }

    /// <summary>Adds one file the way a drop on a thread does: with "sort files by type in type categories" on.</summary>
    private static async Task<LibraryItem> DropAsync(TestLibrary t, long categoryId, string path)
    {
        var result = await t.Library.AddPathsAsync([path], new AddRequest { TargetCategoryId = categoryId, SortIntoTypeCategories = true });
        Assert.Empty(result.Failures);
        return Assert.Single(result.Added);
    }

    /// <summary>Closes the library and removes the six and the "added" mark, as in a library made before 2026-10-24.</summary>
    private static async Task MakeItAnOlderLibraryAsync(TestLibrary t)
    {
        await t.Library.DisposeAsync();
        Execute(t, "DELETE FROM settings WHERE key = 'builtins-added-2026-10-24'");
        Execute(t, "DELETE FROM categories WHERE builtin_key IN ('documents.pdf', 'documents.word', 'executable.msi', " +
                   "'executable.exe', 'ebooks.epub', 'ebooks.pdf')");
    }

    private static void Execute(TestLibrary t, string sql)
    {
        using var connection = new Database(t.DataDirectory).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] ChildNames(IReadOnlyList<Category> all, string parentKey)
    {
        var parent = all.Single(c => c.BuiltInKey == parentKey);
        return [.. all.Where(c => c.ParentId == parent.Id).Select(c => c.Name).Order(StringComparer.Ordinal)];
    }
}
