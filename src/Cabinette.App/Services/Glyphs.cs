using System.Globalization;
using Cabinette.App.Localization;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Wpf.Ui.Controls;

namespace Cabinette.App.Services;

/// <summary>Fluent icons for category icon keys and file types, plus chat-style time labels.</summary>
public static class Glyphs
{
    /// <summary>The glyph drawn in the XAML fallback avatar (when a category has no PNG icon).</summary>
    public static SymbolRegular ForIconKey(string? iconKey) => iconKey switch
    {
        "audio" => SymbolRegular.MusicNote224,
        "video" => SymbolRegular.VideoClip24,
        "images" => SymbolRegular.Image24,
        "text" => SymbolRegular.DocumentText24,
        "documents" or "documents.pdf" or "documents.word" => SymbolRegular.Document24,
        "spreadsheets" => SymbolRegular.Table24,
        "presentations" => SymbolRegular.SlideText24,
        "code" or "code.python" or "code.java" or "code.csharp" or "code.cpp" or "code.jsts" or "code.web" or "code.shell" => SymbolRegular.Code24,
        "executable" or "executable.msi" or "executable.exe" => SymbolRegular.Apps24,
        "archives" => SymbolRegular.FolderZip24,
        "ebooks" or "ebooks.epub" or "ebooks.pdf" => SymbolRegular.Book24,
        "fonts" => SymbolRegular.TextFont24,
        "design" => SymbolRegular.DesignIdeas24,
        "models3d" => SymbolRegular.Cube24,
        "diskimages" => SymbolRegular.Storage24,
        "notes" => SymbolRegular.Notepad24,
        "links" => SymbolRegular.Link24,
        "other" => SymbolRegular.Box24,
        "deleted" => SymbolRegular.Delete24,
        "toplevel" => SymbolRegular.Home24,
        "allin" => SymbolRegular.Stack24,
        "starred" => SymbolRegular.Star24,
        _ => SymbolRegular.Folder24,
    };

    /// <summary>Small glyph for a file in previews and search results.</summary>
    public static SymbolRegular ForItem(ItemKind? kind, string? fileNameOrExtension)
    {
        switch (kind)
        {
            case ItemKind.Note:
                return SymbolRegular.Notepad16;
            case ItemKind.PastedImage:
                return SymbolRegular.Image16;
            case ItemKind.Link:
                return SymbolRegular.Link16;
        }

        var extension = fileNameOrExtension is null ? null : System.IO.Path.GetExtension(fileNameOrExtension);
        if (string.IsNullOrEmpty(extension))
        {
            extension = fileNameOrExtension;
        }

        return ForTypeKey(ExtensionMap.GetTypeKey(extension));
    }

    public static SymbolRegular ForTypeKey(string? typeKey) => typeKey switch
    {
        "audio" => SymbolRegular.MusicNote216,
        "video" => SymbolRegular.Video16,
        "images" => SymbolRegular.Image16,
        "text" => SymbolRegular.DocumentText16,
        "documents" => SymbolRegular.Document16,
        "spreadsheets" => SymbolRegular.Table16,
        "presentations" => SymbolRegular.SlideText16,
        "code" => SymbolRegular.Code16,
        "executable" => SymbolRegular.Apps16,
        "archives" => SymbolRegular.FolderZip16,
        "ebooks" => SymbolRegular.Book16,
        "fonts" => SymbolRegular.TextFont16,
        "design" => SymbolRegular.DesignIdeas16,
        "models3d" => SymbolRegular.Cube16,
        "diskimages" => SymbolRegular.HardDrive16,
        "notes" => SymbolRegular.Notepad16,
        "links" => SymbolRegular.Link16,
        _ => SymbolRegular.Document16,
    };

    /// <summary>"14:05" today, "Yesterday", the weekday within a week, otherwise the short date.</summary>
    public static string ChatTime(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null)
        {
            return "";
        }

        var culture = CultureInfo.CurrentCulture;
        var local = value.Value.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = local.Date;
        if (day == today)
        {
            return local.ToString("HH:mm", culture);
        }

        if (day == today.AddDays(-1))
        {
            return L.Get("Time_Yesterday");
        }

        if (day > today.AddDays(-7) && day < today)
        {
            return culture.DateTimeFormat.GetDayName(local.DayOfWeek);
        }

        return local.ToString("d", culture);
    }

    public static string FileSize(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{value} {units[0]}")
            : string.Create(CultureInfo.CurrentCulture, $"{size:0.#} {units[unit]}");
    }
}
