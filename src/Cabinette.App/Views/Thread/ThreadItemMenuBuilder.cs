using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cabinette.App.ViewModels.Thread;
using Wpf.Ui.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// Builds the styled right-click menu for files in a thread. It uses the app's implicit ContextMenu/MenuItem styles,
/// so it looks exactly like the category menu. "Show more options" opens the Windows classic menu.
/// </summary>
public static class ThreadItemMenuBuilder
{
    /// <param name="screenPointPx">Where the menu opens, in physical pixels (passed on to "Show more options").</param>
    public static ContextMenu? Build(ThreadViewModel vm, Point screenPointPx)
    {
        var entries = vm.GetContextMenuEntries();
        if (entries.Count == 0)
        {
            return null;
        }

        var menu = new ContextMenu();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem
            {
                Header = entry.Text,
                Icon = new SymbolIcon { Symbol = entry.Symbol, FontSize = 18 },
                IsEnabled = entry.IsEnabled,
            };
            if (entry.IsDanger)
            {
                item.SetResourceReference(Control.ForegroundProperty, "Cab.Danger");
            }

            var id = entry.Id;
            item.Click += (_, e) =>
            {
                e.Handled = true;

                // Let the styled menu close before a modal one (the Windows menu or a dialog) opens.
                menu.Dispatcher.BeginInvoke(() => vm.RunContextMenuAction(id, screenPointPx));
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Opens at the mouse, or (keyboard) relative to <paramref name="target"/>.</summary>
    public static void Open(ContextMenu menu, UIElement target, Point? relativeToTarget)
    {
        menu.PlacementTarget = target;
        if (relativeToTarget is { } at)
        {
            menu.Placement = PlacementMode.Relative;
            menu.HorizontalOffset = at.X;
            menu.VerticalOffset = at.Y;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }

        menu.IsOpen = true;
    }
}
