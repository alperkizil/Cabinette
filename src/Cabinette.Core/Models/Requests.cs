namespace Cabinette.Core.Models;

/// <summary>Filters for thread and global search. All set conditions must match.</summary>
public sealed record ItemFilter
{
    public static ItemFilter None { get; } = new();

    /// <summary>Top-level type keys ("images", "audio", ...). Item matches if its TypeKey is in the list.</summary>
    public IReadOnlyList<string>? TypeKeys { get; init; }

    public DateTimeOffset? AddedFrom { get; init; }

    public DateTimeOffset? AddedTo { get; init; }

    public long? MinSizeBytes { get; init; }

    public long? MaxSizeBytes { get; init; }

    public ItemMode? Mode { get; init; }

    public bool MissingOnly { get; init; }

    /// <summary>Lower-case extensions without dots.</summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    public bool IsEmpty =>
        (TypeKeys is null || TypeKeys.Count == 0) && AddedFrom is null && AddedTo is null &&
        MinSizeBytes is null && MaxSizeBytes is null && Mode is null && !MissingOnly &&
        (Extensions is null || Extensions.Count == 0);
}

public sealed record SearchResults(IReadOnlyList<CategorySummary> Categories, IReadOnlyList<LibraryItem> Items)
{
    /// <summary>True when more items matched than <see cref="Items"/> holds (the main-window search returns at most a fixed number).</summary>
    public bool HasMoreItems { get; init; }
}

public sealed record AddRequest
{
    /// <summary>Target category. Null = auto-categorize each file by extension.</summary>
    public long? TargetCategoryId { get; init; }

    /// <summary>Null = use the "default add mode" setting.</summary>
    public ItemMode? Mode { get; init; }

    public AddedBy AddedBy { get; init; } = AddedBy.AppUi;

    /// <summary>Null = use the setting.</summary>
    public bool? SubfoldersAsSubcategories { get; init; }

    /// <summary>Null = use the setting.</summary>
    public bool? AlsoAddToTypeCategories { get; init; }

    /// <summary>
    /// Only with <see cref="TargetCategoryId"/>: when the target is a built-in type category (even a renamed one; not a user
    /// category, not Deleted), a file of a known type that belongs to another type category goes to that category instead
    /// (an image dropped on Spreadsheets goes to Images). Files of unknown types and files inside added folders stay. Those
    /// files are listed in <see cref="AddResult.SortedByType"/>. False (the default) keeps every file in the target: the UI
    /// sets it from <see cref="AppSettings.SortFilesByTypeInTypeCategories"/> for drops and the paperclip only.
    /// </summary>
    public bool SortIntoTypeCategories { get; init; }
}

public sealed record AddProgress(int ProcessedFiles, int TotalFiles, string? CurrentFile, long BytesProcessed, long TotalBytes);

public sealed record AddResult
{
    public IReadOnlyList<LibraryItem> Added { get; init; } = [];

    public IReadOnlyList<DuplicateInfo> Duplicates { get; init; } = [];

    public IReadOnlyList<AddFailure> Failures { get; init; } = [];

    /// <summary>Categories created by the operation (e.g. when a folder was added).</summary>
    public IReadOnlyList<long> CreatedCategoryIds { get; init; } = [];

    /// <summary>
    /// Files that <see cref="AddRequest.SortIntoTypeCategories"/> sent to their own type category instead of the target, in
    /// the order they were added. Each also appears in <see cref="Added"/> or <see cref="Duplicates"/>; failures aren't listed.
    /// </summary>
    public IReadOnlyList<SortedFile> SortedByType { get; init; } = [];
}

/// <summary>A file that went to its own type category instead of the requested one (see <see cref="AddRequest.SortIntoTypeCategories"/>).</summary>
public sealed record SortedFile(string SourcePath, long CategoryId);

/// <summary>A file whose content was already in the library. No second copy was stored.</summary>
/// <param name="AddedToTargetCategory">True when the existing item was added to the requested category because it was not in it yet.</param>
public sealed record DuplicateInfo(string SourcePath, LibraryItem Existing, bool AddedToTargetCategory);

public sealed record AddFailure(string SourcePath, string Message);

public sealed record DeleteItemsOptions
{
    /// <summary>True = Recycle Bin, false = permanent. Null = use the setting.</summary>
    public bool? RecycleVaultFiles { get; init; }

    /// <summary>True = also delete a pointer's real file (to the Recycle Bin). Null = use the setting.</summary>
    public bool? DeleteRealPointerFiles { get; init; }

    /// <summary>
    /// False (default): a file that should go to the Recycle Bin but can't (its drive has none, or it is too large for it) makes
    /// the whole call fail with <see cref="Services.LibraryErrorCode.RecycleBinUnavailable"/> before anything changes. When the
    /// Recycle Bin refuses a file only after the delete went through, the call fails with that code too and the file stays on disk.
    /// True: such files are deleted permanently instead (set only after the user confirmed). Repeating a call that failed after
    /// the delete went through deletes exactly the files the Recycle Bin refused.
    /// </summary>
    public bool PermanentWhenNoRecycleBin { get; init; }
}
