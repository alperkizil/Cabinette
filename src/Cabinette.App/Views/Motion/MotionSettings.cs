using System.Windows;

namespace Cabinette.App.Views.Motion;

/// <summary>
/// Whether Cabinette plays its animations. It follows Windows' "Animation effects" setting (Settings › Accessibility ›
/// Visual effects), which WPF reports as <see cref="SystemParameters.ClientAreaAnimation"/> and refreshes when the
/// setting changes, so switching it takes effect without a restart. With animations off, every animated piece shows
/// its end state at once. Animations are purely visual: nothing waits for them.
/// </summary>
internal static class MotionSettings
{
    [ThreadStatic]
    private static bool? _override;

    /// <summary>
    /// Forces animations on (true) or off (false); null follows Windows. Used by the screenshot tool and tests. It applies
    /// to the calling thread only (WPF elements belong to one thread), so tests on their own STA threads can't affect
    /// each other when they run in parallel.
    /// </summary>
    public static bool? Override
    {
        get => _override;
        set => _override = value;
    }

    public static bool IsEnabled => Resolve(SystemParameters.ClientAreaAnimation, Override);

    /// <summary>The rule on its own: an override wins, otherwise Windows' setting decides.</summary>
    public static bool Resolve(bool windowsAnimationsOn, bool? overrideValue) => overrideValue ?? windowsAnimationsOn;
}
