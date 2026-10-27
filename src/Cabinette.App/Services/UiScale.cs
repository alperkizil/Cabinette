using System.Windows;
using System.Windows.Media;
using Cabinette.Core.Models;

namespace Cabinette.App.Services;

/// <summary>
/// App-wide text size (Settings › Appearance). Everything is scaled through one shared transform resource,
/// <c>{DynamicResource Cab.UiScaleTransform}</c>, used as LayoutTransform by window contents, dialogs, popups,
/// context menus and tooltips, so text, icons and spacing grow together and nothing gets cramped.
/// </summary>
public static class UiScale
{
    public const string TransformKey = "Cab.UiScaleTransform";

    public static TextSize Size { get; private set; } = TextSize.Medium;

    public static double Factor { get; private set; } = 1.0;

    public static double FactorFor(TextSize size) => size switch
    {
        TextSize.Small => 0.9,
        TextSize.Large => 1.15,
        TextSize.ExtraLarge => 1.3,
        _ => 1.0,
    };

    /// <summary>Applies a size live. Call on the UI thread.</summary>
    public static void Apply(TextSize size)
    {
        Size = size;
        Factor = FactorFor(size);
        var transform = new ScaleTransform(Factor, Factor);
        transform.Freeze();
        Application.Current.Resources[TransformKey] = transform;
    }

    /// <summary>Grows a fixed-size dialog window so its scaled content still fits. Call before showing it.</summary>
    public static void FitWindow(Window window)
    {
        if (Factor == 1.0)
        {
            return;
        }

        if (!double.IsNaN(window.Width))
        {
            window.Width *= Factor;
        }

        if (!double.IsNaN(window.Height))
        {
            window.Height *= Factor;
        }

        window.MinWidth *= Factor;
        window.MinHeight *= Factor;
    }
}
