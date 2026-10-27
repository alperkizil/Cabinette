using System.Text;
using Cabinette.Core.Services;

namespace Cabinette.Core.Vault;

/// <summary>Windows naming rules: folder-name sanitizing, file-name validation, "name (1).ext" numbering and skip rules.</summary>
internal static class NameRules
{
    public const int MaxNameLength = 255;

    private const int MaxFolderNameLength = 120;

    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turns a display name into a valid Windows folder name ("C/C++" → "C-C++").</summary>
    public static string SanitizeFolderName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch < 32 || Array.IndexOf(InvalidChars, ch) >= 0 ? '-' : ch);
        }

        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > MaxFolderNameLength)
        {
            s = s[..MaxFolderNameLength];
            if (char.IsHighSurrogate(s[^1]))
            {
                s = s[..^1];
            }

            s = s.TrimEnd('.', ' ');
        }

        if (s.Length == 0)
        {
            s = "_";
        }

        if (IsReserved(s))
        {
            s += "_";
        }

        // Names Cabinette uses for its own temp files and its staging folder are ignored by the vault monitor, Move vault and backup.
        if (s.StartsWith(CabinetteTempPrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = "_" + s;
        }

        return s;
    }

    /// <summary>True for device names Windows reserves (CON, NUL, COM1, ...), also with an extension ("CON.txt").</summary>
    public static bool IsReserved(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    /// <summary>Validates a file name typed by the user. Returns the trimmed name or throws <see cref="LibraryErrorCode.InvalidName"/>.</summary>
    public static string ValidateFileName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "Enter a name.");
        }

        if (trimmed.IndexOfAny(InvalidChars) >= 0 || trimmed.Any(c => c < 32))
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "A name can't contain any of these characters: \\ / : * ? \" < > |");
        }

        if (trimmed.EndsWith('.') || trimmed is "." or "..")
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "A name can't end with a period.");
        }

        if (IsReserved(trimmed))
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, $"\"{trimmed}\" is a name reserved by Windows.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "The name is too long.");
        }

        return trimmed;
    }

    /// <summary>Validates a category display name (any characters except control characters). Returns it trimmed.</summary>
    public static string ValidateCategoryName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "Enter a category name.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "A category name can't contain control characters.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new LibraryException(LibraryErrorCode.InvalidName, "The category name is too long.");
        }

        return trimmed;
    }

    /// <summary>"report.pdf", "report (1).pdf", "report (2).pdf", ...</summary>
    public static IEnumerable<string> NumberedFileNames(string fileName)
    {
        yield return fileName;
        var ext = Path.GetExtension(fileName);
        var stem = fileName[..^ext.Length];
        if (stem.Length == 0)
        {
            // ".gitignore" → ".gitignore (1)"
            stem = fileName;
            ext = "";
        }

        for (var n = 1; ; n++)
        {
            yield return $"{stem} ({n}){ext}";
        }
    }

    /// <summary>"Name", "Name (1)", "Name (2)", ... (no extension handling; for categories and folders).</summary>
    public static IEnumerable<string> NumberedNames(string name)
    {
        yield return name;
        for (var n = 1; ; n++)
        {
            yield return $"{name} ({n})";
        }
    }

    /// <summary>Files skipped when a folder is added: hidden/system files, desktop.ini, Thumbs.db, Office lock files and *.tmp.</summary>
    public static bool ShouldSkipFile(FileInfo file)
    {
        if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            return true;
        }

        var name = file.Name;
        return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~$", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Subfolders skipped when a folder is added: hidden, system and links (junctions/symlinks, to avoid cycles).</summary>
    public static bool ShouldSkipDirectory(DirectoryInfo dir)
        => (dir.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0;

    /// <summary>Prefix of Cabinette's own hidden temp files and folders inside the vault.</summary>
    public const string CabinetteTempPrefix = ".cabinette-";

    /// <summary>
    /// Hidden folder at the top of the vault that adds and Migrate copy into before the file is moved into its category folder
    /// under the write lock. Its name starts with <see cref="CabinetteTempPrefix"/>, so the vault monitor, Move vault and backup
    /// ignore it. It is emptied when the library opens.
    /// </summary>
    public const string StagingFolderName = CabinetteTempPrefix + "tmp";

    public static string StagingFolder(string vault) => Path.Combine(vault, StagingFolderName);

    /// <summary>
    /// The temp file an add or a migration left in a category folder before the staging folder existed (the app quit or crashed
    /// mid-copy): ".cabinette-" + 32 hex digits + ".tmp".
    /// </summary>
    public static bool IsLegacyCopyTempName(string name)
    {
        const int hexLength = 32;
        const string extension = ".tmp";
        if (name.Length != CabinetteTempPrefix.Length + hexLength + extension.Length
            || !name.StartsWith(CabinetteTempPrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var ch in name.AsSpan(CabinetteTempPrefix.Length, hexLength))
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// File names the vault monitor never imports, judged by name alone (SPEC.md section 8): Office lock files, temp and partial
    /// downloads, desktop.ini, Thumbs.db and Cabinette's own temp files.
    /// </summary>
    public static bool IsIgnoredVaultName(string name)
        => name.StartsWith(CabinetteTempPrefix, StringComparison.OrdinalIgnoreCase)
           || name.Contains(".cabinette-rename-", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("~$", StringComparison.Ordinal)
           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
           || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files that don't keep a vault folder alive: Windows' own folder files (desktop.ini, Thumbs.db) and Cabinette's temp files.
    /// A folder holding nothing else may be adopted by a new category, and goes when its category is deleted.
    /// </summary>
    public static bool IsFolderLeftover(string name)
        => name.StartsWith(CabinetteTempPrefix, StringComparison.OrdinalIgnoreCase)
           || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files in the vault the monitor does not import: ignored names plus hidden and system files.</summary>
    public static bool IsIgnoredVaultFile(FileInfo file)
        => (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || IsIgnoredVaultName(file.Name);

    /// <summary>Folders in the vault the monitor does not turn into categories: hidden, system, links and Cabinette's temp folders.</summary>
    public static bool IsIgnoredVaultDirectory(DirectoryInfo dir) => ShouldSkipDirectory(dir) || IsIgnoredVaultName(dir.Name);
}
