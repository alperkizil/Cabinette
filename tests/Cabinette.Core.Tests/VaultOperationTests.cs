using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>Move vault and migrate pointers.</summary>
public sealed class VaultOperationTests
{
    [Fact]
    public async Task MoveVault_CopiesEverything_VerifiesAndSwitches_LeavingTheOldVaultAlone()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var notes = await t.BuiltInAsync("notes");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("report.pdf", "report"));
        var note = await t.Library.CreateNoteAsync(notes.Id, "a note");
        var unknown = t.WriteVaultFile(@"Documents\not-in-library.bin", "unknown");
        var hidden = t.WriteVaultFile(@"Documents\desktop.ini", "[.ShellClassInfo]");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var target = Path.Combine(t.Root, "new vault");
        var progress = new RecordingProgress<VaultOperationProgress>();
        t.ClearEvents();

        var result = await t.Library.MoveVaultAsync(target, progress);

        Assert.Equal(t.Vault, result.OldVaultPath);
        Assert.Equal(target, result.NewVaultPath);
        Assert.Equal(4, result.FilesCopied);
        Assert.Equal(target, t.Library.VaultPath);
        Assert.Equal(target, (await t.Library.GetSettingsAsync()).VaultPath);

        var moved = await t.ItemAsync(item.Id);
        Assert.Equal(Path.Combine(target, "Documents", "report.pdf"), moved.FullPath);
        Assert.Equal("report", File.ReadAllText(moved.FullPath));
        Assert.Equal(File.GetLastWriteTimeUtc(item.FullPath), File.GetLastWriteTimeUtc(moved.FullPath));
        Assert.Equal("a note", File.ReadAllText((await t.ItemAsync(note.Id)).FullPath));
        Assert.Equal("unknown", File.ReadAllText(Path.Combine(target, "Documents", "not-in-library.bin")));
        Assert.True(File.GetAttributes(Path.Combine(target, "Documents", "desktop.ini")).HasFlag(FileAttributes.Hidden));
        Assert.True(Directory.Exists(Path.Combine(target, "Fonts")));
        Assert.True(Directory.Exists(Path.Combine(target, "Code", "Python")));

        // The old vault is kept as it was.
        Assert.True(File.Exists(item.FullPath));
        Assert.True(File.Exists(unknown));

        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Settings));
        Assert.Contains(progress.Reports, p => p.Stage == "copying");
        Assert.Contains(progress.Reports, p => p.Stage == "verifying");
        var lastCopy = progress.Reports.Last(p => p.Stage == "copying");
        Assert.Equal(4, lastCopy.ProcessedFiles);
        Assert.Equal(lastCopy.TotalBytes, lastCopy.BytesProcessed);

        // Remembered after a restart, and the moved vault matches the library exactly.
        await t.ReopenAsync();
        Assert.Equal(target, t.Library.VaultPath);
        var summary = await t.RescanAsync();
        Assert.Equal(1, summary.AddedByCategory["Documents"]); // only the unknown file
    }

    [Fact]
    public async Task MoveVault_RestartsTheMonitorOnTheNewVault()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var monitor = t.FastMonitor();
        await monitor.StartAsync();
        var states = new List<VaultMonitorState>();
        monitor.StatusChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s.State);
            }
        };
        var target = Path.Combine(t.Root, "moved");

        await t.Library.MoveVaultAsync(target);

        lock (states)
        {
            Assert.Contains(VaultMonitorState.Paused, states);
        }

        Assert.NotEqual(VaultMonitorState.Paused, monitor.Status.State);
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        Assert.Equal(VaultMonitorState.Idle, monitor.Status.State);
        File.WriteAllText(Path.Combine(target, "Images", "new.png"), "png");
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(images.Id)).Count == 1, "a file in the new vault");

        // The old vault is no longer watched.
        t.WriteVaultFile(@"Images\old.png", "old");
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        Assert.Single(await t.Library.GetThreadItemsAsync(images.Id));
    }

    [Fact]
    public async Task MoveVault_VerificationFailure_RemovesTheCopy_AndKeepsTheOldVault()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var target = Path.Combine(t.Root, "target");
        t.Service.AfterMoveVaultFileCopied = path => File.AppendAllText(path, "corrupted");

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(target));

        Assert.Contains("verified", ex.Message);
        Assert.False(Directory.Exists(target));
        Assert.Equal(t.Vault, t.Library.VaultPath);
        Assert.Equal(item.FullPath, (await t.ItemAsync(item.Id)).FullPath);
        Assert.True(File.Exists(item.FullPath));

        // An empty folder the user picked stays, but empty.
        Directory.CreateDirectory(target);
        await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(target));
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task MoveVault_Cancelled_RemovesTheCopy()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.AddOneAsync(docs.Id, t.CreateFile("b.txt", "b"));
        var target = Path.Combine(t.Root, "target");
        using var cts = new CancellationTokenSource();
        t.Service.AfterMoveVaultFileCopied = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Library.MoveVaultAsync(target, ct: cts.Token));

        Assert.False(Directory.Exists(target));
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }

    [Fact]
    public async Task MoveVault_RejectsBadTargets()
    {
        await using var t = await TestLibrary.CreateAsync();
        var notEmpty = Path.Combine(t.Root, "not-empty");
        Directory.CreateDirectory(notEmpty);
        File.WriteAllText(Path.Combine(notEmpty, "keep.txt"), "keep");

        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(notEmpty));
        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(notEmpty).Select(p => Path.GetFileName(p)).ToArray());

        Assert.Equal(LibraryErrorCode.NotAllowed,
            (await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(t.VaultPath("Inside")))).Code);
        Assert.Equal(LibraryErrorCode.NotAllowed,
            (await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync(t.Vault))).Code);
        Assert.Equal(LibraryErrorCode.InvalidName,
            (await Assert.ThrowsAsync<LibraryException>(() => t.Library.MoveVaultAsync("relative\\path"))).Code);
        Assert.False(Directory.Exists(t.VaultPath("Inside")));
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }

    [Fact]
    public async Task Migrate_CopiesExistingPointerFiles_AndSkipsMissingOnes()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var notes = await t.BuiltInAsync("notes");
        await t.AddOneAsync(docs.Id, t.CreateFile(@"copy\keep.pdf", "already in the vault"));
        var source = t.CreateFile("keep.pdf", "pointer content");
        var present = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.Library.AddToCategoryAsync([present.Id], notes.Id);
        await t.Library.AddTagsAsync([present.Id], ["important"]);
        var gonePath = t.CreateFile("gone.pdf", "gone");
        var gone = await t.AddOneAsync(docs.Id, gonePath, ItemMode.Pointer);
        File.Delete(gonePath);
        var progress = new RecordingProgress<VaultOperationProgress>();
        t.ClearEvents();

        var result = await t.Library.MigratePointersAsync(progress);

        Assert.Equal(1, result.Migrated);
        Assert.Equal(gone.Id, Assert.Single(result.SkippedMissing).Id);
        Assert.Empty(result.Failures);

        var after = await t.ItemAsync(present.Id);
        Assert.Equal(ItemMode.Copy, after.Mode);
        Assert.Equal("keep (1).pdf", after.FileName);
        Assert.Equal(t.VaultPath(@"Documents\keep (1).pdf"), after.FullPath);
        Assert.Equal(source, after.OriginalPath);
        Assert.Equal(present.Hash, after.Hash);
        Assert.Equal(["important"], after.Tags);
        Assert.Equal([docs.Id, notes.Id], after.CategoryIds.Order().ToArray());
        Assert.Equal("pointer content", File.ReadAllText(after.FullPath));
        Assert.False(File.GetAttributes(after.FullPath).HasFlag(FileAttributes.Hidden));
        Assert.True(File.Exists(source)); // originals stay

        var stillPointer = await t.ItemAsync(gone.Id);
        Assert.Equal(ItemMode.Pointer, stillPointer.Mode);
        Assert.True(stillPointer.IsMissing);

        Assert.Contains(t.Events, e => e.ItemIds.Contains(present.Id));
        Assert.Equal(2, progress.Reports[^1].ProcessedFiles);
        Assert.Empty(Directory.EnumerateFiles(t.Vault, ".cabinette-*", SearchOption.AllDirectories));

        // Nothing for the monitor to pick up afterwards, and nothing left to migrate.
        Assert.True((await t.RescanAsync()).IsEmpty);
        Assert.Equal(0, (await t.Library.MigratePointersAsync()).Migrated);
    }
}
