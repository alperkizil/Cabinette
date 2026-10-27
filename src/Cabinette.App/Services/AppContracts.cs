using Cabinette.Core.Models;

namespace Cabinette.App.Services;

// Contracts shared by the shell (main window, chat list, dialogs) and the thread view.
// Implemented by the shell workstream; the thread workstream only consumes them.

/// <summary>Opens threads in the right-hand pane.</summary>
public interface IShellNavigator
{
    long? CurrentCategoryId { get; }

    /// <summary>Opens the category's thread. When <paramref name="focusItemId"/> is set, scrolls to and highlights that item.</summary>
    void OpenCategory(long categoryId, long? focusItemId = null);
}

/// <summary>Adds files to the library with progress UI and result notices (duplicates, failures).</summary>
public interface IFileIntake
{
    /// <summary>Adds dropped/selected files and folders. <paramref name="targetCategoryId"/> null = auto-categorize.</summary>
    Task AddPathsAsync(IReadOnlyList<string> paths, long? targetCategoryId, AddedBy addedBy = AddedBy.AppUi);

    /// <summary>Opens the multi-select file dialog and adds the chosen files.</summary>
    Task PickAndAddFilesAsync(long? targetCategoryId);

    /// <summary>
    /// Reads the clipboard and adds its content (files, image, text or URL) as files in the vault.
    /// <paramref name="targetCategoryId"/> null = main-window paste: asks which category, pre-selecting Notes/Images/Links.
    /// </summary>
    Task PasteAsync(long? targetCategoryId);
}

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>In-app snackbar notices.</summary>
public interface INotificationService
{
    void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Action? action = null);
}

/// <summary>Modal dialogs styled to match the app.</summary>
public interface IDialogService
{
    /// <summary>Text input. <paramref name="validate"/> returns an error message, or null when the text is valid. Returns null if cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);

    /// <summary>Shows one button per option. Returns the chosen index, or null if cancelled.</summary>
    Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options);

    /// <summary>Category tree picker. Returns the chosen category id, or null if cancelled.</summary>
    Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true);

    /// <summary>File dialog for re-linking a missing pointer file. Returns the chosen path, or null.</summary>
    Task<string?> PickFileToLocateAsync(string originalFileName);
}
