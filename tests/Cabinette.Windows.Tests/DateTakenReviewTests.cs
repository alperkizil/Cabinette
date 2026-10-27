using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.Windows.Tests;

/// <summary>
/// Bug hunt of the date-taken reader (feat/date-taken, docs/review/bughunt-2026-10-24-datetaken). Each F-test fails on that
/// branch because of the finding it names and passes once it is fixed.
/// </summary>
public sealed class DateTakenReviewTests
{
    private readonly ShellDateTakenReader _reader = new();

    /// <summary>
    /// F1: a photo's EXIF date is the camera's wall-clock time, without a zone. The reader turns it into an instant with this
    /// PC's zone at the time of reading, and the Core stores that instant (UTC). Shown back as local time, the wall clock
    /// drifts as soon as the PC's zone differs from the one at reading time: a laptop that travels (or has "set time zone
    /// automatically" on), a zone corrected after first run, a backup restored on another PC. Explorer re-reads the file each
    /// time, so its "Date taken" never drifts. The value the reader returns must keep the camera time as its wall clock.
    /// </summary>
    [Theory]
    [InlineData(2015, 1, 14, 15, 30, 45)]
    [InlineData(2015, 7, 14, 15, 30, 45)]
    [InlineData(2003, 12, 31, 23, 59, 59)]
    public void F1_ExifWallClock_IsKeptAsTheCameraTime_NotOnlyAsAnInstantOfThisPcsZone(int y, int mo, int d, int h, int mi, int s)
    {
        var cameraTime = new DateTime(y, mo, d, h, mi, s);
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "photo.jpg");
        DateTakenTests.WriteJpeg(path, cameraTime);

        var taken = _reader.ReadDateTaken(path);

        Assert.NotNull(taken);

        // What the camera showed must survive a change of this PC's zone: the wall clock is in the value itself, with the
        // zone's offset for that date, not folded into UTC.
        Assert.Equal(cameraTime, taken.Value.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(cameraTime), taken.Value.Offset);
    }

    /// <summary>
    /// F2: the reader treats any date before 1904-01-02 as "not set". That cut-off belongs to QuickTime/MP4 creation times
    /// (which count from 1904-01-01), but it is applied to photos too, so a scanned photo whose archivist set the EXIF date to
    /// the day it was taken (1890s, 1900s) shows no date taken in Cabinette while Explorer shows it.
    /// </summary>
    [Fact]
    public void F2_PhotoDatedBefore1904_KeepsItsDate()
    {
        var cameraTime = new DateTime(1900, 6, 15, 12, 0, 0);
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "scan.jpg");
        DateTakenTests.WriteJpeg(path, cameraTime);

        // The file really carries the date (WIC wrote it as EXIF).
        var exif = cameraTime.ToString("yyyy':'MM':'dd HH':'mm':'ss", CultureInfo.InvariantCulture);
        Assert.Contains(exif, System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);

        var taken = _reader.ReadDateTaken(path);

        Assert.NotNull(taken);
        Assert.Equal(cameraTime, taken.Value.ToLocalTime().DateTime);
    }

    // ------------------------------------------------------------ leads checked (these pass; kept out of the F list)

    [Fact]
    public void Lead_LongPath_IsRead()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, new string('a', 120), new string('b', 120), new string('c', 40));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "photo.jpg");
        Assert.True(path.Length > 300);
        var cameraTime = new DateTime(2020, 5, 5, 5, 5, 5);
        DateTakenTests.WriteJpeg(path, cameraTime);

        var taken = _reader.ReadDateTaken(path);
        var throughShell = new ShellDateTakenReader(shellOnly: true).ReadDateTaken(path);

        Assert.NotNull(taken);
        Assert.Equal(cameraTime, taken.Value.ToLocalTime().DateTime);
        Assert.Equal(taken, throughShell);
    }

    [Fact]
    public void Lead_QuickTimeExtension_IsRead()
    {
        using var temp = new TempFolder();
        var created = new DateTimeOffset(2016, 2, 29, 10, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(temp.Path, "clip.mov");
        DateTakenTests.WriteMp4(path, created);

        Assert.Equal(created, _reader.ReadDateTaken(path));
    }

    [Fact]
    public void Lead_ExifZeroDate_IsNoDate()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "zero.jpg");
        WriteJpegWithExifDateString(path, "0000:00:00 00:00:00");
        Assert.Contains("0000:00:00 00:00:00", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);

        Assert.Null(_reader.ReadDateTaken(path));
    }

    [Fact]
    public void Lead_FileHeldByAWriterThatSharesRead_IsRead()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "open.jpg");
        var cameraTime = new DateTime(2020, 5, 5, 5, 5, 5);
        DateTakenTests.WriteJpeg(path, cameraTime);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var taken = _reader.ReadDateTaken(path);
            Assert.NotNull(taken);
            Assert.Equal(cameraTime, taken.Value.ToLocalTime().DateTime);
        }
    }

    [Fact]
    public void Lead_FileLockedExclusively_ReadsAsNoDate()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "locked.jpg");
        DateTakenTests.WriteJpeg(path, new DateTime(2020, 5, 5, 5, 5, 5));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // The reader can't say "couldn't look" apart from "no date": the Core stores this as no date.
            Assert.Null(_reader.ReadDateTaken(path));
        }

        Assert.NotNull(_reader.ReadDateTaken(path));
    }

    [Fact]
    public void Lead_ManyReads_LeakNoHandles()
    {
        using var temp = new TempFolder();
        var photo = Path.Combine(temp.Path, "photo.jpg");
        DateTakenTests.WriteJpeg(photo, new DateTime(2020, 5, 5, 5, 5, 5));
        var video = Path.Combine(temp.Path, "clip.mp4");
        DateTakenTests.WriteMp4(video, new DateTimeOffset(2021, 6, 1, 8, 0, 0, TimeSpan.Zero));
        var none = Path.Combine(temp.Path, "none.jpg");
        DateTakenTests.WriteJpeg(none, null);

        for (var i = 0; i < 100; i++)
        {
            _reader.ReadDateTaken(photo);
            _reader.ReadDateTaken(video);
            _reader.ReadDateTaken(none);
        }

        var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var before = process.HandleCount;
        for (var i = 0; i < 1000; i++)
        {
            Assert.NotNull(_reader.ReadDateTaken(photo));
            Assert.NotNull(_reader.ReadDateTaken(video));
            Assert.Null(_reader.ReadDateTaken(none));
        }

        process.Refresh();
        var after = process.HandleCount;
        Assert.True(after - before < 100, $"Handles went from {before} to {after} over 3000 reads.");
    }

    [Fact]
    public void Lead_FileCanBeRenamedAndDeleted_WhileTheReadersStreamIsOpen()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "held.jpg");
        DateTakenTests.WriteJpeg(path, new DateTime(2020, 5, 5, 5, 5, 5));

        // The exact open the reader hands the property handler.
        Assert.True(Interop.PropertyHandlerInterop.SHCreateStreamOnFileEx(path,
            Interop.PropertyHandlerInterop.STGM_READ | Interop.PropertyHandlerInterop.STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out var stream) >= 0);
        try
        {
            File.Move(path, path + ".renamed");
            File.Delete(path + ".renamed");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(stream);
        }
    }

    /// <summary>A small JPEG whose EXIF DateTimeOriginal (tag 36867) is exactly <paramref name="exifDate"/>.</summary>
    private static void WriteJpegWithExifDateString(string path, string exifDate) => Sta.Run(() =>
    {
        var pixels = new byte[16 * 16 * 3];
        Array.Fill(pixels, (byte)0x80);
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr24, null, pixels, 16 * 3);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", exifDate);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    });
}
