using System.Collections.Concurrent;
using System.Globalization;
using Cabinette.Core.Abstractions;

namespace Cabinette.Core.Tests.Infrastructure;

/// <summary>
/// Reads a "date taken" from the test file's text: a file whose content starts with <c>taken:</c> and an ISO time has that date,
/// any other file has none. Records every path it was asked about, and can be slowed down or held.
/// </summary>
public sealed class FakeDateTakenReader : IDateTakenReader
{
    private readonly ConcurrentQueue<string> _reads = new();

    /// <summary>The content of a test file with <paramref name="taken"/> as its date taken.</summary>
    public static string Photo(DateTimeOffset taken, string extra = "")
        => "taken:" + taken.ToString("O", CultureInfo.InvariantCulture) + "\n" + extra;

    /// <summary>Paths read so far, in order.</summary>
    public IReadOnlyList<string> Reads => [.. _reads];

    public int ReadCount => _reads.Count;

    /// <summary>Each read takes this long.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>While set (not signalled), reads wait for it (at most a minute, so a broken test can't hang).</summary>
    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>Signalled when a read starts.</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    /// <summary>Runs at the start of every read, with the path.</summary>
    public Action<string>? OnRead { get; set; }

    public int ReadsOf(string path) => _reads.Count(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    public DateTimeOffset? ReadDateTaken(string path)
    {
        _reads.Enqueue(path);
        OnRead?.Invoke(path);
        Entered.Release();
        Gate?.Wait(TimeSpan.FromMinutes(1));
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        try
        {
            var text = File.ReadAllText(path);
            if (!text.StartsWith("taken:", StringComparison.Ordinal))
            {
                return null;
            }

            var end = text.IndexOf('\n', StringComparison.Ordinal);
            return DateTimeOffset.Parse(text[6..(end < 0 ? text.Length : end)], CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
