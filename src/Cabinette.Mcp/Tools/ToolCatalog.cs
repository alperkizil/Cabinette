using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinette.Ipc;

namespace Cabinette.Mcp.Tools;

/// <summary>One MCP tool: its name (an <see cref="AiTools"/> constant), what the model reads about it, and its args record.</summary>
internal sealed class ToolDefinition
{
    public required string Name { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required ObjectSchema Input { get; init; }

    /// <summary>The <c>*Args</c> record of AiProtocol.cs that the arguments become.</summary>
    public required Type ArgsType { get; init; }

    public bool ReadOnly { get; init; }

    public bool Destructive { get; init; }

    public bool Idempotent { get; init; }

    /// <summary>Reads the arguments into the args record (JsonException when they don't fit).</summary>
    public required Func<JsonObject, object?> Deserialize { get; init; }

    /// <summary>Makes relative paths in the args full (the resolver maps one path).</summary>
    public required Func<object, Func<string, string>, object> ResolvePaths { get; init; }

    /// <summary>The "ai" message for these args, from that client.</summary>
    public required Func<string, object, InstanceMessage> CreateMessage { get; init; }
}

/// <summary>
/// The tools: one per <see cref="AiTools"/> constant, with the same names, mapped to the <c>*Args</c> records. The
/// descriptions are written for the model.
/// </summary>
internal static class ToolCatalog
{
    public const string ServerInstructions =
        "Cabinette is the user's personal file organizer on this Windows PC, laid out like a chat app: each category is a " +
        "thread of files, categories can have subcategories, and a file can be in several categories. The tools name files " +
        "and categories by numeric ids: get file ids from search, list_files or get_files, and category ids from " +
        "list_categories or search. A file is either a copy stored in Cabinette's vault folder (mode \"copy\") or a pointer " +
        "to a file that stays where it is (mode \"pointer\"). Cabinette never asks the user to confirm what you do, so change " +
        "only what the user asked for; every call is recorded in its AI activity log and changes show live in its window. " +
        "Deleted files go to the Windows Recycle Bin, so the user can restore them. Moving the vault, migrating pointers and " +
        "backups run in the background (follow them with get_operation_status); while one runs, changes fail with \"busy\" " +
        "but reading still works.";

    private const string FileIdsHint = "File ids (from search, list_files or get_files)";
    private const string CategoryIdHint = "from list_categories or search";
    private const string BusyNote = " While it runs, Cabinette refuses changes as \"busy\" (reading still works).";

    private const string TypeKeys =
        "images, video, audio, documents, text, notes, spreadsheets, presentations, code, archives, executable, ebooks, " +
        "fonts, design, models3d, diskimages, links, other";

    public static IReadOnlyList<ToolDefinition> All { get; } = Create();

    public static ToolDefinition? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    private static List<ToolDefinition> Create() =>
    [
        // ---------------------------------------------------------------- reading
        Reading<SearchArgs>(
            AiTools.Search,
            "Search Cabinette",
            "Search the user's Cabinette library like its main search box: category names and file names. All words of " +
            "`query` must match, and `#tag` tokens require that tag. Returns the matching categories and files, newest " +
            "first, at most `limit` files (default 100, max 500); `hasMore` is true when more files matched (narrow the " +
            "query or use list_files to page). With an empty query and a `filter`, lists the files that match the filter. " +
            "The file and category ids in the result are what the other tools take.",
            new ObjectSchema()
                .Property("query", Json.String(), "Words to find in category and file names; `#tag` tokens require that tag. May be empty when `filter` is set.", required: true)
                .Property("filter", FilterSchema(), "Only files that match every condition set here.")
                .Property("limit", Json.Integer(1, AiProtocol.MaxListLimit), "Most files to return: default 100, max 500.")),

        Reading<ListCategoriesArgs>(
            AiTools.ListCategories,
            "List categories",
            "List Cabinette's categories (the threads in its window), parents before children, with their ids, paths such " +
            "as \"Code/Python\", kinds (builtin, user, or the system category Deleted), file and unread counts. Pass " +
            "`parentId` to list only that category and its subcategories. Use the ids with list_files, add_files, " +
            "move_files and the other tools.",
            new ObjectSchema()
                .Property("parentId", Json.Integer(), $"Only this category and its subcategories (an id {CategoryIdHint}); omit for all.")),

        Reading<ListFilesArgs>(
            AiTools.ListFiles,
            "List files",
            "Give exactly one of `categoryId` or `starred`: true (never both, never neither): lists the files of that " +
            "category's thread (including its subcategories) or of the Starred view, newest first, in pages. `total` is how " +
            "many files match, and the result's `nextOffset` is the `offset` of the next page (null after the last page). " +
            "`query` and `filter` narrow the list.",
            new ObjectSchema()
                .Property("categoryId", Json.Integer(), $"The category whose files to list, with its subcategories (an id {CategoryIdHint}). Omit it when `starred` is true.")
                .Property("starred", Json.Boolean(), "true lists the Starred view instead of a category (then omit `categoryId`).")
                .Property("query", Json.String(), "Only files whose names match these words; `#tag` tokens require that tag.")
                .Property("filter", FilterSchema(), "Only files that match every condition set here.")
                .Property("offset", Json.Integer(0), "Where the page starts: 0 (the default), or the previous page's nextOffset.")
                .Property("limit", Json.Integer(1, AiProtocol.MaxListLimit), "Most files per page: default 100, max 500.")),

        Reading<GetFilesArgs>(
            AiTools.GetFiles,
            "Get file details",
            "Get the full details of files by id: name, path on disk, mode (copy or pointer), type, size, dates, the " +
            "categories it is in with their paths, tags, star, content hash and, for notes, the start of the text. Ids " +
            "that don't exist are listed in `notFound` (not an error).",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true)),

        Reading<ReadFileArgs>(
            AiTools.ReadFile,
            "Read file contents",
            "Read a library file's contents by id (only files in the library, not arbitrary paths). Text-like files (text, " +
            "code, notes, csv, json, xml, ...) come back as text in pages of up to 256 KB; PDF and Office files as text " +
            "extracted by Windows, where its text extractors are installed. Then the first content block is JSON metadata " +
            "(file, offset, nextOffset, total, unit, encoding) and the second is the text itself; pass `nextOffset` as " +
            "`offset` to read the next page (null at the end). Images come back as an image scaled down to at most " +
            "1568 px, with a small metadata block. Other files (videos, audio, archives, programs) are refused.",
            new ObjectSchema()
                .Property("fileId", Json.Integer(), "The file's id (from search, list_files or get_files).", required: true)
                .Property("offset", Json.Integer(0), "Where to start: 0 (the default), or the previous page's nextOffset (in the unit the metadata gives: bytes or chars). Ignored for images.")),

        Reading<ListTagsArgs>(
            AiTools.ListTags,
            "List tags",
            "List every tag in the library with how many files have it.",
            new ObjectSchema()),

        Reading<GetOperationStatusArgs>(
            AiTools.GetOperationStatus,
            "Get vault operation status",
            "Follow a vault operation started by move_vault, migrate_pointers or create_backup: its state (running, " +
            "succeeded, failed, or cancelled by the user in Cabinette's progress dialog), stage and progress, and when it " +
            "is over a summary or the error. Poll it every few seconds while the state is running.",
            new ObjectSchema()
                .Property("operationId", Json.String(), "The operationId that move_vault, migrate_pointers or create_backup returned; omit for the latest operation an AI started.")),

        // ---------------------------------------------------------------- changes
        Change<AddFilesArgs>(
            AiTools.AddFiles,
            "Add files",
            "Add files and folders to Cabinette by path. Use full paths: a relative path is resolved against your working " +
            "folder, and ~ isn't expanded. Folders become categories. With `categoryId` everything goes into that category; " +
            "without it each file goes to the category of its type (Images, Documents, ...), like a drop on Cabinette's " +
            "window. `mode` \"copy\" stores a copy in Cabinette's vault, \"pointer\" links the file where it is; omit it for " +
            "the user's default. Files whose content is already in Cabinette are reported as duplicates, not added twice; " +
            "files that fail are listed with the reason. Large files take a while to copy.",
            new ObjectSchema()
                .Property("paths", Json.Array(Json.String(minLength: 1), minItems: 1, maxItems: AiProtocol.MaxPathsPerCall), $"Full paths of the files and folders to add, at most {AiProtocol.MaxPathsPerCall}.", required: true)
                .Property("categoryId", Json.Integer(), $"Put everything in this category (an id {CategoryIdHint}); omit to sort each file by its type.")
                .Property("mode", Json.String(oneOf: [InstanceProtocol.CopyMode, InstanceProtocol.PointerMode]), "\"copy\" (store a copy in the vault) or \"pointer\" (link the file where it is); omit for the user's default."),
            destructive: false,
            idempotent: false,
            resolvePaths: (args, resolve) => args with { Paths = [.. args.Paths.Select(resolve)] }),

        Change<CreateNoteArgs>(
            AiTools.CreateNote,
            "Create note",
            "Create a note (Markdown text) in a category, by default the Notes category. Returns the new file.",
            new ObjectSchema()
                .Property("text", Json.String(minLength: 1), "The note's text, in Markdown.", required: true)
                .Property("categoryId", Json.Integer(), $"The category for the note (an id {CategoryIdHint}); omit for Notes."),
            destructive: false,
            idempotent: false),

        Change<TagFilesArgs>(
            AiTools.TagFiles,
            "Tag files",
            "Add tags to files. Returns the files as they are now.",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true)
                .Property("tags", TagArray(), "The tags to add.", required: true),
            destructive: false,
            idempotent: true),

        Change<TagFilesArgs>(
            AiTools.UntagFiles,
            "Untag files",
            "Remove tags from files. Returns the files as they are now.",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true)
                .Property("tags", TagArray(), "The tags to remove.", required: true),
            destructive: true,
            idempotent: true),

        Change<FilesCategoryArgs>(
            AiTools.AddToCategory,
            "Add files to category",
            "Add files to a category as well: they stay in the categories they are in (a file can be in several). Use " +
            "move_files to move them instead.",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true)
                .Property("categoryId", Json.Integer(), $"The category to add them to (an id {CategoryIdHint}).", required: true),
            destructive: false,
            idempotent: true),

        Change<FilesCategoryArgs>(
            AiTools.RemoveFromCategory,
            "Remove files from category",
            "Remove files from one category (and its subcategories). The files stay in the library and in their other " +
            "categories; use delete_files to delete them.",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true)
                .Property("categoryId", Json.Integer(), $"The category to remove them from (an id {CategoryIdHint}).", required: true),
            destructive: true,
            idempotent: true),

        Change<MoveFilesArgs>(
            AiTools.MoveFiles,
            "Move files",
            "Move files from one category to another, like \"Move to category…\" in Cabinette: they leave `fromCategoryId` " +
            "(and its subcategories) and join `toCategoryId`; their other categories stay. Files can't be moved into " +
            "Deleted (use delete_files).",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true)
                .Property("fromCategoryId", Json.Integer(), $"The category they leave (an id {CategoryIdHint}).", required: true)
                .Property("toCategoryId", Json.Integer(), $"The category they join (an id {CategoryIdHint}).", required: true),
            destructive: true,
            idempotent: true),

        Change<RenameFileArgs>(
            AiTools.RenameFile,
            "Rename file",
            "Rename a file. The file on disk is renamed: the vault copy, or for a pointer the linked file itself. Give the " +
            "whole new name with its extension.",
            new ObjectSchema()
                .Property("fileId", Json.Integer(), "The file's id (from search, list_files or get_files).", required: true)
                .Property("newName", Json.String(minLength: 1), "The new file name, with its extension (no folder).", required: true),
            destructive: true,
            idempotent: true),

        Change<FileIdsArgs>(
            AiTools.DeleteFiles,
            "Delete files",
            "Delete files from Cabinette, without asking the user. They go to the Windows Recycle Bin, where the user can " +
            "restore them; if one of them can't go there (network drive, USB stick, too large), nothing is deleted and the " +
            "call fails with recycle_bin_unavailable. For a pointer file, the user's setting decides whether only its " +
            "library entry is removed or the linked file goes to the Recycle Bin too.",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true),
            destructive: true,
            idempotent: true),

        Change<FileIdsArgs>(
            AiTools.StarFiles,
            "Star files",
            "Star files: they show in Cabinette's Starred view (list it with list_files and starred: true).",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true),
            destructive: false,
            idempotent: true),

        Change<FileIdsArgs>(
            AiTools.UnstarFiles,
            "Unstar files",
            "Remove the star from files.",
            new ObjectSchema()
                .Property("fileIds", IdArray(), $"{FileIdsHint}, at most {AiProtocol.MaxIdsPerCall}.", required: true),
            destructive: true,
            idempotent: true),

        Change<CreateCategoryArgs>(
            AiTools.CreateCategory,
            "Create category",
            "Create a category (a new thread), at the top level or under `parentId`. Returns it with its id.",
            new ObjectSchema()
                .Property("name", Json.String(minLength: 1), "The new category's name.", required: true)
                .Property("parentId", Json.Integer(), $"Create it under this category (an id {CategoryIdHint}); omit for the top level."),
            destructive: false,
            idempotent: false),

        Change<RenameCategoryArgs>(
            AiTools.RenameCategory,
            "Rename category",
            "Rename a category.",
            new ObjectSchema()
                .Property("categoryId", Json.Integer(), $"The category to rename (an id {CategoryIdHint}).", required: true)
                .Property("newName", Json.String(minLength: 1), "Its new name.", required: true),
            destructive: true,
            idempotent: true),

        Change<DeleteCategoryArgs>(
            AiTools.DeleteCategory,
            "Delete category",
            "Delete a category and its subcategories, without asking the user. Their files follow the user's \"Deleting a " +
            "category\" setting: they move to the Deleted category, or go to the Windows Recycle Bin (restorable there); the " +
            "result says which. The Deleted category itself can't be deleted.",
            new ObjectSchema()
                .Property("categoryId", Json.Integer(), $"The category to delete, with its subcategories (an id {CategoryIdHint}).", required: true),
            destructive: true,
            idempotent: true),

        // ---------------------------------------------------------------- vault operations (start and return at once)
        Change<MoveVaultArgs>(
            AiTools.MoveVault,
            "Move vault",
            "Move Cabinette's vault (the folder that holds the copied files) to an empty or new folder: Cabinette copies it, " +
            "checks the copy, then switches to it; the old vault folder is kept. Give a full path (a relative one is " +
            "resolved against your working folder). Starts and returns at once with an operationId: follow it with " +
            "get_operation_status. Cabinette shows its progress dialog on the user's screen, where they can cancel." + BusyNote,
            new ObjectSchema()
                .Property("newPath", Json.String(minLength: 1), "Full path of an empty or new folder for the vault.", required: true),
            destructive: true,
            idempotent: false,
            resolvePaths: (args, resolve) => args with { NewPath = resolve(args.NewPath) }),

        Change<MigratePointersArgs>(
            AiTools.MigratePointers,
            "Migrate pointers",
            "Turn every pointer file into a copy stored in the vault (the linked files themselves stay where they are). " +
            "Starts and returns at once with an operationId: follow it with get_operation_status, which lists files it " +
            "couldn't copy. Cabinette shows its progress dialog on the user's screen, where they can cancel." + BusyNote,
            new ObjectSchema(),
            destructive: false,
            idempotent: false),

        Change<CreateBackupArgs>(
            AiTools.CreateBackup,
            "Create backup",
            "Write a portable backup .zip of the library (database and vault files) to `zipPath`. Give a full path (a " +
            "relative one is resolved against your working folder). Starts and returns at once with an operationId: follow " +
            "it with get_operation_status, which also says how many linked (pointer) files the backup left out. Cabinette " +
            "shows its progress dialog on the user's screen, where they can cancel." + BusyNote,
            new ObjectSchema()
                .Property("zipPath", Json.String(minLength: 1), "Full path of the .zip file to write.", required: true),
            destructive: false,
            idempotent: false,
            resolvePaths: (args, resolve) => args with { ZipPath = resolve(args.ZipPath) }),

        // ---------------------------------------------------------------- the window
        Change<ShowArgs>(
            AiTools.ShowInCabinette,
            "Show in Cabinette",
            "Give exactly one of `fileId` or `categoryId` (never both, never neither): opens Cabinette's window on the " +
            "user's screen at that file (its thread, scrolled to the file) or at that category. Use it when the user wants " +
            "to see something: it brings the window up on their desktop.",
            new ObjectSchema()
                .Property("fileId", Json.Integer(), "Show this file (an id from search, list_files or get_files). Omit it when giving `categoryId`.")
                .Property("categoryId", Json.Integer(), $"Show this category's thread (an id {CategoryIdHint}). Omit it when giving `fileId`."),
            destructive: false,
            idempotent: true),
    ];

    /// <summary>The schema of <see cref="AiFilter"/>.</summary>
    public static ObjectSchema FilterSchema() => new ObjectSchema()
        .Property("types", Json.Array(Json.String(minLength: 1)), $"Type keys, as in Cabinette's type filter: {TypeKeys}.")
        .Property("extensions", Json.Array(Json.String(minLength: 1)), "File extensions, with or without the dot (\"pdf\", \".py\").")
        .Property("mode", Json.String(oneOf: [InstanceProtocol.CopyMode, InstanceProtocol.PointerMode]), "\"copy\" (stored in the vault) or \"pointer\" (linked where it is).")
        .Property("missingOnly", Json.Boolean(), "true: only pointer files whose linked file is missing.")
        .Property("addedFrom", Json.String(format: "date-time"), "Added at or after this time (ISO 8601, e.g. 2026-10-01T00:00:00+02:00).")
        .Property("addedTo", Json.String(format: "date-time"), "Added at or before this time (ISO 8601).")
        .Property("minSizeBytes", Json.Integer(0), "At least this many bytes.")
        .Property("maxSizeBytes", Json.Integer(0), "At most this many bytes.");

    private static JsonObject IdArray() => Json.Array(Json.Integer(), minItems: 1, maxItems: AiProtocol.MaxIdsPerCall);

    private static JsonObject TagArray() => Json.Array(Json.String(minLength: 1), minItems: 1);

    private static ToolDefinition Reading<TArgs>(string name, string title, string description, ObjectSchema input)
        where TArgs : class
        => Define<TArgs>(name, title, description, input, readOnly: true, destructive: false, idempotent: true, resolvePaths: null);

    private static ToolDefinition Change<TArgs>(string name, string title, string description, ObjectSchema input,
        bool destructive, bool idempotent, Func<TArgs, Func<string, string>, TArgs>? resolvePaths = null)
        where TArgs : class
        => Define(name, title, description, input, readOnly: false, destructive, idempotent, resolvePaths);

    private static ToolDefinition Define<TArgs>(string name, string title, string description, ObjectSchema input,
        bool readOnly, bool destructive, bool idempotent, Func<TArgs, Func<string, string>, TArgs>? resolvePaths)
        where TArgs : class
        => new()
        {
            Name = name,
            Title = title,
            Description = description,
            Input = input,
            ArgsType = typeof(TArgs),
            ReadOnly = readOnly,
            Destructive = destructive,
            Idempotent = idempotent,
            Deserialize = json => json.Deserialize<TArgs>(AiProtocol.JsonOptions),
            ResolvePaths = resolvePaths is null ? (args, _) => args : (args, resolve) => resolvePaths((TArgs)args, resolve),
            CreateMessage = (client, args) => InstanceMessage.Ai(client, name, (TArgs)args),
        };
}
