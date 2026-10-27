using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Services;

/// <summary>
/// Category icons for the UI: decodes PNG bytes off the UI thread (cached), generates default icons through
/// <see cref="ICategoryIconGenerator"/> for categories that have none, and scales user-chosen PNGs to 256×256.
/// When the generator is unavailable the UI falls back to an icon drawn in XAML.
/// </summary>
public sealed class CategoryIconService
{
    /// <summary>Decoded size: crisp at 48 px on 200% displays.</summary>
    private const int DecodeSizePx = 96;
    private const int IconSizePx = 256;

    /// <summary>Above this many categories to check, one query for all of them is cheaper than one each.</summary>
    private const int MaxSingleLookups = 16;

    private readonly ILibraryService _library;
    private readonly ICategoryIconGenerator _generator;
    private readonly Dictionary<(int Hash, int Length), ImageSource> _cache = [];
    private readonly Lock _cacheGate = new();
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    /// <summary>Categories known to have an icon, so later passes don't load them again.</summary>
    private readonly ConcurrentDictionary<long, byte> _withIcon = new();
    private volatile bool _generatorUnavailable;

    public CategoryIconService(ILibraryService library, ICategoryIconGenerator generator)
    {
        _library = library;
        _generator = generator;
    }

    /// <summary>Returns the decoded icon if it is already cached.</summary>
    public ImageSource? TryGetCached(byte[]? png)
    {
        if (png is null || png.Length == 0)
        {
            return null;
        }

        lock (_cacheGate)
        {
            return _cache.TryGetValue(Key(png), out var image) ? image : null;
        }
    }

    /// <summary>Decodes on a background thread; returns a frozen image or null when the bytes are not a valid image.</summary>
    public async Task<ImageSource?> GetAsync(byte[]? png)
    {
        if (png is null || png.Length == 0)
        {
            return null;
        }

        var cached = TryGetCached(png);
        if (cached is not null)
        {
            return cached;
        }

        var image = await Task.Run(() => Decode(png, DecodeSizePx));
        if (image is not null)
        {
            lock (_cacheGate)
            {
                _cache[Key(png)] = image;
            }
        }

        return image;
    }

    /// <summary>Generates a default icon, or null when the generator fails (the XAML fallback is shown instead).</summary>
    public byte[]? TryGenerate(string? iconKey, string name)
    {
        if (_generatorUnavailable)
        {
            return null;
        }

        try
        {
            return _generator.Generate(iconKey, name);
        }
        catch (NotImplementedException)
        {
            _generatorUnavailable = true;
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Icon generation failed for '{name}'.", ex);
            return null;
        }
    }

    /// <summary>These categories were seen without an icon: the next pass looks at them again even if they had one before.</summary>
    public void Forget(IEnumerable<long> categoryIds)
    {
        foreach (var id in categoryIds)
        {
            _withIcon.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Background pass: gives categories without an icon their generated default. Never throws.
    /// <paramref name="categoryIds"/> null (startup) looks at every category once. Otherwise only the given categories that
    /// are not already known to have an icon are looked at, one by one, so a library change (an added file, a renamed
    /// category) does not load every category's icon again.
    /// </summary>
    public async Task EnsureDefaultIconsAsync(IReadOnlyCollection<long>? categoryIds = null, CancellationToken ct = default)
    {
        if (_generatorUnavailable)
        {
            return;
        }

        // One pass at a time; a pass that has to wait still runs, so no category is missed (later passes are cheap:
        // categories already known to have an icon are skipped without a query).
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var missing = await FindMissingAsync(categoryIds, ct).ConfigureAwait(false);
            if (missing.Count == 0)
            {
                return;
            }

            // Render all icons on one STA worker thread, then save them.
            var generated = await StaWorker.RunAsync(() =>
            {
                var result = new List<(Category Category, byte[] Png)>();
                foreach (var category in missing)
                {
                    if (ct.IsCancellationRequested || _generatorUnavailable)
                    {
                        break;
                    }

                    if (TryGenerate(category.IconKey ?? ExtensionMap.FolderIconKey, category.Name) is { } png)
                    {
                        result.Add((category, png));
                    }
                }

                return result;
            }).ConfigureAwait(false);

            if (_generatorUnavailable)
            {
                AppLog.Info("Category icon generator is not available; using the built-in fallback icons.");
            }

            foreach (var (category, png) in generated)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _library.SetCategoryIconAsync(category.Id, png, ct).ConfigureAwait(false);
                    _withIcon[category.Id] = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Warn($"Saving the generated icon of '{category.Name}' failed.", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("Generating default category icons failed.", ex);
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    /// <summary>The categories (of <paramref name="categoryIds"/>, or all) that have no icon yet.</summary>
    private async Task<List<Category>> FindMissingAsync(IReadOnlyCollection<long>? categoryIds, CancellationToken ct)
    {
        var unknown = categoryIds?.Where(id => !_withIcon.ContainsKey(id)).Distinct().ToList();
        if (unknown is { Count: 0 })
        {
            return [];
        }

        IReadOnlyList<Category> candidates;
        if (unknown is null || unknown.Count > MaxSingleLookups)
        {
            // Startup, or many new categories at once (a folder was added): one query beats many.
            candidates = await _library.GetAllCategoriesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var found = new List<Category>(unknown.Count);
            foreach (var id in unknown)
            {
                if (await _library.GetCategoryAsync(id, ct).ConfigureAwait(false) is { } category)
                {
                    found.Add(category);
                }
            }

            candidates = found;
        }

        foreach (var category in candidates.Where(c => c.IconPng is not null))
        {
            _withIcon[category.Id] = 0;
        }

        return candidates.Where(c => c.IconPng is null).ToList();
    }

    /// <summary>Resets a category to its default icon: clears the custom one and regenerates from <see cref="Category.IconKey"/>.</summary>
    public async Task ResetIconAsync(Category category)
    {
        var png = await GenerateAsync(category.IconKey, category.Name);
        await _library.SetCategoryIconAsync(category.Id, png);
    }

    /// <summary>Generates a default icon off the UI thread (on an STA worker), or null when generation is unavailable.</summary>
    public Task<byte[]?> GenerateAsync(string? iconKey, string name)
        => _generatorUnavailable
            ? Task.FromResult<byte[]?>(null)
            : StaWorker.RunAsync(() => TryGenerate(iconKey ?? ExtensionMap.FolderIconKey, name));

    /// <summary>Loads any image file, center-crops it to a square and scales it to 256×256 PNG bytes. Runs off the UI thread.</summary>
    public static Task<byte[]> LoadAndScaleIconAsync(string path) => Task.Run(() => ScaleToSquarePng(File.ReadAllBytes(path), IconSizePx));

    public static byte[] ScaleToSquarePng(byte[] imageBytes, int sizePx)
    {
        using var stream = new MemoryStream(imageBytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        if (source.PixelWidth != source.PixelHeight)
        {
            var x = (source.PixelWidth - side) / 2;
            var y = (source.PixelHeight - side) / 2;
            source = new CroppedBitmap(source, new Int32Rect(x, y, side, side));
        }

        if (side != sizePx)
        {
            var scale = (double)sizePx / side;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);

        // The user's own picture: the banner must never take it for a generated avatar.
        return UserPictureMark.Add(output.ToArray());
    }

    public static ImageSource? Decode(byte[] png, int decodeSizePx)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeSizePx;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Warn("A category icon could not be decoded.", ex);
            return null;
        }
    }

    private static (int, int) Key(byte[] png)
    {
        var hash = new HashCode();
        hash.AddBytes(png);
        return (hash.ToHashCode(), png.Length);
    }
}
