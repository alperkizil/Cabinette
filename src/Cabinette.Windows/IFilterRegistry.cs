using System.IO;
using Microsoft.Win32;

namespace Cabinette.Windows;

/// <summary>
/// Finds the IFilter Windows Search uses for a file type, the way Windows looks it up: the extension's
/// <c>PersistentHandler</c> (directly, or through its ProgID's CLSID), then that handler's IFilter add-in. Reads the
/// registry only; never loads a filter. Safe on any thread.
/// </summary>
public static class IFilterRegistry
{
    /// <summary>IID_IFilter: the add-in key under a persistent handler.</summary>
    private const string IFilterAddinKey = "{89BCB740-6119-101A-BCB7-00DD010655AF}";

    /// <summary>The "null" persistent handler and filter (query.dll): properties only, never any text (mp3, jpg, ...).</summary>
    public static readonly Guid NullPersistentHandler = new("098F2470-BAE0-11CD-B579-08002B30BFEB");

    public static readonly Guid NullFilter = new("C3278E90-BEA7-11CD-B579-08002B30BFEB");

    /// <summary>The plain-text filter (query.dll), registered for txt, csv, cs and other text types.</summary>
    public static readonly Guid PlainTextFilter = new("C1243CA0-BF96-11CD-B579-08002B30BFEB");

    /// <summary>
    /// The CLSID of the IFilter registered for the extension (".pdf" or "pdf"), or null when there is none, it is the
    /// null filter (no text), or its DLL is gone.
    /// </summary>
    public static Guid? FindFilter(string? extension)
    {
        var ext = NormalizeExtension(extension);
        if (ext is null)
        {
            return null;
        }

        try
        {
            var handler = FindPersistentHandler(ext);
            if (handler is null || handler == NullPersistentHandler)
            {
                return null;
            }

            var filter = ReadGuid($@"CLSID\{handler.Value:B}\PersistentAddinsRegistered\{IFilterAddinKey}");
            if (filter is null || filter == NullFilter || !InprocServerExists(filter.Value))
            {
                return null;
            }

            return filter;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The CLSID of the IFilter for a file, by its extension (see <see cref="FindFilter"/>).</summary>
    public static Guid? FindFilterForPath(string? path)
        => string.IsNullOrEmpty(path) ? null : FindFilter(Path.GetExtension(path));

    /// <summary>The filter's DLL as registered (environment variables expanded), or null.</summary>
    public static string? FindFilterDll(Guid filter)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{filter:B}\InprocServer32");
        return key?.GetValue(null) is string { Length: > 0 } dll ? Environment.ExpandEnvironmentVariables(dll.Trim('"')) : null;
    }

    private static Guid? FindPersistentHandler(string ext)
    {
        var direct = ReadGuid($@"{ext}\PersistentHandler");
        if (direct is not null)
        {
            return direct;
        }

        // Through the ProgID: .ext → ProgID → CLSID → PersistentHandler.
        if (ReadString(ext) is not { Length: > 0 } progId || progId.Contains('\\'))
        {
            return null;
        }

        var clsid = ReadGuid($@"{progId}\CLSID");
        return clsid is null ? null : ReadGuid($@"CLSID\{clsid.Value:B}\PersistentHandler");
    }

    private static bool InprocServerExists(Guid filter)
    {
        var dll = FindFilterDll(filter);
        if (dll is null)
        {
            return false;
        }

        // A bare name ("query.dll") is found on the DLL search path; a full path must still be there (an uninstalled
        // program can leave its registration behind).
        return !Path.IsPathRooted(dll) || File.Exists(dll);
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var trimmed = extension.Trim().TrimStart('*').TrimStart('.');
        if (trimmed.Length == 0 || trimmed.IndexOfAny(['\\', '/', '.', ' ']) >= 0)
        {
            return null;
        }

        return "." + trimmed.ToLowerInvariant();
    }

    private static string? ReadString(string subKey)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(subKey);
        return key?.GetValue(null) as string;
    }

    private static Guid? ReadGuid(string subKey)
        => ReadString(subKey) is { } text && Guid.TryParse(text.Trim(), out var guid) && guid != Guid.Empty ? guid : null;
}
