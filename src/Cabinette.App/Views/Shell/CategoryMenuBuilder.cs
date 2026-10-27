using System.Windows;
using System.Windows.Controls;
using Cabinette.App.Localization;
using Cabinette.App.ViewModels.Shell;
using Cabinette.Core.Models;
using Wpf.Ui.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace Cabinette.App.Views.Shell;

/// <summary>Builds the right-click menu of a chat-list row (styled by the implicit ContextMenu/MenuItem styles).</summary>
public static class CategoryMenuBuilder
{
    public static ContextMenu Build(MainViewModel vm, CategoryRowViewModel row)
    {
        var menu = new ContextMenu();
        var category = row.Category;
        var isSystem = category.Kind == CategoryKind.System;

        Add(menu, "Menu_Open", SymbolRegular.Open20, () => vm.OpenThreadAsync(row));
        if (row.IsStarredRow)
        {
            // A view, not a category: nothing to pin, rename, open in Explorer or delete.
            return menu;
        }

        if (!isSystem)
        {
            Add(
                menu,
                category.IsPinned ? "Menu_Unpin" : "Menu_Pin",
                category.IsPinned ? SymbolRegular.PinOff20 : SymbolRegular.Pin20,
                () => vm.TogglePinAsync(row));
        }

        menu.Items.Add(new Separator());
        if (!isSystem)
        {
            Add(menu, "Menu_Rename", SymbolRegular.Rename20, () => vm.RenameAsync(row));
            Add(menu, "Menu_ChangeIcon", SymbolRegular.ImageEdit20, () => vm.ChangeIconAsync(row));
            Add(menu, "Menu_ResetIcon", SymbolRegular.ArrowCounterclockwise20, () => vm.ResetIconAsync(row));
            Add(menu, "Menu_NewSubcategory", SymbolRegular.FolderAdd20, () => vm.NewSubcategoryAsync(row));
        }

        Add(menu, "Menu_OpenFolder", SymbolRegular.FolderOpen20, () => vm.OpenFolderAsync(row));
        menu.Items.Add(new Separator());

        var delete = isSystem
            ? Add(menu, "Menu_EmptyDeleted", SymbolRegular.DeleteDismiss20, vm.EmptyDeletedAsync)
            : Add(menu, "Menu_Delete", SymbolRegular.Delete20, () => vm.DeleteAsync(row));
        delete.SetResourceReference(Control.ForegroundProperty, "Cab.Danger");
        return menu;
    }

    private static MenuItem Add(ContextMenu menu, string textKey, SymbolRegular symbol, Func<Task> action)
    {
        var item = new MenuItem
        {
            Header = L.Get(textKey),
            Icon = new SymbolIcon { Symbol = symbol, FontSize = 18 },
        };
        item.Click += (_, e) =>
        {
            e.Handled = true;
            _ = action();
        };
        menu.Items.Add(item);
        return item;
    }

    public static void Open(ContextMenu menu, UIElement target, bool atMouse)
    {
        menu.PlacementTarget = target;
        menu.Placement = atMouse ? System.Windows.Controls.Primitives.PlacementMode.MousePoint : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
