using System.IO;
using System.Reflection;
using System.Windows.Input;
using Cabinette.App.DesignTime;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.Views.Shell;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>Ctrl+K quick switcher (SPEC section 7): ranking, the view model, recent categories and Shift+Enter.</summary>
public sealed class QuickSwitcherRankingTests
{
    [Theory]
    [InlineData("Invoices", "invoices", false, QuickSwitcherRanking.Exact)]
    [InlineData("Invoice.pdf", "invoice", true, QuickSwitcherRanking.Exact)] // a file's name without its extension
    [InlineData("Invoice 2026-03.pdf", "inv", true, QuickSwitcherRanking.Prefix)]
    [InlineData("Tax invoice.pdf", "inv", true, QuickSwitcherRanking.WordStart)]
    [InlineData("2026_invoice.pdf", "inv", true, QuickSwitcherRanking.WordStart)]
    [InlineData("Previnvoice.pdf", "inv", true, QuickSwitcherRanking.Contains)]
    [InlineData("March invoice.pdf", "invoice march", true, QuickSwitcherRanking.Other)] // words found apart
    [InlineData("Invoice march 2026.pdf", "INVOICE   march", true, QuickSwitcherRanking.Prefix)] // case and spacing don't matter
    [InlineData("Invoices", "#invoice", false, QuickSwitcherRanking.Other)] // tags only
    public void Scores_how_well_a_name_matches(string name, string query, bool isFile, int expected)
        => Assert.Equal(expected, QuickSwitcherRanking.Score(name, QuickSwitcherRanking.Phrase(query), isFile));

    [Fact]
    public void Best_matches_first_categories_before_files_on_a_tie_library_order_otherwise()
    {
        // As the library returns them: categories A–Z, files newest first, all matching "inv".
        string[] categories = ["Invoices", "Old invoices", "Previnvoices"];
        string[] files = ["Previnvoice.pdf", "Invoice 2026-05.pdf", "Invoice 2026-04.pdf", "Tax invoice.pdf", "inv.txt"];

        var ranked = QuickSwitcherRanking.Rank("inv", categories, files, max: 10);

        Assert.Equal(
            new[]
            {
                (false, 4), // exact ("inv.txt" without its extension)
                (true, 0), (false, 1), (false, 2), // prefix: the category first, then the files newest first
                (true, 1), (false, 3), // a word starts with it
                (true, 2), (false, 0), // somewhere inside
            },
            ranked);

        Assert.Equal(new[] { (false, 4), (true, 0), (false, 1) }, QuickSwitcherRanking.Rank("inv", categories, files, max: 3));
    }

    [Fact]
    public void A_tag_only_query_keeps_the_library_order()
        => Assert.Equal(new[] { (false, 0), (false, 1), (false, 2) }, QuickSwitcherRanking.Rank("#invoice", [], ["c.pdf", "a.pdf", "b.pdf"], 10));
}

public sealed class QuickSwitcherShortcutTests
{
    [Fact]
    public void Ctrl_K_opens_it_closes_it_again_and_does_nothing_over_another_dialog()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var switcher = new QuickSwitcherViewModel((text, ct) => library.SearchAsync(text, null, ct), new CategoryDirectory(library), null, [], null);
        var confirm = new ConfirmDialogViewModel("Empty Deleted?", "Sure?", "Empty Deleted", isDestructive: true);

        Assert.Equal(QuickSwitcherKeyAction.Open, MainWindow.DecideQuickSwitcherKey(Key.K, ModifierKeys.Control, isRepeat: false, topDialog: null));
        Assert.Equal(QuickSwitcherKeyAction.Close, MainWindow.DecideQuickSwitcherKey(Key.K, ModifierKeys.Control, isRepeat: false, switcher));
        Assert.Equal(QuickSwitcherKeyAction.None, MainWindow.DecideQuickSwitcherKey(Key.K, ModifierKeys.Control, isRepeat: false, confirm));
        Assert.Equal(QuickSwitcherKeyAction.Swallow, MainWindow.DecideQuickSwitcherKey(Key.K, ModifierKeys.Control, isRepeat: true, topDialog: null));
        Assert.Equal(QuickSwitcherKeyAction.None, MainWindow.DecideQuickSwitcherKey(Key.K, ModifierKeys.Control | ModifierKeys.Shift, isRepeat: false, topDialog: null));
        Assert.Equal(QuickSwitcherKeyAction.None, MainWindow.DecideQuickSwitcherKey(Key.K, ModifierKeys.None, isRepeat: false, topDialog: null));
        Assert.Equal(QuickSwitcherKeyAction.None, MainWindow.DecideQuickSwitcherKey(Key.F, ModifierKeys.Control, isRepeat: false, topDialog: null));
    }
}

public sealed class QuickSwitcherViewModelTests
{
    // ------------------------------------------------------------------ searching

    [Fact]
    public async Task Typing_lists_the_best_matches_with_their_category_path()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);

        vm.Query = "inv";
        await vm.Pending;

        Assert.False(vm.IsShowingRecent);
        Assert.False(vm.IsEmpty);
        var category = Assert.IsType<QuickSwitcherCategoryRow>(vm.Rows[0]);
        Assert.Equal("Invoices", category.Name);
        Assert.Equal("Work", category.Detail); // its parent
        var files = vm.Rows.Skip(1).Cast<QuickSwitcherFileRow>().ToList();
        Assert.Equal(["Invoice 2026-05.pdf", "Invoice 2026-04.pdf", "Invoice 2026-03.pdf"], files.Select(f => f.Name));
        Assert.All(files, f => Assert.Equal("Work › Invoices", f.Detail));
        Assert.Same(vm.Rows[0], vm.Selected);
    }

    [Fact]
    public async Task At_most_ten_matches_are_listed()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);

        vm.Query = "o"; // most of the sample library
        await vm.Pending;

        Assert.Equal(QuickSwitcherViewModel.MaxResults, vm.Rows.Count);
    }

    [Fact]
    public async Task Search_waits_for_a_pause_and_cancels_earlier_keystrokes()
    {
        var gate = new DelayGate();
        var (vm, searches) = await CreateAsync(delay: gate.Delay);

        vm.Query = "i";
        vm.Query = "in";
        vm.Query = "inv";

        Assert.Equal(3, gate.Waits.Count);
        Assert.All(gate.Waits, w => Assert.Equal(QuickSwitcherViewModel.Debounce, w.Duration));
        Assert.True(gate.Waits[0].Token.IsCancellationRequested);
        Assert.True(gate.Waits[1].Token.IsCancellationRequested);
        Assert.False(gate.Waits[2].Token.IsCancellationRequested);
        Assert.Empty(searches); // nothing searched while typing

        gate.ReleaseAll();
        await vm.Pending;

        Assert.Equal(["inv"], searches);
        Assert.Equal("Invoices", vm.Rows[0].Name);
    }

    [Fact]
    public async Task A_late_answer_for_older_text_never_replaces_newer_results()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(library, async (text, ct) =>
        {
            if (text == "inv")
            {
                await slow.Task; // ignores the cancellation: the answer arrives anyway
            }

            return await library.SearchAsync(text, null, ct);
        });
        await vm.LoadAsync();

        vm.Query = "inv";
        var stale = vm.Pending;
        vm.Query = "travel";
        await vm.Pending;
        slow.SetResult();
        await stale;

        Assert.Equal("Travel 2026", vm.Rows[0].Name);
        Assert.DoesNotContain(vm.Rows, r => r.Name.StartsWith("Inv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_match_says_so()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);

        vm.Query = "zzz";
        await vm.Pending;

        Assert.True(vm.IsEmpty);
        Assert.Null(vm.Selected);
        Assert.Equal(L.Format("Search_NoResults", "zzz"), vm.EmptyText);
    }

    [Fact]
    public async Task A_failed_search_says_so_and_Enter_does_nothing()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var vm = Create(library, (_, _) => throw new LibraryException(LibraryErrorCode.Unknown, "The library is busy."), NoDelay);
        await vm.LoadAsync();

        vm.Query = "inv";
        await vm.Pending;
        Assert.True(vm.IsEmpty);
        Assert.Equal("The library is busy.", vm.EmptyText);

        await vm.ActivateSelectedAsync(openFile: false);
        Assert.False(vm.IsClosed);
    }

    // ------------------------------------------------------------------ keyboard and choices

    [Fact]
    public async Task Arrow_keys_move_the_selection_within_the_list()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);
        vm.Query = "inv";
        await vm.Pending;

        vm.MoveSelection(1);
        Assert.Same(vm.Rows[1], vm.Selected);
        vm.MoveSelection(-5);
        Assert.Same(vm.Rows[0], vm.Selected);
        vm.MoveSelection(50);
        Assert.Same(vm.Rows[^1], vm.Selected);
    }

    [Fact]
    public async Task Enter_on_a_category_opens_its_thread()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);
        vm.Query = "inv";
        await vm.Pending;
        var invoices = Assert.IsType<QuickSwitcherCategoryRow>(vm.Selected);

        vm.Accept();
        var actions = await ApplyAsync(vm);

        Assert.Equal([(invoices.CategoryId, (long?)null)], actions.Opened);
        Assert.Empty(actions.Files);
    }

    [Fact]
    public async Task Enter_on_a_file_opens_its_home_thread_scrolled_to_it()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);
        vm.Query = "inv";
        await vm.Pending;
        vm.MoveSelection(1);
        var file = Assert.IsType<QuickSwitcherFileRow>(vm.Selected).Item;

        await vm.ActivateSelectedAsync(openFile: false);
        var actions = await ApplyAsync(vm);

        Assert.Equal([(file.HomeCategoryId, (long?)file.Id)], actions.Opened);
        Assert.Empty(actions.Files);
    }

    [Fact]
    public async Task Shift_Enter_opens_a_file_in_its_app_and_a_category_as_usual()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);
        vm.Query = "inv";
        await vm.Pending;
        vm.MoveSelection(1);
        var file = Assert.IsType<QuickSwitcherFileRow>(vm.Selected).Item;

        await vm.ActivateSelectedAsync(openFile: true);
        var actions = await ApplyAsync(vm);
        Assert.Equal([file.Id], actions.Files.Select(f => f.Id));
        Assert.Empty(actions.Opened);

        var (other, _) = await CreateAsync(delay: NoDelay);
        other.Query = "inv";
        await other.Pending;
        var category = Assert.IsType<QuickSwitcherCategoryRow>(other.Selected);
        await other.ActivateSelectedAsync(openFile: true);
        actions = await ApplyAsync(other);
        Assert.Equal([(category.CategoryId, (long?)null)], actions.Opened);
    }

    [Fact]
    public async Task A_click_acts_on_that_row()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);
        vm.Query = "inv";
        await vm.Pending;
        var row = vm.Rows[2];

        vm.Activate(row, openFile: false);

        var choice = Assert.IsType<QuickSwitcherChoice>(await vm.Completion);
        Assert.Same(row, choice.Row);
    }

    [Fact]
    public async Task Enter_before_the_pause_searches_at_once_and_takes_the_best_match()
    {
        var gate = new DelayGate(); // never released: only Enter can make it search
        var (vm, searches) = await CreateAsync(delay: gate.Delay);

        vm.Query = "inv";
        await vm.ActivateSelectedAsync(openFile: false);

        Assert.True(gate.Waits.Single().Token.IsCancellationRequested);
        Assert.Equal(["inv"], searches);
        var choice = Assert.IsType<QuickSwitcherChoice>(await vm.Completion);
        Assert.Equal("Invoices", choice.Row.Name);
    }

    [Fact]
    public async Task Esc_closes_without_a_choice_and_cancels_the_pending_search()
    {
        var gate = new DelayGate();
        var (vm, searches) = await CreateAsync(delay: gate.Delay);
        vm.Query = "inv";

        vm.Dismiss();

        Assert.Null(await vm.Completion);
        Assert.True(gate.Waits.Single().Token.IsCancellationRequested);
        await vm.Pending;
        Assert.Empty(searches);
    }

    // ------------------------------------------------------------------ recent categories (empty box)

    [Fact]
    public async Task Empty_box_lists_recent_categories_without_the_open_one_or_deleted_ones()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var all = await library.GetAllCategoriesAsync();
        long Id(string name) => all.Single(c => c.Name == name).Id;
        var deleted = await library.CreateCategoryAsync("Gone soon", null);
        await library.DeleteCategoryAsync(deleted.Id, CategoryDeletePolicy.MoveToDeleted);
        long[] recent = [Id("Notes"), Id("Invoices"), deleted.Id, Id("Images"), Id("Audio"), Id("Video"), Id("Code"), Id("Links"), Id("Fonts"), Id("Work"), Id("Recipes")];

        var vm = Create(library, (text, ct) => library.SearchAsync(text, null, ct), NoDelay, recent, openCategoryId: Id("Notes"));
        await vm.LoadAsync();

        Assert.True(vm.IsShowingRecent);
        Assert.Equal(["Invoices", "Images", "Audio", "Video", "Code", "Links", "Fonts", "Work"], vm.Rows.Select(r => r.Name));
        Assert.Equal(QuickSwitcherViewModel.MaxRecent, vm.Rows.Count);
        Assert.Equal("Work", vm.Rows[0].Detail);
        Assert.Same(vm.Rows[0], vm.Selected); // Enter goes back to where you were before
    }

    [Fact]
    public async Task Empty_box_without_recent_categories_says_what_to_do()
    {
        var (vm, _) = await CreateAsync(delay: NoDelay);

        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsShowingRecent);
        Assert.Equal(L.Get("QuickSwitcher_NoRecent"), vm.EmptyText);
    }

    [Fact]
    public async Task Clearing_the_query_goes_back_to_the_recent_list()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var notes = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Notes");
        var vm = Create(library, (text, ct) => library.SearchAsync(text, null, ct), NoDelay, [notes.Id], openCategoryId: null);
        await vm.LoadAsync();

        vm.Query = "inv";
        await vm.Pending;
        Assert.False(vm.IsShowingRecent);

        vm.Query = "  ";
        await vm.Pending;
        Assert.True(vm.IsShowingRecent);
        Assert.Equal(["Notes"], vm.Rows.Select(r => r.Name));
    }

    // ------------------------------------------------------------------ helpers

    private static Task NoDelay(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

    private static async Task<(QuickSwitcherViewModel Vm, List<string> Searches)> CreateAsync(Func<TimeSpan, CancellationToken, Task> delay)
    {
        var library = SampleLibraryService.Create(withVault: true);
        var searches = new List<string>();
        var vm = Create(
            library,
            (text, ct) =>
            {
                lock (searches)
                {
                    searches.Add(text);
                }

                return library.SearchAsync(text, null, ct);
            },
            delay);
        await vm.LoadAsync();
        return (vm, searches);
    }

    private static QuickSwitcherViewModel Create(
        ILibraryService library,
        Func<string, CancellationToken, Task<SearchResults>> search,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IReadOnlyList<long>? recent = null,
        long? openCategoryId = null)
        => new(search, new CategoryDirectory(library), new CategoryIconService(library, new FakeIconGenerator()), recent ?? [], openCategoryId, delay ?? NoDelay);

    private static async Task<FakeActions> ApplyAsync(QuickSwitcherViewModel vm)
    {
        var choice = Assert.IsType<QuickSwitcherChoice>(await vm.Completion);
        var actions = new FakeActions();
        await choice.ApplyAsync(actions);
        return actions;
    }

    private sealed class FakeActions : IQuickSwitcherActions
    {
        public List<(long CategoryId, long? FocusItemId)> Opened { get; } = [];

        public List<LibraryItem> Files { get; } = [];

        public Task OpenCategoryAsync(long categoryId, long? focusItemId)
        {
            Opened.Add((categoryId, focusItemId));
            return Task.CompletedTask;
        }

        public Task OpenFileAsync(LibraryItem item)
        {
            Files.Add(item);
            return Task.CompletedTask;
        }
    }

    /// <summary>A debounce wait that only ends when the test says so (or when it is cancelled).</summary>
    private sealed class DelayGate
    {
        public List<(TimeSpan Duration, CancellationToken Token, TaskCompletionSource Done)> Waits { get; } = [];

        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            Waits.Add((duration, ct, done));
            return done.Task;
        }

        public void ReleaseAll()
        {
            foreach (var wait in Waits)
            {
                wait.Done.TrySetResult();
            }
        }
    }
}

public sealed class RecentCategoriesTests
{
    [Fact]
    public void The_latest_comes_first_without_duplicates_and_the_list_is_capped()
    {
        var recent = new RecentCategories(load: null, save: null);
        foreach (var id in Enumerable.Range(1, RecentCategories.RememberCount + 3))
        {
            recent.Record(id);
        }

        recent.Record(5);

        var ids = recent.Snapshot();
        Assert.Equal(RecentCategories.RememberCount, ids.Count);
        Assert.Equal([5L, 19, 18, 17], ids.Take(4));
        Assert.Single(ids, id => id == 5);
        Assert.DoesNotContain(1L, ids);
    }

    [Fact]
    public async Task Remembered_across_restarts_in_the_state_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "recent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(folder);
            var store = new AppStateStore(paths);
            var first = new RecentCategories(store, new CommandLineOptions { DataDirectory = folder });
            await first.Loaded;
            first.Record(3);
            first.Record(7);
            first.Record(3);

            // The writes run in the background. Wait for them rather than polling the file: a read while the store writes
            // could make that write fail (sharing violation), and a busy machine can take longer than any polling budget.
            await store.WhenSavedAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var stored = StoredIds(paths.AppStateFile);
            Assert.NotNull(stored);
            Assert.Equal([3L, 7], stored);

            // A restart: a new store reads the file.
            var second = new RecentCategories(new AppStateStore(paths), new CommandLineOptions { DataDirectory = folder });
            await second.Loaded;
            Assert.Equal([3L, 7], second.Snapshot());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Sample_data_and_screenshot_runs_keep_the_list_in_memory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "recent-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(folder);
        var recent = new RecentCategories(new AppStateStore(paths), new CommandLineOptions { SampleData = true });
        await recent.Loaded;

        recent.Record(4);
        await Task.Delay(200);

        Assert.Equal([4L], recent.Snapshot());
        Assert.False(File.Exists(paths.AppStateFile));
    }

    [Fact]
    public async Task Ids_recorded_while_the_file_is_read_come_first_and_are_saved()
    {
        var reading = new ManualResetEventSlim();
        var saved = new List<IReadOnlyList<long>>();
        var recent = new RecentCategories(
            () =>
            {
                reading.Wait(TimeSpan.FromSeconds(10));
                return [1, 2, 3];
            },
            ids =>
            {
                lock (saved)
                {
                    saved.Add(ids);
                }
            });

        recent.Record(2);
        recent.Record(9);
        lock (saved)
        {
            Assert.Empty(saved); // nothing is saved before the stored list is known
        }

        reading.Set();
        await recent.Loaded;

        Assert.Equal([9L, 2, 1, 3], recent.Snapshot());
        lock (saved)
        {
            Assert.Equal([9L, 2, 1, 3], Assert.Single(saved));
        }
    }

    [Fact]
    public void Push_puts_the_id_first_and_keeps_the_order_of_the_others()
        => Assert.Equal([4L, 1, 2], RecentCategories.Push([1, 4, 2, 3], 4, max: 3));

    private static long[]? StoredIds(string file)
    {
        try
        {
            return File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<AppState>(File.ReadAllText(file))?.RecentCategoryIds?.ToArray() : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null; // being written
        }
    }
}

/// <summary>Shift+Enter: the file opens in its default app, or "Locate…" is offered when it is missing.</summary>
public sealed class ItemOpenerTests
{
    private static readonly LibraryItem Stale = new() { Id = 7, FileName = "Report.pdf", FullPath = @"C:\Old\Report.pdf", Mode = ItemMode.Pointer };

    [Fact]
    public async Task An_existing_file_opens_at_its_current_path()
    {
        var library = FakeLibrary.Create(Stale with { FullPath = @"C:\New\Report.pdf" }, exists: true);
        var shell = new FakeShell();
        var dialogs = new FakeDialogs();

        await new ItemOpener(library, shell, dialogs, new FakeNotifications()).OpenAsync(Stale);

        Assert.Equal([@"C:\New\Report.pdf"], shell.Opened);
        Assert.Empty(dialogs.Confirms);
    }

    [Fact]
    public async Task A_missing_linked_file_offers_Locate_and_relinks_it()
    {
        var library = FakeLibrary.Create(Stale, exists: false);
        var shell = new FakeShell();
        var dialogs = new FakeDialogs { Confirm = true, Located = @"D:\Found\Report.pdf" };
        var notifications = new FakeNotifications();

        await new ItemOpener(library, shell, dialogs, notifications).OpenAsync(Stale);

        Assert.Empty(shell.Opened);
        Assert.Equal([L.Get("Thread_MissingTitle")], dialogs.Confirms);
        Assert.Equal([(7L, @"D:\Found\Report.pdf", false)], FakeLibrary.Relocations(library));
        Assert.Equal([L.Format("Thread_Relocated", "Report.pdf")], notifications.Messages);
    }

    [Fact]
    public async Task A_missing_vault_file_is_reported()
    {
        var vaultFile = Stale with { Mode = ItemMode.Copy };
        var library = FakeLibrary.Create(vaultFile, exists: false);
        var dialogs = new FakeDialogs();
        var notifications = new FakeNotifications();

        await new ItemOpener(library, new FakeShell(), dialogs, notifications).OpenAsync(vaultFile);

        Assert.Empty(dialogs.Confirms);
        Assert.Equal([L.Format("Thread_VaultFileMissing", "Report.pdf")], notifications.Messages);
    }

    [Fact]
    public async Task A_file_removed_from_the_library_meanwhile_is_reported()
    {
        var library = FakeLibrary.Create(null, exists: false);
        var shell = new FakeShell();
        var notifications = new FakeNotifications();

        await new ItemOpener(library, shell, new FakeDialogs(), notifications).OpenAsync(Stale);

        Assert.Empty(shell.Opened);
        Assert.Equal([L.Format("QuickSwitcher_FileGone", "Report.pdf")], notifications.Messages);
    }

    /// <summary>Answers GetItemAsync, VerifyExistsAsync and RelocateItemAsync; any other call fails the test.</summary>
    public class FakeLibrary : DispatchProxy
    {
        private LibraryItem? _item;
        private bool _exists;
        private readonly List<(long, string, bool)> _relocations = [];

        public static ILibraryService Create(LibraryItem? item, bool exists)
        {
            var proxy = Create<ILibraryService, FakeLibrary>();
            var fake = (FakeLibrary)(object)proxy;
            fake._item = item;
            fake._exists = exists;
            return proxy;
        }

        public static List<(long, string, bool)> Relocations(ILibraryService library) => ((FakeLibrary)(object)library)._relocations;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            nameof(ILibraryService.GetItemAsync) => Task.FromResult(_item),
            nameof(ILibraryService.VerifyExistsAsync) => Task.FromResult(_exists),
            nameof(ILibraryService.RelocateItemAsync) => Relocate((long)args![0]!, (string)args[1]!, (bool)args[2]!),
            _ => throw new InvalidOperationException($"Unexpected library call: {targetMethod.Name}"),
        };

        private Task<RelocateOutcome> Relocate(long id, string path, bool acceptMismatch)
        {
            _relocations.Add((id, path, acceptMismatch));
            return Task.FromResult(RelocateOutcome.Relocated);
        }
    }

    private sealed class FakeShell : IShellOperations
    {
        public List<string> Opened { get; } = [];

        public void OpenWithDefaultApp(string path) => Opened.Add(path);

        public void ShowInExplorer(string path) => throw new InvalidOperationException();
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Confirm { get; init; }

        public string? Located { get; init; }

        public List<string> Confirms { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
        {
            Confirms.Add(title);
            return Task.FromResult(Confirm);
        }

        public Task<string?> PickFileToLocateAsync(string originalFileName) => Task.FromResult(Located);

        public Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
            => throw new InvalidOperationException();

        public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options) => throw new InvalidOperationException();

        public Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true) => throw new InvalidOperationException();
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<string> Messages { get; } = [];

        public void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Action? action = null) => Messages.Add(message);
    }
}
