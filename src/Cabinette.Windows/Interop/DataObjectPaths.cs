using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using static Cabinette.Windows.Interop.NativeMethods;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Cabinette.Windows.Interop;

/// <summary>
/// The file-system paths in a drag and drop data object: CF_HDROP first, then the shell's item list
/// (SHCreateShellItemArrayFromDataObject) for data objects that have no CF_HDROP. Items without a file-system path
/// (Control Panel entries, phones, items inside zip folders, ...) are skipped. Never throws.
/// </summary>
internal static class DataObjectPaths
{
    public static IReadOnlyList<string> Read(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var paths = Clean(ReadFileDrop(data));
        return paths.Count > 0 ? paths : Clean(ReadShellItems(data));
    }

    /// <summary>The CF_HDROP list, or an empty list when the data object has none.</summary>
    internal static List<string> ReadFileDrop(IDataObject data)
    {
        var format = new FORMATETC
        {
            cfFormat = unchecked((short)CF_HDROP),
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };

        STGMEDIUM medium;
        try
        {
            data.GetData(ref format, out medium);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // DV_E_FORMATETC and friends: no file list in this data object.
            return [];
        }

        try
        {
            return medium.tymed == TYMED.TYMED_HGLOBAL ? HDrop.ReadPaths(medium.unionmember) : [];
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>File-system paths of the shell items in the data object (its "Shell IDList Array"), or an empty list.</summary>
    internal static List<string> ReadShellItems(IDataObject data)
    {
        object? arrayObject = null;
        try
        {
            if (SHCreateShellItemArrayFromDataObject(data, ShellIids.IShellItemArray, out arrayObject) < 0 ||
                arrayObject is not IShellItemArray array ||
                array.GetCount(out var count) < 0)
            {
                return [];
            }

            var paths = new List<string>((int)Math.Min(count, 10_000));
            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out var item) < 0 || item is null)
                {
                    continue;
                }

                try
                {
                    if (item.GetDisplayName(SIGDN.FileSysPath, out var name) >= 0 && name != IntPtr.Zero)
                    {
                        try
                        {
                            if (Marshal.PtrToStringUni(name) is { Length: > 0 } path)
                            {
                                paths.Add(path);
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(name);
                        }
                    }
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }

            return paths;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
        finally
        {
            ReleaseComObject(arrayObject);
        }
    }

    /// <summary>Only fully qualified paths (<c>C:\…</c>, <c>\\server\share\…</c>), each once, in their original order.</summary>
    internal static List<string> Clean(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && seen.Add(path))
            {
                clean.Add(path);
            }
        }

        return clean;
    }
}
