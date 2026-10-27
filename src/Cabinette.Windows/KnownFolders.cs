using System.Runtime.InteropServices;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>Windows known folders (SHGetKnownFolderPath), so a folder the user moved elsewhere is found where it is now.</summary>
public static class KnownFolders
{
    /// <summary>FOLDERID_Downloads.</summary>
    public static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// The current user's Downloads folder, wherever it was moved (Properties › Location), or null when Windows has none.
    /// Only reads the path: the folder itself is neither checked nor created.
    /// </summary>
    public static string? TryGetDownloads() => TryGet(DownloadsId);

    private static string? TryGet(Guid folderId)
    {
        var hr = SHGetKnownFolderPath(folderId, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out var buffer);
        try
        {
            if (hr < 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            var path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            // Freed even when the call failed (the documentation asks for it).
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
