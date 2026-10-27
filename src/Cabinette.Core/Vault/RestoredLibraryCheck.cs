using Cabinette.Core.Data;
using Cabinette.Core.Services;

namespace Cabinette.Core.Vault;

/// <summary>
/// Checks the database of a backup being restored before anything is created from it: every category folder name and every
/// vault file name must be one valid Windows name, and the categories must form a proper tree, so the restored library can
/// only ever point inside the chosen vault. Pointer paths and settings are taken as they are.
/// </summary>
internal static class RestoredLibraryCheck
{
    public const string DamagedMessage = "This backup's library is damaged or was changed, so it can't be restored.";

    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Throws <see cref="LibraryErrorCode.NotAllowed"/> when the restored library would reach outside <paramref name="vault"/>.</summary>
    public static void Validate(IReadOnlyCollection<CategoryRow> categories, IEnumerable<(long HomeCategoryId, string FileName)> vaultFiles,
        string vault)
    {
        var byId = new Dictionary<long, CategoryRow>();
        foreach (var category in categories)
        {
            if (!IsValidName(category.FolderName) || !byId.TryAdd(category.Id, category))
            {
                throw Damaged();
            }
        }

        // Every parent chain ends at a top-level category: no missing parents, no cycles.
        foreach (var category in categories)
        {
            var seen = new HashSet<long> { category.Id };
            for (var current = category; current.ParentId is long parentId;)
            {
                if (!byId.TryGetValue(parentId, out var parent) || !seen.Add(parentId))
                {
                    throw Damaged();
                }

                current = parent;
            }
        }

        // Final safety net: every folder and file resolves inside the vault.
        var tree = new CategoryTree();
        foreach (var category in categories)
        {
            tree.Add(category);
        }

        foreach (var category in categories)
        {
            if (!IsInsideVault(tree.FolderPath(vault, category.Id), vault))
            {
                throw Damaged();
            }
        }

        foreach (var (homeCategoryId, fileName) in vaultFiles)
        {
            if (!IsValidName(fileName))
            {
                throw Damaged();
            }

            var folder = byId.ContainsKey(homeCategoryId) ? tree.FolderPath(vault, homeCategoryId) : vault;
            if (!IsInsideVault(Path.Combine(folder, fileName), vault))
            {
                throw Damaged();
            }
        }
    }

    /// <summary>
    /// True for one valid Windows file or folder name: not empty, no <c>\ / : * ? " &lt; &gt; |</c> or control characters, not "." or "..",
    /// no trailing period or space, at most 255 characters. Leading spaces and periods are fine (".git", " notes"), as is anything
    /// the vault monitor can find on disk. Device names ("con.txt", even "con") are ordinary files on Windows 11 and can't point
    /// outside the vault, so they pass.
    /// </summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name)
           && name.Length <= NameRules.MaxNameLength
           && name.IndexOfAny(InvalidChars) < 0
           && !name.Any(c => c < 32)
           && name is not ("." or "..")
           && !name.EndsWith('.')
           && !name.EndsWith(' ')
           && !Path.IsPathRooted(name);

    private static bool IsInsideVault(string path, string vault)
    {
        try
        {
            return FileOps.IsSameOrInside(path, vault) && !FileOps.PathsEqual(path, vault);
        }
        catch (Exception ex) when (FileOps.IsIoError(ex))
        {
            return false;
        }
    }

    private static LibraryException Damaged() => new(LibraryErrorCode.NotAllowed, DamagedMessage);
}
