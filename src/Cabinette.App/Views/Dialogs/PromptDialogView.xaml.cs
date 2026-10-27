using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Dialogs;

namespace Cabinette.App.Views.Dialogs;

public partial class PromptDialogView : UserControl
{
    public PromptDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Input.Focus();
            if (DataContext is PromptDialogViewModel { SelectFileNameWithoutExtension: true } vm)
            {
                var extension = Path.GetExtension(vm.Text);
                var length = extension.Length > 0 && extension.Length < vm.Text.Length ? vm.Text.Length - extension.Length : vm.Text.Length;
                Input.Select(0, length);
            }
            else
            {
                Input.SelectAll();
            }
        });
    }
}
