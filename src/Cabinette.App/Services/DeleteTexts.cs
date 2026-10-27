using Cabinette.App.Localization;
using Cabinette.Core.Models;

namespace Cabinette.App.Services;

/// <summary>What deleting files does under the item-delete settings. Shared by the thread's and the category's delete dialogs.</summary>
public static class DeleteTexts
{
    /// <summary>
    /// One line for vault files (Recycle Bin or permanent) and one for linked files (original also recycled, or only removed
    /// from Cabinette), each only when such files are involved.
    /// </summary>
    /// <param name="single">Wording for one file ("The file will be…") instead of several ("Files stored in the vault will be…").</param>
    public static string Consequence(bool anyVaultFiles, bool anyPointerFiles, bool single, AppSettings settings)
    {
        var parts = new List<string>(2);
        if (anyVaultFiles)
        {
            var key = settings.RecycleVaultFiles ? "Thread_DeleteVaultRecycle" : "Thread_DeleteVaultPermanent";
            parts.Add(L.Get(key + (single ? "One" : "Many")));
        }

        if (anyPointerFiles)
        {
            var key = settings.DeleteRealPointerFiles ? "Thread_DeletePointerRecycle" : "Thread_DeletePointerLibraryOnly";
            parts.Add(L.Get(key + (single ? "One" : "Many")));
        }

        return string.Join("\n", parts);
    }
}
