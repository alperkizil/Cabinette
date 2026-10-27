using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cabinette.App.Views.Motion;

/// <summary>Small helpers shared by the animated pieces (written the same way as the app's other code-behind animations).</summary>
internal static class Animator
{
    /// <summary>From the current (possibly animated) value to <paramref name="to"/>, or from <paramref name="from"/> when given.</summary>
    public static DoubleAnimation To(double to, TimeSpan duration, IEasingFunction? ease, double? from = null)
        => new() { From = from, To = to, Duration = duration, EasingFunction = ease };

    /// <summary>Holds <paramref name="current"/> until <paramref name="hold"/>, then eases to <paramref name="to"/> by <paramref name="end"/>.</summary>
    public static DoubleAnimationUsingKeyFrames HoldThen(double current, double to, TimeSpan hold, TimeSpan end, IEasingFunction ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = end };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(hold)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(end), ease));
        return animation;
    }

    /// <summary>Starts at <paramref name="current"/>, eases to <paramref name="peak"/> by <paramref name="peakAt"/>, then to <paramref name="to"/> by <paramref name="end"/>.</summary>
    public static DoubleAnimationUsingKeyFrames Pulse(double current, double peak, TimeSpan peakAt, double to, TimeSpan end)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = end };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(peakAt), MotionTokens.Out));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(end), MotionTokens.In));
        return animation;
    }

    /// <summary>
    /// Starts at <paramref name="current"/> and passes through each key in turn. A null ease is linear, so a key with the
    /// same value as the one before holds it.
    /// </summary>
    public static DoubleAnimationUsingKeyFrames Keys(double current, params (TimeSpan At, double Value, IEasingFunction? Ease)[] keys)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = keys[^1].At };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (var (at, value, ease) in keys)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(at), ease));
        }

        return animation;
    }

    /// <summary>From the current value back to the property's own (base) value: an animation with neither From nor To.</summary>
    public static DoubleAnimation ToBase(TimeSpan duration, IEasingFunction ease) => new() { Duration = duration, EasingFunction = ease };

    public static void Scale(ScaleTransform scale, AnimationTimeline animation)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    public static void StopScale(ScaleTransform scale)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>
    /// The element's own <see cref="ScaleTransform"/>, created (centered) when it has none yet. Null when the element
    /// already uses another kind of render transform, which is left alone.
    /// </summary>
    public static ScaleTransform? EnsureScale(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case ScaleTransform { IsFrozen: false } existing:
                return existing;
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
                var scale = new ScaleTransform();
                element.RenderTransform = scale;
                if (element.RenderTransformOrigin == default)
                {
                    element.RenderTransformOrigin = new Point(0.5, 0.5);
                }

                return scale;
            default:
                return null;
        }
    }

    /// <summary>The element's own <see cref="TranslateTransform"/>, created when it has none yet (null if it uses another kind).</summary>
    public static TranslateTransform? EnsureTranslate(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform { IsFrozen: false } existing:
                return existing;
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
                var shift = new TranslateTransform();
                element.RenderTransform = shift;
                return shift;
            default:
                return null;
        }
    }
}
