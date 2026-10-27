using System.IO;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// Reads library files for the read_file tool (SPEC.md section 10, "Reading contents"), always off the UI thread:
/// <list type="bullet">
/// <item>Text (the Text, Code, Notes and Links types, csv, tsv, json, xml, svg and the like, and any file that looks like
/// text): pages of bytes as they are on disk (<see cref="TextFilePages"/>).</item>
/// <item>Documents with an IFilter (PDF, Office, ...): pages of extracted characters, through
/// <see cref="ITextExtractor"/> (in the app, a child process), with the last extractions cached.</item>
/// <item>Images: scaled down to <see cref="AiProtocol.MaxImageEdgePixels"/> (<see cref="ImageContent"/>).</item>
/// <item>Everything else is refused with a message for the AI.</item>
/// </list>
/// </summary>
public sealed class FileContentReader : IFileContentReader
{
    /// <summary>Types whose files are never text: refused without looking inside.</summary>
    private static readonly HashSet<string> NeverText = new(StringComparer.Ordinal)
    {
        "audio", "video", "archives", "executable", "diskimages", "fonts",
    };

    /// <summary>
    /// Extensions whose files are always binary, although their type (Links, Code) is otherwise text: refused with what
    /// they are, never sniffed (a shortcut or class file isn't "a text file with binary content").
    /// </summary>
    private static readonly HashSet<string> AlwaysBinary = new(StringComparer.Ordinal)
    {
        "lnk", "class", "pyc", "pyo",
    };

    /// <summary>Types read as text by their extension (a binary file among them is refused, never extracted).</summary>
    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal)
    {
        "text", "code", ExtensionMap.NotesKey, ExtensionMap.LinksKey,
    };

    /// <summary>Text formats whose type is something else (spreadsheets, design) or "other".</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        "csv", "tsv", "json", "xml", "svg", "ini", "cfg", "conf", "properties", "env", "tex", "bib", "rst", "srt", "vtt",
        "ics", "vcf", "eml", "reg", "inf", "diff", "patch", "xsd", "xsl", "xslt", "dtd", "rss", "atom", "gpx", "kml",
        "m3u", "m3u8", "cue", "gitignore", "gitattributes", "editorconfig",
    };

    /// <summary>Types with an IFilter normally: extracted first, read as text only when no filter is installed.</summary>
    private static readonly HashSet<string> DocumentTypes = new(StringComparer.Ordinal)
    {
        "documents", "spreadsheets", "presentations", "ebooks",
    };

    private readonly ITextExtractor _extractor;
    private readonly ExtractedTextCache _cache = new();
    private readonly int _maxTextPageBytes;
    private readonly int _maxTextPageChars;
    private readonly int _maxImageEdge;

    public FileContentReader(ITextExtractor extractor)
        : this(extractor, AiProtocol.MaxTextPageBytes, AiProtocol.MaxTextPageChars, AiProtocol.MaxImageEdgePixels)
    {
    }

    /// <summary>For tests: smaller pages.</summary>
    internal FileContentReader(ITextExtractor extractor, int maxTextPageBytes, int maxTextPageChars, int maxImageEdge)
    {
        _extractor = extractor;
        _maxTextPageBytes = maxTextPageBytes;
        _maxTextPageChars = maxTextPageChars;
        _maxImageEdge = maxImageEdge;
    }

    internal ExtractedTextCache Cache => _cache;

    public Task<ReadFileResult> ReadAsync(LibraryItem item, long offset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(() => ReadCoreAsync(item, offset, ct), ct);
    }

    private async Task<ReadFileResult> ReadCoreAsync(LibraryItem item, long offset, CancellationToken ct)
    {
        var path = item.FullPath;
        var name = string.IsNullOrEmpty(item.FileName) ? Path.GetFileName(path) : item.FileName;
        var extension = ExtensionMap.Normalize(item.Extension.Length > 0 ? item.Extension : Path.GetExtension(path));
        var type = ExtensionMap.GetTypeKey(item.Kind, extension);
        var info = ExistingFile(path);

        if (type == ExtensionMap.ImagesKey)
        {
            return await ImageContent.ReadAsync(path, name, extension, _maxImageEdge, ct).ConfigureAwait(false);
        }

        if (NeverText.Contains(type))
        {
            throw new FileContentRefusedException(FileContentMessages.NotReadable(name, type, extension));
        }

        // Notes and links that Cabinette wrote are its own text files, whatever their extension.
        if (item.Kind == ItemKind.File && AlwaysBinary.Contains(extension))
        {
            throw new FileContentRefusedException(FileContentMessages.AlwaysBinary(name, extension));
        }

        if (item.Kind is ItemKind.Note or ItemKind.Link || TextTypes.Contains(type) || TextExtensions.Contains(extension))
        {
            return ReadText(path, offset) ?? throw new FileContentRefusedException(FileContentMessages.BinaryDespiteExtension(name));
        }

        // Documents: their IFilter first (an RTF file is text, but its words are what the AI wants). Other types (design,
        // 3D models, anything else): as text when they look like text, else their IFilter if there is one.
        if (DocumentTypes.Contains(type) && _extractor.CanExtract(path))
        {
            return await ReadExtractedAsync(path, name, extension, info, offset, ct).ConfigureAwait(false);
        }

        if (ReadText(path, offset) is { } text)
        {
            return text;
        }

        if (!DocumentTypes.Contains(type) && _extractor.CanExtract(path))
        {
            return await ReadExtractedAsync(path, name, extension, info, offset, ct).ConfigureAwait(false);
        }

        throw new FileContentRefusedException(FileContentMessages.NotReadable(name, type, extension));
    }

    /// <summary>The page, or null when the file isn't text.</summary>
    private ReadFileResult? ReadText(string path, long offset)
    {
        try
        {
            using var stream = TextFilePages.Open(path);
            var format = TextFilePages.Sniff(stream);
            return format is null ? null : TextFilePages.ReadPage(stream, format, offset, _maxTextPageBytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    private async Task<ReadFileResult> ReadExtractedAsync(string path, string name, string extension, FileInfo info, long offset, CancellationToken ct)
    {
        var start = Math.Max(0, offset);
        if (start >= ExtractedTextPages.MaxExtractChars)
        {
            throw new FileContentRefusedException(FileContentMessages.BeyondExtractionLimit(name, ExtractedTextPages.MaxExtractChars));
        }

        var key = new ExtractionKey(info.FullName, info.Length, info.LastWriteTimeUtc);
        var cached = _cache.Get(key);
        var extracted = cached;
        if (extracted is null || !ExtractedTextPages.Covers(extracted, start, _maxTextPageChars))
        {
            var maxChars = ExtractedTextPages.CharsToExtract(start, _maxTextPageChars, cached);
            try
            {
                extracted = await _extractor.ExtractAsync(path, maxChars, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(ex.Message, ex);
            }
            catch (InvalidDataException ex)
            {
                throw new FileContentRefusedException(FileContentMessages.ExtractionFailed(name, ex.Message));
            }
            catch (TimeoutException)
            {
                throw new FileContentRefusedException(FileContentMessages.ExtractionTimedOut(name, extension));
            }

            if (extracted is null)
            {
                throw new FileContentRefusedException(FileContentMessages.NoExtractor(name, extension));
            }

            _cache.Put(key, extracted);
        }

        return ExtractedTextPages.Page(extracted, start, _maxTextPageChars);
    }

    /// <summary>The file, which must exist (else <see cref="FileNotFoundException"/>, an <see cref="IOException"/>).</summary>
    private static FileInfo ExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException("The file has no path.");
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return info;
            }

            if (Directory.Exists(path))
            {
                throw new IOException($"{path} is a folder, not a file.");
            }

            throw new FileNotFoundException("The file doesn't exist.", path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
