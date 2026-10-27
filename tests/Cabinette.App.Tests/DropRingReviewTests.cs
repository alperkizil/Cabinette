using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Cabinette.App.Views.Motion;
using static Cabinette.App.Tests.HiddenWpf;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt of the drop card's upload ring (2026-10-22, branch 098f33e; docs/review/bughunt-2026-10-22-ring): the
/// interrupt paths the branch's own tests don't walk, plus the regression tests for its findings. Real WPF elements
/// shaped like the thread's drop overlay, in a hidden window that is never shown; the dispatcher is pumped so the
/// animations really play.
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class DropRingReviewTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(4);

    // ------------------------------------------------------------------ WPF semantics the generation guards rely on

    [Fact]
    public void A_replaced_animation_still_raises_Completed_at_its_own_end()
        => WithMotion(true, () =>
        {
            // DropZone replaces the zone's fade (SnapshotAndReplace) and relies on its generation counter, not on the
            // old clock going quiet. Pin down which one it is: does a replaced clock still complete?
            var box = new Border { Width = 10, Height = 10 };
            using var source = Source(box, 50, 50);
            box.UpdateLayout();

            var completed = false;
            var first = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            first.Completed += (_, _) => completed = true;
            box.BeginAnimation(UIElement.OpacityProperty, first);
            Pump(TimeSpan.FromMilliseconds(30));
            box.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(500)));
            Pump(TimeSpan.FromMilliseconds(400));

            // Either answer is fine for DropZone (every Completed checks the generation), but record the fact.
            Assert.True(completed, "a replaced clock does not complete: generation checks are the only guard");
        });

    // ------------------------------------------------------------------ interrupt paths

    [Fact]
    public void Enter_leave_enter_inside_the_undo_window_leaves_a_plain_card_and_the_next_drop_plays_a_full_ring()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Progress > 0.3, Plenty), "the ring never got under way");

            // A new drag comes in (UndoRing starts its 200 ms fades), jitters out and back in within that window.
            DropZone.SetIsActive(z.Zone, true);
            Pump(TimeSpan.FromMilliseconds(60));
            DropZone.SetIsActive(z.Zone, false);
            Pump(TimeSpan.FromMilliseconds(60));
            DropZone.SetIsActive(z.Zone, true);

            // Neither UndoRing's nor Hide's completion matches the generation any more: nothing calls StopRing until
            // the zone collapses. The card must still look plain, with the overlay fully shown.
            Pump(TimeSpan.FromMilliseconds(500));
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            Assert.True(z.Zone.Opacity > 0.99, $"zone opacity {z.Zone.Opacity}");
            Assert.True(z.Ring.Opacity < 0.001, $"ring opacity {z.Ring.Opacity}");
            Assert.True(z.Icon.Opacity > 0.999, $"icon opacity {z.Icon.Opacity}");
            Assert.True(z.Dim.Opacity > 0.499, $"dim opacity {z.Dim.Opacity}");
            Assert.True(z.Frame.Opacity > 0.999, $"frame opacity {z.Frame.Opacity}");
            Assert.True(Scale(z.Card) > 0.999, $"card scale {Scale(z.Card)}");

            // The interrupted ring's own fill and check clocks are still attached (they hold 1), only invisible.
            Pump(MotionTokens.RingEnd);
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            Assert.True(z.Ring.Opacity < 0.001);

            // The next drop must start from an empty ring and end plain.
            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Opacity > 0.5, Plenty), "the next drop showed no ring");
            Assert.True(z.Ring.Progress < 0.3, $"the next ring did not start empty (progress {z.Ring.Progress})");
            Assert.True(z.Ring.Check == 0, $"the next ring started with a check ({z.Ring.Check})");
            Assert.True(PumpUntil(() => z.Ring.Check > 0, Plenty), "the next ring never reached the check");
            Assert.True(PumpUntil(() => z.Zone.Visibility == Visibility.Collapsed, Plenty), "the zone never collapsed");
            AssertPlain(z);
        });

    [Fact]
    public void A_drop_inside_the_undo_window_replays_the_ring_from_empty_and_UndoRing_does_not_kill_it()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Progress > 0.5, Plenty), "the ring never got under way");

            // More files come in and are dropped 60 ms later, while UndoRing is still fading the ring out.
            DropZone.SetIsActive(z.Zone, true);
            Pump(TimeSpan.FromMilliseconds(60));
            Assert.True(z.Ring.Opacity is > 0.05 and < 0.95, $"expected the ring mid-fade, opacity {z.Ring.Opacity}");
            var clock = Stopwatch.StartNew();
            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);

            // UndoRing's fade would have completed around 140 ms from here; its StopRing must not run on the new ring.
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.True(z.Ring.Opacity > 0.99, $"the ring was killed (opacity {z.Ring.Opacity})");
            Assert.True(z.Ring.HasAnimatedProperties, "the ring's animations were removed by the stale UndoRing");
            Assert.True(z.Icon.Opacity < 0.01, $"the icon came back (opacity {z.Icon.Opacity})");
            Assert.True(z.Ring.Progress is > 0.05 and < 0.7, $"progress {z.Ring.Progress} after {clock.ElapsedMilliseconds} ms: not a restart from empty");

            Assert.True(PumpUntil(() => z.Ring.Check > 0, Plenty), "the check never showed");
            Assert.True(PumpUntil(() => z.Zone.Visibility == Visibility.Collapsed, Plenty), "the zone never collapsed");
            AssertPlain(z);
        });

    [Fact]
    public void A_drag_entering_during_the_exit_shrink_brings_the_plain_card_back_to_full_size()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => Scale(z.Card) < 0.95, Plenty), "the exit never started");
            Assert.True(z.Zone.Opacity < 0.95, $"zone not fading yet ({z.Zone.Opacity})");

            DropZone.SetIsActive(z.Zone, true); // a new drag, right as the card leaves
            Assert.True(PumpUntil(() => !z.Ring.HasAnimatedProperties, Plenty), "the ring kept its animations");
            Assert.True(PumpUntil(() => z.Zone.Opacity > 0.999 && Scale(z.Card) > 0.999, Plenty), $"zone {z.Zone.Opacity}, card {Scale(z.Card)}");
            Assert.Equal(0, z.Ring.Opacity);
            Assert.Equal(0, z.Ring.Check);
            Assert.True(z.Icon.Opacity > 0.999);
            Assert.True(z.Dim.Opacity > 0.499);
            Assert.True(z.Frame.HasAnimatedProperties, "the frame keeps marching");

            // The interrupted motion's end passes without collapsing the overlay.
            Pump(MotionTokens.RingExit + TimeSpan.FromMilliseconds(100));
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            Assert.True(z.Zone.Opacity > 0.999);
        });

    [Fact]
    public void Receive_then_enter_then_leave_in_one_batch_collapses_to_a_plain_card()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            // A drop and a DragOver/DragLeave jitter inside the same dispatcher batch: no clock ticks in between.
            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            DropZone.SetIsActive(z.Zone, true);
            DropZone.SetIsActive(z.Zone, false);

            Assert.True(PumpUntil(() => z.Zone.Visibility == Visibility.Collapsed, Plenty), "the zone never collapsed");
            AssertPlain(z);
            Pump(MotionTokens.RingEnd);
            AssertPlain(z);
        });

    [Fact]
    public void A_thread_switch_mid_ring_snaps_the_overlay_off_and_the_next_drag_shows_the_plain_icon()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            z.Zone.DataContext = "thread A";
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Check > 0, Plenty), "the check never showed");

            z.Zone.DataContext = "thread B"; // ThreadView keeps its overlay; only its DataContext changes
            AssertPlain(z);

            Pump(MotionTokens.RingEnd);
            AssertPlain(z);

            DropZone.SetIsActive(z.Zone, true);
            Assert.True(PumpUntil(() => z.Zone.Opacity > 0.999, Plenty), "the overlay never came back");
            Assert.Equal(0, z.Ring.Opacity);
            Assert.Equal(0, z.Ring.Check);
            Assert.Equal(1, z.Icon.Opacity);
            Assert.Equal(0.5, z.Dim.Opacity);
        });

    [Fact]
    public void Going_home_mid_ring_snaps_the_overlay_off()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            z.Zone.DataContext = "thread A";
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Progress > 0.3, Plenty), "the ring never got under way");

            z.Zone.DataContext = null; // CurrentThread = null
            AssertPlain(z);
        });

    // ------------------------------------------------------------------ rendering

    /// <summary>
    /// Regression (bug hunt of the upload ring): DropRing.OnRender used to freeze its Pen, which deep-froze the shared
    /// theme brush it was given. The brush must stay as it was.
    /// </summary>
    [Fact]
    public void Rendering_a_ring_leaves_the_brush_it_was_given_alone()
        => RunSta(() =>
        {
            var brush = new SolidColorBrush(Colors.Orange);
            var ring = new DropRing { Stroke = brush, Opacity = 1, Progress = 0.4, Width = 40, Height = 40 };
            using var source = Source(ring, 60, 60);
            ring.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(60, 60, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(ring);
            Assert.False(brush.IsFrozen, "the brush given to the ring stays mutable");
        });

    /// <summary>Regression: with the frozen Pen, a brush that cannot freeze (animated) threw during layout and crashed the app.</summary>
    [Fact]
    public void Rendering_a_ring_with_an_animated_brush_works()
        => RunSta(() =>
        {
            var animated = new SolidColorBrush(Colors.Orange);
            animated.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Red, TimeSpan.FromSeconds(10)));
            Assert.False(animated.CanFreeze);
            var ring = new DropRing { Stroke = animated, Opacity = 1, Progress = 0.4, Width = 40, Height = 40 };
            using var source = Source(ring, 60, 60); // hosting it lays it out, which renders it
            ring.UpdateLayout();
            Assert.True(ring.IsArrangeValid);
        });

    [Fact]
    public void The_ring_takes_its_cell_and_a_negative_margin_does_not_grow_an_auto_sized_cell()
        => RunSta(() =>
        {
            // The pills: an auto-sized Grid holds the icon and the ring (Margin -2). The Grid must stay the icon's size.
            var icon = new TextBlock { Text = "x", FontSize = 16, Width = 16, Height = 21 };
            var ring = new DropRing { Margin = new Thickness(-2), Stroke = Brushes.Orange, StrokeThickness = 2 };
            var cell = new Grid { Margin = new Thickness(0, 0, 8, 0), Children = { icon, ring } };
            var text = new TextBlock { Text = "Drop", FontSize = 13 };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { cell, text } };
            var pill = new Border { Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = panel };
            using var source = Source(pill, 300, 100);
            pill.UpdateLayout();

            // The same pill without the ring, for comparison.
            var plainPanel = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "x", FontSize = 16, Width = 16, Height = 21, Margin = new Thickness(0, 0, 8, 0) }, new TextBlock { Text = "Drop", FontSize = 13 } } };
            var plain = new Border { Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = plainPanel };
            using var source2 = Source(plain, 300, 100);
            plain.UpdateLayout();

            Assert.Equal(plain.ActualWidth, pill.ActualWidth, 1);
            Assert.Equal(plain.ActualHeight, pill.ActualHeight, 1);
            Assert.Equal(16, cell.ActualWidth, 1);
            Assert.Equal(21, cell.ActualHeight, 1);
            Assert.Equal(20, ring.RenderSize.Width, 1);
            Assert.Equal(25, ring.RenderSize.Height, 1);
            Assert.Equal(text.TransformToAncestor(pill).Transform(default).X, plainPanel.Children[1].TransformToAncestor(plain).Transform(default).X, 1);
        });

    /// <summary>
    /// Regression (bug hunt of the upload ring, finding 2): after a drop on one zone (the window overlay, say), a drag into
    /// another zone (the thread) within the 1.75 s ring used to leave the first card with its ring on top of the new
    /// highlight. Now the new zone ends the other zone's "received" motion with the short exit.
    /// </summary>
    [Fact]
    public void A_drag_into_another_zone_ends_the_ring_of_the_previous_drop()
        => WithMotion(true, () =>
        {
            var first = new CardZone();
            var second = new CardZone();
            using var source = Source(new Grid { Children = { first.Zone, second.Zone } }, 600, 400);
            ShowAndSettle(first);

            DropZone.Receive(first.Zone);
            DropZone.SetIsActive(first.Zone, false);
            Assert.True(PumpUntil(() => first.Ring.Progress > 0.1, Plenty), "the ring never got under way");

            var started = Stopwatch.StartNew();
            DropZone.SetIsActive(second.Zone, true);
            Assert.True(PumpUntil(() => first.Zone.Visibility == Visibility.Collapsed, Plenty), "the first card never left");
            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(900), $"the first card lingered {started.ElapsedMilliseconds} ms");
            AssertPlain(first);
            Assert.Equal(Visibility.Visible, second.Zone.Visibility);
        });

    // ------------------------------------------------------------------ helpers

    private static double Scale(UIElement element) => element.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1;

    private static void ShowAndSettle(CardZone z)
    {
        z.Zone.UpdateLayout();
        DropZone.SetIsActive(z.Zone, true);
        Assert.True(PumpUntil(() => z.Zone.Opacity > 0.999, Plenty), "the overlay never faded in");
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, z.Ring.Opacity);
        Assert.Equal(1, z.Icon.Opacity);
    }

    private static void AssertPlain(CardZone z)
    {
        Assert.Equal(Visibility.Collapsed, z.Zone.Visibility);
        foreach (var element in new UIElement[] { z.Zone, z.Dim, z.Frame, z.Card, z.Icon, z.Ring })
        {
            Assert.False(element.HasAnimatedProperties, $"{element.GetType().Name} still animates");
        }

        Assert.Equal(0, z.Ring.Opacity);
        Assert.Equal(0, z.Ring.Progress);
        Assert.Equal(0, z.Ring.Check);
        Assert.Equal(1, z.Icon.Opacity);
        Assert.Equal(0.5, z.Dim.Opacity);
        Assert.Equal(1, z.Frame.Opacity);
        Assert.Equal(1, z.Zone.Opacity);
        Assert.Equal(1, Scale(z.Card));
        Assert.Equal(1, Scale(z.Ring));
    }

    /// <summary>Shaped like the thread's drop overlay: a dim, a dashed frame, and a card whose icon cell holds the ring.</summary>
    private sealed class CardZone
    {
        public CardZone()
        {
            Dim = new Rectangle { Fill = Brushes.Black, Opacity = 0.5 };
            Frame = new Rectangle { Margin = new Thickness(18), Stroke = Brushes.Orange, StrokeThickness = 2.5, StrokeDashArray = [5, 4] };
            DropZone.SetPart(Frame, DropZonePart.Frame);
            Icon = new TextBlock { Text = "↓", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DropZone.SetPart(Icon, DropZonePart.Icon);
            Ring = new DropRing { Stroke = Brushes.Orange, StrokeThickness = 3.5 };
            DropZone.SetPart(Ring, DropZonePart.Ring);
            Card = new Border
            {
                Padding = new Thickness(36, 28, 36, 28),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                Child = new StackPanel { Children = { new Grid { Width = 68, Height = 68, Children = { new Ellipse { Fill = Brushes.Orange, Opacity = 0.16 }, Icon, Ring } } } },
            };
            DropZone.SetPart(Card, DropZonePart.Card);
            Zone = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Children = { Dim, Frame, Card } };
        }

        public Grid Zone { get; }

        public Rectangle Dim { get; }

        public Rectangle Frame { get; }

        public Border Card { get; }

        public TextBlock Icon { get; }

        public DropRing Ring { get; }
    }
}
