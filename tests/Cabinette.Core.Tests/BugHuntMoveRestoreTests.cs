using System.IO.Compression;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Cabinette.Core.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

/// <summary>
/// Regression tests for the bug hunt of 2026-10-15, group A (Move vault and Restore): findings #1, #2, #3, #4 and #11.
/// The repro tests in docs/review/bughunt-2026-10-15 asserted the buggy behavior; these keep their scenarios and assert the fix.
/// Everything runs in temp folders.
/// </summary>
public sealed class BugHuntMoveRestoreTests
{
    // ================================================================== #1 open thread keeps old vault paths

    [Fact]
    public async Task F1_MoveVaultEvent_ListsEveryCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var sub = await t.Library.CreateCategoryAsync("Sub", docs.Id);
        await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        t.ClearEvents();

        await t.Library.MoveVaultAsync(Path.Combine(t.Root, "new vault"));

        var moved = Assert.Single(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Settings));
        Assert.True(moved.Kind.HasFlag(LibraryChangeKind.Items));
        Assert.Equal((await t.Library.GetAllCategoriesAsync()).Select(c => c.Id).Order(), moved.CategoryIds.Order());
        Assert.Contains(docs.Id, moved.CategoryIds);
        Assert.Contains(sub.Id, moved.CategoryIds);
    }

    // ================================================================== #2 cleanup deletes foreign files

    [Fact]
    public async Task F2_CancelledMoveVault_KeepsAForeignFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));
        var target = Path.Combine(t.Root, "target");
        Directory.CreateDirectory(target); // the user picked an existing empty folder
        var foreign = Path.Combine(target, "mine.txt");
        using var cts = new CancellationTokenSource();
        t.Service.AfterMoveVaultFileCopied = _ =>
        {
            if (!File.Exists(foreign))
            {
                File.WriteAllText(foreign, "saved here by the user during the move");
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Library.MoveVaultAsync(target, ct: cts.Token));

        // Only the user's file is left: what the move copied and the folders it made are gone.
        Assert.Equal("saved here by the user during the move", File.ReadAllText(foreign));
        Assert.Equal([foreign], Directory.EnumerateFileSystemEntries(target).ToArray());
        Assert.Empty(t.RecycleBin.Recycled);
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }

    [Fact]
    public async Task F2_MoveVaultNameCollision_FailsAndKeepsTheUsersFile()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));
        var target = Path.Combine(t.Root, "target");
        var users = Path.Combine(target, "Documents", "b.txt");
        t.Service.AfterMoveVaultFileCopied = path =>
        {
            if (path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(users, "the user's own b.txt");
            }
        };

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(target));

        Assert.Equal(LibraryErrorCode.NameAlreadyExists, ex.Code);
        Assert.Equal("the user's own b.txt", File.ReadAllText(users));
        Assert.False(File.Exists(Path.Combine(target, "Documents", "a.txt"))); // the move's own copy is gone
        Assert.Equal([users], Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToArray());
        Assert.Equal([Path.Combine(target, "Documents")], Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).ToArray());
        Assert.Equal(t.Vault, t.Library.VaultPath);
        Assert.True(File.Exists(a.FullPath));
    }

    [Fact]
    public async Task F2_CancelledRestore_KeepsAForeignFile()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        await source.AddOneAsync(docs.Id, source.CreateFile("a.txt", new byte[256 * 1024]));
        await source.AddOneAsync(docs.Id, source.CreateFile("b.txt", "b"));
        var zip = Path.Combine(source.Root, "b.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var vault = Path.Combine(target.Root, "restored");
        Directory.CreateDirectory(vault);
        var foreign = Path.Combine(vault, "mine.txt");
        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(p =>
        {
            if (p.Stage == "extracting" && !File.Exists(foreign))
            {
                File.WriteAllText(foreign, "saved here by the user during the restore");
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LibraryFactory.RestoreAsync(zip, vault, target.Options, target.RecycleBin, progress, cts.Token));

        Assert.Equal("saved here by the user during the restore", File.ReadAllText(foreign));
        Assert.Equal([foreign], Directory.EnumerateFileSystemEntries(vault).ToArray());
    }

    [Fact]
    public async Task F2_RestoreNameCollision_FailsAndKeepsTheUsersFile()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        await source.AddOneAsync(docs.Id, source.CreateFile("a.txt", "a"));
        await source.AddOneAsync(docs.Id, source.CreateFile("b.txt", "b"));
        var zip = Path.Combine(source.Root, "b.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var vault = Path.Combine(target.Root, "restored");
        var users = Path.Combine(vault, "Documents", "b.txt");
        var progress = new CallbackProgress(p =>
        {
            if (p.Stage == "extracting" && !File.Exists(users))
            {
                File.WriteAllText(users, "the user's own b.txt");
            }
        });

        var ex = await Assert.ThrowsAsync<LibraryException>(
            () => LibraryFactory.RestoreAsync(zip, vault, target.Options, target.RecycleBin, progress));

        Assert.Equal(LibraryErrorCode.NameAlreadyExists, ex.Code);
        Assert.Contains("b.txt", ex.Message);
        Assert.Equal("the user's own b.txt", File.ReadAllText(users));
        Assert.Equal([users], Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories).ToArray());
        Assert.Equal([Path.Combine(vault, "Documents")], Directory.EnumerateDirectories(vault, "*", SearchOption.AllDirectories).ToArray());
        Assert.Empty(Directory.EnumerateFiles(target.DataDirectory, "*.bak-*"));
    }

    [Fact]
    public async Task F2_FailedMoveVault_RemovesItsReadOnlyCopies_AndTheFolderItCreated()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        File.SetAttributes(a.FullPath, FileAttributes.ReadOnly);
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));
        var target = Path.Combine(t.Root, "new", "vault"); // neither folder exists yet
        t.Service.AfterMoveVaultFileCopied = path =>
        {
            if (path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase))
            {
                File.AppendAllText(path, "corrupted");
            }
        };

        await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(target));

        Assert.False(Directory.Exists(Path.Combine(t.Root, "new")));
        Assert.Equal(t.Vault, t.Library.VaultPath);
        Assert.True(File.GetAttributes(a.FullPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void F2_CreatedPaths_RemovesOnlyWhatItCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), "cabinette-tests", "created-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var created = new CreatedPaths();
            var existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(existing);
            Assert.False(created.CreateDirectory(existing));
            Assert.True(created.CreateDirectory(Path.Combine(root, "new", "deep", "deeper")));
            Assert.True(created.CreateDirectory(Path.Combine(existing, "child")));

            var own = Path.Combine(root, "new", "deep", "own.txt");
            File.WriteAllText(own, "ours");
            File.SetAttributes(own, FileAttributes.ReadOnly | FileAttributes.Hidden);
            created.FileCreated(own);
            var foreignInNew = Path.Combine(root, "new", "foreign.txt");
            File.WriteAllText(foreignInNew, "theirs");
            var foreignInExisting = Path.Combine(existing, "foreign.txt");
            File.WriteAllText(foreignInExisting, "theirs");

            created.Remove();

            Assert.False(File.Exists(own));
            Assert.False(Directory.Exists(Path.Combine(root, "new", "deep")));
            Assert.Equal("theirs", File.ReadAllText(foreignInNew)); // "new" is ours but not empty: it stays
            Assert.Equal("theirs", File.ReadAllText(foreignInExisting));
            Assert.False(Directory.Exists(Path.Combine(existing, "child")));
            Assert.True(Directory.Exists(existing));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================================================================== #3 crafted backup writes outside the vault

    [Fact]
    public async Task F3_CraftedBackupDatabase_IsRefused_AndNothingIsCreatedOutsideTheVault()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var readOnly = await source.AddOneAsync(docs.Id, source.CreateFile("read-only.txt", "r"));
        File.SetAttributes(readOnly.FullPath, FileAttributes.ReadOnly);
        var original = Path.Combine(source.Root, "original.zip");
        await source.Library.CreateBackupAsync(original);

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var restoreRoot = Path.Combine(target.Root, "restore");
        var vault = Path.Combine(restoreRoot, "vault");
        var escaped = Path.Combine(restoreRoot, "escaped"); // what "..\escaped" resolves to
        var absolute = Path.Combine(target.Root, "absolute-target");

        // The attacker edits only the embedded database; every zip entry name is harmless.
        var crafted = CraftBackup(original, Path.Combine(target.Root, "crafted.zip"), db =>
        {
            db.Execute("UPDATE categories SET folder_name = @f WHERE builtin_key = 'executable'", new { f = @"..\escaped" });
            db.Execute("UPDATE categories SET folder_name = @f WHERE builtin_key = 'archives'", new { f = absolute });
        }, ("vault/payload.exe", "MZ not really a program"));

        var ex = await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.RestoreAsync(crafted, vault, target.Options, target.RecycleBin));

        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Equal("This backup's library is damaged or was changed, so it can't be restored.", ex.Message);
        Assert.False(Directory.Exists(escaped));
        Assert.False(Directory.Exists(absolute));
        Assert.False(Directory.Exists(restoreRoot)); // everything restore created (read-only file included) is gone

        // The previous library is back.
        Assert.Empty(Directory.EnumerateFiles(target.DataDirectory, "*.bak-*"));
        await target.OpenAsync();
        Assert.Null(target.Library.VaultPath);
    }

    public static TheoryData<string, string> CraftedDatabases => new()
    {
        { "parent folder", "UPDATE categories SET folder_name = '..' WHERE builtin_key = 'documents'" },
        { "nested path", @"UPDATE categories SET folder_name = 'a\..\..\b' WHERE builtin_key = 'documents'" },
        { "drive path", @"UPDATE categories SET folder_name = 'C:\Windows' WHERE builtin_key = 'documents'" },
        { "trailing period", "UPDATE categories SET folder_name = 'Documents.' WHERE builtin_key = 'documents'" },
        { "empty", "UPDATE categories SET folder_name = '' WHERE builtin_key = 'documents'" },
        { "control character", "UPDATE categories SET folder_name = 'Doc' || char(9) || 'uments' WHERE builtin_key = 'documents'" },
        { "missing parent", "UPDATE categories SET parent_id = 999999 WHERE builtin_key = 'documents'" },
        { "self parent", "UPDATE categories SET parent_id = id WHERE builtin_key = 'documents'" },
        {
            "cycle",
            """
            UPDATE categories SET parent_id = (SELECT id FROM categories WHERE builtin_key = 'images') WHERE builtin_key = 'documents';
            UPDATE categories SET parent_id = (SELECT id FROM categories WHERE builtin_key = 'documents') WHERE builtin_key = 'images';
            """
        },
        { "item file name", @"UPDATE items SET file_name = '..\..\evil.txt' WHERE mode = 0" },
        { "item file path", @"UPDATE items SET file_name = 'C:\evil.txt' WHERE mode = 0" },
    };

    [Theory]
    [MemberData(nameof(CraftedDatabases))]
    public async Task F3_DamagedBackupDatabase_IsRefused(string what, string sql)
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        await source.BuiltInAsync("images");
        await source.AddOneAsync(docs.Id, source.CreateFile("a.txt", "a"));
        var original = Path.Combine(source.Root, "original.zip");
        await source.Library.CreateBackupAsync(original);

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var vault = Path.Combine(target.Root, "restored");
        Directory.CreateDirectory(vault);
        var crafted = CraftBackup(original, Path.Combine(target.Root, "crafted.zip"), db => db.Execute(sql));

        var ex = await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.RestoreAsync(crafted, vault, target.Options, target.RecycleBin));

        Assert.True(ex.Code == LibraryErrorCode.NotAllowed, what);
        Assert.Equal(RestoredLibraryCheck.DamagedMessage, ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(vault)); // the chosen folder stays, empty
        Assert.False(File.Exists(Path.Combine(Path.GetPathRoot(target.Root)!, "evil.txt")));
        Assert.Empty(Directory.EnumerateFiles(target.DataDirectory, "*.bak-*"));
    }

    [Fact]
    public async Task F3_GenuineBackupWithUnusualFolderNames_StillRestores()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var report = await source.AddOneAsync(docs.Id, source.CreateFile("report.pdf", "report"));

        // Folders made in Explorer become categories through the vault monitor, with their real names: longer than the
        // 120 characters a typed category name is cut to, non-ASCII, dots and characters a typed name would keep too.
        var longName = new string('L', 125);
        string[] folders = [longName, "Ünïcödé & #1 (x)", ".config", "a.b.c", @"Nested\Child folder"];
        foreach (var folder in folders)
        {
            source.WriteVaultFile(Path.Combine(folder, "file.txt"), folder);
        }

        var summary = await source.RescanAsync();
        Assert.Contains(longName, summary.CategoriesCreated);
        var zip = Path.Combine(source.Root, "b.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var vault = Path.Combine(target.Root, "restored");
        target.Use(await LibraryFactory.RestoreAsync(zip, vault, target.Options, target.RecycleBin));

        Assert.Equal(
            (await source.Library.GetAllCategoriesAsync()).Select(c => (c.Id, c.ParentId, c.FolderName)),
            (await target.Library.GetAllCategoriesAsync()).Select(c => (c.Id, c.ParentId, c.FolderName)));
        foreach (var folder in folders)
        {
            Assert.Equal(folder, File.ReadAllText(Path.Combine(vault, folder, "file.txt")));
        }

        Assert.Equal("report", File.ReadAllText((await target.ItemAsync(report.Id)).FullPath));
        Assert.True((await target.RescanAsync()).IsEmpty);
    }

    [Theory]
    [InlineData("Documents", true)]
    [InlineData(".git", true)]
    [InlineData(" leading space", true)]
    [InlineData("a.b.c", true)]
    [InlineData("Ünïcödé", true)]
    [InlineData("CONSOLE", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a/b", false)]
    [InlineData("C:", false)]
    [InlineData("a*b", false)]
    [InlineData("a?b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a<b", false)]
    [InlineData("a>b", false)]
    [InlineData("a|b", false)]
    [InlineData("a\u0001b", false)]
    [InlineData("CON", true)] // device names are ordinary files on Windows 11 and stay inside the vault
    [InlineData("nul.txt", true)]
    [InlineData("COM1", true)]
    public void F3_NameCheck_AcceptsOneValidWindowsName(string name, bool valid)
    {
        Assert.Equal(valid, RestoredLibraryCheck.IsValidName(name));
    }

    [Fact]
    public void F3_NameCheck_AllowsUpTo255Characters()
    {
        Assert.True(RestoredLibraryCheck.IsValidName(new string('x', 255)));
        Assert.False(RestoredLibraryCheck.IsValidName(new string('x', 256)));
        Assert.False(RestoredLibraryCheck.IsValidName(null));
    }

    // ================================================================== #4 changes made during Move vault are lost

    [Fact]
    public async Task F4_SourceEditedAndFileAddedDuringMove_AreCopiedAgain()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "version 1"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));
        var oldA = a.FullPath;
        var lateFile = t.VaultPath(@"Documents\late.txt");
        var target = Path.Combine(t.Root, "new vault");
        var progress = new RecordingProgress<VaultOperationProgress>();
        t.Service.AfterMoveVaultFileCopied = path =>
        {
            if (path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase))
            {
                // Another program saves an edit to the old vault after a.txt was copied, and a file appears after the listing.
                File.WriteAllText(oldA, "version 2, saved during the move");
                File.WriteAllText(lateFile, "late");
            }
        };

        var result = await t.Library.MoveVaultAsync(target, progress);

        Assert.Equal(target, result.NewVaultPath);
        Assert.Equal(3, result.FilesCopied);
        var newA = (await t.ItemAsync(a.Id)).FullPath;
        Assert.Equal("version 2, saved during the move", File.ReadAllText(newA));
        Assert.Equal(File.GetLastWriteTimeUtc(oldA), File.GetLastWriteTimeUtc(newA));
        Assert.Equal("late", File.ReadAllText(Path.Combine(target, "Documents", "late.txt")));
        Assert.Contains(progress.Reports, p => p.Stage == "rechecking");
    }

    [Fact]
    public async Task F4_FilesAndFoldersDeletedDuringMove_AreRemovedFromTheCopy()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));
        t.WriteVaultFile(@"Projects\Sub\x.txt", "x");
        var target = Path.Combine(t.Root, "new vault");
        t.Service.AfterMoveVaultFileCopied = path =>
        {
            if (path.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(a.FullPath);                                // already copied
                Directory.Delete(t.VaultPath(@"Projects\Sub"), true);  // its file is not copied yet
            }
        };

        var result = await t.Library.MoveVaultAsync(target);

        Assert.Equal(1, result.FilesCopied);
        Assert.False(File.Exists(Path.Combine(target, "Documents", "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "Documents", "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "Projects", "Sub")));
        Assert.True(Directory.Exists(Path.Combine(target, "Projects")));
    }

    [Fact]
    public async Task F4_FolderAddedDuringMove_IsCreatedWithItsAttributes()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var target = Path.Combine(t.Root, "new vault");
        t.Service.BeforeMoveVaultRecheck = round =>
        {
            if (round == 1)
            {
                Directory.CreateDirectory(t.VaultPath(@"Documents\.cache\empty"));
                File.SetAttributes(t.VaultPath(@"Documents\.cache"), FileAttributes.Directory | FileAttributes.Hidden);
            }
        };

        await t.Library.MoveVaultAsync(target);

        Assert.True(Directory.Exists(Path.Combine(target, "Documents", ".cache", "empty")));
        Assert.True(File.GetAttributes(Path.Combine(target, "Documents", ".cache")).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task F4_VaultChangingInEveryRound_FailsNamingTheFile_AndLeavesNothingBehind()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var busy = t.VaultPath(@"Documents\busy.log");
        var target = Path.Combine(t.Root, "target");
        Directory.CreateDirectory(target);
        var rounds = new List<int>();
        t.Service.BeforeMoveVaultRecheck = round =>
        {
            rounds.Add(round);
            File.WriteAllText(busy, new string('x', round)); // a program that keeps writing its log
        };

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(target));

        Assert.Equal(
            "Files in the vault kept changing during the move (for example \"busy.log\"). The vault was not moved. " +
            "Try again when no program is changing files in it.",
            ex.Message);
        Assert.Equal([1, 2, 3, 4], rounds);
        Assert.Equal(t.Vault, t.Library.VaultPath);
        Assert.Equal(t.Vault, (await t.Library.GetSettingsAsync()).VaultPath);
        Assert.Equal(a.FullPath, (await t.ItemAsync(a.Id)).FullPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task F4_UnchangedVault_NeedsOneRecheck()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var rounds = new List<int>();
        t.Service.BeforeMoveVaultRecheck = rounds.Add;

        await t.Library.MoveVaultAsync(Path.Combine(t.Root, "new vault"));

        Assert.Equal([1], rounds);
    }

    // ================================================================== #11 hidden and system attributes are lost

    [Fact]
    public async Task F11_RestoreKeepsHiddenVaultEntries_AndTheMonitorLeavesThemAlone()
    {
        await using var source = await TestLibrary.CreateAsync();
        await source.BuiltInAsync("code");
        source.WriteVaultFile(@"Code\.git\HEAD", "ref: refs/heads/main");
        source.WriteVaultFile(@"Code\.git\config", "[core]");
        File.SetAttributes(source.VaultPath(@"Code\.git"), FileAttributes.Directory | FileAttributes.Hidden);
        var hiddenFile = source.WriteVaultFile(@"Code\private.txt", "hidden on purpose");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        Assert.True((await source.RescanAsync()).IsEmpty);

        var zip = Path.Combine(source.Root, "backups", "cabinette.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        var restoredVault = Path.Combine(target.Root, "restored vault");
        target.Use(await LibraryFactory.RestoreAsync(zip, restoredVault, target.Options, target.RecycleBin));

        Assert.True(File.GetAttributes(Path.Combine(restoredVault, "Code", ".git")).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(Path.Combine(restoredVault, "Code", "private.txt")).HasFlag(FileAttributes.Hidden));
        Assert.Equal("ref: refs/heads/main", File.ReadAllText(Path.Combine(restoredVault, "Code", ".git", "HEAD")));

        var summary = await target.RescanAsync();
        Assert.True(summary.IsEmpty);
        Assert.DoesNotContain(await target.Library.GetAllCategoriesAsync(), c => c.Name == ".git");
    }

    [Fact]
    public async Task F11_MoveVaultKeepsHiddenFolders_AndTheMonitorLeavesThemAlone()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.BuiltInAsync("code");
        t.WriteVaultFile(@"Code\.git\HEAD", "ref: refs/heads/main");
        File.SetAttributes(t.VaultPath(@"Code\.git"), FileAttributes.Directory | FileAttributes.Hidden);
        var hiddenFile = t.WriteVaultFile(@"Code\private.txt", "hidden on purpose");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        t.WriteVaultFile(@"Code\.system\a.txt", "a");
        File.SetAttributes(t.VaultPath(@"Code\.system"), FileAttributes.Directory | FileAttributes.System);
        Assert.True((await t.RescanAsync()).IsEmpty);

        var target = Path.Combine(t.Root, "new vault");
        await t.Library.MoveVaultAsync(target);

        Assert.True(File.GetAttributes(Path.Combine(target, "Code", ".git")).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(Path.Combine(target, "Code", ".system")).HasFlag(FileAttributes.System));
        Assert.True(File.GetAttributes(Path.Combine(target, "Code", "private.txt")).HasFlag(FileAttributes.Hidden));
        Assert.False(File.GetAttributes(Path.Combine(target, "Code")).HasFlag(FileAttributes.Hidden));

        var summary = await t.RescanAsync();
        Assert.True(summary.IsEmpty);
        Assert.DoesNotContain(await t.Library.GetAllCategoriesAsync(), c => c.Name == ".git");
    }

    [Fact]
    public async Task F11_RestoreKeepsHiddenAlbumArtHidden()
    {
        await using var source = await TestLibrary.CreateAsync();
        var music = await source.BuiltInAsync("audio");
        await source.AddOneAsync(music.Id, source.CreateFile("song.mp3", "audio"));
        var art = source.WriteVaultFile(@"Audio\AlbumArtSmall.jpg", "jpeg");
        File.SetAttributes(art, FileAttributes.Hidden);
        Assert.True((await source.RescanAsync()).IsEmpty);

        var zip = Path.Combine(source.Root, "backups", "cabinette.zip");
        await source.Library.CreateBackupAsync(zip);

        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        var restoredVault = Path.Combine(target.Root, "restored vault");
        target.Use(await LibraryFactory.RestoreAsync(zip, restoredVault, target.Options, target.RecycleBin));

        Assert.True(File.GetAttributes(Path.Combine(restoredVault, "Audio", "AlbumArtSmall.jpg")).HasFlag(FileAttributes.Hidden));
        Assert.True((await target.RescanAsync()).IsEmpty);
        var items = await target.Library.GetThreadItemsAsync((await target.BuiltInAsync("audio")).Id);
        Assert.DoesNotContain(items, i => i.FileName == "AlbumArtSmall.jpg");
        Assert.Contains(items, i => i.FileName == "song.mp3");
    }

    [Fact]
    public async Task F11_BackupStoresDosAttributes_AndRestorePutsReadOnlyBack()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var plain = await source.AddOneAsync(docs.Id, source.CreateFile("plain.txt", "plain"));
        File.SetAttributes(plain.FullPath, FileAttributes.Normal);
        var readOnly = await source.AddOneAsync(docs.Id, source.CreateFile("read-only.txt", "r"));
        File.SetAttributes(readOnly.FullPath, FileAttributes.ReadOnly | FileAttributes.Archive);
        var system = source.WriteVaultFile(@"Documents\system.bin", "s");
        File.SetAttributes(system, FileAttributes.System | FileAttributes.Hidden);
        source.WriteVaultFile(@"Documents\.hidden\x.txt", "x");
        File.SetAttributes(source.VaultPath(@"Documents\.hidden"), FileAttributes.Directory | FileAttributes.Hidden);
        var zip = Path.Combine(source.Root, "b.zip");

        await source.Library.CreateBackupAsync(zip);

        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(0x00, archive.GetEntry("vault/Documents/plain.txt")!.ExternalAttributes & 0xFF);
            Assert.Equal(0x21, archive.GetEntry("vault/Documents/read-only.txt")!.ExternalAttributes & 0xFF);
            Assert.Equal(0x06, archive.GetEntry("vault/Documents/system.bin")!.ExternalAttributes & 0xFF);
            Assert.Equal(0x12, archive.GetEntry("vault/Documents/.hidden/")!.ExternalAttributes & 0xFF);
            Assert.Equal(0x10, archive.GetEntry("vault/Documents/")!.ExternalAttributes & 0xFF);
        }

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var vault = Path.Combine(target.Root, "restored");
        target.Use(await LibraryFactory.RestoreAsync(zip, vault, target.Options, target.RecycleBin));

        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(Path.Combine(vault, "Documents", "read-only.txt")) & ~FileAttributes.Archive);
        Assert.Equal(FileAttributes.System | FileAttributes.Hidden,
            File.GetAttributes(Path.Combine(vault, "Documents", "system.bin")) & ~FileAttributes.Archive);
        Assert.Equal(0, (int)(File.GetAttributes(Path.Combine(vault, "Documents", "plain.txt")) & (FileAttributes.Hidden | FileAttributes.ReadOnly)));
        Assert.True(File.GetAttributes(Path.Combine(vault, "Documents", ".hidden")).HasFlag(FileAttributes.Hidden));
        Assert.True((await target.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task F11_OldBackupWithoutAttributes_RestoresAsBefore()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        await source.AddOneAsync(docs.Id, source.CreateFile("a.txt", "a"));
        var hidden = source.WriteVaultFile(@"Documents\hidden.txt", "h");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var zip = Path.Combine(source.Root, "b.zip");
        await source.Library.CreateBackupAsync(zip);

        // A backup made before attributes were stored: the low byte is 0. The upper 16 bits (Unix modes) are ignored.
        var old = Path.Combine(source.Root, "old.zip");
        RewriteZip(zip, old, entry => entry.ExternalAttributes = unchecked((int)0x81A40000));

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        var vault = Path.Combine(target.Root, "restored");
        target.Use(await LibraryFactory.RestoreAsync(old, vault, target.Options, target.RecycleBin));

        var restored = File.GetAttributes(Path.Combine(vault, "Documents", "hidden.txt"));
        Assert.False(restored.HasFlag(FileAttributes.Hidden));
        Assert.False(restored.HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("a", File.ReadAllText(Path.Combine(vault, "Documents", "a.txt")));
    }

    // ================================================================== helpers

    private sealed class CallbackProgress(Action<VaultOperationProgress> callback) : IProgress<VaultOperationProgress>
    {
        public void Report(VaultOperationProgress value) => callback(value);
    }

    /// <summary>Copies a real backup's manifest and database, lets <paramref name="edit"/> change the database, adds extra entries.</summary>
    private static string CraftBackup(string originalZip, string craftedZip, Action<SqliteConnection> edit, params (string Entry, string Content)[] extra)
    {
        var work = Path.Combine(Path.GetDirectoryName(craftedZip)!, "craft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "cabinette.db");
        using (var zip = ZipFile.OpenRead(originalZip))
        {
            zip.GetEntry("cabinette.db")!.ExtractToFile(db);
        }

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            connection.Open();
            connection.Execute("PRAGMA foreign_keys = OFF");
            edit(connection);
        }

        // Every entry of the original except the database, which is replaced.
        using (var original = ZipFile.OpenRead(originalZip))
        using (var zip = ZipFile.Open(craftedZip, ZipArchiveMode.Create))
        {
            foreach (var entry in original.Entries.Where(e => e.FullName != "cabinette.db"))
            {
                CopyEntry(entry, zip);
            }

            zip.CreateEntryFromFile(db, "cabinette.db");
            foreach (var (entry, content) in extra)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(content);
            }
        }

        return craftedZip;
    }

    private static void RewriteZip(string originalZip, string newZip, Action<ZipArchiveEntry> change)
    {
        using var original = ZipFile.OpenRead(originalZip);
        using var zip = ZipFile.Open(newZip, ZipArchiveMode.Create);
        foreach (var entry in original.Entries)
        {
            CopyEntry(entry, zip, change);
        }
    }

    private static void CopyEntry(ZipArchiveEntry entry, ZipArchive into, Action<ZipArchiveEntry>? change = null)
    {
        var copy = into.CreateEntry(entry.FullName);
        copy.LastWriteTime = entry.LastWriteTime;
        copy.ExternalAttributes = entry.ExternalAttributes;
        change?.Invoke(copy);
        if (!entry.FullName.EndsWith('/'))
        {
            using var input = entry.Open();
            using var output = copy.Open();
            input.CopyTo(output);
        }
    }
}
