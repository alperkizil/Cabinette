using System.IO;
using Cabinette.Core.Abstractions;

namespace Cabinette.Windows.Tests;

/// <summary>
/// Tests that really recycle files leave them in the user's Recycle Bin, so they only run when
/// CABINETTE_RECYCLE_TESTS=1. The locked-file and missing-path tests always run (nothing gets recycled).
/// </summary>
public sealed class RecycleBinTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CABINETTE_RECYCLE_TESTS") == "1";

    [Fact]
    public void RecyclesFilesAndFoldersFromBackgroundThread()
    {
        if (!Enabled)
        {
            return;
        }

        using var temp = new TempFolder();
        var file1 = temp.File("Cabinette recycle test 1.txt");
        var file2 = temp.File("Cabinette recycle test 2.txt");
        var folder = Path.Combine(temp.Path, "Cabinette recycle test folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inner.txt"), "inner");
        var keep = temp.File("keep.txt");

        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        new ShellRecycleBin().SendToRecycleBin([file1, file2, folder, Path.Combine(temp.Path, "missing.txt")]);

        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(keep));
    }

    /// <summary>How Core calls it: from Task.Run on a thread-pool (MTA) thread, several at once.</summary>
    [Fact]
    public async Task RecyclesFromThreadPoolThreads()
    {
        if (!Enabled)
        {
            return;
        }

        using var temp = new TempFolder();
        var files = Enumerable.Range(0, 4).Select(i => temp.File($"Cabinette recycle pool {i}.txt")).ToList();

        await Task.WhenAll(files.Select(file => Task.Run(() =>
        {
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            new ShellRecycleBin().SendToRecycleBin([file]);
        })));

        Assert.All(files, f => Assert.False(File.Exists(f)));
    }

    [Fact]
    public async Task FailureFromThreadPoolThreadSurfacesAsIOException()
    {
        using var temp = new TempFolder();
        var file = temp.File("Cabinette locked pool.txt");
        using var lockStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => new ShellRecycleBin().SendToRecycleBin([file])));
    }

    [Fact]
    public void RecyclesFromStaThread()
    {
        if (!Enabled)
        {
            return;
        }

        using var temp = new TempFolder();
        var file = temp.File("Cabinette recycle test sta.txt");

        Sta.Run(() => new ShellRecycleBin().SendToRecycleBin([file]));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void IgnoresPathsThatDoNotExist()
    {
        new ShellRecycleBin().SendToRecycleBin([@"C:\Cabinette\definitely\not\here.txt", "", "   "]);
        new ShellRecycleBin().SendToRecycleBin([]);
    }

    [Fact]
    public void LockedFileThrowsIOException()
    {
        using var temp = new TempFolder();
        var file = temp.File("Cabinette locked.txt");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => new ShellRecycleBin().SendToRecycleBin([file]));
            Assert.Contains("Recycle Bin", ex.Message);
        }

        Assert.True(File.Exists(file));
    }

    // ------------------------------------------------------------------ CanRecycle (reads settings only, never deletes)

    [Fact]
    public void SystemDriveHasRecycleBin()
    {
        using var temp = new TempFolder();
        var bin = new ShellRecycleBin();

        Assert.True(bin.CanRecycle(temp.File("can recycle.txt")));
        Assert.True(bin.CanRecycle(temp.Path));
        Assert.True(bin.CanRecycle(Path.GetPathRoot(Environment.SystemDirectory)!));
    }

    [Fact]
    public void CanRecycleAcceptsPathsThatDoNotExist()
    {
        var bin = new ShellRecycleBin();
        var missing = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Cabinette", Guid.NewGuid().ToString("N"), "not here.txt");

        Assert.True(bin.CanRecycle(missing));
        Assert.True(bin.CanRecycle(""));
        Assert.True(bin.CanRecycle("   "));
        Assert.True(bin.CanRecycle("bad\0path"));
    }

    [Theory]
    [InlineData(3u, true)] // fixed
    [InlineData(0u, true)] // unknown: let the shell decide
    [InlineData(1u, true)] // no root dir: let the shell decide
    [InlineData(2u, false)] // removable
    [InlineData(4u, false)] // network
    [InlineData(5u, false)] // CD/DVD
    [InlineData(6u, false)] // RAM disk
    public void DriveTypesWithoutRecycleBin(uint driveType, bool expected)
    {
        Assert.Equal(expected, ShellRecycleBin.DriveTypeHasRecycleBin(driveType));
    }

    // ------------------------------------------------------------------ outcome of an operation (no shell involved)

    [Fact]
    public void ItemsRefusedForPermanentDeleteThrowRecycleBinUnavailable()
    {
        var failure = ShellRecycleBin.BuildFailure([@"D:\a.txt", @"D:\b"], [], unchecked((int)0x800704C7), aborted: true);

        var ex = Assert.IsType<RecycleBinUnavailableException>(failure);
        Assert.Equal([@"D:\a.txt", @"D:\b"], ex.Paths);
    }

    [Fact]
    public void OtherFailuresStayPlainIOExceptions()
    {
        var mixed = ShellRecycleBin.BuildFailure([@"D:\a.txt"], [(@"C:\locked.txt", "in use")], 0, aborted: false);
        Assert.IsType<IOException>(mixed);
        Assert.Contains("locked.txt", mixed!.Message);
        Assert.Contains("a.txt", mixed.Message);

        Assert.IsType<IOException>(ShellRecycleBin.BuildFailure([], [], unchecked((int)0x80004005), aborted: false));
        Assert.IsType<IOException>(ShellRecycleBin.BuildFailure([], [], 0, aborted: true));
        Assert.Null(ShellRecycleBin.BuildFailure([], [], 0, aborted: false));
    }
}
