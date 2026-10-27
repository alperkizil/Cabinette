using System.Text;

namespace Cabinette.Ipc;

/// <summary>Reads UTF-8 text lines (terminated by \n, optional \r) from a stream, with a length limit.</summary>
public sealed class LineReader
{
    private readonly Stream _stream;
    private readonly int _maxLineLength;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly byte[] _bytes = new byte[4096];
    private readonly char[] _chars;
    private readonly StringBuilder _buffer = new();
    private bool _eof;
    private int _scanned;

    public LineReader(Stream stream, int maxLineLength)
    {
        _stream = stream;
        _maxLineLength = maxLineLength;
        _chars = new char[Encoding.UTF8.GetMaxCharCount(_bytes.Length)];
    }

    /// <summary>The next line without its terminator, or null at the end of the stream.</summary>
    /// <exception cref="InvalidDataException">A line is longer than the limit.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var newline = IndexOfNewline();
            if (newline >= 0)
            {
                var line = _buffer.ToString(0, newline);
                _buffer.Remove(0, newline + 1);
                _scanned = 0;
                return line.EndsWith('\r') ? line[..^1] : line;
            }

            if (_buffer.Length > _maxLineLength)
            {
                throw new InvalidDataException("Line too long.");
            }

            if (_eof)
            {
                if (_buffer.Length == 0)
                {
                    return null;
                }

                var rest = _buffer.ToString();
                _buffer.Clear();
                return rest.EndsWith('\r') ? rest[..^1] : rest;
            }

            var read = await _stream.ReadAsync(_bytes, ct).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                var tail = _decoder.GetChars([], 0, 0, _chars, 0, flush: true);
                _buffer.Append(_chars, 0, tail);
                continue;
            }

            var count = _decoder.GetChars(_bytes, 0, read, _chars, 0, flush: false);
            _buffer.Append(_chars, 0, count);
        }
    }

    private int IndexOfNewline()
    {
        for (var i = _scanned; i < _buffer.Length; i++)
        {
            if (_buffer[i] == '\n')
            {
                return i;
            }
        }

        _scanned = _buffer.Length;
        return -1;
    }
}
