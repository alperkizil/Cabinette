using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Services.Ai;

/// <summary>Which version of a file an extraction came from: its path, size and last write time.</summary>
internal readonly record struct ExtractionKey(string Path, long Length, DateTime LastWriteUtc)
{
    public bool Equals(ExtractionKey other)
        => string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) && Length == other.Length && LastWriteUtc == other.LastWriteUtc;

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), Length, LastWriteUtc);
}

/// <summary>
/// The last few extractions in memory, so that reading a document page by page doesn't run its IFilter again for every
/// page. Small and bounded: at most <see cref="MaxEntries"/> files and <see cref="MaxTotalChars"/> characters (the newest
/// entry is always kept). A file that changed (size or time) is a different key. Thread-safe.
/// </summary>
internal sealed class ExtractedTextCache
{
    public const int MaxEntries = 4;

    public const int MaxTotalChars = 16 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly LinkedList<(ExtractionKey Key, ExtractedText Text)> _entries = new();

    public ExtractedText? Get(ExtractionKey key)
    {
        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Key.Equals(key))
                {
                    _entries.Remove(node);
                    _entries.AddFirst(node);
                    return node.Value.Text;
                }
            }

            return null;
        }
    }

    public void Put(ExtractionKey key, ExtractedText text)
    {
        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Key.Path, key.Path, StringComparison.OrdinalIgnoreCase))
                {
                    // One entry per path: an older version of the file is never read again.
                    _entries.Remove(node);
                    break;
                }
            }

            _entries.AddFirst((key, text));
            var total = _entries.Sum(e => (long)e.Text.Text.Length);
            while (_entries.Count > 1 && (_entries.Count > MaxEntries || total > MaxTotalChars))
            {
                total -= _entries.Last!.Value.Text.Text.Length;
                _entries.RemoveLast();
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }
}

/// <summary>Pages of extracted text (PDF, Office): <see cref="AiProtocol.MaxTextPageChars"/> characters from a char offset.</summary>
internal static class ExtractedTextPages
{
    /// <summary>
    /// Text beyond this many characters of a document is never extracted (64 pages of 256 K characters; a 1,000-page
    /// book has about 3 M). Keeps the child process, the pipe and the cache within tens of megabytes.
    /// </summary>
    public const int MaxExtractChars = 16 * 1024 * 1024;

    /// <summary>
    /// How many characters to extract to serve a page from <paramref name="offset"/>: at least the page, and twice what
    /// was extracted before (so a long document is extracted a few times while it is paged through, not once per page).
    /// </summary>
    public static int CharsToExtract(long offset, int pageChars, ExtractedText? previous)
    {
        var needed = Math.Min(Math.Max(0, offset) + pageChars, MaxExtractChars);
        var doubled = previous is { Truncated: true } ? Math.Min(2L * previous.Text.Length, MaxExtractChars) : 0;
        return (int)Math.Max(needed, doubled);
    }

    /// <summary>True when <paramref name="text"/> has what a page from <paramref name="offset"/> needs.</summary>
    public static bool Covers(ExtractedText text, long offset, int pageChars)
        => !text.Truncated || text.Text.Length >= Math.Min(Math.Max(0, offset) + pageChars, MaxExtractChars);

    /// <summary>
    /// The page from <paramref name="offset"/>: at most <paramref name="pageChars"/> characters, never splitting a
    /// surrogate pair (an offset on the second half moves to the next character).
    /// </summary>
    public static ReadFileResult Page(ExtractedText extracted, long offset, int pageChars = AiProtocol.MaxTextPageChars)
    {
        var text = extracted.Text;
        var length = text.Length;
        var start = Math.Max(0, offset);
        long? total = extracted.Truncated ? null : length;
        if (start < length && start > 0 && char.IsLowSurrogate(text[(int)start]) && char.IsHighSurrogate(text[(int)start - 1]))
        {
            start++;
        }

        if (start >= length)
        {
            // Past the end (or the end of what Cabinette extracts at most).
            return Result("", start, next: null, total);
        }

        var s = (int)start;
        var e = (int)Math.Min((long)s + pageChars, length);

        // Never end on the first half of a pair: its second half is on the next page (or not extracted yet).
        if (e - 1 > s && char.IsHighSurrogate(text[e - 1]) && (e < length ? char.IsLowSurrogate(text[e]) : extracted.Truncated))
        {
            e--;
        }

        var more = e < length || extracted.Truncated;
        return Result(text[s..e], s, more ? e : null, total);
    }

    private static ReadFileResult Result(string text, long offset, long? next, long? total) => new()
    {
        Kind = "text",
        Text = text,
        Offset = offset,
        NextOffset = next,
        Total = total,
        Unit = "chars",
        Source = "extractor",
    };
}
