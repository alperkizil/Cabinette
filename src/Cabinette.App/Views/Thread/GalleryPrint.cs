using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cabinette.App.ViewModels.Thread;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// A Gallery photo as a print (SPEC.md section 4, "Gallery"): fitted inside its cell's photo box keeping its shape, never
/// cropped, on the box's bottom line, in a thin frame with a soft shadow. The first child (the thumbnail) fills the photo
/// inside the frame; any further children (play mark, duration, markers) are laid over the whole print.
/// Cheap by design for virtualized lists: the frame and shadow are a few rectangles drawn in <see cref="OnRender"/> (no
/// Effect), and the shape only moves the children inside this element, never the list's layout. Until the shape is known
/// nothing is drawn.
/// </summary>
public sealed class GalleryPrint : Panel
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(GalleryPrint),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FallbackAspectRatioProperty = DependencyProperty.Register(
        nameof(FallbackAspectRatio), typeof(double), typeof(GalleryPrint),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(
        nameof(FrameBrush), typeof(Brush), typeof(GalleryPrint), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShadowBrushProperty = DependencyProperty.Register(
        nameof(ShadowBrush), typeof(Brush), typeof(GalleryPrint), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Rect _print = Rect.Empty;

    /// <summary>Width / height of the photo; 0 while unknown.</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>The shape used while <see cref="AspectRatio"/> is unknown (videos: 16:9); 0 draws nothing until it is known.</summary>
    public double FallbackAspectRatio
    {
        get => (double)GetValue(FallbackAspectRatioProperty);
        set => SetValue(FallbackAspectRatioProperty, value);
    }

    /// <summary>The frame: the file's mode (Cab.GalleryFrameCopy, green, or Cab.GalleryFramePointer, blue).</summary>
    public Brush? FrameBrush
    {
        get => (Brush?)GetValue(FrameBrushProperty);
        set => SetValue(FrameBrushProperty, value);
    }

    /// <summary>The shadow's color, drawn at low opacity in a few offset layers (a dark, translucent theme brush).</summary>
    public Brush? ShadowBrush
    {
        get => (Brush?)GetValue(ShadowBrushProperty);
        set => SetValue(ShadowBrushProperty, value);
    }

    /// <summary>Where the print (frame included) was last laid out, relative to this element; empty while the shape is unknown.</summary>
    public Rect PrintBounds => _print;

    protected override Size MeasureOverride(Size availableSize)
    {
        var box = new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(box);
        }

        return box;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var aspect = AspectRatio > 0 ? AspectRatio : FallbackAspectRatio;
        var frame = GalleryMetrics.FrameThickness(Math.Min(finalSize.Width, finalSize.Height));
        _print = GalleryMetrics.PrintRect(finalSize, aspect, frame);
        var photo = _print.IsEmpty ? Rect.Empty : new Rect(_print.X + frame, _print.Y + frame, Math.Max(0, _print.Width - (2 * frame)), Math.Max(0, _print.Height - (2 * frame)));
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var target = i == 0 ? photo : _print;
            child.Arrange(target.IsEmpty ? new Rect(0, 0, 0, 0) : target);
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_print.IsEmpty)
        {
            return;
        }

        // A soft shadow from three offset, translucent layers below and right of the print, and a faint outline that keeps
        // a white frame apart from a cream background.
        if (ShadowBrush is { } shadow)
        {
            Layer(dc, shadow, 0.10, new Rect(_print.X + 1, _print.Y + 2, _print.Width + 2, _print.Height + 1.5), 3);
            Layer(dc, shadow, 0.14, new Rect(_print.X + 0.5, _print.Y + 1, _print.Width + 1, _print.Height + 1), 2);
            Layer(dc, shadow, 0.22, new Rect(_print.X - 0.5, _print.Y - 0.5, _print.Width + 1, _print.Height + 1), 1);
        }

        if (FrameBrush is { } frame)
        {
            dc.DrawRectangle(frame, null, _print);
        }
    }

    private static void Layer(DrawingContext dc, Brush brush, double opacity, Rect rect, double radius)
    {
        dc.PushOpacity(opacity);
        dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        dc.Pop();
    }
}
