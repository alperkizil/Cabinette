using Cabinette.Core.Categorization;

namespace Cabinette.App.Services;

/// <summary>The extension box of the thread and main-window filters: "pdf, .DOCX; *.md" → pdf, docx, md.</summary>
public static class ExtensionList
{
    private static readonly char[] Separators = [',', ';', ' ', '\t'];

    /// <summary>Splits on spaces, commas and semicolons; each entry goes through <see cref="ExtensionMap.Normalize"/>.</summary>
    public static IReadOnlyList<string> Parse(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ExtensionMap.Normalize)
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
}
