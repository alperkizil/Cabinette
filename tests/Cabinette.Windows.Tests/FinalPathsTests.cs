using System.Diagnostics;
using System.IO;

namespace Cabinette.Windows.Tests;

/// <summary>Final paths of temp folders only; the junctions are made and removed inside the test's own temp folder.</summary>
public sealed class FinalPathsTests
{
    [Fact]
    public void A_junction_resolves_to_its_target()
    {
        using var temp = new TempFolder();
        var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName;
        var link = Path.Combine(temp.Path, "link");
        MakeJunction(link, target);
        try
        {
            Assert.Equal(Final(target), FinalPaths.TryResolve(link));
            Assert.Equal(Path.Combine(Final(target), "sub", "file.txt"), FinalPaths.TryResolve(Path.Combine(link, "sub", "file.txt"))); // not there yet
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_plain_folder_resolves_to_itself_without_a_trailing_separator()
    {
        using var temp = new TempFolder();

        Assert.Equal(Final(temp.Path), FinalPaths.TryResolve(temp.Path + @"\"));
        Assert.DoesNotContain(@"\\?\", FinalPaths.TryResolve(temp.Path));
    }

    [Fact]
    public void Nothing_to_resolve_gives_null()
    {
        Assert.Null(FinalPaths.TryResolve("\0"));
        Assert.Null(FinalPaths.TryResolve(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\nothing"));
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\x", @"C:\Users\x")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void The_long_path_prefix_is_removed(string final, string expected)
        => Assert.Equal(expected, FinalPaths.StripPrefix(final));

    /// <summary>The temp folder's own final form (the temp path itself may go through a short name or a link).</summary>
    private static string Final(string path) => FinalPaths.TryResolve(path) ?? throw new InvalidOperationException(path);

    private static void MakeJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        mklink!.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction could not be created on this machine");
    }
}
