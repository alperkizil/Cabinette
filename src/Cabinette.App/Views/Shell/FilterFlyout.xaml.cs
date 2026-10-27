using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cabinette.App.ViewModels.Shell;

namespace Cabinette.App.Views.Shell;

/// <summary>Search filters (type, date added, size, copy/pointer, missing, extension), drawn as an in-window flyout.</summary>
public partial class FilterFlyout : UserControl
{
    public FilterFlyout()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Card.RenderTransform = new TranslateTransform();
                Card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
                Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            }
        };
    }

    private void OnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsFilterOpen = false;
            e.Handled = true;
        }
    }
}
