using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Dialogs;

namespace Cabinette.App.Views.Dialogs;

/// <summary>
/// The quick switcher's card. The search box keeps the focus: Up/Down move the selection, Shift+Enter opens a file in its
/// app, and a click acts on the row. Enter and Esc are handled by the dialog host (Accept / Dismiss).
/// </summary>
public partial class QuickSwitcherView : UserControl
{
    public QuickSwitcherView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SearchBox.Focus());
    }

    private QuickSwitcherViewModel? ViewModel => DataContext as QuickSwitcherViewModel;

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None:
                vm.MoveSelection(1);
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None:
                vm.MoveSelection(-1);
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                _ = vm.ActivateSelectedAsync(openFile: true);
                break;
            default:
                return;
        }

        if (vm.Selected is not null)
        {
            List.ScrollIntoView(vm.Selected);
        }

        e.Handled = true;
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: QuickSwitcherRow row } && ViewModel is { } vm)
        {
            vm.Activate(row, openFile: Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
    }
}
