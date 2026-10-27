using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cabinette.App.Views.Shell;

/// <summary>
/// Round category icon: the PNG when there is one, otherwise a themed circle with a Fluent glyph chosen from
/// <see cref="IconKey"/> (the fallback used while icon generation is unavailable). Template in Styles/Controls.xaml.
/// </summary>
public sealed class CategoryAvatar : Control
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(CategoryAvatar), new PropertyMetadata(null));

    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(
        nameof(IconKey), typeof(string), typeof(CategoryAvatar), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(CategoryAvatar), new PropertyMetadata(48.0));

    static CategoryAvatar()
    {
        FocusableProperty.OverrideMetadata(typeof(CategoryAvatar), new FrameworkPropertyMetadata(false));
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? IconKey
    {
        get => (string?)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}
