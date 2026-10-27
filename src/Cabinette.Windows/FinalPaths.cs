using System.IO;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>
/// The path Windows itself uses for a file or folder (GetFinalPathNameByHandle): junctions and symbolic links followed, 8.3
/// short names expanded, subst drives replaced by the folder they stand for, mapped network drives by their UNC path. Two
/// spellings of one folder give the same final path, so a guard that compares folders can't be walked around.
/// </summary>
public static class FinalPaths
{
    /// <summary>
    /// The final path of <paramref name="path"/>, without the "\\?\" prefix and without a trailing separator (except for a
    /// root). A part that doesn't exist yet is resolved up to its deepest existing folder and kept as written below it. Null
    /// when nothing of it can be opened (an invalid path, a drive that isn't there). Opens folders without reading them.
    /// </summary>
    public static string? TryResolve(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }

        var missing = new Stack<string>();
        var current = Path.TrimEndingDirectorySeparator(full);
        while (!string.IsNullOrEmpty(current))
        {
            if (TryResolveExisting(current) is { } resolved)
            {
                return Trim(missing.Count == 0 ? resolved : Path.Combine([resolved, .. missing]));
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            missing.Push(Path.GetFileName(current));
            current = parent;
        }

        return null;
    }

    private static unsafe string? TryResolveExisting(string path)
    {
        // No access rights asked: only the name is wanted. Backup semantics let a folder be opened at all.
        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
            {
                length = GetFinalPathNameByHandle(handle, chars, (uint)buffer.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return StripPrefix(new string(buffer, 0, (int)length));
            }

            // Too small: the answer is the size needed, including the terminating null.
            buffer = new char[length];
        }
    }

    /// <summary>"\\?\C:\x" → "C:\x", "\\?\UNC\server\share\x" → "\\server\share\x".</summary>
    internal static string StripPrefix(string path)
    {
        const string Unc = @"\\?\UNC\";
        const string Local = @"\\?\";
        if (path.StartsWith(Unc, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[Unc.Length..];
        }

        return path.StartsWith(Local, StringComparison.Ordinal) ? path[Local.Length..] : path;
    }

    private static string Trim(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
