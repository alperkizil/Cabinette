using System.Windows;
using System.Windows.Media;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// The push pin on a chat bubble (SPEC.md section 4): a glossy head, a short neck and a needle with a soft shadow, in one of
/// eight colors picked from the file's id, so a file keeps its color while you scroll and across restarts. It draws itself
/// with shared frozen brushes (one element per bubble, cheap to recycle) and takes no clicks.
/// </summary>
public sealed class Tack : FrameworkElement
{
    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(long), typeof(Tack), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What picks the color: the file's id.</summary>
    public long Seed
    {
        get => (long)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>Red, blue, yellow, green, pink, orange, white and black, like a box of push pins.</summary>
    internal static readonly Color[] Colors =
    [
        Color.FromRgb(0xE5, 0x39, 0x35),
        Color.FromRgb(0x1E, 0x88, 0xE5),
        Color.FromRgb(0xFD, 0xD8, 0x35),
        Color.FromRgb(0x43, 0xA0, 0x47),
        Color.FromRgb(0xE9, 0x1E, 0x8C),
        Color.FromRgb(0xFB, 0x8C, 0x00),
        Color.FromRgb(0xF4, 0xF4, 0xF4),
        Color.FromRgb(0x2B, 0x2B, 0x2B),
    ];

    private static readonly (Brush Head, Brush Neck)[] Paints = Colors.Select(MakePaint).ToArray();
    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
    private static readonly Brush ShineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen NeedlePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6))), 1.3)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    });

    private static readonly Geometry Neck = Freeze(Geometry.Parse("M5.6,7.5 L10.4,7.5 L9.5,12 L6.5,12 Z"));

    public Tack()
    {
        Width = 16;
        Height = 20;
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>
    /// The color for a seed: Fibonacci hashing spreads neighboring ids over the eight colors, so any five files added one after
    /// another have five different colors.
    /// </summary>
    internal static int ColorIndex(long seed) => (int)(unchecked((ulong)seed * 0x9E3779B97F4A7C15UL) >> 61);

    protected override void OnRender(DrawingContext dc)
    {
        var (head, neck) = Paints[ColorIndex(Seed)];
        dc.DrawEllipse(ShadowBrush, null, new Point(7, 18.4), 4.5, 1.5);
        dc.DrawLine(NeedlePen, new Point(8.4, 12), new Point(6, 18.6));
        dc.DrawGeometry(neck, null, Neck);
        dc.DrawEllipse(neck, null, new Point(8, 12), 2.6, 1.1);
        dc.DrawEllipse(head, null, new Point(8, 5.2), 6.6, 5);
        dc.DrawEllipse(ShineBrush, null, new Point(5.6, 3.4), 1.8, 1.1);
    }

    private static (Brush Head, Brush Neck) MakePaint(Color color)
    {
        var head = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.65,
            RadiusY = 0.65,
        };
        head.GradientStops.Add(new GradientStop(Mix(color, Colors[6], 0.55), 0));
        head.GradientStops.Add(new GradientStop(color, 0.55));
        head.GradientStops.Add(new GradientStop(Mix(color, System.Windows.Media.Colors.Black, 0.35), 1));
        return (Freeze(head), Freeze(new SolidColorBrush(Mix(color, System.Windows.Media.Colors.Black, 0.2))));
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
