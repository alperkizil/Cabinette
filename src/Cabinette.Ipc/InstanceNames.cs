using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Cabinette.Ipc;

/// <summary>Names of the single-instance mutex and the named pipe of one Cabinette (one user, one data folder).</summary>
/// <param name="BaseName"><c>Cabinette-&lt;user SID&gt;-&lt;hash8 of the data folder&gt;</c>.</param>
public sealed record InstanceIdentity(string BaseName)
{
    /// <summary>Session-local mutex name.</summary>
    public string MutexName => @"Local\" + BaseName;

    /// <summary>Pipe name (without the <c>\\.\pipe\</c> prefix).</summary>
    public string PipeName => BaseName;
}

/// <summary>
/// Derives the single-instance names. They include the user's SID (other accounts never collide) and a short hash of the
/// data folder, so a dev run with <c>--data-dir</c> never talks to the user's real running copy.
/// </summary>
public static class InstanceNames
{
    public static InstanceIdentity ForCurrentUser(string dataDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        return For(sid, dataDirectory);
    }

    public static InstanceIdentity For(string userSid, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        return new InstanceIdentity($"Cabinette-{userSid}-{Hash8(NormalizeDirectory(dataDirectory))}");
    }

    /// <summary>Full path, no trailing separator, upper case (Windows paths are case-insensitive).</summary>
    public static string NormalizeDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory.Trim().Trim('"')));
        return full.ToUpperInvariant();
    }

    /// <summary>First 8 hex digits (lower case) of the SHA-256 of the text.</summary>
    public static string Hash8(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
