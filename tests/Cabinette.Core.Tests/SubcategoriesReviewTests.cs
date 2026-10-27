using System.IO.Compression;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// Bug hunt of the built-in subcategories added on 2026-10-24 (docs/review/bughunt-2026-10-24-subcategories). Each test fails
/// on branch feat/builtin-subcategories (5f760d5) because of the finding it is named after, and passes once it is fixed.
/// </summary>
/// <remarks>
/// Findings 1 to 3 share one cause: <c>AddLaterBuiltInsAsync</c> runs inside <c>OpenAsync</c> and creates the six categories'
/// vault folders (<c>InsertCategory</c> → <c>EnsureDirectory</c>), so opening an older library now touches the vault, which
/// opening never did before. "Older library" below means one made before 2026-10-24: no "builtins-added" settings row and none
/// of the six categories, as <c>LaterBuiltInsTests.MakeItAnOlderLibraryAsync</c> arranges it.
/// </remarks>
public sealed class SubcategoriesReviewTests
{
    private const string AddedMark = "builtins-added-2026-10-24";

    private static readonly string[] Six =
        ["documents.pdf", "documents.word", "executable.msi", "executable.exe", "ebooks.epub", "ebooks.pdf"];

    // ------------------------------------------------------------ 1: restoring an older backup

    /// <summary>
    /// A backup from before 2026-10-24 restored on another PC: the backup's database still names the old vault (restoring
    /// re-points it only after opening), and that path is on a drive this PC doesn't have. Opening the restored database now
    /// creates "Documents\PDF" at the old path, fails, and the whole restore is rolled back with "PDF could not be found".
    /// </summary>
    [Fact]
    public async Task F1_RestoringAnOlderBackup_FailsWhenItsOldVaultDriveIsGone()
    {
        if (MissingDriveLetter() is not { } drive)
        {
            return; // Every drive letter is in use on this PC; the scenario can't be set up here.
        }

        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var report = await source.AddOneAsync(docs.Id, source.CreateFile("report.pdf", "report"));
        var zip = Path.Combine(source.Root, "old.zip");
        await source.Library.CreateBackupAsync(zip);
        EditBackupDatabase(zip, connection =>
        {
            MakeOlder(connection);
            SetVaultPath(connection, $@"{drive}:\Cabinette");
        });

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        var restoredVault = Path.Combine(target.Root, "restored vault");

        target.Use(await LibraryFactory.RestoreAsync(zip, restoredVault, target.Options, target.RecycleBin));

        Assert.Equal(restoredVault, target.Library.VaultPath);
        var kept = await target.ItemAsync(report.Id);
        Assert.True(File.Exists(kept.FullPath), kept.FullPath);
        var all = await target.Library.GetAllCategoriesAsync();
        Assert.All(Six, key => Assert.Single(all, c => c.BuiltInKey == key));
    }

    /// <summary>
    /// The same restore on the PC that made the backup, where the old vault still exists (say, to look at an old backup next to
    /// the live library): the six folders are created inside the old vault, which the restore has no business touching
    /// ("only what this restore created goes; anything else ... stays"). The restored vault gets its folders either way.
    /// </summary>
    [Fact]
    public async Task F1_RestoringAnOlderBackup_CreatesFoldersInTheBackupsOldVault()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        await source.AddOneAsync(docs.Id, source.CreateFile("report.pdf", "report"));
        var zip = Path.Combine(source.Root, "old.zip");
        await source.Library.CreateBackupAsync(zip);
        EditBackupDatabase(zip, MakeOlder);

        // The old vault as an older library left it: without the six folders this build makes.
        foreach (var folder in (string[])[@"Documents\PDF", @"Documents\Word", @"Executable\MSI", @"Executable\Exe", @"E-books\EPUB", @"E-books\PDF"])
        {
            Directory.Delete(Path.Combine(source.Vault, folder));
        }

        var oldVaultBefore = Snapshot(source.Vault);

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        var restoredVault = Path.Combine(target.Root, "restored vault");
        target.Use(await LibraryFactory.RestoreAsync(zip, restoredVault, target.Options, target.RecycleBin));

        Assert.True(Directory.Exists(Path.Combine(restoredVault, "Documents", "PDF")));
        Assert.Equal(oldVaultBefore, Snapshot(source.Vault));
    }

    // ------------------------------------------------------------ 2: opening with the vault out of reach

    /// <summary>
    /// An older library whose vault is on a drive that isn't connected right now (a USB disk, a laptop away from its NAS). Opening
    /// used to work: the vault monitor says "Vault not found" and pointer files stay usable. Now the first open after the update
    /// throws "PDF could not be found" (the folder creation for the new categories), and so does every open until the drive is back,
    /// since the "added" mark is only written when the folders could be created.
    /// </summary>
    [Fact]
    public async Task F2_AnOlderLibrary_DoesNotOpen_WhileItsVaultDriveIsMissing()
    {
        if (MissingDriveLetter() is not { } drive)
        {
            return; // Every drive letter is in use on this PC; the scenario can't be set up here.
        }

        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var linked = await t.AddOneAsync(docs.Id, t.CreateFile("linked.pdf", "linked"), ItemMode.Pointer);
        await t.Library.DisposeAsync();
        var vault = $@"{drive}:\Cabinette";
        using (var connection = OpenRaw(t))
        {
            MakeOlder(connection);
            SetVaultPath(connection, vault);
        }

        await t.OpenAsync();

        Assert.Equal(vault, t.Library.VaultPath);
        Assert.NotNull(await t.Library.GetItemAsync(linked.Id));

        // Integrator's change (fix): nothing is added while the vault can't be reached. Categories without folders would be
        // removed by the rescan once the drive is back, and the "added" mark would keep them away for good.
        var all = await t.Library.GetAllCategoriesAsync();
        Assert.All(Six, key => Assert.DoesNotContain(all, c => c.BuiltInKey == key));

        // The drive is back: the next open adds them, with their folders.
        await t.Library.DisposeAsync();
        using (var connection = OpenRaw(t))
        {
            SetVaultPath(connection, t.Vault);
        }

        await t.OpenAsync();
        all = await t.Library.GetAllCategoriesAsync();
        Assert.All(Six, key => Assert.Single(all, c => c.BuiltInKey == key));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "E-books", "EPUB")));
    }

    // ------------------------------------------------------------ 3: a vault folder that isn't there is brought back, empty

    /// <summary>
    /// An older library whose vault folder is missing while its parent is there: renamed in Explorer while Cabinette was closed,
    /// or on a removable drive whose letter now belongs to another disk. Opening used to leave the disk alone, and the startup
    /// rescan then reports "The vault folder can't be found" and changes nothing. Now the open re-creates the vault root with
    /// "Documents\PDF", "E-books\EPUB" and so on. The reconciler's "vault looks empty" guard checks whether any top-level
    /// category folder exists, and "Documents" now does, so the rescan goes ahead: every file is "gone" and is removed from the
    /// library, with its tags and stars, and every category whose folder is missing is deleted, user categories included.
    /// </summary>
    [Fact]
    public async Task F3_AnOlderLibrary_RecreatesAVaultThatIsNotThere_AndTheRescanThenEmptiesTheLibrary()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var images = await t.BuiltInAsync("images");
        var work = await t.Library.CreateCategoryAsync("Work", null);
        var report = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report"));
        var photo = await t.AddOneAsync(images.Id, t.CreateFile("photo.jpg", "photo"));
        var plan = await t.AddOneAsync(work.Id, t.CreateFile("plan.docx", "plan"));
        await t.Library.AddTagsAsync([report.Id], ["taxes"]);
        await t.Library.DisposeAsync();
        using (var connection = OpenRaw(t))
        {
            MakeOlder(connection);
        }

        var away = t.Vault + " (renamed)";
        Directory.Move(t.Vault, away);

        await t.OpenAsync();
        var outcome = await t.Service.ReconcileAsync(ReconcileRequest.Rescan(), CancellationToken.None);

        var all = await t.Library.GetAllCategoriesAsync();
        var reportNow = await t.Library.GetItemAsync(report.Id);
        var photoNow = await t.Library.GetItemAsync(photo.Id);
        var planNow = await t.Library.GetItemAsync(plan.Id);
        Assert.Multiple(
            () => Assert.True(outcome.VaultUnavailable, "the rescan should report the vault as unavailable, but it ran: " + Describe(outcome.Summary)),
            () => Assert.NotNull(reportNow),
            () => Assert.NotNull(photoNow),
            () => Assert.NotNull(planNow),
            () => Assert.Contains(all, c => c.Id == work.Id),
            () => Assert.Contains(all, c => c.Id == images.Id),
            () => Assert.False(Directory.Exists(t.Vault), "opening re-created the missing vault folder"));
        Assert.True(File.Exists(Path.Combine(away, "Documents", "report.pdf")));
    }

    // ------------------------------------------------------------ 4: a folder you made by hand

    /// <summary>
    /// The user keeps PDFs in a "Documents\PDF" folder they made in Explorer while Cabinette was closed (the vault monitor would
    /// have made it their own "PDF" subcategory at the next start, and the update would then have given it the built-in role, as
    /// specified). The update runs first, though: the folder holds a file that isn't a library item, so the built-in doesn't adopt
    /// it and takes the folder "PDF (1)"; the rescan then makes the hand-made folder a user category, which can only be called
    /// "PDF (1)". Two PDF categories under Documents, each with the other's folder name.
    /// </summary>
    [Fact]
    public async Task F4_AHandMadePdfFolderUnderDocuments_TakesTheBuiltInRole_InsteadOfATwin()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.Library.DisposeAsync();
        using (var connection = OpenRaw(t))
        {
            MakeOlder(connection);
        }

        var manual = t.WriteVaultFile(@"Documents\PDF\manual.pdf", "manual");

        await t.OpenAsync();
        await t.RescanAsync();

        var under = (await t.Library.GetAllCategoriesAsync()).Where(c => c.ParentId == docs.Id).ToList();
        var pdf = Assert.Single(under, c => c.Name.StartsWith("PDF", StringComparison.Ordinal));
        Assert.Equal("documents.pdf", pdf.BuiltInKey);
        Assert.Equal("PDF", pdf.FolderName);
        Assert.Equal([manual], (await t.Library.GetThreadItemsAsync(pdf.Id)).Select(i => i.FullPath));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A library (or a backup's database) made before 2026-10-24: none of the six, and no "added" mark.</summary>
    private static void MakeOlder(SqliteConnection connection)
    {
        connection.Execute("DELETE FROM settings WHERE key = @k", new { k = AddedMark });
        connection.Execute("DELETE FROM categories WHERE builtin_key IN (SELECT value FROM json_each(@keys))",
            new { keys = System.Text.Json.JsonSerializer.Serialize(Six) });
    }

    private static void SetVaultPath(SqliteConnection connection, string vault)
        => Assert.Equal(1, connection.Execute("UPDATE settings SET value = json_set(value, '$.VaultPath', @vault) WHERE key = 'app_settings'", new { vault }));

    /// <summary>Changes the database inside a backup zip in place.</summary>
    private static void EditBackupDatabase(string zip, Action<SqliteConnection> edit)
    {
        var temp = zip + ".db";
        try
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
            var entry = archive.GetEntry(Database.FileName)!;
            entry.ExtractToFile(temp, overwrite: true);
            using (var connection = OpenRaw(temp))
            {
                edit(connection);

                // Everything into the main file: the zip gets one file.
                connection.Execute("PRAGMA journal_mode = DELETE");
            }

            entry.Delete();
            archive.CreateEntryFromFile(temp, Database.FileName);
        }
        finally
        {
            foreach (var suffix in (string[])["", "-wal", "-shm"])
            {
                FileOps.TryDelete(temp + suffix);
            }
        }
    }

    private static SqliteConnection OpenRaw(TestLibrary t) => OpenRaw(Path.Combine(t.DataDirectory, Database.FileName));

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>A drive letter no drive on this PC uses (Z downwards), as the vault of a backup from another PC would have.</summary>
    private static char? MissingDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (var letter in "ZYXWVUTSRQPONMLKJIHGFED")
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }

    /// <summary>Every folder and file below <paramref name="root"/>, relative, sorted.</summary>
    private static string[] Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Describe(VaultChangeSummary s)
        => $"removed {s.Removed}, categories removed [{string.Join(", ", s.CategoriesRemoved)}], added {s.AddedByCategory.Values.Sum()}";
}
