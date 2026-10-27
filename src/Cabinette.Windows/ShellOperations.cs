using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>Opening files with their default app and revealing them in Explorer.</summary>
public sealed class ShellOperations : IShellOperations
{
    private const int ERROR_CANCELLED = 1223;

    /// <summary>
    /// Opens the file (or folder) with its default handler via ShellExecuteEx. Unassociated types get Windows'
    /// "How do you want to open this file?" picker.
    /// </summary>
    /// <exception cref="FileNotFoundException">The path does not exist.</exception>
    /// <exception cref="Win32Exception">The shell could not open it.</exception>
    public void OpenWithDefaultApp(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The file does not exist.", fullPath);
        }

        Execute(fullPath, verb: null);
    }

    /// <summary>
    /// Opens Explorer with the item selected. If the item is gone, opens the closest folder that still exists.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">Neither the item nor any of its parent folders exist.</exception>
    public void ShowInExplorer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        if ((File.Exists(fullPath) || Directory.Exists(fullPath)) && TrySelectInExplorer(fullPath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            folder = Path.GetDirectoryName(folder);
        }

        if (string.IsNullOrEmpty(folder))
        {
            throw new DirectoryNotFoundException($"Neither \"{fullPath}\" nor any of its folders exist.");
        }

        Execute(folder, verb: null);
    }

    private static bool TrySelectInExplorer(string fullPath)
    {
        if (SHParseDisplayName(fullPath, IntPtr.Zero, out var pidl, 0, out _) < 0 || pidl == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // A full item PIDL with cidl = 0 opens its parent folder with the item selected.
            return SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static void Execute(string file, string? verb)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = 0,
            lpVerb = verb,
            lpFile = file,
            lpDirectory = Path.GetDirectoryName(file),
            nShow = SW_SHOWNORMAL,
        };

        if (!ShellExecuteEx(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_CANCELLED)
            {
                throw new Win32Exception(error);
            }
        }
    }
}
