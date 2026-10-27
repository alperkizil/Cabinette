using System.IO;

namespace Cabinette.Windows.Tests;

/// <summary>Only the failure paths: the success paths open Explorer or another app.</summary>
public sealed class ShellOperationsTests
{
    private readonly ShellOperations _operations = new();

    [Fact]
    public void OpenMissingFileThrowsFileNotFound()
    {
        using var temp = new TempFolder();
        var missing = Path.Combine(temp.Path, "missing.pdf");
        var ex = Assert.Throws<FileNotFoundException>(() => _operations.OpenWithDefaultApp(missing));
        Assert.Equal(missing, ex.FileName);
    }

    [Fact]
    public void ShowInExplorerWithNoExistingFolderThrows()
    {
        var usedLetters = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 23).Select(c => (char)c).FirstOrDefault(c => !usedLetters.Contains(c));
        if (free == default)
        {
            return;
        }

        Assert.Throws<DirectoryNotFoundException>(() => _operations.ShowInExplorer($@"{free}:\Cabinette\missing\file.txt"));
    }

    [Fact]
    public void RejectsEmptyPaths()
    {
        Assert.ThrowsAny<ArgumentException>(() => _operations.OpenWithDefaultApp(" "));
        Assert.ThrowsAny<ArgumentException>(() => _operations.ShowInExplorer(""));
    }
}
