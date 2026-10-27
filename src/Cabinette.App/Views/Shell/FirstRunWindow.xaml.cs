using System.Windows.Input;
using Cabinette.App.ViewModels.Shell;
using Wpf.Ui.Controls;

namespace Cabinette.App.Views.Shell;

/// <summary>Shown once, before the main window, to choose the vault folder (or restore a backup).</summary>
public partial class FirstRunWindow : FluentWindow
{
    public FirstRunWindow(FirstRunViewModel vm)
    {
        InitializeComponent();
        Services.UiScale.FitWindow(this);
        DataContext = vm;
        vm.Completed += (_, _) =>
        {
            Completed = true;
            Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !vm.Dialogs.IsAnyOpen && vm.StartCommand.CanExecute(null))
            {
                vm.StartCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>True when the vault was set up (false when the window was closed instead).</summary>
    public bool Completed { get; private set; }

    /// <summary>Asked before closing (e.g. a restore is running and must not be interrupted). Null = always allowed.</summary>
    public Func<bool>? CanClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!Completed && CanClose is { } canClose && !canClose())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
