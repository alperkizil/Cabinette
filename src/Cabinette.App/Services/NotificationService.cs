using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cabinette.App.Services;

/// <summary>
/// Snackbar notices at the bottom of the chat list: one at a time, queued, auto-dismissed, with an optional action.
/// Also hosts the progress cards of running operations. Safe to call from any thread.
/// </summary>
public sealed partial class NotificationService : ObservableObject, INotificationService
{
    private const int MaxQueued = 12;
    private readonly Queue<NotificationViewModel> _queue = new();
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;

    public NotificationService()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (Current is not null && !IsPaused)
            {
                Dismiss(Current);
            }
        };
    }

    /// <summary>The notice on screen, or null.</summary>
    [ObservableProperty]
    private NotificationViewModel? _current;

    /// <summary>True while the mouse is over the snackbar (auto-dismiss waits).</summary>
    [ObservableProperty]
    private bool _isPaused;

    public ObservableCollection<ProgressTaskViewModel> Tasks { get; } = [];

    /// <summary>
    /// Whether the user is looking at the app, so an in-app notice is seen: the window is shown and active. Anywhere
    /// else (in the tray, minimized, or behind Explorer) a notice would dismiss itself unseen, so changes made outside
    /// the window (the vault monitor, the Explorer entries) go to a Windows toast instead.
    /// </summary>
    public static bool IsSeen(Window? window) => window is { IsVisible: true, IsActive: true };

    public void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Action? action = null)
    {
        if (_dispatcher.CheckAccess())
        {
            Enqueue(message, kind, actionText, action);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Enqueue(message, kind, actionText, action));
        }
    }

    public ProgressTaskViewModel BeginTask(string title)
    {
        var task = new ProgressTaskViewModel(title);
        Tasks.Add(task);
        return task;
    }

    public void EndTask(ProgressTaskViewModel task) => Tasks.Remove(task);

    partial void OnIsPausedChanged(bool value)
    {
        if (!value && Current is not null)
        {
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(2.5);
            _timer.Start();
        }
    }

    private void Enqueue(string message, NotificationKind kind, string? actionText, Action? action)
    {
        var notice = new NotificationViewModel(message, kind, actionText, action, Dismiss);
        if (Current is null)
        {
            Display(notice);
            return;
        }

        // Same text as the visible notice: just keep it up longer.
        if (Current.Message == message && Current.Kind == kind)
        {
            Restart(Current);
            return;
        }

        if (_queue.Count >= MaxQueued)
        {
            _queue.Dequeue();
        }

        _queue.Enqueue(notice);

        // Shorten the wait for the current one so the queue moves on.
        if (_timer.IsEnabled && _timer.Interval > TimeSpan.FromSeconds(2))
        {
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Start();
        }
    }

    private void Display(NotificationViewModel notice)
    {
        Current = notice;
        Restart(notice);
    }

    private void Restart(NotificationViewModel notice)
    {
        _timer.Stop();
        _timer.Interval = notice.Duration;
        _timer.Start();
    }

    private void Dismiss(NotificationViewModel notice)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Dismiss(notice));
            return;
        }

        if (!ReferenceEquals(Current, notice))
        {
            return;
        }

        _timer.Stop();
        Current = null;
        if (_queue.Count > 0)
        {
            var next = _queue.Dequeue();
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (Current is null)
                {
                    Display(next);
                }
                else
                {
                    _queue.Enqueue(next);
                }
            });
        }
    }
}
