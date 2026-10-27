using Cabinette.App.Services;
using Cabinette.Core.Categorization;
using Wpf.Ui.Controls;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt of the built-in subcategories added on 2026-10-24 (docs/review/bughunt-2026-10-24-subcategories), App side.
/// </summary>
public sealed class SubcategoriesReviewTests
{
    /// <summary>
    /// <see cref="Glyphs.ForIconKey"/> draws the avatar until a category has its generated PNG (the first start after the
    /// update, for every older library) and whenever the generator is unavailable; <c>--sample-icons</c> dev runs render every
    /// avatar from it. The Code subcategories map to their parent's glyph; the six new keys were not added, so PDF, Word, MSI,
    /// Exe and EPUB fall through to the folder glyph of a user category.
    /// </summary>
    [Fact]
    public void F5_TheFallbackAvatar_ShowsAFolder_ForTheNewSubcategories()
    {
        var wrong = new List<string>();
        foreach (var def in ExtensionMap.BuiltInCategories)
        {
            var glyph = Glyphs.ForIconKey(def.IconKey);
            var expected = def.ParentKey is { } parent ? Glyphs.ForIconKey(ExtensionMap.GetDefinition(parent)!.IconKey) : glyph;
            if (glyph == SymbolRegular.Folder24 || glyph != expected)
            {
                wrong.Add($"{def.IconKey} → {glyph}");
            }
        }

        Assert.Empty(wrong);
    }
}
