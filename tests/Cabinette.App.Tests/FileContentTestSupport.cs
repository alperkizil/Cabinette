using System.IO;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>A temp folder for the read_file tests, deleted afterwards.</summary>
internal sealed class ContentFolder : IDisposable
{
    public ContentFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cabinette.App.Tests", "content-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Bytes(string name, byte[] content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string Text(string name, string content) => Bytes(name, new System.Text.UTF8Encoding(false).GetBytes(content));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}

internal static class ContentItems
{
    private static long _nextId = 1;

    public static LibraryItem For(string path, ItemKind kind = ItemKind.File)
    {
        var ext = ExtensionMap.Normalize(Path.GetExtension(path));
        return new LibraryItem
        {
            Id = Interlocked.Increment(ref _nextId),
            FileName = Path.GetFileName(path),
            Extension = ext,
            FullPath = path,
            Kind = kind,
            TypeKey = ExtensionMap.GetTypeKey(kind, ext),
        };
    }
}

/// <summary>
/// The real Cabinette.exe as the extraction child, only ever with an argument set that is safe even if the child mode
/// were broken: a temp <c>--data-dir</c> (never the real data folder) and <c>--screenshot</c> (an off-screen run that
/// shows no window and exits). Only the build next to these tests, whose Cabinette.dll is the one they compile against.
/// </summary>
internal static class SafeChild
{
    public static string Exe { get; } = Path.Combine(AppContext.BaseDirectory, "Cabinette.exe");

    /// <summary>Fails the test unless Cabinette.exe here runs the Cabinette.dll these tests were built with (it has the child mode).</summary>
    public static void EnsureSameBuild()
    {
        Assert.True(File.Exists(Exe), $"Cabinette.exe isn't next to the tests ({Exe}).");
        var appAssembly = typeof(TextExtractionChild).Assembly.Location;
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Cabinette.dll")),
            Path.GetFullPath(appAssembly),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The child for one file, plus the safety arguments (their folder under <paramref name="sandbox"/>).</summary>
    public static ChildCommand For(string path, int maxChars, string sandbox)
    {
        var command = IsolatedTextExtractor.ChildCommandFor(Exe, path, maxChars);
        return command with { Arguments = [.. command.Arguments, .. Safety(sandbox)] };
    }

    /// <summary>Cabinette.exe with exactly these arguments, plus the safety arguments.</summary>
    public static ChildCommand WithArguments(IEnumerable<string> arguments, string sandbox)
        => new(Exe, [.. arguments, .. Safety(sandbox)], Environment.SystemDirectory);

    /// <summary>The data folder a broken child would have created.</summary>
    public static string DataDirectory(string sandbox) => Path.Combine(sandbox, "data");

    public static IsolatedTextExtractor Extractor(string sandbox, TimeSpan? timeout = null)
    {
        EnsureSameBuild();
        return new IsolatedTextExtractor((path, maxChars) => For(path, maxChars, sandbox), timeout, p => IFilterRegistry.FindFilterForPath(p) is not null);
    }

    /// <summary>Runs <paramref name="command"/> through the app's runner.</summary>
    public static Task<ChildProcessRunner.Result> RunAsync(ChildCommand command, TimeSpan timeout, long maxOutputBytes = 1 << 20, Action<int>? started = null)
        => ChildProcessRunner.RunAsync(command.Program, command.Arguments, command.WorkingDirectory, timeout, maxOutputBytes, started, CancellationToken.None);

    private static string[] Safety(string sandbox) => ["--data-dir", DataDirectory(sandbox), "--screenshot", Path.Combine(sandbox, "never.png")];
}

/// <summary>A text extractor with the text it gives in memory; counts and records its calls.</summary>
internal sealed class FakeTextExtractor : ITextExtractor
{
    private readonly Lock _gate = new();

    public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".rtf", ".msg" };

    /// <summary>The whole text of every file (by full path); a file without one gives "".</summary>
    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Throw { get; set; }

    public bool ReturnNull { get; set; }

    public List<int> Calls { get; } = [];

    public bool CanExtract(string path) => Extensions.Contains(Path.GetExtension(path));

    public Task<ExtractedText?> ExtractAsync(string path, int maxChars, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add(maxChars);
        }

        if (Throw is not null)
        {
            return Task.FromException<ExtractedText?>(Throw);
        }

        if (ReturnNull)
        {
            return Task.FromResult<ExtractedText?>(null);
        }

        var text = Texts.GetValueOrDefault(Path.GetFullPath(path), "");
        return Task.FromResult<ExtractedText?>(text.Length > maxChars
            ? new ExtractedText(text[..maxChars], Truncated: true)
            : new ExtractedText(text, Truncated: false));
    }
}
