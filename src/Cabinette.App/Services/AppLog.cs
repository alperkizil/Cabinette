using System.Globalization;
using System.IO;
using System.Text;

namespace Cabinette.App.Services;

/// <summary>Minimal file log in %LocalAppData%\Cabinette\logs (one file per day). Never throws.</summary>
public static class AppLog
{
    private static readonly Lock Gate = new();
    private static string? _directory;

    public static string? Directory => _directory;

    public static void Initialize(string logDirectory)
    {
        _directory = logDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(logDirectory);
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var directory = _directory;
        if (directory is null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(message);
        if (ex is not null)
        {
            builder.AppendLine().Append(ex);
        }

        builder.AppendLine();

        try
        {
            var file = Path.Combine(directory, $"cabinette-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(file, builder.ToString());
            }
        }
        catch (Exception)
        {
            // Ignore: disk full, folder removed, ...
        }
    }
}
