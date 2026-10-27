using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Cabinette.App.ViewModels.Thread;
using Wpf.Ui.Controls;

namespace Cabinette.App.Views.Thread;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>null or empty string → Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Enum value equals the converter parameter (two-way for radio buttons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>Fluent icon for an item's type (fallback glyph when the shell has no icon or thumbnail).</summary>
public sealed class TypeSymbolConverter : IValueConverter
{
    public static SymbolRegular ForItem(ThreadItemViewModel item) => item.VisualKind switch
    {
        ItemVisualKind.Note => SymbolRegular.Notepad24,
        ItemVisualKind.Link => SymbolRegular.Link24,
        ItemVisualKind.Image => SymbolRegular.Image24,
        ItemVisualKind.Video => SymbolRegular.Video24,
        ItemVisualKind.Audio => SymbolRegular.MusicNote224,
        _ => item.Extension == "pdf" ? SymbolRegular.DocumentPdf24 : ForTypeKey(item.TypeKey),
    };

    public static SymbolRegular ForTypeKey(string typeKey) => typeKey switch
    {
        "images" => SymbolRegular.Image24,
        "video" => SymbolRegular.Video24,
        "audio" => SymbolRegular.MusicNote224,
        "text" => SymbolRegular.DocumentText24,
        "spreadsheets" => SymbolRegular.Table24,
        "presentations" => SymbolRegular.SlideText24,
        "code" => SymbolRegular.Code24,
        "executable" => SymbolRegular.Apps24,
        "archives" => SymbolRegular.FolderZip24,
        "ebooks" => SymbolRegular.Notebook24,
        "fonts" => SymbolRegular.TextFont24,
        "design" => SymbolRegular.DesignIdeas24,
        "models3d" => SymbolRegular.Cube24,
        "diskimages" => SymbolRegular.Storage24,
        "links" => SymbolRegular.Link24,
        _ => SymbolRegular.Document24,
    };

    /// <summary>Parameter "label": the glyph sits above an extension label, so avoid symbols that contain text (PDF).</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ThreadItemViewModel { Extension: "pdf" } when parameter is "label" => SymbolRegular.Document24,
        ThreadItemViewModel item => ForItem(item),
        string key => ForTypeKey(key),
        _ => SymbolRegular.Document24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
