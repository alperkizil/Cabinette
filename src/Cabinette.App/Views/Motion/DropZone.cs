using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Cabinette.App.Views.Motion;

/// <summary>A piece inside a <see cref="DropZone"/> that gets its own motion.</summary>
public enum DropZonePart
{
    None,

    /// <summary>Scales in with a spring and shrinks on drop. Without a Card, the zone itself scales.</summary>
    Card,

    /// <summary>A dashed <see cref="Shape"/> whose dashes slowly march while the zone is active.</summary>
    Frame,

    /// <summary>Bobs gently while active and dips "into the tray" on drop.</summary>
    Icon,

    /// <summary>A <see cref="DropRing"/> next to the Icon: on drop the icon turns into it, it fills, and a check pops in.</summary>
    Ring,
}

/// <summary>
/// Animates a drag-and-drop highlight: the thread's drop overlay, a chat-list row, the chat-list background, the
/// empty state and the window. Bind or set <c>DropZone.IsActive</c> and mark pieces inside with <c>DropZone.Part</c>.
/// <list type="bullet">
/// <item>Active: fades in while the Card (or the zone) springs to full size; the Frame marches, the Icon bobs.</item>
/// <item>Inactive: fades and shrinks out, then collapses (a cancelled drag never snaps off).</item>
/// <item><see cref="Receive"/> (files were dropped): the Icon dips, the Card shrinks (a zone without a Card pulses) and the zone fades.</item>
/// <item><see cref="Receive"/> on a Card with a Ring: everything but the card fades at once; the Icon dips and turns into
/// the Ring, which fills; a check pops in and holds; then the card shrinks and fades. Cosmetic, fixed timing
/// (<see cref="MotionTokens.RingEnd"/>), not tied to the real add.</item>
/// </list>
/// The zone owns its Visibility, so start it Collapsed. Its base values stay at the end state, so with animations off
/// (see <see cref="MotionSettings"/>) or when a dev tool just makes it visible, it shows exactly as designed.
/// Animations never block anything: drop handling runs at once, the motion plays on top.
/// A zone inside a recycled list container snaps to its new state when its DataContext changed meanwhile.
/// </summary>
public static class DropZone
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(DropZone), new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
        "Part", typeof(DropZonePart), typeof(DropZone), new PropertyMetadata(DropZonePart.None));

    /// <summary>The zone that last started its "received" motion, on this thread (the UI thread; per thread so tests don't mix).</summary>
    [ThreadStatic]
    private static WeakReference<FrameworkElement>? _receiving;

    private static readonly DependencyProperty RuntimeProperty = DependencyProperty.RegisterAttached(
        "Runtime", typeof(ZoneRuntime), typeof(DropZone), new PropertyMetadata(null));

    private enum Mode
    {
        Hidden,
        Active,
        Hiding,
        Receiving,
    }

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    public static DropZonePart GetPart(DependencyObject element) => (DropZonePart)element.GetValue(PartProperty);

    public static void SetPart(DependencyObject element, DropZonePart value) => element.SetValue(PartProperty, value);

    /// <summary>
    /// Files were dropped on this zone: plays the "received" motion instead of the plain exit. Call it from the drop
    /// handler, before or after clearing IsActive; it does nothing when the zone isn't showing.
    /// </summary>
    public static void Receive(FrameworkElement zone)
    {
        var rt = Runtime(zone);
        if (rt.Mode is Mode.Hidden or Mode.Receiving || !MotionSettings.IsEnabled)
        {
            return; // Nothing on screen, or no motion: clearing IsActive hides it.
        }

        FindParts(zone, rt);
        var generation = ++rt.Generation;
        rt.Mode = Mode.Receiving;
        _receiving = new WeakReference<FrameworkElement>(zone);

        // The icon lifts a little, then drops into the tray.
        if (rt.DipShift is { } dip)
        {
            var distance = DipDistance(rt.Icon!);
            dip.BeginAnimation(TranslateTransform.YProperty, Animator.Pulse(dip.Y, -distance * 0.2, TimeSpan.FromMilliseconds(80), distance, TimeSpan.FromMilliseconds(260)));
        }

        var fade = rt is { Card: { } card, Ring: { } ring } ? PlayRing(zone, rt, card, ring) : PlayPop(zone, rt);
        fade.Completed += (_, _) =>
        {
            if (rt.Generation != generation)
            {
                return; // A new drag took over.
            }

            Collapse(zone, rt);
            if (GetIsActive(zone))
            {
                Show(zone, rt); // Nobody cleared the highlight: keep showing what IsActive says.
            }
        };
        zone.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// The plain "received" motion: the card gives a small "got it" pop, then shrinks away; a zone without a card
    /// pulses the same way. Returns the zone's fade, which sets the length.
    /// </summary>
    private static AnimationTimeline PlayPop(FrameworkElement zone, ZoneRuntime rt)
    {
        var end = MotionTokens.Receive;
        if (Animator.EnsureScale(rt.Card ?? zone) is { } scale)
        {
            var current = scale.ScaleX;
            Animator.Scale(scale, rt.Card is not null
                ? Animator.Pulse(current, current * 1.03, TimeSpan.FromMilliseconds(90), 0.85, end)
                : Animator.Pulse(current, 1.04, TimeSpan.FromMilliseconds(130), 0.97, end));
        }

        return Animator.HoldThen(zone.Opacity, 0, TimeSpan.FromMilliseconds(140), end, MotionTokens.Wave);
    }

    /// <summary>
    /// The "received" motion of a card with an upload ring (times from the drop; see <see cref="MotionTokens"/>):
    /// <list type="bullet">
    /// <item>0–200 ms: everything but the card (the dim, the marching frame) fades, so the new files show behind it.</item>
    /// <item>0–240 ms: the card pops ("got it") and settles at full size; the icon dips into the tray (as before) and fades.</item>
    /// <item>60–420 ms: the ring fades and springs in where the icon was; from 80 ms it fills, 0 → 360° in 0.9 s.</item>
    /// <item>Then a check pops in with a spring and holds, and only then the card shrinks and the zone fades.</item>
    /// </list>
    /// Returns the zone's fade, which sets the total length (<see cref="MotionTokens.RingEnd"/>).
    /// </summary>
    private static AnimationTimeline PlayRing(FrameworkElement zone, ZoneRuntime rt, FrameworkElement card, DropRing ring)
    {
        static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
        var exitStart = MotionTokens.RingExitStart;
        var end = MotionTokens.RingEnd;
        rt.RingShown = true;

        foreach (var part in rt.Backdrop)
        {
            part.BeginAnimation(UIElement.OpacityProperty, Animator.To(0, MotionTokens.Exit, MotionTokens.Out));
        }

        if (Animator.EnsureScale(card) is { } scale)
        {
            Animator.Scale(scale, Animator.Keys(
                scale.ScaleX,
                (Ms(90), 1.03, MotionTokens.Out),
                (Ms(240), 1, MotionTokens.In),
                (exitStart, 1, null),
                (end, 0.85, MotionTokens.In)));
        }

        rt.Icon?.BeginAnimation(UIElement.OpacityProperty, Animator.HoldThen(rt.Icon.Opacity, 0, Ms(40), Ms(200), MotionTokens.Out));
        ring.BeginAnimation(UIElement.OpacityProperty, Animator.HoldThen(ring.Opacity, 1, Ms(60), Ms(220), MotionTokens.Out));
        if (Animator.EnsureScale(ring) is { } grow)
        {
            Animator.Scale(grow, Animator.HoldThen(0.7, 1, Ms(60), Ms(420), MotionTokens.Spring));
        }

        // Explicit starting values (not "from the current value"): a ring left over from an interrupted drop starts empty.
        var fillStart = MotionTokens.RingFillStart;
        var checkStart = MotionTokens.RingCheckStart;
        ring.BeginAnimation(DropRing.ProgressProperty, Animator.Keys(0, (fillStart, 0, null), (fillStart + MotionTokens.RingFill, 1, MotionTokens.Fill)));
        ring.BeginAnimation(DropRing.CheckProperty, Animator.Keys(0, (checkStart, 0, null), (checkStart + MotionTokens.CheckPop, 1, MotionTokens.Spring)));

        // The zone itself stays (finishing its fade-in if the drop came early) until the card leaves.
        return Animator.Keys(zone.Opacity, (Ms(150), 1, MotionTokens.Out), (exitStart, 1, null), (end, 0, MotionTokens.Wave));
    }

    /// <summary>
    /// A new drag took over during the ring motion: the backdrop and the icon fade back in, the ring fades out, and
    /// once that has played the ring's animations are removed, so the card is plain again (unless something newer runs).
    /// </summary>
    private static void UndoRing(ZoneRuntime rt, int generation)
    {
        rt.RingShown = false;
        foreach (var part in rt.Backdrop)
        {
            part.BeginAnimation(UIElement.OpacityProperty, Animator.ToBase(MotionTokens.FadeIn, MotionTokens.Out));
        }

        rt.Icon?.BeginAnimation(UIElement.OpacityProperty, Animator.ToBase(MotionTokens.FadeIn, MotionTokens.Out));
        if (rt.Ring is not { } ring)
        {
            return;
        }

        var fade = Animator.ToBase(MotionTokens.Exit, MotionTokens.Out);
        fade.Completed += (_, _) =>
        {
            if (rt.Generation == generation)
            {
                StopRing(rt);
            }
        };
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement zone)
        {
            return;
        }

        var rt = Runtime(zone);
        var active = (bool)e.NewValue;

        // A recycled list container now shows another row: its highlight must not animate from the old row's state.
        var recycled = !ReferenceEquals(rt.DataContext, zone.DataContext);
        rt.DataContext = zone.DataContext;
        if (!MotionSettings.IsEnabled || (recycled && rt.Mode != Mode.Hidden))
        {
            Snap(zone, rt, active);
        }
        else if (active)
        {
            Show(zone, rt);
        }
        else if (rt.Mode != Mode.Receiving)
        {
            Hide(zone, rt);
        }

        // While receiving, the drop motion finishes on its own and then collapses the zone.
    }

    private static void Show(FrameworkElement zone, ZoneRuntime rt)
    {
        // Another zone is still playing its "received" motion (a new drag right after a drop elsewhere): end it now,
        // so its card doesn't linger over this zone's highlight.
        if (_receiving is not null && _receiving.TryGetTarget(out var other) && !ReferenceEquals(other, zone)
            && Runtime(other) is { Mode: Mode.Receiving } otherRt)
        {
            Hide(other, otherRt);
        }

        FindParts(zone, rt);
        var fromHidden = rt.Mode == Mode.Hidden || zone.Visibility != Visibility.Visible;
        if (fromHidden)
        {
            StopAll(zone, rt);
        }

        var generation = ++rt.Generation;
        rt.Mode = Mode.Active;
        zone.Visibility = Visibility.Visible;
        zone.BeginAnimation(UIElement.OpacityProperty, Animator.To(1, MotionTokens.FadeIn, MotionTokens.Out, fromHidden ? 0 : null));

        if (Animator.EnsureScale(rt.Card ?? zone) is { } scale)
        {
            double? from = fromHidden ? (rt.Card is not null ? 0.82 : 0.94) : null;
            Animator.Scale(scale, Animator.To(1, MotionTokens.SpringIn, MotionTokens.Spring, from));
        }

        // Coming back during the drop motion: lift the icon out of the tray again (and turn the ring back into it).
        rt.DipShift?.BeginAnimation(TranslateTransform.YProperty, Animator.To(0, MotionTokens.Exit, MotionTokens.Out));
        if (rt.RingShown)
        {
            UndoRing(rt, generation);
        }

        if (!rt.Looping)
        {
            StartLoops(rt);
        }
    }

    private static void Hide(FrameworkElement zone, ZoneRuntime rt)
    {
        if (rt.Mode == Mode.Hidden)
        {
            zone.Visibility = Visibility.Collapsed;
            return;
        }

        var generation = ++rt.Generation;
        rt.Mode = Mode.Hiding;
        var fade = Animator.To(0, MotionTokens.Exit, MotionTokens.In);
        fade.Completed += (_, _) =>
        {
            if (rt.Generation == generation)
            {
                Collapse(zone, rt);
            }
        };
        zone.BeginAnimation(UIElement.OpacityProperty, fade);

        if (Animator.EnsureScale(rt.Card ?? zone) is { } scale)
        {
            Animator.Scale(scale, Animator.To(rt.Card is not null ? 0.92 : 0.97, MotionTokens.Exit, MotionTokens.In));
        }
    }

    /// <summary>Jumps straight to the end state (animations off, or a recycled container).</summary>
    private static void Snap(FrameworkElement zone, ZoneRuntime rt, bool active)
    {
        rt.Generation++;
        StopAll(zone, rt);
        rt.Mode = active ? Mode.Active : Mode.Hidden;
        zone.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Collapse(FrameworkElement zone, ZoneRuntime rt)
    {
        rt.Mode = Mode.Hidden;
        zone.Visibility = Visibility.Collapsed;
        StopAll(zone, rt);
    }

    /// <summary>Removes every animation, so all values fall back to their (end-state) base values.</summary>
    private static void StopAll(FrameworkElement zone, ZoneRuntime rt)
    {
        rt.Looping = false;
        zone.BeginAnimation(UIElement.OpacityProperty, null);
        if ((rt.Card ?? zone).RenderTransform is ScaleTransform { IsFrozen: false } scale)
        {
            Animator.StopScale(scale);
        }

        rt.Frame?.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        rt.Bob?.BeginAnimation(TranslateTransform.YProperty, null);
        rt.DipShift?.BeginAnimation(TranslateTransform.YProperty, null);
        StopRing(rt);
    }

    /// <summary>Back to the plain card: no ring or check, the icon and the backdrop at their own opacity.</summary>
    private static void StopRing(ZoneRuntime rt)
    {
        rt.RingShown = false;
        foreach (var part in rt.Backdrop)
        {
            part.BeginAnimation(UIElement.OpacityProperty, null);
        }

        rt.Icon?.BeginAnimation(UIElement.OpacityProperty, null);
        if (rt.Ring is { } ring)
        {
            ring.BeginAnimation(UIElement.OpacityProperty, null);
            ring.BeginAnimation(DropRing.ProgressProperty, null);
            ring.BeginAnimation(DropRing.CheckProperty, null);
            if (ring.RenderTransform is ScaleTransform { IsFrozen: false } grow)
            {
                Animator.StopScale(grow);
            }
        }
    }

    private static void StartLoops(ZoneRuntime rt)
    {
        rt.Looping = true;
        if (rt.Frame is { } frame && DashPeriod(frame) is > 0 and var period)
        {
            frame.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -period, MotionTokens.MarchPeriod)
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }

        if (rt.Bob is { } bob)
        {
            bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -BobAmplitude(rt.Icon!), MotionTokens.BobPeriod)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = MotionTokens.Wave,
            });
        }
    }

    /// <summary>Length of one repeat of the dash pattern, in the same units as StrokeDashOffset (stroke widths).</summary>
    internal static double DashPeriod(Shape frame)
    {
        if (frame.StrokeDashArray is not { Count: > 0 } dashes)
        {
            return 0;
        }

        var sum = dashes.Sum();
        return dashes.Count % 2 == 0 ? sum : 2 * sum;
    }

    private static double IconSize(FrameworkElement icon)
        => icon.ActualHeight > 0 ? icon.ActualHeight : icon.GetValue(TextElement.FontSizeProperty) is double size ? size : 24;

    private static double BobAmplitude(FrameworkElement icon) => Math.Clamp(IconSize(icon) * 0.16, 2, 5);

    private static double DipDistance(FrameworkElement icon) => Math.Clamp(IconSize(icon) * 0.5, 6, 16);

    private static void FindParts(FrameworkElement zone, ZoneRuntime rt)
    {
        if (rt.PartsFound)
        {
            return;
        }

        foreach (var node in VisualTreeSearch.Descendants(zone))
        {
            switch (GetPart(node))
            {
                case DropZonePart.Card when rt.Card is null && node is FrameworkElement card:
                    rt.Card = card;
                    break;
                case DropZonePart.Frame when rt.Frame is null && node is Shape frame:
                    rt.Frame = frame;
                    break;
                case DropZonePart.Icon when rt.Icon is null && node is FrameworkElement icon:
                    rt.Icon = icon;
                    if (icon.RenderTransform is MatrixTransform { Matrix.IsIdentity: true })
                    {
                        // Two shifts: one bobs, one dips, so a drop can dip while the bob carries on.
                        rt.Bob = new TranslateTransform();
                        rt.DipShift = new TranslateTransform();
                        icon.RenderTransform = new TransformGroup { Children = { rt.Bob, rt.DipShift } };
                    }

                    break;
                case DropZonePart.Ring when rt.Ring is null && node is DropRing ring:
                    rt.Ring = ring;
                    break;
            }
        }

        // The backdrop: everything in the zone beside the card (the dim, the frame), which fades first on a ring drop.
        rt.Backdrop.Clear();
        if (rt.Card is { } found)
        {
            DependencyObject child = found;
            while (!ReferenceEquals(child, zone) && VisualTreeHelper.GetParent(child) is { } parent)
            {
                if (parent is Panel panel)
                {
                    rt.Backdrop.AddRange(panel.Children.OfType<UIElement>().Where(sibling => !ReferenceEquals(sibling, child)));
                }

                child = parent;
            }
        }

        // Parts are plain children, so they exist once the zone is built; scan again later if it isn't loaded yet.
        rt.PartsFound = zone.IsLoaded;
    }

    private static ZoneRuntime Runtime(FrameworkElement zone)
    {
        if (zone.GetValue(RuntimeProperty) is not ZoneRuntime rt)
        {
            rt = new ZoneRuntime { DataContext = zone.DataContext };
            zone.SetValue(RuntimeProperty, rt);
            var runtime = rt;
            zone.DataContextChanged += (_, _) =>
            {
                // A container recycled for another row while the old row's highlight fades or plays the "received"
                // motion: stop it here. (When IsActive changes too, OnIsActiveChanged sees the new DataContext;
                // this covers the new row's IsActive staying false, which raises no change.)
                if (runtime.Mode is Mode.Hiding or Mode.Receiving)
                {
                    runtime.DataContext = zone.DataContext;
                    Snap(zone, runtime, GetIsActive(zone));
                }
            };
        }

        return rt;
    }

    private sealed class ZoneRuntime
    {
        public Mode Mode;
        public int Generation;
        public object? DataContext;
        public bool PartsFound;
        public bool Looping;
        public FrameworkElement? Card;
        public Shape? Frame;
        public FrameworkElement? Icon;
        public TranslateTransform? Bob;
        public TranslateTransform? DipShift;
        public DropRing? Ring;
        public readonly List<UIElement> Backdrop = [];

        /// <summary>The ring motion has changed the card (ring, icon, backdrop) and nothing has put it back yet.</summary>
        public bool RingShown;
    }
}
