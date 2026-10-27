using Microsoft.Extensions.Logging;

namespace Cabinette.Mcp;

/// <summary>
/// cabinette-mcp.exe's only log: warnings and errors, one line each, on stderr (stdout is the MCP channel). Lines carry
/// the message and the exception's type and message, never a stack, tool arguments or file contents. Claude Code and
/// Codex keep a server's stderr in their own logs.
/// </summary>
internal sealed class StderrLog : ILoggerFactory, ILoggerProvider
{
    private readonly TextWriter _writer;
    private readonly LogLevel _minimum;
    private readonly CancellationToken _stopping;

    /// <param name="stopping">
    /// The server's stop token. Once it is cancelled (the client closed stdin, or Ctrl+C), the cancellations it causes
    /// are expected: lines about an <see cref="OperationCanceledException"/> (the SDK reports each cancelled tool call
    /// as a failed handler) are left out.
    /// </param>
    public StderrLog(TextWriter writer, LogLevel minimum = LogLevel.Warning, CancellationToken stopping = default)
    {
        _writer = writer;
        _minimum = minimum;
        _stopping = stopping;
    }

    public void Warn(string message) => Write(LogLevel.Warning, message, null);

    public void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _minimum || level == LogLevel.None || (exception is OperationCanceledException && _stopping.IsCancellationRequested))
        {
            return;
        }

        var label = level switch
        {
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => level.ToString().ToLowerInvariant(),
        };
        var line = exception is null ? $"cabinette-mcp {label}: {message}" : $"cabinette-mcp {label}: {message} ({exception.GetType().Name}: {exception.Message})";
        try
        {
            lock (_writer)
            {
                _writer.WriteLine(line.ReplaceLineEndings(" "));
                _writer.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nobody reads stderr any more: nothing to do.
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(StderrLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= log._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                log.Write(logLevel, formatter(state, exception), exception);
            }
        }
    }
}
