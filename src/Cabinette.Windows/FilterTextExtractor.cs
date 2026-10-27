using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Cabinette.Windows.Interop;

namespace Cabinette.Windows;

/// <summary>
/// Pulls a document's text through its IFilter <b>in this process</b>, on a fresh STA thread per call. IFilters are
/// third-party in-process DLLs (Adobe, Foxit, Office) that can crash or hang, which is why Windows Search runs them in
/// SearchFilterHost.exe: Cabinette runs this only in its hidden child process (<c>Cabinette.exe --extract-text</c>), never
/// in the app's own process. A filter that hangs can't be stopped from here; the child process is killed instead.
/// </summary>
public sealed class FilterTextExtractor : ITextExtractor
{
    /// <summary>Characters asked of GetText at a time.</summary>
    private const int BufferChars = 4096;

    /// <summary>A filter that keeps returning empty text is given up on after this many calls in a row.</summary>
    private const int MaxEmptyReads = 16;

    /// <summary>A filter that returns chunks without end (a broken one) is stopped after this many.</summary>
    private const int MaxChunks = 4_000_000;

    private static readonly IFILTER_INIT InitFlags = IFILTER_INIT.CanonParagraphs | IFILTER_INIT.HardLineBreaks |
        IFILTER_INIT.CanonHyphens | IFILTER_INIT.CanonSpaces | IFILTER_INIT.ApplyIndexAttributes;

    public bool CanExtract(string path) => IFilterRegistry.FindFilterForPath(path) is not null;

    public Task<ExtractedText?> ExtractAsync(string path, int maxChars, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<ExtractedText?>(ct);
        }

        var filter = IFilterRegistry.FindFilterForPath(path);
        if (filter is null)
        {
            return Task.FromResult<ExtractedText?>(null);
        }

        var completion = new TaskCompletionSource<ExtractedText?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Extract(path, filter.Value, maxChars, ct));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Cabinette IFilter",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// Keeps Windows from showing error dialogs for this process (a filter that crashes, a missing disk). For the hidden
    /// child process that runs the filters: nothing it does may appear on the desktop.
    /// </summary>
    public static void SuppressErrorDialogs()
        => FilterNativeMethods.SetErrorMode(FilterNativeMethods.SemFailCriticalErrors | FilterNativeMethods.SemNoGpFaultErrorBox |
                                            FilterNativeMethods.SemNoOpenFileErrorBox);

    /// <summary>Runs the filter on the calling thread (which must be STA).</summary>
    internal static ExtractedText Extract(string path, Guid filterClsid, int maxChars, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Directory.Exists(fullPath)
                ? new IOException($"{fullPath} is a folder, not a file.")
                : new FileNotFoundException("The file doesn't exist.", fullPath);
        }

        var kind = Path.GetExtension(fullPath).ToLowerInvariant();
        var hr = FilterNativeMethods.CoCreateInstance(filterClsid, IntPtr.Zero, FilterNativeMethods.ClsctxInprocServer,
            FilterNativeMethods.IidIUnknown, out var instance);
        if (hr < 0 || instance is null)
        {
            throw new InvalidDataException($"The text extractor for {kind} files could not be started (0x{hr:X8}).");
        }

        IStream? stream = null;
        try
        {
            if (instance is not IFilter filter)
            {
                throw new InvalidDataException($"The text extractor registered for {kind} files is not an IFilter.");
            }

            stream = Load(instance, fullPath, kind);
            hr = filter.Init(InitFlags, 0, IntPtr.Zero, out _);
            if (hr < 0)
            {
                throw Failure(hr, fullPath, kind);
            }

            return Pull(filter, maxChars, fullPath, kind, ct);
        }
        finally
        {
            if (stream is not null && Marshal.IsComObject(stream))
            {
                Marshal.FinalReleaseComObject(stream);
            }

            if (Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    /// <summary>
    /// Gives the filter the file: through a stream (IPersistStream, what Windows Search uses since Windows 7), else by
    /// path (IPersistFile, older filters). Returns the stream to release afterwards.
    /// </summary>
    private static IStream? Load(object instance, string fullPath, string kind)
    {
        if (instance is IPersistStream persistStream)
        {
            var hr = FilterNativeMethods.SHCreateStreamOnFileEx(fullPath, FilterNativeMethods.StgmRead | FilterNativeMethods.StgmShareDenyNone,
                0, false, IntPtr.Zero, out var stream);
            if (hr < 0 || stream is null)
            {
                throw Failure(hr, fullPath, kind);
            }

            hr = persistStream.Load(stream);
            if (hr >= 0)
            {
                return stream;
            }

            Marshal.FinalReleaseComObject(stream);
            if (hr is not (FilterHResults.NotImplemented or FilterHResults.NoInterface) || instance is not IPersistFileNative)
            {
                throw Failure(hr, fullPath, kind);
            }
        }

        if (instance is IPersistFileNative persistFile)
        {
            var hr = persistFile.Load(fullPath, FilterNativeMethods.StgmRead | FilterNativeMethods.StgmShareDenyNone);
            if (hr < 0)
            {
                throw Failure(hr, fullPath, kind);
            }

            return null;
        }

        throw new InvalidDataException($"The text extractor for {kind} files can't open files.");
    }

    private static ExtractedText Pull(IFilter filter, int maxChars, string fullPath, string kind, CancellationToken ct)
    {
        // In long: maxChars + 1 wraps for int.MaxValue.
        var text = new StringBuilder((int)Math.Min(maxChars + 1L, 64 * 1024));
        var buffer = new char[BufferChars];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var address = pin.AddrOfPinnedObject();
            var chunks = 0;
            while (text.Length <= maxChars && ++chunks <= MaxChunks)
            {
                ct.ThrowIfCancellationRequested();
                var hr = filter.GetChunk(out var chunk);
                if (hr == FilterHResults.EndOfChunks)
                {
                    break;
                }

                if (hr is FilterHResults.EmbeddingUnavailable or FilterHResults.LinkUnavailable)
                {
                    continue;
                }

                if (hr < 0)
                {
                    // A filter that fails part way still gave what it had; one that fails at once rejects the file.
                    if (text.Length > 0)
                    {
                        break;
                    }

                    throw Failure(hr, fullPath, kind);
                }

                if ((chunk.flags & CHUNKSTATE.Text) == 0)
                {
                    continue;
                }

                var before = text.Length;
                AppendBreak(text, chunk.breakType);
                var lengthWithBreak = text.Length;
                var emptyReads = 0;
                while (text.Length <= maxChars)
                {
                    var count = (uint)BufferChars;
                    hr = filter.GetText(ref count, address);
                    if (hr < 0)
                    {
                        // FILTER_E_NO_MORE_TEXT ends the chunk normally; any other error ends it too.
                        break;
                    }

                    count = Math.Min(count, (uint)BufferChars);
                    AppendText(text, buffer.AsSpan(0, (int)count));
                    if (hr == FilterHResults.LastText)
                    {
                        break;
                    }

                    if (count == 0 && ++emptyReads >= MaxEmptyReads)
                    {
                        break;
                    }
                }

                if (text.Length == lengthWithBreak)
                {
                    // A text chunk without text: no break for it either.
                    text.Length = before;
                }
            }
        }
        finally
        {
            pin.Free();
        }

        var truncated = text.Length > maxChars;
        if (truncated)
        {
            // Never end on half of a surrogate pair.
            var cut = maxChars;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            text.Length = cut;
        }

        return new ExtractedText(text.ToString(), truncated);
    }

    /// <summary>Separates a chunk from the text before it, as its break type says.</summary>
    private static void AppendBreak(StringBuilder text, CHUNK_BREAKTYPE breakType)
    {
        if (text.Length == 0)
        {
            return;
        }

        var last = text[^1];
        switch (breakType)
        {
            case CHUNK_BREAKTYPE.EndOfWord:
            case CHUNK_BREAKTYPE.EndOfSentence:
                if (!char.IsWhiteSpace(last))
                {
                    text.Append(' ');
                }

                break;
            case CHUNK_BREAKTYPE.EndOfParagraph:
            case CHUNK_BREAKTYPE.EndOfChapter:
                if (last != '\n')
                {
                    text.Append('\n');
                }

                break;
        }
    }

    /// <summary>Appends filter text without NUL characters (some filters pad their buffers with them).</summary>
    private static void AppendText(StringBuilder text, ReadOnlySpan<char> chars)
    {
        while (!chars.IsEmpty)
        {
            var nul = chars.IndexOf('\0');
            if (nul < 0)
            {
                text.Append(chars);
                return;
            }

            text.Append(chars[..nul]);
            chars = chars[(nul + 1)..];
        }
    }

    private static Exception Failure(int hr, string fullPath, string kind)
    {
        if (FilterHResults.IsFileAccessError(hr))
        {
            return new IOException($"{Path.GetFileName(fullPath)} can't be read (0x{hr:X8}).", hr);
        }

        return hr switch
        {
            FilterHResults.Password => new InvalidDataException($"{Path.GetFileName(fullPath)} is protected by a password."),
            FilterHResults.UnknownFormat => new InvalidDataException($"The text extractor for {kind} files doesn't recognize the format of {Path.GetFileName(fullPath)}."),
            _ => new InvalidDataException($"The text extractor for {kind} files couldn't read {Path.GetFileName(fullPath)} (0x{hr:X8})."),
        };
    }
}
