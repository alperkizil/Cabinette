namespace Cabinette.Windows.Tests;

/// <summary>
/// Bug hunt 2026-10-15, #8 part 1: <see cref="ShellRecycleBin.CanRecycle"/> also says no for a file larger than its drive's
/// Recycle Bin maximum size, so Core catches "too big for the Recycle Bin" before anything changes. Only the pure rules are
/// tested here; nothing reads or writes the registry, and nothing is recycled.
/// </summary>
public sealed class BugHuntDeleteFirstRunTests
{
    private const long Mb = 1024 * 1024;

    [Theory]
    [InlineData(10 * Mb + 1, 10L, true)]   // larger than the limit: Windows would delete it permanently
    [InlineData(21 * Mb, 10L, true)]
    [InlineData(10 * Mb, 10L, false)]      // exactly the limit: not refused (conservative)
    [InlineData(10 * Mb - 1, 10L, false)]
    [InlineData(0L, 10L, false)]
    [InlineData(10_000_001L, 10L, false)]  // 10 decimal MB + 1 byte: still under 10 MiB, so not refused (the larger reading of MB)
    public void F8_FileLargerThanMaxCapacity_IsTooLargeForTheRecycleBin(long size, long maxCapacityMb, bool expected)
    {
        Assert.Equal(expected, ShellRecycleBin.IsTooLargeForRecycleBin(size, maxCapacityMb));
    }

    [Fact]
    public void F8_UnknownSizeOrUnknownLimit_IsNeverRefused()
    {
        Assert.False(ShellRecycleBin.IsTooLargeForRecycleBin(null, 10));           // a folder, or the path isn't there
        Assert.False(ShellRecycleBin.IsTooLargeForRecycleBin(long.MaxValue, null)); // no MaxCapacity value
        Assert.False(ShellRecycleBin.IsTooLargeForRecycleBin(long.MaxValue, 0));
        Assert.False(ShellRecycleBin.IsTooLargeForRecycleBin(long.MaxValue, -5));
        Assert.False(ShellRecycleBin.IsTooLargeForRecycleBin(long.MaxValue, long.MaxValue)); // no overflow
        Assert.True(ShellRecycleBin.IsTooLargeForRecycleBin(long.MaxValue, uint.MaxValue));
    }

    [Fact]
    public void F8_MaxCapacityRegistryValue_IsReadAsAnUnsignedDwordInMb()
    {
        Assert.Equal<long?>(10, ShellRecycleBin.ParseMaxCapacityMb(10));
        Assert.Equal<long?>(0xF9BB, ShellRecycleBin.ParseMaxCapacityMb(0xF9BB));
        Assert.Equal<long?>(uint.MaxValue, ShellRecycleBin.ParseMaxCapacityMb(-1)); // a DWORD above int.MaxValue reads as negative
        Assert.Equal<long?>(5000, ShellRecycleBin.ParseMaxCapacityMb(5000L));      // a QWORD, should one be there
    }

    [Fact]
    public void F8_MissingZeroOrOddMaxCapacityValues_MeanNoKnownLimit()
    {
        Assert.Null(ShellRecycleBin.ParseMaxCapacityMb(null));
        Assert.Null(ShellRecycleBin.ParseMaxCapacityMb(0));
        Assert.Null(ShellRecycleBin.ParseMaxCapacityMb(0L));
        Assert.Null(ShellRecycleBin.ParseMaxCapacityMb(-3L));
        Assert.Null(ShellRecycleBin.ParseMaxCapacityMb("10"));
        Assert.Null(ShellRecycleBin.ParseMaxCapacityMb(new byte[] { 10, 0, 0, 0 }));
    }
}
