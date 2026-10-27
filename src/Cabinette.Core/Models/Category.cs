namespace Cabinette.Core.Models;

/// <summary>A category. Categories are the "threads" of the app and can be nested.</summary>
public sealed record Category
{
    public required long Id { get; init; }

    /// <summary>Null for top-level categories.</summary>
    public long? ParentId { get; init; }

    public required string Name { get; init; }

    /// <summary>Sanitized folder name inside the vault (relative to the parent's folder).</summary>
    public required string FolderName { get; init; }

    public CategoryKind Kind { get; init; }

    /// <summary>Stable key of a built-in category (e.g. "audio", "code.python", "deleted"); survives renames. Null for user categories.</summary>
    public string? BuiltInKey { get; init; }

    /// <summary>Which default icon to generate when <see cref="IconPng"/> is null (e.g. "audio", "folder").</summary>
    public string? IconKey { get; init; }

    /// <summary>The category icon as PNG bytes. Null until generated or set by the user.</summary>
    public byte[]? IconPng { get; init; }

    public bool IsPinned { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When a file was last added to this category or any of its descendants. In a <see cref="CategorySummary"/> (the chat
    /// list): when the newest file still there arrived, so it falls back when that file leaves, and is null when none is left.
    /// </summary>
    public DateTimeOffset? LastUploadAt { get; init; }

    public DateTimeOffset? LastViewedAt { get; init; }
}

/// <summary>A row in the chat list.</summary>
public sealed record CategorySummary
{
    public required Category Category { get; init; }

    /// <summary>Distinct items in this category and all its descendants.</summary>
    public int ItemCount { get; init; }

    /// <summary>Items added by Explorer, the vault monitor or the AI since the thread was last opened (subtree, distinct).</summary>
    public int UnreadCount { get; init; }

    /// <summary>Number of direct subcategories.</summary>
    public int SubcategoryCount { get; init; }

    /// <summary>File name of the most recently added item in the subtree (chat-list preview line).</summary>
    public string? LastItemName { get; init; }

    public ItemKind? LastItemKind { get; init; }

    /// <summary>Sum of the known sizes of the distinct items in this category and all its descendants (the thread banner).</summary>
    public long TotalSizeBytes { get; init; }
}

public sealed record TagInfo(string Name, int ItemCount);

/// <summary>The "Starred" row of the chat list (SPEC.md section 4): shown while <see cref="Count"/> is above 0.</summary>
/// <param name="Count">Starred items (each once, wherever it is, Deleted included).</param>
/// <param name="LastStarredAt">When the most recent star was given, or null when nothing is starred.</param>
public sealed record StarredSummary(int Count, DateTimeOffset? LastStarredAt);
