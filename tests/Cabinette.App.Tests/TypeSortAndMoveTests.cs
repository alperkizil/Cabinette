using System.IO;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Tests;

/// <summary>What the UI does after files were sorted by type (SPEC.md section 5), and the sample library's version of both features.</summary>
public sealed class TypeSortAndMoveTests
{
    private const long Spreadsheets = 6;
    private const long Images = 3;
    private const long Code = 8;
    private const long Python = 9;

    private static LibraryItem Item(long id, long category) => new()
    {
        Id = id,
        FileName = $"file{id}",
        FullPath = $@"C:\v\file{id}",
        HomeCategoryId = category,
        CategoryIds = [category],
    };

    private static AddResult Result(int added, int duplicates, params (string Path, long Category)[] sorted) => new()
    {
        Added = Enumerable.Range(1, added).Select(i => Item(i, Spreadsheets)).ToList(),
        Duplicates = Enumerable.Range(100, duplicates).Select(i => new DuplicateInfo($"dup{i}", Item(i, Images), false)).ToList(),
        SortedByType = sorted.Select(s => new SortedFile(s.Path, s.Category)).ToList(),
    };

    [Fact]
    public void Everything_went_to_one_other_category_so_it_opens()
    {
        var outcome = TypeSortOutcome.From(Result(2, 1, ("a.png", Images), ("b.png", Images), ("dup100", Images)), new HashSet<long> { Spreadsheets });

        Assert.Equal(Images, outcome.OpenCategoryId);
        Assert.Equal(0, outcome.Stayed);
        Assert.Equal([new SortedDestination(Images, 3)], outcome.Destinations);
    }

    [Fact]
    public void Some_stayed_so_the_thread_stays_with_one_notice()
    {
        var outcome = TypeSortOutcome.From(Result(3, 0, ("a.png", Images)), new HashSet<long> { Spreadsheets });

        Assert.Null(outcome.OpenCategoryId);
        Assert.Equal(2, outcome.Stayed);
        Assert.Equal([new SortedDestination(Images, 1)], outcome.Destinations);
    }

    [Fact]
    public void Split_over_several_categories_stays_and_lists_the_biggest_first()
    {
        var outcome = TypeSortOutcome.From(Result(3, 0, ("a.py", Python), ("b.png", Images), ("c.png", Images)), new HashSet<long> { Spreadsheets });

        Assert.Null(outcome.OpenCategoryId);
        Assert.Equal(0, outcome.Stayed);
        Assert.Equal([new SortedDestination(Images, 2), new SortedDestination(Python, 1)], outcome.Destinations);
    }

    [Fact]
    public void Sorted_into_a_subcategory_of_the_thread_it_still_shows_so_nothing_opens()
    {
        var outcome = TypeSortOutcome.From(Result(2, 0, ("a.py", Python), ("b.py", Python)), new HashSet<long> { Code, Python });

        Assert.Null(outcome.OpenCategoryId);
        Assert.Equal([new SortedDestination(Python, 2)], outcome.Destinations);
    }

    // ------------------------------------------------------------------ the sample library (--sample-data runs)

    [Fact]
    public async Task Sample_library_sorts_by_type_only_when_asked()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var all = await library.GetAllCategoriesAsync();
        var sheets = all.Single(c => c.BuiltInKey == "spreadsheets");
        var images = all.Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var folder = Directory.CreateTempSubdirectory("cabinette-sample-sort-");
        try
        {
            var picture = Path.Combine(folder.FullName, "picture.png");
            var table = Path.Combine(folder.FullName, "table.csv");
            File.WriteAllText(picture, "png");
            File.WriteAllText(table, "a,b");

            var result = await library.AddPathsAsync([picture, table], new AddRequest { TargetCategoryId = sheets.Id, SortIntoTypeCategories = true });

            Assert.Equal([new SortedFile(picture, images.Id)], result.SortedByType);
            Assert.Equal(images.Id, result.Added.Single(i => i.FileName == "picture.png").HomeCategoryId);
            Assert.Equal(sheets.Id, result.Added.Single(i => i.FileName == "table.csv").HomeCategoryId);

            var other = Path.Combine(folder.FullName, "other.png");
            File.WriteAllText(other, "png2");
            var kept = await library.AddPathsAsync([other], new AddRequest { TargetCategoryId = sheets.Id });
            Assert.Empty(kept.SortedByType);
            Assert.Equal(sheets.Id, Assert.Single(kept.Added).HomeCategoryId);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Sample_library_moves_out_of_the_thread_and_keeps_other_categories()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var all = await library.GetAllCategoriesAsync();
        var code = all.Single(c => c.BuiltInKey == "code");
        var python = all.Single(c => c.BuiltInKey == "code.python");
        var work = all.Single(c => c.Name == "Work");
        var deleted = all.Single(c => c.BuiltInKey == ExtensionMap.DeletedKey);
        var script = (await library.GetThreadItemsAsync(python.Id)).Single();
        await library.AddToCategoryAsync([script.Id], code.Id);

        await library.MoveToCategoryAsync([script.Id], code.Id, work.Id);

        var moved = (await library.GetItemAsync(script.Id))!;
        Assert.Equal([work.Id], moved.CategoryIds);
        Assert.Equal(work.Id, moved.HomeCategoryId);
        Assert.Empty(await library.GetThreadItemsAsync(python.Id));

        // Out of Deleted, and into Deleted is refused like the real library.
        var draft = (await library.GetThreadItemsAsync(deleted.Id)).Single();
        await library.MoveToCategoryAsync([draft.Id], deleted.Id, work.Id);
        Assert.Equal([work.Id], (await library.GetItemAsync(draft.Id))!.CategoryIds);
        var ex = await Assert.ThrowsAsync<LibraryException>(() => library.MoveToCategoryAsync([draft.Id], work.Id, deleted.Id));
        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
    }
}

/// <summary>
/// The intake against a real library: drops and the paperclip into a type category follow the setting, and the UI opens the
/// one category the files went to, or stays with one notice. Runs on a dispatcher thread, like the app.
/// </summary>
public sealed class FileIntakeTypeSortTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cabinette-app-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ILibraryService> _libraries = [];

    [Fact]
    public void Everything_sorted_into_one_category_opens_it() => RunOnDispatcher(async () =>
    {
        var (library, intake, navigator, notifications, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        var images = await library.GetOrCreateBuiltInCategoryAsync(ExtensionMap.ImagesKey);
        navigator.CurrentCategoryId = sheets.Id;

        await intake.AddPathsAsync([Source("a.png", "a"), Source("b.jpg", "b")], sheets.Id);

        Assert.Equal([images.Id], opened);
        Assert.Equal("2 files sorted by type into Images", notifications.Current?.Message);
        Assert.Equal(2, (await library.GetThreadItemsAsync(images.Id)).Count);
        Assert.Empty(await library.GetThreadItemsAsync(sheets.Id));
    });

    [Fact]
    public void Split_files_stay_with_one_notice_that_offers_show() => RunOnDispatcher(async () =>
    {
        var (library, intake, navigator, notifications, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        var images = await library.GetOrCreateBuiltInCategoryAsync(ExtensionMap.ImagesKey);
        navigator.CurrentCategoryId = sheets.Id;

        await intake.AddPathsAsync([Source("a.png", "a"), Source("table.csv", "x,y"), Source("odd.zzq", "odd")], sheets.Id);

        Assert.Empty(opened);
        var notice = notifications.Current!;
        Assert.Equal("Sorted by type: 1 to Images. 2 stayed here.", notice.Message);
        Assert.Equal("Show", notice.ActionText);
        notice.Action!();
        Assert.Equal([images.Id], opened);
        Assert.Equal(2, (await library.GetThreadItemsAsync(sheets.Id)).Count);
    });

    [Fact]
    public void A_sorted_duplicate_says_where_it_went() => RunOnDispatcher(async () =>
    {
        var (library, intake, navigator, notifications, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        var work = await library.CreateCategoryAsync("Work", null);
        await library.AddPathsAsync([Source(@"old\logo.png", "logo")], new AddRequest { TargetCategoryId = work.Id, Mode = ItemMode.Copy });
        navigator.CurrentCategoryId = sheets.Id;

        await intake.AddPathsAsync([Source("logo.png", "logo"), Source("table.csv", "x,y")], sheets.Id);

        Assert.Empty(opened);
        Assert.Equal("Sorted by type: 1 to Images. 1 stayed here.", notifications.Current?.Message);
        notifications.Current!.DismissCommand.Execute(null);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal("“logo.png” was already in Cabinette (in: Work) and is now also in Images", notifications.Current?.Message);
    });

    [Fact]
    public void With_the_setting_off_or_from_another_source_the_category_keeps_everything() => RunOnDispatcher(async () =>
    {
        var (library, intake, navigator, notifications, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        navigator.CurrentCategoryId = sheets.Id;

        await intake.AddPathsAsync([Source("ai.png", "ai")], sheets.Id, AddedBy.Ai);
        await library.SaveSettingsAsync(await library.GetSettingsAsync() with { SortFilesByTypeInTypeCategories = false });
        await intake.AddPathsAsync([Source("mine.png", "mine")], sheets.Id);

        Assert.Empty(opened);
        Assert.Equal("1 file added to Spreadsheets", notifications.Current?.Message);
        Assert.Equal(2, (await library.GetThreadItemsAsync(sheets.Id)).Count);
    });

    private async Task<(ILibraryService Library, FileIntake Intake, ShellNavigator Navigator, NotificationService Notifications, List<long> Opened)> OpenAsync()
    {
        Directory.CreateDirectory(_root);
        var library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.Combine(_root, "data") }, new NoRecycleBin());
        _libraries.Add(library);
        await library.InitializeVaultAsync(Path.Combine(_root, "vault"));
        var directory = new CategoryDirectory(library);
        var navigator = new ShellNavigator();
        var opened = new List<long>();
        navigator.SetHandler((id, _) =>
        {
            opened.Add(id);
            return Task.CompletedTask;
        });
        var notifications = new NotificationService();
        var intake = new FileIntake(
            library,
            new Cabinette.Windows.ClipboardReader(),
            notifications,
            new DialogService(library, new CategoryIconService(library, new FakeIconGenerator()), directory),
            navigator,
            directory);
        return (library, intake, navigator, notifications, opened);
    }

    private string Source(string name, string content)
    {
        var path = Path.Combine(_root, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Runs <paramref name="body"/> on an STA thread with a dispatcher, so awaits come back to it as in the app.</summary>
    private void RunOnDispatcher(Func<Task> body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var frame = new System.Windows.Threading.DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    foreach (var library in _libraries)
                    {
                        await library.DisposeAsync();
                    }

                    frame.Continue = false;
                }
            });
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The test body did not finish.");
        TryDelete(_root);
        failure?.Throw();
    }

    private static void TryDelete(string root)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class NoRecycleBin : Cabinette.Core.Abstractions.IRecycleBin
    {
        public bool CanRecycle(string path) => false;

        public void SendToRecycleBin(IReadOnlyCollection<string> paths) => throw new InvalidOperationException("Not used here.");
    }
}
