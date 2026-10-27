using System.Windows;
using System.Windows.Controls;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Motion;

namespace Cabinette.App.Views.Shell;

/// <summary>Right pane when no thread is open. Files dropped here are auto-categorized.</summary>
public partial class EmptyStateView : UserControl
{
    private readonly DropHighlightWatchdog _dropWatchdog;

    public EmptyStateView()
    {
        InitializeComponent();
        _dropWatchdog = new DropHighlightWatchdog(() =>
        {
            if (Vm is { } vm)
            {
                vm.IsEmptyStateDropTarget = false;
            }
        });
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (Vm is { } vm)
        {
            vm.IsEmptyStateDropTarget = hasFiles;
        }

        _dropWatchdog.Touch();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(this);
        if ((position.X <= 0 || position.Y <= 0 || position.X >= ActualWidth || position.Y >= ActualHeight) && Vm is { } vm)
        {
            _dropWatchdog.Stop();
            vm.IsEmptyStateDropTarget = false;
            return;
        }

        // Cancelled drags (Esc) and fast exits can report a position inside; the watchdog clears those.
        _dropWatchdog.Touch();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropWatchdog.Stop();
        if (Vm is not { } vm)
        {
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is { Length: > 0 })
        {
            DropZone.Receive(DropHighlight); // pulses and fades; the add starts right away
        }

        vm.IsEmptyStateDropTarget = false;
        if (paths is { Length: > 0 })
        {
            _ = vm.DropAsync(paths, null);
        }
    }
}
