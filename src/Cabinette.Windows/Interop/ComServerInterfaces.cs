using System.Runtime.InteropServices;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Cabinette.Windows.Interop;

// The two COM interfaces Cabinette implements itself (as a local server for the Explorer entries). Classic [ComImport]
// declarations, like ShellComInterfaces.cs: the CLR exposes a managed class that implements them through a COM callable
// wrapper, which Explorer reaches across processes.

internal static class ComIids
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IDropTarget = new("00000122-0000-0000-C000-000000000046");
}

/// <summary>POINTL: a point in screen coordinates (two LONGs), passed by value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

/// <summary>OLE drag and drop target. Explorer's DropTarget verbs call DragEnter, then Drop, with the whole selection.</summary>
[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    [PreserveSig]
    int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [PreserveSig]
    int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
}

/// <summary>Creates instances of a COM class. Registered with CoRegisterClassObject so COM can hand out drop targets.</summary>
[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, in Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
