using System.Text.Json;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// Cabinette's side of the "ai" command (SPEC.md section 10): checks one tool call from cabinette-mcp.exe, runs it through the
/// library like the app's own buttons (marked as added by the AI, so files count as unread and the UI updates live), logs it in
/// the AI activity log, reports changes for the grouped toasts and answers with the tool's result record from AiProtocol.cs.
/// Calls naming an unknown id change nothing. Never throws. Call from any thread (the local channel's).
/// </summary>
public sealed partial class AiCommandHandler
{
    internal const string DefaultClientName = "AI client";
    internal const int MaxClientNameLength = 64;
    internal const string ClosingMessage = "Cabinette is closing.";
    internal const string VersionMessage = "Cabinette and cabinette-mcp.exe are different versions; update both.";

    internal const string BusyMessage =
        "Cabinette is busy with a long vault operation (move vault, migrate, backup or restore). Reading still works; try changes " +
        "again when it is done (get_operation_status follows one an AI started).";

    private readonly ILibraryService _library;
    private readonly IAiActivityLog _log;
    private readonly IAiChangeReporter _changes;
    private readonly IAiCommandHost _host;
    private readonly IAiVaultOperations _vault;
    private readonly AiCommandOperations _operations;
    private readonly IFileContentReader? _contentReader;
    private readonly Dictionary<string, Func<AiCall, Task<object>>> _tools;

    /// <param name="contentReader">Reads file contents for read_file; null in builds without it.</param>
    public AiCommandHandler(
        ILibraryService library,
        IAiActivityLog log,
        IAiChangeReporter changes,
        IAiCommandHost host,
        IAiVaultOperations vault,
        AiCommandOperations operations,
        IFileContentReader? contentReader = null)
    {
        _library = library;
        _log = log;
        _changes = changes;
        _host = host;
        _vault = vault;
        _operations = operations;
        _contentReader = contentReader;
        _tools = new Dictionary<string, Func<AiCall, Task<object>>>(StringComparer.Ordinal)
        {
            [AiTools.Search] = SearchAsync,
            [AiTools.ListCategories] = ListCategoriesAsync,
            [AiTools.ListFiles] = ListFilesAsync,
            [AiTools.GetFiles] = GetFilesAsync,
            [AiTools.ReadFile] = ReadFileAsync,
            [AiTools.ListTags] = ListTagsAsync,
            [AiTools.GetOperationStatus] = GetOperationStatusAsync,
            [AiTools.AddFiles] = AddFilesAsync,
            [AiTools.CreateNote] = CreateNoteAsync,
            [AiTools.TagFiles] = call => ChangeTagsAsync(call, add: true),
            [AiTools.UntagFiles] = call => ChangeTagsAsync(call, add: false),
            [AiTools.AddToCategory] = AddToCategoryAsync,
            [AiTools.RemoveFromCategory] = RemoveFromCategoryAsync,
            [AiTools.MoveFiles] = MoveFilesAsync,
            [AiTools.RenameFile] = RenameFileAsync,
            [AiTools.DeleteFiles] = DeleteFilesAsync,
            [AiTools.StarFiles] = call => SetStarredAsync(call, starred: true),
            [AiTools.UnstarFiles] = call => SetStarredAsync(call, starred: false),
            [AiTools.CreateCategory] = CreateCategoryAsync,
            [AiTools.RenameCategory] = RenameCategoryAsync,
            [AiTools.DeleteCategory] = DeleteCategoryAsync,
            [AiTools.MoveVault] = MoveVaultAsync,
            [AiTools.MigratePointers] = MigratePointersAsync,
            [AiTools.CreateBackup] = CreateBackupAsync,
            [AiTools.ShowInCabinette] = ShowInCabinetteAsync,
        };
    }

    /// <summary>The tools this handler runs (every tool of <see cref="AiTools.All"/>).</summary>
    internal IReadOnlyCollection<string> Tools => _tools.Keys;

    /// <summary>Runs one "ai" message and answers it. Every call that reaches the library is logged, failed ones too.</summary>
    public async Task<InstanceReply> HandleAsync(InstanceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_host.IsQuitting)
        {
            // The library may be closing: nothing is logged.
            return InstanceReply.Failed(AiErrorCodes.NotReady, ClosingMessage);
        }

        var client = AiCommandConversions.CleanName(message.Client, MaxClientNameLength, DefaultClientName);
        var tool = AiCommandConversions.CleanName(message.Tool, MaxClientNameLength, "");
        var call = new AiCall(client, tool, message.Args);
        InstanceReply reply;
        try
        {
            reply = await RunAsync(call, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reply = ToReply(call, ex);
        }

        // Never throws; awaited so the entry is stored before the AI hears back. The library appends it in a short transaction of
        // its own, not behind its write lock, so a running Move vault or Backup doesn't hold the reply.
        await _log.LogAsync(call.Client, tool.Length > 0 ? tool : "(none)", ForLog(call.Details with
        {
            Ok = reply.Ok,
            ErrorCode = reply.Ok ? null : reply.Code ?? AiErrorCodes.Failed,
            Error = reply.Ok ? null : reply.Error,
        })).ConfigureAwait(false);
        return reply;
    }

    /// <summary>The longest text the activity log keeps of one field (a query, a path, an error); longer ones end in "…".</summary>
    internal const int MaxLoggedTextLength = 2000;

    /// <summary>
    /// What the activity log keeps of a call, which it stores for a year: every text cut to <see cref="MaxLoggedTextLength"/>
    /// characters, at most <see cref="AiActivityDetails.MaxRefs"/> paths, files and categories. The reply isn't affected.
    /// </summary>
    internal static AiActivityDetails ForLog(AiActivityDetails details) => details with
    {
        ErrorCode = Cap(details.ErrorCode),
        Error = Cap(details.Error),
        Query = Cap(details.Query),
        NewName = Cap(details.NewName),
        Mode = Cap(details.Mode),
        OperationId = Cap(details.OperationId),
        Paths = details.Paths?.Take(AiActivityDetails.MaxRefs).Select(p => Cap(p) ?? "").ToList(),
        Tags = details.Tags?.Select(t => Cap(t) ?? "").ToList(),
        Files = details.Files?.Take(AiActivityDetails.MaxRefs).Select(r => r with { Name = Cap(r.Name) ?? "" }).ToList(),
        Categories = details.Categories?.Take(AiActivityDetails.MaxRefs).Select(r => r with { Name = Cap(r.Name) ?? "" }).ToList(),
    };

    private static string? Cap(string? text)
    {
        if (text is null || text.Length <= MaxLoggedTextLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[MaxLoggedTextLength - 1]) ? MaxLoggedTextLength - 1 : MaxLoggedTextLength;
        return string.Concat(text.AsSpan(0, cut), "…");
    }

    private async Task<InstanceReply> RunAsync(AiCall call, InstanceMessage message)
    {
        if (message.Version != AiProtocol.Version)
        {
            return InstanceReply.Failed(AiErrorCodes.Unsupported, VersionMessage);
        }

        if (call.Tool.Length == 0 || !_tools.TryGetValue(call.Tool, out var run))
        {
            return InstanceReply.Failed(AiErrorCodes.Unsupported,
                call.Tool.Length == 0 ? "The call names no tool." : $"Cabinette has no tool named \"{call.Tool}\".");
        }

        // While a long vault operation runs, changes wait; reading and "show in Cabinette" still work.
        if (AiTools.IsChange(call.Tool) && await _host.IsOperationRunningAsync().ConfigureAwait(false))
        {
            return InstanceReply.Failed(AiErrorCodes.Busy, BusyMessage);
        }

        var result = await run(call).ConfigureAwait(false);
        return InstanceReply.WithResult(result);
    }

    /// <summary>A failed call's reply. Unexpected errors are written to Cabinette's log.</summary>
    private static InstanceReply ToReply(AiCall call, Exception ex)
    {
        switch (ex)
        {
            case AiCallException e:
                return InstanceReply.Failed(e.Code, e.Message);
            case LibraryException e:
                var code = ErrorCodeFor(e.Code);
                if (code == AiErrorCodes.Failed)
                {
                    AppLog.Warn($"The AI tool '{call.Tool}' failed in the library ({e.Code}).", e);
                }

                return InstanceReply.Failed(code, e.Message);
            case FileContentRefusedException e:
                return InstanceReply.Failed(AiErrorCodes.Refused, e.Message);
            case ObjectDisposedException:
                // The library closed under the call (quitting, or a restore replacing it).
                return InstanceReply.Failed(AiErrorCodes.NotReady, "Cabinette is closing or reopening its library. Try again in a moment.");
            default:
                AppLog.Error($"The AI tool '{call.Tool}' failed.", ex);
                return InstanceReply.Failed(AiErrorCodes.Failed, $"Something went wrong: {ex.Message}");
        }
    }

    internal static string ErrorCodeFor(LibraryErrorCode code) => code switch
    {
        LibraryErrorCode.RecycleBinUnavailable => AiErrorCodes.RecycleBinUnavailable,
        LibraryErrorCode.NotFound => AiErrorCodes.NotFound,
        LibraryErrorCode.InvalidName or LibraryErrorCode.NameAlreadyExists or LibraryErrorCode.FileInUse
            or LibraryErrorCode.AccessDenied or LibraryErrorCode.NotAllowed => AiErrorCodes.Refused,
        LibraryErrorCode.VaultNotConfigured => AiErrorCodes.NotReady,
        _ => AiErrorCodes.Failed,
    };

    // ==================================================================== arguments

    /// <summary>The tool's arguments; invalid_argument when they are missing or don't fit the record.</summary>
    private static T Args<T>(AiCall call)
        where T : class
        => AiProtocol.FromElement<T>(call.Args) ?? throw AiCallException.Invalid($"The arguments of {call.Tool} are missing or don't fit the tool.");

    /// <summary>Arguments that may be left out entirely (all optional): <paramref name="none"/> then.</summary>
    private static T OptionalArgs<T>(AiCall call, T none)
        where T : class
        => call.Args is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? none : Args<T>(call);

    /// <summary>At least one id, at most <see cref="AiProtocol.MaxIdsPerCall"/>; duplicates removed, order kept.</summary>
    private static IReadOnlyList<long> FileIds(IReadOnlyList<long>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            throw AiCallException.Invalid("fileIds must name at least one file.");
        }

        if (ids.Count > AiProtocol.MaxIdsPerCall)
        {
            throw AiCallException.Invalid($"At most {AiProtocol.MaxIdsPerCall} file ids per call ({ids.Count} given).");
        }

        return ids.Distinct().ToList();
    }

    private static string RequiredName(string? name, string what)
        => string.IsNullOrWhiteSpace(name) ? throw AiCallException.Invalid($"{what} can't be empty.") : name.Trim();

    private static int ClampLimit(int? limit) => Math.Clamp(limit ?? AiProtocol.DefaultListLimit, 1, AiProtocol.MaxListLimit);

    // ==================================================================== ids → library records (every id checked before a change)

    /// <summary>Every item, in order; not_found naming the unknown ids (nothing has changed yet). The log names the known ones.</summary>
    private async Task<IReadOnlyList<LibraryItem>> LoadItemsAsync(AiCall call, IReadOnlyList<long> ids)
    {
        var items = new List<LibraryItem>(ids.Count);
        var missing = new List<long>();
        foreach (var id in ids)
        {
            if (await _library.GetItemAsync(id).ConfigureAwait(false) is { } item)
            {
                items.Add(item);
            }
            else
            {
                missing.Add(id);
            }
        }

        call.Details = call.Details with { Files = Refs(items), FileCount = ids.Count };
        if (missing.Count > 0)
        {
            throw new AiCallException(AiErrorCodes.NotFound, MissingMessage("file", missing) + " Nothing was changed.");
        }

        return items;
    }

    private async Task<LibraryItem> LoadItemAsync(AiCall call, long id) => (await LoadItemsAsync(call, [id]).ConfigureAwait(false))[0];

    /// <summary>The category, also named in the log; not_found when it doesn't exist.</summary>
    private async Task<Category> LoadCategoryAsync(AiCall call, long id)
    {
        var category = await _library.GetCategoryAsync(id).ConfigureAwait(false)
            ?? throw new AiCallException(AiErrorCodes.NotFound, MissingMessage("category", [id]) + " Nothing was changed.");
        call.AddCategory(category);
        return category;
    }

    private static string MissingMessage(string noun, IReadOnlyList<long> ids)
    {
        const int Listed = 20;
        var list = string.Join(", ", ids.Take(Listed));
        if (ids.Count > Listed)
        {
            list += $" and {ids.Count - Listed} more";
        }

        return ids.Count == 1 ? $"No {noun} has the id {ids[0]}." : $"No {noun}s have the ids {list}.";
    }

    private async Task<IReadOnlyDictionary<long, string>> CategoryPathsAsync()
        => AiCommandConversions.CategoryPaths(await _library.GetAllCategoriesAsync().ConfigureAwait(false));

    internal static IReadOnlyList<AiActivityRef> Refs(IEnumerable<LibraryItem> items)
        => items.Take(AiActivityDetails.MaxRefs).Select(i => new AiActivityRef(i.Id, i.FileName)).ToList();

    private void Report(AiChange change)
    {
        try
        {
            _changes.Report(change);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reporting an AI change ({change.Kind}) failed.", ex);
        }
    }

    /// <summary>One call being handled: who and what, and what the activity log will say about it.</summary>
    private sealed class AiCall(string client, string tool, JsonElement? args)
    {
        public string Client { get; } = client;

        public string Tool { get; } = tool;

        public JsonElement? Args { get; } = args;

        public AiActivityDetails Details { get; set; } = new();

        public void AddCategory(Category category)
        {
            var refs = Details.Categories ?? [];
            if (refs.Count < AiActivityDetails.MaxRefs && refs.All(r => r.Id != category.Id))
            {
                Details = Details with { Categories = [.. refs, new AiActivityRef(category.Id, category.Name)] };
            }
        }
    }
}

/// <summary>A tool call refused with one of <see cref="AiErrorCodes"/>; the message is for the AI.</summary>
internal sealed class AiCallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static AiCallException Invalid(string message) => new(AiErrorCodes.InvalidArgument, message);

    public static AiCallException Refused(string message) => new(AiErrorCodes.Refused, message);
}
