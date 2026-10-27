using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cabinette.Ipc;

// The "ai" command (stage 4, SPEC.md section 10): cabinette-mcp.exe turns each MCP tool call into one InstanceMessage
// (command "ai", tool = one of AiTools, args = the tool's *Args record) and the running Cabinette answers with the
// tool's *Result record in InstanceReply.Result, or an error with one of AiErrorCodes. Both ends serialize these records
// with AiProtocol.JsonOptions (camelCase). Ids are the library's item and category ids. "Files" are library items.

/// <summary>Version and JSON settings of the "ai" command.</summary>
public static class AiProtocol
{
    /// <summary>Raised when a record below changes incompatibly. Cabinette refuses calls from another version.</summary>
    public const int Version = 1;

    /// <summary>The most file ids one call may name.</summary>
    public const int MaxIdsPerCall = 1000;

    /// <summary>The most paths one <see cref="AiTools.AddFiles"/> call may name.</summary>
    public const int MaxPathsPerCall = 1000;

    /// <summary>Text files are read in pages of at most this many bytes (SPEC.md section 10: 256 KB per call).</summary>
    public const int MaxTextPageBytes = 256 * 1024;

    /// <summary>Extracted text (PDF, Office) is returned in pages of at most this many characters.</summary>
    public const int MaxTextPageChars = 256 * 1024;

    /// <summary>Images are scaled down so that their longer side is at most this many pixels.</summary>
    public const int MaxImageEdgePixels = 1568;

    /// <summary>Default and largest page size of <see cref="AiTools.Search"/> and <see cref="AiTools.ListFiles"/>.</summary>
    public const int DefaultListLimit = 100;

    public const int MaxListLimit = 500;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    /// <summary>Reads a record from an "args" or "result" element. Null when the element is missing or doesn't fit.</summary>
    public static T? FromElement<T>(JsonElement? element)
        where T : class
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        try
        {
            return value.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The tool names (the MCP tool names are the same).</summary>
public static class AiTools
{
    // Reading (logged, no toast)
    public const string Search = "search";
    public const string ListCategories = "list_categories";
    public const string ListFiles = "list_files";
    public const string GetFiles = "get_files";
    public const string ReadFile = "read_file";
    public const string ListTags = "list_tags";
    public const string GetOperationStatus = "get_operation_status";

    // Changes (logged, grouped toast)
    public const string AddFiles = "add_files";
    public const string CreateNote = "create_note";
    public const string TagFiles = "tag_files";
    public const string UntagFiles = "untag_files";
    public const string AddToCategory = "add_to_category";
    public const string RemoveFromCategory = "remove_from_category";
    public const string MoveFiles = "move_files";
    public const string RenameFile = "rename_file";
    public const string DeleteFiles = "delete_files";
    public const string StarFiles = "star_files";
    public const string UnstarFiles = "unstar_files";
    public const string CreateCategory = "create_category";
    public const string RenameCategory = "rename_category";
    public const string DeleteCategory = "delete_category";

    // Long vault operations: they start and reply at once; get_operation_status follows them.
    public const string MoveVault = "move_vault";
    public const string MigratePointers = "migrate_pointers";
    public const string CreateBackup = "create_backup";

    // The window (shows on the desktop; logged, no toast)
    public const string ShowInCabinette = "show_in_cabinette";

    public static IReadOnlyList<string> Reading { get; } =
        [Search, ListCategories, ListFiles, GetFiles, ReadFile, ListTags, GetOperationStatus];

    public static IReadOnlyList<string> Changes { get; } =
    [
        AddFiles, CreateNote, TagFiles, UntagFiles, AddToCategory, RemoveFromCategory, MoveFiles, RenameFile, DeleteFiles,
        StarFiles, UnstarFiles, CreateCategory, RenameCategory, DeleteCategory, MoveVault, MigratePointers, CreateBackup,
    ];

    public static IReadOnlyList<string> All { get; } = [.. Reading, .. Changes, ShowInCabinette];

    /// <summary>True for tools that change the library or the vault (the activity log's "changes only" filter).</summary>
    public static bool IsChange(string tool) => Changes.Contains(tool, StringComparer.Ordinal);
}

/// <summary>The <see cref="InstanceReply.Code"/> of a failed "ai" call.</summary>
public static class AiErrorCodes
{
    /// <summary>
    /// Cabinette is starting, waiting for first-run setup (no vault yet), or quitting. cabinette-mcp.exe retries for a
    /// while before it passes the message on.
    /// </summary>
    public const string NotReady = "not_ready";

    /// <summary>A long vault operation (move vault, migrate, backup, restore) is running, started by the user or an AI.</summary>
    public const string Busy = "busy";

    /// <summary>An id names no file or category (the message says which).</summary>
    public const string NotFound = "not_found";

    /// <summary>Missing or malformed arguments, too many ids, a relative path, an unknown mode or type.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>
    /// The call is valid but not allowed: reading a binary file, deleting the Deleted category, moving into Deleted, a
    /// name clash, an invalid name, a file in use. The message explains it.
    /// </summary>
    public const string Refused = "refused";

    /// <summary>A file can't go to the Recycle Bin (network drive, USB stick, too large). Nothing was deleted (SPEC.md section 10).</summary>
    public const string RecycleBinUnavailable = "recycle_bin_unavailable";

    /// <summary>The tool name is unknown, or the sender is another <see cref="AiProtocol.Version"/>.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Anything else that went wrong (logged in Cabinette's log).</summary>
    public const string Failed = "failed";
}

// ======================================================================== shared shapes

/// <summary>A file in the library.</summary>
public sealed record AiFile
{
    public long Id { get; init; }

    /// <summary>File name with extension.</summary>
    public string Name { get; init; } = "";

    /// <summary>Full path on disk (the vault file, or the linked file of a pointer item).</summary>
    public string Path { get; init; } = "";

    /// <summary>"copy" (stored in the vault) or "pointer" (linked where it is).</summary>
    public string Mode { get; init; } = InstanceProtocol.CopyMode;

    /// <summary>"file", "note", "pastedImage" or "link".</summary>
    public string Kind { get; init; } = "file";

    /// <summary>Type key ("images", "documents", "code", "other", ...), as in Cabinette's type filter.</summary>
    public string Type { get; init; } = "";

    public long? SizeBytes { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>"app", "explorer", "vaultMonitor", "ai", "paste" or "downloads".</summary>
    public string AddedBy { get; init; } = "";

    /// <summary>The category whose vault folder holds a copy-mode file (for a pointer, the first category it was added to).</summary>
    public long HomeCategoryId { get; init; }

    public IReadOnlyList<long> CategoryIds { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>A pointer item whose file couldn't be found the last time it was checked.</summary>
    public bool IsMissing { get; init; }

    public DateTimeOffset? StarredAt { get; init; }

    // Details (filled by get_files only)

    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Copy mode: where the file was copied from.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>XXH128 of the content, lower-case hex. Null while a large pointer file is still being hashed.</summary>
    public string? Hash { get; init; }

    /// <summary>Notes: the first ~300 characters.</summary>
    public string? NotePreview { get; init; }

    /// <summary>The categories' paths ("Code/Python"), in the order of <see cref="CategoryIds"/>.</summary>
    public IReadOnlyList<string>? CategoryPaths { get; init; }
}

/// <summary>A category (a "thread").</summary>
public sealed record AiCategory
{
    public long Id { get; init; }

    public long? ParentId { get; init; }

    public string Name { get; init; } = "";

    /// <summary>Names from the top level down, joined with "/" ("Code/Python").</summary>
    public string Path { get; init; } = "";

    /// <summary>"builtin", "user" or "deleted" (the system category files go to when their category is deleted).</summary>
    public string Kind { get; init; } = "";

    /// <summary>Files in this category and all its subcategories.</summary>
    public int FileCount { get; init; }

    /// <summary>Files added by Explorer, the vault monitor, Downloads or an AI since the user last opened it.</summary>
    public int UnreadCount { get; init; }

    public int SubcategoryCount { get; init; }

    public bool IsPinned { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastUploadAt { get; init; }
}

/// <summary>Narrows <see cref="AiTools.Search"/> and <see cref="AiTools.ListFiles"/>. Every condition set must match.</summary>
public sealed record AiFilter
{
    /// <summary>Type keys ("images", "audio", "documents", ...).</summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>Extensions, with or without the dot.</summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>"copy" or "pointer".</summary>
    public string? Mode { get; init; }

    public bool? MissingOnly { get; init; }

    public DateTimeOffset? AddedFrom { get; init; }

    public DateTimeOffset? AddedTo { get; init; }

    public long? MinSizeBytes { get; init; }

    public long? MaxSizeBytes { get; init; }
}

/// <summary>The result of a change to existing files: how many changed, and the files as they are now.</summary>
public sealed record AiChangeResult
{
    public int Count { get; init; }

    /// <summary>The files after the change (absent for deletes).</summary>
    public IReadOnlyList<AiFile>? Files { get; init; }
}

// ======================================================================== reading

/// <summary><see cref="AiTools.Search"/>: category names plus file names across the library, like the main search.</summary>
/// <param name="Query">Free text and "#tag" tokens (files must have all the tags). Empty with a filter lists matching files.</param>
public sealed record SearchArgs(string Query, AiFilter? Filter = null, int? Limit = null);

/// <param name="Files">Newest first.</param>
/// <param name="HasMore">More files matched than were returned.</param>
public sealed record SearchResult(IReadOnlyList<AiCategory> Categories, IReadOnlyList<AiFile> Files, bool HasMore);

/// <summary><see cref="AiTools.ListCategories"/>: every category (flat, parents before children), or one category's subtree.</summary>
public sealed record ListCategoriesArgs(long? ParentId = null);

public sealed record ListCategoriesResult(IReadOnlyList<AiCategory> Categories);

/// <summary>
/// <see cref="AiTools.ListFiles"/>: the files of one category's thread (with its subcategories), or of the Starred view
/// when <paramref name="Starred"/> is true, newest first, in pages.
/// </summary>
public sealed record ListFilesArgs(long? CategoryId = null, bool Starred = false, string? Query = null, AiFilter? Filter = null,
    int Offset = 0, int? Limit = null);

/// <param name="NextOffset">Pass as Offset for the next page; null after the last one.</param>
public sealed record ListFilesResult(IReadOnlyList<AiFile> Files, int Total, int? NextOffset);

/// <summary><see cref="AiTools.GetFiles"/>: details of the files (unknown ids are listed, not an error).</summary>
public sealed record GetFilesArgs(IReadOnlyList<long> FileIds);

public sealed record GetFilesResult(IReadOnlyList<AiFile> Files, IReadOnlyList<long> NotFound);

/// <summary>
/// <see cref="AiTools.ReadFile"/>: a file's contents. Text-like files as text in pages; PDF and Office files as text
/// through Windows' text extractors (IFilters) where installed; images scaled down. Other files are refused.
/// </summary>
/// <param name="Offset">Where to start: 0, or the previous page's <see cref="ReadFileResult.NextOffset"/>.</param>
public sealed record ReadFileArgs(long FileId, long Offset = 0);

public sealed record ReadFileResult
{
    /// <summary>"text" or "image".</summary>
    public string Kind { get; init; } = "text";

    public AiFile? File { get; init; }

    // Text

    public string? Text { get; init; }

    /// <summary>Where this page starts, in <see cref="Unit"/>.</summary>
    public long Offset { get; init; }

    /// <summary>Where the next page starts, or null when this page reaches the end.</summary>
    public long? NextOffset { get; init; }

    /// <summary>The whole length in <see cref="Unit"/>, when known.</summary>
    public long? Total { get; init; }

    /// <summary>"bytes" (a text file, read as it is on disk) or "chars" (text extracted from a PDF or Office file).</summary>
    public string? Unit { get; init; }

    /// <summary>For text files: the encoding used ("utf-8", "utf-16le", ...).</summary>
    public string? Encoding { get; init; }

    /// <summary>"file" (the file is text) or "extractor" (Windows extracted the text).</summary>
    public string? Source { get; init; }

    // Image

    /// <summary>Base64 of the scaled-down image.</summary>
    public string? ImageBase64 { get; init; }

    /// <summary>"image/png" or "image/jpeg".</summary>
    public string? MimeType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? OriginalWidth { get; init; }

    public int? OriginalHeight { get; init; }
}

/// <summary><see cref="AiTools.ListTags"/>: every tag with how many files have it (no arguments).</summary>
public sealed record ListTagsArgs;

public sealed record AiTag(string Name, int FileCount);

public sealed record ListTagsResult(IReadOnlyList<AiTag> Tags);

// ======================================================================== changes

/// <summary>
/// <see cref="AiTools.AddFiles"/>: adds files and folders (full paths). Folders become categories. Without
/// <paramref name="CategoryId"/> each file goes to its type category; with it, everything goes there.
/// </summary>
/// <param name="Mode">"copy" or "pointer"; null uses the user's default add mode.</param>
public sealed record AddFilesArgs(IReadOnlyList<string> Paths, long? CategoryId = null, string? Mode = null);

/// <param name="AddedToCategory">The existing file was added to the requested category because it wasn't in it yet.</param>
public sealed record AiDuplicate(string Path, AiFile Existing, bool AddedToCategory);

public sealed record AiFailure(string Path, string Message);

public sealed record AddFilesResult(IReadOnlyList<AiFile> Added, IReadOnlyList<AiDuplicate> Duplicates, IReadOnlyList<AiFailure> Failures,
    IReadOnlyList<long> CreatedCategoryIds);

/// <summary><see cref="AiTools.CreateNote"/>: a note (Markdown) in the category, by default in Notes.</summary>
public sealed record CreateNoteArgs(string Text, long? CategoryId = null);

public sealed record FileResult(AiFile File);

/// <summary><see cref="AiTools.TagFiles"/> and <see cref="AiTools.UntagFiles"/>.</summary>
public sealed record TagFilesArgs(IReadOnlyList<long> FileIds, IReadOnlyList<string> Tags);

/// <summary><see cref="AiTools.AddToCategory"/> and <see cref="AiTools.RemoveFromCategory"/>.</summary>
public sealed record FilesCategoryArgs(IReadOnlyList<long> FileIds, long CategoryId);

/// <summary>
/// <see cref="AiTools.MoveFiles"/>: like "Move to category…": the files leave <paramref name="FromCategoryId"/> (and its
/// subcategories) and join <paramref name="ToCategoryId"/>; their other categories stay.
/// </summary>
public sealed record MoveFilesArgs(IReadOnlyList<long> FileIds, long FromCategoryId, long ToCategoryId);

/// <summary><see cref="AiTools.RenameFile"/>: renames the real file (for a pointer, the file on disk).</summary>
public sealed record RenameFileArgs(long FileId, string NewName);

/// <summary><see cref="AiTools.DeleteFiles"/>, <see cref="AiTools.StarFiles"/> and <see cref="AiTools.UnstarFiles"/>.</summary>
public sealed record FileIdsArgs(IReadOnlyList<long> FileIds);

/// <summary><see cref="AiTools.CreateCategory"/>.</summary>
public sealed record CreateCategoryArgs(string Name, long? ParentId = null);

/// <summary><see cref="AiTools.RenameCategory"/>.</summary>
public sealed record RenameCategoryArgs(long CategoryId, string NewName);

public sealed record CategoryResult(AiCategory Category);

/// <summary><see cref="AiTools.DeleteCategory"/>: deletes the category and its subcategories.</summary>
public sealed record DeleteCategoryArgs(long CategoryId);

/// <param name="Files">What happened to their files: "moved_to_deleted" or "recycled".</param>
public sealed record DeleteCategoryResult(string Files, int Categories);

// ======================================================================== vault operations

/// <summary><see cref="AiTools.MoveVault"/>: copies the vault to an empty or new folder, checks it, then switches to it.</summary>
public sealed record MoveVaultArgs(string NewPath);

/// <summary><see cref="AiTools.CreateBackup"/>: writes a portable backup .zip to that full path.</summary>
public sealed record CreateBackupArgs(string ZipPath);

/// <summary><see cref="AiTools.MigratePointers"/> (no arguments).</summary>
public sealed record MigratePointersArgs;

/// <summary>A vault operation started; follow it with <see cref="AiTools.GetOperationStatus"/>.</summary>
/// <param name="Kind">The tool that started it.</param>
public sealed record OperationStarted(string OperationId, string Kind);

/// <summary><see cref="AiTools.GetOperationStatus"/>: the operation with that id, or the latest one an AI started.</summary>
public sealed record GetOperationStatusArgs(string? OperationId = null);

public sealed record OperationStatus
{
    public string OperationId { get; init; } = "";

    /// <summary>The tool that started it.</summary>
    public string Kind { get; init; } = "";

    /// <summary>"running", "succeeded", "failed" or "cancelled" (the user cancelled it in Cabinette's progress dialog).</summary>
    public string State { get; init; } = "running";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Short English stage ("copying", "verifying", "zipping", ...).</summary>
    public string? Stage { get; init; }

    public int? FilesDone { get; init; }

    public int? FilesTotal { get; init; }

    public long? BytesDone { get; init; }

    public long? BytesTotal { get; init; }

    /// <summary>Why it failed.</summary>
    public string? Error { get; init; }

    /// <summary>What it did, when it succeeded ("1,204 files (3.2 GB) copied to D:\Vault").</summary>
    public string? Summary { get; init; }

    /// <summary>Move vault: the old vault folder, which is kept.</summary>
    public string? OldVaultPath { get; init; }

    /// <summary>Move vault: the new vault folder. Backup: the .zip.</summary>
    public string? Path { get; init; }

    /// <summary>Migrate: files it couldn't copy, and pointer files that were missing.</summary>
    public IReadOnlyList<AiFailure>? Failures { get; init; }
}

// ======================================================================== the window

/// <summary>
/// <see cref="AiTools.ShowInCabinette"/>: opens Cabinette's window at a file (its thread, scrolled to it) or at a
/// category. One of the two must be set.
/// </summary>
public sealed record ShowArgs(long? FileId = null, long? CategoryId = null);

public sealed record ShowResult(bool Shown);
