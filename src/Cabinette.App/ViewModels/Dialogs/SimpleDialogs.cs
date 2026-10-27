using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>Single-line text input with live validation.</summary>
public sealed partial class PromptDialogViewModel : DialogViewModelBase
{
    private readonly Func<string, string?>? _validate;
    private bool _touched;

    public PromptDialogViewModel(string title, string label, string initialText, Func<string, string?>? validate, bool selectFileNameWithoutExtension)
        : base(title)
    {
        Label = label;
        _text = initialText;
        _validate = validate;
        SelectFileNameWithoutExtension = selectFileNameWithoutExtension;
        _error = null;
    }

    public string Label { get; }

    public bool SelectFileNameWithoutExtension { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _text;

    [ObservableProperty]
    private string? _error;

    public override void Accept()
    {
        if (OkCommand.CanExecute(null))
        {
            OkCommand.Execute(null);
        }
    }

    partial void OnTextChanged(string value)
    {
        _touched = true;
        Error = Validate(value);
    }

    private string? Validate(string value) => _validate?.Invoke(value);

    private bool CanOk() => !_touched || Validate(Text) is null;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        var error = Validate(Text);
        if (error is not null)
        {
            _touched = true;
            Error = error;
            return;
        }

        Close(Text);
    }
}

/// <summary>Yes/no question, optionally destructive, optionally with a list of details (e.g. failed files).</summary>
public sealed partial class ConfirmDialogViewModel : DialogViewModelBase
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDestructive, bool showCancel = true, IReadOnlyList<string>? details = null)
        : base(title)
    {
        Message = message;
        ConfirmText = confirmText;
        IsDestructive = isDestructive;
        ShowCancel = showCancel;
        Details = details ?? [];
    }

    public string Message { get; }

    public string ConfirmText { get; }

    public bool IsDestructive { get; }

    public bool ShowCancel { get; }

    public IReadOnlyList<string> Details { get; }

    public bool HasDetails => Details.Count > 0;

    public override void Accept() => Close(true);

    [RelayCommand]
    private void Confirm() => Close(true);
}

public sealed record ChoiceOption(int Index, string Text, bool IsPrimary);

/// <summary>One button per option; returns the chosen index.</summary>
public sealed partial class ChooseDialogViewModel : DialogViewModelBase
{
    public ChooseDialogViewModel(string title, string message, IReadOnlyList<string> options)
        : base(title)
    {
        Message = message;
        Options = options.Select((text, i) => new ChoiceOption(i, text, i == 0)).ToList();
    }

    public string Message { get; }

    public IReadOnlyList<ChoiceOption> Options { get; }

    [RelayCommand]
    private void Choose(ChoiceOption? option)
    {
        if (option is not null)
        {
            Close(option.Index);
        }
    }
}
