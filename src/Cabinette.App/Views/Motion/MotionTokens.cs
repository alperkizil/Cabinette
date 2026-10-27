using System.Windows;
using System.Windows.Media.Animation;

namespace Cabinette.App.Views.Motion;

/// <summary>
/// The "playful" motion style in one place: entrances are springy (a slight overshoot) and take 300–450 ms, exits are
/// quicker and plain so adding files often still feels fast. Easing functions are frozen, so they can be shared.
/// </summary>
internal static class MotionTokens
{
    /// <summary>Overlay fade in.</summary>
    public static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(200);

    /// <summary>Overlay card / highlight scaling in, with a spring.</summary>
    public static readonly TimeSpan SpringIn = TimeSpan.FromMilliseconds(400);

    /// <summary>Leaving a drop zone without dropping: fade and shrink out.</summary>
    public static readonly TimeSpan Exit = TimeSpan.FromMilliseconds(200);

    /// <summary>The drop itself: icon dips into the tray, the overlay shrinks and fades.</summary>
    public static readonly TimeSpan Receive = TimeSpan.FromMilliseconds(420);

    /// <summary>A drop card's upload ring filling up (0 → 360°), from the drop. Cosmetic: not tied to the real add.</summary>
    public static readonly TimeSpan RingFill = TimeSpan.FromMilliseconds(900);

    /// <summary>The check popping in once the ring is full.</summary>
    public static readonly TimeSpan CheckPop = TimeSpan.FromMilliseconds(240);

    /// <summary>The check stays a moment before the card leaves.</summary>
    public static readonly TimeSpan CheckHold = TimeSpan.FromMilliseconds(250);

    /// <summary>After the check: the card shrinks and fades away (the fading part of <see cref="Receive"/>).</summary>
    public static readonly TimeSpan RingExit = TimeSpan.FromMilliseconds(280);

    /// <summary>The ring starts filling once the icon has mostly turned into it (times below count from the drop).</summary>
    public static readonly TimeSpan RingFillStart = TimeSpan.FromMilliseconds(80);

    public static TimeSpan RingCheckStart => RingFillStart + RingFill;

    public static TimeSpan RingExitStart => RingCheckStart + CheckPop + CheckHold;

    /// <summary>The whole "received" motion of a card with a ring; the zone collapses at this point.</summary>
    public static TimeSpan RingEnd => RingExitStart + RingExit;

    /// <summary>A chat-list row jumping to its new place.</summary>
    public static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(420);

    /// <summary>The rows it passes, making room.</summary>
    public static readonly TimeSpan Shift = TimeSpan.FromMilliseconds(360);

    /// <summary>A new row fading in.</summary>
    public static readonly TimeSpan Appear = TimeSpan.FromMilliseconds(260);

    /// <summary>Unread badge pop.</summary>
    public static readonly TimeSpan Pop = TimeSpan.FromMilliseconds(450);

    /// <summary>One full step of the dashed border's "marching ants".</summary>
    public static readonly TimeSpan MarchPeriod = TimeSpan.FromMilliseconds(900);

    /// <summary>Half a bob of the drop icon (it auto-reverses).</summary>
    public static readonly TimeSpan BobPeriod = TimeSpan.FromMilliseconds(700);

    /// <summary>Overlay cards and highlights: a modest overshoot (about 2 % of the card's size).</summary>
    public static readonly IEasingFunction Spring = Freeze(new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });

    /// <summary>Gliding rows: an even smaller overshoot, so rows never visibly overlap their neighbours.</summary>
    public static readonly IEasingFunction SoftSpring = Freeze(new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut });

    /// <summary>The badge pop: one small bounce.</summary>
    public static readonly IEasingFunction Bounce = Freeze(new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut });

    public static readonly IEasingFunction Out = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static readonly IEasingFunction In = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

    public static readonly IEasingFunction Wave = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>The upload ring filling: eases out, but gentler than <see cref="Out"/>, so the fill reads as steady progress.</summary>
    public static readonly IEasingFunction Fill = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Freeze<T>(T ease)
        where T : Freezable, IEasingFunction
    {
        ease.Freeze();
        return ease;
    }
}
