using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;

namespace Cabinette.Core.Tests;

/// <summary>
/// The Mark of the Web (the Zone.Identifier alternate data stream browsers and mail programs put on downloads) goes along with
/// every copy into a vault: adds in copy mode, Migrate and Move vault. Temp files only.
/// </summary>
public sealed class MarkOfTheWebTests
{
    private const string Zone = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/\r\nHostUrl=https://example.com/report.docm\r\n";

    [Fact]
    public async Task A_copy_mode_add_keeps_the_mark()
    {
        await using var t = await TestLibrary.CreateAsync();
        var source = Marked(t.CreateFile("report.docm", "macros inside"));

        var item = await t.AddOneAsync(null, source);

        Assert.Equal("macros inside", File.ReadAllText(item.FullPath));
        Assert.Equal(Zone, ReadMark(item.FullPath));
        Assert.Equal(Zone, ReadMark(source)); // the original keeps it too
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(item.FullPath)); // writing the mark changed no date
    }

    [Fact]
    public async Task A_file_without_a_mark_gets_none()
    {
        await using var t = await TestLibrary.CreateAsync();
        var item = await t.AddOneAsync(null, t.CreateFile("notes.txt", "local notes"));

        Assert.Null(ReadMark(item.FullPath));
    }

    [Fact]
    public async Task A_file_found_again_keeps_the_mark_of_the_file_that_brought_it_back()
    {
        await using var t = await TestLibrary.CreateAsync();
        var item = await t.AddOneAsync(null, t.CreateFile("report.docm", "macros inside"));
        File.Delete(item.FullPath); // deleted outside Cabinette while monitoring was paused
        var again = Marked(t.CreateFile(@"again\report.docm", "macros inside"));

        var relinked = await t.AddOneAsync(null, again);

        Assert.Equal(item.Id, relinked.Id);
        Assert.Equal(Zone, ReadMark(relinked.FullPath));
    }

    [Fact]
    public async Task Migrate_keeps_the_mark_of_a_pointer_file()
    {
        await using var t = await TestLibrary.CreateAsync();
        var source = Marked(t.CreateFile("report.docm", "macros inside"));
        var pointer = await t.AddOneAsync(null, source, ItemMode.Pointer);

        var result = await t.Library.MigratePointersAsync();

        Assert.Equal(1, result.Migrated);
        var migrated = await t.ItemAsync(pointer.Id);
        Assert.Equal(ItemMode.Copy, migrated.Mode);
        Assert.Equal(Zone, ReadMark(migrated.FullPath));
    }

    [Fact]
    public async Task Move_vault_keeps_the_mark()
    {
        await using var t = await TestLibrary.CreateAsync();
        var item = await t.AddOneAsync(null, Marked(t.CreateFile("report.docm", "macros inside")));
        var target = Path.Combine(t.Root, "new vault");

        await t.Library.MoveVaultAsync(target);

        var moved = await t.ItemAsync(item.Id);
        Assert.StartsWith(target, moved.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Zone, ReadMark(moved.FullPath));
    }

    [Fact]
    public void A_mark_that_can_not_be_written_fails_the_copy_rather_than_being_dropped_silently()
    {
        var root = Directory.CreateTempSubdirectory("cabinette-motw-").FullName;
        try
        {
            var source = Marked(Path.Combine(root, "source.exe"), "exe");
            var destination = Path.Combine(root, "copy.exe");
            File.WriteAllText(destination, "exe");
            File.WriteAllText(destination + FileOps.ZoneIdentifierStream, "already there"); // e.g. no right to write it

            Assert.Throws<IOException>(() => FileOps.CopyMarkOfTheWeb(source, destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Marked(string path, string? content = null)
    {
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path + FileOps.ZoneIdentifierStream, Zone);
        File.SetLastWriteTimeUtc(path, stamp);
        return path;
    }

    private static string? ReadMark(string path)
    {
        try
        {
            return File.ReadAllText(path + FileOps.ZoneIdentifierStream);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
