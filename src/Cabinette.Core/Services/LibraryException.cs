namespace Cabinette.Core.Services;

/// <summary>An error the user should see, with a message that can be shown as is.</summary>
public sealed class LibraryException : Exception
{
    public LibraryException(LibraryErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public LibraryErrorCode Code { get; }
}

public enum LibraryErrorCode
{
    Unknown = 0,
    InvalidName,
    NameAlreadyExists,
    NotFound,
    FileInUse,
    AccessDenied,
    VaultNotConfigured,
    NotAllowed,

    /// <summary>
    /// A file should go to the Recycle Bin, but can't: its drive has none, or it is too large for it. Usually nothing was changed;
    /// rarely the Recycle Bin refuses only after the delete went through, and then the items are gone but their files stay. Either
    /// way the UI asks the user and retries with <see cref="Models.DeleteItemsOptions.PermanentWhenNoRecycleBin"/> set.
    /// </summary>
    RecycleBinUnavailable,
}
