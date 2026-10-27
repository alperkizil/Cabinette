using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>Starred files in the UI (SPEC.md section 4): the chat list's Starred row, the Starred view, menus and the selection bar.</summary>
public sealed class StarredFilesTests
{
    // ------------------------------------------------------------------ the chat list's Starred row

    [Fact]
    public async Task Starred_row_sits_above_pinned_categories_while_anything_is_starred()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var chat = new ChatListViewModel(library, new CategoryIconService(library, new FakeIconGenerator()));
        var starred = await library.GetStarredItemsAsync();
        Assert.NotEmpty(starred);

        await chat.RefreshAsync();

        var row = chat.Rows[0];
        Assert.True(row.IsStarredRow);
        Assert.Equal("Starred", row.Name);
        Assert.Equal(CategoryRowViewModel.StarredIconKey, row.IconKey);
        Assert.True(row.IsSystem); // so the chat list takes no drops on it
        Assert.False(row.IsPinned);
        Assert.False(row.HasUnread);
        Assert.Equal($"{starred.Count} files", row.PreviewText);
        Assert.Equal(Glyphs.ChatTime(starred.Max(i => i.StarredAt), DateTimeOffset.Now), row.TimeText);
        Assert.True(chat.Rows[1].IsPinned);
        Assert.Single(chat.Rows, r => r.IsStarredRow);

        // Unstarring everything takes the row away; one star brings it back.
        await library.SetStarredAsync(starred.Select(i => i.Id).ToList(), false);
        await chat.RefreshAsync();
        Assert.DoesNotContain(chat.Rows, r => r.IsStarredRow);

        await library.SetStarredAsync([starred[0].Id], true);
        await chat.RefreshAsync();
        Assert.True(chat.Rows[0].IsStarredRow);
        Assert.Equal("1 file", chat.Rows[0].PreviewText);
    }

    [Fact]
    public async Task Starred_row_is_only_on_the_top_level_and_stays_selected_while_its_view_is_open()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var chat = new ChatListViewModel(library, new CategoryIconService(library, new FakeIconGenerator()));
        await chat.RefreshAsync();
        var work = chat.Rows.Single(r => r.Name == "Work");

        chat.OpenCategoryId = CategoryRowViewModel.StarredRowId;
        chat.SyncSelection();
        Assert.True(chat.SelectedRow?.IsStarredRow);

        await chat.DrillInAsync(work.CategoryId);
        Assert.DoesNotContain(chat.Rows, r => r.IsStarredRow);

        await chat.GoUpAsync();
        Assert.True(chat.Rows[0].IsStarredRow);
    }

    // ------------------------------------------------------------------ the Starred view

    [Fact]
    public void Starred_view_lists_stars_in_order_and_takes_no_files() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, intake, dialogs) = Create(library);
        var expected = await library.GetStarredItemsAsync();
        var itemsBefore = (await library.SearchAsync("")).Items.Count; // the sample library's search lists every item

        await vm.LoadStarredAsync();

        Assert.True(vm.IsStarredView);
        Assert.Equal(ThreadViewModel.StarredViewId, vm.CategoryId);
        Assert.Equal("Starred", vm.CategoryName);
        Assert.False(vm.CanAddFiles);
        Assert.False(vm.HasSubcategories);
        Assert.Equal($"{expected.Count} files", vm.Subtitle);
        Assert.Equal(expected.Select(i => i.Id), vm.Items.Select(i => i.Id));
        Assert.All(vm.Items, i => Assert.True(i.IsStarred));

        // Like a chat: dated by when they were starred, the newest at the bottom; one bubble per file, no albums.
        Assert.Equal(expected[0].StarredAt!.Value.ToLocalTime().Date, vm.ChatRows.OfType<DateHeaderRow>().First().Date);
        Assert.Equal(ThreadFormat.Time(expected[^1].StarredAt!.Value), vm.Items[^1].TimeText);
        Assert.DoesNotContain(vm.ChatRows, r => r is AlbumRow);
        Assert.Equal(expected.Count, vm.ChatRows.OfType<ItemRow>().Count());

        // No message box, paperclip, drops or pastes.
        vm.ComposerText = "a note";
        vm.SendNoteCommand.Execute(null);
        await vm.AttachCommand.ExecuteAsync(null);
        await vm.PasteAsync();
        await vm.AddDroppedPathsAsync([@"C:\Windows\win.ini"]);
        Assert.Equal(0, intake.Calls);
        Assert.Equal(itemsBefore, (await library.SearchAsync("")).Items.Count);

        // No "Move to category…" or "Remove from this category": there is no category to leave.
        vm.SelectOnly([vm.Items[^1]]);
        var ids = vm.GetContextMenuEntries().OfType<ThreadViewModel.ThreadMenuEntry>().Select(e => e.Id).ToList();
        Assert.DoesNotContain("moveto", ids);
        Assert.DoesNotContain("remove", ids);
        Assert.Contains("addto", ids);
        Assert.Contains("delete", ids);
        Assert.Equal("Unstar", vm.GetContextMenuEntries().OfType<ThreadViewModel.ThreadMenuEntry>().Single(e => e.Id == "star").Text);
        await vm.MoveSelectedToCategoryAsync();
        await vm.RemoveSelectedFromCategoryAsync();
        Assert.Equal(0, dialogs.PickCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
        vm.Dispose();
    });

    [Fact]
    public void Unstarring_in_the_starred_view_removes_the_file_there_and_then() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, _) = Create(library);
        await vm.LoadStarredAsync();
        var count = vm.Items.Count;
        var newest = vm.Items[^1];

        vm.SelectOnly([newest]);
        Assert.True(vm.IsSelectionStarred);
        Assert.Equal("Unstar", vm.StarActionText);
        await vm.ToggleStarSelectedCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => vm.Items.Count == count - 1, "the unstarred file leaves the Starred view");
        Assert.DoesNotContain(vm.Items, i => i.Id == newest.Id);
        Assert.False(vm.HasSelection);
        await WaitUntilAsync(() => vm.Subtitle == $"{count - 1} files", "the header counts one file less");
        Assert.False((await library.GetItemAsync(newest.Id))!.IsStarred);

        // Starred elsewhere: it arrives at the bottom, newest last.
        var other = (await library.SearchAsync("Lease")).Items.Single();
        await library.SetStarredAsync([other.Id], true);
        await WaitUntilAsync(() => vm.Items.Count == count && vm.Items[^1].Id == other.Id, "a new star shows up at the bottom");
        Assert.Equal((await library.GetStarredItemsAsync()).Select(i => i.Id), vm.Items.Select(i => i.Id));
        vm.Dispose();
    });

    [Fact]
    public void Deleting_in_the_starred_view_deletes_everywhere_without_offering_remove_from_this_category() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, dialogs) = Create(library);
        var budget = (await library.SearchAsync("Budget")).Items.Single();
        var docs = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == "documents");
        await library.AddToCategoryAsync([budget.Id], docs.Id); // in two categories: a thread would ask "Remove from this category?"
        await vm.LoadStarredAsync();

        vm.SelectOnly([vm.Items.Single(i => i.Id == budget.Id)]);
        await vm.DeleteSelectedAsync();

        Assert.Equal(0, dialogs.ChooseCalls);
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Null(await library.GetItemAsync(budget.Id));
        vm.Dispose();
    });

    // ------------------------------------------------------------------ menus and the selection bar in a thread

    [Fact]
    public void Menu_and_selection_bar_star_a_mixed_selection_and_unstar_an_all_starred_one() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, _) = Create(library);
        var docs = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == "documents");
        await vm.LoadAsync(docs.Id);
        Assert.False(vm.IsStarredView);
        var tax = vm.Items.Single(i => i.FileName == "Tax return 2025.pdf");
        var lease = vm.Items.Single(i => i.FileName == "Lease agreement.docx");
        var taxStarredAt = tax.Item.StarredAt;
        Assert.True(tax.IsStarred);
        Assert.False(lease.IsStarred);

        vm.SelectOnly([tax]);
        Assert.Equal("Unstar", StarEntry(vm).Text);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.StarOff20, StarEntry(vm).Symbol);
        Assert.True(vm.IsSelectionStarred);
        Assert.Contains(vm.GetContextMenuEntries().OfType<ThreadViewModel.ThreadMenuEntry>(), e => e.Id == "moveto");

        vm.SelectOnly([tax, lease]);
        Assert.Equal("Star", StarEntry(vm).Text);
        Assert.False(vm.IsSelectionStarred);
        Assert.Equal("Star", vm.StarActionText);

        // Star them all: the one already starred keeps its time; the bubbles show the star once the thread reloads.
        await vm.ToggleStarSelectedCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => lease.IsStarred, "the bubble shows the new star");
        Assert.Equal(taxStarredAt, (await library.GetItemAsync(tax.Id))!.StarredAt);
        Assert.True(vm.IsSelectionStarred);
        Assert.Equal("Unstar", vm.StarActionText);
        Assert.Equal("Unstar", StarEntry(vm).Text);

        // Unstar them all.
        await vm.ToggleStarSelectedCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !tax.IsStarred && !lease.IsStarred, "both stars go");
        Assert.Equal("Star", vm.StarActionText);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ live updates

    [Fact]
    public void Starred_view_reloads_for_stars_its_files_and_their_categories_only()
    {
        var shown = new HashSet<long> { 100 };
        var shownCategories = new HashSet<long> { 5 };

        Assert.Equal((true, true, false), ThreadViewModel.ClassifyStarredChange(new() { Kind = LibraryChangeKind.Items | LibraryChangeKind.Starred, ItemIds = [7] }, shown, shownCategories));
        Assert.Equal((true, true, false), ThreadViewModel.ClassifyStarredChange(new() { Kind = LibraryChangeKind.Items, ItemIds = [100] }, shown, shownCategories));
        Assert.Equal((true, true, false), ThreadViewModel.ClassifyStarredChange(new() { Kind = LibraryChangeKind.Categories | LibraryChangeKind.Items, CategoryIds = [5] }, shown, shownCategories));
        Assert.Equal((false, false, false), ThreadViewModel.ClassifyStarredChange(new() { Kind = LibraryChangeKind.Categories | LibraryChangeKind.Items, CategoryIds = [6], ItemIds = [8] }, shown, shownCategories));
        Assert.Equal((false, false, false), ThreadViewModel.ClassifyStarredChange(new() { Kind = LibraryChangeKind.Viewed, CategoryIds = [5] }, shown, shownCategories));
        Assert.Equal((false, false, true), ThreadViewModel.ClassifyStarredChange(new() { Kind = LibraryChangeKind.Settings }, shown, shownCategories));

        // An ordinary thread redraws a bubble whose star changed (its id is shown), and ignores stars elsewhere.
        Assert.True(ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Items | LibraryChangeKind.Starred, ItemIds = [100] }, 5, shown).Items);
        Assert.False(ThreadViewModel.ClassifyChange(new() { Kind = LibraryChangeKind.Items | LibraryChangeKind.Starred, ItemIds = [7] }, 5, shown).Items);
    }

    // ------------------------------------------------------------------ helpers

    private static ThreadViewModel.ThreadMenuEntry StarEntry(ThreadViewModel vm)
        => vm.GetContextMenuEntries().OfType<ThreadViewModel.ThreadMenuEntry>().Single(e => e.Id == "star");

    private static (ThreadViewModel Vm, FakeIntake Intake, FakeDialogs Dialogs) Create(ILibraryService library)
    {
        var intake = new FakeIntake();
        var dialogs = new FakeDialogs();
        var services = new ThreadServices(
            library,
            new NoContextMenu(),
            new NoShell(),
            intake,
            dialogs,
            new NotificationService(),
            new ShellNavigator(),
            new ThumbnailLoader(new NoThumbnails(), new NoMediaInfo()),
            new ThreadViewState());
        return (new ThreadViewModel(services), intake, dialogs);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(25);
        }
    }

    /// <summary>Runs <paramref name="body"/> on an STA thread with a dispatcher, so awaits and timers come back to it as in the app.</summary>
    private static void RunOnDispatcher(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "The test body did not finish.");
        failure?.Throw();
    }

    private sealed class FakeIntake : IFileIntake
    {
        public int Calls { get; private set; }

        public Task AddPathsAsync(IReadOnlyList<string> paths, long? targetCategoryId, AddedBy addedBy = AddedBy.AppUi)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task PickAndAddFilesAsync(long? targetCategoryId)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task PasteAsync(long? targetCategoryId)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public int PickCalls { get; private set; }

        public int ConfirmCalls { get; private set; }

        public int ChooseCalls { get; private set; }

        public Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
        {
            ConfirmCalls++;
            return Task.FromResult(true);
        }

        public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
        {
            ChooseCalls++;
            return Task.FromResult<int?>(null);
        }

        public Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true)
        {
            PickCalls++;
            return Task.FromResult<long?>(null);
        }

        public Task<string?> PickFileToLocateAsync(string originalFileName) => Task.FromResult<string?>(null);
    }

    private sealed class NoContextMenu : IShellContextMenuService
    {
        public ContextMenuResult Show(IntPtr ownerHwnd, IReadOnlyList<string> paths, System.Windows.Point screenPointPx, IReadOnlyList<CustomMenuItem> customItems)
            => throw new InvalidOperationException("Not shown in tests.");
    }

    private sealed class NoShell : IShellOperations
    {
        public void OpenWithDefaultApp(string path) => throw new InvalidOperationException("Not opened in tests.");

        public void ShowInExplorer(string path) => throw new InvalidOperationException("Not opened in tests.");
    }

    private sealed class NoThumbnails : IThumbnailService
    {
        public Task<BitmapSource?> GetThumbnailAsync(string path, int sizePx, CancellationToken ct = default) => Task.FromResult<BitmapSource?>(null);

        public Task<BitmapSource?> GetFileIconAsync(string pathOrExtension, int sizePx, CancellationToken ct = default) => Task.FromResult<BitmapSource?>(null);
    }

    private sealed class NoMediaInfo : IMediaInfoService
    {
        public MediaInfo? GetMediaInfo(string path) => null;
    }
}
