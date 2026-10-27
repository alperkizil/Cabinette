using System.Diagnostics;

namespace Cabinette.Core.Vault;

/// <summary>
/// Journal of what a whole-vault operation (Move vault, Restore) created in its destination, so a cancel or failure removes
/// exactly that and nothing else: the files it created (always created new, so they are its own), then the folders that did
/// not exist before, deepest first and only if they are empty. Files or folders anyone else put there are never touched.
/// </summary>
internal sealed class CreatedPaths
{
    private readonly List<string> _files = [];
    private readonly HashSet<string> _fileSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _directories = [];
    private readonly HashSet<string> _directorySet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates <paramref name="path"/> and its missing parents (errors are mapped). True when the folder itself is new.</summary>
    public bool CreateDirectory(string path)
    {
        var missing = new List<string>();
        for (var current = path; !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Add(current);
        }

        try
        {
            FileOps.CreateDirectory(path);
        }
        finally
        {
            // Also after a failure: the parents that were created before it are ours too.
            foreach (var created in missing)
            {
                if (Directory.Exists(created) && _directorySet.Add(created))
                {
                    _directories.Add(created);
                }
            }
        }

        return missing.Count > 0;
    }

    /// <summary>Records a file the operation just created (opened with <see cref="FileMode.CreateNew"/>).</summary>
    public void FileCreated(string path)
    {
        if (_fileSet.Add(path))
        {
            _files.Add(path);
        }
    }

    /// <summary>The operation deleted one of its own files again.</summary>
    public void FileDeleted(string path)
    {
        if (_fileSet.Remove(path))
        {
            _files.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>True when the operation created this file.</summary>
    public bool IsOwnFile(string path) => _fileSet.Contains(path);

    /// <summary>True when the operation created this folder.</summary>
    public bool IsOwnDirectory(string path) => _directorySet.Contains(path);

    /// <summary>Deletes one of the operation's own files (read-only ones included). IO errors propagate.</summary>
    public void DeleteFile(string path)
    {
        if (!IsOwnFile(path))
        {
            throw new InvalidOperationException($"\"{path}\" was not created by this operation.");
        }

        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        FileDeleted(path);
    }

    /// <summary>Removes one of the operation's own folders if it is empty. True when it is gone.</summary>
    public bool TryDeleteEmptyDirectory(string path)
    {
        if (!IsOwnDirectory(path))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return false;
                }

                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            Debug.WriteLine($"Cabinette: could not remove {path}: {ex.Message}");
            return false;
        }

        _directorySet.Remove(path);
        _directories.RemoveAll(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// Best effort: deletes every recorded file, then every recorded folder that is empty, deepest first. Never anything else.
    /// </summary>
    public void Remove()
    {
        foreach (var file in _files)
        {
            FileOps.TryDelete(file);
        }

        _files.Clear();
        _fileSet.Clear();
        foreach (var directory in _directories.OrderByDescending(Depth).ToList())
        {
            TryDeleteEmptyDirectory(directory);
        }
    }

    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
}
