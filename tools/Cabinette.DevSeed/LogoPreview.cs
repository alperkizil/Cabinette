using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

/// <summary>Dev aid: renders a logo SVG (rect, circle, ellipse, path; solid fills) to a contact-sheet PNG.</summary>
internal static class LogoPreview
{
    public static void Render(string svgPath, string pngPath)
    {
        var thread = new Thread(() => RenderSta(svgPath, pngPath));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// Like a real .ico: the master SVG at 128/64/48/32 and the small variant at 24/16, on cream and dark rows,
    /// with a caption. Used to compare logo options fairly.
    /// </summary>
    public static void RenderPair(string bigSvg, string smallSvg, string caption, string pngPath)
    {
        var thread = new Thread(() =>
        {
            var (big, bigBox) = Load(bigSvg);
            var (small, smallBox) = Load(smallSvg);
            (DrawingGroup Logo, double[] Box, int Size)[] cells =
                [(big, bigBox, 128), (big, bigBox, 64), (big, bigBox, 48), (big, bigBox, 32), (small, smallBox, 24), (small, smallBox, 16)];
            var rows = new[] { Color.FromRgb(0xFB, 0xF3, 0xE4), Color.FromRgb(0x20, 0x20, 0x20) };
            const int captionHeight = 34;
            var width = cells.Sum(c => c.Size) + (cells.Length + 1) * 20;
            var rowHeight = 128 + 32;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, captionHeight));
                var text = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 15, Brushes.Black, 1.0);
                dc.DrawText(text, new Point(14, 7));
                for (var r = 0; r < rows.Length; r++)
                {
                    var top = captionHeight + r * rowHeight;
                    dc.DrawRectangle(new SolidColorBrush(rows[r]), null, new Rect(0, top, width, rowHeight));
                    double x = 20;
                    foreach (var (logo, box, size) in cells)
                    {
                        dc.DrawImage(RenderFrame(logo, box, size), new Rect(x, top + 16 + (128 - size), size, size));
                        x += size + 20;
                    }
                }
            }

            var bitmap = new RenderTargetBitmap(width, captionHeight + rows.Length * rowHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(pngPath);
            encoder.Save(stream);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static (DrawingGroup Logo, double[] ViewBox) Load(string svgPath)
    {
        var svg = XElement.Load(svgPath);
        var viewBox = ((string)svg.Attribute("viewBox")!).Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var logo = new DrawingGroup();
        foreach (var element in svg.Elements())
        {
            var fill = (Brush)new BrushConverter().ConvertFromString((string)element.Attribute("fill")!)!;
            double A(string name) => double.Parse((string?)element.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
            Geometry geometry = element.Name.LocalName switch
            {
                "rect" => new RectangleGeometry(new Rect(A("x"), A("y"), A("width"), A("height")), A("rx"), A("rx")),
                "circle" => new EllipseGeometry(new Point(A("cx"), A("cy")), A("r"), A("r")),
                "ellipse" => new EllipseGeometry(new Point(A("cx"), A("cy")), A("rx"), A("ry")),
                "path" => Geometry.Parse("F1 " + (string)element.Attribute("d")!),
                _ => Geometry.Empty,
            };
            logo.Children.Add(new GeometryDrawing(fill, null, geometry));
        }

        return (logo, viewBox);
    }

    private static void RenderSta(string svgPath, string pngPath)
    {
        var svg = XElement.Load(svgPath);
        var viewBox = ((string)svg.Attribute("viewBox")!).Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var logo = new DrawingGroup();
        foreach (var element in svg.Elements())
        {
            var fill = (Brush)new BrushConverter().ConvertFromString((string)element.Attribute("fill")!)!;
            double A(string name) => double.Parse((string?)element.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
            Geometry geometry = element.Name.LocalName switch
            {
                "rect" => new RectangleGeometry(new Rect(A("x"), A("y"), A("width"), A("height")), A("rx"), A("rx")),
                "circle" => new EllipseGeometry(new Point(A("cx"), A("cy")), A("r"), A("r")),
                "ellipse" => new EllipseGeometry(new Point(A("cx"), A("cy")), A("rx"), A("ry")),
                "path" => Geometry.Parse("F1 " + (string)element.Attribute("d")!),
                _ => Geometry.Empty,
            };
            logo.Children.Add(new GeometryDrawing(fill, null, geometry));
        }

        int[] sizes = [256, 64, 48, 32, 24, 16];
        var backgrounds = new[] { Color.FromRgb(0xFB, 0xF3, 0xE4), Color.FromRgb(0x20, 0x20, 0x20) };
        var width = sizes.Sum() + (sizes.Length + 1) * 24;
        var height = backgrounds.Length * (256 + 48);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (var row = 0; row < backgrounds.Length; row++)
            {
                var top = row * (256 + 48);
                dc.DrawRectangle(new SolidColorBrush(backgrounds[row]), null, new Rect(0, top, width, 256 + 48));
                double x = 24;
                foreach (var size in sizes)
                {
                    // Render each size into its own bitmap first, like an icon frame, then place it unscaled.
                    var frame = RenderFrame(logo, viewBox, size);
                    dc.DrawImage(frame, new Rect(x, top + 24 + (256 - size), size, size));
                    x += size + 24;
                }
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
    }

    private static BitmapSource RenderFrame(DrawingGroup logo, double[] viewBox, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / viewBox[2], size / viewBox[3]));
            dc.PushTransform(new TranslateTransform(-viewBox[0], -viewBox[1]));
            dc.DrawDrawing(logo);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
