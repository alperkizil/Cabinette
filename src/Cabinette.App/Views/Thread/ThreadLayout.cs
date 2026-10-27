using System.Windows;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// Inherited layout values computed from the pane width in code-behind and read by the row templates
/// (cheaper than a RelativeSource binding per realized row).
/// </summary>
public static class ThreadLayout
{
    public static readonly DependencyProperty BubbleMaxWidthProperty = DependencyProperty.RegisterAttached(
        "BubbleMaxWidth", typeof(double), typeof(ThreadLayout), new FrameworkPropertyMetadata(420d, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty RowPaddingProperty = DependencyProperty.RegisterAttached(
        "RowPadding", typeof(Thickness), typeof(ThreadLayout), new FrameworkPropertyMetadata(new Thickness(24, 0, 24, 0), FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.RegisterAttached(
        "TileSize", typeof(double), typeof(ThreadLayout), new FrameworkPropertyMetadata(160d, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetBubbleMaxWidth(DependencyObject d) => (double)d.GetValue(BubbleMaxWidthProperty);

    public static void SetBubbleMaxWidth(DependencyObject d, double value) => d.SetValue(BubbleMaxWidthProperty, value);

    public static Thickness GetRowPadding(DependencyObject d) => (Thickness)d.GetValue(RowPaddingProperty);

    public static void SetRowPadding(DependencyObject d, Thickness value) => d.SetValue(RowPaddingProperty, value);

    public static double GetTileSize(DependencyObject d) => (double)d.GetValue(TileSizeProperty);

    public static void SetTileSize(DependencyObject d, double value) => d.SetValue(TileSizeProperty, value);

    /// <summary>The Gallery's photo box (96, 128 or 176): fixed per size step, so thumbnails keep their size while the pane resizes.</summary>
    public static readonly DependencyProperty GalleryCellSizeProperty = DependencyProperty.RegisterAttached(
        "GalleryCellSize", typeof(double), typeof(ThreadLayout), new FrameworkPropertyMetadata(128d, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>The width each Gallery cell gets: the row shared out evenly, the cell centered in it.</summary>
    public static readonly DependencyProperty GallerySlotWidthProperty = DependencyProperty.RegisterAttached(
        "GallerySlotWidth", typeof(double), typeof(ThreadLayout), new FrameworkPropertyMetadata(148d, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Lines the Gallery's day headers up with the cells' edges (half the space around a cell).</summary>
    public static readonly DependencyProperty GalleryHeaderIndentProperty = DependencyProperty.RegisterAttached(
        "GalleryHeaderIndent", typeof(Thickness), typeof(ThreadLayout), new FrameworkPropertyMetadata(new Thickness(10, 0, 10, 0), FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>The type icon of a Gallery cell that holds no photo or video.</summary>
    public static readonly DependencyProperty GalleryIconSizeProperty = DependencyProperty.RegisterAttached(
        "GalleryIconSize", typeof(double), typeof(ThreadLayout), new FrameworkPropertyMetadata(64d, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetGalleryCellSize(DependencyObject d) => (double)d.GetValue(GalleryCellSizeProperty);

    public static void SetGalleryCellSize(DependencyObject d, double value) => d.SetValue(GalleryCellSizeProperty, value);

    public static double GetGallerySlotWidth(DependencyObject d) => (double)d.GetValue(GallerySlotWidthProperty);

    public static void SetGallerySlotWidth(DependencyObject d, double value) => d.SetValue(GallerySlotWidthProperty, value);

    public static Thickness GetGalleryHeaderIndent(DependencyObject d) => (Thickness)d.GetValue(GalleryHeaderIndentProperty);

    public static void SetGalleryHeaderIndent(DependencyObject d, Thickness value) => d.SetValue(GalleryHeaderIndentProperty, value);

    public static double GetGalleryIconSize(DependencyObject d) => (double)d.GetValue(GalleryIconSizeProperty);

    public static void SetGalleryIconSize(DependencyObject d, double value) => d.SetValue(GalleryIconSizeProperty, value);
}
