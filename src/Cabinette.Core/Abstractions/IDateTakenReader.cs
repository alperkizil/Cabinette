namespace Cabinette.Core.Abstractions;

/// <summary>
/// Platform service that reads when a photo or video was taken from the file's own metadata (implemented in Cabinette.Windows
/// through the Windows property system). Used by the Gallery's "Date taken" (SPEC.md section 4, "Gallery").
/// </summary>
public interface IDateTakenReader
{
    /// <summary>
    /// The date taken, or null when the file has none or can't be read (missing, locked, unsupported). Must not decode the image,
    /// must not throw for those cases, and is called on background threads.
    /// </summary>
    DateTimeOffset? ReadDateTaken(string path);
}
