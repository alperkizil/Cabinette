using System.Collections.Specialized;
using System.IO;
using System.Windows;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Thread;

// Actions on files (SPEC.md §6), the composer and adding files (§5).
public partial class ThreadViewModel
{
    private const string MenuOpen = "open";
    private const string MenuMoreOptions = "more";
    private const string MenuShowInFolder = "show";
    private const string MenuRename = "rename";
    private const string MenuCopy = "copy";
    private const string MenuTags = "tags";
    private const string MenuStar = "star";
    private const string MenuMoveTo = "moveto";
    private const string MenuAddTo = "addto";
    private const string MenuRemove = "remove";
    private const string MenuDelete = "delete";
    private const int MaxOpenAtOnce = 10;

    /// <summary>
    /// Token for library calls the user already asked for or confirmed in a dialog. Deliberately not the view model's
    /// lifetime: when another thread opens while a dialog is up, what the user then confirms still happens (item and
    /// category ids stay valid after the switch) instead of being silently cancelled.
    /// </summary>
    private static CancellationToken Confirmed => CancellationToken.None;

    // ------------------------------------------------------------------ composer & intake

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendNoteAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0 || !CanAddFiles)
        {
            return;
        }

        ComposerText = "";
        _scrollToEndOnArrival = true;
        try
        {
            var id = CategoryId;
            await Task.Run(() => _s.Library.CreateNoteAsync(id, text, AddedBy.AppUi, Confirmed));
            await ReloadItemsAsync();
        }
        catch (Exception ex)
        {
            if (ComposerText.Length == 0)
            {
                ComposerText = text;
            }

            ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        if (!CanAddFiles)
        {
            return;
        }

        _scrollToEndOnArrival = true;
        try
        {
            await _s.Intake.PickAndAddFilesAsync(CategoryId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Ctrl+V inside the thread: files, an image, text or a URL become files in this category.</summary>
    [RelayCommand]
    public async Task PasteAsync()
    {
        if (!CanAddFiles)
        {
            return;
        }

        _scrollToEndOnArrival = true;
        try
        {
            await _s.Intake.PasteAsync(CategoryId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Files or folders dropped onto the thread.</summary>
    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !CanAddFiles)
        {
            return;
        }

        _scrollToEndOnArrival = true;
        try
        {
            await _s.Intake.AddPathsAsync(paths, CategoryId);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    private void OpenSubcategory(SubcategoryChip? chip)
    {
        if (chip is not null)
        {
            _s.Navigator.OpenCategory(chip.Id);
        }
    }

    // ------------------------------------------------------------------ open, view, locate

    [RelayCommand]
    public async Task OpenSelectedAsync()
    {
        foreach (var item in SelectedTargets().Take(MaxOpenAtOnce))
        {
            await OpenItemAsync(item);
        }
    }

    /// <summary>Double-click / Enter: verify the file, offer Locate… for missing pointers, otherwise open it in its default app.</summary>
    public async Task OpenItemAsync(ThreadItemViewModel item)
    {
        bool exists;
        try
        {
            exists = await Task.Run(() => _s.Library.VerifyExistsAsync(item.Id, _lifetime.Token));
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }

        if (_disposed)
        {
            return;
        }

        item.SetMissing(!exists);
        if (!exists)
        {
            await LocateAsync(item, prompt: true);
            return;
        }

        OpenExternally(item);
    }

    public void OpenExternally(ThreadItemViewModel item)
    {
        try
        {
            _s.Shell.OpenWithDefaultApp(item.FullPath);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Opens the built-in viewer on the thread's images and videos, starting at <paramref name="item"/>.</summary>
    public void OpenInViewer(ThreadItemViewModel item)
    {
        if (item.IsMissing || item.DisplayKind is not (ItemVisualKind.Image or ItemVisualKind.Video))
        {
            _ = OpenItemAsync(item);
            return;
        }

        // The viewer steps through the files in the order on screen (the Gallery has its own).
        var order = ViewMode == ThreadViewMode.Gallery ? GetGalleryOrder() : _items;
        var media = order.Where(i => i.DisplayKind is ItemVisualKind.Image or ItemVisualKind.Video).ToList();
        var index = media.IndexOf(item);
        if (index < 0)
        {
            media = [item];
            index = 0;
        }

        _host?.ShowMediaViewer(media, index);
    }

    /// <summary>Re-links a missing pointer: optional "file not found" prompt, file picker, hash-mismatch confirmation.</summary>
    internal async Task LocateAsync(ThreadItemViewModel item, bool prompt)
    {
        if (!item.IsPointer)
        {
            _s.Notifications.Show(L.Format("Thread_VaultFileMissing", item.FileName), NotificationKind.Error);
            return;
        }

        try
        {
            if (prompt && !await _s.Dialogs.ConfirmAsync(L.Get("Thread_MissingTitle"), L.Format("Thread_MissingMessage", item.FileName), L.Get("Thread_LocateButton")))
            {
                return;
            }

            var path = await _s.Dialogs.PickFileToLocateAsync(item.FileName);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var outcome = await Task.Run(() => _s.Library.RelocateItemAsync(item.Id, path, false, Confirmed));
            if (outcome == RelocateOutcome.HashMismatch)
            {
                if (!await _s.Dialogs.ConfirmAsync(L.Get("Thread_HashMismatchTitle"), L.Format("Thread_HashMismatchMessage", item.FileName), L.Get("Thread_HashMismatchConfirm")))
                {
                    return;
                }

                outcome = await Task.Run(() => _s.Library.RelocateItemAsync(item.Id, path, true, Confirmed));
            }

            if (outcome == RelocateOutcome.Relocated)
            {
                item.SetMissing(false);
                _s.Notifications.Show(L.Format("Thread_Relocated", item.FileName), NotificationKind.Success);
            }
            else
            {
                _s.Notifications.Show(L.Get("Thread_RelocateNotFound"), NotificationKind.Error);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    internal async Task<string?> ReadNoteTextAsync(ThreadItemViewModel item)
    {
        try
        {
            return await Task.Run(() => _s.Library.ReadNoteTextAsync(item.Id, _lifetime.Token));
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ actions on the selection

    [RelayCommand]
    private void ShowSelectedInFolder()
    {
        var item = SelectedTargets().FirstOrDefault(i => !i.IsMissing);
        if (item is null)
        {
            return;
        }

        try
        {
            _s.Shell.ShowInExplorer(item.FullPath);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    public async Task RenameSelectedAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count != 1)
        {
            return;
        }

        var item = targets[0];
        var newName = await _s.Dialogs.PromptTextAsync(
            L.Get("Thread_RenameTitle"), L.Get("Thread_RenameLabel"), item.FileName, ValidateFileName, selectFileNameWithoutExtension: true);
        newName = newName?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.FileName)
        {
            return;
        }

        try
        {
            await Task.Run(() => _s.Library.RenameItemAsync(item.Id, newName, Confirmed));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Ctrl+C: puts the existing files on the clipboard as a file drop list (paste in Explorer).</summary>
    [RelayCommand]
    public async Task CopySelectedAsync()
    {
        var paths = SelectedTargets().Where(i => !i.IsMissing).Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
        {
            if (_selection.Count > 0)
            {
                _s.Notifications.Show(L.Get("Thread_CopyNothing"), NotificationKind.Warning);
            }

            return;
        }

        var existing = await Task.Run(() => paths.Where(File.Exists).ToArray());
        if (existing.Length == 0)
        {
            _s.Notifications.Show(L.Get("Thread_CopyNothing"), NotificationKind.Warning);
            return;
        }

        try
        {
            var list = new StringCollection();
            list.AddRange(existing);
            Clipboard.SetFileDropList(list);
            _s.Notifications.Show(existing.Length == 1 ? L.Get("Thread_CopiedOne") : L.Format("Thread_CopiedMany", existing.Length), NotificationKind.Success);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var ids = targets.Select(t => t.Id).ToList();
        var categoryId = CategoryId;
        var settings = _settings ?? new AppSettings();
        var title = targets.Count == 1 ? L.Format("Thread_DeleteTitleOne", targets[0].FileName) : L.Format("Thread_DeleteTitleMany", targets.Count);
        var consequence = DeleteTexts.Consequence(targets.Any(i => !i.IsPointer), targets.Any(i => i.IsPointer), targets.Count == 1, settings);
        try
        {
            // The Starred view is no category to take the files out of: deleting there always deletes everywhere.
            var subtree = IsStarredView ? null : await LoadSubtreeAsync();
            var onlyHere = IsStarredView ? targets.Count : targets.Count(t => IsOnlyInThread(t.Item, subtree));
            if (onlyHere < targets.Count)
            {
                // Some files are also in other categories: offer to just take them out of this one. Files that are only
                // here would end up in Deleted that way, so say so (a mixed selection).
                var message = targets.Count == 1 ? L.Get("Thread_DeleteInOtherCategoriesOne") : L.Get("Thread_DeleteInOtherCategoriesMany");
                if (onlyHere > 0)
                {
                    message += "\n\n" + (onlyHere == 1 ? L.Get("Thread_RemoveMeansOnlyHereOne") : L.Format("Thread_RemoveMeansOnlyHereMany", onlyHere));
                }

                message += "\n\n" + L.Format("Thread_DeleteEverywhereMeans", consequence);
                var choice = await _s.Dialogs.ChooseAsync(title, message, [L.Get("Thread_RemoveFromThisCategory"), L.Get("Thread_DeleteEverywhere")]);
                switch (choice)
                {
                    case 0:
                        await Task.Run(() => _s.Library.RemoveFromCategoryAsync(ids, categoryId, Confirmed));
                        break;
                    case 1:
                        if (!await DeleteEverywhereAsync(ids))
                        {
                            return;
                        }

                        break;
                    default:
                        return;
                }
            }
            else
            {
                if (!await _s.Dialogs.ConfirmAsync(title, consequence, L.Get("Thread_DeleteButton"), isDestructive: true) ||
                    !await DeleteEverywhereAsync(ids))
                {
                    return;
                }
            }

            ClearSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Deletes with the settings; on a drive without a Recycle Bin, asks before deleting permanently. False when declined.</summary>
    private Task<bool> DeleteEverywhereAsync(IReadOnlyList<long> ids)
        => RecycleBinFallback.RunAsync(_s.Dialogs, options => Task.Run(() => _s.Library.DeleteItemsAsync(ids, options, Confirmed)));

    /// <summary>
    /// True when removing the item from this thread leaves it in no category (it would move to Deleted): all its categories
    /// are this thread's category or its subcategories. Without the tree (<paramref name="threadSubtree"/> null), only items
    /// with a single category count.
    /// </summary>
    internal static bool IsOnlyInThread(LibraryItem item, IReadOnlySet<long>? threadSubtree)
        => threadSubtree is null ? item.CategoryIds.Count <= 1 : item.CategoryIds.All(threadSubtree.Contains);

    /// <summary>This thread's category and all its subcategories, or null when the category tree is not available.</summary>
    private async Task<IReadOnlySet<long>?> LoadSubtreeAsync()
    {
        if (_s.Directory is not { } directory)
        {
            return null;
        }

        var id = CategoryId;
        await Task.Run(() => directory.EnsureLoadedAsync());
        return directory.Get(id) is null ? null : directory.GetSubtree(id);
    }

    [RelayCommand]
    public async Task EditTagsAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0 || _host is null)
        {
            return;
        }

        var editor = await CreateTagEditorAsync(targets);
        if (!_host.ShowTagEditor(editor))
        {
            return;
        }

        var ids = targets.Select(t => t.Id).ToList();
        try
        {
            if (targets.Count == 1)
            {
                var tags = editor.FinalTags;
                await Task.Run(() => _s.Library.SetItemTagsAsync(ids[0], tags, Confirmed));
            }
            else
            {
                var removed = editor.RemovedTags;
                var added = editor.AddedTags;
                if (removed.Count > 0)
                {
                    await Task.Run(() => _s.Library.RemoveTagsAsync(ids, removed, Confirmed));
                }

                if (added.Count > 0)
                {
                    await Task.Run(() => _s.Library.AddTagsAsync(ids, added, Confirmed));
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Builds the tag editor for the current selection (public for the dev harness).</summary>
    public Task<TagEditorViewModel> CreateTagEditorForSelectionAsync() => CreateTagEditorAsync(SelectedTargets());

    [RelayCommand]
    public async Task AddSelectedToCategoryAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var target = await _s.Dialogs.PickCategoryAsync(L.Get("Thread_AddToCategoryTitle"), null, allowCreate: true);
        if (target is not { } categoryId)
        {
            return;
        }

        var ids = targets.Select(t => t.Id).ToList();
        try
        {
            await Task.Run(() => _s.Library.AddToCategoryAsync(ids, categoryId, AddedBy.AppUi, Confirmed));
            var category = await Task.Run(() => _s.Library.GetCategoryAsync(categoryId, Confirmed));
            var name = category?.Name ?? "";
            _s.Notifications.Show(
                ids.Count == 1 ? L.Format("Thread_AddedToCategoryOne", name) : L.Format("Thread_AddedToCategoryMany", ids.Count, name),
                NotificationKind.Success,
                L.Get("Thread_OpenAction"),
                () => _s.Navigator.OpenCategory(categoryId));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>
    /// "Move to category…" (SPEC.md section 6): the selection leaves this thread (its category and subcategories) for the
    /// picked category; other categories stay. A vault file whose home it leaves moves into the new category's folder.
    /// The user's own move leaves nothing unread; the thread stays open with a notice that offers "Show".
    /// </summary>
    [RelayCommand]
    public async Task MoveSelectedToCategoryAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0 || IsStarredView)
        {
            // The Starred view has no category to move out of (SPEC.md section 4).
            return;
        }

        var target = await _s.Dialogs.PickCategoryAsync(L.Get("Thread_MoveToCategoryTitle"), null, allowCreate: true);
        var fromId = CategoryId;
        if (target is not { } toId || toId == fromId)
        {
            // Nothing to move: the files already are in this thread.
            return;
        }

        var ids = targets.Select(t => t.Id).ToList();
        try
        {
            await Task.Run(() => _s.Library.MoveToCategoryAsync(ids, fromId, toId, AddedBy.AppUi, Confirmed));
            ClearSelection();
            var category = await Task.Run(() => _s.Library.GetCategoryAsync(toId, Confirmed));
            var name = category?.Name ?? "";
            var focusId = ids.Count == 1 ? ids[0] : (long?)null;
            _s.Notifications.Show(
                ids.Count == 1 ? L.Format("Thread_MovedToCategoryOne", name) : L.Format("Thread_MovedToCategoryMany", ids.Count, name),
                NotificationKind.Success,
                L.Get("Thread_ShowAction"),
                () => _s.Navigator.OpenCategory(toId, focusId));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Removes the selection from this category. Asks first when some files would end up in Deleted.</summary>
    [RelayCommand]
    public async Task RemoveSelectedFromCategoryAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0 || IsStarredView)
        {
            return;
        }

        var ids = targets.Select(t => t.Id).ToList();
        var categoryId = CategoryId;
        try
        {
            var subtree = await LoadSubtreeAsync();
            var onlyHere = targets.Count(t => IsOnlyInThread(t.Item, subtree));
            if (onlyHere > 0)
            {
                var message = (onlyHere, targets.Count) switch
                {
                    (1, 1) => L.Format("Thread_RemoveOnlyHereOne", targets[0].FileName),
                    (1, _) => L.Get("Thread_RemoveOnlyHereOneOfMany"),
                    _ => L.Format("Thread_RemoveOnlyHereMany", onlyHere),
                };
                if (!await _s.Dialogs.ConfirmAsync(L.Get("Thread_RemoveTitle"), message, L.Get("Thread_RemoveButton")))
                {
                    return;
                }
            }

            await Task.Run(() => _s.Library.RemoveFromCategoryAsync(ids, categoryId, Confirmed));
            ClearSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>
    /// Star or Unstar (SPEC.md section 4): a selection with any unstarred file is starred, an all-starred one unstarred. The
    /// selection stays; in the Starred view unstarred files leave the view when the library reports the change.
    /// </summary>
    [RelayCommand]
    public async Task ToggleStarSelectedAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var star = !targets.All(t => t.IsStarred);
        var ids = targets.Select(t => t.Id).ToList();
        try
        {
            await Task.Run(() => _s.Library.SetStarredAsync(ids, star, Confirmed));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    // ------------------------------------------------------------------ context menus

    /// <summary>One entry of the styled right-click menu. A null entry in the list is a separator.</summary>
    public sealed record ThreadMenuEntry(string Id, string Text, SymbolRegular Symbol, bool IsEnabled = true, bool IsDanger = false);

    /// <summary>
    /// The styled right-click menu for the selection (same look as the category menu). Its last entry,
    /// "Show more options", opens the real Windows classic menu, like Windows 11.
    /// </summary>
    public IReadOnlyList<ThreadMenuEntry?> GetContextMenuEntries()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            return [];
        }

        var anyExisting = targets.Any(t => !t.IsMissing);
        var unstar = targets.All(t => t.IsStarred);
        var entries = new List<ThreadMenuEntry?>
        {
            new(MenuOpen, L.Get("Thread_MenuOpen"), SymbolRegular.Open20, targets.Count == 1),
            new(MenuShowInFolder, L.Get("Thread_MenuShowInFolder"), SymbolRegular.FolderOpen20, anyExisting),
            null,
            new(MenuRename, L.Get("Thread_MenuRename"), SymbolRegular.Rename20, targets.Count == 1),
            new(MenuCopy, L.Get("Thread_MenuCopy"), SymbolRegular.Copy20, anyExisting),
            new(MenuTags, L.Get("Thread_MenuTags"), SymbolRegular.Tag20),
            new(MenuStar, L.Get(unstar ? "Thread_MenuUnstar" : "Thread_MenuStar"), unstar ? SymbolRegular.StarOff20 : SymbolRegular.Star20),
        };

        // The Starred view is no category: nothing to move out of or remove from.
        if (!IsStarredView)
        {
            entries.Add(new(MenuMoveTo, L.Get("Thread_MenuMoveToCategory"), SymbolRegular.FolderArrowRight20));
        }

        entries.Add(new(MenuAddTo, L.Get("Thread_MenuAddToCategory"), SymbolRegular.FolderAdd20));
        if (!IsStarredView && targets.Any(t => t.Item.CategoryIds.Count > 1))
        {
            entries.Add(new(MenuRemove, L.Get("Thread_MenuRemoveFromCategory"), SymbolRegular.FolderArrowLeft20));
        }

        entries.Add(null);
        entries.Add(new(MenuDelete, L.Get("Thread_MenuDelete"), SymbolRegular.Delete20, IsDanger: true));
        entries.Add(null);
        entries.Add(new(MenuMoreOptions, L.Get("Thread_MenuMoreOptions"), SymbolRegular.MoreHorizontal20, anyExisting));
        return entries;
    }

    /// <summary>
    /// Runs a choice from the styled menu. <paramref name="screenPointPx"/> is where the menu was opened, in physical
    /// pixels; "Show more options" opens the Windows menu there.
    /// </summary>
    public void RunContextMenuAction(string id, Point screenPointPx)
    {
        switch (id)
        {
            case MenuOpen:
                _ = OpenSelectedAsync();
                break;
            case MenuMoreOptions:
                ShowShellContextMenu(screenPointPx);
                break;
            default:
                RunMenuItem(id);
                break;
        }
    }

    /// <summary>
    /// Shows the real Windows classic menu for the selection (Open with, Send to, 7-Zip...) with Cabinette's items on
    /// top, then runs the choice. Used by "Show more options" and Shift+right-click.
    /// <paramref name="screenPointPx"/> is in physical screen pixels. UI thread (the menu is modal).
    /// </summary>
    public void ShowShellContextMenu(Point screenPointPx)
    {
        var targets = SelectedTargets();
        if (targets.Count == 0 || _host is null)
        {
            return;
        }

        var anyExisting = targets.Any(t => !t.IsMissing);
        var items = new List<CustomMenuItem>
        {
            new(MenuShowInFolder, L.Get("Thread_MenuShowInFolder")) { IsEnabled = anyExisting },
            new(MenuRename, L.Get("Thread_MenuRename")) { IsEnabled = targets.Count == 1 },
            new(MenuCopy, L.Get("Thread_MenuCopy")) { IsEnabled = anyExisting },
            new(MenuTags, L.Get("Thread_MenuTags")),
            new(MenuStar, L.Get(targets.All(t => t.IsStarred) ? "Thread_MenuUnstar" : "Thread_MenuStar")),
        };

        if (!IsStarredView)
        {
            items.Add(new(MenuMoveTo, L.Get("Thread_MenuMoveToCategory")));
        }

        items.Add(new(MenuAddTo, L.Get("Thread_MenuAddToCategory")));
        if (!IsStarredView && targets.Any(t => t.Item.CategoryIds.Count > 1))
        {
            items.Add(new(MenuRemove, L.Get("Thread_MenuRemoveFromCategory")));
        }

        items.Add(CustomMenuItem.Separator);
        items.Add(new(MenuDelete, L.Get("Thread_MenuDelete")));

        ContextMenuResult result;
        try
        {
            result = _s.ContextMenu.Show(_host.OwnerHandle, targets.Select(t => t.FullPath).ToList(), screenPointPx, items);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }

        switch (result.Verb)
        {
            case InterceptedShellVerb.Delete:
                _ = DeleteSelectedAsync();
                return;
            case InterceptedShellVerb.Rename:
                _ = RenameSelectedAsync();
                return;
        }

        RunMenuItem(result.CustomItemId);
    }

    private void RunMenuItem(string? id)
    {
        switch (id)
        {
            case MenuShowInFolder:
                ShowSelectedInFolder();
                break;
            case MenuRename:
                _ = RenameSelectedAsync();
                break;
            case MenuCopy:
                _ = CopySelectedAsync();
                break;
            case MenuTags:
                _ = EditTagsAsync();
                break;
            case MenuStar:
                _ = ToggleStarSelectedAsync();
                break;
            case MenuMoveTo:
                _ = MoveSelectedToCategoryAsync();
                break;
            case MenuAddTo:
                _ = AddSelectedToCategoryAsync();
                break;
            case MenuRemove:
                _ = RemoveSelectedFromCategoryAsync();
                break;
            case MenuDelete:
                _ = DeleteSelectedAsync();
                break;
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<TagEditorViewModel> CreateTagEditorAsync(IReadOnlyList<ThreadItemViewModel> targets)
    {
        IReadOnlyList<TagInfo> all = [];
        try
        {
            all = await Task.Run(() => _s.Library.GetTagsAsync(_lifetime.Token));
        }
        catch (Exception)
        {
            // Autocomplete is optional.
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var common = targets.Count == 0
            ? []
            : targets.Skip(1).Aggregate(
                new HashSet<string>(targets[0].Tags, comparer),
                (set, item) =>
                {
                    set.IntersectWith(item.Tags);
                    return set;
                });
        var partial = targets.SelectMany(t => t.Tags).Distinct(comparer).Where(t => !common.Contains(t)).ToList();
        var orderedCommon = targets.SelectMany(t => t.Tags).Distinct(comparer).Where(common.Contains).ToList();
        var subtitle = targets.Count == 1 ? targets[0].FileName : L.Format("Thread_TagsSubtitleMany", targets.Count);
        return new TagEditorViewModel(L.Get("Thread_TagsTitle"), subtitle, orderedCommon, partial, all);
    }

    private static string? ValidateFileName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return L.Get("Thread_NameEmpty");
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed is "." or ".." || trimmed.EndsWith('.'))
        {
            return L.Get("Thread_NameInvalid");
        }

        return null;
    }

    private void ShowError(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return;
            case LibraryException library:
                _s.Notifications.Show(library.Message, NotificationKind.Error);
                return;
            default:
                _s.Notifications.Show(L.Format("Thread_UnexpectedError", ex.Message), NotificationKind.Error);
                return;
        }
    }
}
