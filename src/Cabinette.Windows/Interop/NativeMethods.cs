using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Cabinette.Windows.Interop;

/// <summary>Win32 entry points used by the interop layer. Blittable signatures use LibraryImport; COM-marshalled ones use DllImport.</summary>
internal static unsafe partial class NativeMethods
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);

    public const uint COINIT_MULTITHREADED = 0x0;

    // Window messages.
    public const int WM_NULL = 0x0000;
    public const int WM_DRAWITEM = 0x002B;
    public const int WM_MEASUREITEM = 0x002C;
    public const int WM_INITMENUPOPUP = 0x0117;
    public const int WM_MENUCHAR = 0x0120;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;

    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;

    // Menus.
    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_BYPOSITION = 0x0400;
    public const uint MFT_SEPARATOR = 0x0800;
    public const uint MFT_OWNERDRAW = 0x0100;
    public const uint MFS_DISABLED = 0x0003;
    public const uint MIIM_STATE = 0x0001;
    public const uint MIIM_ID = 0x0002;
    public const uint MIIM_SUBMENU = 0x0004;
    public const uint MIIM_STRING = 0x0040;
    public const uint MIIM_FTYPE = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    // Clipboard formats.
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;

    public const uint GMEM_MOVEABLE = 0x0002;

    // GDI.
    public const uint BI_RGB = 0;
    public const uint BI_BITFIELDS = 3;
    public const uint BI_ALPHABITFIELDS = 6;
    public const uint DIB_RGB_COLORS = 0;

    // Shell.
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const int SW_SHOWNORMAL = 1;

    // ---- gdi32 / user32 bitmaps -------------------------------------------------------------

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(IntPtr h, int c, void* pv);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, void* lpvBits, void* lpbmi, uint usage);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    // ---- menus ---------------------------------------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    public static partial int GetMenuItemCount(IntPtr hMenu);

    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMenuItemInfo(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFOW lpmii);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndMenu();

    // ---- clipboard -----------------------------------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll")]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    public static partial UIntPtr GlobalSize(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    public static partial uint DragQueryFile(IntPtr hDrop, uint iFile, char* lpszFile, uint cch);

    // ---- volumes -------------------------------------------------------------------------------

    public const uint DRIVE_UNKNOWN = 0;
    public const uint DRIVE_NO_ROOT_DIR = 1;
    public const uint DRIVE_REMOVABLE = 2;
    public const uint DRIVE_FIXED = 3;
    public const uint DRIVE_REMOTE = 4;
    public const uint DRIVE_CDROM = 5;
    public const uint DRIVE_RAMDISK = 6;

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string lpRootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string lpszFileName, char* lpszVolumePathName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, char* lpszVolumeName, uint cchBufferLength);

    // ---- final paths ---------------------------------------------------------------------------

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_NAME_NORMALIZED = 0x0;
    public const uint VOLUME_NAME_DOS = 0x0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath,
        uint dwFlags);

    // ---- COM / shell ---------------------------------------------------------------------------

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    public static partial int SHBindToParent(IntPtr pidl, in Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetDesktopFolder(out IntPtr ppshf);

    [LibraryImport("shell32.dll")]
    public static partial IntPtr ILFindLastID(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    public static partial int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    /// <summary>KF_FLAG_DONT_VERIFY: return the path without checking (or creating) the folder.</summary>
    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHCreateItemFromParsingName(
        string pszPath, IBindCtx? pbc, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHGetPropertyStoreFromParsingName(string pszPath, IntPtr pbc, int flags, in Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SHGetFileInfoW")]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFOW lpExecInfo);

    // ---- COM local server (Explorer drop targets) ------------------------------------------

    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint REGCLS_MULTIPLEUSE = 1;

    public const uint DROPEFFECT_NONE = 0;
    public const uint DROPEFFECT_COPY = 1;

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoRegisterClassObject(
        in Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    public static partial int CoRevokeClassObject(uint dwRegister);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHCreateShellItemArrayFromDataObject(
        IDataObject pdo, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    public static bool IsKeyDown(int virtualKey) => GetKeyState(virtualKey) < 0;

    public static void ReleaseComObject(object? o)
    {
        if (o is not null && Marshal.IsComObject(o))
        {
            try
            {
                Marshal.FinalReleaseComObject(o);
            }
            catch (Exception)
            {
                // Already released or not an RCW; nothing to do.
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE(int cx, int cy)
{
    public int cx = cx;
    public int cy = cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DIBSECTION
{
    public BITMAP dsBm;
    public BITMAPINFOHEADER dsBmih;
    public fixed uint dsBitfields[3];
    public IntPtr dshSection;
    public uint dsOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MENUITEMINFOW
{
    public uint cbSize;
    public uint fMask;
    public uint fType;
    public uint fState;
    public uint wID;
    public IntPtr hSubMenu;
    public IntPtr hbmpChecked;
    public IntPtr hbmpUnchecked;
    public UIntPtr dwItemData;
    public IntPtr dwTypeData;
    public uint cch;
    public IntPtr hbmpItem;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMINVOKECOMMANDINFOEX
{
    public uint cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon;
    public IntPtr lpTitle;
    public IntPtr lpVerbW;
    public IntPtr lpParametersW;
    public IntPtr lpDirectoryW;
    public IntPtr lpTitleW;
    public POINT ptInvoke;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFOW
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHELLEXECUTEINFOW
{
    public int cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public string? lpVerb;
    public string? lpFile;
    public string? lpParameters;
    public string? lpDirectory;
    public int nShow;
    public IntPtr hInstApp;
    public IntPtr lpIDList;
    public string? lpClass;
    public IntPtr hkeyClass;
    public uint dwHotKey;
    public IntPtr hIconOrMonitor;
    public IntPtr hProcess;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WIN32_FIND_DATAW
{
    public uint dwFileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;
    public uint dwReserved1;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string cFileName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string cAlternateFileName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY(Guid fmtid, uint pid)
{
    public Guid fmtid = fmtid;
    public uint pid = pid;
}

/// <summary>PROPVARIANT (24 bytes on x64). Only the scalar members Cabinette reads are mapped.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PROPVARIANT
{
    [FieldOffset(0)]
    public ushort vt;

    [FieldOffset(8)]
    public byte bVal;

    [FieldOffset(8)]
    public short iVal;

    [FieldOffset(8)]
    public ushort uiVal;

    [FieldOffset(8)]
    public int lVal;

    [FieldOffset(8)]
    public uint ulVal;

    [FieldOffset(8)]
    public long hVal;

    [FieldOffset(8)]
    public ulong uhVal;

    [FieldOffset(8)]
    public double dblVal;
}
