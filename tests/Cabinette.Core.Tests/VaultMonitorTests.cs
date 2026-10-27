using System.Collections.Concurrent;
using System.Reflection;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>The monitor with a real FileSystemWatcher (short test timings, polling waits).</summary>
public sealed class VaultMonitorTests
{
    [Fact]
    public async Task Start_RescansFirst_ThenPicksUpNewFiles_AndReportsThem()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        t.WriteVaultFile(@"Images\before-start.png", "before");
        var monitor = t.FastMonitor();
        var summaries = new ConcurrentQueue<VaultChangeSummary>();
        var states = new ConcurrentQueue<VaultMonitorState>();
        monitor.ChangesApplied += (_, s) => summaries.Enqueue(s);
        monitor.StatusChanged += (_, s) => states.Enqueue(s.State);

        await monitor.StartAsync();

        // StartAsync returns once the startup rescan is done.
        Assert.Single(await t.Library.GetThreadItemsAsync(images.Id));
        Assert.NotNull(monitor.Status.LastSyncAt);
        Assert.Contains(VaultMonitorState.Syncing, states);
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        Assert.Equal(VaultMonitorState.Idle, monitor.Status.State);

        t.WriteVaultFile(@"Images\after-start.png", "after");
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(images.Id)).Count == 2, "the new file to be added");
        await VaultTestHelpers.WaitUntilAsync(() => summaries.Sum(s => s.AddedByCategory.GetValueOrDefault("Images")) == 2, "both additions reported");
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        Assert.Equal(VaultMonitorState.Idle, monitor.Status.State);
        Assert.Equal(0, monitor.Status.PendingChanges);
        Assert.Same(monitor, t.Library.CreateVaultMonitor());
    }

    [Fact]
    public async Task Watcher_RenameMoveAndFolderRename_KeepIdentities()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var projects = await t.Library.CreateCategoryAsync("Projects", null);
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("draft.txt", "draft"));
        var inProject = await t.AddOneAsync(projects.Id, t.CreateFile("p.txt", "p"));
        await t.Library.AddTagsAsync([item.Id], ["tag"]);
        var monitor = t.FastMonitor();
        await monitor.StartAsync();

        File.Move(item.FullPath, t.VaultPath(@"Documents\final.txt"));
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.ItemAsync(item.Id)).FileName == "final.txt", "the rename");

        File.Move(t.VaultPath(@"Documents\final.txt"), t.VaultPath(@"Text\final.txt"));
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.ItemAsync(item.Id)).HomeCategoryId == text.Id, "the move");
        Assert.Equal(["tag"], (await t.ItemAsync(item.Id)).Tags);

        Directory.Move(t.VaultPath("Projects"), t.VaultPath("Work"));
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.CategoryAsync(projects.Id)).Name == "Work", "the folder rename");
        Assert.Equal(t.VaultPath(@"Work\p.txt"), (await t.ItemAsync(inProject.Id)).FullPath);
        Assert.Single(await t.Library.GetThreadItemsAsync(projects.Id));
    }

    [Fact]
    public async Task Watcher_DeletedFileAndFolder_AreRemoved()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var old = await t.Library.CreateCategoryAsync("Old", null);
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        await t.AddOneAsync(old.Id, t.CreateFile("b.txt", "b"));
        var monitor = t.FastMonitor();
        await monitor.StartAsync();

        File.Delete(item.FullPath);
        Directory.Delete(t.VaultPath("Old"), recursive: true);

        await VaultTestHelpers.WaitUntilAsync(async () => await t.Library.GetItemAsync(item.Id) is null, "the file to be removed");
        await VaultTestHelpers.WaitUntilAsync(async () => await t.Library.GetCategoryAsync(old.Id) is null, "the category to be removed");
        Assert.Empty(await t.AllItemsAsync());
    }

    [Fact]
    public async Task CabinettesOwnOperations_AreNotReportedByTheMonitor()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var notes = await t.BuiltInAsync("notes");
        var monitor = t.FastMonitor();
        var summaries = new ConcurrentQueue<VaultChangeSummary>();
        monitor.ChangesApplied += (_, s) => summaries.Enqueue(s);
        await monitor.StartAsync();

        var a = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var b = await t.AddOneAsync(null, t.CreateFile("b.png", "b"));
        await t.Library.RenameItemAsync(a.Id, "renamed.txt");
        await t.Library.CreateNoteAsync(notes.Id, "note");
        var mine = await t.Library.CreateCategoryAsync("Mine", null);
        await t.Library.RenameCategoryAsync(mine.Id, "Ours");
        await t.Library.AddToCategoryAsync([b.Id], mine.Id);
        await t.Library.RemoveFromCategoryAsync([b.Id], (await t.BuiltInAsync("images")).Id);
        await t.Library.DeleteItemsAsync([a.Id]);
        await t.Library.DeleteCategoryAsync(mine.Id, CategoryDeletePolicy.MoveToDeleted);

        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15));
        Assert.Empty(summaries);
        Assert.DoesNotContain(t.Events, e => e.Source == AddedBy.VaultMonitor);
        Assert.Equal(VaultMonitorState.Idle, monitor.Status.State);
    }

    [Fact]
    public async Task TempFiles_AreIgnoredByTheWatcher()
    {
        await using var t = await TestLibrary.CreateAsync();
        var monitor = t.FastMonitor();
        var summaries = new ConcurrentQueue<VaultChangeSummary>();
        monitor.ChangesApplied += (_, s) => summaries.Enqueue(s);
        await monitor.StartAsync();

        t.WriteVaultFile(@"Documents\~$report.docx", "lock");
        t.WriteVaultFile(@"Documents\big.iso.part", "partial");
        t.WriteVaultFile("setup.crdownload", "partial");

        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
        Assert.Empty(summaries);
        Assert.Empty(await t.AllItemsAsync());
        Assert.True(File.Exists(t.VaultPath("setup.crdownload")));
    }

    [Fact]
    public async Task PauseDropsChanges_ResumeRescans()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var monitor = t.FastMonitor();
        await monitor.StartAsync();

        await monitor.PauseAsync();
        Assert.Equal(VaultMonitorState.Paused, monitor.Status.State);
        t.WriteVaultFile(@"Images\while-paused.png", "p");
        await Task.Delay(400);
        Assert.Empty(await t.Library.GetThreadItemsAsync(images.Id));

        // A rescan request while paused applies nothing either.
        await monitor.RescanAsync();
        Assert.Empty(await t.Library.GetThreadItemsAsync(images.Id));

        await monitor.ResumeAsync();
        Assert.Single(await t.Library.GetThreadItemsAsync(images.Id));
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        Assert.Equal(VaultMonitorState.Idle, monitor.Status.State);

        t.WriteVaultFile(@"Images\after-resume.png", "r");
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(images.Id)).Count == 2, "watching again after resume");
    }

    [Fact]
    public async Task MissingVault_IsUnavailable_RemovesNothing_AndRecovers()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var away = t.Vault + "-away";
        Directory.Move(t.Vault, away);
        var monitor = t.FastMonitor();
        var states = new ConcurrentQueue<VaultMonitorStatus>();
        monitor.StatusChanged += (_, s) => states.Enqueue(s);

        await monitor.StartAsync();

        Assert.Equal(VaultMonitorState.VaultUnavailable, monitor.Status.State);
        Assert.Contains(t.Vault, monitor.Status.Message);
        Assert.Contains(states, s => s.State == VaultMonitorState.VaultUnavailable);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));

        // Still missing after a few retries: still nothing removed.
        await Task.Delay(500);
        Assert.Equal(VaultMonitorState.VaultUnavailable, monitor.Status.State);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));

        Directory.Move(away, t.Vault);
        await VaultTestHelpers.WaitUntilAsync(() => monitor.Status.State == VaultMonitorState.Idle, "the monitor to recover");
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
        Assert.Null(monitor.Status.Message);

        t.WriteVaultFile(@"Documents\new.txt", "new");
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(docs.Id)).Count == 2, "watching after recovery");
    }

    [Fact]
    public async Task VaultDisappearingWhileWatching_IsDetectedByARescan()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var item = await t.AddOneAsync(docs.Id, t.CreateFile("a.txt", "a"));
        var monitor = t.FastMonitor();
        monitor.UnavailableRetryInterval = TimeSpan.FromSeconds(30);
        await monitor.StartAsync();
        await monitor.PauseAsync();
        Directory.Move(t.Vault, t.Vault + "-away");
        await monitor.ResumeAsync();

        Assert.Equal(VaultMonitorState.VaultUnavailable, monitor.Status.State);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));

        Directory.Move(t.Vault + "-away", t.Vault);
        await monitor.RescanAsync();
        Assert.NotEqual(VaultMonitorState.VaultUnavailable, monitor.Status.State);
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        Assert.Equal(VaultMonitorState.Idle, monitor.Status.State);
        Assert.NotNull(await t.Library.GetItemAsync(item.Id));
    }

    [Fact]
    public async Task WatcherError_TriggersAFullRescan_WithAFreshWatcher()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var monitor = t.FastMonitor();
        await monitor.StartAsync();

        // A change the watcher can't report: a hidden file (ignored) that later becomes visible (attributes aren't watched).
        var path = t.WriteVaultFile(@"Documents\was-hidden.txt", "x");
        File.SetAttributes(path, FileAttributes.Hidden);
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        File.SetAttributes(path, FileAttributes.Normal);
        await Task.Delay(300);
        Assert.Empty(await t.Library.GetThreadItemsAsync(docs.Id));

        var watcherField = typeof(Vault.VaultMonitor).GetField("_watcher", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var before = (FileSystemWatcher)watcherField.GetValue(monitor)!;
        typeof(FileSystemWatcher).GetMethod("OnError", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(before, [new ErrorEventArgs(new InternalBufferOverflowException())]);

        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(docs.Id)).Count == 1, "the full rescan");
        await monitor.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        Assert.NotSame(before, watcherField.GetValue(monitor));
        t.WriteVaultFile(@"Documents\after.txt", "y");
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(docs.Id)).Count == 2, "the new watcher to work");
    }

    [Fact]
    public async Task StartedBeforeFirstRunSetup_WaitsForTheVault()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var monitor = t.FastMonitor();

        await monitor.StartAsync();
        Assert.Equal(VaultMonitorState.VaultUnavailable, monitor.Status.State);
        Assert.NotNull(monitor.Status.Message);

        await t.Library.InitializeVaultAsync(t.Vault);
        await VaultTestHelpers.WaitUntilAsync(() => monitor.Status.State == VaultMonitorState.Idle, "the monitor to find the new vault");
        t.WriteVaultFile(@"Images\first.png", "png");
        var images = await t.BuiltInAsync("images");
        await VaultTestHelpers.WaitUntilAsync(async () => (await t.Library.GetThreadItemsAsync(images.Id)).Count == 1, "watching the new vault");
    }

    [Fact]
    public async Task DisposingTheLibrary_StopsTheMonitor()
    {
        await using var t = await TestLibrary.CreateAsync();
        var monitor = t.FastMonitor();
        await monitor.StartAsync();

        await t.Library.DisposeAsync();

        Assert.Equal(VaultMonitorState.Stopped, monitor.Status.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync());
        t.WriteVaultFile(@"Images\after-dispose.png", "x");
        await Task.Delay(300);
    }
}
