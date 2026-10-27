using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media.Imaging;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Interop;

/// <summary>Shell image extraction via IShellItemImageFactory. Call on an STA thread.</summary>
internal static class ShellImages
{
    private const string FileSystemBindDataKey = "File System Bind Data";
    private const int STGM_CREATE = 0x00001000;

    /// <summary>The shell thumbnail (no icon fallback), or null when the shell has none.</summary>
    public static BitmapSource? GetThumbnail(string path, int sizePx)
        => GetImage(path, null, sizePx, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk);

    /// <summary>The icon of an existing file or folder (per-file icons such as .exe are respected).</summary>
    public static BitmapSource? GetIcon(string path, int sizePx)
        => GetImage(path, null, sizePx, SIIGBF.IconOnly);

    /// <summary>The generic icon for a file type, without touching the disk (works for files that don't exist).</summary>
    public static BitmapSource? GetIconForExtension(string extension, bool isFolder, int sizePx)
    {
        // A "simple" shell item for a fake path: the bind data tells the parser what the item is, so nothing is read from disk.
        var fakeName = isFolder ? "Cabinette icon probe" : "Cabinette icon probe" + extension;
        var fakePath = Path.Combine(Path.GetTempPath(), fakeName);

        IBindCtx? bindCtx = null;
        try
        {
            if (CreateBindCtx(0, out bindCtx) < 0 || bindCtx is null)
            {
                return FallbackIcon(extension, isFolder);
            }

            var options = new BIND_OPTS { cbStruct = Marshal.SizeOf<BIND_OPTS>(), grfMode = STGM_CREATE };
            bindCtx.SetBindOptions(ref options);

            var findData = new WIN32_FIND_DATAW
            {
                dwFileAttributes = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                cFileName = fakeName,
                cAlternateFileName = "",
            };
            bindCtx.RegisterObjectParam(FileSystemBindDataKey, new FileSystemBindData(findData));

            return GetImage(fakePath, bindCtx, sizePx, SIIGBF.IconOnly) ?? FallbackIcon(extension, isFolder);
        }
        catch (Exception)
        {
            return FallbackIcon(extension, isFolder);
        }
        finally
        {
            ReleaseComObject(bindCtx);
        }
    }

    private static BitmapSource? GetImage(string path, IBindCtx? bindCtx, int sizePx, SIIGBF flags)
    {
        object? item = null;
        var hbitmap = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(path, bindCtx, ShellIids.IShellItemImageFactory, out item) < 0
                || item is not IShellItemImageFactory factory)
            {
                return null;
            }

            if (factory.GetImage(new SIZE(sizePx, sizePx), flags, out hbitmap) < 0 || hbitmap == IntPtr.Zero)
            {
                return null;
            }

            var bitmap = BitmapInterop.FromHBitmap(hbitmap);
            return bitmap is null ? null : BitmapInterop.DownscaleToFit(bitmap, sizePx);
        }
        catch (Exception)
        {
            // Broken thumbnail handlers throw all kinds of COM errors; treat them all as "no image".
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }

            ReleaseComObject(item);
        }
    }

    /// <summary>32px icon from SHGetFileInfo, used only if the image factory route fails.</summary>
    private static BitmapSource? FallbackIcon(string extension, bool isFolder)
    {
        var info = default(SHFILEINFOW);
        var result = SHGetFileInfo(
            isFolder ? "folder" : (string.IsNullOrEmpty(extension) ? "file" : extension),
            isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFOW>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return BitmapInterop.FromHIcon(info.hIcon);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileSystemBindData(WIN32_FIND_DATAW data) : IFileSystemBindData
    {
        private WIN32_FIND_DATAW _data = data;

        public int SetFindData(in WIN32_FIND_DATAW pfd)
        {
            _data = pfd;
            return S_OK;
        }

        public int GetFindData(out WIN32_FIND_DATAW pfd)
        {
            pfd = _data;
            return S_OK;
        }
    }
}
