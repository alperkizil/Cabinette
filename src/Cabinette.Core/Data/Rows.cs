using Cabinette.Core.Models;

namespace Cabinette.Core.Data;

// Dapper row types. Columns are aliased to these property names in every query.

internal sealed class CategoryRow
{
    public const string SelectSql = """
        SELECT id AS Id, parent_id AS ParentId, name AS Name, folder_name AS FolderName, kind AS Kind,
               builtin_key AS BuiltInKey, icon_key AS IconKey, is_pinned AS IsPinned, created_at AS CreatedAt,
               last_upload_at AS LastUploadAt, last_viewed_at AS LastViewedAt
        FROM categories
        """;

    public long Id { get; set; }

    public long? ParentId { get; set; }

    public string Name { get; set; } = "";

    public string FolderName { get; set; } = "";

    public long Kind { get; set; }

    public string? BuiltInKey { get; set; }

    public string? IconKey { get; set; }

    public long IsPinned { get; set; }

    public string CreatedAt { get; set; } = "";

    public string? LastUploadAt { get; set; }

    public string? LastViewedAt { get; set; }

    public CategoryKind CategoryKind => (CategoryKind)Kind;

    public Category ToCategory(byte[]? iconPng) => new()
    {
        Id = Id,
        ParentId = ParentId,
        Name = Name,
        FolderName = FolderName,
        Kind = (CategoryKind)Kind,
        BuiltInKey = BuiltInKey,
        IconKey = IconKey,
        IconPng = iconPng,
        IsPinned = IsPinned != 0,
        CreatedAt = DbValues.ParseTime(CreatedAt),
        LastUploadAt = DbValues.ParseTimeOrNull(LastUploadAt),
        LastViewedAt = DbValues.ParseTimeOrNull(LastViewedAt),
    };
}

internal sealed class ItemRow
{
    public const string SelectSql = """
        SELECT i.id AS Id, i.mode AS Mode, i.kind AS Kind, i.file_name AS FileName, i.extension AS Extension,
               i.pointer_path AS PointerPath, i.home_category_id AS HomeCategoryId, i.size_bytes AS SizeBytes,
               i.hash AS Hash, i.file_modified_at AS FileModifiedAt, i.added_at AS AddedAt, i.added_by AS AddedBy,
               i.batch_id AS BatchId, i.original_path AS OriginalPath, i.is_missing AS IsMissing,
               i.note_preview AS NotePreview
        FROM items i
        """;

    public long Id { get; set; }

    public long Mode { get; set; }

    public long Kind { get; set; }

    public string FileName { get; set; } = "";

    public string Extension { get; set; } = "";

    public string? PointerPath { get; set; }

    public long HomeCategoryId { get; set; }

    public long? SizeBytes { get; set; }

    public byte[]? Hash { get; set; }

    public string? FileModifiedAt { get; set; }

    public string AddedAt { get; set; } = "";

    public long AddedBy { get; set; }

    public string? BatchId { get; set; }

    public string? OriginalPath { get; set; }

    public long IsMissing { get; set; }

    public string? NotePreview { get; set; }

    public ItemMode ItemMode => (ItemMode)Mode;

    public ItemKind ItemKind => (ItemKind)Kind;
}

internal sealed class MembershipRow
{
    public long ItemId { get; set; }

    public long CategoryId { get; set; }

    public string AddedAt { get; set; } = "";

    public long AddedBy { get; set; }
}

internal sealed class ItemTagRow
{
    public long ItemId { get; set; }

    public string Name { get; set; } = "";
}

internal sealed class IconRow
{
    public long Id { get; set; }

    public byte[]? IconPng { get; set; }
}

internal sealed class CategoryStatsRow
{
    public long RowId { get; set; }

    public long ItemCount { get; set; }

    public long UnreadCount { get; set; }

    public string? LastName { get; set; }

    public long? LastKind { get; set; }

    /// <summary>When the row's newest remaining file arrived in its subtree (NULL when the subtree has no files).</summary>
    public string? LastAt { get; set; }

    /// <summary>Sum of the known sizes of the row's distinct items (0 when none is known).</summary>
    public long TotalSize { get; set; }
}

internal sealed class TagCountRow
{
    public string Name { get; set; } = "";

    public long ItemCount { get; set; }
}

internal sealed class ActivityRow
{
    public const string SelectSql = """
        SELECT id AS Id, at AS At, source AS Source, action AS Action, details_json AS DetailsJson
        FROM activity_log
        """;

    public long Id { get; set; }

    public string At { get; set; } = "";

    public string Source { get; set; } = "";

    public string Action { get; set; } = "";

    public string? DetailsJson { get; set; }

    public ActivityEntry ToEntry() => new()
    {
        Id = Id,
        At = DbValues.ParseTime(At),
        Source = Source,
        Action = Action,
        DetailsJson = DetailsJson,
    };
}
