namespace Cabinette.Core.Models;

[Flags]
public enum LibraryChangeKind
{
    None = 0,
    Categories = 1,
    Items = 2,
    Tags = 4,
    Settings = 8,

    /// <summary>Only unread state changed (a thread was viewed). Chat-list badges need refreshing; thread contents did not change.</summary>
    Viewed = 16,

    /// <summary>
    /// Stars were given or taken (with <see cref="Items"/> and the items' ids), or starred items were deleted: the Starred
    /// view and the chat list's Starred row need refreshing.
    /// </summary>
    Starred = 32,
}

/// <summary>Raised after any change to the library. Raised on a background thread; UI must marshal to the dispatcher.</summary>
public sealed class LibraryChangedEventArgs : EventArgs
{
    public LibraryChangeKind Kind { get; init; }

    /// <summary>Categories whose contents or properties changed. Ancestors are included so parent threads can refresh.</summary>
    public IReadOnlyCollection<long> CategoryIds { get; init; } = [];

    public IReadOnlyCollection<long> ItemIds { get; init; } = [];

    /// <summary>Who caused the change, when known.</summary>
    public AddedBy? Source { get; init; }

    /// <summary>Duplicates found after the fact (large pointer files hashed in the background) and merged into existing items.</summary>
    public IReadOnlyList<DuplicateInfo> MergedDuplicates { get; init; } = [];
}
