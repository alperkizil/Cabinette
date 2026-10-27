using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Cabinette.Core.Abstractions;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;
using static Cabinette.Windows.Interop.PropertyHandlerInterop;

namespace Cabinette.Windows;

/// <summary>
/// The date a photo or video was taken, from the Windows property system (the "Date taken" and "Media created" Explorer shows):
/// <c>System.Photo.DateTaken</c> (EXIF DateTimeOriginal of JPEG, TIFF, HEIC and camera raw files), else
/// <c>System.Media.DateEncoded</c> (the creation time of MP4, MOV, WMV and other videos). Nothing is decoded: the property handler
/// only reads the file's metadata. Thread-safe: every call opens and releases its own property store. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// The property system gives both as a UTC FILETIME. For EXIF, which stores the camera's wall-clock time without a zone, Windows
/// takes that time as this PC's local time (with the daylight saving rule of that date); the result is the matching instant, so
/// its local time is what the camera showed. MP4 and QuickTime store UTC.
/// </para>
/// <para>
/// The file type's property handler is loaded directly and given a read-only stream on the file, as the Windows Search indexer
/// does: going through the shell namespace (SHGetPropertyStoreFromParsingName) gives the same values but costs about 7 ms a file
/// on this PC, against about 1 ms. The shell is still used when the handler can't be loaded that way. Cloud placeholders whose
/// content isn't on this PC are skipped, so reading never downloads them.
/// </para>
/// </remarks>
public sealed class ShellDateTakenReader : IDateTakenReader
{
    private const int GPS_BESTEFFORT = 0x40;
    private const ushort VT_FILETIME = 64;

    private static readonly PROPERTYKEY PhotoDateTaken = new(new Guid("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), 36867);
    private static readonly PROPERTYKEY MediaDateEncoded = new(new Guid("2E4B640D-5019-46D8-8881-55414CC5CAA0"), 100);

    /// <summary>
    /// Earlier values are "not set" rather than a date: a zero FILETIME (1601) or a zero MP4/QuickTime creation time, which counts
    /// from 1904-01-01.
    /// </summary>
    private static readonly DateTime EarliestUtc = new(1904, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Just after a zero FILETIME (1601-01-01), which means "not set" for photos too.</summary>
    private static readonly DateTime EarliestPhotoUtc = new(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The time the camera showed, with the offset it had. An EXIF time has no zone: Windows turns it into UTC with this PC's zone at
    /// the moment of reading, so converting back with the same zone right away gives the camera's wall-clock time again. Kept with
    /// its offset, it no longer moves when the PC's zone changes later (a travelling laptop, a corrected zone, a backup restored
    /// elsewhere). Videos (stored as UTC) follow the same rule: their local time when read.
    /// </summary>
    internal static DateTimeOffset AsCameraTime(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(TimeZoneInfo.Local.GetUtcOffset(utc));

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: a cloud file (OneDrive "online-only") whose content would be downloaded.</summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Property handler per extension (looking it up reads the registry, about 1 ms); <see cref="Guid.Empty"/> = none.</summary>
    private static readonly ConcurrentDictionary<string, Guid> HandlerByExtension = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _shellOnly;

    public ShellDateTakenReader()
    {
    }

    /// <summary>Tests: <paramref name="shellOnly"/> always goes through the shell namespace, to compare both ways.</summary>
    internal ShellDateTakenReader(bool shellOnly) => _shellOnly = shellOnly;

    public DateTimeOffset? ReadDateTaken(string path)
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
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Offline | RecallOnDataAccess)) != 0)
            {
                return null;
            }

            // Callers use background threads; make sure COM is initialized (RPC_E_CHANGED_MODE = already STA, fine).
            uninitialize = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED) >= 0;

            var handler = _shellOnly ? HandlerResult.NotLoaded : OpenHandler(fullPath, out store);
            if (handler == HandlerResult.None || handler == HandlerResult.Failed)
            {
                // No handler for this type (so no date), or it could not read this file (the shell would use the same one).
                return null;
            }

            if (handler == HandlerResult.NotLoaded && (store = OpenThroughShell(fullPath)) is null)
            {
                return null;
            }

            // The 1904 floor is QuickTime's zero date, so it applies to videos only: a scanned photo may be dated 1900.
            var utc = ReadFileTime(store!, PhotoDateTaken, EarliestPhotoUtc) ?? ReadFileTime(store!, MediaDateEncoded, EarliestUtc);
            return utc is { } value ? AsCameraTime(value) : null;
        }
        catch (Exception)
        {
            // Missing, inaccessible, or a handler that failed.
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

    private enum HandlerResult
    {
        /// <summary>The handler read the file: the store is set.</summary>
        Opened,

        /// <summary>No property handler is registered for this type.</summary>
        None,

        /// <summary>The handler was loaded but could not read the file.</summary>
        Failed,

        /// <summary>The handler could not be loaded this way; the shell may still manage.</summary>
        NotLoaded,
    }

    private static HandlerResult OpenHandler(string fullPath, out IPropertyStore? store)
    {
        store = null;
        var extension = Path.GetExtension(fullPath);
        if (extension.Length == 0)
        {
            return HandlerResult.NotLoaded;
        }

        var clsid = HandlerByExtension.GetOrAdd(extension, static (_, p) => PSLookupPropertyHandlerCLSID(p, out var found) >= 0 ? found : Guid.Empty,
            fullPath);
        if (clsid == Guid.Empty)
        {
            return HandlerResult.None;
        }

        if (CoCreateInstance(clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ShellIids.IPropertyStore, out var ppv) < 0 || ppv == IntPtr.Zero)
        {
            return HandlerResult.NotLoaded;
        }

        object handler;
        try
        {
            handler = Marshal.GetObjectForIUnknown(ppv);
        }
        finally
        {
            Marshal.Release(ppv);
        }

        var result = HandlerResult.NotLoaded;
        try
        {
            if (handler is IInitializeWithStream withStream)
            {
                if (SHCreateStreamOnFileEx(fullPath, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out var stream) < 0
                    || stream == IntPtr.Zero)
                {
                    result = HandlerResult.Failed;
                    return result;
                }

                try
                {
                    result = withStream.Initialize(stream, STGM_READ) >= 0 ? HandlerResult.Opened : HandlerResult.Failed;
                }
                finally
                {
                    Marshal.Release(stream);
                }
            }
            else if (handler is IInitializeWithFile withFile)
            {
                result = withFile.Initialize(fullPath, STGM_READ) >= 0 ? HandlerResult.Opened : HandlerResult.Failed;
            }

            if (result == HandlerResult.Opened)
            {
                store = (IPropertyStore)handler;
            }

            return result;
        }
        finally
        {
            if (result != HandlerResult.Opened)
            {
                ReleaseComObject(handler);
            }
        }
    }

    private static IPropertyStore? OpenThroughShell(string fullPath)
    {
        if (SHGetPropertyStoreFromParsingName(fullPath, IntPtr.Zero, GPS_BESTEFFORT, ShellIids.IPropertyStore, out var ppv) < 0
            || ppv == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (IPropertyStore)Marshal.GetObjectForIUnknown(ppv);
        }
        finally
        {
            Marshal.Release(ppv);
        }
    }

    /// <summary>A FILETIME property as a UTC time, or null when it is missing, of another type or not a real date.</summary>
    private static DateTime? ReadFileTime(IPropertyStore store, PROPERTYKEY key, DateTime earliestUtc)
    {
        if (store.GetValue(key, out var pv) < 0)
        {
            return null;
        }

        try
        {
            if (pv.vt != VT_FILETIME || pv.uhVal > (ulong)DateTime.MaxValue.ToFileTimeUtc())
            {
                return null;
            }

            var utc = DateTime.FromFileTimeUtc((long)pv.uhVal);
            return utc >= earliestUtc ? utc : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }
}
