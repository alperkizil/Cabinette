using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.Core.Categorization;
using Wpf.Ui.Controls;

namespace Cabinette.Windows;

/// <summary>
/// Default category avatars: a muted colored circle with a white Fluent System Icons glyph (or a short label for
/// programming languages that have no Fluent glyph), transparent outside the circle. Rendered with WPF.
/// </summary>
/// <remarks>
/// Every key of <see cref="ExtensionMap.BuiltInCategories"/> has a fixed glyph and color. "folder" (user categories and
/// folders added from disk) and unknown keys use the folder glyph with a color picked by a stable hash of the seed text,
/// so different user categories get different, but always the same, colors. Callable from any thread.
/// </remarks>
public sealed class CategoryIconGenerator : ICategoryIconGenerator
{
    private const double GlyphEmRatio = 0.6;   // Fluent glyphs use ~20/24 of their em box → ink ≈ 50% of the diameter.
    private const double MaxInkRatio = 0.56;
    private const double LabelCapRatio = 0.29;
    private const double LabelMaxWidthRatio = 0.6;

    /// <summary>Muted palette chosen to sit well on both the cream (#FBF3E4) and the dark (#111B21) theme, ≥ 3:1 against white.</summary>
    internal static readonly IReadOnlyList<Color> Palette =
    [
        Rgb(0xC6, 0x5A, 0x55), // 0 red
        Rgb(0xC8, 0x74, 0x3D), // 1 orange
        Rgb(0xB9, 0x8A, 0x2C), // 2 amber
        Rgb(0x8A, 0x94, 0x40), // 3 olive
        Rgb(0x4F, 0x9A, 0x63), // 4 green
        Rgb(0x2E, 0x8F, 0x86), // 5 teal
        Rgb(0x2F, 0x87, 0xAD), // 6 ocean
        Rgb(0x4A, 0x72, 0xBE), // 7 blue
        Rgb(0x63, 0x66, 0xC2), // 8 indigo
        Rgb(0x8A, 0x5E, 0xBA), // 9 violet
        Rgb(0xA8, 0x57, 0x9D), // 10 plum
        Rgb(0xC4, 0x54, 0x7A), // 11 rose
        Rgb(0x68, 0x7B, 0x8E), // 12 slate
        Rgb(0x8E, 0x6B, 0x50), // 13 brown
    ];

    private static readonly FrozenDictionary<string, IconSpec> Specs = new Dictionary<string, IconSpec>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio"] = Glyph(SymbolFilled.MusicNote224, 11),
        ["video"] = Glyph(SymbolFilled.Video24, 9),
        ["images"] = Glyph(SymbolFilled.Image24, 5),
        ["text"] = Glyph(SymbolFilled.TextDescription24, 12),
        ["documents"] = Glyph(SymbolFilled.DocumentText24, 7),
        ["documents.pdf"] = Label("PDF", 0),
        ["documents.word"] = Label("W", 7),
        ["spreadsheets"] = Glyph(SymbolFilled.Table24, 4),
        ["presentations"] = Glyph(SymbolFilled.ChartMultiple24, 1),
        ["code"] = Glyph(SymbolFilled.Code24, 8),
        ["code.python"] = Label("Py", 6),
        ["code.java"] = Glyph(SymbolFilled.DrinkCoffee24, 1),
        ["code.csharp"] = Label("C#", 9),
        ["code.cpp"] = Label("C++", 7),
        ["code.jsts"] = Label("JS", 2),
        ["code.web"] = Glyph(SymbolFilled.Globe24, 5),
        ["code.shell"] = Glyph(SymbolFilled.WindowConsole20, 12),
        ["executable"] = Glyph(SymbolFilled.Apps24, 6),
        ["executable.msi"] = Label("MSI", 5),
        ["executable.exe"] = Label("EXE", 9),
        ["archives"] = Glyph(SymbolFilled.Archive24, 13),
        ["ebooks"] = Glyph(SymbolFilled.Book24, 10),
        ["ebooks.epub"] = Label("EPUB", 4),
        ["ebooks.pdf"] = Label("PDF", 1),
        ["fonts"] = Glyph(SymbolFilled.TextFont24, 3),
        ["design"] = Glyph(SymbolFilled.Color24, 11),
        ["models3d"] = Glyph(SymbolFilled.Cube24, 8),
        ["diskimages"] = Glyph(SymbolFilled.HardDrive24, 6),
        ["notes"] = Glyph(SymbolFilled.Note24, 2),
        ["links"] = Glyph(SymbolFilled.Link24, 7),
        ["other"] = Glyph(SymbolFilled.Shapes24, 12),
        ["deleted"] = Glyph(SymbolFilled.Delete24, 0),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<GlyphTypeface> FluentFilled = new(LoadFluentFont, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RenderThread> SharedRenderThread = new(() => new RenderThread(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lock RenderLock = new();

    public byte[] Generate(string? iconKey, string seedText, int sizePx = 256)
    {
        sizePx = Math.Clamp(sizePx, 16, 1024);
        var spec = Resolve(iconKey, seedText);

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Dispatcher.FromThread(Thread.CurrentThread) is not null)
        {
            return Render(spec, sizePx);
        }

        return SharedRenderThread.Value.Invoke(() => Render(spec, sizePx));
    }

    /// <summary>All keys with a dedicated design ("folder" included).</summary>
    internal static IReadOnlyCollection<string> KnownKeys => [.. Specs.Keys, ExtensionMap.FolderIconKey];

    internal static Color ResolveColor(string? iconKey, string seedText) => Resolve(iconKey, seedText).Color;

    public Color GetColor(string? iconKey, string seedText) => ResolveColor(iconKey, seedText);

    private static IconSpec Resolve(string? iconKey, string seedText)
    {
        if (iconKey is not null && Specs.TryGetValue(iconKey, out var spec))
        {
            return spec;
        }

        // "folder" and unknown keys: the folder glyph in a color that is stable for the same name.
        return new IconSpec((int)SymbolFilled.Folder24, null, Palette[(int)(StableHash(seedText) % (uint)Palette.Count)]);
    }

    /// <summary>FNV-1a over the lower-cased text (string.GetHashCode is randomized per process).</summary>
    internal static uint StableHash(string? text)
    {
        var hash = 2166136261u;
        foreach (var c in (text ?? "").Trim().ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    private static byte[] Render(IconSpec spec, int size)
    {
        lock (RenderLock)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var radius = size / 2.0;
                var center = new Point(radius, radius);
                var fill = new SolidColorBrush(spec.Color);
                fill.Freeze();
                dc.DrawEllipse(fill, null, center, radius, radius);

                var (geometry, transform) = spec.Label is null ? BuildGlyph(spec.CodePoint, size) : BuildLabel(spec.Label, size);
                if (geometry is not null)
                {
                    dc.PushTransform(transform);
                    dc.DrawGeometry(Brushes.White, null, geometry);
                    dc.Pop();
                }
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return Interop.DibConverter.EncodePng(bitmap);
        }
    }

    private static (Geometry? Geometry, Transform Transform) BuildGlyph(int codePoint, int size)
    {
        var font = FluentFilled.Value;
        if (!font.CharacterToGlyphMap.TryGetValue(codePoint, out var glyphIndex))
        {
            return (null, Transform.Identity);
        }

        var em = size * GlyphEmRatio;
        var geometry = font.GetGlyphOutline(glyphIndex, em, em);
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (null, Transform.Identity);
        }

        var scale = Math.Min(1.0, size * MaxInkRatio / Math.Max(bounds.Width, bounds.Height));
        return (geometry, CenterTransform(bounds, scale, size / 2.0, size / 2.0));
    }

    private static (Geometry? Geometry, Transform Transform) BuildLabel(string label, int size)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        const double Em = 100;
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Em, Brushes.White, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0)
        {
            return (null, Transform.Identity);
        }

        // Size and center on the cap height so descenders ("y") don't push the label up.
        var capHeight = typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs.CapsHeight * Em : bounds.Height;
        var scale = Math.Min(size * LabelCapRatio / capHeight, size * LabelMaxWidthRatio / bounds.Width);
        var capBox = new Rect(bounds.X, text.Baseline - capHeight, bounds.Width, capHeight);
        return (geometry, CenterTransform(capBox, scale, size / 2.0, size / 2.0));
    }

    private static Transform CenterTransform(Rect box, double scale, double cx, double cy)
    {
        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(-(box.X + (box.Width / 2)), -(box.Y + (box.Height / 2))));
        group.Children.Add(new ScaleTransform(scale, scale));
        group.Children.Add(new TranslateTransform(cx, cy));
        group.Freeze();
        return group;
    }

    private static GlyphTypeface LoadFluentFont()
    {
        const string ResourceName = "resources/fonts/fluentsystemicons-filled.ttf";
        var assembly = typeof(SymbolIcon).Assembly;

        try
        {
            // Outside a running WPF Application (tests, background services) the pack:// scheme and the "application"
            // package only get registered once these types are initialized.
            _ = PackUriHelper.UriSchemePack;
            _ = Application.Current;
            return new GlyphTypeface(new Uri($"pack://application:,,,/{assembly.GetName().Name};component/{ResourceName}", UriKind.Absolute));
        }
        catch (Exception)
        {
            // No application package (e.g. unit tests): fall back to a copy of the embedded font on disk.
        }

        var version = assembly.GetName().Version?.ToString() ?? "0";
        var directory = Path.Combine(Path.GetTempPath(), "Cabinette", "fonts");
        var file = Path.Combine(directory, $"FluentSystemIcons-Filled-{version}.ttf");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(directory);
            using var stream = new ResourceManager(assembly.GetName().Name + ".g", assembly).GetStream(ResourceName)
                ?? throw new InvalidOperationException("The Fluent System Icons font was not found in WPF-UI.");
            var temp = file + "." + Environment.ProcessId + ".tmp";
            using (var output = File.Create(temp))
            {
                stream.CopyTo(output);
            }

            File.Move(temp, file, overwrite: true);
        }

        return new GlyphTypeface(new Uri(file, UriKind.Absolute));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static IconSpec Glyph(SymbolFilled symbol, int color) => new((int)symbol, null, Palette[color]);

    private static IconSpec Label(string text, int color) => new(0, text, Palette[color]);

    private sealed record IconSpec(int CodePoint, string? Label, Color Color);

    /// <summary>A dedicated STA thread for rendering when the caller is not a WPF (dispatcher) thread.</summary>
    private sealed class RenderThread
    {
        private readonly BlockingCollection<(Func<byte[]> Work, TaskCompletionSource<byte[]> Result)> _queue = [];

        public RenderThread()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Cabinette icon renderer" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public byte[] Invoke(Func<byte[]> work)
        {
            var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((work, result));
            return result.Task.GetAwaiter().GetResult();
        }

        private void Loop()
        {
            foreach (var (work, result) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    result.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                }
            }
        }
    }
}
