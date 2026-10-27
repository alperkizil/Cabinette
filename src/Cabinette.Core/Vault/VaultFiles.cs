using System.Diagnostics;
using Cabinette.Core.Models;

namespace Cabinette.Core.Vault;

/// <summary>A file in the vault, for whole-vault operations (move, backup).</summary>
internal sealed record VaultFileEntry(string RelativePath, string FullPath, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

/// <summary>A folder in the vault, for whole-vault operations (move, backup). Its attributes carry Hidden and System over.</summary>
internal sealed record VaultDirectoryEntry(string RelativePath, string FullPath, FileAttributes Attributes);

internal static class VaultFiles
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Every folder and file of the vault (hidden and unknown files included), sorted by relative path.
    /// Cabinette's own temp files and folder links (junctions, to avoid cycles) are left out. IO errors propagate.
    /// </summary>
    public static (List<VaultDirectoryEntry> Directories, List<VaultFileEntry> Files) Enumerate(string vault, CancellationToken ct)
    {
        var directories = new List<VaultDirectoryEntry>();
        var files = new List<VaultFileEntry>();
        var pending = new Stack<string>();
        pending.Push(vault);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Options))
            {
                if (entry.Name.StartsWith(NameRules.CabinetteTempPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(vault, entry.FullName);
                if (entry is DirectoryInfo dir)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    directories.Add(new VaultDirectoryEntry(relative, dir.FullName, dir.Attributes));
                    pending.Push(dir.FullName);
                }
                else if (entry is FileInfo file)
                {
                    files.Add(new VaultFileEntry(relative, file.FullName, file.Length, file.LastWriteTimeUtc, file.Attributes));
                }
            }
        }

        directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return (directories, files);
    }
}

/// <summary>Throttled <see cref="VaultOperationProgress"/> reports for one stage of a long vault operation.</summary>
internal sealed class VaultProgressReporter(IProgress<VaultOperationProgress>? progress, string stage, int totalFiles, long totalBytes)
{
    private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
    private int _processedFiles;
    private long _bytes;
    private long _bytesAtFileStart;
    private string? _current;

    public void StartFile(string? name)
    {
        _current = name;
        _bytesAtFileStart = _bytes;
        Report(force: _processedFiles == 0);
    }

    public void AddBytes(long count)
    {
        _bytes += count;
        Report(force: false);
    }

    public void FileDone(long fileSize)
    {
        _processedFiles++;
        _bytes = Math.Max(_bytes, _bytesAtFileStart + fileSize);
        Report(force: _processedFiles >= totalFiles);
    }

    public void Report(bool force)
    {
        if (progress is null || (!force && _sinceReport.ElapsedMilliseconds < 100))
        {
            return;
        }

        _sinceReport.Restart();
        progress.Report(new VaultOperationProgress(stage, _processedFiles, totalFiles, Math.Min(_bytes, totalBytes), totalBytes, _current));
    }
}
