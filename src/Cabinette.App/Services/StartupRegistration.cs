using System.IO;
using Microsoft.Win32;

namespace Cabinette.App.Services;

/// <summary>The "start with Windows" entry (HKCU\...\Run, value "Cabinette").</summary>
public interface IStartupRegistration
{
    /// <summary>The current command line, or null when there is no entry.</summary>
    string? Read();

    void Write(string command);

    void Remove();
}

/// <summary>The Run-key command line: <c>"&lt;full path to Cabinette.exe&gt;" --tray</c>.</summary>
public static class StartupCommand
{
    public const string ValueName = "Cabinette";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Build(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var path = executablePath.Trim().Trim('"');
        return $"\"{path}\" {CommandLineOptions.TrayFlag}";
    }

    /// <summary>
    /// True when <paramref name="existing"/> already starts this executable in tray mode (case and extra spaces don't
    /// matter; a missing <c>--tray</c>, another path or another program does).
    /// </summary>
    public static bool IsCurrent(string? existing, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return false;
        }

        var (path, arguments) = Split(existing);
        if (!string.Equals(Normalize(path), Normalize(executablePath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return args.Length == 1 && string.Equals(args[0], CommandLineOptions.TrayFlag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits a command line into the program path and the rest.</summary>
    public static (string Path, string Arguments) Split(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? (text[1..], "") : (text[1..close], text[(close + 1)..].Trim());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }
}

/// <summary>Writes the real HKCU Run key. Only used by normal runs (never with dev flags).</summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupCommand.RunKeyPath, writable: false);
        return key?.GetValue(StartupCommand.ValueName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupCommand.RunKeyPath, writable: true);
        key.SetValue(StartupCommand.ValueName, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupCommand.RunKeyPath, writable: true);
        key?.DeleteValue(StartupCommand.ValueName, throwOnMissingValue: false);
    }
}

/// <summary>In-memory stand-in for dev runs (<c>--data-dir</c>, <c>--sample-data</c>, <c>--screenshot</c>): the registry is never touched.</summary>
public sealed class DevStartupRegistration : IStartupRegistration
{
    private string? _command;

    public string? Read() => _command;

    public void Write(string command)
    {
        _command = command;
        AppLog.Info($"Dev run: would register for startup: {command}");
    }

    public void Remove()
    {
        _command = null;
        AppLog.Info("Dev run: would remove the startup entry.");
    }
}

/// <summary>Keeps the Run entry in line with the "Start with Windows" setting.</summary>
public sealed class StartupManager
{
    private readonly IStartupRegistration _registration;
    private readonly string _executablePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StartupManager(IStartupRegistration registration, string executablePath)
    {
        _registration = registration;
        _executablePath = executablePath;
    }

    /// <summary>Adds the entry if <paramref name="enabled"/> and it is missing or outdated, removes it otherwise. Runs off the UI thread.</summary>
    public async Task ApplyAsync(bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => Apply(enabled)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Updating the Windows startup entry failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Apply(bool enabled)
    {
        var existing = _registration.Read();
        if (enabled)
        {
            if (!StartupCommand.IsCurrent(existing, _executablePath))
            {
                _registration.Write(StartupCommand.Build(_executablePath));
                AppLog.Info("Registered Cabinette to start with Windows.");
            }
        }
        else if (existing is not null)
        {
            _registration.Remove();
            AppLog.Info("Removed Cabinette from Windows startup.");
        }
    }
}
