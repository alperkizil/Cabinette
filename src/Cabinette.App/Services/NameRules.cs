using Cabinette.App.Localization;

namespace Cabinette.App.Services;

/// <summary>Client-side checks for names that become folder names in the vault. Core validates again.</summary>
public static class NameRules
{
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Returns an error message, or null when the name is acceptable.</summary>
    public static string? ValidateCategoryName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return L.Get("Category_NameRequired");
        }

        if (trimmed.IndexOfAny(InvalidChars) >= 0 || trimmed.Any(char.IsControl))
        {
            return L.Get("Category_NameInvalid");
        }

        if (trimmed is "." or "..")
        {
            return L.Get("Category_NameReserved");
        }

        return null;
    }
}
