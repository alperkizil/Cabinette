using Cabinette.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>One snackbar notice.</summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly Action<NotificationViewModel> _dismiss;

    public NotificationViewModel(string message, NotificationKind kind, string? actionText, Action? action, Action<NotificationViewModel> dismiss)
    {
        Message = message;
        Kind = kind;
        ActionText = actionText;
        Action = action;
        _dismiss = dismiss;
    }

    public string Message { get; }

    public NotificationKind Kind { get; }

    public string? ActionText { get; }

    public Action? Action { get; }

    public bool HasAction => ActionText is not null && Action is not null;

    /// <summary>Errors and notices with an action stay longer.</summary>
    public TimeSpan Duration => Kind == NotificationKind.Error || HasAction ? TimeSpan.FromSeconds(7) : TimeSpan.FromSeconds(4);

    [RelayCommand]
    private void RunAction()
    {
        _dismiss(this);
        try
        {
            Action?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("Notification action failed.", ex);
        }
    }

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}

/// <summary>A running operation shown as a small non-blocking progress card (e.g. adding files).</summary>
public sealed partial class ProgressTaskViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    public ProgressTaskViewModel(string title)
    {
        _title = title;
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private bool _isCancelling;

    public CancellationToken Token => _cts.Token;

    [RelayCommand]
    private void Cancel()
    {
        IsCancelling = true;
        _cts.Cancel();
    }
}
