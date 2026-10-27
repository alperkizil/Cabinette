using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cabinette.Windows.Tests;

/// <summary>The date taken of photos and videos (SPEC.md section 4, "Gallery"), read through the Windows property system.</summary>
public sealed class DateTakenTests
{
    private readonly ShellDateTakenReader _reader = new();

    /// <summary>
    /// A one-second 16×16 MPEG-4 video without metadata, made with
    /// <c>ffmpeg -f lavfi -i color=c=black:s=16x16:r=1:d=1 -c:v mpeg4 -fflags +bitexact -flags:v +bitexact -map_metadata -1 v.mp4</c>.
    /// Its creation times (mvhd, tkhd, mdhd) are 0, "not set"; <see cref="WriteMp4"/> fills them in.
    /// </summary>
    private const string Mp4Base64 =
        "AAAAHGZ0eXBpc29tAAACAGlzb21pc28ybXA0MQAAAAhmcmVlAAAAGW1kYXQAAAGzABAHAAABthYFGCPbfgAAAwttb292AAAAbG12" +
        "aGQAAAAAAAAAAAAAAAAAAAPoAAAD6AABAAABAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAACWnRyYWsAAABcdGtoZAAAAAMAAAAAAAAAAAAAAAEAAAAAAAAD6AAAAAAAAAAA" +
        "AAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAEAAAABAAAAAAACRlZHRzAAAAHGVsc3QAAAAAAAAA" +
        "AQAAA+gAAAAAAAEAAAAAAdJtZGlhAAAAIG1kaGQAAAAAAAAAAAAAAAAAAEAAAABAAFXEAAAAAAAtaGRscgAAAAAAAAAAdmlkZQAA" +
        "AAAAAAAAAAAAAFZpZGVvSGFuZGxlcgAAAAF9bWluZgAAABR2bWhkAAAAAQAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAAB" +
        "AAAADHVybCAAAAABAAABPXN0YmwAAADZc3RzZAAAAAAAAAABAAAAyW1wNHYAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAEAAQAEgA" +
        "AABIAAAAAAAAAAEKTGF2YyBtcGVnNAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY//8AAABPZXNkcwAAAAADgICAPgABAASAgIAwIBEA" +
        "AAAAAw1AAAAAiAWAgIAeAAABsAEAAAG1iRMAAAEAAAABIADEjYgADQCEAhRjBoCAgAECAAAAEHBhc3AAAAABAAAAAQAAABRidHJ0" +
        "AAAAAAADDUAAAACIAAAAGHN0dHMAAAAAAAAAAQAAAAEAAEAAAAAAHHN0c2MAAAAAAAAAAQAAAAEAAAABAAAAAQAAABRzdHN6AAAA" +
        "AAAAABEAAAABAAAAFHN0Y28AAAAAAAAAAQAAACwAAAA9dWR0YQAAADVtZXRhAAAAAAAAACFoZGxyAAAAAAAAAABtZGlyYXBwbAAA" +
        "AAAAAAAAAAAAAAhpbHN0";

    public static TheoryData<DateTime> CameraTimes => new()
    {
        // Winter and summer of a year in which this PC's zone may have had daylight saving time (Turkey did until 2016), so a
        // conversion with today's offset instead of that date's would show up as an hour off.
        new DateTime(2015, 1, 14, 15, 30, 45),
        new DateTime(2015, 7, 14, 15, 30, 45),
        new DateTime(2025, 3, 2, 8, 5, 6),
        new DateTime(2003, 12, 31, 23, 59, 59), // late in the evening: the UTC date is another day in zones east of UTC
    };

    [Theory]
    [MemberData(nameof(CameraTimes))]
    public void Jpeg_ReturnsTheInstantWhoseLocalTimeIsTheCameraTime(DateTime cameraTime)
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "photo.jpg");
        WriteJpeg(path, cameraTime);

        // The file holds the wall-clock time, as a camera writes it (EXIF has no zone).
        var exif = cameraTime.ToString("yyyy':'MM':'dd HH':'mm':'ss", CultureInfo.InvariantCulture);
        Assert.Contains(exif, Encoding.ASCII.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);

        var taken = _reader.ReadDateTaken(path);

        Assert.NotNull(taken);

        // The camera's wall-clock time with the offset it had when read (date-taken bug hunt F1), and the same instant as before.
        Assert.Equal(cameraTime, taken.Value.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(taken.Value.UtcDateTime), taken.Value.Offset);
        Assert.Equal(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(cameraTime, TimeZoneInfo.Local)), taken.Value);
    }

    [Fact]
    public void JpegWithoutDateTaken_ReturnsNull()
    {
        using var temp = new TempFolder();
        var jpeg = Path.Combine(temp.Path, "screenshot.jpg");
        WriteJpeg(jpeg, null);
        var png = Path.Combine(temp.Path, "screenshot.png");
        ThumbnailTests.WritePng(png, 20, 10, dc => dc.DrawRectangle(Brushes.Teal, null, new System.Windows.Rect(0, 0, 20, 10)));

        Assert.Null(_reader.ReadDateTaken(jpeg));
        Assert.Null(_reader.ReadDateTaken(png));
    }

    [Fact]
    public void Video_ReturnsItsCreationTime_WhichIsStoredAsUtc()
    {
        using var temp = new TempFolder();
        var created = new DateTimeOffset(2015, 1, 14, 12, 30, 45, TimeSpan.Zero);
        var path = Path.Combine(temp.Path, "clip.mp4");
        WriteMp4(path, created);
        var unset = Path.Combine(temp.Path, "unset.mp4");
        WriteMp4(unset, null);

        Assert.Equal(created, _reader.ReadDateTaken(path));
        Assert.Null(_reader.ReadDateTaken(unset));

        // A few seconds after QuickTime's zero (1904-01-01) is a clock that was never set, not a date.
        var nearZero = Path.Combine(temp.Path, "near-zero.mp4");
        WriteMp4(nearZero, new DateTimeOffset(1904, 1, 1, 0, 0, 5, TimeSpan.Zero));
        Assert.Null(_reader.ReadDateTaken(nearZero));
    }

    [Fact]
    public void ThroughTheShell_TheSameValues()
    {
        using var temp = new TempFolder();
        var photo = Path.Combine(temp.Path, "photo.jpg");
        WriteJpeg(photo, new DateTime(2015, 1, 14, 15, 30, 45));
        var video = Path.Combine(temp.Path, "clip.mp4");
        WriteMp4(video, new DateTimeOffset(2021, 6, 1, 8, 0, 0, TimeSpan.Zero));
        var none = Path.Combine(temp.Path, "none.jpg");
        WriteJpeg(none, null);
        var shell = new ShellDateTakenReader(shellOnly: true);

        foreach (var path in new[] { photo, video, none })
        {
            Assert.Equal(shell.ReadDateTaken(path), _reader.ReadDateTaken(path));
        }

        Assert.NotNull(_reader.ReadDateTaken(photo));
        Assert.NotNull(_reader.ReadDateTaken(video));
    }

    [Fact]
    public void MissingUnsupportedOrDamaged_ReturnsNull()
    {
        using var temp = new TempFolder();
        Assert.Null(_reader.ReadDateTaken(Path.Combine(temp.Path, "missing.jpg")));
        Assert.Null(_reader.ReadDateTaken(Path.Combine(temp.Path, "no-folder", "missing.mp4")));
        Assert.Null(_reader.ReadDateTaken(temp.File("notes.txt", "hello")));
        Assert.Null(_reader.ReadDateTaken(temp.File("not-really.jpg", "this is text, not a JPEG")));
        Assert.Null(_reader.ReadDateTaken(temp.File("not-really.mp4", "this is text, not a video")));
        Assert.Null(_reader.ReadDateTaken(temp.File("noextension", "x")));
        Directory.CreateDirectory(Path.Combine(temp.Path, "folder.jpg"));
        Assert.Null(_reader.ReadDateTaken(Path.Combine(temp.Path, "folder.jpg")));
        Assert.Null(_reader.ReadDateTaken(""));
        Assert.Null(_reader.ReadDateTaken("   "));
        Assert.Null(_reader.ReadDateTaken("\0bad"));
    }

    [Fact]
    public void FileCanBeRenamedAndDeletedRightAfterReading()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "photo.jpg");
        WriteJpeg(path, new DateTime(2020, 5, 5, 5, 5, 5));

        Assert.NotNull(_reader.ReadDateTaken(path));
        File.Move(path, path + ".renamed");
        File.Delete(path + ".renamed");
    }

    [Fact]
    public void ThreadSafeUnderParallelCalls_AndFromSta()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "parallel.jpg");
        var cameraTime = new DateTime(2019, 7, 14, 15, 30, 45);
        WriteJpeg(path, cameraTime);
        var expected = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(cameraTime, TimeZoneInfo.Local));

        var results = new DateTimeOffset?[64];
        Parallel.For(0, results.Length, i => results[i] = _reader.ReadDateTaken(path));
        Assert.All(results, r => Assert.Equal(expected, r));

        Assert.Equal(expected, Sta.Run(() => _reader.ReadDateTaken(path)));
    }

    /// <summary>A small JPEG, with <paramref name="dateTaken"/> (local wall-clock time) as its EXIF date taken when given.</summary>
    internal static void WriteJpeg(string path, DateTime? dateTaken) => Sta.Run(() =>
    {
        var pixels = new byte[16 * 16 * 3];
        Array.Fill(pixels, (byte)0x80);
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr24, null, pixels, 16 * 3);
        BitmapMetadata? metadata = null;
        if (dateTaken is { } taken)
        {
            // WPF parses this with the invariant culture and stores it through WIC's System.Photo.DateTaken policy.
            metadata = new BitmapMetadata("jpg") { DateTaken = taken.ToString("MM'/'dd'/'yyyy HH':'mm':'ss", CultureInfo.InvariantCulture) };
        }

        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    });

    /// <summary>The test video with its creation and modification times set to <paramref name="created"/> (null: left unset).</summary>
    internal static void WriteMp4(string path, DateTimeOffset? created)
    {
        var bytes = Convert.FromBase64String(Mp4Base64);
        var seconds = created is { } c ? (uint)(c.UtcDateTime - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : 0u;
        foreach (var box in new[] { "mvhd", "tkhd", "mdhd" })
        {
            var at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(box));
            Assert.True(at > 0 && bytes[at + 4] == 0, $"{box} (version 0) not found");
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 8), seconds);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 12), seconds);
        }

        File.WriteAllBytes(path, bytes);
    }
}
