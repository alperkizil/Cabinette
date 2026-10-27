namespace Cabinette.Core.Abstractions;

/// <summary>Platform service that moves files or folders to the Windows Recycle Bin (implemented in Cabinette.Windows).</summary>
public interface IRecycleBin
{
    /// <summary>Moves the given files/folders to the Recycle Bin without any UI. Paths that don't exist are ignored.</summary>
    /// <exception cref="RecycleBinUnavailableException">When some paths could only be deleted permanently (nothing is deleted for them).</exception>
    /// <exception cref="IOException">When the shell reports another failure.</exception>
    void SendToRecycleBin(IReadOnlyCollection<string> paths);

    /// <summary>
    /// True if <paramref name="path"/> can go to the Recycle Bin: false when its drive has none (network shares, most USB sticks
    /// and drives where it is switched off) or when the file is larger than that drive's Recycle Bin maximum size. Asked once per
    /// file. A best-effort check made before deleting: <see cref="SendToRecycleBin"/> can still throw
    /// <see cref="RecycleBinUnavailableException"/>, for example when the size limit is unknown.
    /// </summary>
    bool CanRecycle(string path) => true;
}

/// <summary>Thrown by <see cref="IRecycleBin.SendToRecycleBin"/> when some items could only be deleted permanently. Those items are left in place.</summary>
public sealed class RecycleBinUnavailableException : IOException
{
    public RecycleBinUnavailableException(IReadOnlyList<string> paths, string message)
        : base(message)
    {
        Paths = paths;
    }

    /// <summary>The items that were not deleted because they cannot go to the Recycle Bin.</summary>
    public IReadOnlyList<string> Paths { get; }
}
