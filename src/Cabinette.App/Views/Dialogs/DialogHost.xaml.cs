using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cabinette.App.Services;

namespace Cabinette.App.Views.Dialogs;

/// <summary>
/// Overlay layer for <see cref="DialogService"/>: keeps focus inside the top dialog, Esc cancels, Enter runs the
/// primary action, and focus returns to where it was when the dialog closes.
/// </summary>
public partial class DialogHost : UserControl
{
    public DialogHost()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private DialogService? Service => DataContext as DialogService;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var top = Service?.Top;
        if (top is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (IsInOpenDropDown(e.OriginalSource as DependencyObject, this))
                {
                    // An open drop-down list or popup closes itself first; the next Esc closes the dialog.
                    return;
                }

                top.Dismiss();
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                if (Keyboard.FocusedElement is ButtonBase or ListBoxItem || Keyboard.FocusedElement is TextBox { AcceptsReturn: true })
                {
                    return;
                }

                top.Accept();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Whether a key comes from an open drop-down list or popup inside the dialog. Those live in their own window, so the key
    /// reaches the host from another presentation source than the host's.
    /// </summary>
    internal static bool IsInOpenDropDown(DependencyObject? source, Visual host)
    {
        if (source is ComboBox { IsDropDownOpen: true })
        {
            return true;
        }

        return source is Visual visual
            && PresentationSource.FromVisual(visual) is { } popup
            && !ReferenceEquals(popup, PresentationSource.FromVisual(host));
    }

    private void OnLayerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid layer)
        {
            return;
        }

        // Remember where focus was so it can return there.
        layer.Tag = Keyboard.FocusedElement;

        if (layer.FindName("Scrim") is Border scrim)
        {
            scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }

        if (layer.FindName("Card") is Border card)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            if (card.RenderTransform is ScaleTransform scale)
            {
                var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }

            // Let the dialog view focus its own control first; otherwise focus the first focusable element.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (Keyboard.FocusedElement is DependencyObject focused && IsDescendant(card, focused))
                {
                    return;
                }

                card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            });
        }
    }

    private void OnLayerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid { Tag: IInputElement previous } layer)
        {
            layer.Tag = null;
            if (previous is UIElement { IsVisible: true, Focusable: true } element)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => element.Focus());
            }
        }
    }

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject node)
    {
        for (var current = node; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
