using System.Runtime.InteropServices;

namespace Cabinette.Windows.Interop;

// Classic [ComImport] declarations. Built-in COM interop requires every interface to redeclare the methods of the
// interfaces it extends (vtable order), so IContextMenu2/3 repeat the IContextMenu methods instead of inheriting.

internal static class ShellIids
{
    public static readonly Guid IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    public static readonly Guid IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    public static readonly Guid IShellItemArray = new("b63ea76d-1f85-456f-a19c-48159efa858b");
    public static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
}

[Flags]
internal enum SIIGBF
{
    ResizeToFit = 0x00,
    BiggerSizeOk = 0x01,
    MemoryOnly = 0x02,
    IconOnly = 0x04,
    ThumbnailOnly = 0x08,
    InCacheOnly = 0x10,
}

internal enum SIGDN : uint
{
    NormalDisplay = 0,
    FileSysPath = 0x80058000,
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetParent(out IntPtr ppsi);

    [PreserveSig]
    int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IntPtr psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppvOut);

    [PreserveSig]
    int GetPropertyStore(int flags, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(IntPtr keyType, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out IShellItem ppsi);

    [PreserveSig]
    int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);

    [PreserveSig]
    int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

    [PreserveSig]
    int BindToObject(IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int BindToStorage(IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

    [PreserveSig]
    int CreateViewObject(IntPtr hwndOwner, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);

    [PreserveSig]
    int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, in Guid riid, IntPtr rgfReserved, out IntPtr ppv);

    [PreserveSig]
    int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

    [PreserveSig]
    int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

[ComImport]
[Guid("000214E4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

    [PreserveSig]
    int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[ComImport]
[Guid("000214F4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

    [PreserveSig]
    int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

    [PreserveSig]
    int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport]
[Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

    [PreserveSig]
    int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

    [PreserveSig]
    int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

    [PreserveSig]
    int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    [PreserveSig]
    int GetValue(in PROPERTYKEY key, out PROPVARIANT pv);

    [PreserveSig]
    int SetValue(in PROPERTYKEY key, in PROPVARIANT propvar);

    [PreserveSig]
    int Commit();
}

/// <summary>Lets the shell parse a path that does not exist ("simple" item), so file-type icons work for missing files.</summary>
[ComImport]
[Guid("01E18D10-4D8B-11d2-855D-006008059367")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileSystemBindData
{
    [PreserveSig]
    int SetFindData(in WIN32_FIND_DATAW pfd);

    [PreserveSig]
    int GetFindData(out WIN32_FIND_DATAW pfd);
}

[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig]
    int Advise(IFileOperationProgressSink pfops, out uint pdwCookie);

    [PreserveSig]
    int Unadvise(uint dwCookie);

    [PreserveSig]
    int SetOperationFlags(uint dwOperationFlags);

    [PreserveSig]
    int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);

    [PreserveSig]
    int SetProgressDialog(IntPtr popd);

    [PreserveSig]
    int SetProperties(IntPtr pproparray);

    [PreserveSig]
    int SetOwnerWindow(IntPtr hwndOwner);

    [PreserveSig]
    int ApplyPropertiesToItem(IShellItem psiItem);

    [PreserveSig]
    int ApplyPropertiesToItems(IntPtr punkItems);

    [PreserveSig]
    int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);

    [PreserveSig]
    int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

    [PreserveSig]
    int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);

    [PreserveSig]
    int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);

    [PreserveSig]
    int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);

    [PreserveSig]
    int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);

    [PreserveSig]
    int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);

    [PreserveSig]
    int DeleteItems(IntPtr punkItems);

    [PreserveSig]
    int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);

    [PreserveSig]
    int PerformOperations();

    [PreserveSig]
    int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

[ComImport]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int hrResult);

    [PreserveSig]
    int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

    [PreserveSig]
    int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);

    [PreserveSig]
    int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

    [PreserveSig]
    int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);

    [PreserveSig]
    int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

    [PreserveSig]
    int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);

    [PreserveSig]
    int PreDeleteItem(uint dwFlags, IShellItem psiItem);

    [PreserveSig]
    int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);

    [PreserveSig]
    int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

    [PreserveSig]
    int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);

    [PreserveSig]
    int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}
