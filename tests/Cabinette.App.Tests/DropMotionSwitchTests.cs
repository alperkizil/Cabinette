using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cabinette.App.Views.Motion;

namespace Cabinette.App.Tests;

/// <summary>
/// The reduced-motion switch and the drop highlights' states, on real WPF elements (no window, so animation clocks
/// don't advance: these tests check where things start and end, not the motion). <see cref="MotionSettings.Override"/>
/// is per thread, and each test sets it on its own STA thread, so parallel tests can't affect each other.
/// </summary>
public sealed class DropMotionSwitchTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void Windows_animation_setting_decides_unless_overridden(bool windowsOn, bool? overrideValue, bool expected)
        => Assert.Equal(expected, MotionSettings.Resolve(windowsOn, overrideValue));

    [Fact]
    public void With_animations_off_a_drop_zone_shows_and_hides_its_end_state_at_once()
        => WithMotion(false, () =>
        {
            var (zone, card, frame) = BuildZone();

            DropZone.SetIsActive(zone, true);
            Assert.Equal(Visibility.Visible, zone.Visibility);
            Assert.Equal(1, zone.Opacity);
            Assert.False(zone.HasAnimatedProperties);
            Assert.False(frame.HasAnimatedProperties); // no marching dashes either

            DropZone.Receive(zone); // nothing to play
            DropZone.SetIsActive(zone, false);
            Assert.Equal(Visibility.Collapsed, zone.Visibility);
            Assert.False(zone.HasAnimatedProperties);
            Assert.Equal(1, ScaleOf(card));
        });

    [Fact]
    public void With_animations_on_the_zone_fades_in_from_nothing_and_leaving_does_not_snap_it_off()
        => WithMotion(true, () =>
        {
            var (zone, card, frame) = BuildZone();

            DropZone.SetIsActive(zone, true);
            Assert.Equal(Visibility.Visible, zone.Visibility);
            Assert.True(zone.HasAnimatedProperties);
            Assert.True(frame.HasAnimatedProperties); // the dashes march
            Assert.IsType<ScaleTransform>(card.RenderTransform); // the card springs in

            // Leaving (a cancelled drag) fades and shrinks out: still visible until that has played.
            DropZone.SetIsActive(zone, false);
            Assert.Equal(Visibility.Visible, zone.Visibility);
        });

    [Fact]
    public void A_recycled_container_snaps_to_its_new_rows_state()
        => WithMotion(true, () =>
        {
            var (zone, _, _) = BuildZone();
            zone.DataContext = "row A";
            DropZone.SetIsActive(zone, true);

            // The container now shows row B, which isn't a drop target: no exit animation from row A's highlight.
            zone.DataContext = "row B";
            DropZone.SetIsActive(zone, false);
            Assert.Equal(Visibility.Collapsed, zone.Visibility);
            Assert.False(zone.HasAnimatedProperties);
        });

    [Fact]
    public void Dimming_switches_at_once_with_animations_off()
        => WithMotion(false, () =>
        {
            var content = new Grid();
            Dimmer.SetDimmedOpacity(content, 0.2);

            Dimmer.SetIsDimmed(content, true);
            Assert.Equal(0.2, content.Opacity, 3);
            Assert.False(content.HasAnimatedProperties);

            Dimmer.SetIsDimmed(content, false);
            Assert.Equal(1, content.Opacity, 3);
        });

    [Fact]
    public void Dashes_march_by_one_full_pattern()
        => RunSta(() =>
        {
            Assert.Equal(9, DropZone.DashPeriod(new Rectangle { StrokeDashArray = [5, 4] }));
            Assert.Equal(10, DropZone.DashPeriod(new Rectangle { StrokeDashArray = [5] })); // an odd pattern repeats twice
            Assert.Equal(0, DropZone.DashPeriod(new Rectangle()));
        });

    private static (Grid Zone, Border Card, Rectangle Frame) BuildZone()
    {
        var frame = new Rectangle { StrokeDashArray = [5, 4] };
        DropZone.SetPart(frame, DropZonePart.Frame);
        var card = new Border();
        DropZone.SetPart(card, DropZonePart.Card);
        var zone = new Grid { Visibility = Visibility.Collapsed, Children = { frame, card } };
        return (zone, card, frame);
    }

    private static double ScaleOf(UIElement element) => element.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1;

    private static void WithMotion(bool enabled, Action test) => RunSta(() =>
    {
        MotionSettings.Override = enabled;
        try
        {
            test();
        }
        finally
        {
            MotionSettings.Override = null;
        }
    });

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
