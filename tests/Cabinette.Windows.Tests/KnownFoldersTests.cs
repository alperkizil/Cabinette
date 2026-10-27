using System.IO;

namespace Cabinette.Windows.Tests;

/// <summary>Only asks Windows for the path: the Downloads folder itself is never opened, listed or changed.</summary>
public sealed class KnownFoldersTests
{
    [Fact]
    public void The_Downloads_folder_path_is_a_full_local_path()
    {
        var downloads = KnownFolders.TryGetDownloads();

        Assert.NotNull(downloads);
        Assert.True(Path.IsPathFullyQualified(downloads), downloads);
    }

    [Fact]
    public void The_Downloads_id_is_FOLDERID_Downloads()
    {
        Assert.Equal(new Guid("374DE290-123F-4565-9164-39C4925E467B"), KnownFolders.DownloadsId);
    }
}
