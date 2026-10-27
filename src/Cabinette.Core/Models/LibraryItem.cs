namespace Cabinette.Core.Models;

/// <summary>A file in the library (a "message" in a thread).</summary>
public sealed record LibraryItem
{
    public required long Id { get; init; }

    public ItemMode Mode { get; init; }

    public ItemKind Kind { get; init; }

    /// <summary>File name including extension, e.g. "report.pdf".</summary>
    public required string FileName { get; init; }

    /// <summary>Lower-case extension without the dot ("pdf"), or "" when there is none.</summary>
    public string Extension { get; init; } = "";

    /// <summary>Absolute path on disk. For copy mode this is resolved from the vault and the home category's folder.</summary>
    public required string FullPath { get; init; }

    /// <summary>Copy mode: the category whose vault folder physically holds the file. Pointer mode: the first category it was added to.</summary>
    public long HomeCategoryId { get; init; }

    /// <summary>Every category the item belongs to (always includes <see cref="HomeCategoryId"/>).</summary>
    public IReadOnlyList<long> CategoryIds { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public long? SizeBytes { get; init; }

    /// <summary>XXH128 of the content (16 bytes). Null while a large pointer file is still being hashed.</summary>
    public byte[]? Hash { get; init; }

    public DateTimeOffset? FileModifiedAt { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public AddedBy AddedBy { get; init; }

    /// <summary>Items added in the same operation share a batch id (used for album bubbles).</summary>
    public string? BatchId { get; init; }

    /// <summary>Copy mode: where the file was copied from.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Pointer mode: the real file could not be found the last time it was checked.</summary>
    public bool IsMissing { get; init; }

    /// <summary>For notes: the first ~300 characters of the text. Null for other kinds.</summary>
    public string? NotePreview { get; init; }

    /// <summary>Top-level type key from <see cref="Categorization.ExtensionMap"/> ("images", "audio", "code", "other", ...).</summary>
    public string TypeKey { get; init; } = Categorization.ExtensionMap.OtherKey;

    /// <summary>When the item was starred (SPEC.md section 4, "Starred files"), or null when it isn't starred.</summary>
    public DateTimeOffset? StarredAt { get; init; }

    public bool IsStarred => StarredAt is not null;

    /// <summary>
    /// When the photo or video was taken, from the file's own metadata (SPEC.md section 4, "Gallery"). Null when the file has
    /// none, isn't a photo or video, or hasn't been read yet; the Gallery then uses <see cref="FileModifiedAt"/>.
    /// </summary>
    public DateTimeOffset? TakenAt { get; init; }
}
