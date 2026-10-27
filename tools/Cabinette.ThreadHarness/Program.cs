using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Services;
using Cabinette.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Cabinette.ThreadHarness;

/// <summary>
/// Dev harness for the thread view.
/// <code>
/// Cabinette.ThreadHarness [--theme light|dark] [--view chat|grid|details]
///                         [--state select|search|empty|noresults|filter|tags|viewer|drop|focus]
///                         [--count N] [--width W] [--height H] [--scale S] [--screenshot file.png]
/// </code>
/// With --screenshot the window is rendered off-screen after data and thumbnails have loaded, saved as PNG, and the process exits.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        HarnessOptions options;
        try
        {
            options = HarnessOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: Cabinette.ThreadHarness [--theme light|dark] [--view chat|grid|details] [--state select|search|empty|noresults|filter|tags|viewer|drop|focus] [--count N] [--screenshot file.png]");
            return 2;
        }

        if (options.Screenshot is not null)
        {
            // Never leave a process behind, whatever happens.
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(120));
                Console.Error.WriteLine("Harness timed out.");
                Environment.Exit(3);
            })
            { IsBackground = true }.Start();
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = BuildResources(options.Dark);
        var exitCode = 0;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            if (options.Screenshot is not null)
            {
                e.Handled = true;
                exitCode = 1;
                app.Shutdown(1);
            }
        };

        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                exitCode = await RunAsync(app, options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }

            if (options.Screenshot is not null || exitCode != 0)
            {
                app.Shutdown(exitCode);
            }
        });

        app.Run();
        return exitCode;
    }

    private static ResourceDictionary BuildResources(bool dark)
    {
        // Same dictionaries as src/Cabinette.App/App.xaml.
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Cabinette;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        });
        return resources;
    }

    private static async Task<int> RunAsync(Application app, HarnessOptions options)
    {
        var media = new FakeMediaInfoService();
        var library = SampleData.Create(media, options.Count, Path.Combine(Path.GetTempPath(), "CabinetteThreadHarness"));
        var services = new ServiceCollection()
            .AddSingleton<ILibraryService>(library)
            .AddSingleton<IThumbnailService, FakeThumbnailService>()
            .AddSingleton<IMediaInfoService>(media)
            .AddSingleton<IShellContextMenuService, FakeContextMenuService>()
            .AddSingleton<IShellOperations, FakeShellOperations>()
            .AddSingleton<IFileIntake>(new FakeFileIntake(library))
            .AddSingleton<IDialogService, FakeDialogService>()
            .AddSingleton<INotificationService, ConsoleNotifications>()
            .AddSingleton<IShellNavigator, FakeNavigator>()
            .AddThreadFeature()
            .BuildServiceProvider();

        var loader = services.GetRequiredService<ThumbnailLoader>();
        var vm = services.GetRequiredService<IThreadViewModelFactory>().Create();
        var view = new ThreadView();
        var window = new Window
        {
            Title = "Cabinette - thread harness",
            Width = options.Width,
            Height = options.Height,
            Content = view,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        };
        window.SetResourceReference(Window.BackgroundProperty, "Cab.WindowBackground");
        if (options.Screenshot is not null)
        {
            HideOffScreen(window);
        }

        window.Show();

        var category = options.State switch
        {
            "empty" => SampleData.Inbox,
            _ => options.Count > 0 ? SampleData.Big : options.Category,
        };
        var stopwatch = Stopwatch.StartNew();
        await vm.LoadAsync(category);
        Console.WriteLine($"Loaded {vm.TotalCount} items ({vm.ChatRows.Count} rows) in {stopwatch.ElapsedMilliseconds} ms");
        view.DataContext = vm;
        vm.ViewMode = options.View switch
        {
            "grid" => ThreadViewMode.Grid,
            "details" => ThreadViewMode.Details,
            _ => ThreadViewMode.Chat,
        };

        Window? extra = await ApplyStateAsync(options, vm, view, library);
        await WaitForQuietAsync(loader);
        if ((options.Scroll ?? (options.State is "sticky" or "unseen" ? (double?)0.5 : null)) is { } fraction && FindScrollViewer(view) is { } scroll)
        {
            // Scroll a few times: with pixel virtualization the extent settles as rows get measured.
            for (var i = 0; i < 4; i++)
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(150);
            }

            await WaitForQuietAsync(loader);
            if (options.State == "unseen")
            {
                // New items arrive while the user reads older ones: the jump button shows a count.
                await library.CreateNoteAsync(SampleData.Holiday, "Arrived while you were scrolled up.", Cabinette.Core.Models.AddedBy.Ai);
                await library.CreateNoteAsync(SampleData.Holiday, "And another one.", Cabinette.Core.Models.AddedBy.Ai);
                await Task.Delay(2200);
            }

            if (options.State == "sticky")
            {
                // A user-like scroll right before the capture: the floating date pill is visible for 1.5 s.
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 60);
                await Task.Delay(350);
            }
        }
        Console.WriteLine($"Ready after {stopwatch.ElapsedMilliseconds} ms");

        if (options.Screenshot is null)
        {
            window.Closed += (_, _) =>
            {
                vm.Dispose();
                app.Shutdown();
            };
            return 0;
        }

        Save(extra ?? window, options.Screenshot, options.Scale);
        extra?.Close();
        window.Close();
        vm.Dispose();
        return 0;
    }

    private static async Task<Window?> ApplyStateAsync(HarnessOptions options, ThreadViewModel vm, ThreadView view, FakeLibraryService library)
    {
        ThreadItemViewModel Find(string name) => vm.Items.First(i => i.FileName == name);
        switch (options.State)
        {
            case "select":
                vm.IsSelectMode = true;
                vm.ClickItem(Find("Windows bloom.jpg"), ctrl: true, shift: false);
                vm.ClickItem(Find("Photos backup.zip"), ctrl: true, shift: false);
                vm.ClickItems(vm.Items.Where(i => i.FileName.StartsWith("IMG_210", StringComparison.Ordinal)).ToList(), ctrl: true, shift: false);
                break;
            case "search":
                vm.IsSearchOpen = true;
                vm.SearchText = "#todo";
                await Task.Delay(700);
                break;
            case "noresults":
                vm.IsSearchOpen = true;
                vm.SearchText = "zebra";
                await Task.Delay(700);
                break;
            case "drop":
                if (view.FindName("DropOverlay") is UIElement overlay)
                {
                    // Plays the real entrance; the capture happens well after it settled.
                    Cabinette.App.Views.Motion.DropZone.SetIsActive(overlay, true);
                }

                break;
            case "live":
                // Changes made elsewhere (Explorer, vault monitor, AI) while the thread is open.
                await Task.Delay(300);
                await library.CreateNoteAsync(SampleData.Holiday, "This note arrived from the AI while the thread was open.", Cabinette.Core.Models.AddedBy.Ai);
                await library.AddPathsAsync([@"C:\Windows\Web\Wallpaper\ThemeD\img34.jpg"], new Cabinette.Core.Models.AddRequest { TargetCategoryId = SampleData.Holiday, AddedBy = Cabinette.Core.Models.AddedBy.Explorer });
                await Task.Delay(900);
                break;
            case "playing":
            {
                var audio = Find("Alarm01.wav");
                audio.IsPlaying = true;
                audio.PlaybackPosition = 2.1;
                vm.FocusItem(audio.Id);
                await Task.Delay(2600);
                break;
            }

            case "focus":
                vm.FocusItem(Find("Budget 2026.xlsx").Id);
                await Task.Delay(250);
                break;
            case "filter":
            {
                vm.Filter.Types.First(t => t.Key == "images").IsSelected = true;
                vm.Filter.Types.First(t => t.Key == "documents").IsSelected = true;
                vm.Filter.DatePreset = DateFilterPreset.Last30Days;
                var panelWindow = new Window
                {
                    Content = new ThreadFilterPanel { DataContext = vm.Filter },
                    SizeToContent = SizeToContent.WidthAndHeight,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    AllowsTransparency = true,
                    WindowStyle = WindowStyle.None,
                    Background = Brushes.Transparent,
                };
                HideOffScreen(panelWindow);
                panelWindow.Show();
                await Task.Delay(400);
                return panelWindow;
            }

            case "tags":
            {
                vm.ClickItem(Find("IMG_2041.jpg"), ctrl: true, shift: false);
                vm.ClickItem(Find("Budget 2026.xlsx"), ctrl: true, shift: false);
                var editor = await vm.CreateTagEditorForSelectionAsync();
                editor.Input = "ita";
                var dialog = new TagEditorDialog(editor) { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
                HideOffScreen(dialog);
                dialog.Show();
                await Task.Delay(400);
                return dialog;
            }

            case "viewer":
            {
                var images = vm.Items.Where(i => i.VisualKind == ItemVisualKind.Image).ToList();
                var viewer = new MediaViewerWindow(images, images.FindIndex(i => i.FileName == "IMG_2041.jpg"), _ => { })
                {
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    Width = options.Width,
                    Height = options.Height,
                };
                HideOffScreen(viewer);
                viewer.Show();
                await Task.Delay(1200);
                return viewer;
            }
        }

        return null;
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is System.Windows.Controls.ScrollViewer { ScrollableHeight: > 0, IsVisible: true } viewer)
            {
                return viewer;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
        }

        return null;
    }

    private static void HideOffScreen(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
    }

    /// <summary>Waits until no thumbnail/media request has been pending for a while (and the dispatcher is idle).</summary>
    private static async Task WaitForQuietAsync(ThumbnailLoader loader)
    {
        var stopwatch = Stopwatch.StartNew();
        var quiet = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            quiet = loader.PendingCount == 0 ? quiet + 1 : 0;
            if (quiet >= 5 && stopwatch.ElapsedMilliseconds > 800)
            {
                break;
            }
        }

        await Task.Delay(300); // fade-in animations
    }

    private static void Save(Window window, string path, double scale)
    {
        window.UpdateLayout();
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("Cab.ChatBackground"), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = File.Create(full);
        encoder.Save(stream);
        Console.WriteLine($"Saved {full}");
    }
}

internal sealed record HarnessOptions(string Theme, string View, string? Screenshot, string? State, int Count, long Category, double Width, double Height, double Scale, double? Scroll = null)
{
    public bool Dark => Theme == "dark";

    public static HarnessOptions Parse(string[] args)
    {
        var options = new HarnessOptions("light", "chat", null, null, 0, SampleData.Holiday, 1000, 780, 1);
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            options = args[i] switch
            {
                "--theme" => options with { Theme = Expect(Next(), "light", "dark") },
                "--view" => options with { View = Expect(Next(), "chat", "grid", "details") },
                "--state" => options with { State = Expect(Next(), "select", "search", "empty", "noresults", "filter", "tags", "viewer", "drop", "focus", "live", "playing", "sticky", "unseen") },
                "--screenshot" => options with { Screenshot = Next() },
                "--count" => options with { Count = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--category" => options with { Category = long.Parse(Next(), CultureInfo.InvariantCulture) },
                "--width" => options with { Width = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--height" => options with { Height = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--scale" => options with { Scale = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--scroll" => options with { Scroll = double.Parse(Next(), CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown argument {args[i]}"),
            };
        }

        return options;
    }

    private static string Expect(string value, params string[] allowed)
        => allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? value.ToLowerInvariant() : throw new ArgumentException($"Expected one of {string.Join(", ", allowed)} but got {value}");
}
