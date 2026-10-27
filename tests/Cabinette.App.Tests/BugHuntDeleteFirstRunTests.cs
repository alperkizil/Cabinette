using System.IO;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Shell;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt 2026-10-15, group C, with App logic and a real library in a temp folder: #8 "Delete permanently" through
/// <see cref="RecycleBinFallback"/> after a late Recycle Bin refusal (was VerifyGpt.App G5), and #13 what first run shows for a
/// vault that contains the data folder (was VerifyFableFindings.App F4). RecycleBinFallback itself is unchanged.
/// </summary>
public sealed class BugHuntDeleteFirstRunTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "bughunt-" + Guid.NewGuid().ToString("N"));
    private readonly TestRecycleBin _recycleBin = new();
    private ILibraryService? _library;

    private string Vault => Path.Combine(_root, "vault");

    // ================================================================ #8

    [Fact]
    public async Task F8_LateRefusal_DeletePermanentlyThroughTheFallback_DeletesTheFile()
    {
        var library = await OpenAsync();
        var docs = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var item = await AddCopyAsync(library, docs.Id, "huge.bin", "too large for the Recycle Bin");
        _recycleBin.Refused.Add(item.FullPath); // CanRecycle says yes, the shell refuses
        var dialogs = new FakeDialogs(confirm: true);

        // The thread's delete path: RecycleBinFallback around DeleteItemsAsync.
        var done = await RecycleBinFallback.RunAsync(dialogs, options => library.DeleteItemsAsync([item.Id], options));

        Assert.True(done);
        var confirm = Assert.Single(dialogs.Confirms);
        Assert.StartsWith("\"huge.bin\" can't be moved to the Recycle Bin", confirm.Message);
        Assert.False(File.Exists(item.FullPath)); // deleted permanently
        Assert.Empty(_recycleBin.Recycled);
        Assert.Null(await library.GetItemAsync(item.Id));
    }

    /// <summary>"No" after a late refusal: nothing more happens, the file stays (it comes back at the next rescan).</summary>
    [Fact]
    public async Task F8_LateRefusal_No_LeavesTheFile()
    {
        var library = await OpenAsync();
        var docs = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var item = await AddCopyAsync(library, docs.Id, "huge.bin", "too large for the Recycle Bin");
        _recycleBin.Refused.Add(item.FullPath);
        var dialogs = new FakeDialogs(confirm: false);

        var done = await RecycleBinFallback.RunAsync(dialogs, options => library.DeleteItemsAsync([item.Id], options));

        Assert.False(done);
        Assert.Single(dialogs.Confirms);
        Assert.True(File.Exists(item.FullPath));
        Assert.Null(await library.GetItemAsync(item.Id)); // the row went with the commit, before the refusal
    }

    [Fact]
    public async Task F8b_LateRefusal_CategoryDeleteThroughTheFallback_DeletesTheFileAndTheFolder()
    {
        var library = await OpenAsync();
        var project = await library.CreateCategoryAsync("Project", null);
        var item = await AddCopyAsync(library, project.Id, "huge.bin", "too large for the Recycle Bin");
        _recycleBin.Refused.Add(item.FullPath);
        var dialogs = new FakeDialogs(confirm: true);

        var done = await RecycleBinFallback.RunAsync(dialogs,
            options => library.DeleteCategoryAsync(project.Id, CategoryDeletePolicy.RecycleFiles, options));

        Assert.True(done);
        Assert.Single(dialogs.Confirms);
        Assert.False(File.Exists(item.FullPath));
        Assert.False(Directory.Exists(Path.Combine(Vault, "Project")));
        Assert.Null(await library.GetCategoryAsync(project.Id));
    }

    /// <summary>Too large, known before anything changes: "No" leaves the item, its tags and its file as they were.</summary>
    [Fact]
    public async Task F8_TooLargeUpFront_No_ChangesNothing()
    {
        var library = await OpenAsync();
        var docs = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var item = await AddCopyAsync(library, docs.Id, "huge.bin", "too large for the Recycle Bin");
        await library.SetItemTagsAsync(item.Id, ["keep"]);
        _recycleBin.TooLarge.Add(item.FullPath);
        var dialogs = new FakeDialogs(confirm: false);

        var done = await RecycleBinFallback.RunAsync(dialogs, options => library.DeleteItemsAsync([item.Id], options));

        Assert.False(done);
        Assert.Single(dialogs.Confirms);
        Assert.True(File.Exists(item.FullPath));
        Assert.Equal(["keep"], (await library.GetItemAsync(item.Id))!.Tags);
    }

    // ================================================================ #13

    /// <summary>
    /// What first run does for a pick that contains the data folder: the "This folder isn't empty" warning comes first (the
    /// database is already there), and after "Use this folder anyway" the library refuses; FirstRunViewModel shows that
    /// <see cref="LibraryException.Message"/> as its error. Move vault's picker refuses such folders as not empty, as before.
    /// </summary>
    [Fact]
    public async Task F13_PicksThatContainTheDataFolder_AreRefusedByTheLibrary_AfterTheNotEmptyWarning()
    {
        var appData = Path.Combine(_root, "home", "AppData");
        var localAppData = Path.Combine(appData, "Local");
        var dataFolder = Path.Combine(localAppData, "Cabinette");
        Directory.CreateDirectory(localAppData);
        File.SetAttributes(appData, File.GetAttributes(appData) | FileAttributes.Hidden);
        var library = await OpenAsync(dataFolder, initializeVault: false);
        Directory.CreateDirectory(Vault);

        foreach (var pick in new[] { localAppData, dataFolder })
        {
            Assert.True(FirstRunViewModel.FolderHasContent(pick));
            Assert.Equal(L.Get("Vault_FolderNotEmpty"), VaultOperationsService.CheckEmptyFolder(pick, Vault));

            var ex = await Assert.ThrowsAsync<LibraryException>(() => library.InitializeVaultAsync(pick));
            Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
            Assert.Equal("The vault can't contain Cabinette's data folder.", ex.Message);
        }

        Assert.Null(library.VaultPath);
    }

    // ================================================================ helpers

    private async Task<ILibraryService> OpenAsync(string? dataDirectory = null, bool initializeVault = true)
    {
        Directory.CreateDirectory(_root);
        _library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = dataDirectory ?? Path.Combine(_root, "data") }, _recycleBin);
        if (initializeVault)
        {
            await _library.InitializeVaultAsync(Vault);
        }

        return _library;
    }

    private async Task<LibraryItem> AddCopyAsync(ILibraryService library, long categoryId, string name, string content)
    {
        var source = Path.Combine(_root, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, content);
        var result = await library.AddPathsAsync([source], new AddRequest { TargetCategoryId = categoryId, Mode = ItemMode.Copy });
        Assert.Empty(result.Failures);
        return Assert.Single(result.Added);
    }

    public async ValueTask DisposeAsync()
    {
        if (_library is not null)
        {
            await _library.DisposeAsync(); // releases this library's pooled connections
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(entry, File.Exists(entry) ? FileAttributes.Normal : FileAttributes.Directory);
                    }

                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// A Recycle Bin in memory: "recycling" deletes the file and records it. <see cref="TooLarge"/> files are refused up front
    /// (CanRecycle), <see cref="Refused"/> ones only when sent, as the shell does for items too large for the Recycle Bin.
    /// </summary>
    private sealed class TestRecycleBin : IRecycleBin
    {
        public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> TooLarge { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Recycled { get; } = [];

        public bool CanRecycle(string path) => !TooLarge.Contains(path);

        public void SendToRecycleBin(IReadOnlyCollection<string> paths)
        {
            var refused = paths.Where(p => Refused.Contains(p) || TooLarge.Contains(p)).ToList();
            foreach (var path in paths.Except(refused))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Recycled.Add(path);
                }
            }

            if (refused.Count > 0)
            {
                throw new RecycleBinUnavailableException(refused, "Simulated: too large for the Recycle Bin.");
            }
        }
    }
}
