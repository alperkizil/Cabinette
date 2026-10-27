using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.ThreadHarness;

/// <summary>Decodes image files with <see cref="BitmapImage"/> so thumbnails are visible without the real shell service.</summary>
internal sealed class FakeThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> Decodable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".doc", ".pptx", ".xlsx" };

    public Task<BitmapSource?> GetThumbnailAsync(string path, int sizePx, CancellationToken ct = default)
        => Documents.Contains(Path.GetExtension(path)) ? RenderPageAsync(path, sizePx) : DecodeAsync(path, sizePx, ct);

    public Task<BitmapSource?> GetFileIconAsync(string pathOrExtension, int sizePx, CancellationToken ct = default)
        => Task.FromResult<BitmapSource?>(null);

    private static Task<BitmapSource?> DecodeAsync(string path, int sizePx, CancellationToken ct) => Task.Run(
        () =>
        {
            if (!Decodable.Contains(Path.GetExtension(path)) || !File.Exists(path))
            {
                return null;
            }

            Thread.Sleep(20); // a little shell latency
            ct.ThrowIfCancellationRequested();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = sizePx;
            image.EndInit();
            image.Freeze();
            return (BitmapSource?)image;
        },
        ct);

    /// <summary>Simulates the shell's first-page thumbnail of PDF/Office files (drawn on an STA thread, frozen).</summary>
    private static Task<BitmapSource?> RenderPageAsync(string path, int sizePx)
    {
        var completion = new TaskCompletionSource<BitmapSource?>();
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(DrawPage(Path.GetFileNameWithoutExtension(path), Path.GetExtension(path).ToLowerInvariant(), sizePx));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static BitmapSource DrawPage(string title, string ext, int sizePx)
    {
        var slide = ext == ".pptx";
        var sheet = ext == ".xlsx";
        double width = slide || sheet ? sizePx : Math.Round(sizePx * 0.773);
        double height = slide ? Math.Round(sizePx * 0.5625) : sheet ? Math.Round(sizePx * 0.75) : sizePx;
        var ink = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35));
        var grey = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));
        var accent = new SolidColorBrush(ext switch
        {
            ".pdf" => Color.FromRgb(0xC6, 0x3B, 0x2F),
            ".pptx" => Color.FromRgb(0xC5, 0x5A, 0x11),
            ".xlsx" => Color.FromRgb(0x1D, 0x6F, 0x42),
            _ => Color.FromRgb(0x2B, 0x57, 0x9A),
        });
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var u = width / 100;
            if (slide)
            {
                dc.DrawRectangle(accent, null, new Rect(0, 0, width, height));
                dc.DrawText(Text(title, 7 * u, Brushes.White, FontWeights.SemiBold, width - (16 * u)), new Point(8 * u, height * 0.38));
                dc.DrawRectangle(Brushes.White, null, new Rect(8 * u, height * 0.62, 30 * u, 1.2 * u));
            }
            else if (sheet)
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(accent, null, new Rect(0, 0, width, 9 * u));
                for (var row = 1; row < 14; row++)
                {
                    dc.DrawRectangle(grey, null, new Rect(0, (9 * u) + (row * 5 * u), width, 0.3 * u));
                }

                for (var col = 1; col < 6; col++)
                {
                    dc.DrawRectangle(grey, null, new Rect(col * 18 * u, 9 * u, 0.3 * u, height));
                }

                dc.DrawText(Text(title, 4 * u, Brushes.White, FontWeights.SemiBold, width), new Point(3 * u, 2 * u));
            }
            else
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(accent, null, new Rect(0, 0, width, 3 * u));
                var heading = Text(title, 7.5 * u, ink, FontWeights.Bold, width - (20 * u));
                dc.DrawText(heading, new Point(10 * u, 12 * u));
                var y = (16 * u) + heading.Height;
                dc.DrawRectangle(accent, null, new Rect(10 * u, y, 22 * u, 1 * u));
                y += 7 * u;
                for (var line = 0; line < 22 && y < height - (8 * u); line++)
                {
                    var length = line % 6 == 5 ? 45 : 80 - (line * 7 % 13);
                    dc.DrawRectangle(grey, null, new Rect(10 * u, y, length * u, 1.6 * u));
                    y += line % 6 == 5 ? 8 * u : 4.2 * u;
                }
            }
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var copy = new WriteableBitmap(bitmap);
        copy.Freeze();
        return copy;
    }

    private static FormattedText Text(string text, double size, Brush brush, FontWeight weight, double maxWidth)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };
}

/// <summary>Durations for .wav files (from the RIFF header) plus per-path overrides set by the sample data.</summary>
internal sealed class FakeMediaInfoService : IMediaInfoService
{
    public ConcurrentDictionary<string, MediaInfo> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MediaInfo? GetMediaInfo(string path)
    {
        if (Overrides.TryGetValue(path, out var info))
        {
            return info;
        }

        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return ReadWav(path);
        }

        // Page counts as System.Document.PageCount would report them.
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" or ".docx" or ".doc" or ".pptx" => new MediaInfo(null, null, null, PageCount: 1 + (Path.GetFileName(path).Sum(c => c) % 64)),
            _ => null,
        };
    }

    private static MediaInfo? ReadWav(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            reader.ReadBytes(12); // RIFF, size, WAVE
            var byteRate = 0;
            while (reader.BaseStream.Position < reader.BaseStream.Length - 8)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadInt32();
                if (id == "fmt ")
                {
                    reader.ReadBytes(8);
                    byteRate = reader.ReadInt32();
                    reader.ReadBytes(size - 12);
                }
                else if (id == "data")
                {
                    return byteRate > 0 ? new MediaInfo(TimeSpan.FromSeconds((double)size / byteRate), null, null) : null;
                }
                else
                {
                    reader.ReadBytes(size);
                }
            }
        }
        catch (Exception)
        {
            // Not a readable wav.
        }

        return null;
    }
}

internal sealed class FakeContextMenuService : IShellContextMenuService
{
    public ContextMenuResult Show(IntPtr ownerHwnd, IReadOnlyList<string> paths, Point screenPointPx, IReadOnlyList<CustomMenuItem> customItems)
    {
        Console.WriteLine($"[menu] at {screenPointPx} for {paths.Count} file(s): {string.Join(", ", customItems.Where(i => !i.IsSeparator).Select(i => i.Text))}");
        return ContextMenuResult.Dismissed;
    }
}

internal sealed class FakeShellOperations : IShellOperations
{
    public void OpenWithDefaultApp(string path) => Console.WriteLine($"[shell] open {path}");

    public void ShowInExplorer(string path) => Console.WriteLine($"[shell] show {path}");
}

internal sealed class FakeFileIntake(ILibraryService library) : IFileIntake
{
    public async Task AddPathsAsync(IReadOnlyList<string> paths, long? targetCategoryId, AddedBy addedBy = AddedBy.AppUi)
    {
        Console.WriteLine($"[intake] add {paths.Count} path(s) to {targetCategoryId}");
        await library.AddPathsAsync(paths, new AddRequest { TargetCategoryId = targetCategoryId, AddedBy = addedBy });
    }

    public Task PickAndAddFilesAsync(long? targetCategoryId)
    {
        Console.WriteLine($"[intake] file picker for {targetCategoryId}");
        return Task.CompletedTask;
    }

    public Task PasteAsync(long? targetCategoryId)
    {
        Console.WriteLine($"[intake] paste into {targetCategoryId}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeDialogService : IDialogService
{
    public Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
    {
        Console.WriteLine($"[dialog] prompt '{title}' ({initialText})");
        return Task.FromResult<string?>(null);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        Console.WriteLine($"[dialog] confirm '{title}': {message} [{confirmText}]");
        return Task.FromResult(false);
    }

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
    {
        Console.WriteLine($"[dialog] choose '{title}': {message} [{string.Join(" | ", options)}]");
        return Task.FromResult<int?>(null);
    }

    public Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true)
    {
        Console.WriteLine($"[dialog] pick category '{title}'");
        return Task.FromResult<long?>(null);
    }

    public Task<string?> PickFileToLocateAsync(string originalFileName)
    {
        Console.WriteLine($"[dialog] locate '{originalFileName}'");
        return Task.FromResult<string?>(null);
    }
}

internal sealed class ConsoleNotifications : INotificationService
{
    public void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Action? action = null)
        => Console.WriteLine($"[notice:{kind}] {message}{(actionText is null ? "" : $" [{actionText}]")}");
}

internal sealed class FakeNavigator : IShellNavigator
{
    public long? CurrentCategoryId { get; set; }

    public void OpenCategory(long categoryId, long? focusItemId = null)
    {
        Console.WriteLine($"[nav] open category {categoryId} (focus {focusItemId})");
        CurrentCategoryId = categoryId;
    }
}
