using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>
/// The real Windows classic context menu (IContextMenu: Open with, Send to, 7-Zip, ...) with Cabinette's items on top.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only paths that exist get shell items; when none exist only the custom items are shown.</item>
/// <item>Items in one folder use that folder's IShellFolder. Items spread over several folders use the desktop folder with
/// absolute PIDLs (what Explorer's search results do); if the shell refuses that, only the custom items are shown, so a
/// shell verb never silently applies to just part of the selection.</item>
/// <item>Custom item text is literal: '&amp;' is shown as-is (no accelerator).</item>
/// <item>The shell's "Cut" is removed (a paste in Explorer would move the file out of the library's sight).</item>
/// <item>"Send to"/"Open with" are filled lazily through IContextMenu2/3.HandleMenuMsg(2): while the menu is open the
/// owner's HwndSource is hooked (or a hidden helper window is used when the owner is not a WPF window).</item>
/// </list>
/// </remarks>
public sealed class ShellContextMenuService : IShellContextMenuService
{
    internal const uint FirstCustomId = 1;
    internal const uint LastCustomId = 0x0FFF;
    internal const uint FirstShellId = 0x1000;
    internal const uint LastShellId = 0x7FFF;

    public ContextMenuResult Show(IntPtr ownerHwnd, IReadOnlyList<string> paths, Point screenPointPx, IReadOnlyList<CustomMenuItem> customItems)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(customItems);

        using var session = ContextMenuSession.Create(ownerHwnd, paths, customItems, extendedVerbs: IsKeyDown(VK_SHIFT));
        if (session.IsEmpty)
        {
            return ContextMenuResult.Dismissed;
        }

        uint command;
        var x = (int)Math.Round(screenPointPx.X);
        var y = (int)Math.Round(screenPointPx.Y);
        using (var router = MenuMessageRouter.Attach(ownerHwnd, session))
        {
            command = (uint)TrackPopupMenuEx(session.Handle, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, router.OwnerHwnd, IntPtr.Zero);
        }

        if (command == 0)
        {
            return ContextMenuResult.Dismissed;
        }

        if (session.TryGetCustomId(command, out var customId))
        {
            return new ContextMenuResult(customId, InterceptedShellVerb.None);
        }

        if (!session.IsShellCommand(command))
        {
            return ContextMenuResult.Dismissed;
        }

        var verb = session.GetVerb(command);
        if (string.Equals(verb, "delete", StringComparison.OrdinalIgnoreCase))
        {
            return new ContextMenuResult(null, InterceptedShellVerb.Delete);
        }

        if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
        {
            return new ContextMenuResult(null, InterceptedShellVerb.Rename);
        }

        session.Invoke(command, ownerHwnd, x, y);
        return ContextMenuResult.Dismissed;
    }

    /// <summary>
    /// Builds the menu exactly like <see cref="Show"/> (custom items, separator, QueryContextMenu) without displaying it,
    /// optionally initializing submenus the way WM_INITMENUPOPUP would, and returns its structure. Call on an STA thread.
    /// </summary>
    internal static IReadOnlyList<MenuItemSnapshot> BuildMenuForTest(
        IReadOnlyList<string> paths, IReadOnlyList<CustomMenuItem> customItems, bool initializeSubmenus = true, bool extendedVerbs = false)
    {
        using var session = ContextMenuSession.Create(IntPtr.Zero, paths, customItems, extendedVerbs);
        return session.Snapshot(initializeSubmenus);
    }
}

/// <summary>One menu entry as seen by tests.</summary>
internal sealed record MenuItemSnapshot(
    uint Id, string Text, bool IsSeparator, bool IsEnabled, bool IsOwnerDrawn, string? CustomId, string? Verb, IReadOnlyList<MenuItemSnapshot> Children)
{
    public bool IsShellItem => Id >= ShellContextMenuService.FirstShellId && Id <= ShellContextMenuService.LastShellId;

    public override string ToString() => IsSeparator ? "----" : $"{Text} [id {Id}{(Verb is null ? "" : ", verb " + Verb)}]";
}

/// <summary>An HMENU with Cabinette's items plus the shell's IContextMenu items, and everything needed to clean up.</summary>
internal sealed class ContextMenuSession : IDisposable
{
    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_CANRENAME = 0x10;
    private const uint CMF_EXTENDEDVERBS = 0x100;
    private const uint GCS_VERBA = 0x0;
    private const uint GCS_VERBW = 0x4;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;

    private readonly Dictionary<uint, string> _customIds = [];
    private readonly List<IntPtr> _pidls = [];
    private IContextMenu? _menu;
    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;
    private IShellFolder? _folder;

    private ContextMenuSession()
    {
    }

    public IntPtr Handle { get; private set; }

    public bool HasShellItems => _menu is not null;

    public bool IsEmpty => Handle == IntPtr.Zero || GetMenuItemCount(Handle) <= 0;

    public static ContextMenuSession Create(IntPtr hwnd, IReadOnlyList<string> paths, IReadOnlyList<CustomMenuItem> customItems, bool extendedVerbs)
    {
        var session = new ContextMenuSession();
        try
        {
            session.Handle = CreatePopupMenu();
            if (session.Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreatePopupMenu failed.");
            }

            var nextId = ShellContextMenuService.FirstCustomId;
            AddCustomItems(session.Handle, customItems, session._customIds, ref nextId);
            session.AddShellItems(hwnd, paths, extendedVerbs);
            RemoveRedundantSeparators(session.Handle);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public bool TryGetCustomId(uint command, out string id) => _customIds.TryGetValue(command, out id!);

    public bool IsShellCommand(uint command)
        => _menu is not null && command >= ShellContextMenuService.FirstShellId && command <= ShellContextMenuService.LastShellId;

    /// <summary>Canonical verb ("open", "delete", "rename", ...) of a shell command, or null when the handler has none.</summary>
    public string? GetVerb(uint command)
    {
        if (!IsShellCommand(command))
        {
            return null;
        }

        const int Chars = 512;
        var buffer = Marshal.AllocHGlobal(Chars * 2);
        try
        {
            var offset = (UIntPtr)(command - ShellContextMenuService.FirstShellId);
            ClearBuffer(buffer, Chars * 2);
            if (_menu!.GetCommandString(offset, GCS_VERBW, IntPtr.Zero, buffer, Chars) >= 0)
            {
                var verb = Marshal.PtrToStringUni(buffer);
                if (!string.IsNullOrEmpty(verb))
                {
                    return verb;
                }
            }

            ClearBuffer(buffer, Chars * 2);
            if (_menu.GetCommandString(offset, GCS_VERBA, IntPtr.Zero, buffer, Chars) >= 0)
            {
                var verb = Marshal.PtrToStringAnsi(buffer);
                if (!string.IsNullOrEmpty(verb))
                {
                    return verb;
                }
            }
        }
        catch (Exception)
        {
            // Some handlers throw for commands they don't describe.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return null;
    }

    public void Invoke(uint command, IntPtr ownerHwnd, int x, int y)
    {
        if (!IsShellCommand(command))
        {
            return;
        }

        var mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
        if (IsKeyDown(VK_SHIFT))
        {
            mask |= CMIC_MASK_SHIFT_DOWN;
        }

        if (IsKeyDown(VK_CONTROL))
        {
            mask |= CMIC_MASK_CONTROL_DOWN;
        }

        var verb = (IntPtr)(command - ShellContextMenuService.FirstShellId);
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = mask,
            hwnd = ownerHwnd,
            lpVerb = verb,
            lpVerbW = verb,
            nShow = SW_SHOWNORMAL,
            ptInvoke = new POINT { x = x, y = y },
        };

        try
        {
            var hr = _menu!.InvokeCommand(ref info);
            if (hr < 0)
            {
                Debug.WriteLine($"Cabinette: shell command {command} failed: 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cabinette: shell command {command} threw: {ex.Message}");
        }
    }

    /// <summary>Forwards owner-window menu messages to the shell menu (fills Send to / Open with, draws their icons).</summary>
    public bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        try
        {
            if (_menu3 is not null)
            {
                if (_menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) >= 0)
                {
                    return true;
                }
            }
            else if (_menu2 is not null && msg != WM_MENUCHAR)
            {
                if (_menu2.HandleMenuMsg((uint)msg, wParam, lParam) >= 0)
                {
                    result = msg == WM_INITMENUPOPUP ? IntPtr.Zero : 1;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // A misbehaving extension must not take the app down.
        }

        return false;
    }

    public IReadOnlyList<MenuItemSnapshot> Snapshot(bool initializeSubmenus) => SnapshotMenu(Handle, initializeSubmenus, depth: 0);

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyMenu(Handle); // Destroys submenus too.
            Handle = IntPtr.Zero;
        }

        ReleaseComObject(_menu);
        _menu = null;
        _menu2 = null;
        _menu3 = null;
        ReleaseComObject(_folder);
        _folder = null;

        foreach (var pidl in _pidls)
        {
            Marshal.FreeCoTaskMem(pidl);
        }

        _pidls.Clear();
    }

    private void AddShellItems(IntPtr hwnd, IReadOnlyList<string> paths, bool extendedVerbs)
    {
        _menu = CreateShellMenu(hwnd, paths);
        if (_menu is null)
        {
            return;
        }

        var position = (uint)GetMenuItemCount(Handle);
        if (position > 0)
        {
            AppendMenu(Handle, MF_SEPARATOR, UIntPtr.Zero, null);
            position++;
        }

        var flags = CMF_NORMAL | CMF_CANRENAME | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
        int hr;
        try
        {
            hr = _menu.QueryContextMenu(Handle, position, ShellContextMenuService.FirstShellId, ShellContextMenuService.LastShellId, flags);
        }
        catch (Exception)
        {
            hr = -1;
        }

        if (hr < 0)
        {
            ReleaseComObject(_menu);
            _menu = null;
            return;
        }

        _menu2 = _menu as IContextMenu2;
        _menu3 = _menu as IContextMenu3;
        RemoveShellVerbs(position);
    }

    /// <summary>
    /// Removes the shell's "Cut": pasting it in Explorer moves the vault file (or a pointer's real file) away behind
    /// Cabinette's back, and the item turns missing. Copy stays, and Delete/Rename are routed through Cabinette.
    /// </summary>
    private void RemoveShellVerbs(uint firstShellPosition)
    {
        for (var i = GetMenuItemCount(Handle) - 1; i >= (int)firstShellPosition; i--)
        {
            var info = new MENUITEMINFOW { cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(), fMask = MIIM_ID | MIIM_SUBMENU };
            if (!GetMenuItemInfo(Handle, (uint)i, true, ref info) || info.hSubMenu != IntPtr.Zero)
            {
                continue;
            }

            if (string.Equals(GetVerb(info.wID), "cut", StringComparison.OrdinalIgnoreCase))
            {
                RemoveMenu(Handle, (uint)i, MF_BYPOSITION);
            }
        }
    }

    private IContextMenu? CreateShellMenu(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            if (!seen.Add(full) || SHParseDisplayName(full, IntPtr.Zero, out var pidl, 0, out _) < 0 || pidl == IntPtr.Zero)
            {
                continue;
            }

            _pidls.Add(pidl);
            parents.Add(Path.GetDirectoryName(full.TrimEnd('\\')) ?? "");
        }

        if (_pidls.Count == 0)
        {
            return null;
        }

        try
        {
            IntPtr menuPtr;
            if (parents.Count == 1)
            {
                if (SHBindToParent(_pidls[0], ShellIids.IShellFolder, out var folderPtr, out _) < 0 || folderPtr == IntPtr.Zero)
                {
                    return null;
                }

                _folder = TakeObject<IShellFolder>(folderPtr);
                var children = _pidls.Select(ILFindLastID).ToArray();
                if (_folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ShellIids.IContextMenu, IntPtr.Zero, out menuPtr) < 0)
                {
                    return null;
                }
            }
            else
            {
                if (SHGetDesktopFolder(out var desktopPtr) < 0 || desktopPtr == IntPtr.Zero)
                {
                    return null;
                }

                _folder = TakeObject<IShellFolder>(desktopPtr);
                if (_folder.GetUIObjectOf(hwnd, (uint)_pidls.Count, [.. _pidls], ShellIids.IContextMenu, IntPtr.Zero, out menuPtr) < 0)
                {
                    return null;
                }
            }

            return menuPtr == IntPtr.Zero ? null : TakeObject<IContextMenu>(menuPtr);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T TakeObject<T>(IntPtr unknown)
        where T : class
    {
        try
        {
            return (T)Marshal.GetObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static void AddCustomItems(IntPtr menu, IReadOnlyList<CustomMenuItem> items, Dictionary<uint, string> ids, ref uint nextId)
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (item.IsSeparator)
            {
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }

            var flags = MF_STRING | (item.IsEnabled ? 0 : MF_GRAYED);
            var text = (item.Text ?? "").Replace("&", "&&", StringComparison.Ordinal);
            if (item.Children is { Count: > 0 } children)
            {
                var submenu = CreatePopupMenu();
                AddCustomItems(submenu, children, ids, ref nextId);
                if (!AppendMenu(menu, flags | MF_POPUP, (UIntPtr)(nuint)submenu, text))
                {
                    DestroyMenu(submenu);
                }

                continue;
            }

            if (nextId > ShellContextMenuService.LastCustomId)
            {
                throw new InvalidOperationException("Too many custom menu items.");
            }

            var id = nextId++;
            ids[id] = item.Id;
            AppendMenu(menu, flags, (UIntPtr)id, text);
        }
    }

    /// <summary>Removes leading, trailing and doubled separators at the top level (shell handlers add their own).</summary>
    private static void RemoveRedundantSeparators(IntPtr menu)
    {
        var previousWasSeparator = true; // Treat the start as a separator so a leading one is removed.
        for (var i = 0; i < GetMenuItemCount(menu);)
        {
            if (IsSeparator(menu, i) && previousWasSeparator)
            {
                RemoveMenu(menu, (uint)i, MF_BYPOSITION);
                continue;
            }

            previousWasSeparator = IsSeparator(menu, i);
            i++;
        }

        var count = GetMenuItemCount(menu);
        if (count > 0 && IsSeparator(menu, count - 1))
        {
            RemoveMenu(menu, (uint)(count - 1), MF_BYPOSITION);
        }
    }

    private static bool IsSeparator(IntPtr menu, int position)
    {
        var info = new MENUITEMINFOW { cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(), fMask = MIIM_FTYPE };
        return GetMenuItemInfo(menu, (uint)position, true, ref info) && (info.fType & MFT_SEPARATOR) != 0;
    }

    private List<MenuItemSnapshot> SnapshotMenu(IntPtr menu, bool initializeSubmenus, int depth)
    {
        var result = new List<MenuItemSnapshot>();
        var count = GetMenuItemCount(menu);
        for (var i = 0; i < count; i++)
        {
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_FTYPE | MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_STRING,
            };
            if (!GetMenuItemInfo(menu, (uint)i, true, ref info))
            {
                continue;
            }

            var text = "";
            if ((info.fType & MFT_SEPARATOR) == 0 && info.cch > 0)
            {
                var buffer = Marshal.AllocHGlobal((int)(info.cch + 1) * 2);
                try
                {
                    info.dwTypeData = buffer;
                    info.cch++;
                    info.fMask = MIIM_STRING;
                    if (GetMenuItemInfo(menu, (uint)i, true, ref info))
                    {
                        text = Marshal.PtrToStringUni(buffer) ?? "";
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // Re-read the rest (MIIM_STRING alone left the other fields as they were).
                info.fMask = MIIM_FTYPE | MIIM_ID | MIIM_STATE | MIIM_SUBMENU;
                info.dwTypeData = IntPtr.Zero;
                GetMenuItemInfo(menu, (uint)i, true, ref info);
            }

            IReadOnlyList<MenuItemSnapshot> children = [];
            if (info.hSubMenu != IntPtr.Zero && depth < 3)
            {
                if (initializeSubmenus)
                {
                    // What Windows sends to the owner window right before a submenu opens.
                    HandleMenuMessage(WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i, out _);
                }

                children = SnapshotMenu(info.hSubMenu, initializeSubmenus, depth + 1);
            }

            var isSeparator = (info.fType & MFT_SEPARATOR) != 0;
            var id = info.hSubMenu != IntPtr.Zero ? 0 : info.wID;
            _customIds.TryGetValue(id, out var customId);
            var verb = !isSeparator && info.hSubMenu == IntPtr.Zero ? GetVerb(id) : null;
            result.Add(new MenuItemSnapshot(
                id, text.Replace("&&", "\u0001", StringComparison.Ordinal).Replace("&", "", StringComparison.Ordinal).Replace('\u0001', '&'),
                isSeparator, (info.fState & MFS_DISABLED) == 0, (info.fType & MFT_OWNERDRAW) != 0, customId, verb, children));
        }

        return result;
    }

    private static unsafe void ClearBuffer(IntPtr buffer, int bytes) => new Span<byte>((void*)buffer, bytes).Clear();
}

/// <summary>
/// Routes WM_INITMENUPOPUP / WM_DRAWITEM / WM_MEASUREITEM / WM_MENUCHAR from the menu's owner window to the shell menu
/// while it is open. Hooks the owner's WPF HwndSource, or creates a hidden helper window when there is none.
/// </summary>
internal sealed class MenuMessageRouter : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly ContextMenuSession _session;
    private readonly HwndSourceHook _hook;
    private HwndSource? _hookedSource;
    private HwndSource? _helperWindow;

    private MenuMessageRouter(ContextMenuSession session)
    {
        _session = session;
        _hook = Hook;
    }

    /// <summary>The window to pass to TrackPopupMenuEx.</summary>
    public IntPtr OwnerHwnd { get; private set; }

    public static MenuMessageRouter Attach(IntPtr ownerHwnd, ContextMenuSession session)
    {
        var router = new MenuMessageRouter(session);
        var source = ownerHwnd != IntPtr.Zero ? HwndSource.FromHwnd(ownerHwnd) : null;
        if (source is { IsDisposed: false })
        {
            source.AddHook(router._hook);
            router._hookedSource = source;
            router.OwnerHwnd = ownerHwnd;
            return router;
        }

        var parameters = new HwndSourceParameters("Cabinette shell menu owner")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            ParentWindow = ownerHwnd,
        };
        router._helperWindow = new HwndSource(parameters);
        router._helperWindow.AddHook(router._hook);
        router.OwnerHwnd = router._helperWindow.Handle;

        // A menu owned by a window that is not in the foreground does not close when the user clicks elsewhere.
        SetForegroundWindow(router.OwnerHwnd);
        return router;
    }

    public void Dispose()
    {
        _hookedSource?.RemoveHook(_hook);
        _hookedSource = null;

        if (_helperWindow is not null)
        {
            PostMessage(_helperWindow.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            _helperWindow.RemoveHook(_hook);
            _helperWindow.Dispose();
            _helperWindow = null;
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INITMENUPOPUP:
            case WM_MENUCHAR:
            case WM_DRAWITEM when wParam == IntPtr.Zero: // wParam 0 = sent by a menu, not a control.
            case WM_MEASUREITEM when wParam == IntPtr.Zero:
                if (_session.HandleMenuMessage(msg, wParam, lParam, out var result))
                {
                    handled = true;
                    return result;
                }

                break;
        }

        return IntPtr.Zero;
    }
}
