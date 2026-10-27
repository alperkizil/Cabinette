using System.IO;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests for the bug hunt of 2026-10-15, group A (Move vault and Restore), for the parts that need App logic:
/// #1 (the open thread reloads after Move vault) and #4 (the old-vault check before it is recycled). Everything runs in a
/// temp folder.
/// </summary>
public sealed class BugHuntMoveRestoreTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cabinette-tests", "bughunt-a-" + Guid.NewGuid().ToString("N"));
    private ILibraryService? _library;

    private string Vault => Path.Combine(_root, "vault");

    // ------------------------------------------------------------------ #1 open thread keeps old vault paths

    [Fact]
    public async Task F1_MoveVaultEvent_ReloadsTheOpenThread()
    {
        var library = await OpenAsync();
        var docs = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var item = await AddCopyAsync(library, docs.Id, "report.docx", "report");

        // The thread of "Documents" is open and shows this item (path in the old vault).
        var shown = (await library.GetThreadItemsAsync(docs.Id)).Single();
        Assert.StartsWith(Vault + Path.DirectorySeparatorChar, shown.FullPath);
        var events = Record(library);

        var newVault = Path.Combine(_root, "new vault");
        await library.MoveVaultAsync(newVault);

        // The open ThreadViewModel reloads its items for the move's event...
        var raised = Snapshot(events);
        Assert.Contains(raised, e => ThreadViewModel.ClassifyChange(e, docs.Id, new HashSet<long> { item.Id }).Items);

        // ...and the reload gets the new vault's path.
        var reloaded = (await library.GetThreadItemsAsync(docs.Id)).Single();
        Assert.Equal(item.Id, reloaded.Id);
        Assert.StartsWith(newVault + Path.DirectorySeparatorChar, reloaded.FullPath);
        Assert.True(File.Exists(reloaded.FullPath));
    }

    [Fact]
    public async Task F1_AfterMoveVault_EveryOpenThreadReloads_EvenOneShowingNothingYet()
    {
        var library = await OpenAsync();
        var docs = await library.GetOrCreateBuiltInCategoryAsync("documents");
        var images = await library.GetOrCreateBuiltInCategoryAsync("images");
        var item = await AddCopyAsync(library, docs.Id, "report.txt", "report");
        var shownIds = (await library.GetThreadItemsAsync(docs.Id)).Select(i => i.Id).ToHashSet();
        var events = Record(library);

        await library.MoveVaultAsync(Path.Combine(_root, "new vault"));

        var raised = Snapshot(events);
        Assert.Contains(raised, e => e.Kind.HasFlag(LibraryChangeKind.Items) && e.CategoryIds.Contains(docs.Id));
        Assert.Contains(raised, e => ThreadViewModel.ClassifyChange(e, docs.Id, shownIds).Items);
        Assert.Contains(raised, e => ThreadViewModel.ClassifyChange(e, images.Id, new HashSet<long>()).Items);

        // Open (VerifyExistsAsync) and the reloaded bubble now agree on the new vault's copy.
        Assert.True(await library.VerifyExistsAsync(item.Id));
        Assert.Equal((await library.GetItemAsync(item.Id))!.FullPath, (await library.GetThreadItemsAsync(docs.Id)).Single().FullPath);
    }

    // ------------------------------------------------------------------ #4 the old vault is checked before it is recycled

    [Fact]
    public void F4_OldVaultCheck_FindsFilesWrittenOrCreatedAfterTheSwitch()
    {
        var oldVault = Path.Combine(_root, "old vault");
        var switched = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var before = switched.AddHours(-1);
        var after = switched.AddMinutes(1);
        Write(oldVault, "untouched.txt", before, before);
        Write(oldVault, "edited.txt", created: before, written: after);
        Write(oldVault, "copied-in.txt", created: after, written: before); // a copy keeps the original's modification time
        Write(oldVault, @"Sub\new.txt", after, after);
        Write(oldVault, @"Sub\.cabinette-0123456789abcdef.tmp", after, after);
        Write(oldVault, @".cabinette-tmp\staged.bin", after, after);

        var changes = VaultOperationsService.FindFilesChangedSince(oldVault, switched, maxListed: 20);

        Assert.Equal(3, changes.Count);
        Assert.Equal(["copied-in.txt", "edited.txt", @"Sub\new.txt"], changes.RelativePaths);

        var capped = VaultOperationsService.FindFilesChangedSince(oldVault, switched, maxListed: 2);
        Assert.Equal(3, capped.Count);
        Assert.Equal(2, capped.RelativePaths.Count);
    }

    [Fact]
    public void F4_OldVaultCheck_NothingChangedOrNoFolder_IsEmpty()
    {
        var oldVault = Path.Combine(_root, "old vault");
        var switched = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Write(oldVault, @"Images\a.jpg", switched.AddDays(-3), switched.AddDays(-3));
        Write(oldVault, @"Images\b.jpg", switched, switched); // written exactly at the switch: part of the move

        Assert.Equal(0, VaultOperationsService.FindFilesChangedSince(oldVault, switched, 20).Count);
        Assert.Equal(0, VaultOperationsService.FindFilesChangedSince(Path.Combine(_root, "gone"), switched, 20).Count);
    }

    [Fact]
    public async Task F4_OldVaultCheck_Warning_ListsTheFiles_AndOffersRecycleAnyway()
    {
        var dialog = VaultOperationsService.BuildOldVaultChangedDialog(@"D:\Old vault",
            new OldVaultChanges(["a.txt", @"Sub\b.txt"], 25));

        Assert.Equal(DialogTone.Warning, dialog.Tone);
        Assert.True(dialog.IsPrimaryDestructive);
        Assert.True(dialog.ShowCancel);
        Assert.Equal(L.Get("Vault_RecycleAnyway"), dialog.PrimaryText);
        Assert.Equal(L.Get("Vault_KeepOldVault"), dialog.CancelText);
        Assert.Equal(["a.txt", @"Sub\b.txt", L.Format("Vault_OldChangedMore", "23")], dialog.Details);
        Assert.Contains(dialog.Facts, f => f.Value == @"D:\Old vault");
        Assert.DoesNotContain('[', dialog.Title + dialog.Message + dialog.DetailsTitle + dialog.PrimaryText + dialog.CancelText);

        // Esc and Cancel keep the old vault.
        dialog.Dismiss();
        Assert.Null(await dialog.Completion);
    }

    [Fact]
    public void F4_RecheckStage_HasItsOwnLabel()
    {
        Assert.Equal(L.Get("Progress_Rechecking"), ProgressDialogViewModel.StageTextFor("rechecking"));
        Assert.DoesNotContain('[', ProgressDialogViewModel.StageTextFor("rechecking"));
    }

    // ------------------------------------------------------------------ helpers

    private static void Write(string root, string relative, DateTime created, DateTime written)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);
    }

    private static List<LibraryChangedEventArgs> Record(ILibraryService library)
    {
        var events = new List<LibraryChangedEventArgs>();
        library.Changed += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        return events;
    }

    private static List<LibraryChangedEventArgs> Snapshot(List<LibraryChangedEventArgs> events)
    {
        lock (events)
        {
            return events.ToList();
        }
    }

    private async Task<ILibraryService> OpenAsync()
    {
        Directory.CreateDirectory(_root);
        _library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.Combine(_root, "data") }, new NoRecycleBin());
        await _library.InitializeVaultAsync(Vault);
        return _library;
    }

    private async Task<LibraryItem> AddCopyAsync(ILibraryService library, long categoryId, string name, string content)
    {
        var source = Path.Combine(_root, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, content);
        var result = await library.AddPathsAsync([source], new AddRequest { TargetCategoryId = categoryId, Mode = ItemMode.Copy });
        Assert.Empty(result.Failures);
        return Assert.Single(result.Added);
    }

    public async ValueTask DisposeAsync()
    {
        if (_library is not null)
        {
            await _library.DisposeAsync(); // releases this library's pooled connections
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    VaultOperationsService.DeleteTree(_root);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(IReadOnlyCollection<string> paths) => throw new IOException("not used");
    }
}
