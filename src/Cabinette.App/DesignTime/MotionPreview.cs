using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Motion;
using Cabinette.App.Views.Shell;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App.DesignTime;

/// <summary>
/// Screenshot states for the drag-and-drop motion (all rendered off-screen like every other screenshot state).
/// <list type="bullet">
/// <item><c>drop-thread</c>, <c>drop-window</c>, <c>drop-list</c>: a drop highlight at rest (the end of its entrance).</item>
/// <item><c>film-&lt;name&gt;</c>: a contact sheet of frames while the motion plays, each labelled with its time in ms.
/// Names: drop-thread, cancel-thread, drop-window, drop-list, drop-row, drop-empty, receive-ring (a close-up of the thread
/// card after a drop: the upload ring fills, the check pops in), receive-ring-list and receive-ring-empty (the same for the
/// chat-list and empty-state pills, at twice the size), ring-takeover (the thread card, but a new drag comes in while
/// the ring fills, then leaves), reorder (a category below the visible rows receives a file and rises in; its badge
/// pops), reorder-near (the same for a visible row), reorder-az (A–Z sort: only the badge pops), storm (eight quick
/// changes: the first four animate, then the list stays still).</item>
/// </list>
/// Appending <c>-still</c> to any state renders it with animations off (the reduced-motion path).
/// </summary>
internal static class MotionPreview
{
    private static readonly Rect LeftPane = new(0, 0, 380, 760);

    /// <summary>Around the thread overlay's card (and the window overlay's), where most of the motion happens.</summary>
    private static readonly Rect ThreadCard = new(560, 230, 460, 330);

    private static readonly Rect WindowCard = new(400, 230, 400, 330);

    /// <summary>How long to keep recording after a drop: the whole "received" motion (with its ring) and a little more.</summary>
    private static readonly int ReceivedMs = (int)MotionTokens.RingEnd.TotalMilliseconds + 150;

    /// <summary>Shows a highlight at rest. Returns false for states it doesn't know.</summary>
    public static async Task<bool> ShowAsync(MainWindow window, MainViewModel vm, string state)
    {
        switch (state)
        {
            case "drop-thread":
                await OpenAsync(vm, "Images");
                DropZone.SetIsActive(window.ThreadView.DropOverlay, true);
                return true;
            case "drop-window":
                DropZone.SetIsActive(window.RootDropOverlay, true);
                return true;
            case "drop-list":
                vm.ChatList.IsBackgroundDropTarget = true;
                return true;
            default:
                return false;
        }
    }

    public static async Task FilmAsync(MainWindow window, MainViewModel vm, IServiceProvider services, string name, string path)
    {
        var frames = new List<(BitmapSource Image, long Ms)>();
        var clip = new Rect(0, 0, ScreenshotRunner.Width, ScreenshotRunner.Height);
        var scale = 0.3;
        var zoom = 1.0; // renders at a higher DPI for sharp close-ups of small pieces
        var columns = 6;
        var clock = Stopwatch.StartNew();

        async Task Record(int durationMs, int everyMs = 45)
        {
            var until = clock.ElapsedMilliseconds + durationMs;
            while (clock.ElapsedMilliseconds <= until)
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                frames.Add((Render(window.ContentRoot, zoom), clock.ElapsedMilliseconds));
                await Task.Delay(everyMs);
            }
        }

        switch (name)
        {
            case "drop-thread":
            case "cancel-thread":
                {
                    (clip, scale, columns) = (ThreadCard, 0.5, 8);
                    await OpenAsync(vm, "Images");
                    await Settle(1500);
                    var overlay = window.ThreadView.DropOverlay;
                    clock.Restart();
                    DropZone.SetIsActive(overlay, true);
                    await Record(560);
                    await Task.Delay(500);
                    if (name == "drop-thread")
                    {
                        DropZone.Receive(overlay);
                    }

                    DropZone.SetIsActive(overlay, false);
                    await Record(name == "drop-thread" ? ReceivedMs : 520);
                    break;
                }

            case "receive-ring":
            case "receive-ring-list":
            case "receive-ring-empty":
            case "ring-takeover":
                {
                    // Close-ups of a drop card after a drop: the icon turns into the ring, it fills, the check pops in
                    // and the card leaves; or (takeover, thread) a new drag comes in while the ring fills, then leaves.
                    FrameworkElement zone;
                    Action<bool> setActive;
                    switch (name)
                    {
                        case "receive-ring-list":
                            zone = window.ChatListPane.BackgroundDropZone;
                            setActive = on => vm.ChatList.IsBackgroundDropTarget = on;
                            (zoom, columns) = (2.0, 4);
                            break;
                        case "receive-ring-empty":
                            zone = VisualTreeSearch.FindDescendant<EmptyStateView>(window.ContentRoot)!.DropHighlight;
                            setActive = on => vm.IsEmptyStateDropTarget = on;
                            (zoom, columns) = (2.0, 4);
                            break;
                        default:
                            await OpenAsync(vm, "Images");
                            await Settle(1500);
                            var overlay = window.ThreadView.DropOverlay;
                            (zone, setActive) = (overlay, on => DropZone.SetIsActive(overlay, on));
                            (zoom, columns) = (1.0, 6);
                            break;
                    }

                    setActive(true);
                    await Settle(700);
                    (clip, scale) = (CardBounds(zone, window.ContentRoot), zoom);
                    clock.Restart();
                    await Record(90, 90);
                    DropZone.Receive(zone);
                    setActive(false);
                    if (name != "ring-takeover")
                    {
                        await Record(ReceivedMs, 90);
                    }
                    else
                    {
                        await Record(560, 90);
                        setActive(true); // a new drag enters mid-ring
                        await Record(700, 90);
                        setActive(false); // and leaves again
                        await Record(400, 90);
                    }

                    break;
                }

            case "drop-window":
                {
                    (clip, scale, columns) = (WindowCard, 0.5, 8);
                    clock.Restart();
                    DropZone.SetIsActive(window.RootDropOverlay, true);
                    await Record(560);
                    await Task.Delay(500);
                    DropZone.Receive(window.RootDropOverlay);
                    DropZone.SetIsActive(window.RootDropOverlay, false);
                    await Record(ReceivedMs);
                    break;
                }

            case "drop-list":
                {
                    (clip, scale, columns) = (LeftPane, 0.5, 9);
                    var zone = window.ChatListPane.BackgroundDropZone;
                    clock.Restart();
                    vm.ChatList.IsBackgroundDropTarget = true;
                    await Record(520);
                    await Task.Delay(400);
                    DropZone.Receive(zone);
                    vm.ChatList.IsBackgroundDropTarget = false;
                    await Record(ReceivedMs, 70);
                    break;
                }

            case "drop-empty":
                {
                    var zone = VisualTreeSearch.FindDescendant<EmptyStateView>(window.ContentRoot)!.DropHighlight;
                    columns = 8;
                    clock.Restart();
                    vm.IsEmptyStateDropTarget = true;
                    await Record(520);
                    await Task.Delay(400);
                    DropZone.Receive(zone);
                    vm.IsEmptyStateDropTarget = false;
                    await Record(ReceivedMs, 70);
                    break;
                }

            case "drop-row":
                {
                    (clip, scale, columns) = (LeftPane, 0.5, 9);
                    var row = vm.ChatList.Rows.First(r => r.Name == "Documents");
                    clock.Restart();
                    row.IsDropTarget = true;
                    await Record(520);
                    await Task.Delay(400);
                    if (window.ChatListPane.GetRowContainer(row) is { } container && VisualTreeSearch.FindByName(container, "DropHighlight") is { } zone)
                    {
                        DropZone.Receive(zone);
                    }

                    row.IsDropTarget = false;
                    await Record(480);
                    break;
                }

            case "reorder":
            case "reorder-near":
            case "reorder-az":
            case "storm":
                {
                    (clip, scale, columns) = (LeftPane, 0.5, 10);
                    var library = services.GetRequiredService<ILibraryService>();
                    if (name == "reorder-az")
                    {
                        await vm.ToggleSortCommand.ExecuteAsync(null);
                        await Settle(600);
                    }

                    // Sample files the sample library only points at (nothing on disk is read or changed).
                    var spare = Directory.EnumerateFiles(@"C:\Windows\Media", "*.wav").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                    var next = 0;
                    Task AddAsync(CategoryRowViewModel target, AddedBy by)
                        => library.AddPathsAsync([spare[next++ % spare.Count]], new AddRequest { TargetCategoryId = target.CategoryId, AddedBy = by });

                    var candidates = vm.ChatList.Rows.Where(r => !r.IsPinned && !r.IsSystem && !r.IsAllInRow).ToList();
                    clock.Restart();
                    switch (name)
                    {
                        case "storm":
                            // Eight changes about 250 ms apart (each one refreshes the list): after four, the list stops animating.
                            foreach (var target in Enumerable.Reverse(candidates).Take(8))
                            {
                                await AddAsync(target, AddedBy.VaultMonitor);
                                await Record(180, 60);
                            }

                            await Record(600, 60);
                            break;
                        case "reorder":
                            // The last category (not on screen) receives a file from Explorer: it rises in from the bottom edge.
                            await AddAsync(candidates[^1], AddedBy.Explorer);
                            await Record(900, 35);
                            break;
                        case "reorder-near":
                            // A category on screen: it glides up over the rows making room.
                            await AddAsync(candidates[4], AddedBy.Explorer);
                            await Record(900, 35);
                            break;
                        default:
                            // A–Z: the row stays where it is, only its badge pops.
                            await AddAsync(candidates[2], AddedBy.Explorer);
                            await Record(900, 35);
                            break;
                    }

                    break;
                }

            default:
                throw new ArgumentException($"Unknown film: {name}");
        }

        SaveSheet(frames, clip, zoom, scale, columns, path);
    }

    /// <summary>The drop card of a zone, in the root's coordinates, with a margin (for close-ups).</summary>
    private static Rect CardBounds(FrameworkElement zone, FrameworkElement root)
    {
        var card = VisualTreeSearch.FindDescendant<FrameworkElement>(zone, e => DropZone.GetPart(e) == DropZonePart.Card)!;
        var bounds = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
        bounds.Inflate(28, 28);
        bounds.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        return new Rect(Math.Floor(bounds.X), Math.Floor(bounds.Y), Math.Floor(bounds.Width), Math.Floor(bounds.Height));
    }

    private static async Task OpenAsync(MainViewModel vm, string name)
    {
        var row = vm.ChatList.Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            await vm.ActivateRowAsync(row);
        }
    }

    private static async Task Settle(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static BitmapSource Render(FrameworkElement root, double zoom)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * zoom), (int)Math.Ceiling(root.ActualHeight * zoom), 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Draws the frames (clipped and scaled) in a grid, each with its time stamp. Frames were rendered at <paramref name="zoom"/>.</summary>
    private static void SaveSheet(List<(BitmapSource Image, long Ms)> frames, Rect clip, double zoom, double scale, int columns, string path)
    {
        var pixels = new Rect(clip.X * zoom, clip.Y * zoom, clip.Width * zoom, clip.Height * zoom);
        const double Gap = 6;
        var cellWidth = clip.Width * scale;
        var cellHeight = clip.Height * scale;
        var rows = (frames.Count + columns - 1) / columns;
        var width = (int)Math.Ceiling((columns * (cellWidth + Gap)) + Gap);
        var height = (int)Math.Ceiling((rows * (cellHeight + Gap)) + Gap);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, width, height));
            for (var i = 0; i < frames.Count; i++)
            {
                var (image, ms) = frames[i];
                var cell = new Rect(Gap + ((i % columns) * (cellWidth + Gap)), Gap + ((i / columns) * (cellHeight + Gap)), cellWidth, cellHeight);
                var cropped = new CroppedBitmap(image, new Int32Rect((int)pixels.X, (int)pixels.Y, (int)Math.Min(pixels.Width, image.PixelWidth - pixels.X), (int)Math.Min(pixels.Height, image.PixelHeight - pixels.Y)));
                dc.DrawImage(cropped, cell);
                var label = new FormattedText(ms.ToString(CultureInfo.InvariantCulture) + " ms", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, 1.0);
                dc.DrawRectangle(Brushes.Black, null, new Rect(cell.X, cell.Y, label.Width + 6, label.Height + 2));
                dc.DrawText(label, new Point(cell.X + 3, cell.Y + 1));
            }
        }

        var sheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = File.Create(full);
        encoder.Save(stream);
    }
}
