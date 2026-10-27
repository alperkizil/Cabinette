using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Cabinette.App.Services;
using Wpf.Ui.Controls;

namespace Cabinette.App.Converters;

/// <summary>true → Visible, false → Collapsed. ConverterParameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Non-null (and non-empty string / collection) → Visible. ConverterParameter "invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Category icon key → Fluent symbol for the fallback avatar.</summary>
public sealed class IconKeyToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Glyphs.ForIconKey(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>For radio buttons bound to an enum: IsChecked = (value == parameter); checking sets the value.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string p)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum)
            {
                return Enum.Parse(enumType, p);
            }

            if (enumType == typeof(bool))
            {
                return bool.Parse(p);
            }
        }

        return Binding.DoNothing;
    }
}

/// <summary>For radio buttons bound to a bool: IsChecked = (value == parameter "True"/"False").</summary>
public sealed class BoolEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && parameter is string p && bool.TryParse(p, out var expected) && b == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p && bool.TryParse(p, out var expected) ? expected : Binding.DoNothing;
}

/// <summary>Multiplies a number (e.g. a size) by the parameter.</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var factor = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1.0;
        return value is double d ? d * factor : 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Adds the parameter to a number (e.g. ActualWidth - 48 for a max width).</summary>
public sealed class AddConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var offset = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 0.0;
        return value is double d ? Math.Max(0, d + offset) : double.PositiveInfinity;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when value.ToString() equals the parameter.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Size → circular clip geometry (for round avatars without an ImageBrush).</summary>
public sealed class CircleClipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var size = value is double d && d > 0 ? d : 0;
        var geometry = new System.Windows.Media.EllipseGeometry(new Rect(0, 0, size, size));
        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Notification kind → symbol.</summary>
public sealed class NotificationSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NotificationKind.Success => SymbolRegular.CheckmarkCircle20,
        NotificationKind.Warning => SymbolRegular.Warning20,
        NotificationKind.Error => SymbolRegular.ErrorCircle20,
        _ => SymbolRegular.Info20,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
