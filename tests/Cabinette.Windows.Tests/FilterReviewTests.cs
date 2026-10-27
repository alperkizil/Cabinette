namespace Cabinette.Windows.Tests;

/// <summary>
/// Review of the IFilter extractor (branch review/file-content-bughunt), fixed on fix/file-content: each F-test is the
/// regression test for its finding (its summary says what the finding was).
/// </summary>
public sealed class FilterReviewTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>
    /// F5 was: FilterTextExtractor.Pull sized its StringBuilder with <c>Math.Min(maxChars + 1, 64 * 1024)</c>, which wraps
    /// to int.MinValue for maxChars = int.MaxValue, so the extraction failed before the filter was even created (the child
    /// accepts any <c>--max-chars</c> and answered exit code 45). Now the size is computed in long, and any count works.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    public async Task F5_MaxChars_up_to_int_MaxValue_extracts_the_text(int maxChars)
    {
        var path = _folder.File("plain.txt", "Hello from the plain-text filter.");
        var result = await new FilterTextExtractor().ExtractAsync(path, maxChars);
        Assert.NotNull(result);
        Assert.Contains("Hello from the plain-text filter.", result.Text);
        Assert.False(result.Truncated);
    }
}
