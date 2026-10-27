using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cabinette.App.ViewModels.Thread;

namespace Cabinette.App.Views.Thread;

/// <summary>The part a child plays in a <see cref="ThreadBannerPanel"/>.</summary>
public enum ThreadBannerSlot
{
    /// <summary>The first line (the category's name). It may be trimmed, down to <see cref="ThreadBannerPanel.MinTitleWidth"/>.</summary>
    Title,

    /// <summary>The second line ("12 files · 11 MB"). The panel keeps room for all of it.</summary>
    Subtitle,

    /// <summary>The buttons (search, filter, view toggle, select), on the right.</summary>
    Tools,

    /// <summary>The large, faint picture of the category, drawn behind the rest in the free space between the text and the tools.</summary>
    Watermark,
}

/// <summary>
/// Lays out the thread banner (SPEC.md section 4): the two lines of text on the left, the tools on the right, and the watermark in
/// the free space between them, right next to the tools. When the pane is too narrow for the text and the tools side by side
/// (the minimum window width with large text sizes, or with the search box open), the tools move to a second row under the text
/// instead of covering it, and the banner grows. The watermark shrinks with the free space and is left out when too little is
/// left, so it never sits under text or buttons.
/// </summary>
public sealed class ThreadBannerPanel : Panel
{
    public static readonly DependencyProperty SlotProperty = DependencyProperty.RegisterAttached(
        "Slot", typeof(ThreadBannerSlot), typeof(ThreadBannerPanel),
        new FrameworkPropertyMetadata(ThreadBannerSlot.Title, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty MinTitleWidthProperty = DependencyProperty.Register(
        nameof(MinTitleWidth), typeof(double), typeof(ThreadBannerPanel), new FrameworkPropertyMetadata(110.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ThreadBannerPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ThreadBannerPanel), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty WatermarkSizeProperty = DependencyProperty.Register(
        nameof(WatermarkSize), typeof(double), typeof(ThreadBannerPanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty MinWatermarkSizeProperty = DependencyProperty.Register(
        nameof(MinWatermarkSize), typeof(double), typeof(ThreadBannerPanel), new FrameworkPropertyMetadata(56.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty WatermarkDropProperty = DependencyProperty.Register(
        nameof(WatermarkDrop), typeof(double), typeof(ThreadBannerPanel), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    private const double Epsilon = 0.5;

    private double _toolsWidth;
    private double _textNatural;

    public static ThreadBannerSlot GetSlot(DependencyObject element) => (ThreadBannerSlot)element.GetValue(SlotProperty);

    public static void SetSlot(DependencyObject element, ThreadBannerSlot value) => element.SetValue(SlotProperty, value);

    /// <summary>The first line is trimmed down to this width before the tools move to a second row.</summary>
    public double MinTitleWidth
    {
        get => (double)GetValue(MinTitleWidthProperty);
        set => SetValue(MinTitleWidthProperty, value);
    }

    /// <summary>Space between the text and the tools (and around the watermark).</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Space above the tools when they are on a second row.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Side of the square the watermark is drawn in (it is scaled into it).</summary>
    public double WatermarkSize
    {
        get => (double)GetValue(WatermarkSizeProperty);
        set => SetValue(WatermarkSizeProperty, value);
    }

    /// <summary>Below this size there is no room for the watermark, and it is left out.</summary>
    public double MinWatermarkSize
    {
        get => (double)GetValue(MinWatermarkSizeProperty);
        set => SetValue(MinWatermarkSizeProperty, value);
    }

    /// <summary>How far the watermark sits below the middle of the band (it bleeds off the band's bottom edge).</summary>
    public double WatermarkDrop
    {
        get => (double)GetValue(WatermarkDropProperty);
        set => SetValue(WatermarkDropProperty, value);
    }

    /// <summary>True while the tools are on a second row, under the text.</summary>
    public bool IsWrapped { get; private set; }

    /// <summary>Where the watermark was last drawn; empty when it was left out for lack of room.</summary>
    public Rect WatermarkBounds { get; private set; } = Rect.Empty;

    protected override Size MeasureOverride(Size availableSize)
    {
        var title = Find(ThreadBannerSlot.Title);
        var subtitle = Find(ThreadBannerSlot.Subtitle);
        var tools = Find(ThreadBannerSlot.Tools);
        var watermark = Find(ThreadBannerSlot.Watermark);
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);

        title?.Measure(infinite);
        subtitle?.Measure(infinite);
        tools?.Measure(infinite);
        watermark?.Measure(new Size(WatermarkSize, WatermarkSize));

        var titleWidth = title?.DesiredSize.Width ?? 0;
        var subtitleWidth = subtitle?.DesiredSize.Width ?? 0;
        _textNatural = Math.Max(titleWidth, subtitleWidth);
        var textNeed = Math.Max(subtitleWidth, Math.Min(titleWidth, MinTitleWidth));
        var toolsNatural = tools?.DesiredSize.Width ?? 0;
        var toolsMinimum = tools is BannerToolsPanel flexible ? flexible.MinimumWidth : toolsNatural;
        var spacing = tools is null ? 0 : ColumnSpacing;
        var width = double.IsInfinity(availableSize.Width) ? _textNatural + spacing + toolsNatural : availableSize.Width;

        // One row when the tools fit (the search box may give up some width) beside the second line and a little of the first.
        var toolsBudget = width - textNeed - spacing;
        IsWrapped = tools is not null && toolsBudget + Epsilon < toolsMinimum;
        var toolsConstraint = IsWrapped ? width : Math.Clamp(toolsBudget, toolsMinimum, toolsNatural);
        tools?.Measure(new Size(toolsConstraint, double.PositiveInfinity));
        _toolsWidth = Math.Min(tools?.DesiredSize.Width ?? 0, toolsConstraint);
        var textWidth = IsWrapped ? width : Math.Max(0, width - _toolsWidth - spacing);

        title?.Measure(new Size(textWidth, double.PositiveInfinity));
        subtitle?.Measure(new Size(textWidth, double.PositiveInfinity));

        var textHeight = (title?.DesiredSize.Height ?? 0) + (subtitle?.DesiredSize.Height ?? 0);
        var toolsHeight = tools?.DesiredSize.Height ?? 0;
        var height = IsWrapped ? textHeight + RowSpacing + toolsHeight : Math.Max(textHeight, toolsHeight);
        var desiredWidth = IsWrapped
            ? Math.Max(Math.Min(_textNatural, width), _toolsWidth)
            : Math.Min(_textNatural, textWidth) + spacing + _toolsWidth;
        return new Size(Math.Min(desiredWidth, width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var title = Find(ThreadBannerSlot.Title);
        var subtitle = Find(ThreadBannerSlot.Subtitle);
        var tools = Find(ThreadBannerSlot.Tools);
        var watermark = Find(ThreadBannerSlot.Watermark);
        var width = finalSize.Width;
        var spacing = tools is null ? 0 : ColumnSpacing;
        var titleHeight = title?.DesiredSize.Height ?? 0;
        var subtitleHeight = subtitle?.DesiredSize.Height ?? 0;
        var textHeight = titleHeight + subtitleHeight;
        var toolsHeight = tools?.DesiredSize.Height ?? 0;
        var toolsWidth = Math.Min(_toolsWidth, width);
        var freeRight = width - toolsWidth - spacing;

        // The watermark goes into the free space next to the tools: beside the text in one row; left of the tools (and below
        // the text) on two rows. It bleeds off the band's bottom edge, which clips it.
        double textTop, textWidth, freeLeft, watermarkTop, size;
        if (IsWrapped)
        {
            textTop = 0;
            textWidth = width;
            var toolsTop = textHeight + RowSpacing;
            tools?.Arrange(new Rect(width - toolsWidth, toolsTop, toolsWidth, toolsHeight));
            freeLeft = 0;
            size = Math.Min(WatermarkSize, freeRight - freeLeft);
            watermarkTop = toolsTop;
        }
        else
        {
            textTop = Math.Max(0, (finalSize.Height - textHeight) / 2);
            textWidth = Math.Max(0, width - toolsWidth - spacing);
            tools?.Arrange(new Rect(width - toolsWidth, Math.Max(0, (finalSize.Height - toolsHeight) / 2), toolsWidth, toolsHeight));
            freeLeft = Math.Min(_textNatural, textWidth) + spacing;
            size = Math.Min(WatermarkSize, freeRight - freeLeft);
            watermarkTop = ((finalSize.Height - size) / 2) + WatermarkDrop;
        }

        title?.Arrange(new Rect(0, textTop, textWidth, titleHeight));
        subtitle?.Arrange(new Rect(0, textTop + titleHeight, textWidth, subtitleHeight));

        if (watermark is not null)
        {
            if (size >= MinWatermarkSize)
            {
                WatermarkBounds = new Rect(freeRight - size, watermarkTop, size, size);
                watermark.Arrange(WatermarkBounds);
            }
            else
            {
                // No room: arranged empty, it draws nothing (layout clip).
                WatermarkBounds = Rect.Empty;
                watermark.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }

    private UIElement? Find(ThreadBannerSlot slot)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child is not null && GetSlot(child) == slot)
            {
                return child;
            }
        }

        return null;
    }
}

/// <summary>
/// The banner's buttons in a row, like a horizontal StackPanel, except that the first child (the search box) is flexible: it
/// takes its MaxWidth when there is room and gives up width down to its MinWidth when there isn't (see <see cref="MinimumWidth"/>).
/// When even that doesn't fit (the search box open at the minimum window width with extra-large text), the search box gets a row
/// of its own above the buttons. Rows are right-aligned.
/// </summary>
public sealed class BannerToolsPanel : Panel
{
    private const double RowSpacing = 4;

    private double _flexSlot;
    private double _othersWidth;
    private double _firstRowHeight;

    /// <summary>The narrowest this can be on one row: every button, and the search box (when shown) at its MinWidth.</summary>
    public double MinimumWidth { get; private set; }

    /// <summary>True while the search box is on a row of its own, above the buttons.</summary>
    public bool IsStacked { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        _othersWidth = 0;
        var othersHeight = 0.0;
        for (var i = 1; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _othersWidth += children[i].DesiredSize.Width;
            othersHeight = Math.Max(othersHeight, children[i].DesiredSize.Height);
        }

        _flexSlot = 0;
        IsStacked = false;
        MinimumWidth = _othersWidth;
        if (children.Count == 0 || children[0] is not { } first)
        {
            return new Size(_othersWidth, othersHeight);
        }

        if (first.Visibility == Visibility.Collapsed || first is not FrameworkElement flexible || double.IsInfinity(flexible.MaxWidth))
        {
            first.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _flexSlot = first.DesiredSize.Width;
            MinimumWidth += _flexSlot;
        }
        else
        {
            var margin = flexible.Margin.Left + flexible.Margin.Right;
            var maxSlot = flexible.MaxWidth + margin;
            var minSlot = Math.Min(flexible.MinWidth + margin, maxSlot);
            MinimumWidth += minSlot;
            if (double.IsInfinity(availableSize.Width))
            {
                _flexSlot = maxSlot;
            }
            else if (availableSize.Width >= MinimumWidth)
            {
                _flexSlot = Math.Clamp(availableSize.Width - _othersWidth, minSlot, maxSlot);
            }
            else
            {
                IsStacked = true;
                _flexSlot = Math.Clamp(availableSize.Width, minSlot, maxSlot);
            }

            first.Measure(new Size(_flexSlot, availableSize.Height));
        }

        if (IsStacked)
        {
            _firstRowHeight = first.DesiredSize.Height;
            return new Size(Math.Max(_flexSlot, _othersWidth), _firstRowHeight + RowSpacing + othersHeight);
        }

        _firstRowHeight = 0;
        return new Size(_flexSlot + _othersWidth, Math.Max(othersHeight, first.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (IsStacked && children.Count > 0)
        {
            children[0].Arrange(new Rect(Math.Max(0, finalSize.Width - _flexSlot), 0, _flexSlot, _firstRowHeight));
            var top = _firstRowHeight + RowSpacing;
            var rowHeight = Math.Max(0, finalSize.Height - top);
            var x = Math.Max(0, finalSize.Width - _othersWidth);
            for (var i = 1; i < children.Count; i++)
            {
                children[i].Arrange(new Rect(x, top, children[i].DesiredSize.Width, rowHeight));
                x += children[i].DesiredSize.Width;
            }

            return finalSize;
        }

        var left = Math.Max(0, finalSize.Width - _flexSlot - _othersWidth);
        for (var i = 0; i < children.Count; i++)
        {
            var width = i == 0 ? _flexSlot : children[i].DesiredSize.Width;
            children[i].Arrange(new Rect(left, 0, width, finalSize.Height));
            left += width;
        }

        return finalSize;
    }
}

/// <summary>The band color behind the banner's text, before <see cref="ReadableBandBrushConverter"/> makes it readable.</summary>
public static class ThreadBannerBand
{
    public static readonly DependencyProperty BaseBackgroundProperty = DependencyProperty.RegisterAttached(
        "BaseBackground", typeof(Brush), typeof(ThreadBannerBand), new FrameworkPropertyMetadata(null));

    public static Brush? GetBaseBackground(DependencyObject element) => (Brush?)element.GetValue(BaseBackgroundProperty);

    public static void SetBaseBackground(DependencyObject element, Brush? value) => element.SetValue(BaseBackgroundProperty, value);
}

/// <summary>
/// Band brush (values[0]) and text brush (values[1]) → the band brush darkened or lightened just enough for the text to reach
/// <see cref="ThreadBanner.MinTextContrast"/> (see <see cref="ThreadBanner.ReadableBackground"/>). Brushes that aren't solid
/// colors pass through unchanged.
/// </summary>
public sealed class ReadableBandBrushConverter : IMultiValueConverter
{
    public static ReadableBandBrushConverter Instance { get; } = new();

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not SolidColorBrush band)
        {
            return values.Length > 0 && values[0] is Brush other ? other : null;
        }

        if (values[1] is not SolidColorBrush text)
        {
            return band;
        }

        var readable = ThreadBanner.ReadableBackground(band.Color, text.Color);
        if (readable == band.Color)
        {
            return band;
        }

        var brush = new SolidColorBrush(readable);
        brush.Freeze();
        return brush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
