using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Dialogs;

namespace Cabinette.App.Views.Dialogs;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // ------------------------------------------------------------------ Settings › AI filter lists

    /// <summary>An activity filter's list opened: focus its current choice so the arrow keys and Enter work.</summary>
    private void OnFilterPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: { } child })
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var buttons = Descendants<Button>(child).Where(b => b.IsVisible).ToList();
            var current = buttons.FirstOrDefault(b => b.DataContext is AiFilterOption { IsSelected: true }) ?? buttons.FirstOrDefault();
            current?.Focus();
        });
    }

    /// <summary>The list closed: focus goes back to its chip (unless the user clicked elsewhere).</summary>
    private void OnFilterPopupClosed(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement chip } popup && (Keyboard.FocusedElement is null || IsInside(popup.Child, Keyboard.FocusedElement as DependencyObject)))
        {
            chip.Focus();
        }
    }

    /// <summary>Esc closes only the list, not the whole Settings dialog.</summary>
    private void OnFilterPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is FrameworkElement { DataContext: AiFilterChoice choice })
        {
            choice.IsOpen = false;
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject? root, DependencyObject? element)
    {
        for (var node = element; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
