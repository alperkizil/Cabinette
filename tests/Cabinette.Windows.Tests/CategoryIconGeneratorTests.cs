using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.Core.Categorization;

namespace Cabinette.Windows.Tests;

public sealed class CategoryIconGeneratorTests(ITestOutputHelper output)
{
    private readonly CategoryIconGenerator _generator = new();

    public static TheoryData<string?> AllKeys()
    {
        var data = new TheoryData<string?>();
        foreach (var key in ExtensionMap.BuiltInCategories.Select(c => c.IconKey).Append(ExtensionMap.FolderIconKey).Distinct())
        {
            data.Add(key);
        }

        data.Add(null);
        data.Add("no-such-icon");
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void ProducesValidTransparentCircle(string? key)
    {
        const int Size = 128;
        var png = _generator.Generate(key, "Seed " + key, Size);

        var image = Pixels.DecodePng(png);
        Assert.Equal(Size, image.PixelWidth);
        Assert.Equal(Size, image.PixelHeight);

        var pixels = Pixels.Bgra(image, out var w, out var h);
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (6, 6) })
        {
            Assert.Equal(0, Pixels.At(pixels, w, x, y).A);
        }

        // Fully opaque, colored ring just inside the circle's top edge.
        var ring = Pixels.At(pixels, w, w / 2, 4);
        Assert.Equal(255, ring.A);
        Assert.False(ring is { R: > 240, G: > 240, B: > 240 });

        // A white glyph covering a reasonable share of the circle.
        var white = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 245 && pixels[i + 1] > 245 && pixels[i + 2] > 245 && pixels[i + 3] == 255)
            {
                white++;
            }
        }

        var share = white / (double)(w * h);
        output.WriteLine($"{key ?? "(null)"}: white share {share:P1}");
        Assert.InRange(share, 0.02, 0.30);
    }

    [Fact]
    public void ColorsAreStablePerSeedAndVaried()
    {
        var a = _generator.Generate("folder", "Invoices", 64);
        var b = _generator.Generate("folder", "Invoices", 64);
        Assert.Equal(a, b);
        Assert.Equal(CategoryIconGenerator.StableHash("Invoices"), CategoryIconGenerator.StableHash("invoices "));

        var colors = Enumerable.Range(0, 12).Select(i => CategoryIconGenerator.ResolveColor(null, "Category " + i)).Distinct().Count();
        Assert.True(colors >= 4, $"only {colors} distinct colors");
    }

    [Fact]
    public void WorksOnStaThreadsWithAndWithoutDispatcher()
    {
        var noDispatcher = Sta.Run(() => _generator.Generate("audio", "Audio", 48));
        var withDispatcher = Sta.Run(() =>
        {
            _ = Dispatcher.CurrentDispatcher;
            return _generator.Generate("audio", "Audio", 48);
        });
        var fromMta = _generator.Generate("audio", "Audio", 48);

        Assert.Equal(fromMta, noDispatcher);
        Assert.Equal(fromMta, withDispatcher);
    }

    [Fact]
    public void ParallelCallsFromThreadPool()
    {
        var results = new byte[16][];
        Parallel.For(0, results.Length, i => results[i] = _generator.Generate("video", "Video", 64));
        Assert.All(results, r => Assert.Equal(results[0], r));
    }

    /// <summary>Writes every icon and a contact sheet (cream and dark backgrounds) for visual review.</summary>
    [Fact]
    public void WritesContactSheet()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Cabinette.Windows.Tests", "icons");
        Directory.CreateDirectory(folder);

        List<(string? Key, string Name)> keys =
        [
            .. ExtensionMap.BuiltInCategories.Select(c => ((string?)c.IconKey, c.Name)),
            (ExtensionMap.FolderIconKey, "My folder"),
            (null, "Invoices"),
            ("unknown", "Holiday 2026"),
        ];

        const int Icon = 96;
        const int Cell = 132;
        var columns = 10;
        var rows = (keys.Count + columns - 1) / columns;
        const int Small = 40;
        var pngs = keys.Select(k => _generator.Generate(k.Key, k.Name, 256)).ToList();
        var smallPngs = keys.Select(k => _generator.Generate(k.Key, k.Name, Small)).ToList();
        var smallRowHeight = Small + 16;
        for (var i = 0; i < keys.Count; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"{keys[i].Key ?? "null"}-{i}.png"), pngs[i]);
        }

        var sheetPath = Path.Combine(folder, "contact-sheet.png");
        Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var backgrounds = new[] { Color.FromRgb(0xFB, 0xF3, 0xE4), Color.FromRgb(0x11, 0x1B, 0x21) };
                var textColors = new[] { Color.FromRgb(0x3D, 0x32, 0x22), Color.FromRgb(0xE9, 0xED, 0xEF) };
                var bandHeight = (rows * Cell) + smallRowHeight;
                for (var band = 0; band < 2; band++)
                {
                    var top = band * bandHeight;
                    dc.DrawRectangle(new SolidColorBrush(backgrounds[band]), null, new Rect(0, top, Math.Max(columns * Cell, 16 + (keys.Count * (Small + 4))), bandHeight));

                    // Chat-list size, rendered 1:1.
                    for (var i = 0; i < keys.Count; i++)
                    {
                        dc.DrawImage(Pixels.DecodePng(smallPngs[i]), new Rect(8 + (i * (Small + 4)), top + (rows * Cell) + 8, Small, Small));
                    }

                    for (var i = 0; i < keys.Count; i++)
                    {
                        var x = (i % columns) * Cell;
                        var y = top + ((i / columns) * Cell);
                        var bitmap = Pixels.DecodePng(pngs[i]);
                        dc.DrawImage(bitmap, new Rect(x + ((Cell - Icon) / 2), y + 8, Icon, Icon));
                        var label = new FormattedText(
                            keys[i].Key ?? "(null)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), 12, new SolidColorBrush(textColors[band]), 1.0);
                        dc.DrawText(label, new Point(x + ((Cell - label.Width) / 2), y + Icon + 12));
                    }
                }
            }

            var sheet = new RenderTargetBitmap(Math.Max(columns * Cell, 16 + (keys.Count * (Small + 4))), ((rows * Cell) + smallRowHeight) * 2, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            using var stream = File.Create(sheetPath);
            encoder.Save(stream);
        });

        output.WriteLine(sheetPath);
        Assert.True(File.Exists(sheetPath));
    }
}
