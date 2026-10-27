using System.IO;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Cabinette.App.ViewModels.Shell;

/// <summary>First run: choose (and create) the vault folder, then initialize the library, or restore a backup.</summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly VaultOperationsService _operations;

    public FirstRunViewModel(ILibraryService library, DialogService dialogs, VaultOperationsService operations)
    {
        _library = library;
        _operations = operations;
        Dialogs = dialogs;
    }

    /// <summary>Dialogs of the restore flow are shown inside the first-run window.</summary>
    public DialogService Dialogs { get; }

    /// <summary>Raised on the UI thread once the vault is ready.</summary>
    public event EventHandler? Completed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _vaultPath = AppPaths.DefaultVaultSuggestion;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(RestoreCommand))]
    private bool _isBusy;

    partial void OnVaultPathChanged(string value) => Error = null;

    [RelayCommand]
    private void Browse()
    {
        var current = VaultPath.Trim();
        var initial = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        var dialog = new OpenFolderDialog
        {
            Title = L.Get("FirstRun_PickTitle"),
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog() == true)
        {
            VaultPath = dialog.FolderName;
        }
    }

    /// <summary>"Restore from a backup": the restore flow; Cabinette restarts afterwards.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        Error = null;
        await _operations.RestoreAsync(replacesLibrary: false);
    }

    private bool CanRestore() => !IsBusy;

    private bool CanStart() => !IsBusy && !string.IsNullOrWhiteSpace(VaultPath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var path = VaultPath.Trim().Trim('"');
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            Error = L.Get("FirstRun_PathRequired");
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            path = Path.GetFullPath(path);
            if (await Task.Run(() => FolderHasContent(path)))
            {
                IsBusy = false;
                if (!await ConfirmFolderWithContentAsync())
                {
                    return;
                }

                IsBusy = true;
            }

            await Task.Run(() => Directory.CreateDirectory(path));
            await _library.InitializeVaultAsync(path);
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (LibraryException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Error = L.Format("FirstRun_Error", ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("First-run vault setup failed.", ex);
            Error = L.Format("FirstRun_Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A folder that already holds files: the vault monitor's first scan adds them to the library, turns its subfolders into
    /// categories and files loose files into category folders by type. Say so; the user picks another folder (the
    /// highlighted choice, which opens the folder picker) or explicitly keeps this one. True = use it.
    /// </summary>
    internal async Task<bool> ConfirmFolderWithContentAsync()
    {
        var choice = await Dialogs.ChooseAsync(
            L.Get("FirstRun_NotEmptyTitle"),
            L.Get("FirstRun_NotEmptyMessage"),
            [L.Get("FirstRun_ChooseAnotherFolder"), L.Get("FirstRun_UseFolderAnyway")]);
        if (choice == 0)
        {
            Browse();
        }

        return choice == 1;
    }

    /// <summary>
    /// True when the folder has files or subfolders the vault monitor would pick up. Hidden and system entries (desktop.ini,
    /// Thumbs.db) don't count, and neither does a folder that can't be read (setting it up reports that).
    /// </summary>
    internal static bool FolderHasContent(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = true,
            };
            return Directory.EnumerateFileSystemEntries(path, "*", options).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
