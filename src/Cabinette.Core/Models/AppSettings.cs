namespace Cabinette.Core.Models;

/// <summary>All user settings. Stored in the database. Defaults match SPEC.md section 12.</summary>
public sealed record AppSettings
{
    /// <summary>Null until the user picks a vault on first run.</summary>
    public string? VaultPath { get; init; }

    public ItemMode DefaultAddMode { get; init; } = ItemMode.Copy;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public TextSize TextSize { get; init; } = TextSize.Medium;

    /// <summary>
    /// "Bold text" (SPEC.md section 11): all text one weight heavier. Off by default; settings saved before it existed
    /// don't have it, so it is off for them too.
    /// </summary>
    public bool BoldText { get; init; }

    /// <summary>"Font" (SPEC.md section 11). Settings saved before it existed, or by a newer build with a font this one doesn't know, read as the default.</summary>
    public AppFont Font { get; init; } = AppFont.SegoeUIVariable;

    public bool StartWithWindows { get; init; } = true;

    public bool CloseToTray { get; init; } = true;

    public bool NotifyOnVaultChanges { get; init; } = true;

    /// <summary>True = deleted vault files go to the Recycle Bin; false = permanent.</summary>
    public bool RecycleVaultFiles { get; init; } = true;

    /// <summary>True = deleting a pointer item also deletes the real file (to the Recycle Bin); false = only remove it from the library.</summary>
    public bool DeleteRealPointerFiles { get; init; } = true;

    public CategoryDeletePolicy CategoryDeletePolicy { get; init; } = CategoryDeletePolicy.MoveToDeleted;

    public bool FolderSubfoldersAsSubcategories { get; init; } = true;

    public bool FolderAlsoAddToTypeCategories { get; init; }

    /// <summary>
    /// "Sort files by type in type categories" (SPEC.md section 5): files dropped onto a built-in type category, or picked with
    /// its paperclip, that are of a known type belonging to another type category go there instead. The UI passes it to
    /// <see cref="AddRequest.SortIntoTypeCategories"/>; the library itself doesn't read it.
    /// </summary>
    public bool SortFilesByTypeInTypeCategories { get; init; } = true;

    /// <summary>
    /// "Clean My Downloads mode" (SPEC.md section 5): new files in the Downloads folder are moved into the vault and the
    /// originals sent to the Recycle Bin. Off by default. The app runs it; the library itself doesn't read it.
    /// </summary>
    public bool CleanMyDownloads { get; init; }

    public CategorySort ChatListSort { get; init; } = CategorySort.LastUpload;
}
