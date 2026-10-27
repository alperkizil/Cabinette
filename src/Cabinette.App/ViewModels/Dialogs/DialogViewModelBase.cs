using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>Base of the in-window modal dialogs shown by <see cref="Services.DialogService"/>.</summary>
public abstract partial class DialogViewModelBase : ObservableObject
{
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected DialogViewModelBase(string title)
    {
        Title = title;
    }

    public string Title { get; }

    /// <summary>Card width in DIPs (clamped to the window).</summary>
    public virtual double Width => 440;

    /// <summary>Card max height in DIPs, or NaN for content height (clamped to the window).</summary>
    public virtual double Height => double.NaN;

    /// <summary>
    /// True: the card sits near the top of the window instead of in the middle, so a card that grows and shrinks with its
    /// content (the quick switcher's result list) keeps its search box still.
    /// </summary>
    public virtual bool IsTopAligned => false;

    public Task<object?> Completion => _completion.Task;

    public bool IsClosed { get; private set; }

    public void Close(object? result)
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        OnClosing();
        _completion.TrySetResult(result);
    }

    /// <summary>Enter pressed outside a button: run the primary action.</summary>
    public virtual void Accept()
    {
    }

    /// <summary>Esc pressed: cancel the dialog. A dialog that must not just vanish (e.g. running progress) overrides this.</summary>
    public virtual void Dismiss() => Close(null);

    protected virtual void OnClosing()
    {
    }

    [RelayCommand]
    private void Cancel() => Close(null);
}
