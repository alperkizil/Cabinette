using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.Core.Vault;

/// <summary>manifest.json of a portable backup.</summary>
internal sealed class BackupManifest
{
    public const string FormatName = "cabinette-backup";
    public const int CurrentFormatVersion = 1;

    public string Format { get; set; } = FormatName;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public DateTimeOffset CreatedAt { get; set; }

    public string MachineName { get; set; } = "";

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public int CategoryCount { get; set; }

    public int ItemCount { get; set; }

    public int PointerItemCount { get; set; }

    public string AppVersion { get; set; } = "";

    public int SchemaVersion { get; set; }

    /// <summary>Where the vault was on the PC that made the backup (informational; restoring picks a new folder).</summary>
    public string? VaultPath { get; set; }

    public BackupInfo ToInfo() => new(CreatedAt, MachineName, FileCount, TotalBytes, CategoryCount, PointerItemCount, FormatVersion);
}

/// <summary>files.json: exact modification times (zip entries only keep 2-second local times), so restored files look unchanged.</summary>
internal sealed class BackupFileRecord
{
    public string Path { get; set; } = "";

    public long Size { get; set; }

    public DateTime Modified { get; set; }
}

/// <summary>
/// The portable backup: one .zip with manifest.json, cabinette.db (a consistent snapshot), files.json and every vault file under
/// vault/ with its relative path (SPEC.md section 8).
/// </summary>
internal static class BackupArchive
{
    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntry = "cabinette.db";
    public const string FilesEntry = "files.json";
    public const string VaultPrefix = "vault/";

    private const int BufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Formats that are already compressed: stored as they are (compressing them again only costs time).</summary>
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "avif", "jxl",
        "mp3", "aac", "m4a", "ogg", "opus", "flac", "wma",
        "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "3gp",
        "zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "cab", "jar", "apk", "msi", "msix", "appx",
        "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "cbz", "cbr", "woff", "woff2",
    };

    // ------------------------------------------------------------ writing

    /// <summary>
    /// Writes the archive to <paramref name="zipPath"/> (a new file). Returns the files and bytes included. Every vault entry
    /// carries its DOS attributes (read-only, hidden, system, directory, archive) in the low byte of its external attributes.
    /// </summary>
    public static (int Files, long Bytes) Write(string zipPath, BackupManifest manifest, string databaseSnapshot,
        IReadOnlyList<VaultDirectoryEntry> directories, IReadOnlyList<VaultFileEntry> files, IProgress<VaultOperationProgress>? progress,
        CancellationToken ct)
    {
        var reporter = new VaultProgressReporter(progress, "zipping", files.Count, files.Sum(f => f.Size));
        var records = new List<BackupFileRecord>(files.Count);
        long bytes = 0;
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            AddFile(zip, DatabaseEntry, databaseSnapshot, File.GetLastWriteTimeUtc(databaseSnapshot), CompressionLevel.Fastest, 0, null, ct);
            foreach (var dir in directories)
            {
                var entry = zip.CreateEntry(VaultPrefix + ToEntryPath(dir.RelativePath) + "/");
                entry.ExternalAttributes = DosAttributes(dir.Attributes | FileAttributes.Directory);
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                reporter.StartFile(file.RelativePath);
                long written;
                try
                {
                    written = AddFile(zip, VaultPrefix + ToEntryPath(file.RelativePath), file.FullPath, file.LastWriteUtc,
                        LevelFor(file.RelativePath), DosAttributes(file.Attributes & ~FileAttributes.Directory), reporter.AddBytes, ct);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Deleted outside Cabinette since the listing: nothing to back up.
                    reporter.FileDone(file.Size);
                    continue;
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    throw FileOps.ToLibraryException(ex, file.FullPath);
                }

                records.Add(new BackupFileRecord { Path = ToEntryPath(file.RelativePath), Size = written, Modified = file.LastWriteUtc });
                bytes += written;
                reporter.FileDone(file.Size);
            }

            manifest.FileCount = records.Count;
            manifest.TotalBytes = bytes;
            WriteJson(zip, FilesEntry, records);
            WriteJson(zip, ManifestEntry, manifest);
        }

        return (records.Count, bytes);
    }

    private static long AddFile(ZipArchive zip, string entryName, string path, DateTime lastWriteUtc, CompressionLevel level,
        int externalAttributes, Action<long>? onBytes, CancellationToken ct)
    {
        // Open the source first: a file that can't be read leaves no half-written entry behind.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var entry = zip.CreateEntry(entryName, level);
        entry.LastWriteTime = ZipTime(lastWriteUtc);
        entry.ExternalAttributes = externalAttributes;
        using var output = entry.Open();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, BufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                total += read;
                onBytes?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private static void WriteJson<T>(ZipArchive zip, string entryName, T value)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var output = entry.Open();
        JsonSerializer.Serialize(output, value, JsonOptions);
    }

    private static CompressionLevel LevelFor(string relativePath)
        => StoredExtensions.Contains(Path.GetExtension(relativePath).TrimStart('.')) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;

    private static string ToEntryPath(string relativePath) => relativePath.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>ReadOnly 0x01, Hidden 0x02, System 0x04, Directory 0x10, Archive 0x20 (the same values as <see cref="FileAttributes"/>).</summary>
    private const FileAttributes DosAttributeMask =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory | FileAttributes.Archive;

    /// <summary>The DOS attribute byte zip tools read from the low byte of an entry's external attributes.</summary>
    private static int DosAttributes(FileAttributes attributes) => (int)(attributes & DosAttributeMask);

    /// <summary>
    /// What a restore puts back from an entry's external attributes: Hidden, System and ReadOnly on files, Hidden and System
    /// on folders. Only the low (DOS) byte counts; backups made before attributes were stored have 0 there, so nothing is applied.
    /// </summary>
    internal static FileAttributes RestoredAttributes(int externalAttributes, bool isDirectory)
    {
        var dos = (FileAttributes)(externalAttributes & 0xFF);
        var keep = isDirectory
            ? FileAttributes.Hidden | FileAttributes.System
            : FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly;
        return dos & keep;
    }

    /// <summary>Zip entries hold DOS times (1980–2107).</summary>
    private static DateTimeOffset ZipTime(DateTime utc)
    {
        var min = new DateTime(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2107, 12, 30, 0, 0, 0, DateTimeKind.Utc);
        var value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(value < min ? min : value > max ? max : value);
    }

    // ------------------------------------------------------------ reading

    public static Task<BackupInfo> ReadInfoAsync(string zipPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        return Task.Run(() =>
        {
            using var zip = OpenZip(zipPath);
            return ReadManifest(zip).ToInfo();
        }, ct);
    }

    private static ZipArchive OpenZip(string zipPath)
    {
        try
        {
            return ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException ex)
        {
            throw NotABackup(ex);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, zipPath);
        }
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry);
        if (entry is null || entry.Length > 1024 * 1024)
        {
            throw NotABackup(null);
        }

        BackupManifest? manifest;
        try
        {
            using var input = entry.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(input, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            throw NotABackup(ex);
        }

        if (manifest is null || manifest.Format != BackupManifest.FormatName || manifest.FormatVersion < 1)
        {
            throw NotABackup(null);
        }

        if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion || manifest.SchemaVersion > Database.SchemaVersion)
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed,
                "This backup was made by a newer version of Cabinette. Update Cabinette to restore it.");
        }

        if (zip.GetEntry(DatabaseEntry) is null)
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "This backup is incomplete: the library database is missing.");
        }

        return manifest;
    }

    private static LibraryException NotABackup(Exception? inner)
        => new(LibraryErrorCode.NotAllowed, "This file is not a Cabinette backup.", inner);

    // ------------------------------------------------------------ restoring

    public static async Task<ILibraryService> RestoreAsync(string zipPath, string newVaultPath, LibraryOptions options, IRecycleBin recycleBin,
        IProgress<VaultOperationProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        var vault = LibraryService.NormalizeVaultPath(newVaultPath);
        try
        {
            return await Task.Run(() => RestoreCoreAsync(zipPath, vault, options, recycleBin, progress, ct), ct).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "The library database in this backup could not be opened.", ex);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            throw FileOps.ToLibraryException(ex, zipPath);
        }
    }

    private sealed record PlannedEntry(ZipArchiveEntry Entry, string Target, string Relative, bool IsDirectory);

    private static async Task<ILibraryService> RestoreCoreAsync(string zipPath, string vault, LibraryOptions options, IRecycleBin recycleBin,
        IProgress<VaultOperationProgress>? progress, CancellationToken ct)
    {
        using var zip = OpenZip(zipPath);
        ReadManifest(zip);

        // Everything is checked before anything is written.
        if (File.Exists(vault))
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, $"\"{vault}\" is a file. Choose a folder for the vault.");
        }

        if (Directory.Exists(vault) && Directory.EnumerateFileSystemEntries(vault).Any())
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "Choose an empty folder for the restored vault.");
        }

        if (FileOps.IsSameOrInside(options.DataDirectory, vault))
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed, "The vault can't contain Cabinette's data folder.");
        }

        var entries = PlanExtraction(zip, vault);
        var times = ReadFileTimes(zip);

        var dbPath = Path.Combine(options.DataDirectory, Database.FileName);
        var saved = new List<(string From, string To)>();
        var databaseReplaced = false;
        var created = new CreatedPaths();
        LibraryService? library = null;
        try
        {
            created.CreateDirectory(vault);
            ExtractVault(entries, times, created, progress, ct);

            FileOps.CreateDirectory(options.DataDirectory);
            new Database(options.DataDirectory).ClearPool();
            SaveExistingDatabase(dbPath, options.TimeProvider, saved);
            databaseReplaced = true;
            ExtractDatabase(zip, dbPath);

            progress?.Report(new VaultOperationProgress("opening", entries.Count, entries.Count, 0, 0, null));
            library = await LibraryService.OpenAsync(options, recycleBin, ct, sweepStaging: false, readDatesTaken: false).ConfigureAwait(false);
            await library.CompleteRestoreAsync(vault, ct).ConfigureAwait(false);

            // Now that the library names the restored vault: an older backup gets the later built-in subcategories there.
            await library.AddLaterBuiltInsAsync(ct).ConfigureAwait(false);

            // Only now that the library names the new vault: before, vault files would have been looked for in the backup's old one.
            library.StartReadingDatesTaken();
            return library;
        }
        catch
        {
            if (library is not null)
            {
                await library.DisposeAsync().ConfigureAwait(false);
            }

            if (databaseReplaced)
            {
                new Database(options.DataDirectory).ClearPool();
                foreach (var suffix in (string[])["", "-wal", "-shm"])
                {
                    FileOps.TryDelete(dbPath + suffix);
                }
            }

            // Put the previous library back.
            for (var i = saved.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Move(saved[i].To, saved[i].From);
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    System.Diagnostics.Debug.WriteLine($"Cabinette: could not put {saved[i].From} back: {ex.Message}");
                }
            }

            // Only what this restore created goes; anything else in the chosen folder stays.
            created.Remove();
            throw;
        }
    }

    /// <summary>The vault entries and where they go. Any entry that would land outside the vault (zip slip) rejects the whole backup.</summary>
    private static List<PlannedEntry> PlanExtraction(ZipArchive zip, string vault)
    {
        var result = new List<PlannedEntry>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(VaultPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = name[VaultPrefix.Length..];
            var isDirectory = relative.EndsWith('/');
            relative = relative.TrimEnd('/');
            if (relative.Length == 0)
            {
                continue;
            }

            var unsafePath = new LibraryException(LibraryErrorCode.NotAllowed,
                "This backup contains a file path that points outside the vault folder, so it can't be restored.");
            if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(p => p == ".."))
            {
                throw unsafePath;
            }

            var localRelative = relative.Replace('/', Path.DirectorySeparatorChar);
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(vault, localRelative));
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw unsafePath;
            }

            if (!FileOps.IsSameOrInside(target, vault) || FileOps.PathsEqual(target, vault))
            {
                throw unsafePath;
            }

            result.Add(new PlannedEntry(entry, target, localRelative, isDirectory));
        }

        return result;
    }

    private static Dictionary<string, DateTime> ReadFileTimes(ZipArchive zip)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry(FilesEntry);
        if (entry is null)
        {
            return result;
        }

        try
        {
            using var input = entry.Open();
            foreach (var record in JsonSerializer.Deserialize<List<BackupFileRecord>>(input, JsonOptions) ?? [])
            {
                result[record.Path.Replace('/', Path.DirectorySeparatorChar)] = DateTime.SpecifyKind(record.Modified, DateTimeKind.Utc);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            // Only exact times are lost; the zip's own times are used instead.
        }

        return result;
    }

    /// <summary>
    /// Extracts the vault entries. Every folder and file it creates is recorded in <paramref name="created"/>. Files are always
    /// created new, so a name someone else took meanwhile fails the restore instead of being overwritten.
    /// </summary>
    private static void ExtractVault(List<PlannedEntry> entries, Dictionary<string, DateTime> times, CreatedPaths created,
        IProgress<VaultOperationProgress>? progress, CancellationToken ct)
    {
        var files = entries.Where(e => !e.IsDirectory).ToList();
        var reporter = new VaultProgressReporter(progress, "extracting", files.Count, files.Sum(e => e.Entry.Length));
        var folderAttributes = new List<(string Path, FileAttributes Attributes)>();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var planned in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (planned.IsDirectory)
                {
                    created.CreateDirectory(planned.Target);
                    var flags = RestoredAttributes(planned.Entry.ExternalAttributes, isDirectory: true);
                    if (flags != 0 && created.IsOwnDirectory(planned.Target))
                    {
                        folderAttributes.Add((planned.Target, flags));
                    }

                    continue;
                }

                reporter.StartFile(planned.Relative);
                created.CreateDirectory(Path.GetDirectoryName(planned.Target)!);
                try
                {
                    using (var input = planned.Entry.Open())
                    using (var output = new FileStream(planned.Target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created.FileCreated(planned.Target);
                        int read;
                        while ((read = input.Read(buffer, 0, BufferSize)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            reporter.AddBytes(read);
                        }
                    }

                    var modified = times.TryGetValue(planned.Relative, out var exact) ? exact : planned.Entry.LastWriteTime.UtcDateTime;
                    File.SetLastWriteTimeUtc(planned.Target, modified);
                    var attributes = RestoredAttributes(planned.Entry.ExternalAttributes, isDirectory: false);
                    if (attributes != 0)
                    {
                        File.SetAttributes(planned.Target, attributes);
                    }
                }
                catch (Exception ex) when (FileOps.IsIoError(ex))
                {
                    // Names the file (e.g. one that someone else created in the chosen folder meanwhile), not the backup.
                    throw FileOps.ToLibraryException(ex, planned.Target);
                }

                reporter.FileDone(planned.Entry.Length);
            }

            // Folders last, once everything is in them.
            foreach (var (path, attributes) in folderAttributes)
            {
                FileOps.ApplyFolderAttributes(path, attributes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Renames an existing database (and its -wal/-shm) to cabinette.db.bak-yyyyMMdd-HHmmss so it can be put back.</summary>
    private static void SaveExistingDatabase(string dbPath, TimeProvider time, List<(string From, string To)> saved)
    {
        string[] suffixes = ["", "-wal", "-shm"];
        if (!suffixes.Any(s => File.Exists(dbPath + s)))
        {
            return;
        }

        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = NameRules.NumberedNames(dbPath + ".bak-" + stamp).First(p => !suffixes.Any(s => File.Exists(p + s)));
        foreach (var suffix in suffixes)
        {
            var from = dbPath + suffix;
            if (!File.Exists(from))
            {
                continue;
            }

            try
            {
                File.Move(from, backup + suffix);
            }
            catch (Exception ex) when (FileOps.IsIoError(ex))
            {
                throw FileOps.IsSharingViolation(ex)
                    ? new LibraryException(LibraryErrorCode.FileInUse, "The current library is still open. Close it before restoring.", ex)
                    : FileOps.ToLibraryException(ex, from);
            }

            saved.Add((from, backup + suffix));
        }
    }

    private static void ExtractDatabase(ZipArchive zip, string dbPath)
    {
        var entry = zip.GetEntry(DatabaseEntry)!;
        var temp = dbPath + ".restore-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            File.Move(temp, dbPath);
        }
        finally
        {
            FileOps.TryDelete(temp);
        }
    }
}
