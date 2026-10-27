using System.Windows;
using System.Windows.Media;

namespace Cabinette.App.Views.Motion;

/// <summary>
/// The "upload" ring of a drop card: a faint track, an arc that fills clockwise from the top (<see cref="Progress"/>
/// 0–1) and a check mark that pops in (<see cref="Check"/>: its scale, which may overshoot 1 with a spring).
/// Purely cosmetic. Put it next to the card's icon (same cell) and mark it <c>DropZone.Part="Ring"</c>: after a drop,
/// <see cref="DropZone"/> turns the icon into the ring, fills it, pops the check, then lets the card go.
/// At rest the ring is not shown (Opacity defaults to 0, Progress and Check to 0), so the card shows its plain icon.
/// It takes the size of its cell; a negative margin lets it reach a little past a small icon without changing the layout.
/// </summary>
public sealed class DropRing : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(DropRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(DropRing), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DropRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CheckProperty = DependencyProperty.Register(
        nameof(Check), typeof(double), typeof(DropRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How strongly the track (the not yet filled part) shows, relative to <see cref="Stroke"/>.</summary>
    private const double TrackOpacity = 0.28;

    static DropRing()
    {
        // Hidden at rest: DropZone fades it in on a drop, and removing its animations hides it again.
        OpacityProperty.OverrideMetadata(typeof(DropRing), new UIPropertyMetadata(0.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(DropRing), new UIPropertyMetadata(false));
    }

    /// <summary>The ring, its track (faint) and the check. Use a <c>Cab.*</c> brush through DynamicResource.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>How much of the ring is filled, 0–1 (clockwise from the top).</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>The check mark's scale around the center: 0 hides it, 1 is full size (a spring briefly goes past 1).</summary>
    public double Check
    {
        get => (double)GetValue(CheckProperty);
        set => SetValue(CheckProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => default; // takes the cell it is given

    protected override void OnRender(DrawingContext dc)
    {
        var thickness = StrokeThickness;
        var size = Math.Min(RenderSize.Width, RenderSize.Height);
        if (Stroke is not { } brush || size <= thickness || thickness <= 0)
        {
            return;
        }

        // Not frozen: freezing a pen freezes its brush too, and that is the shared theme brush.
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var radius = (size - thickness) / 2;

        dc.PushOpacity(TrackOpacity);
        dc.DrawEllipse(null, pen, center, radius, radius);
        dc.Pop();

        if (Arc(center, radius, Progress) is { } arc)
        {
            dc.DrawGeometry(null, pen, arc);
        }
        else if (Progress >= 1)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
        }

        var check = Check;
        if (check > 0)
        {
            // The check's own stroke is a bit bolder than the ring's; it fades in over the first part of its pop.
            var checkPen = new Pen(brush, thickness * 1.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.PushTransform(new ScaleTransform(check, check, center.X, center.Y));
            dc.PushOpacity(Math.Clamp(check * 2, 0, 1));
            dc.DrawGeometry(null, checkPen, CheckMark(center, radius - (thickness / 2)));
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>The filled part, clockwise from 12 o'clock; null when nothing or everything is filled.</summary>
    internal static StreamGeometry? Arc(Point center, double radius, double progress)
    {
        if (progress is <= 0 or >= 1 || double.IsNaN(progress))
        {
            return null;
        }

        var angle = progress * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, isLargeArc: progress > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>A check mark sized to sit inside a circle of the given (inner) radius.</summary>
    private static StreamGeometry CheckMark(Point center, double inner)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X - (0.42 * inner), center.Y + (0.02 * inner)), isFilled: false, isClosed: false);
            context.LineTo(new Point(center.X - (0.12 * inner), center.Y + (0.31 * inner)), isStroked: true, isSmoothJoin: true);
            context.LineTo(new Point(center.X + (0.44 * inner), center.Y - (0.28 * inner)), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }
}
