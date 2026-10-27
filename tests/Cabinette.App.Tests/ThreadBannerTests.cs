using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.App.DesignTime;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using Wpf.Ui.Controls;
using Xunit.Abstractions;

namespace Cabinette.App.Tests;

/// <summary>
/// The thread banner (SPEC.md section 4, "Header: a banner"): its color (the avatar's, the theme's for the user's own picture,
/// the accent for Starred), the darkening that keeps its text at 4.5:1, its watermark, its two lines of text, and how the thread
/// view model keeps the count and size current.
/// </summary>
public sealed class ThreadBannerTests(ITestOutputHelper output)
{
    private static readonly CategoryIconGenerator Generator = new();

    // ================================================================ readable band colors

    [Fact]
    public void Every_palette_color_reaches_4_5_to_1_under_white_once_darkened_and_keeps_its_hue()
    {
        var colors = PaletteColors();
        Assert.True(colors.Count >= 14, $"Only {colors.Count} palette colors were found.");

        output.WriteLine("avatar   band     white on avatar  white on band  factor");
        foreach (var color in colors)
        {
            var band = ThreadBanner.ReadableBackground(color, Colors.White);
            var before = ThreadBanner.ContrastRatio(color, Colors.White);
            var after = ThreadBanner.ContrastRatio(band, Colors.White);
            output.WriteLine($"{Hex(color)}  {Hex(band)}  {before,6:0.00}           {after,6:0.00}         {Factor(color, band):0.000}");

            Assert.True(after >= ThreadBanner.MinTextContrast, $"{Hex(color)} → {Hex(band)} only reaches {after:0.00}:1.");
            if (before >= ThreadBanner.MinTextContrast)
            {
                Assert.Equal(color, band);
                continue;
            }

            // Darker, by one factor for all three channels (so the same hue), and only as much as needed.
            Assert.True(band.R <= color.R && band.G <= color.G && band.B <= color.B);
            Assert.InRange(Hue(band) - Hue(color), -2.0, 2.0);
            Assert.True(after < ThreadBanner.MinTextContrast + 0.1, $"{Hex(color)} was darkened more than needed ({Hex(band)}, {after:0.00}:1).");
        }
    }

    [Fact]
    public void Bands_that_are_already_readable_stay_as_they_are_and_dark_text_lightens_a_band()
    {
        // The theme's banner colors under white, and the Starred band (the accent under the text-on-accent color).
        Assert.Equal(Hex("#8A6A3A"), ThreadBanner.ReadableBackground(Hex("#8A6A3A"), Colors.White));
        Assert.Equal(Hex("#2A3942"), ThreadBanner.ReadableBackground(Hex("#2A3942"), Colors.White));
        Assert.Equal(Hex("#C8963E"), ThreadBanner.ReadableBackground(Hex("#C8963E"), Hex("#3D2B0F")));
        Assert.Equal(Hex("#00A884"), ThreadBanner.ReadableBackground(Hex("#00A884"), Hex("#0B141A")));

        // Dark text on a band too dark for it: the band lightens (toward white) instead.
        var band = ThreadBanner.ReadableBackground(Hex("#6B6B6B"), Hex("#202020"));
        Assert.True(ThreadBanner.ContrastRatio(band, Hex("#202020")) >= ThreadBanner.MinTextContrast);
        Assert.True(band.R > 0x6B);
    }

    [Fact]
    public void Contrast_ratios_match_the_wcag_formula()
    {
        Assert.Equal(21.0, ThreadBanner.ContrastRatio(Colors.Black, Colors.White), 2);
        Assert.Equal(1.0, ThreadBanner.ContrastRatio(Hex("#2E8F86"), Hex("#2E8F86")), 3);
        Assert.Equal(4.54, ThreadBanner.ContrastRatio(Hex("#767676"), Colors.White), 2); // the classic AA gray
        Assert.Equal(ThreadBanner.ContrastRatio(Hex("#C65A55"), Colors.White), ThreadBanner.ContrastRatio(Colors.White, Hex("#C65A55")), 6);
    }

    [Fact]
    public void The_view_darkens_an_avatar_band_but_never_a_readable_one() => HiddenWpf.RunSta(() =>
    {
        var converter = ReadableBandBrushConverter.Instance;
        var teal = new SolidColorBrush(Hex("#2E8F86"));
        var white = new SolidColorBrush(Colors.White);

        var darkened = Assert.IsType<SolidColorBrush>(converter.Convert([teal, white], typeof(Brush), null!, CultureInfo.InvariantCulture));
        Assert.Equal(ThreadBanner.ReadableBackground(teal.Color, Colors.White), darkened.Color);
        Assert.NotEqual(teal.Color, darkened.Color);

        var indigo = new SolidColorBrush(Hex("#6366C2"));
        Assert.Same(indigo, converter.Convert([indigo, white], typeof(Brush), null!, CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert([null!, white], typeof(Brush), null!, CultureInfo.InvariantCulture));
    });

    // ================================================================ which color

    [Fact]
    public void A_generated_avatar_is_recognized_by_the_color_of_its_circle()
    {
        foreach (var definition in ExtensionMap.BuiltInCategories)
        {
            var png = Generator.Generate(definition.IconKey, definition.Name);
            Assert.Equal(Generator.GetColor(definition.IconKey, definition.Name), ThreadBanner.ReadAvatarColor(png));
        }

        foreach (var name in new[] { "Work", "Recipes", "Travel 2026", "Ödevler" })
        {
            var png = Generator.Generate(ExtensionMap.FolderIconKey, name);
            Assert.Equal(Generator.GetColor(ExtensionMap.FolderIconKey, name), ThreadBanner.ReadAvatarColor(png));
        }
    }

    [Fact]
    public void The_users_own_picture_is_never_taken_for_an_avatar() => HiddenWpf.RunSta(() =>
    {
        // A photo (CategoryIconService crops pictures square, so their corners are opaque).
        Assert.Null(ThreadBanner.ReadAvatarColor(Png(256, (x, y) => ((byte)x, (byte)y, 120, 255))));

        // A round logo of one pale color: shaped like an avatar, but white text wouldn't read on it.
        Assert.Null(ThreadBanner.ReadAvatarColor(Circle(256, Hex("#F2D04A"))));

        // A round logo with two colors on its rim.
        Assert.Null(ThreadBanner.ReadAvatarColor(Png(256, (x, y) => InCircle(256, x, y) ? (x < 128 ? Bgra(Hex("#4A72BE")) : Bgra(Hex("#C65A55"))) : default)));

        // Not a picture at all, or nothing.
        Assert.Null(ThreadBanner.ReadAvatarColor([1, 2, 3]));
        Assert.Null(ThreadBanner.ReadAvatarColor([]));
        Assert.Null(ThreadBanner.ReadAvatarColor(null));

        // A dark round logo of one color does count: its band has that color (and white text reads on it).
        Assert.Equal(Hex("#4A72BE"), ThreadBanner.ReadAvatarColor(Circle(256, Hex("#4A72BE"))));
    });

    [Fact]
    public void A_category_takes_its_avatars_color_or_the_theme_color_for_its_own_picture() => HiddenWpf.RunSta(() =>
    {
        var photo = Png(64, (x, y) => ((byte)(x * 4), (byte)(y * 4), 90, 255));

        // Built-in, avatar generated: the avatar's color and the built-in watermark.
        var video = Category("Video", CategoryKind.BuiltIn, "video", Generator.Generate("video", "Video"));
        var look = ThreadBanner.ForCategory(video, Generator);
        Assert.Equal(ThreadBannerKind.Avatar, look.Kind);
        Assert.Equal(Generator.GetColor("video", "Video"), look.Color);
        Assert.Equal(SymbolRegular.Filmstrip24, look.Watermark);
        Assert.Null(look.WatermarkPicture);

        // No avatar yet (generated in the background at startup): the color the generator will give it.
        look = ThreadBanner.ForCategory(Category("Audio", CategoryKind.BuiltIn, "audio", null), Generator);
        Assert.Equal(ThreadBannerKind.Avatar, look.Kind);
        Assert.Equal(Generator.GetColor("audio", "Audio"), look.Color);

        // ... and without a generator, the theme's banner color.
        Assert.Equal(ThreadBannerKind.Fallback, ThreadBanner.ForCategory(Category("Audio", CategoryKind.BuiltIn, "audio", null), null).Kind);

        // A category the user made, with their own picture: the theme's color, and the picture as the watermark.
        look = ThreadBanner.ForCategory(Category("Travel", CategoryKind.User, ExtensionMap.FolderIconKey, photo), Generator);
        Assert.Equal(ThreadBannerKind.Fallback, look.Kind);
        Assert.Null(look.Color);
        Assert.NotNull(look.WatermarkPicture);

        // A built-in category with the user's picture: the theme's color, and still the picture of what it holds.
        look = ThreadBanner.ForCategory(Category("Images", CategoryKind.BuiltIn, "images", photo), Generator);
        Assert.Equal(ThreadBannerKind.Fallback, look.Kind);
        Assert.Equal(SymbolRegular.ImageMultiple48, look.Watermark);
        Assert.Null(look.WatermarkPicture);

        // A user category renamed after its avatar was made keeps the avatar's color (the chat list shows that one).
        var renamed = Category("Holidays", CategoryKind.User, ExtensionMap.FolderIconKey, Generator.Generate(ExtensionMap.FolderIconKey, "Trips"));
        look = ThreadBanner.ForCategory(renamed, Generator);
        Assert.Equal(Generator.GetColor(ExtensionMap.FolderIconKey, "Trips"), look.Color);
        Assert.Equal(SymbolRegular.Folder48, look.Watermark);
    });

    [Fact]
    public void Every_built_in_category_has_a_watermark_of_its_own()
    {
        Assert.Equal(SymbolRegular.Filmstrip24, ThreadBanner.WatermarkFor("video"));
        Assert.Equal(SymbolRegular.ImageMultiple48, ThreadBanner.WatermarkFor("images"));
        Assert.Equal(SymbolRegular.MusicNote224, ThreadBanner.WatermarkFor("audio"));
        Assert.Equal(SymbolRegular.Delete48, ThreadBanner.WatermarkFor("deleted"));
        Assert.Equal(SymbolRegular.Folder48, ThreadBanner.WatermarkFor(ExtensionMap.FolderIconKey));
        Assert.Equal(SymbolRegular.Folder48, ThreadBanner.WatermarkFor(null));
        Assert.Equal(SymbolRegular.Star48, ThreadBanner.StarredLook.Watermark);
        Assert.All(ExtensionMap.BuiltInCategories, d => Assert.NotEqual(SymbolRegular.Folder48, ThreadBanner.WatermarkFor(d.IconKey)));
    }

    [Fact]
    public void Every_watermark_glyph_is_in_the_icon_font()
    {
        // ui:SymbolIcon turns a symbol into one UTF-16 character, so one above U+FFFF (many 48 px designs) draws as another
        // character: Filmstrip48 (U+F01CD) comes out as "Ǎ".
        var font = FluentRegularFont();
        var symbols = ExtensionMap.BuiltInCategories.Select(d => ThreadBanner.WatermarkFor(d.IconKey))
            .Append(ThreadBanner.WatermarkFor(ExtensionMap.FolderIconKey))
            .Append(ThreadBanner.StarredLook.Watermark)
            .Distinct();
        Assert.All(symbols, symbol =>
        {
            Assert.True((int)symbol <= 0xFFFF, $"{symbol} (U+{(int)symbol:X}) is above U+FFFF.");
            Assert.True(font.CharacterToGlyphMap.ContainsKey((int)symbol), $"{symbol} is not in the font.");
        });
    }

    // ================================================================ text

    [Fact]
    public void The_second_line_is_the_count_then_the_total_size()
    {
        var banner = new ThreadBanner { CountText = ThreadFormat.FileCount(12), SizeText = ThreadBanner.FormatSize(11_534_336) };
        Assert.Equal($"12 files · {ThreadFormat.Size(11_534_336)}", banner.Subtitle);

        banner.CountText = ThreadFormat.FileCount(1);
        Assert.StartsWith("1 file · ", banner.Subtitle, StringComparison.Ordinal);

        // No size known (or nothing there): the count alone.
        banner.SizeText = ThreadBanner.FormatSize(0);
        Assert.Equal("", banner.SizeText);
        Assert.Equal("1 file", banner.Subtitle);

        // Sizes read like the sizes in the thread.
        Assert.Equal(ThreadFormat.Size(900), ThreadBanner.FormatSize(900));
        Assert.Equal(ThreadFormat.Size(3_400_000_000), ThreadBanner.FormatSize(3_400_000_000));
    }

    // ================================================================ the thread view model

    [Fact]
    public void A_built_in_thread_shows_its_name_count_size_and_avatar_color() => StaAsync.Run(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var images = await CategoryAsync(library, "Images");
        using var thread = new ThreadViewModel(Services(library));

        await thread.LoadAsync(images.Id);

        var files = await library.GetThreadItemsAsync(images.Id);
        Assert.Equal("Images", thread.Banner.Title);
        Assert.Equal(Line(files), thread.Banner.Subtitle);
        Assert.Equal(ThreadBannerKind.Avatar, thread.Banner.Kind);
        Assert.Equal(Generator.GetColor("images", "Images"), thread.Banner.Color);
        Assert.Equal(SymbolRegular.ImageMultiple48, thread.Banner.Watermark);
    });

    [Fact]
    public void A_user_category_with_its_own_picture_gets_the_theme_color_and_its_picture() => StaAsync.Run(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var travel = await CategoryAsync(library, "Travel 2026");
        Assert.NotNull(travel.IconPng); // the sample library gives it a wallpaper as its icon
        using var thread = new ThreadViewModel(Services(library));

        await thread.LoadAsync(travel.Id);

        Assert.Equal(ThreadBannerKind.Fallback, thread.Banner.Kind);
        Assert.Null(thread.Banner.Color);
        Assert.True(thread.Banner.HasWatermarkPicture);
        Assert.Equal(Line(await library.GetThreadItemsAsync(travel.Id)), thread.Banner.Subtitle);
    });

    [Fact]
    public void Starred_and_Deleted_have_bands_of_their_own() => StaAsync.Run(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        using (var starred = new ThreadViewModel(Services(library)))
        {
            await starred.LoadStarredAsync();
            Assert.Equal(ThreadBannerKind.Starred, starred.Banner.Kind);
            Assert.Equal(L.Get("Thread_StarredTitle"), starred.Banner.Title);
            Assert.Equal(SymbolRegular.Star48, starred.Banner.Watermark);
            Assert.Equal(Line(await library.GetStarredItemsAsync()), starred.Banner.Subtitle);
        }

        var deleted = await CategoryAsync(library, "Deleted");
        using var thread = new ThreadViewModel(Services(library));
        await thread.LoadAsync(deleted.Id);
        Assert.Equal(ThreadBannerKind.Avatar, thread.Banner.Kind);
        Assert.Equal(Generator.GetColor("deleted", "Deleted"), thread.Banner.Color);
        Assert.Equal(SymbolRegular.Delete48, thread.Banner.Watermark);
        Assert.Equal(Line(await library.GetThreadItemsAsync(deleted.Id)), thread.Banner.Subtitle);
    });

    [Fact]
    public void The_count_and_size_follow_files_added_moved_and_deleted() => StaAsync.Run(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var notes = await CategoryAsync(library, "Notes");
        var other = await CategoryAsync(library, "Other");
        using var thread = new ThreadViewModel(Services(library));
        await thread.LoadAsync(notes.Id);
        Assert.Equal(Line(await library.GetThreadItemsAsync(notes.Id)), thread.Banner.Subtitle);

        var added = await library.CreateNoteAsync(notes.Id, new string('n', 5000));
        await ExpectLineAsync(thread, library, notes.Id, "a note was added");

        await library.MoveToCategoryAsync([added.Id], notes.Id, other.Id);
        await ExpectLineAsync(thread, library, notes.Id, "the note moved out");

        var first = (await library.GetThreadItemsAsync(notes.Id))[0];
        await library.DeleteItemsAsync([first.Id]);
        await ExpectLineAsync(thread, library, notes.Id, "a note was deleted");
    });

    [Fact]
    public void A_search_counts_the_matches_and_keeps_the_category_size() => StaAsync.Run(async () =>
    {
        var library = SampleLibraryService.Create(withVault: true);
        var documents = await CategoryAsync(library, "Documents");
        using var thread = new ThreadViewModel(Services(library));
        await thread.LoadAsync(documents.Id);
        var files = await library.GetThreadItemsAsync(documents.Id);

        thread.IsSearchOpen = true;
        thread.SearchText = "minutes";
        await StaAsync.UntilAsync(() => Task.FromResult(thread.TotalCount == 1), "the search matched one file");

        var size = ThreadFormat.Size(files.Sum(i => i.SizeBytes ?? 0));
        Assert.Equal($"{L.Format("Thread_ResultCount", 1, files.Count)} · {size}", thread.Banner.Subtitle);
    });

    // ================================================================ helpers

    private static async Task ExpectLineAsync(ThreadViewModel thread, ILibraryService library, long categoryId, string what)
    {
        var expected = Line(await library.GetThreadItemsAsync(categoryId));
        await StaAsync.UntilAsync(() => Task.FromResult(thread.Banner.Subtitle == expected), $"{what}: \"{expected}\" (shown: \"{thread.Banner.Subtitle}\")");
    }

    /// <summary>The second line for these files, worked out from the files themselves.</summary>
    private static string Line(IReadOnlyCollection<LibraryItem> files)
    {
        var count = ThreadFormat.FileCount(files.Count);
        var size = files.Sum(i => i.SizeBytes ?? 0);
        return size > 0 ? $"{count} · {ThreadFormat.Size(size)}" : count;
    }

    private static async Task<Category> CategoryAsync(ILibraryService library, string name)
        => (await library.GetAllCategoriesAsync()).Single(c => c.Name == name);

    private static ThreadServices Services(ILibraryService library) => new(
        library,
        NullProxy.Create<IShellContextMenuService>(),
        NullProxy.Create<IShellOperations>(),
        NullProxy.Create<IFileIntake>(),
        NullProxy.Create<IDialogService>(),
        NullProxy.Create<INotificationService>(),
        NullProxy.Create<IShellNavigator>(),
        new ThumbnailLoader(NullProxy.Create<IThumbnailService>(), NullProxy.Create<IMediaInfoService>()),
        new ThreadViewState())
    {
        Directory = new CategoryDirectory(library),
        IconGenerator = Generator,
    };

    private static Category Category(string name, CategoryKind kind, string iconKey, byte[]? png) => new()
    {
        Id = 42,
        Name = name,
        FolderName = name,
        Kind = kind,
        BuiltInKey = kind == CategoryKind.BuiltIn ? iconKey : null,
        IconKey = iconKey,
        IconPng = png,
    };

    /// <summary>Every color the avatar generator hands out: the built-in categories use all of the palette, folders add any rest.</summary>
    private static List<Color> PaletteColors()
    {
        var colors = ExtensionMap.BuiltInCategories.Select(d => Generator.GetColor(d.IconKey, d.Name)).ToList();
        colors.AddRange(Enumerable.Range(0, 500).Select(i => Generator.GetColor(ExtensionMap.FolderIconKey, $"Folder {i}")));
        return colors.Distinct().OrderBy(c => ThreadBanner.ContrastRatio(c, Colors.White)).ToList();
    }

    private static byte[] Circle(int size, Color color) => Png(size, (x, y) => InCircle(size, x, y) ? Bgra(color) : default);

    private static bool InCircle(int size, int x, int y)
    {
        var r = size / 2.0;
        var dx = x + 0.5 - r;
        var dy = y + 0.5 - r;
        return (dx * dx) + (dy * dy) <= r * r;
    }

    private static (byte B, byte G, byte R, byte A) Bgra(Color c) => (c.B, c.G, c.R, 255);

    private static byte[] Png(int size, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                var i = ((y * size) + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The regular Fluent System Icons font inside WPF-UI (what ui:SymbolIcon draws with), from a copy on disk.</summary>
    private static GlyphTypeface FluentRegularFont()
    {
        var assembly = typeof(SymbolIcon).Assembly;
        using var stream = new System.Resources.ResourceManager(assembly.GetName().Name + ".g", assembly).GetStream("resources/fonts/fluentsystemicons-regular.ttf");
        Assert.NotNull(stream);
        var directory = Path.Combine(Path.GetTempPath(), "cabinette-tests", "fonts");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"FluentSystemIcons-Regular-{assembly.GetName().Version}-{Guid.NewGuid():N}.ttf");
        using (var copy = File.Create(file))
        {
            stream.CopyTo(copy);
        }

        return new GlyphTypeface(new Uri(file));
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The factor the darkening applied (the largest channel's ratio).</summary>
    private static double Factor(Color from, Color to)
    {
        var max = Math.Max(from.R, Math.Max(from.G, from.B));
        return max == 0 ? 1 : Math.Max(to.R, Math.Max(to.G, to.B)) / (double)max;
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d == 0)
        {
            return 0;
        }

        var h = max == r ? ((g - b) / d) % 6 : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h * 60 + 360) % 360;
    }
}
