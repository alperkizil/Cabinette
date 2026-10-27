using System.Runtime.InteropServices;

namespace Cabinette.Windows.Interop;

/// <summary>
/// Loading a file type's property handler directly (the way the Windows Search indexer does), without the shell namespace:
/// look up its CLSID, create it in-process and give it a read-only stream on the file.
/// </summary>
internal static partial class PropertyHandlerInterop
{
    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint STGM_READ = 0x0;
    public const uint STGM_SHARE_DENY_NONE = 0x40;

    /// <summary>The property handler registered for the file's type (HKCR\...\PropertySystem\PropertyHandlers).</summary>
    [LibraryImport("propsys.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PSLookupPropertyHandlerCLSID(string pszFilePath, out Guid pclsid);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    /// <summary>A read stream on a file. With STGM_READ the file can still be renamed, deleted and written meanwhile.</summary>
    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool fCreate, IntPtr pstmTemplate, out IntPtr ppstm);
}

[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    /// <param name="pstream">An IStream pointer (the handler adds its own reference when it keeps it).</param>
    [PreserveSig]
    int Initialize(IntPtr pstream, uint grfMode);
}

[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    [PreserveSig]
    int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}
