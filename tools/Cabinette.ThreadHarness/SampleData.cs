using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Windows;

namespace Cabinette.ThreadHarness;

/// <summary>Realistic sample library: several days of items, copy + pointer, albums, audio, notes, a link, a missing pointer, tags, subcategories.</summary>
internal static class SampleData
{
    public const long Travel = 1;
    public const long Holiday = 2;
    public const long Beach = 3;
    public const long Mountains = 4;
    public const long Food = 5;
    public const long Receipts = 6;
    public const long Images = 7;
    public const long Big = 8;
    public const long Inbox = 9;

    private const string Wallpapers = @"C:\Windows\Web\Wallpaper";
    private const string Media = @"C:\Windows\Media";
    private const string Vault = @"C:\Users\Public\Cabinette Vault";

    private static long _id;

    public static FakeLibraryService Create(FakeMediaInfoService media, int extraCount, string tempDir)
    {
        var lib = new FakeLibraryService();
        lib.AddCategory(Travel, "Travel", null, Icon("#2E7D6B", "T"));
        lib.AddCategory(Holiday, "Holiday 2026", Travel, Icon("#D08A2E", "H"));
        lib.AddCategory(Beach, "Beach", Holiday, Icon("#2B8CC4", "B"));
        lib.AddCategory(Mountains, "Mountains", Holiday, Icon("#5E8C3A", "M"));
        lib.AddCategory(Food, "Food & drinks", Holiday, Icon("#C0563B", "F"));
        lib.AddCategory(Receipts, "Receipts", null, Icon("#7A5BB5", "R"));
        lib.AddCategory(Images, "Images", null, Icon("#D17A22", "I"), "images");
        lib.AddCategory(Big, "Big thread", null, Icon("#3B6FB5", "B"));
        lib.AddCategory(Inbox, "Inbox", null, Icon("#8C6D3F", "I"));

        var walls = Directory.Exists(Wallpapers)
            ? Directory.GetFiles(Wallpapers, "*.jpg", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        string Wall(string name) => walls.FirstOrDefault(p => p.EndsWith(name, StringComparison.OrdinalIgnoreCase)) ?? (walls.Length > 0 ? walls[0] : name);

        Directory.CreateDirectory(tempDir);
        var urlPath = Path.Combine(tempDir, "Link 2026-10-04 10-22-11.url");
        System.IO.File.WriteAllText(urlPath, "[InternetShortcut]\r\nURL=https://www.lonelyplanet.com/italy/amalfi-coast/things-to-do\r\n");

        // 40 days ago
        Add(lib, MakeItem("Passport scan.pdf", @"D:\Scans\Passport scan.pdf", ItemMode.Pointer, 1_245_000), At(40, 9, 12), [Holiday, Receipts], ["travel", "documents"]);
        Add(lib, MakeItem("Travel insurance policy 2026 - full terms and conditions for the family plan (signed copy).docx", VaultPath("Travel insurance policy 2026.docx"), ItemMode.Copy, 382_000), At(40, 9, 15), [Holiday]);

        // 12 days ago: an album of six beach photos and a packing list
        string[] album = ["img20.jpg", "img21.jpg", "img22.jpg", "img23.jpg", "img24.jpg", "img25.jpg"];
        for (var i = 0; i < album.Length; i++)
        {
            Add(lib, MakeItem($"IMG_18{i + 20}.jpg", Wall(album[i]), ItemMode.Copy, 2_400_000 + (i * 91_000)) with { BatchId = "beach-batch" }, At(12, 18, 40), [Beach]);
        }

        AddNote(lib, "Packing list:\n- Sunscreen and hats\n- Passports and tickets\n- Phone chargers\n- Snorkel gear", At(12, 19, 2), [Holiday], ["todo"]);

        // 4 days ago: link, voice memo, a missing pointer, a spreadsheet
        Add(lib, MakeItem("Link 2026-10-04 10-22-11.url", urlPath, ItemMode.Copy, 120) with { Kind = ItemKind.Link }, At(4, 10, 22), [Holiday]);
        Add(lib, MakeItem("Alarm01.wav", Path.Combine(Media, "Alarm01.wav"), ItemMode.Copy, SizeOf(Path.Combine(Media, "Alarm01.wav"))), At(4, 11, 5), [Holiday], ["voice-memo"]);
        Add(lib, MakeItem("Hotel booking confirmation.pdf", @"E:\Bookings\Hotel booking confirmation.pdf", ItemMode.Pointer, 212_000) with { IsMissing = true }, At(4, 11, 30), [Holiday], ["booking"]);
        Add(lib, MakeItem("Budget 2026.xlsx", VaultPath("Budget 2026.xlsx"), ItemMode.Copy, 45_300), At(4, 12, 10), [Holiday], ["budget", "2026"]);
        Add(lib, MakeItem("Amalfi coast day trips.pptx", @"D:\Presentations\Amalfi coast day trips.pptx", ItemMode.Pointer, 6_480_000), At(4, 12, 40), [Holiday]);

        // Yesterday: video, two linked photos, a long note
        var video = Wall("img28.jpg");
        media.Overrides[video] = new MediaInfo(TimeSpan.FromSeconds(84), 1920, 1080);
        Add(lib, MakeItem("Beach sunset.mp4", video, ItemMode.Copy, 48_200_000) with { Extension = "mp4", TypeKey = "video" }, At(1, 8, 30), [Beach]);
        Add(lib, MakeItem("IMG_2041.jpg", Wall("img32.jpg"), ItemMode.Pointer, 3_120_000), At(1, 9, 10), [Beach], ["beach", "family"]);
        Add(lib, MakeItem("IMG_2042.jpg", Wall("img33.jpg"), ItemMode.Pointer, 2_980_000), At(1, 9, 11), [Beach]);
        AddNote(
            lib,
            "Day 3 recap: we took the early bus along the coast road to Positano and walked down to the beach before the crowds arrived. "
            + "Lunch at the little place above the harbour was the best meal of the trip so far - lemon pasta, grilled fish and far too much bread.\n\n"
            + "In the afternoon we hiked the Path of the Gods from Bomerano. It is easier than it looks, but bring water and good shoes. "
            + "The views over the bay are unreal, especially around five when the light turns gold.\n\n"
            + "Tomorrow: ferry to Capri at 9:10, Blue Grotto if the sea is calm, back by 18:00. Remember to charge the camera batteries tonight.",
            At(1, 21, 45),
            [Holiday],
            ["journal"]);

        // Today
        Add(lib, MakeItem("Route map.png", Wall("img50.jpg"), ItemMode.Copy, 23_615) with { Extension = "png" }, At(0, 8, 5), [Mountains]);
        Add(lib, MakeItem("chimes.wav", Path.Combine(Media, "chimes.wav"), ItemMode.Pointer, SizeOf(Path.Combine(Media, "chimes.wav"))), At(0, 9, 20), [Holiday]);
        Add(lib, MakeItem("IMG_2101.jpg", Wall("img29.jpg"), ItemMode.Copy, 2_210_000) with { BatchId = "mountain-batch" }, At(0, 9, 41), [Mountains], ["mountains"]);
        Add(lib, MakeItem("IMG_2102.jpg", Wall("img30.jpg"), ItemMode.Copy, 2_330_000) with { BatchId = "mountain-batch" }, At(0, 9, 41), [Mountains], ["mountains"]);
        Add(lib, MakeItem("Photos backup.zip", VaultPath("Photos backup.zip"), ItemMode.Copy, 245_000_000), At(0, 10, 2), [Holiday]);
        AddNote(lib, "Book the ferry to Capri before Friday.", At(0, 10, 15), [Holiday], ["todo"]);
        Add(lib, MakeItem("Windows bloom.jpg", Wall("img0.jpg"), ItemMode.Copy, 1_602_752), At(0, 10, 16), [Holiday], ["wallpaper"]);

        AddBigThread(lib, walls, extraCount);
        return lib;
    }

    private static void AddBigThread(FakeLibraryService lib, string[] walls, int count)
    {
        var random = new Random(42);
        string[] docs = ["Quarterly report.pdf", "Invoice 2291.pdf", "Meeting notes.docx", "Budget.xlsx", "Slides.pptx", "main.py", "archive.zip", "setup.exe", "font.ttf"];
        string[] tags = ["work", "invoice", "2026", "family", "travel", "todo"];
        var start = DateTimeOffset.Now.AddDays(-400);
        string? batch = null;
        for (var i = 0; i < count; i++)
        {
            var added = start.AddMinutes(i * (400.0 * 24 * 60 / Math.Max(1, count)));
            var roll = random.Next(100);
            var mode = random.Next(4) == 0 ? ItemMode.Pointer : ItemMode.Copy;
            LibraryItem item;
            if (roll < 45 && walls.Length > 0)
            {
                batch = random.Next(4) == 0 ? batch ?? Guid.NewGuid().ToString("N") : null;
                item = MakeItem($"Photo {i:00000}.jpg", walls[i % walls.Length], mode, 1_000_000 + random.Next(4_000_000)) with { BatchId = batch };
            }
            else if (roll < 55)
            {
                item = MakeItem($"Recording {i:00000}.wav", Path.Combine(Media, "chord.wav"), mode, 113_964);
            }
            else if (roll < 65)
            {
                lib.AddItem(NoteItem($"Note number {i}: remember to follow up on item {random.Next(1000)}.", added, [Big], []));
                continue;
            }
            else
            {
                var name = docs[random.Next(docs.Length)];
                item = MakeItem($"{Path.GetFileNameWithoutExtension(name)} {i:00000}{Path.GetExtension(name)}", VaultPath(name), mode, random.Next(5_000_000));
            }

            var itemTags = random.Next(5) == 0 ? new[] { tags[random.Next(tags.Length)] } : [];
            lib.AddItem(item with { Id = ++_id, AddedAt = added, CategoryIds = [Big], HomeCategoryId = Big, Tags = itemTags, IsMissing = mode == ItemMode.Pointer && random.Next(30) == 0 });
        }
    }

    // ------------------------------------------------------------------ builders

    private static LibraryItem MakeItem(string name, string path, ItemMode mode, long size)
    {
        var ext = ExtensionMap.Normalize(Path.GetExtension(name));
        return new LibraryItem
        {
            Id = 0,
            FileName = name,
            Extension = ext,
            FullPath = path,
            Mode = mode,
            SizeBytes = size,
            TypeKey = ExtensionMap.GetTypeKey(ext),
            FileModifiedAt = DateTimeOffset.Now.AddDays(-50),
        };
    }

    private static void Add(FakeLibraryService lib, LibraryItem item, DateTimeOffset addedAt, long[] categories, string[]? tags = null)
        => lib.AddItem(item with
        {
            Id = ++_id,
            AddedAt = addedAt,
            CategoryIds = categories,
            HomeCategoryId = categories[0],
            Tags = tags ?? [],
        });

    private static void AddNote(FakeLibraryService lib, string text, DateTimeOffset addedAt, long[] categories, string[] tags)
        => lib.AddItem(NoteItem(text, addedAt, categories, tags), text);

    private static LibraryItem NoteItem(string text, DateTimeOffset addedAt, long[] categories, string[] tags)
    {
        var name = $"Note {addedAt:yyyy-MM-dd HH-mm-ss}.md";
        return new LibraryItem
        {
            Id = ++_id,
            Kind = ItemKind.Note,
            FileName = name,
            Extension = "md",
            FullPath = VaultPath(name),
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
            AddedAt = addedAt,
            CategoryIds = categories,
            HomeCategoryId = categories[0],
            Tags = tags,
            NotePreview = text.Length > 300 ? text[..300] : text,
            TypeKey = ExtensionMap.NotesKey,
        };
    }

    private static DateTimeOffset At(int daysAgo, int hour, int minute)
        => new(DateTime.Today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute));

    private static string VaultPath(string name) => Path.Combine(Vault, "Travel", "Holiday 2026", name);

    private static long SizeOf(string path) => System.IO.File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>A letter avatar: colored circle with a white letter (harness only; the real app uses the icon generator).</summary>
    private static byte[] Icon(string color, string letter)
    {
        const int Size = 128;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
            dc.DrawEllipse(brush, null, new Point(Size / 2.0, Size / 2.0), Size / 2.0, Size / 2.0);
            var text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 60, Brushes.White, 1.0);
            dc.DrawText(text, new Point((Size - text.Width) / 2, (Size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
