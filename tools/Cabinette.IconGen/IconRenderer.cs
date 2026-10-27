using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.IconGen;

/// <summary>Tray status shown as a badge in the bottom-right corner of the tray icon.</summary>
internal enum TrayBadge
{
    None,
    Syncing,
    Paused,
    Error,
}

/// <summary>Renders the logo at exact pixel sizes, optionally with a tray status badge.</summary>
internal static class IconRenderer
{
    // Tool-only constants (these are image pixels, not UI colors).
    private static readonly Color SyncingColor = Color.FromRgb(0x00, 0xA8, 0x84);
    private static readonly Color PausedColor = Color.FromRgb(0x5F, 0x6B, 0x73);
    private static readonly Color ErrorColor = Color.FromRgb(0xD9, 0x30, 0x25);

    public static BitmapSource Render(SvgLogo logo, int sizePx, TrayBadge badge = TrayBadge.None)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var logoDrawing = logo.ToDrawing(sizePx);
            if (badge == TrayBadge.None)
            {
                dc.DrawDrawing(logoDrawing);
            }
            else
            {
                var (box, gap) = BadgeMetrics(sizePx);
                var center = new Point(sizePx - (box / 2.0), sizePx - (box / 2.0));
                var cutout = Geometry.Combine(
                    new RectangleGeometry(new Rect(0, 0, sizePx, sizePx)),
                    new EllipseGeometry(center, (box / 2.0) + gap, (box / 2.0) + gap),
                    GeometryCombineMode.Exclude,
                    null);
                dc.PushClip(cutout);
                dc.DrawDrawing(logoDrawing);
                dc.Pop();
                DrawBadge(dc, badge, sizePx - box, sizePx - box, box);
            }
        }

        var bitmap = new RenderTargetBitmap(sizePx, sizePx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Badge diameter (even, so glyphs sit on whole pixels) and the transparent ring around it. Sized so the badge and its
    /// ring stay below the logo's two speech bubbles (their lowest edge is at 9/16 of the 16 px design and at about
    /// 120/256 of the large one) and only cover part of the drawer.
    /// </summary>
    private static (int Box, double Gap) BadgeMetrics(int sizePx) => sizePx switch
    {
        <= 16 => (6, 1),
        <= 20 => (8, 0.75),
        <= 24 => (10, 1),
        <= 32 => (14, 1.5),
        _ => (22, 2),
    };

    private static void DrawBadge(DrawingContext dc, TrayBadge badge, int left, int top, int box)
    {
        var color = badge switch
        {
            TrayBadge.Syncing => SyncingColor,
            TrayBadge.Paused => PausedColor,
            _ => ErrorColor,
        };
        var fill = new SolidColorBrush(color);
        var white = Brushes.White;
        var center = new Point(left + (box / 2.0), top + (box / 2.0));
        dc.DrawEllipse(fill, null, center, box / 2.0, box / 2.0);

        // Glyph widths are even so they line up with the (even) badge on whole pixels; tiny badges use 1 px strokes.
        var tiny = box <= 8;
        var stem = 2 * Math.Max(1, (int)Math.Round(box * 0.09));
        switch (badge)
        {
            case TrayBadge.Paused:
                {
                    stem = tiny ? 1 : stem;
                    var gap = tiny ? 2 : 2 * Math.Max(1, (int)Math.Round(box * 0.06));
                    var height = tiny ? 4 : 2 * (int)Math.Round(box * 0.24);
                    var x = left + ((box - ((2 * stem) + gap)) / 2);
                    var y = top + ((box - height) / 2);
                    dc.DrawRectangle(white, null, new Rect(x, y, stem, height));
                    dc.DrawRectangle(white, null, new Rect(x + stem + gap, y, stem, height));
                    break;
                }

            case TrayBadge.Error:
                {
                    var height = tiny ? box - 2 : 2 * (int)Math.Round(box * 0.3);
                    var dot = tiny ? 1 : stem;
                    var dotGap = Math.Max(1, (int)Math.Round(box * 0.08));
                    var x = left + ((box - stem) / 2);
                    var y = top + ((box - height) / 2);
                    dc.DrawRectangle(white, null, new Rect(x, y, stem, height - dot - dotGap));
                    dc.DrawRectangle(white, null, new Rect(x, y + height - dot, stem, dot));
                    break;
                }

            case TrayBadge.Syncing:
                {
                    // One circular arrow (about 245°) with an arrowhead, stroked with flat ends.
                    // Tiny badges only get a thin open ring: an arrowhead would fill the whole badge.
                    var radius = tiny ? box * 0.3 : box * 0.25;
                    var thickness = tiny ? 1.0 : Math.Max(1.6, box * 0.14);
                    var startAngle = -60.0;
                    var sweep = tiny ? 270.0 : 245.0;
                    var start = PointOnCircle(center, radius, startAngle);
                    var end = PointOnCircle(center, radius, startAngle + sweep);
                    var arc = new PathGeometry([
                        new PathFigure(start, [new ArcSegment(end, new Size(radius, radius), 0, isLargeArc: true, SweepDirection.Clockwise, isStroked: true)], closed: false),
                    ]);
                    var pen = new Pen(white, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                    dc.DrawGeometry(null, pen, arc);
                    if (tiny)
                    {
                        break;
                    }

                    // Arrowhead at the end, pointing along the direction of travel (clockwise tangent).
                    var tangentAngle = (startAngle + sweep + 90) * Math.PI / 180;
                    var tangent = new Vector(Math.Cos(tangentAngle), Math.Sin(tangentAngle));
                    var normal = new Vector(-tangent.Y, tangent.X);
                    var headLength = thickness * 2.3;
                    var headWidth = thickness * 2.5;
                    var tip = end + (tangent * headLength * 0.75);
                    var back = end - (tangent * headLength * 0.25);
                    var head = new PathGeometry([
                        new PathFigure(tip, [
                            new LineSegment(back + (normal * headWidth / 2), true),
                            new LineSegment(back - (normal * headWidth / 2), true),
                        ], closed: true),
                    ]);
                    dc.DrawGeometry(white, null, head);
                    break;
                }
        }
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }
}
