// Dev tool: fills a throwaway Cabinette library through the real Core + Windows code paths,
// so the real app can be checked end to end with: Cabinette.exe --data-dir <dataDir>.
// Usage: Cabinette.DevSeed <dataDir> <vaultDir>
//        Cabinette.DevSeed <dataDir> <vaultDir> --import <folder>   (copies the folder's files, auto-categorized, then the folder itself)
//        Cabinette.DevSeed --datetaken <files>                      (the date taken of photos and videos, and the time per read)
using System.Diagnostics;
using System.IO;
using System.Text;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

if (args.Length == 5 && args[0] == "--render-logo-pair")
{
    // Diagnostic: master SVG at 128-32 px plus the 16 px variant at 24/16, with a caption.
    LogoPreview.RenderPair(args[1], args[2], args[3], args[4]);
    return 0;
}

if (args.Length == 3 && args[0] == "--render-logo")
{
    // Diagnostic: renders a simple-subset SVG (rect/circle/path with fill) at several sizes on light and dark backgrounds.
    LogoPreview.Render(args[1], args[2]);
    return 0;
}

if (args.Length >= 2 && args[0] == "--mediainfo")
{
    // Diagnostic: what the Windows layer reports for files (duration, size, page count).
    var service = new ShellMediaInfoService();
    foreach (var file in args.Skip(1))
    {
        Console.WriteLine($"{Path.GetFileName(file)}: {service.GetMediaInfo(file)?.ToString() ?? "null"}");
    }

    return 0;
}

if (args.Length >= 2 && args[0] == "--datetaken")
{
    // Diagnostic: the date taken the Windows layer reads (UTC and this PC's local time) and how long each read took.
    var reader = new ShellDateTakenReader();
    foreach (var file in args.Skip(1))
    {
        var sw = Stopwatch.StartNew();
        var taken = reader.ReadDateTaken(file);
        var elapsed = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"{Path.GetFileName(file)}: {(taken is { } t ? $"{t:yyyy-MM-dd HH:mm:ss}Z (local {t.ToLocalTime():yyyy-MM-dd HH:mm:ss})" : "none")} ({elapsed:0.0} ms)");
    }

    return 0;
}

if (args.Length == 3 && args[1] == "--thumbs")
{
    // Diagnostic: thumbnails for one category, twice, with fresh service instances (second pass hits the disk cache).
    await using var lib = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.GetFullPath(args[0]), DateTakenReader = new ShellDateTakenReader() }, new ShellRecycleBin());
    var category = (await lib.GetAllCategoriesAsync()).First(c => string.Equals(c.Name, args[2], StringComparison.OrdinalIgnoreCase));
    var items = await lib.GetThreadItemsAsync(category.Id);
    for (var pass = 1; pass <= 2; pass++)
    {
        var service = new ShellThumbnailService(Path.Combine(Path.GetFullPath(args[0]), "thumbs"));
        foreach (var item in items)
        {
            var sw = Stopwatch.StartNew();
            var bitmap = await service.GetThumbnailAsync(item.FullPath, 660);
            Console.WriteLine($"pass {pass} {item.FileName}: {(bitmap is null ? "null" : $"{bitmap.PixelWidth}x{bitmap.PixelHeight} {bitmap.Format} dpi={bitmap.DpiX:0.##}x{bitmap.DpiY:0.##} dip={bitmap.Width:0.#}x{bitmap.Height:0.#} type={bitmap.GetType().Name}")} ({sw.ElapsedMilliseconds} ms) exists={File.Exists(item.FullPath)}");
        }
    }

    return 0;
}

if (args.Length == 4 && args[2] == "--import")
{
    return await Importer.RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Cabinette.DevSeed <dataDir> <vaultDir> [--import <folder>]");
    return 2;
}

var dataDir = Path.GetFullPath(args[0]);
var vaultDir = Path.GetFullPath(args[1]);
var scratch = Path.Combine(dataDir, "seed-sources");
Directory.CreateDirectory(scratch);

await using var library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = dataDir, DateTakenReader = new ShellDateTakenReader() }, new ShellRecycleBin());
if (library.VaultPath is null)
{
    await library.InitializeVaultAsync(vaultDir);
}

void Report(string step, AddResult result) =>
    Console.WriteLine($"{step}: added {result.Added.Count}, duplicates {result.Duplicates.Count}, failures {result.Failures.Count}"
        + string.Concat(result.Failures.Select(f => $"\n  failed {f.SourcePath}: {f.Message}")));

// Images (copy, auto-categorized) and one exact duplicate.
var wallpapers = Directory.GetFiles(@"C:\Windows\Web\Wallpaper", "*.jpg", SearchOption.AllDirectories).Take(6).ToList();
Report("wallpapers (copy)", await library.AddPathsAsync(wallpapers, new AddRequest { Mode = ItemMode.Copy }));
Report("duplicate wallpaper", await library.AddPathsAsync([wallpapers[0]], new AddRequest { Mode = ItemMode.Copy }));

// Audio as pointers (stay in C:\Windows\Media).
var sounds = Directory.GetFiles(@"C:\Windows\Media", "*.wav").Take(3).ToList();
Report("sounds (pointer)", await library.AddPathsAsync(sounds, new AddRequest { Mode = ItemMode.Pointer }));

// Documents: generated PDFs and a code file.
var pdf = Path.Combine(scratch, "CSE211 Week 03.pdf");
File.WriteAllBytes(pdf, MinimalPdf.Build(["CSE211 Data Structures", "Week 3: ArrayList", "Exceptions", "Generics"]));
var code = Path.Combine(scratch, "hello.py");
File.WriteAllText(code, "print('hello from Cabinette')\n");
Report("pdf + code (copy)", await library.AddPathsAsync([pdf, code], new AddRequest { Mode = ItemMode.Copy }));

// A pointer whose file then disappears (shows as missing when opened).
var vanishing = Path.Combine(scratch, "Moved later.txt");
File.WriteAllText(vanishing, "This file will be deleted to simulate a moved pointer.");
Report("vanishing (pointer)", await library.AddPathsAsync([vanishing], new AddRequest { Mode = ItemMode.Pointer }));
File.Delete(vanishing);

// Notes, pasted link, user categories with a subcategory, tags.
var notes = await library.GetOrCreateBuiltInCategoryAsync("notes");
await library.CreateNoteAsync(notes.Id, "Shopping list\n- milk\n- bread\n- coffee beans");
Report("pasted url", await library.AddPastedTextAsync(notes.Id, "https://github.com"));
var work = await library.CreateCategoryAsync("Work", null);
var invoices = await library.CreateCategoryAsync("Invoices", work.Id);
var invoice = Path.Combine(scratch, "Invoice 2026-09.pdf");
File.WriteAllBytes(invoice, MinimalPdf.Build(["Invoice 2026-09", "Total: 120.00 EUR"]));
var added = await library.AddPathsAsync([invoice], new AddRequest { TargetCategoryId = invoices.Id, Mode = ItemMode.Copy, AddedBy = AddedBy.Explorer });
Report("invoice into Work > Invoices (as if from Explorer)", added);
await library.AddTagsAsync(added.Added.Select(i => i.Id).ToList(), ["invoice", "2026"]);

var images = await library.GetOrCreateBuiltInCategoryAsync("images");
var imageItems = await library.GetThreadItemsAsync(images.Id);
await library.AddTagsAsync([imageItems[0].Id], ["wallpaper"]);

// Search checks.
var bySubstring = await library.SearchAsync("voi");
var byTag = await library.SearchAsync("#invoice #2026");
Console.WriteLine($"search 'voi': {bySubstring.Categories.Count} categories, {bySubstring.Items.Count} files");
Console.WriteLine($"search '#invoice #2026': {byTag.Items.Count} files");

// Media info (duration and page count) through the Windows layer.
var media = new ShellMediaInfoService();
Console.WriteLine($"media info {Path.GetFileName(sounds[0])}: {media.GetMediaInfo(sounds[0])}");
var vaultPdf = (await library.SearchAsync("CSE211")).Items.FirstOrDefault();
Console.WriteLine($"media info {vaultPdf?.FileName}: {(vaultPdf is null ? "-" : media.GetMediaInfo(vaultPdf.FullPath))}");

var top = await library.GetCategoriesAsync(null, CategorySort.LastUpload);
Console.WriteLine("chat list: " + string.Join(", ", top.Take(8).Select(s => $"{s.Category.Name}({s.ItemCount}{(s.UnreadCount > 0 ? $", {s.UnreadCount} unread" : "")})")));
Console.WriteLine($"vault: {library.VaultPath}");
return 0;

/// <summary>Imports a real folder twice: loose files (auto-categorized), then the folder (becomes a category; all duplicates).</summary>
internal static class Importer
{
    public static async Task<int> RunAsync(string dataDir, string vaultDir, string folder)
    {
        await using var library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = dataDir, DateTakenReader = new ShellDateTakenReader() }, new ShellRecycleBin());
        if (library.VaultPath is null)
        {
            await library.InitializeVaultAsync(vaultDir);
        }

        var files = Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var watch = Stopwatch.StartNew();
        var loose = await library.AddPathsAsync(files, new AddRequest { Mode = ItemMode.Copy });
        Console.WriteLine($"loose files: {files.Count} in, added {loose.Added.Count}, duplicates {loose.Duplicates.Count}, failures {loose.Failures.Count} ({watch.ElapsedMilliseconds} ms)");
        foreach (var failure in loose.Failures)
        {
            Console.WriteLine($"  FAILED {Path.GetFileName(failure.SourcePath)}: {failure.Message}");
        }

        foreach (var duplicate in loose.Duplicates)
        {
            Console.WriteLine($"  duplicate {Path.GetFileName(duplicate.SourcePath)} = {duplicate.Existing.FileName}");
        }

        var categories = (await library.GetAllCategoriesAsync()).ToDictionary(c => c.Id);
        string PathOf(long id)
        {
            var parts = new List<string>();
            for (long? at = id; at is { } current && categories.TryGetValue(current, out var c); at = c.ParentId)
            {
                parts.Insert(0, c.Name);
            }

            return string.Join(" > ", parts);
        }

        Console.WriteLine("placement (file -> category):");
        foreach (var item in loose.Added.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {item.FileName,-34} {PathOf(item.HomeCategoryId),-22} type={item.TypeKey}");
        }

        watch.Restart();
        var whole = await library.AddPathsAsync([folder], new AddRequest { Mode = ItemMode.Copy });
        Console.WriteLine($"folder: added {whole.Added.Count}, duplicates {whole.Duplicates.Count} (added to folder category: {whole.Duplicates.Count(d => d.AddedToTargetCategory)}), failures {whole.Failures.Count}, new categories {whole.CreatedCategoryIds.Count} ({watch.ElapsedMilliseconds} ms)");
        foreach (var id in whole.CreatedCategoryIds)
        {
            Console.WriteLine($"  new category: {PathOf(id)}");
        }

        var top = await library.GetCategoriesAsync(null, CategorySort.LastUpload);
        Console.WriteLine("chat list: " + string.Join(", ", top.Select(s => $"{s.Category.Name}({s.ItemCount})")));
        Console.WriteLine($"unmapped extensions: {string.Join(" ", files.Select(f => ExtensionMap.Normalize(Path.GetExtension(f))).Where(e => ExtensionMap.GetCategoryKey(e) == ExtensionMap.OtherKey).Distinct())}");
        return 0;
    }
}

/// <summary>Writes a tiny valid PDF with one line of text per page.</summary>
internal static class MinimalPdf
{
    public static byte[] Build(IReadOnlyList<string> pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var kids = new List<int>();
        foreach (var text in pages)
        {
            var escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            var stream = $"BT /F1 28 Tf 60 720 Td ({escaped}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
            var contentId = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentId} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            kids.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
