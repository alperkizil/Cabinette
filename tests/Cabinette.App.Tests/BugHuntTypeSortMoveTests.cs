using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt 2026-10-22 (commit 6abd784): the "Move to category…" wiring of the thread view model, which the author left
/// untested, against the in-memory sample library with every library call recorded; and the intake's notices after a sort.
/// Runs on a dispatcher thread, like the app.
/// </summary>
public sealed class BugHuntThreadMoveWiringTests
{
    [Fact]
    public void Move_from_a_parent_thread_passes_the_parent_as_from_and_clears_the_selection() => RunOnDispatcher(async () =>
    {
        var (library, calls) = RecordingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var all = await library.GetAllCategoriesAsync();
        var work = all.Single(c => c.Name == "Work");
        var travel = all.Single(c => c.Name == "Travel 2026");
        var (vm, dialogs, notifications, navigator) = await OpenAsync(library, work.Id);
        var targets = vm.Items.Take(2).ToList();
        Assert.Contains(targets, t => t.Item.HomeCategoryId != work.Id); // one lives in a subcategory of Work
        vm.SelectOnly(targets);
        dialogs.Pick = travel.Id;

        await vm.MoveSelectedToCategoryAsync();

        var call = Assert.Single(calls.Of(nameof(ILibraryService.MoveToCategoryAsync)));
        Assert.Equal(targets.Select(t => t.Id), (IReadOnlyList<long>)call[0]!);
        Assert.Equal(work.Id, call[1]);
        Assert.Equal(travel.Id, call[2]);
        Assert.Equal(AddedBy.AppUi, call[3]);
        Assert.Equal(0, vm.SelectedCount);
        var notice = Assert.Single(notifications.Shown);
        Assert.Equal("Moved 2 files to Travel 2026", notice.Message);
        Assert.Equal(NotificationKind.Success, notice.Kind);
        Assert.Equal("Show", notice.ActionText);
        notice.Action!();
        Assert.Equal([(travel.Id, (long?)null)], navigator.Opened);
        vm.Dispose();
    });

    [Fact]
    public void Move_of_one_file_focuses_it_in_the_target() => RunOnDispatcher(async () =>
    {
        var (library, _) = RecordingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var all = await library.GetAllCategoriesAsync();
        var images = all.Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var travel = all.Single(c => c.Name == "Travel 2026");
        var (vm, dialogs, notifications, navigator) = await OpenAsync(library, images.Id);
        var target = vm.Items[0];
        vm.SelectOnly([target]);
        dialogs.Pick = travel.Id;

        await vm.MoveSelectedToCategoryAsync();

        var notice = Assert.Single(notifications.Shown);
        Assert.Equal("Moved 1 file to Travel 2026", notice.Message);
        notice.Action!();
        Assert.Equal([(travel.Id, (long?)target.Id)], navigator.Opened);
        Assert.DoesNotContain(await library.GetThreadItemsAsync(images.Id), i => i.Id == target.Id);
        vm.Dispose();
    });

    [Fact]
    public void Picking_the_open_thread_or_cancelling_calls_nothing_and_keeps_the_selection() => RunOnDispatcher(async () =>
    {
        var (library, calls) = RecordingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var images = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var (vm, dialogs, notifications, _) = await OpenAsync(library, images.Id);
        vm.SelectOnly(vm.Items.Take(2).ToList());

        dialogs.Pick = images.Id;
        await vm.MoveSelectedToCategoryAsync();
        dialogs.Pick = null;
        await vm.MoveSelectedToCategoryAsync();

        Assert.Empty(calls.Of(nameof(ILibraryService.MoveToCategoryAsync)));
        Assert.Empty(notifications.Shown);
        Assert.Equal(2, vm.SelectedCount);
        vm.Dispose();
    });

    [Fact]
    public void Move_is_offered_in_the_Deleted_thread_and_moves_out_of_it() => RunOnDispatcher(async () =>
    {
        var (library, calls) = RecordingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var all = await library.GetAllCategoriesAsync();
        var deleted = all.Single(c => c.BuiltInKey == ExtensionMap.DeletedKey);
        var work = all.Single(c => c.Name == "Work");
        var (vm, dialogs, notifications, _) = await OpenAsync(library, deleted.Id);
        Assert.False(vm.CanAddFiles);
        var draft = Assert.Single(vm.Items);
        vm.SelectOnly([draft]);
        Assert.Contains(vm.GetContextMenuEntries(), e => e?.Text == "Move to category…" && e.IsEnabled);
        Assert.True(vm.MoveSelectedToCategoryCommand.CanExecute(null));
        dialogs.Pick = work.Id;

        await vm.MoveSelectedToCategoryAsync();

        var call = Assert.Single(calls.Of(nameof(ILibraryService.MoveToCategoryAsync)));
        Assert.Equal(deleted.Id, call[1]);
        Assert.Equal([work.Id], (await library.GetItemAsync(draft.Id))!.CategoryIds);
        Assert.Equal("Moved 1 file to Work", Assert.Single(notifications.Shown).Message);
        vm.Dispose();
    });

    [Fact]
    public void A_refused_move_shows_the_librarys_message_and_keeps_the_selection() => RunOnDispatcher(async () =>
    {
        var (library, _) = RecordingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var all = await library.GetAllCategoriesAsync();
        var images = all.Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var deleted = all.Single(c => c.BuiltInKey == ExtensionMap.DeletedKey);
        var (vm, dialogs, notifications, _) = await OpenAsync(library, images.Id);
        vm.SelectOnly(vm.Items.Take(2).ToList());
        dialogs.Pick = deleted.Id; // the real picker never offers it; the library refuses it

        await vm.MoveSelectedToCategoryAsync();

        var notice = Assert.Single(notifications.Shown);
        Assert.Equal(NotificationKind.Error, notice.Kind);
        Assert.Contains("Deleted", notice.Message, StringComparison.Ordinal);
        Assert.Equal(2, vm.SelectedCount);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ wiring

    private static async Task<(ThreadViewModel Vm, ScriptedDialogs Dialogs, RecordingNotifications Notifications, RecordingNavigator Navigator)> OpenAsync(ILibraryService library, long categoryId)
    {
        var dialogs = new ScriptedDialogs();
        var notifications = new RecordingNotifications();
        var navigator = new RecordingNavigator { CurrentCategoryId = categoryId };
        var services = new ThreadServices(
            library,
            new NoContextMenu(),
            new NoShell(),
            new NoIntake(),
            dialogs,
            notifications,
            navigator,
            new ThumbnailLoader(new NoThumbnails(), new NoMediaInfo()),
            new ThreadViewState());
        var vm = new ThreadViewModel(services);
        await vm.LoadAsync(categoryId);
        return (vm, dialogs, notifications, navigator);
    }

    private static void RunOnDispatcher(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
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
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
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
        failure?.Throw();
    }

    public sealed record Notice(string Message, NotificationKind Kind, string? ActionText, Action? Action);

    private sealed class RecordingNotifications : INotificationService
    {
        public List<Notice> Shown { get; } = [];

        public void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Action? action = null)
            => Shown.Add(new Notice(message, kind, actionText, action));
    }

    private sealed class RecordingNavigator : IShellNavigator
    {
        public long? CurrentCategoryId { get; set; }

        public List<(long CategoryId, long? FocusItemId)> Opened { get; } = [];

        public void OpenCategory(long categoryId, long? focusItemId = null) => Opened.Add((categoryId, focusItemId));
    }

    private sealed class ScriptedDialogs : IDialogService
    {
        public long? Pick { get; set; }

        public Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) => Task.FromResult(false);

        public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options) => Task.FromResult<int?>(null);

        public Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true) => Task.FromResult(Pick);

        public Task<string?> PickFileToLocateAsync(string originalFileName) => Task.FromResult<string?>(null);
    }

    private sealed class NoContextMenu : IShellContextMenuService
    {
        public ContextMenuResult Show(IntPtr ownerHwnd, IReadOnlyList<string> paths, Point screenPointPx, IReadOnlyList<CustomMenuItem> customItems)
            => ContextMenuResult.Dismissed;
    }

    private sealed class NoShell : IShellOperations
    {
        public void OpenWithDefaultApp(string path)
        {
        }

        public void ShowInExplorer(string path)
        {
        }
    }

    private sealed class NoIntake : IFileIntake
    {
        public Task AddPathsAsync(IReadOnlyList<string> paths, long? targetCategoryId, AddedBy addedBy = AddedBy.AppUi) => Task.CompletedTask;

        public Task PickAndAddFilesAsync(long? targetCategoryId) => Task.CompletedTask;

        public Task PasteAsync(long? targetCategoryId) => Task.CompletedTask;
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

/// <summary>An <see cref="ILibraryService"/> proxy that records every call with its arguments.</summary>
public class RecordingLibrary : DispatchProxy
{
    private readonly List<(string Method, object?[] Args)> _calls = [];
    private ILibraryService _inner = null!;

    public static (ILibraryService Library, RecordingLibrary Calls) Wrap(ILibraryService inner)
    {
        var proxy = Create<ILibraryService, RecordingLibrary>();
        var recorder = (RecordingLibrary)(object)proxy;
        recorder._inner = inner;
        return (proxy, recorder);
    }

    public IReadOnlyList<object?[]> Of(string method)
    {
        lock (_calls)
        {
            return _calls.Where(c => c.Method == method).Select(c => c.Args).ToList();
        }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        lock (_calls)
        {
            _calls.Add((targetMethod!.Name, args ?? []));
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>The intake's notices after a sort by type, against a real library (see <see cref="FileIntakeTypeSortTests"/> for the harness).</summary>
public sealed class BugHuntIntakeSortNoticeTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cabinette-app-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ILibraryService> _libraries = [];

    /// <summary>
    /// Finding (fixed): a drop onto another category's row in the chat list reported the files that stayed in that category
    /// as "stayed here", although "here" is the thread on screen, which they never touched. Now it names the category.
    /// </summary>
    [Fact]
    public void Drop_onto_another_categorys_row_names_where_the_files_stayed() => RunOnDispatcher(async () =>
    {
        var (library, intake, navigator, notifications, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        var work = await library.CreateCategoryAsync("Work", null);
        navigator.CurrentCategoryId = work.Id;

        await intake.AddPathsAsync([Source("a.png", "a"), Source("table.csv", "x,y")], sheets.Id);

        Assert.Empty(opened);
        Assert.Equal("Sorted by type: 1 to Images. 1 stayed in Spreadsheets.", notifications.Current?.Message);
        Assert.Empty(await library.GetThreadItemsAsync(work.Id));
        Assert.Single(await library.GetThreadItemsAsync(sheets.Id));
    });

    /// <summary>
    /// Finding (fixed): dropping a file that already is in its type category onto another type category opened that
    /// category and said "1 file sorted by type into Images" although nothing was added or moved. Now only the usual
    /// duplicate notice shows, and you stay.
    /// </summary>
    [Fact]
    public void A_file_already_in_its_type_category_is_only_reported_as_a_duplicate() => RunOnDispatcher(async () =>
    {
        var (library, intake, navigator, notifications, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        var images = await library.GetOrCreateBuiltInCategoryAsync(ExtensionMap.ImagesKey);
        await library.AddPathsAsync([Source(@"old\logo.png", "logo")], new AddRequest { TargetCategoryId = images.Id, Mode = ItemMode.Copy });
        navigator.CurrentCategoryId = sheets.Id;
        var before = (await library.GetThreadItemsAsync(images.Id)).Select(i => i.Id).ToList();

        await intake.AddPathsAsync([Source("logo.png", "logo")], sheets.Id);

        Assert.Empty(opened);
        Assert.DoesNotContain("sorted", notifications.Current?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logo.png", notifications.Current?.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(before, (await library.GetThreadItemsAsync(images.Id)).Select(i => i.Id).ToList());
        Assert.Empty(await library.GetThreadItemsAsync(sheets.Id));
    });

    /// <summary>
    /// Finding (fixed): a bubble dragged out of a thread onto a chat-list row went through the same add as files from
    /// outside, and that add sorts by type, so a photo dragged onto the Spreadsheets row was filed into Images instead. Now
    /// the thread's drag carries the item ids and a row adds those items to its category, like "Add to category…"
    /// (<c>ChatListPane.OnDrop</c> → <c>MainViewModel.AddItemsToCategoryAsync</c>; the UI wiring itself has no test).
    /// This pins down the library side of that path: the row's category is where the item goes, also a type category.
    /// </summary>
    [Fact]
    public void A_bubble_dropped_on_a_type_categorys_row_joins_that_category() => RunOnDispatcher(async () =>
    {
        var (library, _, _, _, opened) = await OpenAsync();
        var sheets = await library.GetOrCreateBuiltInCategoryAsync("spreadsheets");
        var work = await library.CreateCategoryAsync("Work", null);
        var photo = Assert.Single((await library.AddPathsAsync([Source("team.png", "team")], new AddRequest { TargetCategoryId = work.Id, Mode = ItemMode.Copy })).Added);

        await library.AddToCategoryAsync([photo.Id], sheets.Id, AddedBy.AppUi);

        var after = (await library.GetItemAsync(photo.Id))!;
        Assert.Equal(new[] { work.Id, sheets.Id }.Order(), after.CategoryIds.Order());
        Assert.Empty(opened);
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

    private void RunOnDispatcher(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
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
                    failure = ExceptionDispatchInfo.Capture(ex);
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
