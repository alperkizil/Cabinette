using System.Windows.Controls;
using System.Windows.Threading;

namespace Cabinette.App.Views.Dialogs;

public partial class NewCategoryDialogView : UserControl
{
    public NewCategoryDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NameBox.Focus());
    }
}
