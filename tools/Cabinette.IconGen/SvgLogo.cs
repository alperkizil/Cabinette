using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Cabinette.IconGen;

/// <summary>One filled shape of the logo, in viewBox coordinates (transforms already applied).</summary>
internal sealed record LogoShape(PathGeometry Geometry, Color Fill);

/// <summary>
/// A logo read from a deliberately small SVG subset: <c>&lt;svg viewBox&gt;</c>, <c>rect</c> (x y width height rx ry),
/// <c>circle</c>, <c>ellipse</c>, <c>polygon</c> and <c>path d</c> with a solid <c>fill</c> (#rgb, #rrggbb, #rrggbbaa,
/// named colors, rgb()), <c>fill-rule</c>, <c>fill-opacity</c>/<c>opacity</c>, and <c>&lt;g&gt;</c> groups that pass
/// those on, plus simple <c>transform</c>s. Anything else (strokes, gradients, masks, text) is reported and skipped.
/// </summary>
internal sealed partial class SvgLogo
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private SvgLogo(string path, Rect viewBox, IReadOnlyList<LogoShape> shapes, IReadOnlyList<string> warnings)
    {
        SourcePath = path;
        ViewBox = viewBox;
        Shapes = shapes;
        Warnings = warnings;
    }

    public string SourcePath { get; }

    public Rect ViewBox { get; }

    public IReadOnlyList<LogoShape> Shapes { get; }

    public IReadOnlyList<string> Warnings { get; }

    public static SvgLogo Load(string path)
    {
        var document = XDocument.Load(path);
        var root = document.Root ?? throw new InvalidDataException($"{path}: empty document.");
        if (root.Name.LocalName != "svg")
        {
            throw new InvalidDataException($"{path}: the root element must be <svg>.");
        }

        var viewBox = ReadViewBox(root, path);
        var shapes = new List<LogoShape>();
        var warnings = new List<string>();
        var state = new Inherited(Colors.Black, FillRule.Nonzero, 1.0, 1.0, Matrix.Identity, HasFill: true);
        Walk(root, state, shapes, warnings);
        if (shapes.Count == 0)
        {
            throw new InvalidDataException($"{path}: no supported shapes found.");
        }

        return new SvgLogo(path, viewBox, shapes, warnings);
    }

    /// <summary>The logo as a drawing scaled to <paramref name="sizePx"/> × <paramref name="sizePx"/> (viewBox mapped to the square).</summary>
    public Drawing ToDrawing(double sizePx)
    {
        var group = new DrawingGroup();
        var scale = Math.Min(sizePx / ViewBox.Width, sizePx / ViewBox.Height);
        var offsetX = (sizePx - ViewBox.Width * scale) / 2;
        var offsetY = (sizePx - ViewBox.Height * scale) / 2;
        var matrix = Matrix.Identity;
        matrix.Translate(-ViewBox.X, -ViewBox.Y);
        matrix.Scale(scale, scale);
        matrix.Translate(offsetX, offsetY);
        group.Transform = new MatrixTransform(matrix);
        foreach (var shape in Shapes)
        {
            var brush = new SolidColorBrush(shape.Fill);
            brush.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, shape.Geometry));
        }

        group.Freeze();
        return group;
    }

    // ------------------------------------------------------------------ parsing

    private sealed record Inherited(Color Fill, FillRule FillRule, double FillOpacity, double Opacity, Matrix Transform, bool HasFill);

    private static void Walk(XElement element, Inherited state, List<LogoShape> shapes, List<string> warnings)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            if (child.Name.Namespace != Svg && child.Name.Namespace != XNamespace.None)
            {
                continue;
            }

            if (name is "title" or "desc" or "metadata" or "defs" or "style")
            {
                if (name == "style" || (name == "defs" && child.HasElements))
                {
                    warnings.Add($"<{name}> is ignored (only presentation attributes are supported).");
                }

                continue;
            }

            Inherited own;
            try
            {
                own = Apply(child, state);
            }
            catch (FormatException ex)
            {
                warnings.Add($"<{name}> skipped: {ex.Message}");
                continue;
            }

            if (name == "g")
            {
                Walk(child, own, shapes, warnings);
                continue;
            }

            if (Attr(child, "stroke") is { } stroke && stroke != "none")
            {
                warnings.Add($"<{name}>: stroke is not supported and was ignored. Convert strokes to filled paths.");
            }

            Geometry? geometry;
            try
            {
                geometry = name switch
                {
                    "rect" => ReadRect(child),
                    "circle" => new EllipseGeometry(new Point(Num(child, "cx"), Num(child, "cy")), Num(child, "r"), Num(child, "r")),
                    "ellipse" => new EllipseGeometry(new Point(Num(child, "cx"), Num(child, "cy")), Num(child, "rx"), Num(child, "ry")),
                    "polygon" => ReadPolygon(child),
                    "path" => ReadPath(child, own.FillRule),
                    _ => null,
                };
            }
            catch (FormatException ex)
            {
                warnings.Add($"<{name}> skipped: {ex.Message}");
                continue;
            }

            if (geometry is null)
            {
                warnings.Add($"<{name}> is not supported and was skipped.");
                continue;
            }

            if (!own.HasFill)
            {
                continue;
            }

            // Figures stay in the element's own coordinates; a transform is kept on the geometry (exact, no flattening).
            var path = PathGeometry.CreateFromGeometry(geometry);
            path.FillRule = own.FillRule;
            path.Transform = own.Transform.IsIdentity ? null : new MatrixTransform(own.Transform);
            path.Freeze();
            var alpha = own.Fill.A / 255.0 * own.FillOpacity * own.Opacity;
            var fill = Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), own.Fill.R, own.Fill.G, own.Fill.B);
            shapes.Add(new LogoShape(path, fill));
        }
    }

    private static Inherited Apply(XElement element, Inherited parent)
    {
        var result = parent;
        var style = ParseStyle(Attr(element, "style"));

        string? Get(string name) => style.TryGetValue(name, out var v) ? v : Attr(element, name);

        if (Get("fill") is { } fill)
        {
            if (fill == "none")
            {
                result = result with { HasFill = false };
            }
            else if (fill.StartsWith("url(", StringComparison.Ordinal))
            {
                throw new FormatException("gradient or pattern fills are not supported");
            }
            else
            {
                result = result with { Fill = ParseColor(fill), HasFill = true };
            }
        }

        if (Get("fill-rule") is { } rule)
        {
            result = result with { FillRule = rule == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero };
        }

        if (Get("fill-opacity") is { } fillOpacity)
        {
            result = result with { FillOpacity = ParseNumber(fillOpacity) };
        }

        if (Get("opacity") is { } opacity)
        {
            result = result with { Opacity = parent.Opacity * ParseNumber(opacity) };
        }

        if (Attr(element, "transform") is { } transform)
        {
            // SVG: the element's own transform applies first, then the parent's.
            result = result with { Transform = ParseTransform(transform) * parent.Transform };
        }

        return result;
    }

    private static Geometry ReadRect(XElement element)
    {
        var x = Num(element, "x", 0);
        var y = Num(element, "y", 0);
        var width = Num(element, "width");
        var height = Num(element, "height");
        var rxAttr = Attr(element, "rx");
        var ryAttr = Attr(element, "ry");
        var rx = rxAttr is not null ? ParseNumber(rxAttr) : ryAttr is not null ? ParseNumber(ryAttr) : 0;
        var ry = ryAttr is not null ? ParseNumber(ryAttr) : rx;
        rx = Math.Min(rx, width / 2);
        ry = Math.Min(ry, height / 2);
        return new RectangleGeometry(new Rect(x, y, width, height), rx, ry);
    }

    private static Geometry ReadPolygon(XElement element)
    {
        var numbers = NumberPattern().Matches(Attr(element, "points") ?? "").Select(m => ParseNumber(m.Value)).ToList();
        if (numbers.Count < 6)
        {
            throw new FormatException("polygon needs at least three points");
        }

        var figure = new PathFigure { StartPoint = new Point(numbers[0], numbers[1]), IsClosed = true, IsFilled = true };
        for (var i = 2; i + 1 < numbers.Count; i += 2)
        {
            figure.Segments.Add(new LineSegment(new Point(numbers[i], numbers[i + 1]), isStroked: true));
        }

        return new PathGeometry([figure]);
    }

    private static Geometry ReadPath(XElement element, FillRule fillRule)
    {
        var data = Attr(element, "d") ?? throw new FormatException("path without d");
        var normalized = SvgPathData.Normalize(data);
        var prefix = fillRule == FillRule.EvenOdd ? "F0 " : "F1 ";
        return Geometry.Parse(prefix + normalized);
    }

    private static Rect ReadViewBox(XElement root, string path)
    {
        if (Attr(root, "viewBox") is { } viewBox)
        {
            var parts = NumberPattern().Matches(viewBox).Select(m => ParseNumber(m.Value)).ToArray();
            if (parts.Length == 4 && parts[2] > 0 && parts[3] > 0)
            {
                return new Rect(parts[0], parts[1], parts[2], parts[3]);
            }
        }

        if (Attr(root, "width") is { } w && Attr(root, "height") is { } h)
        {
            return new Rect(0, 0, ParseNumber(w), ParseNumber(h));
        }

        throw new InvalidDataException($"{path}: the <svg> element needs a viewBox (or width and height).");
    }

    // ------------------------------------------------------------------ values

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value.Trim();

    private static double Num(XElement element, string name, double? fallback = null)
    {
        var value = Attr(element, name);
        if (value is null)
        {
            return fallback ?? throw new FormatException($"missing {name}");
        }

        return ParseNumber(value);
    }

    private static double ParseNumber(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }

        if (trimmed.EndsWith('%'))
        {
            return double.Parse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0;
        }

        return double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    internal static Color ParseColor(string text)
    {
        var value = text.Trim();
        var rgb = RgbPattern().Match(value);
        if (rgb.Success)
        {
            return Color.FromRgb(
                (byte)Math.Clamp(int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture), 0, 255),
                (byte)Math.Clamp(int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture), 0, 255),
                (byte)Math.Clamp(int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture), 0, 255));
        }

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length == 6)
            {
                return Color.FromRgb(Hex(hex, 0), Hex(hex, 2), Hex(hex, 4));
            }

            if (hex.Length == 8)
            {
                // SVG/CSS order is #RRGGBBAA.
                return Color.FromArgb(Hex(hex, 6), Hex(hex, 0), Hex(hex, 2), Hex(hex, 4));
            }

            throw new FormatException($"bad color '{text}'");
        }

        if (ColorConverter.ConvertFromString(value) is Color named)
        {
            return named;
        }

        throw new FormatException($"bad color '{text}'");

        static byte Hex(string s, int at) => byte.Parse(s.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> ParseStyle(string? style)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(style))
        {
            return result;
        }

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon > 0)
            {
                result[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
        }

        return result;
    }

    internal static Matrix ParseTransform(string text)
    {
        var result = Matrix.Identity;
        foreach (Match match in TransformPattern().Matches(text))
        {
            var kind = match.Groups[1].Value;
            var args = NumberPattern().Matches(match.Groups[2].Value).Select(m => ParseNumber(m.Value)).ToArray();
            var m = Matrix.Identity;
            switch (kind)
            {
                case "matrix" when args.Length == 6:
                    m = new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                    break;
                case "translate" when args.Length >= 1:
                    m.Translate(args[0], args.Length > 1 ? args[1] : 0);
                    break;
                case "scale" when args.Length >= 1:
                    m.Scale(args[0], args.Length > 1 ? args[1] : args[0]);
                    break;
                case "rotate" when args.Length == 1:
                    m.Rotate(args[0]);
                    break;
                case "rotate" when args.Length == 3:
                    m.RotateAt(args[0], args[1], args[2]);
                    break;
                case "skewX" when args.Length == 1:
                    m.Skew(args[0], 0);
                    break;
                case "skewY" when args.Length == 1:
                    m.Skew(0, args[0]);
                    break;
                default:
                    throw new FormatException($"unsupported transform '{match.Value}'");
            }

            // In "a b", b applies to the shape first: total = b * a in WPF's row-vector convention.
            result = m * result;
        }

        return result;
    }

    [GeneratedRegex(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$")]
    private static partial Regex RgbPattern();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformPattern();
}
