using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>Which color the thread banner takes (SPEC.md section 4, "Header: a banner").</summary>
public enum ThreadBannerKind
{
    /// <summary>The theme's banner color (Cab.BannerFallbackBackground): the category's icon is the user's own picture.</summary>
    Fallback = 0,

    /// <summary>The category's avatar color (<see cref="ThreadBanner.Color"/>), with white text (Cab.BannerText).</summary>
    Avatar = 1,

    /// <summary>The Starred view: the accent with the text-on-accent color, the look of its avatar in the chat list.</summary>
    Starred = 2,
}

/// <summary>
/// A banner's color and watermark, worked out off the UI thread (<see cref="ThreadBanner.ForCategory"/> reads the icon PNG).
/// </summary>
/// <param name="Color">The band color for <see cref="ThreadBannerKind.Avatar"/>; null otherwise.</param>
/// <param name="Watermark">The large, faint Fluent glyph on the band.</param>
/// <param name="WatermarkPicture">A user category's own picture, shown faded instead of the glyph; frozen.</param>
public sealed record ThreadBannerLook(ThreadBannerKind Kind, Color? Color, SymbolRegular Watermark, ImageSource? WatermarkPicture = null);

/// <summary>
/// The band at the top of the thread pane (SPEC.md section 4, "Header: a banner"), in place of the old header line: the
/// category's avatar color, the category name with the file count and total size under it ("12 files · 11 MB", in the old
/// header's text styles), and a large faint picture of what the category holds. Owned by <see cref="ThreadViewModel"/>, which
/// fills it with the header and keeps the count and size current as files come and go.
/// </summary>
public sealed partial class ThreadBanner : ObservableObject
{
    /// <summary>A pale picture is never mistaken for a generated avatar: the palette guarantees at least 3:1 against white.</summary>
    private const double MinAvatarContrastWithWhite = 3.0;

    public ThreadBanner()
    {
        Watermark = SymbolRegular.Folder48;
        CountText = "";
        Title = "";
        SizeText = "";
        ToolTipText = "";
    }

    [ObservableProperty]
    public partial ThreadBannerKind Kind { get; set; }

    /// <summary>The band color when <see cref="Kind"/> is <see cref="ThreadBannerKind.Avatar"/>: the category's avatar color (data).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    public partial Color? Color { get; set; }

    /// <summary><see cref="Color"/> as a frozen brush for the view; null without a color.</summary>
    public Brush? Brush => Color is { } color ? Frozen(new SolidColorBrush(color)) : null;

    [ObservableProperty]
    public partial SymbolRegular Watermark { get; set; }

    /// <summary>A user category's own picture (shown faded instead of <see cref="Watermark"/>), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWatermarkPicture))]
    public partial ImageSource? WatermarkPicture { get; set; }

    public bool HasWatermarkPicture => WatermarkPicture is not null;

    /// <summary>The first line: the category's name ("Starred" in the Starred view).</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>"14 files", "1 file", or "3 of 14 files" while a search or filter is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string CountText { get; set; }

    /// <summary>"4.2 MB": the total size of the category's files, like the sizes in the thread. Empty when nothing is known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string SizeText { get; set; }

    /// <summary>The second line: "12 files · 11 MB" (the count, then the total size when there is one).</summary>
    public string Subtitle => SizeText.Length > 0 ? L.Format("Thread_BannerCountAndSize", CountText, SizeText) : CountText;

    /// <summary>The name with its parent categories ("Work › Invoices"), for the tooltip of the text.</summary>
    [ObservableProperty]
    public partial string ToolTipText { get; set; }

    public void Apply(ThreadBannerLook look)
    {
        Kind = look.Kind;
        Color = look.Kind == ThreadBannerKind.Avatar ? look.Color : null;
        Watermark = look.Watermark;
        WatermarkPicture = look.WatermarkPicture;
    }

    /// <summary>The Starred view's banner: the accent, like its avatar, with a star.</summary>
    public static ThreadBannerLook StarredLook { get; } = new(ThreadBannerKind.Starred, null, SymbolRegular.Star48);

    /// <summary>
    /// The banner of a category. Reads and decodes its icon PNG, so call it off the UI thread.
    /// <list type="bullet">
    /// <item>A generated avatar (see <see cref="ReadAvatarColor"/>): its circle's color, the very color the chat list shows.</item>
    /// <item>No icon yet (it is generated in the background): the color the generator will use for it.</item>
    /// <item>The user's own picture: the theme's banner color. A category the user made shows the picture faded as its
    /// watermark; a built-in one keeps the picture of what it holds.</item>
    /// </list>
    /// </summary>
    public static ThreadBannerLook ForCategory(Category category, ICategoryIconGenerator? generator, int pictureSizePx = 192)
    {
        var watermark = WatermarkFor(category.IconKey);
        if (category.IconPng is not { Length: > 0 } png)
        {
            return TryGetGeneratedColor(generator, category) is { } next
                ? new ThreadBannerLook(ThreadBannerKind.Avatar, next, watermark)
                : new ThreadBannerLook(ThreadBannerKind.Fallback, null, watermark);
        }

        if (ReadAvatarColor(png) is { } color)
        {
            return new ThreadBannerLook(ThreadBannerKind.Avatar, color, watermark);
        }

        var picture = category.Kind == CategoryKind.User ? ThreadViewModel.DecodePng(png, pictureSizePx) : null;
        return new ThreadBannerLook(ThreadBannerKind.Fallback, null, watermark, picture);
    }

    /// <summary>
    /// The watermark glyph for a category's icon key <b>[my default]</b>: a film strip for Video, photos for Images, notes for
    /// Audio, a fitting Fluent icon for every other built-in category, and the folder of the generated avatar for the user's own
    /// categories ("folder") and unknown keys. 48 px designs where they exist below U+10000: ui:SymbolIcon draws a symbol above that
    /// as some other character.
    /// </summary>
    public static SymbolRegular WatermarkFor(string? iconKey) => iconKey switch
    {
        "video" => SymbolRegular.Filmstrip24,
        "images" => SymbolRegular.ImageMultiple48,
        "audio" => SymbolRegular.MusicNote224,
        "text" => SymbolRegular.DocumentText24,
        "documents" => SymbolRegular.DocumentMultiple24,
        "documents.pdf" or "ebooks.pdf" => SymbolRegular.DocumentPdf24,
        "documents.word" => SymbolRegular.DocumentEdit24,
        "spreadsheets" => SymbolRegular.Table48,
        "presentations" => SymbolRegular.SlideText48,
        "code" => SymbolRegular.CodeBlock48,
        "code.python" or "code.csharp" or "code.cpp" or "code.jsts" => SymbolRegular.Braces48,
        "code.java" => SymbolRegular.DrinkCoffee24,
        "code.web" => SymbolRegular.Globe24,
        "code.shell" => SymbolRegular.WindowConsole20,
        "executable" => SymbolRegular.Apps48,
        "executable.msi" => SymbolRegular.Box24,
        "executable.exe" => SymbolRegular.WindowApps24,
        "archives" => SymbolRegular.Archive48,
        "ebooks" => SymbolRegular.BookOpen48,
        "ebooks.epub" => SymbolRegular.Book24,
        "fonts" => SymbolRegular.TextFont24,
        "design" => SymbolRegular.Color24,
        "models3d" => SymbolRegular.Cube24,
        "diskimages" => SymbolRegular.Storage24,
        "notes" => SymbolRegular.Notepad24,
        "links" => SymbolRegular.Link48,
        "other" => SymbolRegular.Shapes48,
        "deleted" => SymbolRegular.Delete48,
        _ => SymbolRegular.Folder48,
    };

    /// <summary>"4.2 MB" (formatted like the file sizes in the thread), or empty when no size is known.</summary>
    public static string FormatSize(long totalBytes) => totalBytes > 0 ? ThreadFormat.Size(totalBytes) : "";

    /// <summary>Contrast the band keeps under its text: WCAG AA for normal-size text (the banner uses the header's text sizes).</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>
    /// The band color to draw behind text of <paramref name="text"/> color <b>[my default]</b>: <paramref name="background"/> itself
    /// when the text already reaches <paramref name="minContrast"/> on it; otherwise darkened (light text) or lightened (dark
    /// text) just enough to reach it. Darkening scales all three channels by the same factor (toward black) and lightening mixes
    /// toward white, so the hue stays. Only the band changes, never the avatar.
    /// </summary>
    public static Color ReadableBackground(Color background, Color text, double minContrast = MinTextContrast)
    {
        if (ContrastRatio(background, text) >= minContrast)
        {
            return background;
        }

        var darken = RelativeLuminance(text) > RelativeLuminance(background);
        Color At(double t) => darken
            ? System.Windows.Media.Color.FromRgb(Scale(background.R, 1 - t), Scale(background.G, 1 - t), Scale(background.B, 1 - t))
            : System.Windows.Media.Color.FromRgb(Mix(background.R, t), Mix(background.G, t), Mix(background.B, t));

        // The smallest change t (0 = as is, 1 = black or white) that reaches the contrast; the contrast grows with t.
        double low = 0, high = 1;
        for (var i = 0; i < 40; i++)
        {
            var mid = (low + high) / 2;
            if (ContrastRatio(At(mid), text) >= minContrast)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return At(high);

        static byte Scale(byte channel, double factor) => (byte)Math.Floor(channel * factor);

        static byte Mix(byte channel, double t) => (byte)Math.Ceiling(channel + ((255 - channel) * t));
    }

    /// <summary>WCAG contrast ratio between two colors (1 to 21).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG relative luminance (0 for black to 1 for white).</summary>
    public static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }

    /// <summary>
    /// The circle color of an avatar made by the icon generator, or null for any other picture (the user's own). Generated
    /// avatars are a square PNG with a filled circle, transparent outside it, and a white glyph within the middle 60%: so the
    /// four corners are transparent, and four points just inside the rim (top, bottom, left, right) are one opaque color. The
    /// color must also be readable under white text (the palette guarantees 3:1). This needs no record of where the icon came
    /// from, so it works for libraries made before the banner, and it keeps the color of an avatar generated before the
    /// category was renamed (a user category's color follows its name), exactly as the chat list shows it.
    /// </summary>
    public static Color? ReadAvatarColor(byte[]? png)
    {
        if (png is not { Length: > 0 } || UserPictureMark.IsMarked(png))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var size = frame.PixelWidth;
            if (size < 16 || frame.PixelHeight != size)
            {
                return null;
            }

            var pixels = new byte[size * size * 4];
            new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);

            (byte B, byte G, byte R, byte A) At(double fx, double fy)
            {
                var x = (int)Math.Round(fx * (size - 1));
                var y = (int)Math.Round(fy * (size - 1));
                var i = ((y * size) + x) * 4;
                return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
            }

            if (new[] { At(0, 0), At(1, 0), At(0, 1), At(1, 1) }.Any(p => p.A > 4))
            {
                return null;
            }

            const double Inset = 0.08;
            var rim = new[] { At(0.5, Inset), At(0.5, 1 - Inset), At(Inset, 0.5), At(1 - Inset, 0.5) };
            var first = rim[0];
            if (rim.Any(p => p.A != 255 || Math.Abs(p.R - first.R) > 2 || Math.Abs(p.G - first.G) > 2 || Math.Abs(p.B - first.B) > 2))
            {
                return null;
            }

            var color = System.Windows.Media.Color.FromRgb(first.R, first.G, first.B);
            return ContrastRatio(color, Colors.White) >= MinAvatarContrastWithWhite ? color : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a picture WPF can read: no avatar color.
            return null;
        }
    }

    /// <summary>The color the generator gives this category's avatar, or null when there is no generator or it fails.</summary>
    private static Color? TryGetGeneratedColor(ICategoryIconGenerator? generator, Category category)
    {
        if (generator is null)
        {
            return null;
        }

        try
        {
            return generator.GetColor(category.IconKey ?? ExtensionMap.FolderIconKey, category.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.AppLog.Warn($"The banner color of '{category.Name}' could not be worked out.", ex);
            return null;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
