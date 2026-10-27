using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Abstractions;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// A real library in a temp folder with a test Recycle Bin (never the real one), the "ai" command handler on top of it, and
/// fakes for the activity log, the change reporter, the app host and the vault operations. Calls go through the wire format
/// (serialized and parsed like the local channel does). Nothing is shown.
/// </summary>
public abstract class AiCommandTestBase : IAsyncLifetime
{
    protected const string Client = "Claude Code";

    protected AiCommandTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "ai-" + Guid.NewGuid().ToString("N"));
        RecycleBin = new TestRecycleBin(Path.Combine(Root, "recycled"));
    }

    protected string Root { get; }

    protected string Vault => Path.Combine(Root, "vault");

    protected TestRecycleBin RecycleBin { get; }

    protected ILibraryService Library { get; private set; } = null!;

    protected FakeActivityLog Log { get; } = new();

    protected FakeChangeReporter Changes { get; } = new();

    protected FakeAiHost Host { get; } = new();

    protected FakeVaultOperations VaultOperations { get; } = new();

    protected AiCommandOperations Operations { get; } = new();

    protected IFileContentReader? ContentReader { get; set; }

    private AiCommandHandler? _handler;

    protected AiCommandHandler Handler => _handler ??= new AiCommandHandler(Library, Log, Changes, Host, VaultOperations, Operations, ContentReader);

    public virtual async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        Library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.Combine(Root, "data") }, RecycleBin);
        await Library.InitializeVaultAsync(Vault);
    }

    public virtual async Task DisposeAsync()
    {
        if (Library is not null)
        {
            await Library.DisposeAsync();
        }

        TestFolders.Delete(Root);
    }

    // ------------------------------------------------------------------ calls

    /// <summary>One tool call as cabinette-mcp.exe sends it (through the line format).</summary>
    protected Task<InstanceReply> CallAsync<TArgs>(string tool, TArgs args, string? client = Client, int? version = AiProtocol.Version)
        => Handler.HandleAsync(Wire(tool, args, client, version));

    protected Task<InstanceReply> CallAsync(string tool) => CallAsync<object?>(tool, null);

    internal static InstanceMessage Wire<TArgs>(string tool, TArgs args, string? client = Client, int? version = AiProtocol.Version)
    {
        var message = InstanceMessage.Ai(client ?? "", tool, args) with { Client = client, Version = version };
        Assert.True(InstanceProtocol.TryParseMessage(InstanceProtocol.Serialize(message), out var parsed, out var error), error);
        return parsed;
    }

    /// <summary>Asserts success and reads the result record.</summary>
    protected static T Ok<T>(InstanceReply reply)
        where T : class
    {
        Assert.True(reply.Ok, $"{reply.Code}: {reply.Error}");
        Assert.Null(reply.Code);
        return AiProtocol.FromElement<T>(reply.Result) ?? throw new Xunit.Sdk.XunitException("The result doesn't fit " + typeof(T).Name);
    }

    /// <summary>Asserts a failure with <paramref name="code"/> and returns its message.</summary>
    protected static string Failed(InstanceReply reply, string code)
    {
        Assert.False(reply.Ok, "the call succeeded");
        Assert.Equal(code, reply.Code);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error));
        return reply.Error!;
    }

    // ------------------------------------------------------------------ library setup

    protected string SourceFile(string name, string? content = null)
    {
        var path = Path.Combine(Root, "source", Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? name + " " + Guid.NewGuid());
        return path;
    }

    protected async Task<LibraryItem> AddAsync(long categoryId, string name, string? content = null, ItemMode mode = ItemMode.Copy)
    {
        var source = SourceFile(name, content ?? name + " " + Guid.NewGuid());
        var result = await Library.AddPathsAsync([source], new AddRequest { TargetCategoryId = categoryId, Mode = mode });
        Assert.Empty(result.Failures);
        return Assert.Single(result.Added);
    }

    protected Task<Category> BuiltInAsync(string key) => Library.GetOrCreateBuiltInCategoryAsync(key);

    protected async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
        => await Library.SaveSettingsAsync(change(await Library.GetSettingsAsync()));
}

/// <summary>Deletes test folders, retrying while files are still held.</summary>
internal static class TestFolders
{
    public static void Delete(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(entry, File.Exists(entry) ? FileAttributes.Normal : FileAttributes.Directory);
                    }

                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>
/// A Recycle Bin in a temp folder: "recycled" files are moved there and recorded. <see cref="TooLarge"/> files are refused up
/// front (CanRecycle), <see cref="Refused"/> ones only when sent, as Windows does for some files too large for it.
/// </summary>
public sealed class TestRecycleBin(string folder) : IRecycleBin
{
    private readonly Lock _gate = new();

    public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> TooLarge { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Recycled { get; } = [];

    public bool CanRecycle(string path)
    {
        lock (_gate)
        {
            return !TooLarge.Contains(path);
        }
    }

    public void SendToRecycleBin(IReadOnlyCollection<string> paths)
    {
        List<string> refused;
        lock (_gate)
        {
            refused = paths.Where(p => Refused.Contains(p) || TooLarge.Contains(p)).ToList();
        }

        Directory.CreateDirectory(folder);
        foreach (var path in paths.Except(refused, StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(folder, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path)));
                lock (_gate)
                {
                    Recycled.Add(path);
                }
            }
            else if (Directory.Exists(path))
            {
                Directory.Move(path, Path.Combine(folder, Guid.NewGuid().ToString("N")));
                lock (_gate)
                {
                    Recycled.Add(path);
                }
            }
        }

        if (refused.Count > 0)
        {
            throw new RecycleBinUnavailableException(refused, "Simulated: too large for the Recycle Bin.");
        }
    }
}

/// <summary>Records activity entries instead of storing them.</summary>
public sealed class FakeActivityLog : IAiActivityLog
{
    private readonly Lock _gate = new();
    private readonly List<(string Client, string Tool, AiActivityDetails Details)> _entries = [];

    public event EventHandler<ActivityEntry>? EntryAdded
    {
        add { }
        remove { }
    }

    public IReadOnlyList<(string Client, string Tool, AiActivityDetails Details)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public (string Client, string Tool, AiActivityDetails Details) Last => Entries[^1];

    public Task<ActivityEntry?> LogAsync(string client, string tool, AiActivityDetails details)
    {
        lock (_gate)
        {
            _entries.Add((client, tool, details));
        }

        return Task.FromResult<ActivityEntry?>(new ActivityEntry { Source = client, Action = tool, DetailsJson = details.ToJson() });
    }

    public Task<ActivityPage> GetAsync(ActivityQuery query, CancellationToken ct = default) => Task.FromResult(new ActivityPage([], false));
}

/// <summary>Records reported changes.</summary>
public sealed class FakeChangeReporter : IAiChangeReporter
{
    private readonly Lock _gate = new();
    private readonly List<AiChange> _changes = [];

    public IReadOnlyList<AiChange> Reported
    {
        get
        {
            lock (_gate)
            {
                return _changes.ToList();
            }
        }
    }

    public void Report(AiChange change)
    {
        lock (_gate)
        {
            _changes.Add(change);
        }
    }
}

/// <summary>The app as the handler sees it: flags set by the test, "show" recorded, UI work run inline.</summary>
public sealed class FakeAiHost : IAiCommandHost
{
    public bool IsQuitting { get; set; }

    public bool IsOperationRunning { get; set; }

    public List<(long CategoryId, long? FocusItemId)> Shown { get; } = [];

    public Task<bool> IsOperationRunningAsync() => Task.FromResult(IsOperationRunning);

    public Task ShowAsync(long categoryId, long? focusItemId)
    {
        lock (Shown)
        {
            Shown.Add((categoryId, focusItemId));
        }

        return Task.CompletedTask;
    }

    public Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action) => action();
}

/// <summary>Vault operations that record what was asked and answer with <see cref="Next"/>.</summary>
public sealed class FakeVaultOperations : IAiVaultOperations
{
    public List<string> Calls { get; } = [];

    public IProgress<VaultOperationProgress>? LastStatus { get; private set; }

    public Func<AiVaultOperationStart> Next { get; set; } = () => AiVaultOperationStart.Refused(AiErrorCodes.Failed, "not set up");

    public Task<AiVaultOperationStart> StartMoveVaultForAiAsync(string newVaultPath, IProgress<VaultOperationProgress> status)
        => Record("move:" + newVaultPath, status);

    public Task<AiVaultOperationStart> StartMigrateForAiAsync(IProgress<VaultOperationProgress> status) => Record("migrate", status);

    public Task<AiVaultOperationStart> StartBackupForAiAsync(string zipPath, IProgress<VaultOperationProgress> status)
        => Record("backup:" + zipPath, status);

    private Task<AiVaultOperationStart> Record(string call, IProgress<VaultOperationProgress> status)
    {
        Calls.Add(call);
        LastStatus = status;
        return Task.FromResult(Next());
    }
}

/// <summary>A content reader that answers with text, or throws what the test sets.</summary>
public sealed class FakeContentReader : IFileContentReader
{
    public Exception? Throw { get; set; }

    public List<(long ItemId, long Offset)> Reads { get; } = [];

    public Task<ReadFileResult> ReadAsync(LibraryItem item, long offset, CancellationToken ct = default)
    {
        Reads.Add((item.Id, offset));
        if (Throw is not null)
        {
            return Task.FromException<ReadFileResult>(Throw);
        }

        var text = File.ReadAllText(item.FullPath);
        return Task.FromResult(new ReadFileResult
        {
            Kind = "text",
            Text = text[(int)Math.Min(offset, text.Length)..],
            Offset = offset,
            Total = text.Length,
            Unit = "bytes",
            Encoding = "utf-8",
            Source = "file",
        });
    }
}

/// <summary>Records which threads were opened.</summary>
public sealed class RecordingNavigator : IShellNavigator
{
    public List<(long CategoryId, long? FocusItemId)> Opened { get; } = [];

    public long? CurrentCategoryId => Opened.Count > 0 ? Opened[^1].CategoryId : null;

    public void OpenCategory(long categoryId, long? focusItemId = null) => Opened.Add((categoryId, focusItemId));
}

/// <summary>
/// A library that forwards every call to a real one, except the methods a test replaces (for example a Move vault that waits
/// until it is cancelled).
/// </summary>
public class InterceptingLibrary : DispatchProxy
{
    private ILibraryService _inner = null!;
    private Dictionary<string, Func<object?[], object?>> _overrides = [];

    public static ILibraryService Create(ILibraryService inner, Dictionary<string, Func<object?[], object?>> overrides)
    {
        var proxy = Create<ILibraryService, InterceptingLibrary>();
        var self = (InterceptingLibrary)(object)proxy;
        self._inner = inner;
        self._overrides = overrides;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_overrides.TryGetValue(targetMethod.Name, out var replacement))
        {
            return replacement(args ?? []);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>Async test bodies on an STA thread with a pumping dispatcher (as on Cabinette's UI thread). Nothing is shown.</summary>
internal static class StaAsync
{
    public static void Run(Func<Task> body, TimeSpan? timeout = null) => HiddenWpf.RunSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = body();
        if (!HiddenWpf.PumpUntil(() => task.IsCompleted, timeout ?? TimeSpan.FromSeconds(45)))
        {
            throw new TimeoutException("The STA test body did not finish.");
        }

        task.GetAwaiter().GetResult();
    });

    /// <summary>Waits (asynchronously, so the dispatcher keeps pumping) until the condition holds.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }
}
