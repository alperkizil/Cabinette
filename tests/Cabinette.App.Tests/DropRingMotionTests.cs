using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cabinette.App.Views.Motion;
using static Cabinette.App.Tests.HiddenWpf;

namespace Cabinette.App.Tests;

/// <summary>
/// The drop card's upload ring (cosmetic): after a drop the icon turns into a ring that fills, a check pops in and
/// holds, then the card leaves. Real WPF elements shaped like the thread's drop overlay, in a hidden window that is
/// never shown; the dispatcher is pumped so the animations really play.
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class DropRingMotionTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(4);

    [Fact]
    public void After_a_drop_the_ring_fills_then_the_check_pops_and_holds_then_the_zone_collapses()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            var clock = Stopwatch.StartNew();
            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false); // the drop handler clears the highlight at once; the motion plays on
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            Assert.Equal(0, z.Ring.Check);

            // Only the card stays: the dim and the marching frame fade right away, so the thread behind shows.
            Assert.True(PumpUntil(() => z.Dim.Opacity < 0.01 && z.Frame.Opacity < 0.01, Plenty), "the backdrop never faded");
            Assert.True(z.Zone.Opacity > 0.99, "the card must stay while the ring plays");

            // The icon has turned into the ring, which fills; no check yet.
            Assert.True(PumpUntil(() => z.Ring.Progress is > 0.3 and < 0.9, Plenty), $"the ring never got under way (progress {z.Ring.Progress})");
            Assert.True(z.Ring.Opacity > 0.99);
            Assert.True(z.Icon.Opacity < 0.01);
            Assert.Equal(0, z.Ring.Check);

            // Once it is full, the check pops in (with a spring: it briefly grows past full size).
            Assert.True(PumpUntil(() => z.Ring.Check > 0, Plenty), "the check never showed");
            Assert.Equal(1, z.Ring.Progress, 6);
            Assert.True(PumpUntil(() => z.Ring.Check > 1.001, Plenty), "the check has no spring");
            Assert.True(z.Zone.Opacity > 0.99);

            // It holds, and only then does the card leave: never before the fill, the pop and the hold have played.
            Assert.True(PumpUntil(() => z.Zone.Opacity < 0.98, Plenty), "the card never left");
            Assert.True(clock.Elapsed >= MotionTokens.RingExitStart, $"the card left after {clock.ElapsedMilliseconds} ms");
            Assert.Equal(1, z.Ring.Check, 3);

            Assert.True(PumpUntil(() => z.Zone.Visibility == Visibility.Collapsed, Plenty), "the zone never collapsed");
            AssertPlain(z);
        });

    [Fact]
    public void A_new_drag_during_the_ring_takes_over_and_leaves_the_plain_icon()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Progress > 0.3, Plenty), "the ring never got under way");

            // More files are dragged in while the ring fills: the overlay shows again, with its plain icon.
            DropZone.SetIsActive(z.Zone, true);
            Assert.True(PumpUntil(() => !z.Ring.HasAnimatedProperties, Plenty), "the ring kept its animations");
            Assert.Equal(0, z.Ring.Opacity);
            Assert.Equal(0, z.Ring.Progress);
            Assert.Equal(0, z.Ring.Check);
            Assert.True(PumpUntil(() => z.Icon.Opacity > 0.999 && z.Dim.Opacity > 0.499 && z.Frame.Opacity > 0.999, Plenty), "the icon or the backdrop did not come back");
            Assert.True(z.Frame.HasAnimatedProperties, "the frame keeps marching");

            // The interrupted motion's end passes without collapsing the overlay or bringing the ring back.
            Pump(MotionTokens.RingEnd);
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            Assert.True(z.Zone.Opacity > 0.99);
            Assert.Equal(0, z.Ring.Opacity);
            Assert.Equal(1, z.Icon.Opacity);

            // The next drop plays the ring from empty again, and ends in the plain state.
            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Opacity > 0.5, Plenty), "the second drop showed no ring");
            Assert.True(z.Ring.Progress < 0.6, $"the second ring did not start empty (progress {z.Ring.Progress})");
            Assert.Equal(0, z.Ring.Check);
            Assert.True(PumpUntil(() => z.Zone.Visibility == Visibility.Collapsed, Plenty), "the zone never collapsed");
            AssertPlain(z);
        });

    [Fact]
    public void A_drag_that_takes_over_and_then_leaves_collapses_to_the_plain_card()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Check > 0, Plenty), "the check never showed");

            DropZone.SetIsActive(z.Zone, true);  // a new drag comes in while the check shows...
            DropZone.SetIsActive(z.Zone, false); // ...and leaves straight away
            Assert.True(PumpUntil(() => z.Zone.Visibility == Visibility.Collapsed, Plenty), "the zone never collapsed");
            AssertPlain(z);

            DropZone.SetIsActive(z.Zone, true); // the next drag shows the plain icon, not a check
            Assert.Equal(0, z.Ring.Opacity);
            Assert.Equal(1, z.Icon.Opacity);
        });

    [Fact]
    public void A_recycled_container_during_the_ring_snaps_back_to_the_plain_card()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            z.Zone.DataContext = "row A";
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Progress > 0.2, Plenty), "the ring never got under way");

            // The container now shows another row, which is no drop target (the binding delivers false again).
            z.Zone.DataContext = "row B";
            DropZone.SetIsActive(z.Zone, false);
            AssertPlain(z);

            // The old motion's completion must not touch the new row either.
            Pump(MotionTokens.RingEnd);
            AssertPlain(z);
        });

    [Fact]
    public void With_animations_off_there_is_no_ring()
        => WithMotion(false, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            z.Zone.UpdateLayout();

            DropZone.SetIsActive(z.Zone, true);
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            DropZone.Receive(z.Zone);
            Assert.False(z.Ring.HasAnimatedProperties);
            DropZone.SetIsActive(z.Zone, false);
            Assert.Equal(Visibility.Collapsed, z.Zone.Visibility);

            Pump(TimeSpan.FromMilliseconds(300));
            AssertPlain(z);
        });

    [Fact]
    public void Turning_animations_off_during_the_ring_snaps_to_the_plain_card()
        => WithMotion(true, () =>
        {
            var z = new CardZone();
            using var source = Source(z.Zone, 600, 400);
            ShowAndSettle(z);

            DropZone.Receive(z.Zone);
            DropZone.SetIsActive(z.Zone, false);
            Assert.True(PumpUntil(() => z.Ring.Progress > 0.2, Plenty), "the ring never got under way");

            MotionSettings.Override = false; // Windows' "Animation effects" switched off meanwhile
            DropZone.SetIsActive(z.Zone, true); // the next drag
            Assert.Equal(Visibility.Visible, z.Zone.Visibility);
            Assert.False(z.Zone.HasAnimatedProperties);
            Assert.False(z.Ring.HasAnimatedProperties);
            Assert.Equal(0, z.Ring.Opacity);
            Assert.Equal(1, z.Icon.Opacity);
            Assert.Equal(0.5, z.Dim.Opacity);
        });

    [Fact]
    public void A_row_highlight_without_a_card_keeps_its_short_pulse()
        => WithMotion(true, () =>
        {
            var zone = new Grid { Visibility = Visibility.Collapsed, Width = 300, Height = 60, Children = { new Border() } };
            using var source = Source(zone, 400, 100);
            DropZone.SetIsActive(zone, true);
            Pump(TimeSpan.FromMilliseconds(450));

            var clock = Stopwatch.StartNew();
            DropZone.Receive(zone);
            DropZone.SetIsActive(zone, false);
            Assert.True(PumpUntil(() => zone.Visibility == Visibility.Collapsed, Plenty), "the row highlight never collapsed");
            Assert.True(clock.Elapsed < MotionTokens.RingExitStart, $"the pulse took {clock.ElapsedMilliseconds} ms");
        });

    [Fact]
    public void The_arc_fills_clockwise_from_the_top()
        => RunSta(() =>
        {
            var center = new Point(50, 50);
            Assert.Null(DropRing.Arc(center, 40, 0));
            Assert.Null(DropRing.Arc(center, 40, 1)); // drawn as a whole circle instead

            // A quarter: from 12 o'clock to 3 o'clock.
            var quarter = DropRing.Arc(center, 40, 0.25)!.Bounds;
            Assert.Equal(50, quarter.Left, 1);
            Assert.Equal(10, quarter.Top, 1);
            Assert.Equal(90, quarter.Right, 1);
            Assert.Equal(50, quarter.Bottom, 1);

            // Three quarters reach round to 9 o'clock, over the bottom.
            var most = DropRing.Arc(center, 40, 0.75)!.Bounds;
            Assert.Equal(10, most.Left, 1);
            Assert.Equal(90, most.Bottom, 1);
        });

    // ------------------------------------------------------------------ helpers

    private static void ShowAndSettle(CardZone z)
    {
        z.Zone.UpdateLayout();
        DropZone.SetIsActive(z.Zone, true);
        Assert.True(PumpUntil(() => z.Zone.Opacity > 0.999, Plenty), "the overlay never faded in");
        Pump(TimeSpan.FromMilliseconds(300)); // the card's spring settles
        Assert.Equal(0, z.Ring.Opacity); // at rest the card shows its plain icon
        Assert.Equal(1, z.Icon.Opacity);
    }

    /// <summary>The card as designed, with nothing animated: no ring or check, the icon and the backdrop at their own opacity.</summary>
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
        Assert.Equal(1, z.Card.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1);
        Assert.Equal(1, z.Ring.RenderTransform is ScaleTransform grow ? grow.ScaleX : 1);
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
