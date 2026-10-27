using System.Globalization;
using System.Text;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>Library records as the "ai" command's records (AiProtocol.cs), and the AI's filters as the library's.</summary>
internal static class AiCommandConversions
{
    /// <summary>The type keys of the type filter: the top-level built-in categories' keys, "deleted" excepted.</summary>
    public static IReadOnlySet<string> TypeKeys { get; } = ExtensionMap.BuiltInCategories
        .Where(d => d.ParentKey is null && d.Key != ExtensionMap.DeletedKey)
        .Select(d => d.Key)
        .ToHashSet(StringComparer.Ordinal);

    public static string ModeName(ItemMode mode) => mode == ItemMode.Pointer ? InstanceProtocol.PointerMode : InstanceProtocol.CopyMode;

    /// <summary>"copy" or "pointer" (any case, trimmed); null for anything else.</summary>
    public static ItemMode? ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        InstanceProtocol.CopyMode => ItemMode.Copy,
        InstanceProtocol.PointerMode => ItemMode.Pointer,
        _ => null,
    };

    public static string KindName(ItemKind kind) => kind switch
    {
        ItemKind.Note => "note",
        ItemKind.PastedImage => "pastedImage",
        ItemKind.Link => "link",
        _ => "file",
    };

    public static string AddedByName(AddedBy addedBy) => addedBy switch
    {
        AddedBy.Explorer => "explorer",
        AddedBy.VaultMonitor => "vaultMonitor",
        AddedBy.Ai => "ai",
        AddedBy.Paste => "paste",
        AddedBy.Downloads => "downloads",
        _ => "app",
    };

    public static string CategoryKindName(CategoryKind kind) => kind switch
    {
        CategoryKind.BuiltIn => "builtin",
        CategoryKind.System => "deleted",
        _ => "user",
    };

    /// <summary>
    /// A library item for the AI. With <paramref name="categoryPaths"/> the details are filled too (get_files): modification
    /// time, original path, hash, note preview and the categories' paths.
    /// </summary>
    public static AiFile ToAiFile(LibraryItem item, IReadOnlyDictionary<long, string>? categoryPaths = null)
    {
        var file = new AiFile
        {
            Id = item.Id,
            Name = item.FileName,
            Path = item.FullPath,
            Mode = ModeName(item.Mode),
            Kind = KindName(item.Kind),
            Type = item.TypeKey,
            SizeBytes = item.SizeBytes,
            AddedAt = item.AddedAt,
            AddedBy = AddedByName(item.AddedBy),
            HomeCategoryId = item.HomeCategoryId,
            CategoryIds = item.CategoryIds,
            Tags = item.Tags,
            IsMissing = item.IsMissing,
            StarredAt = item.StarredAt,
        };

        return categoryPaths is null
            ? file
            : file with
            {
                ModifiedAt = item.FileModifiedAt,
                OriginalPath = item.OriginalPath,
                Hash = item.Hash is { } hash ? Convert.ToHexStringLower(hash) : null,
                NotePreview = item.NotePreview,
                CategoryPaths = item.CategoryIds.Select(id => categoryPaths.GetValueOrDefault(id) ?? "").ToList(),
            };
    }

    public static AiCategory ToAiCategory(CategorySummary summary, string path) => new()
    {
        Id = summary.Category.Id,
        ParentId = summary.Category.ParentId,
        Name = summary.Category.Name,
        Path = path,
        Kind = CategoryKindName(summary.Category.Kind),
        FileCount = summary.ItemCount,
        UnreadCount = summary.UnreadCount,
        SubcategoryCount = summary.SubcategoryCount,
        IsPinned = summary.Category.IsPinned,
        CreatedAt = summary.Category.CreatedAt,
        LastUploadAt = summary.Category.LastUploadAt,
    };

    /// <summary>Every category's path: the names from the top level down, joined with "/" ("Code/Python").</summary>
    public static IReadOnlyDictionary<long, string> CategoryPaths(IReadOnlyList<Category> all)
    {
        var byId = all.ToDictionary(c => c.Id);
        var paths = new Dictionary<long, string>(all.Count);
        foreach (var category in all)
        {
            var names = new List<string>();
            var seen = new HashSet<long>();
            for (Category? current = category; current is not null && seen.Add(current.Id); current = current.ParentId is long p ? byId.GetValueOrDefault(p) : null)
            {
                names.Add(current.Name);
            }

            names.Reverse();
            paths[category.Id] = string.Join('/', names);
        }

        return paths;
    }

    /// <summary>The AI's filter as the library's; null when it sets nothing.</summary>
    /// <exception cref="AiCallException">An unknown type or mode, or a negative size (invalid_argument).</exception>
    public static ItemFilter? ToItemFilter(AiFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        List<string>? types = null;
        if (filter.Types is { Count: > 0 })
        {
            types = [];
            foreach (var type in filter.Types)
            {
                var key = (type ?? "").Trim().ToLowerInvariant();
                if (!TypeKeys.Contains(key))
                {
                    throw AiCallException.Invalid(
                        $"Unknown type \"{type}\". Types are: {string.Join(", ", TypeKeys.Order(StringComparer.Ordinal))}.");
                }

                types.Add(key);
            }
        }

        ItemMode? mode = null;
        if (filter.Mode is not null)
        {
            mode = ParseMode(filter.Mode) ?? throw AiCallException.Invalid($"Unknown mode \"{filter.Mode}\": use \"copy\" or \"pointer\".");
        }

        if (filter.MinSizeBytes < 0 || filter.MaxSizeBytes < 0)
        {
            throw AiCallException.Invalid("Sizes can't be negative.");
        }

        var extensions = filter.Extensions?
            .Select(ExtensionMap.Normalize)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new ItemFilter
        {
            TypeKeys = types,
            Extensions = extensions is { Count: > 0 } ? extensions : null,
            Mode = mode,
            MissingOnly = filter.MissingOnly == true,
            AddedFrom = filter.AddedFrom,
            AddedTo = filter.AddedTo,
            MinSizeBytes = filter.MinSizeBytes,
            MaxSizeBytes = filter.MaxSizeBytes,
        };
        return result.IsEmpty ? null : result;
    }

    /// <summary>"1,204 files (3.2 GB)" in English, for the AI's operation summaries.</summary>
    public static string FilesAndSize(int files, long bytes)
        => $"{files.ToString("N0", CultureInfo.InvariantCulture)} {(files == 1 ? "file" : "files")} ({Size(bytes)})";

    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} {(bytes == 1 ? "byte" : "bytes")}"
            : $"{value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>
    /// The client's name for the log and the toasts: control and format characters removed, trimmed, at most
    /// <paramref name="maxLength"/> characters; empty becomes <paramref name="fallback"/>.
    /// </summary>
    public static string CleanName(string? name, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(maxLength);
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                || rune == Rune.ReplacementChar || (sb.Length == 0 && Rune.IsWhiteSpace(rune)))
            {
                continue;
            }

            if (sb.Length + rune.Utf16SequenceLength > maxLength)
            {
                break;
            }

            sb.Append(rune.ToString());
        }

        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? fallback : text;
    }
}
