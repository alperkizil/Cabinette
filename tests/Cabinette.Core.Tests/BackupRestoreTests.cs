using System.IO.Compression;
using System.Text;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class BackupRestoreTests
{
    [Fact]
    public async Task Backup_ThenRestoreIntoAFreshDataFolder_BringsBackEverything()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var notes = await source.BuiltInAsync("notes");
        var projects = await source.Library.CreateCategoryAsync("Projects", null, iconPng: [9, 8, 7]);
        await source.Library.SetCategoryPinnedAsync(projects.Id, true);
        var sub = await source.Library.CreateCategoryAsync("Sub", projects.Id);
        var report = await source.AddOneAsync(docs.Id, source.CreateFile("report.pdf", "report"));
        var photo = await source.AddOneAsync(sub.Id, source.CreateFile("photo.jpg", new byte[] { 0xFF, 0xD8, 1, 2, 3 }));
        await source.Library.AddToCategoryAsync([report.Id], projects.Id);
        await source.Library.AddTagsAsync([report.Id, photo.Id], ["keep", "2026"]);
        await source.Library.CreateNoteAsync(notes.Id, "remember the milk");
        var pointer = await source.AddOneAsync(docs.Id, source.CreateFile("linked.txt", "linked"), ItemMode.Pointer);
        var zip = Path.Combine(source.Root, "backups", "cabinette.zip");
        var progress = new RecordingProgress<VaultOperationProgress>();

        var result = await source.Library.CreateBackupAsync(zip, progress);

        Assert.Equal(zip, result.ZipPath);
        Assert.Equal(3, result.FilesIncluded);
        Assert.Equal(1, result.PointerItemsNotIncluded);
        Assert.True(File.Exists(zip));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(zip)!, ".cabinette-*"));
        Assert.Contains(progress.Reports, p => p.Stage == "zipping" && p.ProcessedFiles == 3);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("cabinette.db"));
            Assert.NotNull(archive.GetEntry("vault/Documents/report.pdf"));
            Assert.NotNull(archive.GetEntry("vault/Fonts/"));
            Assert.True(CompressedSizeIsRaw(archive.GetEntry("vault/Projects/Sub/photo.jpg")!));
        }

        var info = await LibraryFactory.ReadBackupInfoAsync(zip);
        Assert.Equal(1, info.FormatVersion);
        Assert.Equal(3, info.FileCount);
        Assert.Equal(1, info.PointerItems);
        Assert.Equal(source.Time.GetUtcNow(), info.CreatedAt);
        Assert.Equal(Environment.MachineName, info.MachineName);
        Assert.Equal((await source.Library.GetAllCategoriesAsync()).Count, info.CategoryCount);
        Assert.Equal(new FileInfo(report.FullPath).Length + 5 + Encoding.UTF8.GetByteCount("remember the milk"), info.TotalBytes);

        // Restore on "another PC": a different data folder that already has a library of its own.
        await using var target = await TestLibrary.CreateAsync();
        await target.Library.DisposeAsync();
        var restoredVault = Path.Combine(target.Root, "restored vault");
        var restoreProgress = new RecordingProgress<VaultOperationProgress>();

        var restored = await LibraryFactory.RestoreAsync(zip, restoredVault, target.Options, target.RecycleBin, restoreProgress);
        target.Use(restored);

        Assert.Equal(restoredVault, restored.VaultPath);
        Assert.Equal(restoredVault, (await restored.GetSettingsAsync()).VaultPath);
        Assert.Single(Directory.EnumerateFiles(target.DataDirectory, "cabinette.db.bak-20260314-092653"));
        Assert.Contains(restoreProgress.Reports, p => p.Stage == "extracting");

        var sourceCategories = await source.Library.GetAllCategoriesAsync();
        var restoredCategories = await restored.GetAllCategoriesAsync();
        Assert.Equal(
            sourceCategories.Select(c => (c.Id, c.ParentId, c.Name, c.FolderName, c.IsPinned, Icon: Convert.ToHexString(c.IconPng ?? []))),
            restoredCategories.Select(c => (c.Id, c.ParentId, c.Name, c.FolderName, c.IsPinned, Icon: Convert.ToHexString(c.IconPng ?? []))));

        foreach (var original in await AllItemsAsync(source.Library))
        {
            var copy = (await restored.GetItemAsync(original.Id))!;
            Assert.Equal(original.FileName, copy.FileName);
            Assert.Equal(original.Mode, copy.Mode);
            Assert.Equal(original.Tags, copy.Tags);
            Assert.Equal(original.CategoryIds, copy.CategoryIds);
            Assert.Equal(original.Hash, copy.Hash);
            Assert.Equal(original.NotePreview, copy.NotePreview);
            if (original.Mode == ItemMode.Copy)
            {
                Assert.StartsWith(restoredVault + Path.DirectorySeparatorChar, copy.FullPath);
                Assert.Equal(File.ReadAllBytes(original.FullPath), File.ReadAllBytes(copy.FullPath));
                Assert.Equal(File.GetLastWriteTimeUtc(original.FullPath), File.GetLastWriteTimeUtc(copy.FullPath));
            }
            else
            {
                // Pointer items keep their absolute paths.
                Assert.Equal(pointer.FullPath, copy.FullPath);
                Assert.False(copy.IsMissing);
            }
        }

        Assert.True(Directory.Exists(Path.Combine(restoredVault, "Fonts")));
        Assert.Equal(["2026", "keep"], (await restored.GetTagsAsync()).Select(x => x.Name).ToArray());
        Assert.Single(await restored.GetThreadItemsAsync(notes.Id, "milk"));

        // The restored vault matches its database: the startup rescan has nothing to do.
        Assert.True((await target.RescanAsync()).IsEmpty);
    }

    [Fact]
    public async Task Restore_ToAPcWithoutThePointerFiles_MarksThemMissing()
    {
        await using var source = await TestLibrary.CreateAsync();
        var docs = await source.BuiltInAsync("documents");
        var pointerPath = source.CreateFile("linked.txt", "linked");
        var pointer = await source.AddOneAsync(docs.Id, pointerPath, ItemMode.Pointer);
        var zip = Path.Combine(source.Root, "b.zip");
        await source.Library.CreateBackupAsync(zip);
        File.Delete(pointerPath);

        await using var target = await TestLibrary.CreateAsync(initializeVault: false);
        await target.Library.DisposeAsync();
        target.Use(await LibraryFactory.RestoreAsync(zip, Path.Combine(target.Root, "v"), target.Options, target.RecycleBin));

        var restored = await target.ItemAsync(pointer.Id);
        Assert.Equal(pointerPath, restored.FullPath);
        Assert.True(restored.IsMissing);
    }

    [Fact]
    public async Task Backup_InsideTheVault_IsRejected()
    {
        await using var t = await TestLibrary.CreateAsync();
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.CreateBackupAsync(t.VaultPath("backup.zip")));
        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.False(File.Exists(t.VaultPath("backup.zip")));
    }

    [Fact]
    public async Task ReadBackupInfo_RejectsFilesThatAreNotCabinetteBackups()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = Path.Combine(t.Root, "not-a-zip.zip");
        File.WriteAllText(text, "hello");
        Assert.Contains("not a Cabinette backup", (await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.ReadBackupInfoAsync(text))).Message);

        var otherZip = Path.Combine(t.Root, "other.zip");
        using (var archive = ZipFile.Open(otherZip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "readme.txt", "hi");
        }

        Assert.Contains("not a Cabinette backup", (await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.ReadBackupInfoAsync(otherZip))).Message);

        var newer = CreateZip(t, "newer.zip", formatVersion: 2, ("vault/a.txt", "a"));
        Assert.Contains("newer version", (await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.ReadBackupInfoAsync(newer))).Message);

        await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.ReadBackupInfoAsync(Path.Combine(t.Root, "missing.zip")));
    }

    [Fact]
    public async Task Restore_RejectsZipSlipEntries_BeforeWritingAnything()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.Library.DisposeAsync();
        var zip = CreateZip(t, "evil.zip", formatVersion: 1, ("vault/ok.txt", "ok"), ("vault/../../evil.txt", "evil"));
        var vault = Path.Combine(t.Root, "restored");

        var ex = await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.RestoreAsync(zip, vault, t.Options, t.RecycleBin));

        Assert.Contains("outside the vault", ex.Message);
        Assert.False(File.Exists(Path.Combine(t.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(t.Root)!, "evil.txt")));
        Assert.False(Directory.Exists(vault));
        Assert.Empty(Directory.EnumerateFiles(t.DataDirectory, "*.bak-*"));

        // The existing library is untouched.
        await t.OpenAsync();
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
    }

    [Fact]
    public async Task Restore_WithABrokenDatabase_PutsThePreviousLibraryBack()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.Library.DisposeAsync();
        var zip = CreateZip(t, "broken.zip", formatVersion: 1, ("vault/Documents/x.txt", "x"));
        var vault = Path.Combine(t.Root, "restored");

        await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.RestoreAsync(zip, vault, t.Options, t.RecycleBin));

        Assert.False(Directory.Exists(vault));
        Assert.Empty(Directory.EnumerateFiles(t.DataDirectory, "*.bak-*"));
        await t.OpenAsync();
        Assert.Equal(t.Vault, t.Library.VaultPath);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
    }

    [Fact]
    public async Task Restore_IntoAFolderThatIsNotEmpty_IsRejected()
    {
        await using var source = await TestLibrary.CreateAsync();
        var zip = Path.Combine(source.Root, "b.zip");
        await source.Library.CreateBackupAsync(zip);
        await source.Library.DisposeAsync();
        var busy = Path.Combine(source.Root, "busy");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "mine.txt"), "mine");

        var ex = await Assert.ThrowsAsync<LibraryException>(() => LibraryFactory.RestoreAsync(zip, busy, source.Options, source.RecycleBin));

        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Equal(["mine.txt"], Directory.EnumerateFileSystemEntries(busy).Select(p => Path.GetFileName(p)).ToArray());
        Assert.Empty(Directory.EnumerateFiles(source.DataDirectory, "*.bak-*"));
    }

    private static bool CompressedSizeIsRaw(ZipArchiveEntry entry) => entry.CompressedLength == entry.Length;

    private static async Task<IReadOnlyList<LibraryItem>> AllItemsAsync(ILibraryService library)
        => (await library.SearchAsync("", new ItemFilter { MinSizeBytes = 0 })).Items;

    private static string CreateZip(TestLibrary t, string name, int formatVersion, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(t.Root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "manifest.json",
            $$"""{"format":"cabinette-backup","formatVersion":{{formatVersion}},"createdAt":"2026-01-01T00:00:00+00:00","machineName":"PC","fileCount":1,"totalBytes":1,"categoryCount":1,"itemCount":0,"pointerItemCount":0,"appVersion":"1","schemaVersion":1}""");
        WriteEntry(archive, "cabinette.db", "this is not a database");
        foreach (var (entry, content) in entries)
        {
            WriteEntry(archive, entry, content);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }
}
