using System.Windows;

namespace Cabinette.App.Views.Motion;

/// <summary>
/// Fades an element to <c>DimmedOpacity</c> while <c>IsDimmed</c> is true (the content behind a drop highlight), and
/// back. With animations off it switches at once.
/// </summary>
public static class Dimmer
{
    public static readonly DependencyProperty IsDimmedProperty = DependencyProperty.RegisterAttached(
        "IsDimmed", typeof(bool), typeof(Dimmer), new PropertyMetadata(false, OnIsDimmedChanged));

    public static readonly DependencyProperty DimmedOpacityProperty = DependencyProperty.RegisterAttached(
        "DimmedOpacity", typeof(double), typeof(Dimmer), new PropertyMetadata(0.2));

    public static bool GetIsDimmed(DependencyObject element) => (bool)element.GetValue(IsDimmedProperty);

    public static void SetIsDimmed(DependencyObject element, bool value) => element.SetValue(IsDimmedProperty, value);

    public static double GetDimmedOpacity(DependencyObject element) => (double)element.GetValue(DimmedOpacityProperty);

    public static void SetDimmedOpacity(DependencyObject element, double value) => element.SetValue(DimmedOpacityProperty, value);

    private static void OnIsDimmedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        var dimmed = (bool)e.NewValue;
        var from = element.Opacity;
        var to = dimmed ? GetDimmedOpacity(d) : 1.0;

        // The new value becomes the element's own; the animation (removed when done) only covers the way there.
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = to;
        if (MotionSettings.IsEnabled)
        {
            var fade = Animator.To(to, dimmed ? MotionTokens.FadeIn : MotionTokens.Exit, MotionTokens.Out, from);
            fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
