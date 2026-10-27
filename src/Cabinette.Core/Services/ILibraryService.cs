using Cabinette.Core.Models;

namespace Cabinette.Core.Services;

/// <summary>
/// The whole library: categories, items, tags, settings and the vault on disk.
/// Every method is thread-safe and may be called from any thread. Every change raises <see cref="Changed"/>.
/// Errors that the user should see (name clash, invalid name, file locked, ...) are thrown as <see cref="LibraryException"/>.
/// </summary>
public interface ILibraryService : IAsyncDisposable
{
    /// <summary>Raised after every change, on a background thread.</summary>
    event EventHandler<LibraryChangedEventArgs>? Changed;

    // ---------------------------------------------------------------- settings & vault

    /// <summary>Current vault root folder, or null before first-run setup.</summary>
    string? VaultPath { get; }

    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Saves settings. Changing <see cref="AppSettings.VaultPath"/> here is not allowed (moving the vault is a stage 2 feature).</summary>
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>First run: stores the vault path and creates a folder for every category in it.</summary>
    Task InitializeVaultAsync(string vaultPath, CancellationToken ct = default);

    // ---------------------------------------------------------------- categories

    /// <summary>
    /// Chat-list rows for the direct children of <paramref name="parentId"/> (null = top level).
    /// Pinned first, then by <paramref name="sort"/> (LastUpload: by the newest file still in the subtree, newest first, so a
    /// category falls back when its newest file leaves; empty categories last, A–Z among them; empty means no files in the
    /// subtree, whether never used or emptied).
    /// The system "Deleted" category is only returned when it contains items.
    /// </summary>
    Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync(long? parentId, CategorySort sort, CancellationToken ct = default);

    Task<CategorySummary?> GetCategorySummaryAsync(long categoryId, CancellationToken ct = default);

    Task<Category?> GetCategoryAsync(long categoryId, CancellationToken ct = default);

    /// <summary>Every category (flat list), for pickers. Callers build the tree from <see cref="Category.ParentId"/>.</summary>
    Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken ct = default);

    /// <summary>Path from the top-level ancestor down to the category itself (for breadcrumbs).</summary>
    Task<IReadOnlyList<Category>> GetCategoryPathAsync(long categoryId, CancellationToken ct = default);

    /// <summary>Finds a built-in category by key (e.g. "notes", "images"), re-creating it with its default name and icon if the user deleted it.</summary>
    Task<Category> GetOrCreateBuiltInCategoryAsync(string builtInKey, CancellationToken ct = default);

    /// <summary>Creates a category and its vault folder. Name must be unique among its siblings.</summary>
    Task<Category> CreateCategoryAsync(string name, long? parentId, byte[]? iconPng = null, CancellationToken ct = default);

    /// <summary>Renames the category and its vault folder.</summary>
    Task RenameCategoryAsync(long categoryId, string newName, CancellationToken ct = default);

    /// <summary>Sets a custom icon. Null resets to the default (the UI regenerates it from <see cref="Category.IconKey"/>).</summary>
    Task SetCategoryIconAsync(long categoryId, byte[]? iconPng, CancellationToken ct = default);

    Task SetCategoryPinnedAsync(long categoryId, bool pinned, CancellationToken ct = default);

    /// <summary>
    /// Deletes a category and all its subcategories.
    /// MoveToDeleted: files whose only memberships are inside the deleted subtree move to "Deleted"; files that also belong elsewhere
    /// just lose these memberships (a copy-mode file whose home is removed moves physically to its next remaining category).
    /// RecycleFiles: those files are deleted using <paramref name="options"/> or the item-delete settings.
    /// </summary>
    Task DeleteCategoryAsync(long categoryId, CategoryDeletePolicy policy, DeleteItemsOptions? options = null, CancellationToken ct = default);

    /// <summary>Marks the category and all its descendants as viewed now (clears unread badges).</summary>
    Task MarkCategoryViewedAsync(long categoryId, CancellationToken ct = default);

    /// <summary>
    /// Deletes everything in "Deleted" (vault files to the Recycle Bin, pointer items only removed from the library).
    /// Only <see cref="DeleteItemsOptions.PermanentWhenNoRecycleBin"/> is read from <paramref name="options"/>.
    /// </summary>
    Task EmptyDeletedAsync(DeleteItemsOptions? options = null, CancellationToken ct = default);

    // ---------------------------------------------------------------- items

    /// <summary>
    /// Items of the category and all its descendants, oldest first (chat order).
    /// <paramref name="searchText"/> matches file names and note text; "#tag" tokens require all those tags.
    /// </summary>
    Task<IReadOnlyList<LibraryItem>> GetThreadItemsAsync(long categoryId, string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default);

    Task<LibraryItem?> GetItemAsync(long itemId, CancellationToken ct = default);

    /// <summary>Main-window search: category names plus file names across the whole library ("#tag" tokens supported). Items newest first.</summary>
    Task<SearchResults> SearchAsync(string searchText, ItemFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Files across the whole library whose name starts with the free text of <paramref name="searchText"/> (its words joined
    /// by single spaces, case-insensitive; "#tag" tokens and <paramref name="filter"/> narrow as in <see cref="SearchAsync"/>):
    /// those named exactly that, with or without their extension, first, then newest first; at most <paramref name="limit"/>.
    /// Every file returned also matches <see cref="SearchAsync"/>. That search returns only the newest matches when there are
    /// many (<see cref="SearchResults.HasMoreItems"/>); the quick switcher adds these so an older file named like the text is
    /// never crowded out by newer files that merely contain it. Empty when there is no free text.
    /// </summary>
    Task<IReadOnlyList<LibraryItem>> SearchNamePrefixAsync(string searchText, int limit, ItemFilter? filter = null, CancellationToken ct = default);

    /// <summary>Adds files and folders. Folders become categories (see settings). Duplicates (same hash) are not stored twice.</summary>
    Task<AddResult> AddPathsAsync(IReadOnlyList<string> paths, AddRequest request, IProgress<AddProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Creates "Note yyyy-MM-dd HH-mm-ss.md" in the category's vault folder. Notes are never de-duplicated.</summary>
    Task<LibraryItem> CreateNoteAsync(long categoryId, string text, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default);

    /// <summary>
    /// Pasted text. A single http(s) URL becomes "Link yyyy-MM-dd HH-mm-ss.url" (de-duplicated by content);
    /// anything else becomes "Pasted text yyyy-MM-dd HH-mm-ss.md" (never de-duplicated).
    /// </summary>
    Task<AddResult> AddPastedTextAsync(long categoryId, string text, CancellationToken ct = default);

    /// <summary>Pasted image saved as "Pasted image yyyy-MM-dd HH-mm-ss.png" (de-duplicated by content).</summary>
    Task<AddResult> AddPastedImageAsync(long categoryId, byte[] png, CancellationToken ct = default);

    /// <summary>Renames the real file (the vault file, or the pointer's file on disk). Keeps the name unique within the folder.</summary>
    Task RenameItemAsync(long itemId, string newFileName, CancellationToken ct = default);

    /// <summary>Deletes items everywhere (all memberships), following <paramref name="options"/> or the settings.</summary>
    Task DeleteItemsAsync(IReadOnlyList<long> itemIds, DeleteItemsOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Removes the items' memberships in <paramref name="categoryId"/> or any of its descendants.
    /// Items left with no category move to "Deleted". A copy-mode file whose home is removed moves physically to its next category.
    /// </summary>
    Task RemoveFromCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, CancellationToken ct = default);

    /// <summary>Adds existing items to another category (a link; the file is not copied again).</summary>
    Task AddToCategoryAsync(IReadOnlyList<long> itemIds, long categoryId, AddedBy addedBy = AddedBy.AppUi, CancellationToken ct = default);

    /// <summary>
    /// "Move to category…" (SPEC.md section 6): the items leave <paramref name="fromCategoryId"/> and all its descendants
    /// (the thread they were picked in) and join <paramref name="toCategoryId"/>; their other categories stay. A copy-mode
    /// file whose home is one of the categories it leaves moves physically into the target's vault folder (with a unique
    /// name, "name (1).ext") and the target becomes its home; a pointer item only changes category. Moving out of "Deleted"
    /// works the same way. Nothing happens when <paramref name="toCategoryId"/> is <paramref name="fromCategoryId"/>.
    /// All or nothing: a vault file that is missing or in use fails the whole call before anything changes.
    /// The new memberships are recorded as added by <paramref name="addedBy"/> (the user's own moves leave nothing unread).
    /// </summary>
    Task MoveToCategoryAsync(IReadOnlyList<long> itemIds, long fromCategoryId, long toCategoryId, AddedBy addedBy = AddedBy.AppUi,
        CancellationToken ct = default);

    /// <summary>Checks whether the item's file exists, updates <see cref="LibraryItem.IsMissing"/>, and returns true if it exists.</summary>
    Task<bool> VerifyExistsAsync(long itemId, CancellationToken ct = default);

    /// <summary>
    /// Re-links a missing pointer item to <paramref name="newPath"/>. If the content hash differs and
    /// <paramref name="acceptHashMismatch"/> is false, nothing changes and <see cref="RelocateOutcome.HashMismatch"/> is returned.
    /// </summary>
    Task<RelocateOutcome> RelocateItemAsync(long itemId, string newPath, bool acceptHashMismatch, CancellationToken ct = default);

    /// <summary>Full text of a note item.</summary>
    Task<string> ReadNoteTextAsync(long itemId, CancellationToken ct = default);

    // ---------------------------------------------------------------- starred files (SPEC.md section 4)

    /// <summary>
    /// Stars or unstars items. Starring an item that already has a star keeps its original time; ids that don't exist are
    /// skipped. Stars stay through moves, renames and category changes (an item in "Deleted" keeps its star); deleting the
    /// item removes it. Raises <see cref="LibraryChangeKind.Items"/> | <see cref="LibraryChangeKind.Starred"/> with the ids
    /// whose star changed, and nothing when none did.
    /// </summary>
    Task SetStarredAsync(IReadOnlyList<long> itemIds, bool starred, CancellationToken ct = default);

    /// <summary>
    /// Every starred item from every category (the Starred view), in the order they were starred: oldest first, the newest at
    /// the bottom like a chat. <paramref name="searchText"/> and <paramref name="filter"/> work as in <see cref="GetThreadItemsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<LibraryItem>> GetStarredItemsAsync(string? searchText = null, ItemFilter? filter = null, CancellationToken ct = default);

    /// <summary>The chat list's "Starred" row: how many items are starred and when the latest star was given.</summary>
    Task<StarredSummary> GetStarredSummaryAsync(CancellationToken ct = default);

    // ---------------------------------------------------------------- vault operations (stage 2)

    /// <summary>The vault monitor for this library (created once; later calls return the same instance).</summary>
    IVaultMonitor CreateVaultMonitor();

    /// <summary>
    /// Copies the whole vault to <paramref name="newVaultPath"/> (must be empty or not exist, and not inside the current vault),
    /// verifies every file by size and hash, then switches the library to it. The monitor is paused meanwhile and resumes on
    /// the new location. The old vault is left untouched; the caller may offer to recycle it.
    /// </summary>
    Task<MoveVaultResult> MoveVaultAsync(string newVaultPath, IProgress<VaultOperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Copies every pointer item's file into its home category's vault folder and turns it into a copy-mode item. Originals stay.</summary>
    Task<MigrateResult> MigratePointersAsync(IProgress<VaultOperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Writes a portable backup: a consistent database snapshot, the whole vault and a manifest, in one .zip.</summary>
    Task<BackupResult> CreateBackupAsync(string zipPath, IProgress<VaultOperationProgress>? progress = null, CancellationToken ct = default);

    // ---------------------------------------------------------------- tags

    /// <summary>All tags with usage counts, A–Z.</summary>
    Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>Replaces the item's tags. Tag names are trimmed, a leading '#' is removed, case-insensitive.</summary>
    Task SetItemTagsAsync(long itemId, IReadOnlyCollection<string> tags, CancellationToken ct = default);

    Task AddTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default);

    Task RemoveTagsAsync(IReadOnlyList<long> itemIds, IReadOnlyCollection<string> tags, CancellationToken ct = default);

    // ---------------------------------------------------------------- AI activity log (stage 4, SPEC.md section 10)

    /// <summary>
    /// Stores one entry (its <see cref="ActivityEntry.Id"/> is ignored) and returns it with its id. Entries are kept for
    /// <see cref="ActivityRetention"/>: older ones are removed when the library opens and at most once a day after that.
    /// Raises no <see cref="Changed"/> event (the app tells its own viewer).
    /// </summary>
    Task<ActivityEntry> AddActivityAsync(ActivityEntry entry, CancellationToken ct = default);

    /// <summary>Activity entries matching <paramref name="query"/>, newest first.</summary>
    Task<ActivityPage> GetActivityAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>How long activity entries are kept: one year (SPEC.md section 10).</summary>
    static TimeSpan ActivityRetention => TimeSpan.FromDays(365);
}
