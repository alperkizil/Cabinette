using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Tests;

/// <summary>
/// Drives the real <see cref="ShellContextMenuService.Show"/> (TrackPopupMenuEx) end to end: opens the menu over a WPF
/// window, keyboards into the lazily filled "Open with" submenu and checks that Windows' WM_INITMENUPOPUP reached the
/// shell through the owner-window hook. It briefly shows a menu on screen, so it only runs when
/// CABINETTE_INTERACTIVE_TESTS=1.
/// </summary>
public sealed partial class InteractiveContextMenuTests(ITestOutputHelper output)
{
    private const int MN_GETHMENU = 0x01E1;
    private const int VK_DOWN = 0x28;
    private const int VK_RIGHT = 0x27;
    private const int VK_UP = 0x26;
    private const uint MFS_HILITE = 0x80;

    [Fact]
    public void ShowRoutesSubmenuInitializationThroughOwnerWindow()
    {
        if (Environment.GetEnvironmentVariable("CABINETTE_INTERACTIVE_TESTS") != "1")
        {
            output.WriteLine("Set CABINETTE_INTERACTIVE_TESTS=1 to run this test (it shows a menu for about a second).");
            return;
        }

        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "interactive.png");
        ThumbnailTests.WritePng(file, 8, 8, _ => { });

        var (result, before, after, log) = Sta.Run(() =>
        {
            var lines = new List<string>();
            var window = new Window { Width = 300, Height = 200, Left = 100, Top = 100, Topmost = true, Title = "Cabinette menu test", ShowActivated = true };
            window.Show();
            window.Activate();
            var hwnd = new WindowInteropHelper(window).Handle;
            var menuMessages = new Dictionary<int, int>();
            HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (m is WM_INITMENUPOPUP or 0x011F /* WM_MENUSELECT */ or WM_MEASUREITEM or WM_DRAWITEM)
                {
                    menuMessages[m] = menuMessages.GetValueOrDefault(m) + 1;
                }

                return IntPtr.Zero;
            });

            int countBefore = -1, countAfter = -1, step = 0, target = -1, state = 0;
            IntPtr submenu = IntPtr.Zero;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                step++;
                var menuWindow = FindWindow("#32768", null);
                if (menuWindow == IntPtr.Zero || step > 80)
                {
                    lines.Add($"tick {step}: {(menuWindow == IntPtr.Zero ? "no menu window" : "giving up")}");
                    if (step > 40)
                    {
                        EndMenu();
                        timer.Stop();
                    }

                    return;
                }

                var menu = SendMessage(menuWindow, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
                switch (state)
                {
                    case 0:
                        // Find "Open with": a lazily filled submenu that holds a single placeholder until initialized.
                        for (var pass = 0; pass < 2 && target < 0; pass++)
                        {
                            for (var i = 0; i < GetMenuItemCount(menu) && target < 0; i++)
                            {
                                var sub = GetSubMenu(menu, i);
                                var (text, enabled) = Describe(menu, i);
                                if (sub != IntPtr.Zero && enabled && GetMenuItemCount(sub) == 1
                                    && (pass == 1 || text.Replace("&", "").StartsWith("Open with", StringComparison.OrdinalIgnoreCase)))
                                {
                                    target = i;
                                    submenu = sub;
                                    lines.Add($"submenu '{text}'");
                                }
                            }
                        }

                        if (target < 0)
                        {
                            lines.Add("no lazily filled submenu found");
                            EndMenu();
                            timer.Stop();
                            return;
                        }

                        countBefore = GetMenuItemCount(submenu);
                        lines.Add($"target #{target}");
                        PostKey(menuWindow, VK_DOWN);
                        state = 1;
                        return;

                    case 1:
                        // Steer by the highlighted item until it is the target, then open the submenu.
                        var highlighted = -1;
                        for (var i = 0; i < GetMenuItemCount(menu); i++)
                        {
                            var info = new MENUITEMINFOW { cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(), fMask = MIIM_STATE };
                            if (GetMenuItemInfo(menu, (uint)i, true, ref info) && (info.fState & MFS_HILITE) != 0)
                            {
                                highlighted = i;
                            }
                        }

                        if (highlighted == target)
                        {
                            lines.Add($"tick {step}: on target, opening it");
                            PostKey(menuWindow, VK_RIGHT);
                            state = 2;
                        }
                        else
                        {
                            PostKey(menuWindow, highlighted > target ? VK_UP : VK_DOWN);
                        }

                        return;

                    default:
                        countAfter = GetMenuItemCount(submenu);
                        lines.Add($"tick {step}: submenu has {countAfter} items; owner saw " + string.Join(", ", menuMessages.Select(kv => $"0x{kv.Key:X}x{kv.Value}")));
                        if (countAfter > countBefore && state == 2)
                        {
                            state = 3; // Give the submenu one more tick to paint.
                        }
                        else if (state == 3 || step > 60)
                        {
                            CaptureScreen(menuWindow, Path.Combine(Path.GetTempPath(), "Cabinette.Windows.Tests", "context-menu.png"));
                            EndMenu();
                            timer.Stop();
                        }

                        return;
                }
            };            timer.Start();

            var source = PresentationSource.FromVisual(window)!;
            var origin = window.PointToScreen(new Point(20, 20));
            var menuResult = new ShellContextMenuService().Show(hwnd, [file], origin, [new("cabinette", "Cabinette item")]);
            timer.Stop();
            window.Close();
            _ = source;
            return (menuResult, countBefore, countAfter, lines);
        });

        foreach (var line in log)
        {
            output.WriteLine(line);
        }

        output.WriteLine($"result {result}, submenu {before} → {after}");
        Assert.Equal(ContextMenuResult.Dismissed, result);
        Assert.True(after > before && before >= 0, "the submenu was not filled while the menu was open");
    }

    private static (string Text, bool Enabled) Describe(IntPtr menu, int position)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_STRING | MIIM_STATE,
                dwTypeData = buffer,
                cch = 255,
            };
            return GetMenuItemInfo(menu, (uint)position, true, ref info)
                ? (Marshal.PtrToStringUni(buffer) ?? "", (info.fState & MFS_DISABLED) == 0)
                : ("", false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Saves exactly the area covered by the open menu windows (menu plus submenu) for visual review.</summary>
    private static void CaptureScreen(IntPtr menuWindow, string path)
    {
        GetWindowRect(menuWindow, out var rect);
        for (var w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "#32768", null); w != IntPtr.Zero; w = FindWindowEx(IntPtr.Zero, w, "#32768", null))
        {
            if (IsWindowVisible(w) && GetWindowRect(w, out var r))
            {
                rect = new RECT { Left = Math.Min(rect.Left, r.Left), Top = Math.Min(rect.Top, r.Top), Right = Math.Max(rect.Right, r.Right), Bottom = Math.Max(rect.Bottom, r.Bottom) };
            }
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 /* SRCCOPY */);
        SelectObject(memory, old);
        var source = BitmapInterop.FromHBitmap(bitmap);
        DeleteObject(bitmap);
        DeleteDC(memory);
        _ = ReleaseDC(IntPtr.Zero, screen);
        if (source is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, DibConverter.EncodePng(source));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    private static void PostKey(IntPtr hwnd, int key)
    {
        PostMessage(hwnd, WM_KEYDOWN, key, 0);
        PostMessage(hwnd, WM_KEYUP, key, unchecked((nint)0xC0000001));
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string? className, string? windowName);
}
