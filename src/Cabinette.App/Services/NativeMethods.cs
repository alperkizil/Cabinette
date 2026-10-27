using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cabinette.App.Services;

/// <summary>The few Win32 calls the app needs itself (window activation, the instance pipe, tray icon sizes).</summary>
internal static class NativeMethods
{
    public const int SW_RESTORE = 9;
    public const int SM_CXICON = 11;
    public const int SM_CXSMICON = 49;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
