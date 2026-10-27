using Cabinette.App.Services;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Tests;

/// <summary>"No Recycle Bin on this drive. Delete permanently?" (finding 5).</summary>
public sealed class RecycleBinFallbackTests
{
    [Fact]
    public async Task Normal_delete_asks_nothing()
    {
        var dialogs = new FakeDialogs(confirm: true);
        var calls = new List<DeleteItemsOptions?>();

        Assert.True(await RecycleBinFallback.RunAsync(dialogs, o => Record(calls, o)));

        Assert.Single(calls);
        Assert.Null(calls[0]);
        Assert.Empty(dialogs.Confirms);
    }

    [Fact]
    public async Task No_recycle_bin_asks_and_retries_permanently_only_after_yes()
    {
        var dialogs = new FakeDialogs(confirm: true);
        var calls = new List<DeleteItemsOptions?>();
        var options = new DeleteItemsOptions { DeleteRealPointerFiles = false };

        Assert.True(await RecycleBinFallback.RunAsync(dialogs, o => Record(calls, o, failFirst: true), options));

        Assert.Equal(2, calls.Count);
        Assert.Same(options, calls[0]);
        Assert.Equal(options with { PermanentWhenNoRecycleBin = true }, calls[1]);
        var (title, message, confirmText, destructive) = Assert.Single(dialogs.Confirms);
        Assert.Equal("Can't use the Recycle Bin", title);
        Assert.Equal("\"X:\\a.txt\" is on a drive without a Recycle Bin.\n\nDelete permanently? This can't be undone.", message);
        Assert.Equal("Delete permanently", confirmText);
        Assert.True(destructive);
    }

    [Fact]
    public async Task No_recycle_bin_and_no_means_nothing_happens()
    {
        var dialogs = new FakeDialogs(confirm: false);
        var calls = new List<DeleteItemsOptions?>();

        Assert.False(await RecycleBinFallback.RunAsync(dialogs, o => Record(calls, o, failFirst: true)));

        Assert.Single(calls);
        Assert.Single(dialogs.Confirms);
    }

    [Fact]
    public async Task Other_library_errors_are_not_turned_into_permanent_deletes()
    {
        var dialogs = new FakeDialogs(confirm: true);

        var ex = await Assert.ThrowsAsync<LibraryException>(() => RecycleBinFallback.RunAsync(
            dialogs, _ => throw new LibraryException(LibraryErrorCode.FileInUse, "in use")));

        Assert.Equal(LibraryErrorCode.FileInUse, ex.Code);
        Assert.Empty(dialogs.Confirms);
    }

    private static Task Record(List<DeleteItemsOptions?> calls, DeleteItemsOptions? options, bool failFirst = false)
    {
        calls.Add(options);
        if (failFirst && calls.Count == 1)
        {
            throw new LibraryException(LibraryErrorCode.RecycleBinUnavailable, "\"X:\\a.txt\" is on a drive without a Recycle Bin.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers every confirmation with a fixed choice and records it.</summary>
internal sealed class FakeDialogs(bool confirm) : IDialogService
{
    public List<(string Title, string Message, string ConfirmText, bool IsDestructive)> Confirms { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        Confirms.Add((title, message, confirmText, isDestructive));
        return Task.FromResult(confirm);
    }

    public Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
        => Task.FromResult<string?>(null);

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options) => Task.FromResult<int?>(null);

    public Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true) => Task.FromResult<long?>(null);

    public Task<string?> PickFileToLocateAsync(string originalFileName) => Task.FromResult<string?>(null);
}
