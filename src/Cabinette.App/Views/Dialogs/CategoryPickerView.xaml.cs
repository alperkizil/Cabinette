using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Dialogs;

namespace Cabinette.App.Views.Dialogs;

public partial class CategoryPickerView : UserControl
{
    public CategoryPickerView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            if (List.SelectedItem is not null)
            {
                List.ScrollIntoView(List.SelectedItem);
            }
        });
    }

    private CategoryPickerViewModel? ViewModel => DataContext as CategoryPickerViewModel;

    /// <summary>Up/Down in the search box move the selection, so the picker works without the mouse.</summary>
    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.VisibleNodes.Count == 0)
        {
            return;
        }

        var index = vm.Selected is null ? -1 : vm.VisibleNodes.IndexOf(vm.Selected);
        switch (e.Key)
        {
            case Key.Down:
                vm.Selected = vm.VisibleNodes[Math.Min(vm.VisibleNodes.Count - 1, index + 1)];
                break;
            case Key.Up:
                vm.Selected = vm.VisibleNodes[Math.Max(0, index - 1)];
                break;
            default:
                return;
        }

        List.ScrollIntoView(vm.Selected);
        e.Handled = true;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } node } vm)
        {
            return;
        }

        if ((e.Key == Key.Right && !node.IsExpanded) || (e.Key == Key.Left && node.IsExpanded))
        {
            vm.ToggleExpand(node);
            e.Handled = true;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ViewModel is { } vm && vm.ChooseCommand.CanExecute(null))
        {
            vm.ChooseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
