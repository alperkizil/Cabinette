namespace Cabinette.Core.Models;

/// <summary>How a file is stored in the library.</summary>
public enum ItemMode
{
    /// <summary>The file was copied into the vault. The original stays where it was.</summary>
    Copy = 0,

    /// <summary>The file stays where it is on disk; the library only references it.</summary>
    Pointer = 1,
}

public enum ItemKind
{
    File = 0,

    /// <summary>A text note typed in a thread or pasted as text (.md file in the vault).</summary>
    Note = 1,

    /// <summary>An image pasted from the clipboard (.png file in the vault).</summary>
    PastedImage = 2,

    /// <summary>A pasted web address saved as a .url shortcut file in the vault.</summary>
    Link = 3,
}

/// <summary>Who added an item (or a category membership). Drives unread badges.</summary>
public enum AddedBy
{
    AppUi = 0,
    Explorer = 1,
    VaultMonitor = 2,
    Ai = 3,
    Paste = 4,

    /// <summary>Clean My Downloads mode moved it from the Downloads folder. Counts as unread, like the other outside adds.</summary>
    Downloads = 5,
}

public enum CategoryKind
{
    User = 0,
    BuiltIn = 1,

    /// <summary>The "Deleted" category. Cannot be deleted, only emptied.</summary>
    System = 2,
}

public enum CategorySort
{
    LastUpload = 0,
    Alphabetical = 1,
}

public enum CategoryDeletePolicy
{
    MoveToDeleted = 0,
    RecycleFiles = 1,

    /// <summary>UI-only: ask the user each time. Never passed to <c>DeleteCategoryAsync</c>.</summary>
    Ask = 2,
}

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>App-wide text size (scales the whole UI): Small 90%, Medium 100%, Large 115%, Extra large 130%.</summary>
public enum TextSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
    ExtraLarge = 3,
}

/// <summary>
/// The font of all of Cabinette's text (Settings › Appearance, SPEC.md section 11). Stored by name; a name this build
/// doesn't know (from a newer build) reads as <see cref="SegoeUIVariable"/>.
/// </summary>
public enum AppFont
{
    SegoeUIVariable = 0,
    SegoeUI = 1,
    Inter = 2,
    SourceSans3 = 3,
    AtkinsonHyperlegibleNext = 4,
    Lexend = 5,
    Bahnschrift = 6,
    Sitka = 7,
    CascadiaMono = 8,
    JetBrainsMono = 9,
}

public enum RelocateOutcome
{
    Relocated = 0,

    /// <summary>The chosen file's content differs from the original; nothing was changed.</summary>
    HashMismatch = 1,

    NotFound = 2,
}
