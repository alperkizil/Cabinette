using System.Text.Json;
using System.Text.Json.Serialization;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

// Contracts between the pieces of stage 4 (SPEC.md section 10) inside the app:
// - the "ai" command handler (Services/Ai/AiCommand*) executes tool calls, logs them and reports changes;
// - the content reader (Services/Ai/FileContent*) reads files for read_file;
// - the AI UI (Settings › AI and the grouped toasts) shows the log and the changes.

/// <summary>What one AI change did, for the grouped toast ("Claude Code added 3 files to Images").</summary>
public enum AiChangeKind
{
    FilesAdded,
    NoteCreated,
    FilesTagged,
    FilesUntagged,
    FilesAddedToCategory,
    FilesRemovedFromCategory,
    FilesMoved,
    FileRenamed,
    FilesDeleted,
    FilesStarred,
    FilesUnstarred,
    CategoryCreated,
    CategoryRenamed,
    CategoryDeleted,
    VaultMoved,
    PointersMigrated,
    BackupCreated,

    /// <summary>A vault operation an AI started failed or was cancelled (<see cref="AiChange.Detail"/> says why).</summary>
    OperationFailed,
}

/// <summary>One change made by an AI client, reported after it succeeded.</summary>
/// <param name="Client">The client's display name ("Claude Code").</param>
/// <param name="Count">Files (or categories) affected.</param>
public sealed record AiChange(string Client, AiChangeKind Kind, int Count = 1)
{
    /// <summary>
    /// The category the files went to (added, note, added to, moved to), or the category created, renamed or deleted. For
    /// files added to several categories (auto-sorted), null: see <see cref="AddedByCategory"/>.
    /// </summary>
    public long? CategoryId { get; init; }

    /// <summary>That category's name (its new name after a rename).</summary>
    public string? CategoryName { get; init; }

    /// <summary><see cref="AiChangeKind.FilesAdded"/>: files added per category name, when they went to several.</summary>
    public IReadOnlyDictionary<string, int>? AddedByCategory { get; init; }

    /// <summary><see cref="AiChangeKind.FilesAdded"/>: files whose content was already in Cabinette.</summary>
    public int Duplicates { get; init; }

    /// <summary><see cref="AiChangeKind.FilesAdded"/>: files that couldn't be added.</summary>
    public int Failures { get; init; }

    /// <summary>The new name of a renamed file or category, or why an operation failed.</summary>
    public string? Detail { get; init; }
}

/// <summary>Takes AI changes for the grouped toasts. Call from any thread; never throws.</summary>
public interface IAiChangeReporter
{
    void Report(AiChange change);
}

/// <summary>Until the AI UI registers its notifier: changes are dropped.</summary>
public sealed class NullAiChangeReporter : IAiChangeReporter
{
    public void Report(AiChange change)
    {
    }
}

/// <summary>A file or category named by an activity entry, with its name at the time.</summary>
public sealed record AiActivityRef(long Id, string Name);

/// <summary>
/// The <c>details_json</c> of an AI activity entry: written by the "ai" command handler, shown by Settings › AI.
/// Only what applies to the tool is set.
/// </summary>
public sealed record AiActivityDetails
{
    /// <summary>The most files or categories listed by name; <see cref="FileCount"/> has the full count.</summary>
    public const int MaxRefs = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool Ok { get; init; } = true;

    /// <summary>One of <see cref="AiErrorCodes"/> when the call failed.</summary>
    public string? ErrorCode { get; init; }

    public string? Error { get; init; }

    /// <summary>The files the call changed or read (at most <see cref="MaxRefs"/>).</summary>
    public IReadOnlyList<AiActivityRef>? Files { get; init; }

    /// <summary>How many files the call changed, read or returned in all.</summary>
    public int? FileCount { get; init; }

    /// <summary>The categories involved (target, source, created, renamed, deleted).</summary>
    public IReadOnlyList<AiActivityRef>? Categories { get; init; }

    /// <summary>Search or list text.</summary>
    public string? Query { get; init; }

    /// <summary>Paths given by the AI (files to add, the new vault, the backup .zip).</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>The new name of a renamed file or category, or the name of a created one.</summary>
    public string? NewName { get; init; }

    /// <summary>"copy" or "pointer" for adds.</summary>
    public string? Mode { get; init; }

    /// <summary>read_file: where the page started.</summary>
    public long? Offset { get; init; }

    /// <summary>Vault operations: the operation id.</summary>
    public string? OperationId { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null when the text is missing or not details JSON (an entry written by another version).</summary>
    public static AiActivityDetails? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiActivityDetails>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The AI activity log as the app writes and reads it (stored in the library, kept for a year).</summary>
public interface IAiActivityLog
{
    /// <summary>Raised on a background thread after an entry was stored (Settings › AI refreshes).</summary>
    event EventHandler<ActivityEntry>? EntryAdded;

    /// <summary>Stores one entry for one tool call. Never throws: a failure is written to Cabinette's log and null returned.</summary>
    Task<ActivityEntry?> LogAsync(string client, string tool, AiActivityDetails details);

    Task<ActivityPage> GetAsync(ActivityQuery query, CancellationToken ct = default);
}

/// <summary>A file the AI may not read as text or image (a binary file, a video, an archive). The message is for the AI.</summary>
public sealed class FileContentRefusedException(string message) : Exception(message);

/// <summary>Reads library files for the read_file tool (SPEC.md section 10).</summary>
public interface IFileContentReader
{
    /// <summary>
    /// One page of the item's contents. Text-like files: up to <see cref="AiProtocol.MaxTextPageBytes"/> bytes from
    /// <paramref name="offset"/> (in bytes), decoded. PDF and Office files: the text of Windows' IFilter, up to
    /// <see cref="AiProtocol.MaxTextPageChars"/> characters from <paramref name="offset"/> (in characters). Images:
    /// scaled down to <see cref="AiProtocol.MaxImageEdgePixels"/> (offset ignored). <see cref="ReadFileResult.File"/>
    /// is left null (the caller fills it). Never on the UI thread.
    /// </summary>
    /// <exception cref="FileContentRefusedException">The file is not text, a document with an installed IFilter, or an image.</exception>
    /// <exception cref="System.IO.IOException">The file can't be read (missing, locked).</exception>
    Task<ReadFileResult> ReadAsync(LibraryItem item, long offset, CancellationToken ct = default);
}
