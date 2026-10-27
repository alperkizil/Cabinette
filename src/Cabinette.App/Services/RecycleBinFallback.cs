using Cabinette.App.Localization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Services;

/// <summary>
/// Deletes that may meet a drive without a Recycle Bin (network shares, USB sticks) or a file too large for it. Core then
/// refuses with <see cref="LibraryErrorCode.RecycleBinUnavailable"/>, normally before changing anything; the user is asked
/// "No Recycle Bin on this drive. Delete permanently?" and the same delete runs again with
/// <see cref="DeleteItemsOptions.PermanentWhenNoRecycleBin"/> only after a clear yes. When the shell refused files only after
/// the delete was committed, Core remembers exactly those files and the retry deletes them.
/// </summary>
public static class RecycleBinFallback
{
    /// <summary>
    /// Runs <paramref name="delete"/>. Returns false when the user declined the permanent delete (nothing changed, except after a
    /// refusal that came after the commit: then the items are gone from the library and their refused files stay on disk).
    /// </summary>
    public static async Task<bool> RunAsync(IDialogService dialogs, Func<DeleteItemsOptions?, Task> delete, DeleteItemsOptions? options = null)
    {
        try
        {
            await delete(options);
            return true;
        }
        catch (LibraryException ex) when (ex.Code == LibraryErrorCode.RecycleBinUnavailable)
        {
            if (!await ConfirmPermanentDeleteAsync(dialogs, ex.Message))
            {
                return false;
            }
        }

        await delete((options ?? new DeleteItemsOptions()) with { PermanentWhenNoRecycleBin = true });
        return true;
    }

    /// <summary>"No Recycle Bin on this drive. Delete permanently?" True only when the user confirmed.</summary>
    /// <param name="detail">Which files or drive, shown above the question (e.g. the library's message).</param>
    public static Task<bool> ConfirmPermanentDeleteAsync(IDialogService dialogs, string? detail = null)
    {
        var question = L.Get("RecycleBin_DeletePermanentlyMessage");
        var message = string.IsNullOrWhiteSpace(detail) ? question : detail.Trim() + "\n\n" + question;
        return dialogs.ConfirmAsync(L.Get("RecycleBin_UnavailableTitle"), message, L.Get("RecycleBin_DeletePermanentlyButton"), isDestructive: true);
    }
}
