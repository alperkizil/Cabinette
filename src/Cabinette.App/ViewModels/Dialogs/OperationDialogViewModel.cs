using System.Collections.ObjectModel;
using Cabinette.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>Color of the dialog's icon badge.</summary>
public enum DialogTone
{
    Info,
    Success,
    Warning,
    Danger,
}

public enum OperationDialogChoice
{
    Primary,
    Secondary,
}

/// <summary>One "label: value" row; the value can be filled in later (e.g. after counting files).</summary>
public sealed partial class FactViewModel(string label, string value) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private string _value = value;
}

/// <summary>
/// Confirmation and result dialog for vault operations: icon, title, message, a list of facts (from, to, files, size),
/// an optional warning note, an optional list of details (skipped files), an optional switch, and up to three buttons
/// (Cancel, secondary, primary). Returns <see cref="OperationDialogChoice"/> or null when cancelled.
/// </summary>
public sealed partial class OperationDialogViewModel : DialogViewModelBase
{
    public OperationDialogViewModel(string title, string primaryText)
        : base(title)
    {
        PrimaryText = primaryText;
    }

    public override double Width => 520;

    public SymbolRegular Symbol { get; init; } = SymbolRegular.Info24;

    public DialogTone Tone { get; init; } = DialogTone.Info;

    public string? Message { get; init; }

    public ObservableCollection<FactViewModel> Facts { get; } = [];

    public bool HasFacts => Facts.Count > 0;

    /// <summary>A highlighted warning under the facts (e.g. not enough free space). Can be set later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public string? DetailsTitle { get; init; }

    public IReadOnlyList<string> Details { get; init; } = [];

    public bool HasDetails => Details.Count > 0;

    /// <summary>Text of an optional switch (e.g. "Copy linked files into the vault first").</summary>
    public string? CheckText { get; init; }

    public string? CheckHint { get; init; }

    public bool HasCheck => CheckText is not null;

    [ObservableProperty]
    private bool _isChecked;

    public string PrimaryText { get; }

    public bool IsPrimaryDestructive { get; init; }

    public string? SecondaryText { get; init; }

    public bool HasSecondary => SecondaryText is not null;

    public bool ShowCancel { get; init; } = true;

    public string CancelText { get; init; } = L.Get("Common_Cancel");

    public FactViewModel AddFact(string label, string value)
    {
        var fact = new FactViewModel(label, value);
        Facts.Add(fact);
        OnPropertyChanged(nameof(HasFacts));
        return fact;
    }

    public override void Accept() => Close(OperationDialogChoice.Primary);

    /// <summary>Esc: cancel, or (without a Cancel button) the safe primary choice.</summary>
    public override void Dismiss() => Close(ShowCancel ? null : OperationDialogChoice.Primary);

    [RelayCommand]
    private void Primary() => Close(OperationDialogChoice.Primary);

    [RelayCommand]
    private void Secondary() => Close(OperationDialogChoice.Secondary);
}
