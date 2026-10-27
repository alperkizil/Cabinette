using System.ComponentModel;
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

/// <summary>Bug-hunt review of the Starred files UI commit (8cca83e): live updates and layout the author's tests leave out.</summary>
public sealed class StarredReviewTests
{
    [Fact]
    public async Task Starred_row_stays_on_top_with_A_to_Z_sort()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var chat = new ChatListViewModel(library, new CategoryIconService(library, new FakeIconGenerator())) { Sort = CategorySort.Alphabetical };

        await chat.RefreshAsync();

        Assert.True(chat.Rows[0].IsStarredRow);
        Assert.Single(chat.Rows, r => r.IsStarredRow);
        var names = chat.Rows.Skip(1).Where(r => !r.IsPinned).Select(r => r.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public void Album_footer_star_follows_live_star_changes() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, _) = Create(library);
        var images = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == "images");
        await vm.LoadAsync(images.Id);

        // The sample stars one wallpaper that is not the newest of its album: the footer shows the star.
        var album = vm.ChatRows.OfType<AlbumRow>().Single(a => a.Items.Any(i => i.IsStarred));
        Assert.False(album.Last.IsStarred);
        Assert.True(album.HasOtherStarred);
        var starredOther = album.Items.Single(i => i.IsStarred);
        var raised = new List<string?>();
        album.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Starring the newest file too: its own meta shows a star, so the footer star goes (one star per album).
        await library.SetStarredAsync([album.Last.Id], true);
        await WaitUntilAsync(() => album.Last.IsStarred, "the newest file shows its star");
        Assert.Same(album, vm.ChatRows.OfType<AlbumRow>().Single(a => a.Items.Contains(starredOther)));
        Assert.False(album.HasOtherStarred);
        Assert.Contains(nameof(AlbumRow.HasOtherStarred), raised);

        // Unstarring the older one leaves only the newest file's star.
        raised.Clear();
        await library.SetStarredAsync([starredOther.Id], false);
        await WaitUntilAsync(() => !starredOther.IsStarred, "the older file loses its star");
        Assert.False(album.HasOtherStarred);
        Assert.True(album.Last.IsStarred);

        // Unstarring the newest: no star anywhere on the album.
        await library.SetStarredAsync([album.Last.Id], false);
        await WaitUntilAsync(() => !album.Last.IsStarred, "the newest file loses its star");
        Assert.False(album.HasOtherStarred);
        Assert.DoesNotContain(album.Items, i => i.IsStarred);

        // Starring an older one again brings the footer star back, on the same row instance.
        raised.Clear();
        await library.SetStarredAsync([starredOther.Id], true);
        await WaitUntilAsync(() => starredOther.IsStarred, "the older file is starred again");
        Assert.True(album.HasOtherStarred);
        Assert.Contains(nameof(AlbumRow.HasOtherStarred), raised);
        vm.Dispose();
    });

    [Fact]
    public void Clicking_Unstar_in_the_real_styled_menu_removes_the_file_from_the_starred_view() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, _) = Create(library);
        await vm.LoadStarredAsync();
        var count = vm.Items.Count;
        var target = vm.Items[0];
        vm.SelectOnly([target]);

        var menu = Views.Thread.ThreadItemMenuBuilder.Build(vm, default);
        Assert.NotNull(menu);
        var headers = menu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => (string)m.Header).ToList();
        Assert.DoesNotContain("Move to category…", headers);
        Assert.DoesNotContain("Remove from this category", headers);
        var unstar = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Unstar");

        unstar.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));

        await WaitUntilAsync(() => vm.Items.Count == count - 1 && vm.Items.All(i => i.Id != target.Id), "the clicked file leaves the view");
        Assert.False((await library.GetItemAsync(target.Id))!.IsStarred);
        vm.Dispose();
    });

    [Fact]
    public void Starred_view_empties_and_keeps_its_header_when_the_last_star_goes() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, _) = Create(library);
        await vm.LoadStarredAsync();
        var all = vm.Items.Select(i => i.Id).ToList();
        Assert.NotEmpty(all);

        await library.SetStarredAsync(all, false);

        await WaitUntilAsync(() => vm.Items.Count == 0, "every file leaves the view");
        Assert.True(vm.IsStarredView);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.CanAddFiles);
        Assert.Equal("Starred", vm.CategoryName);
        await WaitUntilAsync(() => vm.Subtitle == "0 files", "the header counts zero");
        vm.Dispose();
    });

    /// <summary>
    /// Regression (bug hunt of Starred files, finding 1): the details view of the Starred view sorted and dated its rows by
    /// "Date added" while the chat and the grid use when the files were starred. Now its date column is "Starred".
    /// </summary>
    [Fact]
    public void Starred_details_view_sorts_and_dates_by_the_star_time() => RunOnDispatcher(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var (vm, _, _) = Create(library);
        await vm.LoadStarredAsync();
        Assert.NotEmpty(vm.Items);

        var dateColumn = vm.DetailsColumns.Single(c => c.Column == DetailsColumn.DateAdded);
        Assert.Equal("Starred", dateColumn.Title);
        vm.ViewMode = ThreadViewMode.Details; // the details rows are built for that view
        // Several sample files share a star time (ties are broken by name), so check the order of the times themselves.
        var times = vm.DetailsItems.Select(i => i.ListedAt).ToList();
        Assert.Equal(vm.Items.Count, times.Count);
        Assert.True(times.SequenceEqual(times.Order()) || times.SequenceEqual(times.OrderDescending()), "rows follow the star time");
        Assert.False(vm.DetailsItems.Select(i => i.AddedAt).SequenceEqual(vm.DetailsItems.Select(i => i.ListedAt)), "the sample's star times differ from its add times, so this test means something");
        Assert.All(vm.Items, i => Assert.Equal(ThreadFormat.DateTimeShort(i.Item.StarredAt!.Value), i.DateAddedText));

        // A normal thread gets "Date added" back.
        await vm.LoadAsync(vm.Items[0].Item.HomeCategoryId);
        Assert.Equal("Date added", dateColumn.Title);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ helpers (as in StarredFilesTests)

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
