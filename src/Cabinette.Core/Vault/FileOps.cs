using System.Buffers;
using System.IO.Hashing;
using Cabinette.Core.Services;

namespace Cabinette.Core.Vault;

/// <summary>File-system helpers: copy-while-hashing, hashing, moves, and mapping IO errors to <see cref="LibraryException"/>.</summary>
internal static class FileOps
{
    private const int BufferSize = 1024 * 1024;

    // Win32 error codes (low word of the HRESULT carried by IOException).
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorFileExists = 80;
    private const int ErrorDiskFull = 112;
    private const int ErrorAlreadyExists = 183;

    // A volume or server without alternate data streams (FAT32, exFAT, some network shares) answers "name:stream" with one of these.
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInvalidName = 123;

    /// <summary>
    /// The alternate data stream that holds a file's Mark of the Web: browsers and mail programs write it on downloads, and
    /// SmartScreen, Office Protected View and the blocking of macros from the internet rely on it.
    /// </summary>
    public const string ZoneIdentifierStream = ":Zone.Identifier";

    public static byte[] Hash(ReadOnlySpan<byte> data) => XxHash128.Hash(data);

    /// <summary>
    /// Copies <paramref name="source"/> to a new file <paramref name="destination"/> and returns the XXH128 of the content.
    /// The source's Mark of the Web (<see cref="ZoneIdentifierStream"/>) goes along, and the copy is flushed to the disk (not
    /// only to the Windows cache) before this returns, so the caller may delete or recycle the original afterwards.
    /// The destination is hidden while it is a temp file unless <paramref name="hideDestination"/> is false.
    /// <paramref name="onCreated"/> runs as soon as the destination exists (it never runs when the name was already taken),
    /// so the caller knows the file is its own even if the copy fails later.
    /// </summary>
    public static async Task<byte[]> CopyAndHashAsync(string source, string destination, Action<long>? onBytes, CancellationToken ct,
        bool hideDestination = true, Action? onCreated = null)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            onCreated?.Invoke();
            if (hideDestination)
            {
                TryHide(destination);
            }

            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onBytes?.Invoke(read);
            }

            FlushToDisk(output);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        CopyMarkOfTheWeb(source, destination);
        return hasher.GetCurrentHash();
    }

    /// <summary>
    /// Writes what Windows still holds in its cache for <paramref name="stream"/> to the disk (FlushFileBuffers), so the file
    /// survives a power cut or a crash right after this returns.
    /// </summary>
    private static void FlushToDisk(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        FlushedToDisk.Value?.Invoke(stream.Name);
    }

    /// <summary>Test probe: runs with the file's path each time a copy was flushed to the disk (flows with the async context).</summary>
    internal static readonly AsyncLocal<Action<string>?> FlushedToDisk = new();

    /// <summary>
    /// Gives <paramref name="destination"/> the Mark of the Web of <paramref name="source"/>, if it has one. A source on a volume
    /// without alternate data streams has none. A vault on such a volume can't keep it: the copy goes on without it, as Explorer
    /// does when it copies to a FAT drive. Other errors (the mark can't be read or written) fail the copy.
    /// </summary>
    internal static void CopyMarkOfTheWeb(string source, string destination)
    {
        FileStream input;
        try
        {
            input = new FileStream(source + ZoneIdentifierStream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (IsNoStream(ex))
        {
            return; // Not marked.
        }

        using (input)
        {
            FileStream output;
            try
            {
                output = new FileStream(destination + ZoneIdentifierStream, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (IsNoStream(ex) && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine($"Cabinette: the vault can't keep the Mark of the Web of {source}: {ex.Message}");
                return;
            }

            using (output)
            {
                input.CopyTo(output);
                FlushToDisk(output);
            }
        }
    }

    /// <summary>The stream isn't there, or the volume has no alternate data streams.</summary>
    private static bool IsNoStream(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException or NotSupportedException or ArgumentException
           || (ex is IOException && Win32Code(ex) is ErrorFileNotFound or ErrorPathNotFound or ErrorNotSupported or ErrorInvalidParameter or ErrorInvalidName);

    /// <summary>XXH128 of a file's content.</summary>
    public static async Task<byte[]> HashFileAsync(string path, Action<long>? onBytes, CancellationToken ct)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
                onBytes?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHash();
    }

    /// <summary>
    /// XXH128 of a file that nobody is writing to: the file is opened with read sharing only, so a file another program still
    /// has open for writing fails with a sharing violation (<see cref="IsSharingViolation"/>). <paramref name="allowWriters"/> lifts
    /// that check for files that stay open for a long time. Size and modification time are read while the file is open.
    /// </summary>
    public static (byte[] Hash, long Size, DateTime LastWriteUtc) HashCompleteFile(string path, bool allowWriters, CancellationToken ct)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var share = allowWriters ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read | FileShare.Delete;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, share, 1, FileOptions.SequentialScan);
            int read;
            while ((read = input.Read(buffer, 0, BufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, read));
            }

            return (hasher.GetCurrentHash(), input.Length, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>True when the error means another program has the file open (sharing or lock violation).</summary>
    public static bool IsSharingViolation(Exception ex)
        => ex is IOException && Win32Code(ex) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or anywhere below it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(Path.EndsInDirectorySeparator(f) ? f : f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="path"/> with every junction and symbolic link along it followed (the part that doesn't exist is kept as
    /// written), so two spellings of one folder compare equal. Managed APIs only: 8.3 short names and subst drives are not
    /// expanded (Cabinette.Windows' FinalPaths does that). Never throws; a path that can't be looked at stays as written.
    /// </summary>
    public static string ResolveLinks(string path)
    {
        string current;
        try
        {
            current = Normalize(path);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return path;
        }

        // A link can lead to another link: give up after a sane number of hops (a cycle).
        for (var hop = 0; hop < 40; hop++)
        {
            if (FollowFirstLink(current) is not { } next)
            {
                return current;
            }

            current = next;
        }

        return current;
    }

    /// <summary>The path with its first link (from the root down) replaced by the link's final target, or null when it has none.</summary>
    private static string? FollowFirstLink(string full)
    {
        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var walked = root;
            for (var i = 0; i < parts.Length; i++)
            {
                walked = Path.Combine(walked, parts[i]);
                FileSystemInfo info = Directory.Exists(walked) ? new DirectoryInfo(walked) : new FileInfo(walked);
                if (!info.Exists)
                {
                    return null; // The rest doesn't exist: nothing more to follow.
                }

                if (info.LinkTarget is null || info.ResolveLinkTarget(returnFinalTarget: true) is not { } target)
                {
                    continue;
                }

                return Normalize(Path.Combine([target.FullName, .. parts[(i + 1)..]]));
            }
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            // Can't be looked at (no access, a drive that isn't there): compare it as written.
        }

        return null;
    }

    /// <summary>Moves/renames a file. Case-only renames are supported. Never overwrites. Errors are mapped.</summary>
    public static void MoveFile(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            throw ToLibraryException(ex, from);
        }
    }

    /// <summary>Moves/renames a folder. Case-only renames go through a temporary name. Errors are mapped.</summary>
    public static void MoveDirectory(string from, string to)
    {
        try
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(from, to, StringComparison.Ordinal))
                {
                    return;
                }

                var temp = CaseRenameTempPath(from);
                Directory.Move(from, temp);
                Directory.Move(temp, to);
                return;
            }

            if (Directory.Exists(to) || File.Exists(to))
            {
                throw new LibraryException(LibraryErrorCode.NameAlreadyExists, $"A folder named \"{Path.GetFileName(to)}\" already exists.");
            }

            Directory.Move(from, to);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            throw ToLibraryException(ex, from);
        }
    }

    /// <summary>The temporary name a letter-case-only folder rename goes through (the vault monitor ignores it).</summary>
    public static string CaseRenameTempPath(string folder)
        => folder.TrimEnd('\\', '/') + ".cabinette-rename-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Creates a folder (and its parents). Errors are mapped.</summary>
    public static void CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            throw ToLibraryException(ex, path);
        }
    }

    /// <summary>True when another program holds the file open (so it can't be deleted or moved right now).</summary>
    public static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException ex) when (Win32Code(ex) is ErrorSharingViolation or ErrorLockViolation)
        {
            return true;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return false;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            // Best effort (temp files).
        }
    }

    public static void TryHide(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// Gives a folder this operation created the Hidden and System flags of <paramref name="attributes"/> (its source folder's),
    /// so the vault monitor keeps ignoring it. Nothing happens when neither is set.
    /// </summary>
    public static void ApplyFolderAttributes(string path, FileAttributes attributes)
    {
        var flags = attributes & (FileAttributes.Hidden | FileAttributes.System);
        if (flags == 0)
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(path);
            info.Attributes |= flags;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            throw ToLibraryException(ex, path);
        }
    }

    /// <summary>Clears hidden/system/read-only on a file that just landed in the vault.</summary>
    public static void MakeNormal(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            // Cosmetic only.
        }
    }

    public static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full path without a trailing separator (except for roots like "C:\").</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    public static bool IsIoError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException;

    public static int Win32Code(Exception ex) => ex.HResult & 0xFFFF;

    /// <summary>Maps an IO exception to a <see cref="LibraryException"/> with a message that can be shown as is.</summary>
    public static LibraryException ToLibraryException(Exception ex, string path)
    {
        if (ex is LibraryException le)
        {
            return le;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name))
        {
            name = path;
        }

        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException
                => new(LibraryErrorCode.NotFound, $"\"{name}\" could not be found. It may have been moved or deleted.", ex),
            UnauthorizedAccessException or System.Security.SecurityException
                => new(LibraryErrorCode.AccessDenied, $"Access to \"{name}\" was denied.", ex),
            PathTooLongException
                => new(LibraryErrorCode.InvalidName, $"The path of \"{name}\" is too long.", ex),
            ArgumentException or NotSupportedException
                => new(LibraryErrorCode.InvalidName, $"\"{name}\" is not a valid path.", ex),
            IOException io => Win32Code(io) switch
            {
                ErrorSharingViolation or ErrorLockViolation
                    => new(LibraryErrorCode.FileInUse, $"\"{name}\" is in use by another program. Close it and try again.", ex),
                ErrorFileExists or ErrorAlreadyExists
                    => new(LibraryErrorCode.NameAlreadyExists, $"\"{name}\" already exists.", ex),
                ErrorFileNotFound or ErrorPathNotFound
                    => new(LibraryErrorCode.NotFound, $"\"{name}\" could not be found. It may have been moved or deleted.", ex),
                ErrorAccessDenied
                    => new(LibraryErrorCode.AccessDenied, $"Access to \"{name}\" was denied.", ex),
                ErrorDiskFull or ErrorHandleDiskFull
                    => new(LibraryErrorCode.Unknown, "There is not enough space on the disk.", ex),
                _ => new(LibraryErrorCode.Unknown, $"\"{name}\": {io.Message}", ex),
            },
            _ => new(LibraryErrorCode.Unknown, $"\"{name}\": {ex.Message}", ex),
        };
    }
}
