using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// The scroll-to-item request that Enter on a file in the quick switcher (and in the main search) relies on:
/// <see cref="ThreadViewModel.FocusItem"/> waits until the thread has loaded and its view is attached, then asks the view
/// to bring the item into view (the virtualized list realizes it) and flashes it. Runs on an STA thread with a dispatcher.
/// </summary>
public sealed class ThreadFocusTests
{
    [Fact]
    public void Focus_waits_for_the_thread_to_load_and_its_view_to_attach() => RunSta(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Documents");
        var oldest = (await library.GetThreadItemsAsync(documents.Id))[0];
        using var thread = new ThreadViewModel(Services(library));
        var host = new FakeHost();

        thread.FocusItem(oldest.Id); // asked before the thread has loaded (MainViewModel asks right after LoadAsync)
        await thread.LoadAsync(documents.Id);
        await WaitAsync(TimeSpan.FromMilliseconds(150));
        Assert.Empty(host.Brought); // no view to scroll yet

        thread.AttachHost(host); // the thread view binds to it
        await WaitForAsync(() => host.Brought.Count > 0);

        var item = Assert.Single(host.Brought);
        Assert.Equal(oldest.Id, item.Id);
        Assert.True(item.IsHighlighted);
    });

    [Fact]
    public void Focus_on_a_file_hidden_by_the_thread_search_clears_the_search_first() => RunSta(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.Name == "Documents");
        var taxReturn = (await library.GetThreadItemsAsync(documents.Id)).Single(i => i.FileName == "Tax return 2025.pdf");
        using var thread = new ThreadViewModel(Services(library));
        var host = new FakeHost();
        thread.AttachHost(host);
        await thread.LoadAsync(documents.Id);
        thread.IsSearchOpen = true;
        thread.SearchText = "minutes";
        await WaitForAsync(() => thread.TotalCount == 1);

        thread.FocusItem(taxReturn.Id);
        await WaitForAsync(() => host.Brought.Count > 0);

        Assert.Equal("", thread.SearchText);
        Assert.Equal(taxReturn.Id, Assert.Single(host.Brought).Id);
    });

    private static ThreadServices Services(ILibraryService library) => new(
        library,
        NullProxy.Create<IShellContextMenuService>(),
        NullProxy.Create<IShellOperations>(),
        NullProxy.Create<IFileIntake>(),
        NullProxy.Create<IDialogService>(),
        NullProxy.Create<INotificationService>(),
        NullProxy.Create<IShellNavigator>(),
        new ThumbnailLoader(NullProxy.Create<IThumbnailService>(), NullProxy.Create<IMediaInfoService>()),
        new ThreadViewState())
    {
        Directory = new CategoryDirectory(library),
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private static async Task WaitAsync(TimeSpan duration)
    {
        await Task.Delay(duration);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Runs an async test body on a fresh STA thread whose dispatcher keeps running until the body ends.</summary>
    private static void RunSta(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Task task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                task = Task.FromException(ex);
            }

            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            if (task.Exception is { } error)
            {
                failure = ExceptionDispatchInfo.Capture(error.InnerException ?? error);
            }

            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(1)))
        {
            throw new TimeoutException("STA test body did not finish.");
        }

        failure?.Throw();
    }

    private sealed class FakeHost : IThreadViewHost
    {
        public List<ThreadItemViewModel> Brought { get; } = [];

        public IntPtr OwnerHandle => IntPtr.Zero;

        public void ScrollToEnd()
        {
        }

        public void NotifyNewItems(int count)
        {
        }

        public void BringItemIntoView(ThreadItemViewModel item) => Brought.Add(item);

        public bool ShowTagEditor(TagEditorViewModel editor) => false;

        public void ShowMediaViewer(IReadOnlyList<ThreadItemViewModel> items, int index)
        {
        }
    }
}

/// <summary>An interface implementation that does nothing: completed tasks, default results.</summary>
public class NullProxy : DispatchProxy
{
    public static T Create<T>()
        where T : class
        => Create<T, NullProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [Default(result)]);
        }

        return Default(type);
    }

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
