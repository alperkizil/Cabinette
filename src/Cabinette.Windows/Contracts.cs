using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Cabinette.Core.Models;

namespace Cabinette.Windows;

/// <summary>Windows shell thumbnails (the same images Explorer shows).</summary>
public interface IThumbnailService
{
    /// <summary>
    /// Thumbnail for images, video, PDF, Office files, etc. as a frozen <see cref="BitmapSource"/>, or null when the shell has none.
    /// Cached in memory and on disk. Safe to call from any thread; never blocks the UI thread.
    /// </summary>
    Task<BitmapSource?> GetThumbnailAsync(string path, int sizePx, CancellationToken ct = default);

    /// <summary>The file-type icon (frozen), used when there is no thumbnail or the file is missing. Accepts a path or just an extension like ".pdf".</summary>
    Task<BitmapSource?> GetFileIconAsync(string pathOrExtension, int sizePx, CancellationToken ct = default);
}

/// <summary>The real Windows classic right-click menu for files, with Cabinette's own items inserted at the top.</summary>
public interface IShellContextMenuService
{
    /// <summary>
    /// Shows the menu modally at <paramref name="screenPointPx"/> (physical pixels) and runs the chosen shell verb.
    /// Returns which Cabinette item was chosen, or an intercepted verb (Delete/Rename) that Cabinette must handle itself
    /// so the library stays in sync. Must be called on the UI thread. If the files don't exist, only the custom items are shown.
    /// </summary>
    ContextMenuResult Show(IntPtr ownerHwnd, IReadOnlyList<string> paths, Point screenPointPx, IReadOnlyList<CustomMenuItem> customItems);
}

public sealed record CustomMenuItem(string Id, string Text)
{
    public static CustomMenuItem Separator { get; } = new("-", "") { IsSeparator = true };

    public bool IsEnabled { get; init; } = true;

    public bool IsSeparator { get; init; }

    public IReadOnlyList<CustomMenuItem>? Children { get; init; }
}

public enum InterceptedShellVerb
{
    None = 0,
    Delete,
    Rename,
}

/// <param name="CustomItemId">Id of the chosen Cabinette item, or null.</param>
/// <param name="Verb">A shell verb Cabinette must handle itself, or None (the shell handled it, or the menu was dismissed).</param>
public sealed record ContextMenuResult(string? CustomItemId, InterceptedShellVerb Verb)
{
    public static ContextMenuResult Dismissed { get; } = new(null, InterceptedShellVerb.None);
}

/// <param name="PageCount">Documents: System.Document.PageCount (PDF, Word, PowerPoint), or null when unknown.</param>
public sealed record MediaInfo(TimeSpan? Duration, int? Width, int? Height, int? PageCount = null);

/// <summary>Reads media properties from the Windows property system (no decoding).</summary>
public interface IMediaInfoService
{
    MediaInfo? GetMediaInfo(string path);
}

/// <summary>Generates default category icons: a colored circle with a white Fluent icon.</summary>
public interface ICategoryIconGenerator
{
    /// <summary>PNG bytes. Unknown icon keys use the folder icon with a color derived from <paramref name="seedText"/> (the category name).</summary>
    byte[] Generate(string? iconKey, string seedText, int sizePx = 256);

    /// <summary>
    /// The circle color <see cref="Generate"/> uses for these arguments (the thread banner's color, SPEC.md section 4). Readable
    /// under white at 3:1 or better. The default is for fakes only.
    /// </summary>
    System.Windows.Media.Color GetColor(string? iconKey, string seedText) => System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x6B);
}

/// <summary>What is on the clipboard, in paste priority order: files, then image, then text.</summary>
public sealed record ClipboardPayload(IReadOnlyList<string>? FilePaths, byte[]? ImagePng, string? Text)
{
    public bool IsEmpty => (FilePaths is null || FilePaths.Count == 0) && ImagePng is null && string.IsNullOrEmpty(Text);
}

public interface IClipboardReader
{
    /// <summary>
    /// Reads the clipboard robustly (PNG, DIB/DIBV5 with alpha, file drops, Unicode text). Call on an STA thread, preferably
    /// not the UI thread: it may wait briefly for the clipboard and decodes/encodes images.
    /// </summary>
    ClipboardPayload Read();
}

/// <summary>
/// The Explorer entries "Copy to Cabinette" and "Add to Cabinette (Pointer)" (SPEC section 9): classic-menu verbs under
/// <c>HKCR\AllFilesystemObjects\shell\&lt;verb&gt;</c> with <c>MultiSelectModel=Player</c> and a <c>DropTarget</c> whose
/// <c>Clsid</c> is served by Cabinette.exe (<c>HKCR\CLSID\{clsid}\LocalServer32</c>). Explorer hands the whole selection
/// to that COM class in one IDropTarget.Drop. The installer writes the registration; the app serves the classes.
/// </summary>
public static class ExplorerIntegration
{
    public const string CopyVerb = "Cabinette.Copy";
    public const string PointerVerb = "Cabinette.Pointer";

    public static readonly Guid CopyDropTargetClsid = new("E92F98C8-F1F4-4B7F-BBBE-38D6678CE55E");
    public static readonly Guid PointerDropTargetClsid = new("ED9B5A7A-4497-43EF-9727-8C6AFCD8CAE2");

    /// <summary>Dev runs (<c>--data-dir</c>) serve these instead, so they never take files meant for the installed Cabinette.</summary>
    public const string DevCopyVerb = "Cabinette.Dev.Copy";
    public const string DevPointerVerb = "Cabinette.Dev.Pointer";

    public static readonly Guid DevCopyDropTargetClsid = new("C2FACF9C-2622-4B9D-8EF9-D096067DA6F4");
    public static readonly Guid DevPointerDropTargetClsid = new("BD7553E4-FBCA-4E73-9B4C-81D9C5477592");
}

/// <summary>One use of an Explorer entry: the whole selection, as one batch.</summary>
/// <param name="Mode">Copy for "Copy to Cabinette", Pointer for "Add to Cabinette (Pointer)", whatever the default add mode is.</param>
/// <param name="Paths">File-system paths of the selected files and folders, in Explorer's order (items without one are skipped).</param>
public sealed record ExplorerDrop(ItemMode Mode, IReadOnlyList<string> Paths);

/// <summary>
/// Serves the Explorer entries' COM classes (<see cref="ExplorerIntegration"/>) from this process while registered:
/// CoRegisterClassObject with CLSCTX_LOCAL_SERVER and REGCLS_MULTIPLEUSE. Use it on the UI (STA) thread only.
/// </summary>
public interface IExplorerDropServer : IDisposable
{
    bool IsRegistered { get; }

    /// <summary>
    /// A selection arrived. Raised after Explorer's Drop call has returned, through the SynchronizationContext of the thread
    /// that called <see cref="Register"/> (the UI thread in the app). Selections without file-system paths are not raised.
    /// </summary>
    event EventHandler<ExplorerDrop>? Dropped;

    /// <summary>Reading a drop failed (Explorer was told so). For logging; raised like <see cref="Dropped"/>.</summary>
    event EventHandler<Exception>? DropFailed;

    /// <summary>Registers both class objects. Throws (and leaves nothing registered) when COM refuses.</summary>
    void Register();

    /// <summary>Revokes the class objects; Explorer then starts or finds another server. Safe to call twice.</summary>
    void Revoke();
}

public interface IShellOperations
{
    void OpenWithDefaultApp(string path);

    /// <summary>Opens Explorer with the file selected.</summary>
    void ShowInExplorer(string path);
}

/// <summary>Text pulled out of a document by its IFilter.</summary>
/// <param name="Text">The text, at most the requested number of characters.</param>
/// <param name="Truncated">The document has more text than was returned.</param>
public sealed record ExtractedText(string Text, bool Truncated);

/// <summary>
/// Windows' own text extractors (IFilters, the ones Windows Search uses) for PDF, Office and other documents: what the AI
/// reads of a non-text file (SPEC.md section 10). Which types work depends on what is installed on the PC.
/// </summary>
public interface ITextExtractor
{
    /// <summary>True when an IFilter is registered for the file's extension (it may still fail on a damaged file).</summary>
    bool CanExtract(string path);

    /// <summary>
    /// The file's text, up to <paramref name="maxChars"/> characters, or null when no IFilter is registered for its type.
    /// Runs the filter off the calling thread (on an STA thread when the filter needs one) and never on the UI thread.
    /// Throws <see cref="IOException"/> when the file can't be read and <see cref="InvalidDataException"/> when the filter
    /// rejects it.
    /// </summary>
    Task<ExtractedText?> ExtractAsync(string path, int maxChars, CancellationToken ct = default);
}
