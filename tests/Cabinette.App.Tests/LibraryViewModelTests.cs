using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Cabinette.App.DesignTime;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Shell;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>View models against the in-memory sample library, counting which library calls they make.</summary>
public sealed class LibraryViewModelTests
{
    // ------------------------------------------------------------------ finding 6

    [Fact]
    public async Task Viewed_refreshes_only_the_affected_badges()
    {
        var (library, calls) = CountingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var chat = new ChatListViewModel(library, new CategoryIconService(library, new FakeIconGenerator()));
        await chat.RefreshAsync();
        var documents = chat.Rows.Single(r => r.Category.BuiltInKey == "documents");
        var images = chat.Rows.Single(r => r.Category.BuiltInKey == ExtensionMap.ImagesKey);
        Assert.True(documents.UnreadCount > 0);

        await library.MarkCategoryViewedAsync(documents.CategoryId);
        calls.Clear();
        await chat.RefreshUnreadAsync(new HashSet<long> { documents.CategoryId });

        Assert.Equal(0, documents.UnreadCount);
        Assert.False(documents.HasUnread);
        Assert.Equal(1, calls.Count(nameof(ILibraryService.GetCategorySummaryAsync)));
        Assert.Equal(0, calls.Count(nameof(ILibraryService.GetCategoriesAsync)));
        Assert.Same(images, chat.Rows.Single(r => r.Category.BuiltInKey == ExtensionMap.ImagesKey));
    }

    [Fact]
    public async Task Icon_pass_after_a_change_only_looks_at_new_categories()
    {
        var (library, calls) = CountingLibrary.Wrap(SampleLibraryService.Create(withVault: true));
        var icons = new CategoryIconService(library, new FakeIconGenerator());

        await icons.EnsureDefaultIconsAsync(); // startup: every category once
        Assert.All(await library.GetAllCategoriesAsync(), c => Assert.NotNull(c.IconPng));
        var documents = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == "documents");

        calls.Clear();
        await icons.EnsureDefaultIconsAsync([documents.Id]); // e.g. a file was added to Documents
        Assert.Equal(0, calls.Count(nameof(ILibraryService.GetCategoryAsync)));
        Assert.Equal(0, calls.Count(nameof(ILibraryService.GetAllCategoriesAsync)));

        var created = await library.CreateCategoryAsync("Holiday", null);
        calls.Clear();
        await icons.EnsureDefaultIconsAsync([created.Id, documents.Id]);
        Assert.Equal(1, calls.Count(nameof(ILibraryService.GetCategoryAsync)));
        Assert.Equal(0, calls.Count(nameof(ILibraryService.GetAllCategoriesAsync)));
        Assert.Equal(FakeIconGenerator.Png, (await library.GetCategoryAsync(created.Id))!.IconPng);
    }

    // ------------------------------------------------------------------ finding 21

    [Fact]
    public async Task Paste_picker_offers_a_deleted_built_in_without_creating_it()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var icons = new CategoryIconService(library, new FakeIconGenerator());
        var notes = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.NotesKey);
        await library.DeleteCategoryAsync(notes.Id, CategoryDeletePolicy.MoveToDeleted);

        var cancelled = new CategoryPickerViewModel("Paste", library, icons, null, null, builtInKey: ExtensionMap.NotesKey);
        await cancelled.LoadAsync();
        Assert.Equal(ExtensionMap.NotesKey, cancelled.Selected?.BuiltInKeyToCreate);
        Assert.Null(cancelled.Selected!.Id);
        Assert.Contains(cancelled.Selected, cancelled.VisibleNodes);
        cancelled.CancelCommand.Execute(null);
        Assert.Null(await cancelled.Completion);
        Assert.DoesNotContain(await library.GetAllCategoriesAsync(), c => c.BuiltInKey == ExtensionMap.NotesKey);

        var chosen = new CategoryPickerViewModel("Paste", library, icons, null, null, builtInKey: ExtensionMap.NotesKey);
        await chosen.LoadAsync();
        chosen.ChooseCommand.Execute(null);
        var result = Assert.IsType<CategoryPickResult>(await chosen.Completion);
        Assert.Equal(ExtensionMap.NotesKey, result.BuiltInKeyToCreate);

        // The picker itself never creates anything; FileIntake creates it after this result.
        Assert.DoesNotContain(await library.GetAllCategoriesAsync(), c => c.BuiltInKey == ExtensionMap.NotesKey);
    }

    [Fact]
    public async Task Paste_picker_preselects_an_existing_built_in()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var images = (await library.GetAllCategoriesAsync()).Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);

        var picker = new CategoryPickerViewModel("Paste", library, new CategoryIconService(library, new FakeIconGenerator()), null, null, builtInKey: ExtensionMap.ImagesKey);
        await picker.LoadAsync();

        Assert.Equal(images.Id, picker.Selected?.Id);
        Assert.DoesNotContain(picker.VisibleNodes, n => n.BuiltInKeyToCreate is not null);
    }

    [Fact]
    public async Task Pickers_never_offer_Deleted()
    {
        var library = SampleLibraryService.Create(withVault: true);
        var picker = new CategoryPickerViewModel("Add to category", library, new CategoryIconService(library, new FakeIconGenerator()), null, null);
        await picker.LoadAsync();
        picker.SearchText = "Deleted";

        Assert.DoesNotContain(picker.VisibleNodes, n => n.Category?.Kind == CategoryKind.System);
        Assert.True(picker.IsEmpty);
    }
}

internal sealed class FakeIconGenerator : ICategoryIconGenerator
{
    public static readonly byte[] Png = [1, 2, 3];

    public byte[] Generate(string? iconKey, string seedText, int sizePx = 256) => Png;
}

/// <summary>Forwards every <see cref="ILibraryService"/> call to another library and counts the calls by method name.</summary>
public class CountingLibrary : DispatchProxy
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private ILibraryService _inner = null!;

    public static (ILibraryService Library, CountingLibrary Calls) Wrap(ILibraryService inner)
    {
        var proxy = Create<ILibraryService, CountingLibrary>();
        var counter = (CountingLibrary)(object)proxy;
        counter._inner = inner;
        return (proxy, counter);
    }

    public int Count(string method) => _calls.GetValueOrDefault(method);

    public void Clear() => _calls.Clear();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        _calls.AddOrUpdate(targetMethod!.Name, 1, (_, n) => n + 1);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
