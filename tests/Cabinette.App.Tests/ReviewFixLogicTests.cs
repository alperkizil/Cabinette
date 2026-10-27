using System.IO;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>UI-free logic behind the stage 1 review fixes.</summary>
public sealed class ReviewFixLogicTests
{
    // ------------------------------------------------------------------ extension filter box (finding 14)

    [Fact]
    public void Extension_box_splits_on_spaces_commas_and_semicolons()
    {
        Assert.Equal(["pdf", "docx", "md", "txt"], ExtensionList.Parse(" pdf, .DOCX;md  txt ,pdf "));
        Assert.Empty(ExtensionList.Parse(null));
        Assert.Empty(ExtensionList.Parse(" ,; "));
    }

    [Fact]
    public void Extension_box_goes_through_ExtensionMap_Normalize()
    {
        // ExtensionMap.Normalize strips a leading "*" (Core), so "*.pdf" means pdf in both filters.
        Assert.Equal([ExtensionMap.Normalize("*.pdf")], ExtensionList.Parse("*.pdf"));
        Assert.Equal(new ThreadFilterViewModel { ExtensionsText = "*.PDF .md" }.Build().Extensions, ExtensionList.Parse("*.PDF .md"));
        Assert.Equal(new SearchFilterViewModel { ExtensionsText = "*.PDF .md" }.ToItemFilter().Extensions, ExtensionList.Parse("*.PDF .md"));
    }

    [Fact]
    public void Thread_filter_offers_the_notes_type()
    {
        Assert.Contains(new ThreadFilterViewModel().Types, t => t.Key == ExtensionMap.NotesKey);
        Assert.DoesNotContain(new ThreadFilterViewModel().Types, t => t.Key == ExtensionMap.DeletedKey);
    }

    // ------------------------------------------------------------------ Ctrl+V in the thread (findings 8, 13)

    [Theory]
    [InlineData(true, true, true, true, "TextBox")] // Excel cells: text + bitmap, composer focused → text
    [InlineData(true, true, false, true, "TextBox")]
    [InlineData(true, false, true, true, "Thread")] // a copied image or files while typing → into the thread
    [InlineData(true, false, false, true, "TextBox")] // nothing usable: the text box does its normal thing
    [InlineData(false, false, false, true, "Thread")] // outside text boxes everything goes to the thread
    [InlineData(false, true, true, true, "Thread")]
    [InlineData(false, true, true, false, "Nothing")] // "Deleted" takes no files
    [InlineData(true, false, true, false, "TextBox")]
    public void Paste_routing(bool inTextBox, bool hasText, bool hasFilesOrImage, bool acceptsFiles, string expected)
    {
        Assert.Equal(Enum.Parse<ThreadPasteTarget>(expected), ThreadPasteRouting.Route(inTextBox, hasText, hasFilesOrImage, acceptsFiles));
    }

    // ------------------------------------------------------------------ open thread and library changes (finding 6)

    [Fact]
    public void Viewed_only_change_does_not_reload_the_thread()
    {
        var change = new LibraryChangedEventArgs { Kind = LibraryChangeKind.Viewed, CategoryIds = [5, 1] };

        Assert.Equal((false, false, false), ThreadViewModel.ClassifyChange(change, 5, new HashSet<long>()));
    }

    [Fact]
    public void Other_changes_still_reload_what_they_touch()
    {
        var shown = new HashSet<long> { 100 };
        Assert.Equal((true, true, false), ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Items, CategoryIds = [5] }, 5, shown));
        Assert.Equal((true, false, false), ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Tags, ItemIds = [100] }, 5, shown));
        Assert.Equal((false, true, false), ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Categories, CategoryIds = [9] }, 5, shown));
        Assert.Equal((false, false, true), ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Settings }, 5, shown));
        Assert.Equal((true, true, false), ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Viewed | LibraryChangeKind.Items, CategoryIds = [5] }, 5, shown));
    }

    // ------------------------------------------------------------------ "only in this category" (finding 18)

    [Fact]
    public void Only_in_thread_counts_subcategories_as_here()
    {
        var subtree = new HashSet<long> { 10, 11, 12 };

        Assert.True(ThreadViewModel.IsOnlyInThread(Item(10), subtree));
        Assert.True(ThreadViewModel.IsOnlyInThread(Item(10, 12), subtree)); // Code and Code › Python: removing from Code empties it
        Assert.False(ThreadViewModel.IsOnlyInThread(Item(10, 20), subtree));

        // Without the category tree: one category = only here.
        Assert.True(ThreadViewModel.IsOnlyInThread(Item(10), null));
        Assert.False(ThreadViewModel.IsOnlyInThread(Item(10, 12), null));
    }

    // ------------------------------------------------------------------ delete texts (finding 4)

    [Fact]
    public void Delete_consequence_follows_the_settings()
    {
        var defaults = new AppSettings();
        Assert.Equal(
            "Files stored in the vault will be moved to the Recycle Bin.\nLinked files: the originals on your disk will also be moved to the Recycle Bin.",
            DeleteTexts.Consequence(anyVaultFiles: true, anyPointerFiles: true, single: false, defaults));

        var permanentLibraryOnly = new AppSettings { RecycleVaultFiles = false, DeleteRealPointerFiles = false };
        Assert.Equal(
            "The file will be permanently deleted. This can't be undone.",
            DeleteTexts.Consequence(anyVaultFiles: true, anyPointerFiles: false, single: true, permanentLibraryOnly));
        Assert.Equal(
            "Linked files are only removed from Cabinette. The originals stay on your disk.",
            DeleteTexts.Consequence(anyVaultFiles: false, anyPointerFiles: true, single: false, permanentLibraryOnly));
        Assert.Equal("", DeleteTexts.Consequence(false, false, false, defaults));
    }

    // ------------------------------------------------------------------ search note (finding 17)

    [Fact]
    public void More_results_note_comes_from_HasMoreItems()
    {
        var items = Enumerable.Range(1, 500).Select(i => Item(1) with { Id = i }).ToList();

        Assert.Null(SearchViewModel.MoreResultsNote(new SearchResults([], items)));
        Assert.Equal("Showing the first 500 files", SearchViewModel.MoreResultsNote(new SearchResults([], items) { HasMoreItems = true }));
        Assert.Equal("Showing the first 3 files", SearchViewModel.MoreResultsNote(new SearchResults([], items.Take(3).ToList()) { HasMoreItems = true }));
    }

    // ------------------------------------------------------------------ first run (finding 3)

    [Fact]
    public void First_run_folder_with_content_is_detected()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(FirstRunViewModel.FolderHasContent(root)); // doesn't exist yet
            Directory.CreateDirectory(root);
            Assert.False(FirstRunViewModel.FolderHasContent(root));

            var ini = Path.Combine(root, "desktop.ini");
            File.WriteAllText(ini, "");
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            Assert.False(FirstRunViewModel.FolderHasContent(root)); // the monitor ignores it too

            Directory.CreateDirectory(Path.Combine(root, "Images"));
            Assert.True(FirstRunViewModel.FolderHasContent(root));

            Directory.Delete(Path.Combine(root, "Images"));
            File.WriteAllText(Path.Combine(root, "photo.jpg"), "x");
            Assert.True(FirstRunViewModel.FolderHasContent(root));
        }
        finally
        {
            VaultOperationsService.DeleteTree(root);
        }
    }

    // ------------------------------------------------------------------ old vault, deleted permanently (finding 5)

    [Fact]
    public void Permanent_delete_of_old_vault_removes_read_only_and_hidden_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "Images");
        Directory.CreateDirectory(sub);
        var readOnly = Path.Combine(sub, "a.jpg");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var hidden = Path.Combine(root, ".cabinette-x.tmp");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.ReadOnly);

        VaultOperationsService.DeleteTree(root);

        Assert.False(Directory.Exists(root));
        VaultOperationsService.DeleteTree(root); // already gone: fine
    }

    private static LibraryItem Item(params long[] categories) => new()
    {
        Id = 1,
        FileName = "a.txt",
        FullPath = @"C:\Vault\a.txt",
        CategoryIds = categories,
        HomeCategoryId = categories[0],
    };
}
