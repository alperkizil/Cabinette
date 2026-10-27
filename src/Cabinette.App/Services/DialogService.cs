using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Cabinette.App.Localization;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace Cabinette.App.Services;

/// <summary>
/// Themed modal dialogs drawn as overlays inside the main window (see Views/Dialogs/DialogHost).
/// Several can be stacked (e.g. the category picker opened from the new-category dialog). Call on any thread.
/// </summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    private readonly ILibraryService _library;
    private readonly CategoryIconService _icons;
    private readonly CategoryDirectory _directory;

    public DialogService(ILibraryService library, CategoryIconService icons, CategoryDirectory directory)
    {
        _library = library;
        _icons = icons;
        _directory = directory;
        OpenDialogs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsAnyOpen));
    }

    /// <summary>Dialogs on screen, bottom to top.</summary>
    public ObservableCollection<DialogViewModelBase> OpenDialogs { get; } = [];

    public bool IsAnyOpen => OpenDialogs.Count > 0;

    public DialogViewModelBase? Top => OpenDialogs.Count > 0 ? OpenDialogs[^1] : null;

    /// <summary>Shows a dialog and waits until it closes. Returns the dialog's result (null = cancelled).</summary>
    public Task<object?> ShowAsync(DialogViewModelBase dialog) => OnUiAsync(async () =>
    {
        OpenDialogs.Add(dialog);
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            OpenDialogs.Remove(dialog);
        }
    });

    /// <summary>Cancels every open dialog (e.g. when the window closes).</summary>
    public void CancelAll()
    {
        foreach (var dialog in OpenDialogs.ToList())
        {
            dialog.Close(null);
        }
    }

    public async Task<string?> PromptTextAsync(string title, string label, string initialText, Func<string, string?>? validate = null, bool selectFileNameWithoutExtension = false)
        => await ShowAsync(new PromptDialogViewModel(title, label, initialText, validate, selectFileNameWithoutExtension)) as string;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
        => await ShowAsync(new ConfirmDialogViewModel(title, message, confirmText, isDestructive)) is true;

    /// <summary>An informational message with a single OK button and an optional list of details.</summary>
    public Task ShowMessageAsync(string title, string message, IReadOnlyList<string>? details = null)
        => ShowAsync(new ConfirmDialogViewModel(title, message, L.Get("Common_Ok"), isDestructive: false, showCancel: false, details));

    public async Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
        => await ShowAsync(new ChooseDialogViewModel(title, message, options)) as int?;

    public async Task<long?> PickCategoryAsync(string title, long? preselectedCategoryId = null, bool allowCreate = true)
    {
        var result = await ShowCategoryPickerAsync(title, preselectedCategoryId, allowCreate, allowTopLevel: false);
        return result?.CategoryId;
    }

    /// <summary>
    /// Picker with an optional "Top level" row. Returns null when cancelled. <paramref name="builtInKey"/> suggests a
    /// top-level built-in category, offered even if it was deleted (see <see cref="CategoryPickResult.BuiltInKeyToCreate"/>).
    /// </summary>
    public Task<CategoryPickResult?> ShowCategoryPickerAsync(string title, long? preselectedCategoryId, bool allowCreate, bool allowTopLevel, long? excludeSubtreeOf = null, string? builtInKey = null)
        => OnUiAsync(async () =>
        {
            var picker = new CategoryPickerViewModel(
                title,
                _library,
                _icons,
                preselectedCategoryId,
                allowCreate ? ShowNewCategoryAsync : null,
                allowTopLevel,
                excludeSubtreeOf,
                builtInKey);
            await picker.LoadAsync();
            return await ShowAsync(picker) as CategoryPickResult;
        });

    /// <summary>The new-category dialog. Creates the category and returns it, or null when cancelled.</summary>
    public Task<Category?> ShowNewCategoryAsync(long? parentId) => OnUiAsync(async () =>
    {
        await _directory.EnsureLoadedAsync();
        var dialog = new NewCategoryDialogViewModel(_library, _icons, _directory, this, parentId);
        return await ShowAsync(dialog) as Category;
    });

    public Task<string?> PickFileToLocateAsync(string originalFileName) => OnUiAsync(() =>
    {
        var extension = Path.GetExtension(originalFileName).TrimStart('.');
        var filter = extension.Length > 0
            ? $"{L.Format("Locate_Filter", extension.ToUpperInvariant())}|*.{extension}|{L.Get("Intake_AllFiles")}|*.*"
            : $"{L.Get("Intake_AllFiles")}|*.*";
        var dialog = new OpenFileDialog
        {
            Title = L.Format("Locate_Title", originalFileName),
            Filter = filter,
            FileName = originalFileName,
            CheckFileExists = true,
        };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FileName : null);
    });

    /// <summary>File dialog for a category icon (PNG). Returns the path or null. UI thread only.</summary>
    public string? PickIconFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Get("Category_IconPickTitle"),
            Filter = $"{L.Get("Category_IconFilter")}|*.png|{L.Get("Intake_AllImages")}|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|{L.Get("Intake_AllFiles")}|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private static Window? Owner
    {
        get
        {
            var main = Application.Current?.MainWindow;
            return main is { IsVisible: true } ? main : null;
        }
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
