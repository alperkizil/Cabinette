using System.Collections.Frozen;
using Cabinette.Core.Data;
using Cabinette.Core.Models;

namespace Cabinette.Core.Categorization;

/// <summary>
/// Which items have a date taken (SPEC.md section 4, "Gallery"): files whose extension belongs to the built-in Images or Video
/// category (type key "images" or "video"). Notes and links aren't photos, and a pasted image is a PNG Cabinette wrote from the
/// clipboard, which never carries one.
/// </summary>
internal static class DateTakenTypes
{
    /// <summary>The extensions (lower-case, no dot).</summary>
    public static FrozenSet<string> Extensions { get; } =
        ExtensionMap.BuiltInCategories
            .Where(d => d.Key is ExtensionMap.ImagesKey or VideoKey)
            .SelectMany(d => d.Extensions)
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary><see cref="Extensions"/> as a JSON array, for <c>json_each(@param)</c>.</summary>
    public static string ExtensionsJson { get; } = DbValues.JsonArray(Extensions.Order(StringComparer.Ordinal));

    private const string VideoKey = "video";

    public static bool Applies(ItemKind kind, string extension) => kind == ItemKind.File && Extensions.Contains(extension);
}
