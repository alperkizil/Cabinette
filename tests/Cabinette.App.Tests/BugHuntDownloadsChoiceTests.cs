using System.Diagnostics;
using System.IO;
using Cabinette.App.Services;
using Cabinette.App.Services.Downloads;

namespace Cabinette.App.Tests;

/// <summary>
/// Bug hunt of the Clean My Downloads branch (reviewer: Fable), turned into regression tests: the dev-flag guard compared
/// spellings, not folders. Everything happens in a temp folder; the real Downloads folder is only a stand-in path here and is
/// never asked for.
/// </summary>
public sealed class BugHuntDownloadsChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "junction-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (var link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link); // removes the junction only
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Downloads_dir_through_a_junction_to_the_real_Downloads_folder_is_refused()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName; // stands in for the real folder
        var link = Junction(Path.Combine(_root, "dl-link"), real);

        var chosen = DownloadsFolderChoice.Resolve(Options("--downloads-dir", link), () => real);

        Assert.Null(chosen); // the junction is the very same folder
        Assert.Null(DownloadsFolderChoice.Resolve(Options("--downloads-dir", real), () => real));
        Assert.Null(DownloadsFolderChoice.Resolve(Options("--downloads-dir", link + @"\"), () => real));
    }

    [Fact]
    public void A_real_Downloads_folder_that_is_itself_a_link_is_recognised_too()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "D-drive Downloads")).FullName;
        var real = Junction(Path.Combine(_root, "Downloads"), elsewhere); // Downloads moved with a junction

        Assert.Null(DownloadsFolderChoice.Resolve(Options("--downloads-dir", elsewhere), () => real));
    }

    [Fact]
    public void Downloads_dir_by_its_short_8_3_name_is_refused()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "Downloads with a long name")).FullName;
        var shortName = ShortName(real);
        if (shortName is null || string.Equals(shortName, real, StringComparison.OrdinalIgnoreCase))
        {
            return; // This volume doesn't make 8.3 names: nothing to walk around.
        }

        Assert.Null(DownloadsFolderChoice.Resolve(Options("--downloads-dir", shortName), () => real));
    }

    [Fact]
    public void Another_folder_is_still_allowed()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        var dev = Directory.CreateDirectory(Path.Combine(_root, "dev downloads")).FullName;
        var link = Junction(Path.Combine(_root, "dev-link"), dev);

        Assert.Equal(dev, DownloadsFolderChoice.Resolve(Options("--downloads-dir", dev), () => real));
        Assert.Equal(link, DownloadsFolderChoice.Resolve(Options("--downloads-dir", link), () => real));
        Assert.Equal(Path.Combine(_root, "not there yet"),
            DownloadsFolderChoice.Resolve(Options("--downloads-dir", Path.Combine(_root, "not there yet")), () => real));
    }

    [Fact]
    public void The_vault_check_sees_through_a_junction()
    {
        var vault = Directory.CreateDirectory(Path.Combine(_root, "vault", "Inbox")).Parent!.FullName;
        var link = Junction(Path.Combine(_root, "downloads-link"), Path.Combine(vault, "Inbox"));

        Assert.True(DownloadsFolderChoice.IsSameOrInside(link, vault));
        Assert.True(DownloadsFolderChoice.IsSameOrInside(Path.Combine(link, "sub"), vault));
        Assert.False(DownloadsFolderChoice.IsSameOrInside(Path.Combine(_root, "elsewhere"), vault));
    }

    private CommandLineOptions Options(string flag, string value)
        => CommandLineOptions.Parse(["--data-dir", Path.Combine(_root, "data"), flag, value]);

    private string Junction(string link, string target)
    {
        Directory.CreateDirectory(_root);

        // mklink /J needs no privilege.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        mklink!.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction could not be created on this machine");
        _links.Add(link);
        return link;
    }

    /// <summary>The folder's 8.3 short path as cmd reports it, or null.</summary>
    private static string? ShortName(string path)
    {
        using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c for %I in (\"{path}\") do @echo %~sI")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        var output = cmd!.StandardOutput.ReadToEnd().Trim();
        cmd.WaitForExit();
        return output.Length == 0 ? null : output;
    }
}
