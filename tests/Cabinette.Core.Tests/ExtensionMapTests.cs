using Cabinette.Core.Categorization;

namespace Cabinette.Core.Tests;

public sealed class ExtensionMapTests
{
    [Fact]
    public void EachExtensionBelongsToExactlyOneCategory()
    {
        var duplicates = ExtensionMap.BuiltInCategories
            .SelectMany(d => d.Extensions.Select(ext => (ext, d.Key)))
            .GroupBy(x => x.ext)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Key))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("ts", "code.jsts")]
    [InlineData(".TSX", "code.jsts")]
    [InlineData("m2ts", "video")]
    [InlineData("py", "code.python")]
    [InlineData("docx", "documents.word")]
    [InlineData("pdf", "documents.pdf")]
    [InlineData("rtf", "documents")]
    [InlineData("msi", "executable.msi")]
    [InlineData("exe", "executable.exe")]
    [InlineData("epub", "ebooks.epub")]
    [InlineData("mobi", "ebooks")]
    [InlineData("dll", "executable")]
    [InlineData("webloc", "links")]
    [InlineData("ply", "models3d")]
    [InlineData("ora", "design")]
    [InlineData("resx", "code.csharp")]
    [InlineData("note", "notes")]
    [InlineData("enex", "notes")]
    [InlineData("org", "notes")]
    [InlineData("xyz", "other")]
    [InlineData("bak", "other")]
    [InlineData("", "other")]
    public void MapsExtensionToMostSpecificCategory(string extension, string expectedKey)
        => Assert.Equal(expectedKey, ExtensionMap.GetCategoryKey(extension));
}
