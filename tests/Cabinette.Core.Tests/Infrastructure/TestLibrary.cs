using Cabinette.Core.Abstractions;
using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.Core.Tests.Infrastructure;

/// <summary>Clock that only moves when the test says so. Local time = UTC so generated names are predictable.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>Moves "recycled" files and folders into a temp folder so tests can assert what was recycled.</summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    private readonly string _folder;
    private readonly object _gate = new();

    public FakeRecycleBin(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public List<string> Recycled { get; } = [];

    public bool FailNext { get; set; }

    /// <summary>Folders on a "drive" without a Recycle Bin: <see cref="CanRecycle"/> says no and their items are left in place.</summary>
    public List<string> FoldersWithoutRecycleBin { get; } = [];

    /// <summary>Paths the Recycle Bin refuses although <see cref="CanRecycle"/> says yes (as for an item too large for it).</summary>
    public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files <see cref="CanRecycle"/> reports as too large for the Recycle Bin up front; they are left in place too.</summary>
    public HashSet<string> TooLarge { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs before anything is recycled, with the paths.</summary>
    public Action<IReadOnlyCollection<string>>? BeforeRecycle { get; set; }

    public bool CanRecycle(string path)
        => !TooLarge.Contains(path)
           && !FoldersWithoutRecycleBin.Any(folder => path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    public void SendToRecycleBin(IReadOnlyCollection<string> paths)
    {
        BeforeRecycle?.Invoke(paths);
        lock (_gate)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Simulated shell failure.");
            }

            var left = new List<string>();
            foreach (var path in paths)
            {
                if (!CanRecycle(path) || Refused.Contains(path))
                {
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        left.Add(path);
                    }

                    continue;
                }

                var target = Path.Combine(_folder, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path.TrimEnd('\\')));
                if (File.Exists(path))
                {
                    File.Move(path, target);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Move(path, target);
                }
                else
                {
                    continue;
                }

                Recycled.Add(path);
            }

            if (left.Count > 0)
            {
                throw new RecycleBinUnavailableException(left, "Simulated: these can only be deleted permanently.");
            }
        }
    }

    /// <summary>
    /// Under the same lock as <see cref="SendToRecycleBin"/>: a test that saw the file disappear from another thread waits
    /// until the call has recorded it (the file is moved before it is added to <see cref="Recycled"/>).
    /// </summary>
    public bool WasRecycled(string path)
    {
        lock (_gate)
        {
            return Recycled.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>A library in a fresh temp folder (database, vault, source files, recycle bin). Disposing deletes everything.</summary>
public sealed class TestLibrary : IAsyncDisposable
{
    private readonly List<LibraryChangedEventArgs> _events = [];

    private TestLibrary(string root, FakeTimeProvider time, FakeRecycleBin recycleBin)
    {
        Root = root;
        Time = time;
        RecycleBin = recycleBin;
    }

    public string Root { get; }

    public string DataDirectory => Path.Combine(Root, "data");

    public string Vault => Path.Combine(Root, "vault");

    public string Source => Path.Combine(Root, "source");

    public FakeTimeProvider Time { get; }

    public FakeRecycleBin RecycleBin { get; }

    public ILibraryService Library { get; private set; } = null!;

    internal LibraryService Service => (LibraryService)Library;

    public IReadOnlyList<LibraryChangedEventArgs> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToList();
            }
        }
    }

    public static async Task<TestLibrary> CreateAsync(bool initializeVault = true, IDateTakenReader? dateTakenReader = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cabinette-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 14, 9, 26, 53, TimeSpan.Zero));
        var lib = new TestLibrary(root, time, new FakeRecycleBin(Path.Combine(root, "recycle"))) { DateTakenReader = dateTakenReader };
        Directory.CreateDirectory(lib.Source);
        await lib.OpenAsync();
        if (initializeVault)
        {
            await lib.Library.InitializeVaultAsync(lib.Vault);
        }

        lib.ClearEvents();
        return lib;
    }

    /// <summary>The date-taken reader the library is opened with (none by default: dates taken are then never read).</summary>
    public IDateTakenReader? DateTakenReader { get; set; }

    public LibraryOptions Options => new() { DataDirectory = DataDirectory, TimeProvider = Time, DateTakenReader = DateTakenReader };

    public async Task OpenAsync()
    {
        Use(await LibraryFactory.OpenAsync(Options, RecycleBin));

        // Opening empties the staging folder in the background; tests look at the vault right after. It prunes the AI
        // activity log in the background too.
        await Service.WhenStagingSweptAsync();
        await Service.WhenActivityPrunedAsync();
    }

    /// <summary>Uses a library opened elsewhere (e.g. by a restore). It is disposed with this test library.</summary>
    public void Use(ILibraryService library)
    {
        Library = library;
        Library.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public async Task ReopenAsync()
    {
        await Library.DisposeAsync();
        await OpenAsync();
    }

    public void ClearEvents()
    {
        lock (_events)
        {
            _events.Clear();
        }
    }

    /// <summary>Creates a source file (relative to <see cref="Source"/>) and returns its full path.</summary>
    public string CreateFile(string relativePath, string content, DateTime? lastWriteUtc = null)
        => CreateFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content), lastWriteUtc);

    public string CreateFile(string relativePath, byte[] content, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (lastWriteUtc is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
        }

        return path;
    }

    public async Task<Category> BuiltInAsync(string key) => await Library.GetOrCreateBuiltInCategoryAsync(key);

    public async Task<AddResult> AddAsync(long? targetCategoryId, ItemMode mode, params string[] paths)
        => await Library.AddPathsAsync(paths, new AddRequest { TargetCategoryId = targetCategoryId, Mode = mode });

    public async Task<LibraryItem> AddOneAsync(long? targetCategoryId, string path, ItemMode mode = ItemMode.Copy, AddedBy addedBy = AddedBy.AppUi)
    {
        var result = await Library.AddPathsAsync([path], new AddRequest { TargetCategoryId = targetCategoryId, Mode = mode, AddedBy = addedBy });
        Assert.Empty(result.Failures);
        return Assert.Single(result.Added);
    }

    public async Task<Category> CategoryAsync(long id) => (await Library.GetCategoryAsync(id))!;

    public async Task<CategorySummary> SummaryAsync(long id) => (await Library.GetCategorySummaryAsync(id))!;

    public async Task<LibraryItem> ItemAsync(long id) => (await Library.GetItemAsync(id))!;

    public async ValueTask DisposeAsync()
    {
        if (Library is not null)
        {
            await Library.DisposeAsync();
        }

        // Only this library's pool: ClearAllPools would race with other tests opening connections in parallel.
        if (Directory.Exists(DataDirectory))
        {
            new Database(DataDirectory).ClearPool();
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}
