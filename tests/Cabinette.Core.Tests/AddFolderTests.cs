using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class AddFolderTests
{
    private static string CreateProject(TestLibrary t)
    {
        t.CreateFile(@"Project\readme.md", "# readme");
        t.CreateFile(@"Project\src\app.py", "print('app')");
        t.CreateFile(@"Project\src\util\helpers.py", "def h(): pass");
        t.CreateFile(@"Project\docs\guide.pdf", "guide");
        t.CreateFile(@"Project\desktop.ini", "[.ShellClassInfo]");
        t.CreateFile(@"Project\Thumbs.db", "thumbs");
        t.CreateFile(@"Project\~$lock.docx", "lock");
        t.CreateFile(@"Project\scratch.tmp", "tmp");
        var hidden = t.CreateFile(@"Project\hidden.txt", "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        return Path.Combine(t.Source, "Project");
    }

    [Fact]
    public async Task Folder_WithSubfoldersAsSubcategories_MirrorsTheTree()
    {
        await using var t = await TestLibrary.CreateAsync();
        var folder = CreateProject(t);

        var result = await t.Library.AddPathsAsync([folder],
            new AddRequest { Mode = ItemMode.Copy, SubfoldersAsSubcategories = true, AlsoAddToTypeCategories = false });

        Assert.Empty(result.Failures);
        Assert.Equal(4, result.Added.Count);
        Assert.Equal(4, result.CreatedCategoryIds.Count);

        var top = (await t.Library.GetCategoriesAsync(null, CategorySort.Alphabetical)).Single(s => s.Category.Name == "Project");
        Assert.Equal(CategoryKind.User, top.Category.Kind);
        Assert.Equal("folder", top.Category.IconKey);
        Assert.Equal(4, top.ItemCount);
        Assert.Equal(2, top.SubcategoryCount);

        var subs = await t.Library.GetCategoriesAsync(top.Category.Id, CategorySort.Alphabetical);
        Assert.Equal(["docs", "src"], subs.Select(s => s.Category.Name).ToArray());
        var src = subs.Single(s => s.Category.Name == "src");
        Assert.Equal(2, src.ItemCount);
        Assert.Equal(1, src.SubcategoryCount);

        Assert.True(File.Exists(Path.Combine(t.Vault, "Project", "readme.md")));
        Assert.True(File.Exists(Path.Combine(t.Vault, "Project", "src", "app.py")));
        Assert.True(File.Exists(Path.Combine(t.Vault, "Project", "src", "util", "helpers.py")));
        Assert.True(File.Exists(Path.Combine(t.Vault, "Project", "docs", "guide.pdf")));
        Assert.False(File.Exists(Path.Combine(t.Vault, "Project", "desktop.ini")));
        Assert.False(File.Exists(Path.Combine(t.Vault, "Project", "hidden.txt")));

        // Nothing went to the type categories.
        Assert.Equal(0, (await t.SummaryAsync((await t.BuiltInAsync("code.python")).Id)).ItemCount);
    }

    [Fact]
    public async Task Folder_WithoutSubcategories_PutsAllFilesFlat()
    {
        await using var t = await TestLibrary.CreateAsync();
        var folder = CreateProject(t);
        t.CreateFile(@"Project\src\readme.md", "# another readme");

        var result = await t.Library.AddPathsAsync([folder],
            new AddRequest { Mode = ItemMode.Copy, SubfoldersAsSubcategories = false });

        Assert.Equal(5, result.Added.Count);
        var id = Assert.Single(result.CreatedCategoryIds);
        Assert.All(result.Added, i => Assert.Equal(id, i.HomeCategoryId));
        Assert.Equal(0, (await t.SummaryAsync(id)).SubcategoryCount);
        var names = Directory.GetFiles(Path.Combine(t.Vault, "Project")).Select(p => Path.GetFileName(p)!).Order().ToArray();
        Assert.Equal(["app.py", "guide.pdf", "helpers.py", "readme (1).md", "readme.md"], names);
    }

    [Fact]
    public async Task Folder_AlsoAddToTypeCategories_AddsMemberships()
    {
        await using var t = await TestLibrary.CreateAsync();
        var folder = CreateProject(t);

        var result = await t.Library.AddPathsAsync([folder],
            new AddRequest { Mode = ItemMode.Copy, SubfoldersAsSubcategories = true, AlsoAddToTypeCategories = true });

        var python = await t.BuiltInAsync("code.python");
        var app = result.Added.Single(i => i.FileName == "app.py");
        Assert.Contains(python.Id, app.CategoryIds);
        Assert.NotEqual(python.Id, app.HomeCategoryId);
        Assert.Equal(Path.Combine(t.Vault, "Project", "src", "app.py"), app.FullPath);
        Assert.Equal(2, (await t.SummaryAsync(python.Id)).ItemCount);
        Assert.Equal(1, (await t.SummaryAsync((await t.BuiltInAsync("documents")).Id)).ItemCount);
        Assert.Equal(1, (await t.SummaryAsync((await t.BuiltInAsync("text")).Id)).ItemCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Vault, "Code", "Python")));
    }

    [Fact]
    public async Task Folder_UnderTargetCategory_WithSiblingNameClash_GetsNumbered()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.Library.CreateCategoryAsync("Project", docs.Id);
        var folder = CreateProject(t);

        var result = await t.Library.AddPathsAsync([folder], new AddRequest { TargetCategoryId = docs.Id, Mode = ItemMode.Copy });

        var created = await t.CategoryAsync(result.CreatedCategoryIds[0]);
        Assert.Equal("Project (1)", created.Name);
        Assert.Equal(docs.Id, created.ParentId);
        Assert.True(File.Exists(Path.Combine(t.Vault, "Documents", "Project (1)", "readme.md")));

        // The parent thread shows the folder's files too.
        Assert.Equal(4, (await t.Library.GetThreadItemsAsync(docs.Id)).Count);
    }

    [Fact]
    public async Task Folder_InPointerMode_CreatesCategoriesButLeavesFilesInPlace()
    {
        await using var t = await TestLibrary.CreateAsync();
        var folder = CreateProject(t);
        var result = await t.Library.AddPathsAsync([folder], new AddRequest { Mode = ItemMode.Pointer });
        Assert.Equal(4, result.Added.Count);
        Assert.All(result.Added, i => Assert.StartsWith(folder, i.FullPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Vault, "Project"), "*", SearchOption.AllDirectories));
    }
}
