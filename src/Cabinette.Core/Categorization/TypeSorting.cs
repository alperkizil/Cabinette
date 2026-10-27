using Cabinette.Core.Models;

namespace Cabinette.Core.Categorization;

/// <summary>
/// "Type categories sort by type" (SPEC.md section 5): which files dropped onto a category, or picked with its paperclip,
/// go to their own type category instead. Shared by the library and the in-memory sample library.
/// </summary>
public static class TypeSorting
{
    /// <summary>
    /// The built-in key a category sorts by, or null when it keeps whatever is added: only built-in type categories sort
    /// (Images, Code, Code › Java, Other and so on), renamed ones too since the key survives a rename. User categories,
    /// including subcategories created inside a built-in one (Images › Holidays), and "Deleted" keep everything.
    /// </summary>
    public static string? SortingKey(CategoryKind kind, string? builtInKey)
        => kind == CategoryKind.BuiltIn && !string.IsNullOrEmpty(builtInKey) && builtInKey != ExtensionMap.DeletedKey
            ? builtInKey
            : null;

    /// <inheritdoc cref="SortingKey(CategoryKind, string?)"/>
    public static string? SortingKey(Category category) => SortingKey(category.Kind, category.BuiltInKey);

    /// <summary>
    /// The built-in key of the type category a file with <paramref name="extension"/> goes to when it is added to a category
    /// that sorts by <paramref name="sortingKey"/> (see <see cref="SortingKey(CategoryKind, string?)"/>), or null when it
    /// stays there: the category doesn't sort, the file's type is unknown (it would go to "Other"), or it already is the
    /// category's type. The type is the most specific one (".py" → "code.python"), as auto-categorizing uses.
    /// </summary>
    public static string? Destination(string? sortingKey, string? extension)
    {
        if (sortingKey is null)
        {
            return null;
        }

        var key = ExtensionMap.GetCategoryKey(extension);
        if (key == ExtensionMap.OtherKey || string.Equals(key, sortingKey, StringComparison.Ordinal))
        {
            return null;
        }

        // A category that keeps this type although it sorts elsewhere (E-books › PDF keeps PDFs): it stays.
        if (ExtensionMap.GetDefinition(sortingKey)?.Keeps is { } keeps && keeps.Contains(ExtensionMap.Normalize(extension)))
        {
            return null;
        }

        // Notes keeps text files (.md, .txt and so on): markdown notes belong in Notes (decided 2026-10-22).
        return sortingKey == ExtensionMap.NotesKey && key == TextKey ? null : key;
    }

    private const string TextKey = "text";
}
