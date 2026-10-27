using Cabinette.App.Localization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;

namespace Cabinette.App.Services;

/// <summary>
/// Opens a library file in its default app from outside a thread (Shift+Enter in the quick switcher), the same way a
/// double-click in a thread does (SPEC section 6): the file is checked first, and a missing linked file is marked missing
/// and offered "Locate…". Library calls run off the UI thread.
/// </summary>
/// <remarks>
/// The thread view has its own copy of this flow on its item view models (ThreadViewModel.OpenItemAsync / LocateAsync),
/// which also update the bubble in place; this one works from a <see cref="LibraryItem"/> and lets the open thread
/// pick the change up from the library.
/// </remarks>
public sealed class ItemOpener
{
    private readonly ILibraryService _library;
    private readonly IShellOperations _shell;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;

    public ItemOpener(ILibraryService library, IShellOperations shell, IDialogService dialogs, INotificationService notifications)
    {
        _library = library;
        _shell = shell;
        _dialogs = dialogs;
        _notifications = notifications;
    }

    public async Task OpenAsync(LibraryItem item)
    {
        try
        {
            // The search result may be older than a rename or a re-link: use the item as it is now.
            var current = await Task.Run(() => _library.GetItemAsync(item.Id));
            if (current is null)
            {
                _notifications.Show(L.Format("QuickSwitcher_FileGone", item.FileName), NotificationKind.Warning);
                return;
            }

            var exists = await Task.Run(() => _library.VerifyExistsAsync(current.Id));
            if (exists)
            {
                _shell.OpenWithDefaultApp(current.FullPath);
                return;
            }

            await LocateAsync(current);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>"File not found" prompt, file picker, hash-mismatch confirmation (pointer files only).</summary>
    private async Task LocateAsync(LibraryItem item)
    {
        if (item.Mode != ItemMode.Pointer)
        {
            _notifications.Show(L.Format("Thread_VaultFileMissing", item.FileName), NotificationKind.Error);
            return;
        }

        if (!await _dialogs.ConfirmAsync(L.Get("Thread_MissingTitle"), L.Format("Thread_MissingMessage", item.FileName), L.Get("Thread_LocateButton")))
        {
            return;
        }

        var path = await _dialogs.PickFileToLocateAsync(item.FileName);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var outcome = await Task.Run(() => _library.RelocateItemAsync(item.Id, path, false));
        if (outcome == RelocateOutcome.HashMismatch)
        {
            if (!await _dialogs.ConfirmAsync(L.Get("Thread_HashMismatchTitle"), L.Format("Thread_HashMismatchMessage", item.FileName), L.Get("Thread_HashMismatchConfirm")))
            {
                return;
            }

            outcome = await Task.Run(() => _library.RelocateItemAsync(item.Id, path, true));
        }

        if (outcome == RelocateOutcome.Relocated)
        {
            _notifications.Show(L.Format("Thread_Relocated", item.FileName), NotificationKind.Success);
        }
        else
        {
            _notifications.Show(L.Get("Thread_RelocateNotFound"), NotificationKind.Error);
        }
    }

    private void ShowError(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return;
            case LibraryException library:
                _notifications.Show(library.Message, NotificationKind.Error);
                return;
            default:
                AppLog.Error("Opening a file from the quick switcher failed.", ex);
                _notifications.Show(L.Format("Thread_UnexpectedError", ex.Message), NotificationKind.Error);
                return;
        }
    }
}
