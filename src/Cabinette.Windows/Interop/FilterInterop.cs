using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Cabinette.Windows.Interop;

// IFilter (filter.h / filtererr.h) and the interfaces that load one. Classic [ComImport] declarations: every method is
// listed in vtable order.

[Flags]
internal enum IFILTER_INIT : uint
{
    CanonParagraphs = 1,
    HardLineBreaks = 2,
    CanonHyphens = 4,
    CanonSpaces = 8,
    ApplyIndexAttributes = 16,
    ApplyOtherAttributes = 32,
    IndexingOnly = 64,
    SearchLinks = 128,
    FilterOwnedValueOk = 512,
}

internal enum CHUNK_BREAKTYPE
{
    NoBreak = 0,
    EndOfWord = 1,
    EndOfSentence = 2,
    EndOfParagraph = 3,
    EndOfChapter = 4,
}

[Flags]
internal enum CHUNKSTATE
{
    Text = 1,
    Value = 2,
    FilterOwnedValue = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPSPEC
{
    public uint ulKind;

    /// <summary>PROPID or LPOLESTR (a union).</summary>
    public IntPtr union;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FULLPROPSPEC
{
    public Guid guidPropSet;
    public PROPSPEC psProperty;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STAT_CHUNK
{
    public uint idChunk;
    public CHUNK_BREAKTYPE breakType;
    public CHUNKSTATE flags;
    public uint locale;
    public FULLPROPSPEC attribute;
    public uint idChunkSource;
    public uint cwcStartSource;
    public uint cwcLenSource;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILTERREGION
{
    public uint idChunk;
    public uint cwcStart;
    public uint cwcExtent;
}

internal static class FilterHResults
{
    public const int EndOfChunks = unchecked((int)0x80041700);
    public const int NoMoreText = unchecked((int)0x80041701);
    public const int NoMoreValues = unchecked((int)0x80041702);
    public const int Access = unchecked((int)0x80041703);
    public const int NoText = unchecked((int)0x80041705);
    public const int EmbeddingUnavailable = unchecked((int)0x80041707);
    public const int LinkUnavailable = unchecked((int)0x80041708);
    public const int LastText = 0x00041709;
    public const int Password = unchecked((int)0x8004170B);
    public const int UnknownFormat = unchecked((int)0x8004170C);

    public const int NotImplemented = unchecked((int)0x80004001);
    public const int NoInterface = unchecked((int)0x80004002);

    /// <summary>True for the errors that mean the file itself can't be opened (missing, locked, no access).</summary>
    public static bool IsFileAccessError(int hr) => hr switch
    {
        Access => true,
        unchecked((int)0x80070002) or unchecked((int)0x80070003) => true, // file or path not found
        unchecked((int)0x80070005) => true, // access denied
        unchecked((int)0x80070020) or unchecked((int)0x80070021) => true, // sharing or lock violation
        unchecked((int)0x80030002) or unchecked((int)0x80030003) => true, // STG_E_FILENOTFOUND, STG_E_PATHNOTFOUND
        unchecked((int)0x80030005) => true, // STG_E_ACCESSDENIED
        unchecked((int)0x80030020) or unchecked((int)0x80030021) => true, // STG_E_SHAREVIOLATION, STG_E_LOCKVIOLATION
        _ => false,
    };
}

[ComImport]
[Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFilter
{
    [PreserveSig]
    int Init(IFILTER_INIT grfFlags, uint cAttributes, IntPtr aAttributes, out uint pFlags);

    [PreserveSig]
    int GetChunk(out STAT_CHUNK pStat);

    [PreserveSig]
    int GetText(ref uint pcwcBuffer, IntPtr awcBuffer);

    [PreserveSig]
    int GetValue(out IntPtr ppPropValue);

    [PreserveSig]
    int BindRegion(FILTERREGION origPos, in Guid riid, out IntPtr ppunk);
}

[ComImport]
[Guid("00000109-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistStream
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load(IStream pStm);

    [PreserveSig]
    int Save(IStream pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty);

    [PreserveSig]
    int GetSizeMax(out long pcbSize);
}

[ComImport]
[Guid("0000010b-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFileNative
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

    [PreserveSig]
    int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    [PreserveSig]
    int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);

    [PreserveSig]
    int GetCurFile(out IntPtr ppszFileName);
}

internal static class FilterNativeMethods
{
    public const uint ClsctxInprocServer = 0x1;
    public const uint StgmRead = 0x0;
    public const uint StgmShareDenyNone = 0x40;

    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHCreateStreamOnFileEx(
        string pszFile,
        uint grfMode,
        uint dwAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool fCreate,
        IntPtr pstmTemplate,
        out IStream? ppstm);

    public const uint SemFailCriticalErrors = 0x0001;
    public const uint SemNoGpFaultErrorBox = 0x0002;
    public const uint SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint SetErrorMode(uint uMode);
}
