using System.IO;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;

namespace Cabinette.App.DesignTime;

/// <summary>
/// In-memory <see cref="ILibraryService"/> with realistic sample data, for the <c>--sample-data</c> dev flag and screenshots.
/// Items point at real files where possible (Windows wallpapers and sounds) so thumbnails work; nothing on disk is changed.
/// </summary>
public sealed class SampleLibraryService : ILibraryService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Category> _categories = [];
    private readonly Dictionary<long, LibraryItem> _items = [];
    private readonly Dictionary<long, string> _noteTexts = [];
    private readonly InMemoryActivityLog _activity = new();
    private AppSettings _settings = new();
    private long _nextCategoryId = 1000;
    private long _nextItemId = 1;
    private string? _vaultPath;

    private SampleLibraryService()
    {
    }

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public string? VaultPath
    {
        get
        {
            lock (_gate)
            {
                return _vaultPath;
            }
        }
    }

    /// <param name="withVault">False simulates a first run (no vault yet).</param>
    public static SampleLibraryService Create(bool withVault)
    {
        var service = new SampleLibraryService();
        service.Seed(withVault);
        return service;
    }

    // ================================================================== seeding

    private void Seed(bool withVault)
    {
        var now = DateTimeOffset.Now;
        _vaultPath = withVault ? AppPaths.DefaultVaultSuggestion : null;
        _settings = new AppSettings { VaultPath = _vaultPath };

        var idByKey = new Dictionary<string, long>();
        long nextBuiltIn = 1;
        foreach (var def in ExtensionMap.BuiltInCategories)
        {
            var id = nextBuiltIn++;
            idByKey[def.Key] = id;
            _categories[id] = new Category
            {
                Id = id,
                ParentId = def.ParentKey is null ? null : idByKey[def.ParentKey],
                Name = def.Name,
                FolderName = def.Name,
                Kind = def.Key == ExtensionMap.DeletedKey ? CategoryKind.System : CategoryKind.BuiltIn,
                BuiltInKey = def.Key,
                IconKey = def.IconKey,
                IsPinned = def.Key == ExtensionMap.ImagesKey,
                CreatedAt = now.AddDays(-90),
                LastViewedAt = now.AddDays(-2),
            };
        }

        long User(string name, long? parent, bool pinned = false, byte[]? icon = null)
        {
            var id = _nextCategoryId++;
            _categories[id] = new Category
            {
                Id = id,
                ParentId = parent,
                Name = name,
                FolderName = name,
                Kind = CategoryKind.User,
                IconKey = ExtensionMap.FolderIconKey,
                IsPinned = pinned,
                IconPng = icon,
                CreatedAt = now.AddDays(-30),
                LastViewedAt = now.AddDays(-2),
            };
            return id;
        }

        var work = User("Work", null, pinned: true);
        var invoices = User("Invoices", work);
        var contracts = User("Contracts", work);
        var phoenix = User("Project Phoenix", work);
        var mockups = User("Mockups", phoenix);
        var wallpapers = Directory.Exists(@"C:\Windows\Web\Wallpaper")
            ? Directory.EnumerateFiles(@"C:\Windows\Web\Wallpaper", "*.jpg", SearchOption.AllDirectories).Order().ToList()
            : [];
        byte[]? travelIcon = null;
        try
        {
            travelIcon = DrawTravelIcon();
        }
        catch (Exception)
        {
            // Sample icon only.
        }

        var travel = User("Travel 2026", null, icon: travelIcon);
        User("Recipes", null);

        string Vault(params string[] parts) => Path.Combine([_vaultPath ?? AppPaths.DefaultVaultSuggestion, .. parts]);
        var today = now.Date;
        DateTimeOffset At(int daysAgo, int hour, int minute) => new(today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute), now.Offset);

        // Images: real wallpapers (thumbnails work), most recent upload today.
        var imageId = idByKey["images"];
        for (var i = 0; i < Math.Min(12, wallpapers.Count); i++)
        {
            Add(imageId, Path.GetFileName(wallpapers[i]), wallpapers[i], At(i / 3, 9, 41 - i), tags: i % 4 == 0 ? ["wallpaper"] : []);
        }

        // The Gallery's "Date taken": most photos have one; one has only its file date; the last ones have neither.
        // One photo is a pointer (the Gallery frames it in blue, copies in green).
        DateTimeOffset?[] taken = [At(42, 16, 5), At(42, 16, 9), At(42, 17, 30), At(12, 18, 44), At(12, 18, 47), At(75, 10, 3), At(75, 10, 4), At(160, 13, 20)];
        foreach (var (item, i) in _items.Values.Where(i => i.CategoryIds.Contains(imageId)).OrderBy(i => i.Id).Select((item, i) => (item, i)).ToList())
        {
            _items[item.Id] = item with
            {
                TakenAt = i < taken.Length ? taken[i] : null,
                FileModifiedAt = i == taken.Length ? At(30, 8, 15) : item.FileModifiedAt,
                Mode = i == 5 ? ItemMode.Pointer : item.Mode,
            };
        }

        // A video and a PDF among the photos (the Gallery shows a framed video with its length, and a type icon).
        var video = @"C:\Windows\SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4";
        Add(imageId, "Beach clip.mp4", File.Exists(video) ? video : Vault("Images", "Beach clip.mp4"), At(0, 12, 30), size: File.Exists(video) ? null : 18_400_000);
        Add(imageId, "Photo book order.pdf", Vault("Images", "Photo book order.pdf"), At(1, 15, 10), size: 420_000);

        // Travel: pointer items to real files.
        string[] travelNames = ["Lisbon sunset.jpg", "Alfama street.jpg", "Sintra palace.jpg", "Boarding pass.pdf", "Hotel booking.pdf"];
        for (var i = 0; i < travelNames.Length; i++)
        {
            var real = travelNames[i].EndsWith(".jpg", StringComparison.Ordinal) && wallpapers.Count > 12 + i ? wallpapers[12 + i] : @"D:\Trips\Lisbon\" + travelNames[i];
            Add(travel, travelNames[i], real, At(1, 18, 5 + i), mode: ItemMode.Pointer, tags: ["travel", "2026"], missing: !File.Exists(real) && i == 4);
        }

        // Audio: Windows sounds; one unread from Explorer.
        var audioId = idByKey["audio"];
        var sounds = Directory.Exists(@"C:\Windows\Media") ? Directory.EnumerateFiles(@"C:\Windows\Media", "*.wav").Order().Take(10).ToList() : [];
        for (var i = 0; i < sounds.Count; i++)
        {
            Add(audioId, Path.GetFileName(sounds[i]), sounds[i], At(4, 20, i), addedBy: i == sounds.Count - 1 ? AddedBy.Explorer : AddedBy.AppUi);
        }

        // Documents: three unread (Explorer / AI).
        var docs = idByKey["documents"];
        Add(docs, "Tax return 2025.pdf", Vault("Documents", "Tax return 2025.pdf"), At(6, 11, 2), size: 812_000, tags: ["taxes", "2025"]);
        Add(docs, "Lease agreement.docx", Vault("Documents", "Lease agreement.docx"), At(0, 8, 12), size: 64_500, addedBy: AddedBy.Explorer);
        Add(docs, "Q3 report.pdf", Vault("Documents", "Q3 report.pdf"), At(0, 8, 13), size: 2_450_000, addedBy: AddedBy.Ai, tags: ["report"]);
        Add(docs, "Meeting minutes.pdf", Vault("Documents", "Meeting minutes.pdf"), At(0, 8, 47), size: 120_000, addedBy: AddedBy.Explorer);

        // Work and its subcategories.
        Add(invoices, "Invoice 2026-03.pdf", Vault("Work", "Invoices", "Invoice 2026-03.pdf"), At(9, 10, 0), size: 98_000, tags: ["invoice", "2026"]);
        Add(invoices, "Invoice 2026-04.pdf", Vault("Work", "Invoices", "Invoice 2026-04.pdf"), At(1, 16, 20), size: 101_000, addedBy: AddedBy.Explorer, tags: ["invoice", "2026"]);
        Add(invoices, "Invoice 2026-05.pdf", Vault("Work", "Invoices", "Invoice 2026-05.pdf"), At(1, 16, 21), size: 99_500, addedBy: AddedBy.Explorer, tags: ["invoice", "2026"]);
        Add(contracts, "Contract - Acme Corp.pdf", Vault("Work", "Contracts", "Contract - Acme Corp.pdf"), At(12, 14, 30), size: 340_000, tags: ["contract"]);
        Add(phoenix, "Roadmap.pptx", Vault("Work", "Project Phoenix", "Roadmap.pptx"), At(2, 9, 15), size: 4_100_000);
        Add(mockups, "Home screen.png", wallpapers.Count > 18 ? wallpapers[18] : Vault("Mockups", "Home screen.png"), At(2, 9, 30));
        Add(mockups, "Onboarding.fig", Vault("Work", "Project Phoenix", "Mockups", "Onboarding.fig"), At(2, 9, 31), size: 12_000_000);
        Add(work, "Team photo.jpg", wallpapers.Count > 19 ? wallpapers[19] : Vault("Work", "Team photo.jpg"), At(1, 17, 2));

        // Notes and links.
        var notes = idByKey["notes"];
        AddNote(notes, "Buy printer ink and A4 paper. Call the landlord about the heating on Thursday.", At(0, 7, 55));
        AddNote(notes, "Ideas for the Lisbon trip: tram 28, Time Out Market, sunset at Miradouro da Senhora do Monte.", At(3, 21, 10));
        AddNote(notes, "Wi-Fi password for the guest network is on the fridge.", At(15, 12, 0));
        var links = idByKey["links"];
        Add(links, "Link 2026-10-02 18-30-11.url", Vault("Links", "Link 2026-10-02 18-30-11.url"), At(6, 18, 30), kind: ItemKind.Link, size: 120);

        // Code, spreadsheets, archives (one missing pointer), deleted.
        Add(idByKey["code.python"], "analysis.py", Vault("Code", "Python", "analysis.py"), At(20, 13, 0), size: 4_200);
        Add(idByKey["code.csharp"], "Program.cs", Vault("Code", "C#", "Program.cs"), At(5, 15, 45), size: 2_100);
        Add(idByKey["code.jsts"], "app.ts", Vault("Code", "JS-TS", "app.ts"), At(25, 10, 5), size: 8_800);
        Add(idByKey["spreadsheets"], "Budget 2026.xlsx", Vault("Spreadsheets", "Budget 2026.xlsx"), At(8, 19, 0), size: 56_000, tags: ["budget", "2026"]);
        Add(idByKey["archives"], "Old backup.zip", @"E:\Backups\Old backup.zip", At(40, 9, 0), mode: ItemMode.Pointer, size: 3_400_000_000, missing: true);
        Add(idByKey["deleted"], "Draft.txt", Vault("Deleted", "Draft.txt"), At(10, 9, 0), size: 900);

        // Starred files from several categories, starred over the last days (the newest a little while ago, never in the future).
        Star("Tax return 2025.pdf", At(5, 18, 40));
        Star("Lisbon sunset.jpg", At(1, 19, 2));
        Star(wallpapers.Count > 1 ? Path.GetFileName(wallpapers[1]) : "", At(1, 21, 15));
        Star("Budget 2026.xlsx", now.AddHours(-3));
        Star(notes, now.AddMinutes(-95));
        Star("Contract - Acme Corp.pdf", now.AddMinutes(-25));

        SeedActivity(now, idByKey, travel);
        RecomputeLastUploads();
    }

    /// <summary>
    /// The picture of "Travel 2026": a sunset over the sea, drawn here rather than taken from a Windows wallpaper, so
    /// screenshots of the chat list can be published.
    /// </summary>
    private static byte[] DrawTravelIcon()
    {
        const int size = 256;
        const int horizon = 150;
        const double sunRadius = 52;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double r, g, b;
                if (y < horizon)
                {
                    // Sky from violet to orange; the sun sits on the horizon.
                    var t = (double)y / horizon;
                    (r, g, b) = (59 + (245 - 59) * t, 42 + (158 - 42) * t, 107 + (75 - 107) * t);
                    if (Math.Sqrt(Math.Pow(x - 128, 2) + Math.Pow(y - horizon, 2)) < sunRadius)
                    {
                        (r, g, b) = (255, 214, 122);
                    }
                }
                else
                {
                    // Sea, darker with depth, with broken stripes of the sun's reflection.
                    var t = (double)(y - horizon) / (size - horizon);
                    (r, g, b) = (43 - 21 * t, 95 - 45 * t, 138 - 59 * t);
                    if (Math.Abs(x - 128) < sunRadius * (1 - 0.7 * t) && (y - horizon) / 7 % 2 == 0)
                    {
                        (r, g, b) = (247, 184, 96);
                    }
                }

                var i = (y * size + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)b, (byte)g, (byte)r, 255);
            }
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return CategoryIconService.ScaleToSquarePng(output.ToArray(), size);
    }

    /// <summary>
    /// A few days of AI activity (Settings › AI): Claude Code and Codex searching, reading, adding, moving and deleting,
    /// and one call that failed. Oldest first, so the ids grow with time like the real log's.
    /// </summary>
    private void SeedActivity(DateTimeOffset now, Dictionary<string, long> idByKey, long travel)
    {
        const string claude = "Claude Code";
        const string codex = "Codex";
        AiActivityRef FileRef(string name) => _items.Values.FirstOrDefault(i => i.FileName == name) is { } item ? new AiActivityRef(item.Id, item.FileName) : new AiActivityRef(0, name);
        AiActivityRef CategoryRef(long id) => new(id, _categories[id].Name);
        void Log(DateTimeOffset at, string client, string tool, AiActivityDetails details)
            => _activity.Add(new ActivityEntry { At = at, Source = client, Action = tool, DetailsJson = details.ToJson() });

        var today = now.Date;
        DateTimeOffset Day(int daysAgo, int hour, int minute) => new(today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute), now.Offset);

        Log(Day(3, 16, 5), codex, AiTools.ListCategories, new AiActivityDetails { FileCount = 21 });
        Log(Day(3, 16, 6), codex, AiTools.Search, new AiActivityDetails { Query = "budget", FileCount = 2, Files = [FileRef("Budget 2026.xlsx")] });
        Log(Day(3, 16, 8), codex, AiTools.ReadFile, new AiActivityDetails { Files = [FileRef("Budget 2026.xlsx")], FileCount = 1, Offset = 0 });
        Log(Day(1, 10, 12), claude, AiTools.Search, new AiActivityDetails { Query = "invoice", FileCount = 3 });
        Log(Day(1, 10, 13), claude, AiTools.GetFiles, new AiActivityDetails { Files = [FileRef("Invoice 2026-04.pdf"), FileRef("Invoice 2026-05.pdf")], FileCount = 2 });
        Log(Day(1, 10, 15), claude, AiTools.TagFiles, new AiActivityDetails
        {
            Files = [FileRef("Invoice 2026-03.pdf"), FileRef("Invoice 2026-04.pdf"), FileRef("Invoice 2026-05.pdf")],
            FileCount = 3,
            Tags = ["paid"],
        });
        Log(now.AddMinutes(-190), claude, AiTools.AddFiles, new AiActivityDetails
        {
            Files = [FileRef("Q3 report.pdf")],
            FileCount = 1,
            Categories = [CategoryRef(idByKey["documents"])],
            Paths = [@"C:\Users\you\Downloads\Q3 report.pdf"],
            Mode = InstanceProtocol.CopyMode,
        });
        Log(now.AddMinutes(-150), claude, AiTools.CreateCategory, new AiActivityDetails { Categories = [CategoryRef(travel)], NewName = "Travel 2026" });
        Log(now.AddMinutes(-149), claude, AiTools.MoveFiles, new AiActivityDetails
        {
            Files = [FileRef("Boarding pass.pdf"), FileRef("Hotel booking.pdf")],
            FileCount = 2,
            Categories = [CategoryRef(travel), CategoryRef(idByKey["documents"])],
        });
        Log(now.AddMinutes(-120), claude, AiTools.DeleteFiles, new AiActivityDetails
        {
            Files = [new AiActivityRef(9001, "Draft 1.docx"), new AiActivityRef(9002, "Draft 2.docx"), new AiActivityRef(9003, "Old notes.txt")],
            FileCount = 3,
        });
        Log(now.AddMinutes(-95), codex, AiTools.DeleteFiles, new AiActivityDetails
        {
            Ok = false,
            ErrorCode = AiErrorCodes.RecycleBinUnavailable,
            Error = "“Old backup.zip” is on a drive without a Recycle Bin, so nothing was deleted.",
            Files = [FileRef("Old backup.zip")],
            FileCount = 1,
        });
        Log(now.AddMinutes(-94), codex, AiTools.ReadFile, new AiActivityDetails { Files = [FileRef("Tax return 2025.pdf")], FileCount = 1, Offset = 0 });
        Log(now.AddMinutes(-60), claude, AiTools.RenameFile, new AiActivityDetails { Files = [FileRef("Meeting minutes.pdf")], FileCount = 1, NewName = "Board meeting minutes.pdf" });
        Log(now.AddMinutes(-26), claude, AiTools.StarFiles, new AiActivityDetails { Files = [FileRef("Contract - Acme Corp.pdf")], FileCount = 1 });
        Log(now.AddMinutes(-4), claude, AiTools.CreateBackup, new AiActivityDetails { Paths = [@"D:\Backups\Cabinette 2026-10-24.zip"], OperationId = "op-1" });
    }

    private void Star(string fileName, DateTimeOffset at)
    {
        if (_items.Values.FirstOrDefault(i => i.FileName == fileName) is { } item)
        {
            _items[item.Id] = item with { StarredAt = at };
        }
    }

    /// <summary>Stars the oldest note in <paramref name="notesCategoryId"/>.</summary>
    private void Star(long notesCategoryId, DateTimeOffset at)
    {
        if (_items.Values.Where(i => i.CategoryIds.Contains(notesCategoryId)).MinBy(i => i.AddedAt) is { } item)
        {
            _items[item.Id] = item with { StarredAt = at };
        }
    }

    private LibraryItem Add(
        long categoryId,
        string fileName,
        string fullPath,
        DateTimeOffset addedAt,
        ItemMode mode = ItemMode.Copy,
        ItemKind kind = ItemKind.File,
        long? size = null,
        AddedBy addedBy = AddedBy.AppUi,
        string[]? tags = null,
        bool missing = false,
        string? notePreview = null)
    {
        var extension = ExtensionMap.Normalize(Path.GetExtension(fileName));
        if (size is null && File.Exists(fullPath))
        {
            size = new FileInfo(fullPath).Length;
        }

        var item = new LibraryItem
        {
            Id = _nextItemId++,
            Mode = mode,
            Kind = kind,
            FileName = fileName,
            Extension = extension,
            FullPath = fullPath,
            HomeCategoryId = categoryId,
            CategoryIds = [categoryId],
            Tags = tags ?? [],
            SizeBytes = size,
            Hash = new byte[16],
            AddedAt = addedAt,
            AddedBy = addedBy,
            BatchId = addedAt.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture),
            IsMissing = missing,
            NotePreview = notePreview,
            // Same rule as the real library: Cabinette's own notes, links and pasted images count as those types.
            TypeKey = kind switch
            {
                ItemKind.Note => ExtensionMap.NotesKey,
                ItemKind.Link => ExtensionMap.LinksKey,
                ItemKind.PastedImage => ExtensionMap.ImagesKey,
                _ => ExtensionMap.GetTypeKey(extension),
            },
        };
        _items[item.Id] = item;
        return item;
    }

    private LibraryItem AddNote(long categoryId, string text, DateTimeOffset at)
    {
        var name = $"Note {at:yyyy-MM-dd HH-mm-ss}.md";
        var item = Add(categoryId, name, Path.Combine(_vaultPath ?? "", "Notes", name), at, kind: ItemKind.Note, size: text.Length, notePreview: text.Length > 300 ? text[..300] : text);
        _noteTexts[item.Id] = text;
        return item;
    }

    // ================================================================== helpers (call under lock)

    private HashSet<long> Subtree(long id)
    {
        var result = new HashSet<long> { id };
        bool added;
        do
        {
            added = false;
            foreach (var c in _categories.Values)
            {
                if (c.ParentId is { } p && result.Contains(p) && result.Add(c.Id))
                {
                    added = true;
                }
            }
        }
        while (added);

        return result;
    }

    private IEnumerable<LibraryItem> ItemsIn(HashSet<long> subtree) => _items.Values.Where(i => i.CategoryIds.Any(subtree.Contains));

    private void RecomputeLastUploads()
    {
        foreach (var category in _categories.Values.ToList())
        {
            var last = ItemsIn(Subtree(category.Id)).Select(i => (DateTimeOffset?)i.AddedAt).Max();
            _categories[category.Id] = category with { LastUploadAt = last };
        }
    }

    private CategorySummary Summarize(Category category)
    {
        var subtree = Subtree(category.Id);
        var items = ItemsIn(subtree).ToList();
        var last = items.MaxBy(i => i.AddedAt);
        var viewed = category.LastViewedAt ?? DateTimeOffset.MinValue;
        return new CategorySummary
        {
            Category = category,
            ItemCount = items.Count,
            UnreadCount = items.Count(i => i.AddedBy is AddedBy.Explorer or AddedBy.VaultMonitor or AddedBy.Ai or AddedBy.Downloads && i.AddedAt > viewed),
            SubcategoryCount = _categories.Values.Count(c => c.ParentId == category.Id),
            LastItemName = last?.FileName,
            LastItemKind = last?.Kind,
            TotalSizeBytes = items.Sum(i => i.SizeBytes ?? 0),
        };
    }

    private static bool Matches(LibraryItem item, IReadOnlyList<string> words, IReadOnlyList<string> tags, ItemFilter? filter)
    {
        foreach (var word in words)
        {
            if (!item.FileName.Contains(word, StringComparison.CurrentCultureIgnoreCase) &&
                !(item.NotePreview?.Contains(word, StringComparison.CurrentCultureIgnoreCase) ?? false))
            {
                return false;
            }
        }

        if (tags.Any(t => !item.Tags.Contains(t, StringComparer.CurrentCultureIgnoreCase)))
        {
            return false;
        }

        if (filter is null)
        {
            return true;
        }

        return (filter.TypeKeys is not { Count: > 0 } || filter.TypeKeys.Contains(item.TypeKey)) &&
               (filter.AddedFrom is null || item.AddedAt >= filter.AddedFrom) &&
               (filter.AddedTo is null || item.AddedAt <= filter.AddedTo) &&
               (filter.MinSizeBytes is null || (item.SizeBytes ?? 0) >= filter.MinSizeBytes) &&
               (filter.MaxSizeBytes is null || (item.SizeBytes ?? 0) <= filter.MaxSizeBytes) &&
               (filter.Mode is null || item.Mode == filter.Mode) &&
               (!filter.MissingOnly || item.IsMissing) &&
               (filter.Extensions is not { Count: > 0 } || filter.Extensions.Contains(item.Extension));
    }

    private static (List<string> Words, List<string> Tags) Tokenize(string? text)
    {
        var words = new List<string>();
        var tags = new List<string>();
        foreach (var token in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('#'))
            {
                if (token.Length > 1)
                {
                    tags.Add(token[1..]);
                }
            }
            else
            {
                words.Add(token);
            }
        }

        return (words, tags);
    }

    private void Raise(LibraryChangeKind kind, IEnumerable<long>? categoryIds = null, IEnumerable<long>? itemIds = null, AddedBy? source = null)
    {
        var args = new LibraryChangedEventArgs
        {
            Kind = kind,
            CategoryIds = categoryIds?.ToList() ?? [],
            ItemIds = itemIds?.ToList() ?? [],
            Source = source,
        };
        _ = Task.Run(() => Changed?.Invoke(this, args));
    }

    private void EnsureUniqueSibling(long? parentId, string name, long? exceptId)
    {
        if (_categories.Values.Any(c => c.ParentId == parentId && c.Id != exceptId && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new LibraryException(LibraryErrorCode.NameAlreadyExists, $"There is already a category named “{name}” here.");
        }
    }

    private long CategoryForExtension(string extension)
    {
        var key = ExtensionMap.GetCategoryKey(extension);
        return _categories.Values.FirstOrDefault(c => c.BuiltInKey == key)?.Id
               ?? _categories.Values.First(c => c.BuiltInKey == ExtensionMap.OtherKey).Id;
    }

    // ================================================================== settings & vault

    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_settings);
        }
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _settings = settings with { VaultPath = _vaultPath };
        }

        Raise(LibraryChangeKind.Settings);
        return Task.CompletedTask;
    }

    public async Task InitializeVaultAsync(string vaultPath, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        lock (_gate)
        {
            _vaultPath = vaultPath;
            _settings = _settings with { VaultPath = vaultPath };
        }

        Raise(LibraryChangeKind.Settings | LibraryChangeKind.Categories);
    }

    // ================================================================== categories

    public Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync(long? parentId, CategorySort sort, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var summaries = _categories.Values
                .Where(c => c.ParentId == parentId)
                .Select(Summarize)
                .Where(s => s.Category.Kind != CategoryKind.System || s.ItemCount > 0);
            var comparer = StringComparer.CurrentCultureIgnoreCase;
            var ordered = sort == CategorySort.Alphabetical
                ? summaries.OrderByDescending(s => s.Category.IsPinned).ThenBy(s => s.Category.Name, comparer)
                : summaries.OrderByDescending(s => s.Category.IsPinned)
                    .ThenBy(s => s.Category.LastUploadAt is null)
                    .ThenByDescending(s => s.Category.LastUploadAt)
                    .ThenBy(s => s.Category.Name, comparer);
            return Task.FromResult<IReadOnlyList<CategorySummary>>(ordered.ToList());
        }
    }

    public Task<CategorySummary?> GetCategorySummaryAsync(long categoryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_categories.TryGetValue(categoryId, out var c) ? Summarize(c) : null);
        }
    }

    public Task<Category?> GetCategoryAsync(long categoryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_categories.GetValueOrDefault(categoryId));
        }
    }

    public Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Category>>(_categories.Values.ToList());
        }
    }

    public Task<IReadOnlyList<Category>> GetCategoryPathAsync(long categoryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var path = new List<Category>();
            for (long? id = categoryId; id is { } current && _categories.TryGetValue(current, out var c); id = c.ParentId)
            {
                path.Insert(0, c);
            }

            return Task.FromResult<IReadOnlyList<Category>>(path);
        }
    }

    public Task<Category> GetOrCreateBuiltInCategoryAsync(string builtInKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _categories.Values.FirstOrDefault(c => c.BuiltInKey == builtInKey);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var def = ExtensionMap.GetDefinition(builtInKey) ?? throw new LibraryException(LibraryErrorCode.NotFound, "Unknown built-in category.");
            var category = new Category
            {
                Id = _nextCategoryId++,
                Name = def.Name,
                FolderName = def.Name,
                Kind = CategoryKind.BuiltIn,
                BuiltInKey = def.Key,
                IconKey = def.IconKey,
                CreatedAt = DateTimeOffset.Now,
            };
            _categories[category.Id] = category;
            Raise(LibraryChangeKind.Categories, [category.Id]);
            return Task.FromResult(category);
        }
    }

    public Task<Category> CreateCategoryAsync(string name, long? parentId, byte[]? iconPng = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            EnsureUniqueSibling(parentId, name, null);
            var category = new Category
            {
                Id = _nextCategoryId++,
                ParentId = parentId,
                Name = name,
                FolderName = name,
                Kind = CategoryKind.User,
                IconKey = ExtensionMap.FolderIconKey,
                IconPng = iconPng,
                CreatedAt = DateTimeOffset.Now,
                LastViewedAt = DateTimeOffset.Now,
            };
            _categories[category.Id] = category;
            Raise(LibraryChangeKind.Categories, [category.Id]);
            return Task.FromResult(category);
        }
    }

    public Task RenameCategoryAsync(long categoryId, string newName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var category = _categories[categoryId];
            EnsureUniqueSibling(category.ParentId, newName, categoryId);
            _categories[categoryId] = category with { Name = newName, FolderName = newName };
        }

        // Like the real library: the whole subtree, since its paths changed (the Starred view relies on it).
        Raise(LibraryChangeKind.Categories, [.. Subtree(categoryId)]);
        return Task.CompletedTask;
    }

    public Task SetCategoryIconAsync(long categoryId, byte[]? iconPng, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _categories[categoryId] = _categories[categoryId] with { IconPng = iconPng };
        }

        Raise(LibraryChangeKind.Categories, [categoryId]);
        return Task.CompletedTask;
    }

    public Task SetCategoryPinnedAsync(long categoryId, bool pinned, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _categories[categoryId] = _categories[categoryId] with { IsPinned = pinned };
        }

        Raise(LibraryChangeKind.Categories, [categoryId]);
        return Task.CompletedTask;
    }

    public Task DeleteCategoryAsync(long categoryId, CategoryDeletePolicy policy, DeleteItemsOptions? options = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var category = _categories[categoryId];
            if (category.Kind == CategoryKind.System)
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, "The Deleted category can't be deleted.");
            }

            var subtree = Subtree(categoryId);
            var deletedId = _categories.Values.First(c => c.BuiltInKey == ExtensionMap.DeletedKey).Id;
            foreach (var item in _items.Values.ToList())
            {
                var remaining = item.CategoryIds.Where(id => !subtree.Contains(id)).ToList();
                if (remaining.Count == item.CategoryIds.Count)
                {
                    continue;
                }

                if (remaining.Count > 0)
                {
                    _items[item.Id] = item with { CategoryIds = remaining, HomeCategoryId = remaining.Contains(item.HomeCategoryId) ? item.HomeCategoryId : remaining[0] };
                }
                else if (policy == CategoryDeletePolicy.MoveToDeleted)
                {
                    _items[item.Id] = item with { CategoryIds = [deletedId], HomeCategoryId = deletedId };
                }
                else
                {
                    _items.Remove(item.Id);
                }
            }

            foreach (var id in subtree)
            {
                _categories.Remove(id);
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Categories | LibraryChangeKind.Items, [categoryId]);
        return Task.CompletedTask;
    }

    public Task MarkCategoryViewedAsync(long categoryId, CancellationToken ct = default)
    {
        var changed = new HashSet<long>();
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            foreach (var id in Subtree(categoryId))
            {
                _categories[id] = _categories[id] with { LastViewedAt = now };
                changed.Add(id);
            }

            // Ancestors keep their own badge count but should not count these anymore: mark the items as seen.
            foreach (var item in ItemsIn(Subtree(categoryId)).ToList())
            {
                if (item.AddedBy is AddedBy.Explorer or AddedBy.VaultMonitor or AddedBy.Ai or AddedBy.Downloads)
                {
                    _items[item.Id] = item with { AddedBy = AddedBy.AppUi };
                }
            }

            for (var parent = _categories[categoryId].ParentId; parent is long p && _categories.TryGetValue(p, out var ancestor); parent = ancestor.ParentId)
            {
                changed.Add(p);
            }
        }

        // Like the real library: only unread state changed (the subtree and its ancestors).
        Raise(LibraryChangeKind.Viewed, changed);
        return Task.CompletedTask;
    }

    public Task EmptyDeletedAsync(DeleteItemsOptions? options = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var deletedId = _categories.Values.First(c => c.BuiltInKey == ExtensionMap.DeletedKey).Id;
            foreach (var item in _items.Values.Where(i => i.CategoryIds.Contains(deletedId)).ToList())
            {
                _items.Remove(item.Id);
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Categories | LibraryChangeKind.Items);
        return Task.CompletedTask;
    }

    // ================================================================== items

    public Task<IReadOnlyList<LibraryItem>> GetThreadItemsAsync(long categoryId, string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (words, tags) = Tokenize(searchText);
            var items = ItemsIn(Subtree(categoryId)).Where(i => Matches(i, words, tags, filter)).OrderBy(i => i.AddedAt).ToList();
            return Task.FromResult<IReadOnlyList<LibraryItem>>(items);
        }
    }

    public Task<LibraryItem?> GetItemAsync(long itemId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault(itemId));
        }
    }

    public async Task<SearchResults> SearchAsync(string searchText, ItemFilter? filter = null, CancellationToken ct = default)
    {
        await Task.Delay(30, ct);
        lock (_gate)
        {
            var (words, tags) = Tokenize(searchText);
            var categories = words.Count == 0
                ? []
                : _categories.Values
                    .Where(c => c.Kind != CategoryKind.System && words.All(w => c.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(Summarize)
                    .ToList();
            var items = _items.Values
                .Where(i => Matches(i, words, tags, filter is { IsEmpty: false } ? filter : null))
                .OrderByDescending(i => i.AddedAt)
                .ToList();
            return new SearchResults(categories, items);
        }
    }

    public async Task<IReadOnlyList<LibraryItem>> SearchNamePrefixAsync(string searchText, int limit, ItemFilter? filter = null, CancellationToken ct = default)
    {
        var phrase = string.Join(' ', Tokenize(searchText).Words);
        if (phrase.Length == 0 || limit <= 0)
        {
            return [];
        }

        var results = await SearchAsync(searchText, filter, ct); // newest first; OrderBy below is stable
        bool Exact(string name) => string.Equals(name, phrase, StringComparison.CurrentCultureIgnoreCase)
                                   || string.Equals(Path.GetFileNameWithoutExtension(name), phrase, StringComparison.CurrentCultureIgnoreCase);
        return results.Items
            .Where(i => i.FileName.StartsWith(phrase, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(i => Exact(i.FileName) ? 0 : 1)
            .Take(limit)
            .ToList();
    }

    public async Task<AddResult> AddPathsAsync(IReadOnlyList<string> paths, AddRequest request, IProgress<AddProgress>? progress = null, CancellationToken ct = default)
    {
        // Files inside added folders are not sorted by type (the real library makes those folders categories; this fake
        // puts their files straight into the target).
        var files = await Task.Run(
            () => paths.SelectMany(p => Directory.Exists(p)
                    ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Take(500).Select(f => (Path: f, InFolder: true))
                    : File.Exists(p) ? [(Path: p, InFolder: false)] : Enumerable.Empty<(string Path, bool InFolder)>())
                .ToList(),
            ct);
        var added = new List<LibraryItem>();
        var duplicates = new List<DuplicateInfo>();
        var failures = new List<AddFailure>();
        var sortedByType = new List<SortedFile>();
        var batch = Guid.NewGuid().ToString("N");
        string? sortingKey = null;
        if (request.SortIntoTypeCategories && request.TargetCategoryId is long targetId)
        {
            lock (_gate)
            {
                sortingKey = _categories.TryGetValue(targetId, out var targetCategory) ? TypeSorting.SortingKey(targetCategory) : null;
            }
        }

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (file, inFolder) = files[i];
            progress?.Report(new AddProgress(i, files.Count, file, i, files.Count));
            await Task.Delay(60, ct);
            lock (_gate)
            {
                var extension = Path.GetExtension(file);
                var target = request.TargetCategoryId ?? CategoryForExtension(extension);
                if (!inFolder && TypeSorting.Destination(sortingKey, extension) is { } typeKey
                    && _categories.Values.FirstOrDefault(c => c.BuiltInKey == typeKey) is { } typeCategory)
                {
                    target = typeCategory.Id;
                    sortedByType.Add(new SortedFile(file, target));
                }

                var existing = _items.Values.FirstOrDefault(x => string.Equals(x.FullPath, file, StringComparison.OrdinalIgnoreCase) ||
                                                                 string.Equals(x.OriginalPath, file, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    duplicates.Add(new DuplicateInfo(file, existing, false));
                    continue;
                }

                var item = Add(target, Path.GetFileName(file), file, DateTimeOffset.Now, mode: request.Mode ?? _settings.DefaultAddMode, addedBy: request.AddedBy) with { BatchId = batch, OriginalPath = file };
                _items[item.Id] = item;
                added.Add(item);
            }
        }

        progress?.Report(new AddProgress(files.Count, files.Count, null, files.Count, files.Count));
        lock (_gate)
        {
            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, added.Select(a => a.HomeCategoryId).Distinct(), added.Select(a => a.Id), request.AddedBy);
        return new AddResult { Added = added, Duplicates = duplicates, Failures = failures, SortedByType = sortedByType };
    }

    public Task<LibraryItem> CreateNoteAsync(long categoryId, string text, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default)
    {
        LibraryItem item;
        lock (_gate)
        {
            item = AddNote(categoryId, text, DateTimeOffset.Now);
            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, [categoryId], [item.Id]);
        return Task.FromResult(item);
    }

    public Task<AddResult> AddPastedTextAsync(long categoryId, string text, CancellationToken ct = default)
    {
        LibraryItem item;
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (FileIntake.IsSingleUrl(text))
            {
                var name = $"Link {now:yyyy-MM-dd HH-mm-ss}.url";
                item = Add(categoryId, name, Path.Combine(_vaultPath ?? "", name), now, kind: ItemKind.Link, size: text.Length, addedBy: AddedBy.Paste);
            }
            else
            {
                item = AddNote(categoryId, text, now) with { AddedBy = AddedBy.Paste };
                _items[item.Id] = item;
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, [categoryId], [item.Id], AddedBy.Paste);
        return Task.FromResult(new AddResult { Added = [item] });
    }

    public Task<AddResult> AddPastedImageAsync(long categoryId, byte[] png, CancellationToken ct = default)
    {
        LibraryItem item;
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var name = $"Pasted image {now:yyyy-MM-dd HH-mm-ss}.png";
            item = Add(categoryId, name, Path.Combine(_vaultPath ?? "", name), now, kind: ItemKind.PastedImage, size: png.Length, addedBy: AddedBy.Paste);
            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, [categoryId], [item.Id], AddedBy.Paste);
        return Task.FromResult(new AddResult { Added = [item] });
    }

    public Task RenameItemAsync(long itemId, string newFileName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var item = _items[itemId];
            _items[itemId] = item with { FileName = newFileName, Extension = ExtensionMap.Normalize(Path.GetExtension(newFileName)) };
        }

        Raise(LibraryChangeKind.Items, itemIds: [itemId]);
        return Task.CompletedTask;
    }

    public Task DeleteItemsAsync(IReadOnlyList<long> itemIds, DeleteItemsOptions? options = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var id in itemIds)
            {
                _items.Remove(id);
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, itemIds: itemIds);
        return Task.CompletedTask;
    }

    public Task RemoveFromCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var subtree = Subtree(categoryId);
            var deletedId = _categories.Values.First(c => c.BuiltInKey == ExtensionMap.DeletedKey).Id;
            foreach (var id in itemIds)
            {
                if (!_items.TryGetValue(id, out var item))
                {
                    continue;
                }

                var remaining = item.CategoryIds.Where(c => !subtree.Contains(c)).ToList();
                _items[id] = remaining.Count > 0
                    ? item with { CategoryIds = remaining, HomeCategoryId = remaining.Contains(item.HomeCategoryId) ? item.HomeCategoryId : remaining[0] }
                    : item with { CategoryIds = [deletedId], HomeCategoryId = deletedId };
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, [categoryId], itemIds);
        return Task.CompletedTask;
    }

    public Task AddToCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var id in itemIds)
            {
                if (_items.TryGetValue(id, out var item) && !item.CategoryIds.Contains(categoryId))
                {
                    _items[id] = item with { CategoryIds = [.. item.CategoryIds, categoryId] };
                }
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, [categoryId], itemIds);
        return Task.CompletedTask;
    }

    public Task MoveToCategoryAsync(IReadOnlyList<long> itemIds, long fromCategoryId, long toCategoryId, AddedBy addedBy = AddedBy.AppUi,
        CancellationToken ct = default)
    {
        var changed = new HashSet<long> { toCategoryId };
        lock (_gate)
        {
            if (!_categories.ContainsKey(fromCategoryId) || !_categories.TryGetValue(toCategoryId, out var target))
            {
                throw new LibraryException(LibraryErrorCode.NotFound, "The category no longer exists.");
            }

            if (toCategoryId == fromCategoryId)
            {
                return Task.CompletedTask;
            }

            if (target.Kind == CategoryKind.System)
            {
                throw new LibraryException(LibraryErrorCode.NotAllowed, $"Files can't be added to “{target.Name}”.");
            }

            // Like the real library: leave the source thread (the category and its subcategories), join the target, keep the
            // other categories; the home follows when it is left. Nothing moves on disk in this fake.
            var leaving = Subtree(fromCategoryId);
            var deletedId = _categories.Values.First(c => c.BuiltInKey == ExtensionMap.DeletedKey).Id;
            foreach (var id in itemIds)
            {
                if (!_items.TryGetValue(id, out var item))
                {
                    continue;
                }

                var remaining = item.CategoryIds.Where(c => (!leaving.Contains(c) || c == toCategoryId) && c != deletedId).ToList();
                if (!remaining.Contains(toCategoryId))
                {
                    remaining.Add(toCategoryId);
                }

                changed.UnionWith(item.CategoryIds);
                _items[id] = item with
                {
                    CategoryIds = remaining,
                    HomeCategoryId = remaining.Contains(item.HomeCategoryId) ? item.HomeCategoryId : toCategoryId,
                };
            }

            RecomputeLastUploads();
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Categories, changed, itemIds, addedBy);
        return Task.CompletedTask;
    }

    public async Task<bool> VerifyExistsAsync(long itemId, CancellationToken ct = default)
    {
        LibraryItem? item;
        lock (_gate)
        {
            item = _items.GetValueOrDefault(itemId);
        }

        return item is not null && await Task.Run(() => File.Exists(item.FullPath), ct);
    }

    public Task<RelocateOutcome> RelocateItemAsync(long itemId, string newPath, bool acceptHashMismatch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item))
            {
                return Task.FromResult(RelocateOutcome.NotFound);
            }

            _items[itemId] = item with { FullPath = newPath, IsMissing = false };
        }

        Raise(LibraryChangeKind.Items, itemIds: [itemId]);
        return Task.FromResult(RelocateOutcome.Relocated);
    }

    public Task<string> ReadNoteTextAsync(long itemId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_noteTexts.GetValueOrDefault(itemId) ?? _items.GetValueOrDefault(itemId)?.NotePreview ?? "");
        }
    }

    // ================================================================== starred files

    public Task SetStarredAsync(IReadOnlyList<long> itemIds, bool starred, CancellationToken ct = default)
    {
        var changed = new List<long>();
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            foreach (var id in itemIds.Distinct())
            {
                // Like the real library: an existing star keeps its time.
                if (_items.TryGetValue(id, out var item) && item.IsStarred != starred)
                {
                    _items[id] = item with { StarredAt = starred ? now : null };
                    changed.Add(id);
                }
            }
        }

        if (changed.Count > 0)
        {
            Raise(LibraryChangeKind.Items | LibraryChangeKind.Starred, itemIds: changed);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LibraryItem>> GetStarredItemsAsync(string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (words, tags) = Tokenize(searchText);
            var items = _items.Values
                .Where(i => i.IsStarred && Matches(i, words, tags, filter))
                .OrderBy(i => i.StarredAt)
                .ThenBy(i => i.AddedAt)
                .ThenBy(i => i.Id)
                .ToList();
            return Task.FromResult<IReadOnlyList<LibraryItem>>(items);
        }
    }

    public Task<StarredSummary> GetStarredSummaryAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var starred = _items.Values.Where(i => i.IsStarred).ToList();
            return Task.FromResult(new StarredSummary(starred.Count, starred.Max(i => i.StarredAt)));
        }
    }

    // ================================================================== AI activity log

    public Task<ActivityEntry> AddActivityAsync(ActivityEntry entry, CancellationToken ct = default) => Task.FromResult(_activity.Add(entry));

    public Task<ActivityPage> GetActivityAsync(ActivityQuery query, CancellationToken ct = default) => Task.FromResult(_activity.Get(query));

    // ================================================================== tags

    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var tags = _items.Values
                .SelectMany(i => i.Tags)
                .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new TagInfo(g.Key, g.Count()))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<TagInfo>>(tags);
        }
    }

    public Task SetItemTagsAsync(long itemId, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _items[itemId] = _items[itemId] with { Tags = Normalize(tags) };
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Tags, itemIds: [itemId]);
        return Task.CompletedTask;
    }

    public Task AddTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var id in itemIds)
            {
                _items[id] = _items[id] with { Tags = Normalize([.. _items[id].Tags, .. tags]) };
            }
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Tags, itemIds: itemIds);
        return Task.CompletedTask;
    }

    public Task RemoveTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var remove = Normalize(tags);
            foreach (var id in itemIds)
            {
                _items[id] = _items[id] with { Tags = _items[id].Tags.Where(t => !remove.Contains(t, StringComparer.CurrentCultureIgnoreCase)).ToList() };
            }
        }

        Raise(LibraryChangeKind.Items | LibraryChangeKind.Tags, itemIds: itemIds);
        return Task.CompletedTask;
    }

    private static List<string> Normalize(IEnumerable<string> tags) => tags
        .Select(t => t.Trim().TrimStart('#'))
        .Where(t => t.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Stage 2 vault operations are not simulated by this fake.
    public Cabinette.Core.Services.IVaultMonitor CreateVaultMonitor() => throw new NotSupportedException("Not available with sample data.");

    public Task<Cabinette.Core.Models.MoveVaultResult> MoveVaultAsync(string newVaultPath, IProgress<Cabinette.Core.Models.VaultOperationProgress>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not available with sample data.");

    public Task<Cabinette.Core.Models.MigrateResult> MigratePointersAsync(IProgress<Cabinette.Core.Models.VaultOperationProgress>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not available with sample data.");

    public Task<Cabinette.Core.Models.BackupResult> CreateBackupAsync(string zipPath, IProgress<Cabinette.Core.Models.VaultOperationProgress>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not available with sample data.");
}