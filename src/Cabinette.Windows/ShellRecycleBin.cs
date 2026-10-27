using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Cabinette.Core.Abstractions;
using Cabinette.Windows.Interop;
using Microsoft.Win32;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows;

/// <summary>
/// Sends files and folders to the Recycle Bin with IFileOperation, without any UI. Items that the shell could only
/// delete permanently (no Recycle Bin on that drive, item too large) are NOT deleted: the shell is told to skip them,
/// everything else is still recycled, and a <see cref="RecycleBinUnavailableException"/> lists the skipped items so the
/// caller can decide what to do. Other failures throw an <see cref="IOException"/>.
/// Callable from any thread: IFileOperation needs STA, so calls from thread-pool (MTA) threads run on a short-lived
/// dedicated STA thread and the caller blocks until it finishes.
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;

    /// <summary>How long a volume's answer is reused: Core asks once per file of a large delete.</summary>
    private static readonly TimeSpan CanRecycleCacheTime = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, VolumeInfo> VolumeCache = new(StringComparer.OrdinalIgnoreCase);

    public void SendToRecycleBin(IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var existing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                continue;
            }

            if ((File.Exists(full) || Directory.Exists(full)) && seen.Add(full))
            {
                existing.Add(full);
            }
        }

        if (existing.Count == 0)
        {
            return;
        }

        // IFileOperation only works in a single-threaded apartment.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Recycle(existing);
            return;
        }

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Recycle(existing);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Cabinette recycle bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>
    /// Best-effort check before deleting. It must not claim "no Recycle Bin" for a drive that has one (the user would be
    /// offered a permanent delete for nothing), so it only says false when Windows is known not to keep one there:
    /// <list type="bullet">
    /// <item>Network drives and UNC paths (except inside the user's own folders such as Documents, which keep a Recycle Bin
    /// when they are redirected to a server), removable drives (USB sticks, memory cards), CD/DVD drives and RAM disks.</item>
    /// <item>Fixed drives where the Recycle Bin is switched off: the per-volume "Don't move files to the Recycle Bin" setting
    /// (BitBucket\Volume\{guid}\NukeOnDelete) or the NoRecycleFiles policy.</item>
    /// <item>A file larger than its drive's Recycle Bin maximum size (BitBucket\Volume\{guid}\MaxCapacity, in MB), which Windows
    /// would delete permanently ("This file is too big to recycle"). Only checked when that value is set; see
    /// <see cref="IsTooLargeForRecycleBin"/>.</item>
    /// </list>
    /// SHQueryRecycleBin is not used: it can be slow (it sizes the whole bin) and does not tell "no bin" apart from other errors.
    /// Anything unexpected answers true; <see cref="SendToRecycleBin"/> still refuses items the shell would delete permanently.
    /// The registry is read once per volume (cached for a short while); the file's size is checked on every call.
    /// </summary>
    public bool CanRecycle(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var full = Path.GetFullPath(path);
            var volume = GetVolumePath(full);
            if (volume is null)
            {
                return true;
            }

            var now = Environment.TickCount64;
            if (!VolumeCache.TryGetValue(volume, out var cached) || cached.Expires <= now)
            {
                cached = ReadVolume(volume) with { Expires = now + (long)CanRecycleCacheTime.TotalMilliseconds };
                VolumeCache[volume] = cached;
            }

            if (!cached.CanRecycle)
            {
                return cached.IsRemote && IsInsideUserFolder(full);
            }

            return !IsTooLargeForRecycleBin(FileSize(full), cached.MaxCapacityMb);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Whether Windows keeps a Recycle Bin on a drive of this type (GetDriveType). Unknown types answer true.</summary>
    internal static bool DriveTypeHasRecycleBin(uint driveType) => driveType switch
    {
        DRIVE_REMOTE or DRIVE_REMOVABLE or DRIVE_CDROM or DRIVE_RAMDISK => false,
        _ => true,
    };

    /// <summary>
    /// The Recycle Bin maximum size in MB from a volume's BitBucket "MaxCapacity" registry value (a DWORD, read as a signed int),
    /// or null when the value is missing, zero or not a number: the limit is then unknown and nothing is refused for size.
    /// </summary>
    internal static long? ParseMaxCapacityMb(object? value) => value switch
    {
        int dword when dword != 0 => unchecked((uint)dword),
        long qword when qword > 0 => qword,
        _ => null,
    };

    /// <summary>
    /// True when Windows would refuse to recycle a file of <paramref name="sizeBytes"/> on a drive whose Recycle Bin maximum size
    /// is <paramref name="maxCapacityMb"/>: the file is larger than that size ("This file is too big to recycle"). A file exactly
    /// that size, an unknown size or an unknown limit is not refused, and 1 MB counts as 1,048,576 bytes (the larger reading),
    /// so this never claims "too big" for a file Windows would still recycle.
    /// </summary>
    internal static bool IsTooLargeForRecycleBin(long? sizeBytes, long? maxCapacityMb)
    {
        const long BytesPerMb = 1024 * 1024;
        return sizeBytes is long size && maxCapacityMb is long mb && mb > 0 && mb <= long.MaxValue / BytesPerMb && size > mb * BytesPerMb;
    }

    /// <summary>The size of a file, or null for a folder or a path that isn't there.</summary>
    private static long? FileSize(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return info.Exists ? info.Length : null;
    }

    /// <summary>What CanRecycle needs to know about a volume, read once and cached.</summary>
    private readonly record struct VolumeInfo(bool CanRecycle, bool IsRemote, long? MaxCapacityMb, long Expires);

    private static VolumeInfo ReadVolume(string volume)
    {
        var driveType = GetDriveType(volume);
        var isRemote = driveType == DRIVE_REMOTE;
        if (!DriveTypeHasRecycleBin(driveType) || PolicyDisablesRecycleBin())
        {
            return new VolumeInfo(false, isRemote, null, 0);
        }

        var guid = GetVolumeGuid(volume);
        if (guid is null)
        {
            return new VolumeInfo(true, isRemote, null, 0);
        }

        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");

        // The per-volume "Don't move files to the Recycle Bin. Remove files immediately when deleted." setting.
        var nuke = key?.GetValue("NukeOnDelete") is int value && value != 0;
        return new VolumeInfo(!nuke, isRemote, ParseMaxCapacityMb(key?.GetValue("MaxCapacity")), 0);
    }

    private static bool PolicyDisablesRecycleBin()
    {
        const string Policies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(Policies);
            if (key?.GetValue("NoRecycleFiles") is int value && value != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Folders Windows can redirect to a server while keeping a Recycle Bin for them.</summary>
    private static bool IsInsideUserFolder(string fullPath)
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites,
        ];
        foreach (var special in folders)
        {
            var folder = Environment.GetFolderPath(special);
            if (folder.Length > 0 && IsSameOrInside(fullPath, folder))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"C:\", "D:\Mounted\" or "\\server\share\" for any path on that volume (it does not have to exist).</summary>
    private static unsafe string? GetVolumePath(string fullPath)
    {
        const int Chars = 1024;
        var buffer = stackalloc char[Chars];
        return GetVolumePathName(fullPath, buffer, Chars) ? new string(buffer) : null;
    }

    /// <summary>"{guid}" of a local volume, or null (network shares, subst drives).</summary>
    private static unsafe string? GetVolumeGuid(string volumePath)
    {
        const int Chars = 64;
        var buffer = stackalloc char[Chars];
        if (!GetVolumeNameForVolumeMountPoint(volumePath, buffer, Chars))
        {
            return null;
        }

        // "\\?\Volume{xxxxxxxx-...}\"
        var name = new string(buffer);
        var start = name.IndexOf('{');
        var end = name.IndexOf('}');
        return start >= 0 && end > start ? name[start..(end + 1)] : null;
    }

    /// <summary>
    /// A refused item makes the shell cancel the rest of the operation, so whatever is left is recycled in another pass.
    /// Throws once everything that can be recycled was.
    /// </summary>
    private static void Recycle(List<string> paths)
    {
        var refused = new List<string>();
        var pending = paths;
        while (pending.Count > 0)
        {
            var pass = RecyclePass(pending);
            refused.AddRange(pass.Refused);
            if (pass.Failures.Count > 0 || (pass.Refused.Count == 0 && (pass.HResult < 0 || pass.Aborted)))
            {
                throw BuildFailure(refused, pass.Failures, pass.HResult, pass.Aborted)!;
            }

            if (pass.Refused.Count == 0)
            {
                break;
            }

            var skip = new HashSet<string>(refused.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            var left = pending.Where(p => !skip.Contains(Normalize(p)) && (File.Exists(p) || Directory.Exists(p))).ToList();
            if (left.Count >= pending.Count)
            {
                break; // No progress (should not happen): report what was refused.
            }

            pending = left;
        }

        if (BuildFailure(refused, [], 0, aborted: false) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>
    /// What a finished operation means for the caller: null (all recycled), <see cref="RecycleBinUnavailableException"/>
    /// (only items the shell would have deleted permanently were left), or <see cref="IOException"/> (anything else).
    /// </summary>
    internal static IOException? BuildFailure(IReadOnlyList<string> refused, IReadOnlyList<(string Path, string Reason)> failures, int hr, bool aborted)
    {
        if (failures.Count == 0 && refused.Count > 0)
        {
            return new RecycleBinUnavailableException(refused.ToList(), "Not moved to the Recycle Bin because it would be deleted permanently (the drive has no Recycle Bin, or the item is too large): " + List(refused));
        }

        if (failures.Count > 0)
        {
            var lines = failures.Take(5).Select(f => $"\"{f.Path}\": {f.Reason}");
            var more = failures.Count > 5 ? $" (and {failures.Count - 5} more)" : "";
            var skipped = refused.Count > 0 ? ". Also not moved because they would be deleted permanently: " + List(refused) : "";
            return new IOException("Could not move to the Recycle Bin: " + string.Join("; ", lines) + more + skipped);
        }

        if (hr < 0)
        {
            return new IOException("Could not move to the Recycle Bin: " + DescribeHResult(hr));
        }

        return aborted ? new IOException("Moving to the Recycle Bin was cancelled.") : null;
    }

    private static string List(IReadOnlyList<string> paths)
        => string.Join(", ", paths.Take(5).Select(p => $"\"{p}\"")) + (paths.Count > 5 ? $" (and {paths.Count - 5} more)" : "");

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static PassResult RecyclePass(List<string> paths)
    {
        IFileOperation? operation = null;
        var items = new List<IShellItem>();
        var sink = new ProgressSink();
        uint cookie = 0;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellIids.CLSID_FileOperation, throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;

            Check(operation.SetOperationFlags(FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI), "configure the delete operation");
            Check(operation.Advise(sink, out cookie), "watch the delete operation");

            foreach (var path in paths)
            {
                if (SHCreateItemFromParsingName(path, null, ShellIids.IShellItem, out var obj) < 0 || obj is not IShellItem item)
                {
                    // Vanished between the existence check and now: nothing to recycle.
                    ReleaseComObject(obj);
                    continue;
                }

                items.Add(item);
                Check(operation.DeleteItem(item, IntPtr.Zero), $"queue \"{path}\" for the Recycle Bin");
            }

            if (items.Count == 0)
            {
                return new PassResult([], [], 0, false);
            }

            var hr = operation.PerformOperations();
            _ = operation.GetAnyOperationsAborted(out var aborted);
            return new PassResult(sink.Refused, sink.Failures, hr, aborted);
        }
        finally
        {
            if (operation is not null && cookie != 0)
            {
                _ = operation.Unadvise(cookie);
            }

            foreach (var item in items)
            {
                ReleaseComObject(item);
            }

            ReleaseComObject(operation);
        }
    }

    private static void Check(int hr, string action)
    {
        if (hr < 0)
        {
            throw new IOException($"Could not {action}: {DescribeHResult(hr)}");
        }
    }

    internal static string DescribeHResult(int hr)
    {
        var message = ((uint)hr & 0xFFFF0000u) == 0x80070000u ? new Win32Exception(hr & 0xFFFF).Message : Marshal.GetExceptionForHR(hr)?.Message;
        return string.IsNullOrWhiteSpace(message) ? $"error 0x{hr:X8}" : $"{message.TrimEnd('.')} (0x{hr:X8})";
    }

    private static string DisplayPath(IShellItem item)
    {
        try
        {
            if (item.GetDisplayName(SIGDN.FileSysPath, out var p) >= 0 && p != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStringUni(p) ?? "?";
                }
                finally
                {
                    Marshal.FreeCoTaskMem(p);
                }
            }
        }
        catch (Exception)
        {
            // Fall through.
        }

        return "?";
    }

    private readonly record struct PassResult(List<string> Refused, List<(string Path, string Reason)> Failures, int HResult, bool Aborted);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ProgressSink : IFileOperationProgressSink
    {
        /// <summary>Items the shell would have deleted permanently; they were skipped.</summary>
        public List<string> Refused { get; } = [];

        public List<(string Path, string Reason)> Failures { get; } = [];

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            if ((dwFlags & TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0)
            {
                // The shell would delete this permanently. Refuse: the contract is "to the Recycle Bin".
                Refused.Add(DisplayPath(psiItem));
                return HRESULT_ERROR_CANCELLED;
            }

            return S_OK;
        }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            if (hrDelete < 0 && hrDelete != HRESULT_ERROR_CANCELLED && hrDelete != E_ABORT)
            {
                Failures.Add((DisplayPath(psiItem), DescribeHResult(hrDelete)));
            }

            return S_OK;
        }

        public int StartOperations() => S_OK;

        public int FinishOperations(int hrResult) => S_OK;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName) => S_OK;

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) => S_OK;

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => S_OK;

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => S_OK;

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) => S_OK;

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => S_OK;

        public int ResetTimer() => S_OK;

        public int PauseTimer() => S_OK;

        public int ResumeTimer() => S_OK;
    }
}
