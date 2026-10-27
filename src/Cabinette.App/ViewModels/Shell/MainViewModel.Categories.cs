using System.IO;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Models;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>Actions of the chat-list context menu.</summary>
public sealed partial class MainViewModel
{
    public Task OpenThreadAsync(CategoryRowViewModel row) => row.IsStarredRow ? OpenStarredAsync() : OpenCategoryAsync(row.CategoryId, null);

    public Task TogglePinAsync(CategoryRowViewModel row)
        => RunSafeAsync(() => _library.SetCategoryPinnedAsync(row.CategoryId, !row.Category.IsPinned));

    public Task RenameAsync(CategoryRowViewModel row) => RunSafeAsync(async () =>
    {
        var current = row.Category.Name;
        var name = await Dialogs.PromptTextAsync(
            L.Get("Category_RenameTitle"),
            L.Get("Category_NameLabel"),
            current,
            NameRules.ValidateCategoryName);
        if (name is null || name.Trim() == current)
        {
            return;
        }

        await _library.RenameCategoryAsync(row.CategoryId, name.Trim());
    });

    public Task ChangeIconAsync(CategoryRowViewModel row) => RunSafeAsync(async () =>
    {
        var path = Dialogs.PickIconFile();
        if (path is null)
        {
            return;
        }

        byte[] png;
        try
        {
            png = await CategoryIconService.LoadAndScaleIconAsync(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The chosen icon could not be read.", ex);
            Notifications.Show(L.Get("Category_IconFailed"), NotificationKind.Error);
            return;
        }

        await _library.SetCategoryIconAsync(row.CategoryId, png);
        Notifications.Show(L.Get("Category_IconUpdated"), NotificationKind.Success);
    });

    public Task ResetIconAsync(CategoryRowViewModel row)
        => RunSafeAsync(() => _icons.ResetIconAsync(row.Category));

    public Task NewSubcategoryAsync(CategoryRowViewModel row) => RunSafeAsync(async () =>
    {
        var created = await Dialogs.ShowNewCategoryAsync(row.CategoryId);
        if (created is not null)
        {
            Notifications.Show(L.Format("Category_Created", created.Name), NotificationKind.Success);
        }
    });

    public Task OpenFolderAsync(CategoryRowViewModel row) => RunSafeAsync(async () =>
    {
        var vault = _library.VaultPath;
        if (vault is null)
        {
            Notifications.Show(L.Get("Settings_VaultNotSet"), NotificationKind.Warning);
            return;
        }

        var chain = await _library.GetCategoryPathAsync(row.CategoryId);
        var folder = chain.Aggregate(vault, (path, category) => Path.Combine(path, category.FolderName));
        var exists = await Task.Run(() => Directory.Exists(folder));
        if (!exists)
        {
            Notifications.Show(L.Format("Category_FolderNotFound", folder), NotificationKind.Warning);
            return;
        }

        ShellLauncher.OpenFolder(_shell, folder);
    });

    public Task DeleteAsync(CategoryRowViewModel row) => RunSafeAsync(async () =>
    {
        var category = row.Category;
        if (category.Kind == CategoryKind.System)
        {
            await EmptyDeletedAsync();
            return;
        }

        var settings = await _library.GetSettingsAsync();
        var policy = settings.CategoryDeletePolicy;
        var title = L.Format("Category_DeleteTitle", category.Name);
        var count = row.Summary.ItemCount;
        var countText = count == 1 ? L.Get("Category_DeleteHasOne") : L.Format("Category_DeleteHasMany", count);

        // Deleting the files follows the item-delete settings (linked files' originals too), so say it with the same text.
        var consequence = count > 0 && policy != CategoryDeletePolicy.MoveToDeleted ? await DeleteConsequenceAsync(category.Id, settings) : "";

        if (policy == CategoryDeletePolicy.Ask)
        {
            var message = $"{countText} {L.Get("Category_DeleteAskMessage")}";
            if (consequence.Length > 0)
            {
                message += "\n\n" + L.Format("Category_DeleteFilesMeans", consequence);
            }

            var choice = await Dialogs.ChooseAsync(
                title,
                message,
                [L.Get("Category_DeleteMoveOption"), L.Get(settings.RecycleVaultFiles ? "Category_DeleteRecycleOption" : "Category_DeleteFilesOption")]);
            if (choice is null)
            {
                return;
            }

            policy = choice == 0 ? CategoryDeletePolicy.MoveToDeleted : CategoryDeletePolicy.RecycleFiles;
        }
        else
        {
            var message = (count, policy) switch
            {
                (0, _) => L.Get("Category_DeleteEmptyMessage"),
                (_, CategoryDeletePolicy.MoveToDeleted) => $"{countText} {L.Get("Category_DeleteMoveMessage")}",

                // Every file is also in another category: none is deleted.
                _ when consequence.Length == 0 => $"{countText} {L.Get("Category_DeleteEmptyMessage")}",
                _ => $"{countText} {L.Get("Category_DeleteRecycleMessage")}\n{consequence}",
            };
            if (!await Dialogs.ConfirmAsync(title, message, L.Get("Category_DeleteConfirm"), isDestructive: true))
            {
                return;
            }
        }

        if (await RecycleBinFallback.RunAsync(Dialogs, options => _library.DeleteCategoryAsync(category.Id, policy, options)))
        {
            Notifications.Show(L.Format("Category_Deleted", category.Name), NotificationKind.Success);
        }
    });

    public Task EmptyDeletedAsync() => RunSafeAsync(async () =>
    {
        if (!await Dialogs.ConfirmAsync(
                L.Get("Category_EmptyDeletedTitle"),
                L.Get("Category_EmptyDeletedMessage"),
                L.Get("Category_EmptyDeletedConfirm"),
                isDestructive: true))
        {
            return;
        }

        if (await RecycleBinFallback.RunAsync(Dialogs, options => _library.EmptyDeletedAsync(options)))
        {
            Notifications.Show(L.Get("Category_EmptiedDeleted"), NotificationKind.Success);
        }
    });

    /// <summary>
    /// The item-delete consequence for the files that go when the category is deleted with its files: those whose
    /// categories all lie inside it (the others only lose these memberships). Empty when there are none.
    /// </summary>
    private async Task<string> DeleteConsequenceAsync(long categoryId, AppSettings settings)
    {
        try
        {
            var items = await Task.Run(() => _library.GetThreadItemsAsync(categoryId));
            await _directory.EnsureLoadedAsync();
            var subtree = _directory.Get(categoryId) is null ? null : _directory.GetSubtree(categoryId);
            var onlyHere = subtree is null ? items : items.Where(i => i.CategoryIds.All(subtree.Contains)).ToList();
            return DeleteTexts.Consequence(onlyHere.Any(i => i.Mode == ItemMode.Copy), onlyHere.Any(i => i.Mode == ItemMode.Pointer), single: false, settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Listing the files of a category for its delete dialog failed.", ex);
            return DeleteTexts.Consequence(anyVaultFiles: true, anyPointerFiles: true, single: false, settings);
        }
    }
}
