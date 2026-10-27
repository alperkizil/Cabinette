using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.App.Services;
using Cabinette.Windows;
using Wpf.Ui.Extensions;

namespace Cabinette.App.DesignTime;

/// <summary>
/// Dev-only stand-in for the Windows workstream's icon generator (enabled with <c>--sample-icons</c>):
/// a colored circle with a white filled Fluent glyph. Lets the shell's icon pipeline be checked end to end.
/// </summary>
public sealed class SampleIconGenerator : ICategoryIconGenerator
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x2E, 0x9E, 0x8F), Color.FromRgb(0x4F, 0x7C, 0xD6), Color.FromRgb(0xC2, 0x6B, 0x3D),
        Color.FromRgb(0x8E, 0x5B, 0xC7), Color.FromRgb(0xD0, 0x4F, 0x6A), Color.FromRgb(0x3C, 0x8D, 0x5A),
        Color.FromRgb(0xB8, 0x8A, 0x1E), Color.FromRgb(0x5E, 0x6B, 0x7A),
    ];

    private static readonly FontFamily IconFont = new(new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Fonts/"), "./#FluentSystemIcons-Filled");

    public byte[] Generate(string? iconKey, string seedText, int sizePx = 256)
    {
        var seed = iconKey is null or "folder" ? seedText : iconKey;
        var color = Palette[(int)((uint)StableHash(seed) % Palette.Length)];
        var glyph = Glyphs.ForIconKey(iconKey).Swap().GetString();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(sizePx / 2.0, sizePx / 2.0), sizePx / 2.0, sizePx / 2.0);
            var text = new FormattedText(
                glyph,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                sizePx * 0.46,
                Brushes.White,
                1.0);
            dc.DrawText(text, new Point((sizePx - text.Width) / 2, (sizePx - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(sizePx, sizePx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in text)
            {
                hash = (hash * 31) + c;
            }

            return hash;
        }
    }
}
