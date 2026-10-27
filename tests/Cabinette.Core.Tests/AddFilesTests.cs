using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

public sealed class AddFilesTests
{
    [Fact]
    public async Task CopyAdd_CopiesIntoTheCategoryFolder_AndKeepsTheOriginal()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var modified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var source = t.CreateFile("report.pdf", "pdf content", modified);

        var item = await t.AddOneAsync(docs.Id, source, addedBy: AddedBy.Explorer);

        var expected = Path.Combine(t.Vault, "Documents", "report.pdf");
        Assert.Equal(expected, item.FullPath);
        Assert.Equal("pdf content", File.ReadAllText(expected));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(expected));
        Assert.Equal("pdf content", File.ReadAllText(source));
        Assert.Equal(ItemMode.Copy, item.Mode);
        Assert.Equal(ItemKind.File, item.Kind);
        Assert.Equal("pdf", item.Extension);
        Assert.Equal("documents", item.TypeKey);
        Assert.Equal(docs.Id, item.HomeCategoryId);
        Assert.Equal([docs.Id], item.CategoryIds);
        Assert.Equal(11, item.SizeBytes);
        Assert.Equal(16, item.Hash!.Length);
        Assert.Equal(source, item.OriginalPath);
        Assert.Equal(AddedBy.Explorer, item.AddedBy);
        Assert.Equal(t.Time.GetUtcNow(), item.AddedAt);
        Assert.Equal(modified, item.FileModifiedAt!.Value.UtcDateTime);
        Assert.NotNull(item.BatchId);

        // No temp files left behind, and the last upload time was recorded.
        Assert.Single(Directory.GetFiles(Path.Combine(t.Vault, "Documents"), "*", SearchOption.AllDirectories));
        Assert.Equal(t.Time.GetUtcNow(), (await t.CategoryAsync(docs.Id)).LastUploadAt);
    }

    [Fact]
    public async Task OneCall_SharesABatchId_AndReportsProgress()
    {
        await using var t = await TestLibrary.CreateAsync();
        var images = await t.BuiltInAsync("images");
        var a = t.CreateFile("a.png", "image a");
        var b = t.CreateFile("b.png", "image bb");
        var reports = new List<AddProgress>();
        var progress = new SynchronousProgress<AddProgress>(reports.Add);

        var result = await t.Library.AddPathsAsync([a, b], new AddRequest { TargetCategoryId = images.Id, Mode = ItemMode.Copy }, progress);

        Assert.Equal(2, result.Added.Count);
        Assert.Single(result.Added.Select(i => i.BatchId).Distinct());
        var last = reports[^1];
        Assert.Equal(2, last.ProcessedFiles);
        Assert.Equal(2, last.TotalFiles);
        Assert.Equal(15, last.TotalBytes);
        Assert.Equal(15, last.BytesProcessed);

        var other = await t.AddOneAsync(images.Id, t.CreateFile("c.png", "image c"));
        Assert.NotEqual(result.Added[0].BatchId, other.BatchId);
    }

    [Fact]
    public async Task NameClash_WithDifferentContent_GetsNumberedNames()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var first = await t.AddOneAsync(text.Id, t.CreateFile("one/notes.txt", "first"));
        var second = await t.AddOneAsync(text.Id, t.CreateFile("two/notes.txt", "second"));
        var third = await t.AddOneAsync(text.Id, t.CreateFile("three/NOTES.txt", "third"));

        Assert.Equal("notes.txt", first.FileName);
        Assert.Equal("notes (1).txt", second.FileName);
        Assert.Equal("NOTES (2).txt", third.FileName);
        Assert.Equal("second", File.ReadAllText(Path.Combine(t.Vault, "Text", "notes (1).txt")));
        Assert.Equal("third", File.ReadAllText(third.FullPath));
    }

    [Fact]
    public async Task Duplicate_InSameCategory_IsNotStoredTwice()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var original = await t.AddOneAsync(text.Id, t.CreateFile("a.txt", "same content"));

        var result = await t.AddAsync(text.Id, ItemMode.Copy, t.CreateFile("copy of a.txt", "same content"));

        Assert.Empty(result.Added);
        var dup = Assert.Single(result.Duplicates);
        Assert.Equal(original.Id, dup.Existing.Id);
        Assert.False(dup.AddedToTargetCategory);
        Assert.Single(Directory.GetFiles(Path.Combine(t.Vault, "Text")));
        Assert.Single(await t.Library.GetThreadItemsAsync(text.Id));
    }

    [Fact]
    public async Task Duplicate_InAnotherCategory_AddsAMembershipOnly()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var docs = await t.BuiltInAsync("documents");
        var original = await t.AddOneAsync(text.Id, t.CreateFile("a.txt", "shared"));
        t.Time.Advance(TimeSpan.FromMinutes(1));

        var result = await t.AddAsync(docs.Id, ItemMode.Copy, t.CreateFile("b.txt", "shared"));

        var dup = Assert.Single(result.Duplicates);
        Assert.True(dup.AddedToTargetCategory);
        Assert.Equal(original.Id, dup.Existing.Id);
        Assert.Equal(new[] { text.Id, docs.Id }.Order(), dup.Existing.CategoryIds.Order());
        Assert.Equal(text.Id, dup.Existing.HomeCategoryId);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Vault, "Documents")));
        Assert.Single(await t.Library.GetThreadItemsAsync(docs.Id));
        Assert.Equal(t.Time.GetUtcNow(), (await t.CategoryAsync(docs.Id)).LastUploadAt);
    }

    [Fact]
    public async Task Duplicate_AcrossModes_IsDetected()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        await t.AddOneAsync(text.Id, t.CreateFile("a.txt", "same in both modes"));
        var result = await t.AddAsync(text.Id, ItemMode.Pointer, t.CreateFile("pointer.txt", "same in both modes"));
        Assert.Empty(result.Added);
        Assert.Single(result.Duplicates);
    }

    [Fact]
    public async Task EmptyFiles_AreNeverDuplicates()
    {
        await using var t = await TestLibrary.CreateAsync();
        var code = await t.BuiltInAsync("code.python");
        var result = await t.AddAsync(code.Id, ItemMode.Copy, t.CreateFile("a/__init__.py", ""), t.CreateFile("b/__init__.py", ""));
        Assert.Equal(2, result.Added.Count);
        Assert.Empty(result.Duplicates);
    }

    [Fact]
    public async Task AutoCategorize_UsesTheExtensionMap_IncludingCodeSubcategories()
    {
        await using var t = await TestLibrary.CreateAsync();
        var result = await t.AddAsync(null, ItemMode.Copy,
            t.CreateFile("script.py", "print(1)"),
            t.CreateFile("main.cpp", "int main(){}"),
            t.CreateFile("song.mp3", "mp3"),
            t.CreateFile("data.json", "{}"),
            t.CreateFile("weird.xyz", "???"));

        Assert.Empty(result.Failures);
        string KeyOf(string name) => result.Added.Single(i => i.FileName == name).FullPath;
        Assert.Equal(Path.Combine(t.Vault, "Code", "Python", "script.py"), KeyOf("script.py"));
        Assert.Equal(Path.Combine(t.Vault, "Code", "C-C++", "main.cpp"), KeyOf("main.cpp"));
        Assert.Equal(Path.Combine(t.Vault, "Audio", "song.mp3"), KeyOf("song.mp3"));
        Assert.Equal(Path.Combine(t.Vault, "Code", "data.json"), KeyOf("data.json"));
        Assert.Equal(Path.Combine(t.Vault, "Other", "weird.xyz"), KeyOf("weird.xyz"));
    }

    [Fact]
    public async Task AutoCategorize_RecreatesADeletedBuiltIn()
    {
        await using var t = await TestLibrary.CreateAsync();
        var python = await t.BuiltInAsync("code.python");
        var code = await t.BuiltInAsync("code");
        await t.Library.DeleteCategoryAsync(code.Id, CategoryDeletePolicy.MoveToDeleted);
        Assert.Null(await t.Library.GetCategoryAsync(python.Id));

        var result = await t.AddAsync(null, ItemMode.Copy, t.CreateFile("x.py", "x = 1"));

        var item = Assert.Single(result.Added);
        var recreated = await t.CategoryAsync(item.HomeCategoryId);
        Assert.Equal("code.python", recreated.BuiltInKey);
        Assert.Equal("Python", recreated.Name);
        Assert.Equal(CategoryKind.BuiltIn, recreated.Kind);
        var parent = await t.CategoryAsync(recreated.ParentId!.Value);
        Assert.Equal("code", parent.BuiltInKey);
        Assert.Contains(recreated.Id, result.CreatedCategoryIds);
        Assert.Equal(Path.Combine(t.Vault, "Code", "Python", "x.py"), item.FullPath);
        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task TargetCategory_WinsOverAutoCategorization()
    {
        await using var t = await TestLibrary.CreateAsync();
        var java = await t.BuiltInAsync("code.java");
        var item = await t.AddOneAsync(java.Id, t.CreateFile("x.py", "print('in java')"));
        Assert.Equal(java.Id, item.HomeCategoryId);
        Assert.Equal("code", item.TypeKey);
    }

    [Fact]
    public async Task LastUpload_IsUpdatedOnTargetAndAncestors()
    {
        await using var t = await TestLibrary.CreateAsync();
        var python = await t.BuiltInAsync("code.python");
        var code = await t.BuiltInAsync("code");
        t.Time.Advance(TimeSpan.FromHours(1));
        await t.AddOneAsync(python.Id, t.CreateFile("a.py", "a"));
        Assert.Equal(t.Time.GetUtcNow(), (await t.CategoryAsync(python.Id)).LastUploadAt);
        Assert.Equal(t.Time.GetUtcNow(), (await t.CategoryAsync(code.Id)).LastUploadAt);
        Assert.Null((await t.CategoryAsync((await t.BuiltInAsync("audio")).Id)).LastUploadAt);
    }

    [Fact]
    public async Task MissingSourceFiles_AreReportedAsFailures_OthersStillAdded()
    {
        await using var t = await TestLibrary.CreateAsync();
        var text = await t.BuiltInAsync("text");
        var good = t.CreateFile("good.txt", "good");
        var result = await t.AddAsync(text.Id, ItemMode.Copy, Path.Combine(t.Source, "missing.txt"), good);
        Assert.Single(result.Added);
        var failure = Assert.Single(result.Failures);
        Assert.EndsWith("missing.txt", failure.SourcePath);
    }

    [Fact]
    public async Task Cancellation_StopsAndLeavesNoTempFiles()
    {
        await using var t = await TestLibrary.CreateAsync();
        var other = await t.BuiltInAsync("other");
        var small = t.CreateFile("small.bin", "small");
        var big = t.CreateFile("big.bin", new byte[4 * 1024 * 1024]);
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<AddProgress>(p =>
        {
            if (p.CurrentFile == big)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            t.Library.AddPathsAsync([small, big], new AddRequest { TargetCategoryId = other.Id, Mode = ItemMode.Copy }, progress, cts.Token));

        // The file finished before the cancellation stays; nothing else (no temp file) is left in the folder.
        var remaining = Directory.GetFiles(Path.Combine(t.Vault, "Other"), "*", SearchOption.AllDirectories);
        Assert.Equal("small.bin", Path.GetFileName(Assert.Single(remaining)));
        Assert.Equal("small.bin", Assert.Single(await t.Library.GetThreadItemsAsync(other.Id)).FileName);
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Items));
    }

    [Fact]
    public async Task PointerAdd_ReferencesTheFileInPlace()
    {
        await using var t = await TestLibrary.CreateAsync();
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("in place.pdf", "pointer");

        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);

        Assert.Equal(ItemMode.Pointer, item.Mode);
        Assert.Equal(source, item.FullPath);
        Assert.NotNull(item.Hash);
        Assert.Null(item.OriginalPath);
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Vault, "Documents")));

        // The same path again is a duplicate, not a second item.
        var again = await t.AddAsync((await t.BuiltInAsync("text")).Id, ItemMode.Pointer, source);
        Assert.Empty(again.Added);
        Assert.True(Assert.Single(again.Duplicates).AddedToTargetCategory);
    }

    [Fact]
    public async Task LargePointerFiles_AreHashedInTheBackground_AndDuplicatesMerged()
    {
        await using var t = await TestLibrary.CreateAsync();
        t.Service.BackgroundHashThresholdBytes = 10;
        var docs = await t.BuiltInAsync("documents");
        var text = await t.BuiltInAsync("text");
        var first = await t.AddOneAsync(docs.Id, t.CreateFile("big1.dat", "large identical content"), ItemMode.Pointer);
        await t.Service.WhenBackgroundHashingIdleAsync();
        Assert.NotNull((await t.ItemAsync(first.Id)).Hash);

        t.ClearEvents();
        var second = await t.AddOneAsync(text.Id, t.CreateFile("big2.dat", "large identical content"), ItemMode.Pointer, AddedBy.Explorer);
        Assert.Null(second.Hash);
        await t.Service.WhenBackgroundHashingIdleAsync();

        Assert.Null(await t.Library.GetItemAsync(second.Id));
        var merged = await t.ItemAsync(first.Id);
        Assert.Equal(new[] { docs.Id, text.Id }.Order(), merged.CategoryIds.Order());
        var evt = Assert.Single(t.Events, e => e.MergedDuplicates.Count > 0);
        var info = Assert.Single(evt.MergedDuplicates);
        Assert.Equal(first.Id, info.Existing.Id);
        Assert.True(info.AddedToTargetCategory);
        Assert.Contains(second.Id, evt.ItemIds);
        Assert.Contains(text.Id, evt.CategoryIds);

        // The membership keeps who added it, so the unread badge still shows.
        Assert.Equal(1, (await t.SummaryAsync(text.Id)).UnreadCount);
    }

    [Fact]
    public async Task UnhashedPointerItems_AreHashedAfterReopen()
    {
        await using var t = await TestLibrary.CreateAsync();
        t.Service.BackgroundHashThresholdBytes = 10;
        var docs = await t.BuiltInAsync("documents");
        var source = t.CreateFile("big.dat", "a large file that is hashed later");

        // Dispose right away: the queue stops cleanly and the item stays unhashed until the next start.
        var item = await t.AddOneAsync(docs.Id, source, ItemMode.Pointer);
        await t.ReopenAsync();
        await t.Service.WhenBackgroundHashingIdleAsync();
        Assert.NotNull((await t.ItemAsync(item.Id)).Hash);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
