using System.Collections.Frozen;
using System.IO;
using System.Runtime.InteropServices;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>
/// Duration, pixel size and document page count from the Windows property system (the same values Explorer's Details pane shows).
/// Nothing is decoded. Thread-safe: every call opens and releases its own property store.
/// </summary>
public sealed class ShellMediaInfoService : IMediaInfoService
{
    private const int GPS_BESTEFFORT = 0x40;

    private const ushort VT_I2 = 2;
    private const ushort VT_I4 = 3;
    private const ushort VT_R8 = 5;
    private const ushort VT_I1 = 16;
    private const ushort VT_UI1 = 17;
    private const ushort VT_UI2 = 18;
    private const ushort VT_UI4 = 19;
    private const ushort VT_I8 = 20;
    private const ushort VT_UI8 = 21;
    private const ushort VT_INT = 22;
    private const ushort VT_UINT = 23;

    private static readonly PROPERTYKEY MediaDuration = new(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PROPERTYKEY VideoFrameWidth = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PROPERTYKEY VideoFrameHeight = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PROPERTYKEY ImageHorizontalSize = new(new Guid("6444048F-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PROPERTYKEY ImageVerticalSize = new(new Guid("6444048F-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PROPERTYKEY DocumentPageCount = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 14);

    /// <summary>
    /// Only these types have real pages. Other files can carry the same property with a different meaning:
    /// .msi/.msp store the minimum Windows Installer version in it (200 = 2.0), which showed up as "200 pages".
    /// </summary>
    private static readonly FrozenSet<string> PagedExtensions = FrozenSet.ToFrozenSet(
        [".pdf", ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".odt", ".rtf", ".xps", ".oxps", ".pages",
         ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp", ".key", ".djvu"],
        StringComparer.OrdinalIgnoreCase);

    internal static bool HasPages(string path) => PagedExtensions.Contains(Path.GetExtension(path));

    public MediaInfo? GetMediaInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var uninitialize = false;
        IPropertyStore? store = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            // Callers use background threads; make sure COM is initialized (RPC_E_CHANGED_MODE = already STA, fine).
            uninitialize = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED) >= 0;

            if (SHGetPropertyStoreFromParsingName(fullPath, IntPtr.Zero, GPS_BESTEFFORT, ShellIids.IPropertyStore, out var ppv) < 0
                || ppv == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                store = (IPropertyStore)Marshal.GetObjectForIUnknown(ppv);
            }
            finally
            {
                Marshal.Release(ppv);
            }

            TimeSpan? duration = ReadUInt64(store, MediaDuration) is { } ticks and > 0 ? TimeSpan.FromTicks((long)ticks) : null;
            var width = ReadPositiveInt(store, VideoFrameWidth) ?? ReadPositiveInt(store, ImageHorizontalSize);
            var height = ReadPositiveInt(store, VideoFrameHeight) ?? ReadPositiveInt(store, ImageVerticalSize);

            var pageCount = HasPages(fullPath) ? ReadPositiveInt(store, DocumentPageCount) : null;
            if (pageCount is null && PdfDocuments.IsPdf(fullPath))
            {
                // Stock Windows has no PDF property handler; ask the built-in PDF engine.
                pageCount = PdfDocuments.GetPageCount(fullPath);
            }

            if (duration is null && width is null && height is null && pageCount is null)
            {
                return null;
            }

            return new MediaInfo(duration, width, height, pageCount);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(store);
            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static int? ReadPositiveInt(IPropertyStore store, PROPERTYKEY key)
        => ReadUInt64(store, key) is { } v and > 0 and <= int.MaxValue ? (int)v : null;

    private static ulong? ReadUInt64(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(key, out var pv) < 0)
        {
            return null;
        }

        try
        {
            return pv.vt switch
            {
                VT_UI1 => pv.bVal,
                VT_UI2 => pv.uiVal,
                VT_UI4 or VT_UINT => pv.ulVal,
                VT_UI8 => pv.uhVal,
                VT_I1 => pv.bVal <= sbyte.MaxValue ? pv.bVal : null,
                VT_I2 => pv.iVal >= 0 ? (ulong)pv.iVal : null,
                VT_I4 or VT_INT => pv.lVal >= 0 ? (ulong)pv.lVal : null,
                VT_I8 => pv.hVal >= 0 ? (ulong)pv.hVal : null,
                VT_R8 => pv.dblVal is >= 0 and < ulong.MaxValue ? (ulong)pv.dblVal : null,
                _ => null,
            };
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }
}
