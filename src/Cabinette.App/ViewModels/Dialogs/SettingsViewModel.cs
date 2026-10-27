using System.IO;
using System.Windows.Media;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.Services.Downloads;
using Cabinette.App.Services.Monitor;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Cabinette.App.ViewModels.Dialogs;

public sealed record SettingsSection(string Key, string Title, SymbolRegular Symbol);

/// <summary>A font in Settings › Appearance, its name drawn in itself.</summary>
public sealed record FontOption(AppFont Font, string Name, FontFamily Family)
{
    /// <summary>What screen readers, type-ahead and the closed list read for this font: its name.</summary>
    public override string ToString() => Name;
}

/// <summary>Settings (SPEC section 12). Every change is applied and saved immediately.</summary>
public sealed partial class SettingsViewModel : DialogViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IThemeService _themeService;
    private readonly INotificationService _notifications;
    private readonly IShellOperations _shell;
    private readonly VaultOperationsService _operations;
    private readonly DownloadsModeConfirmation _downloadsConfirmation;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _settings = new();
    private bool _loading;

    public SettingsViewModel(
        ILibraryService library,
        IThemeService theme,
        INotificationService notifications,
        IShellOperations shell,
        VaultMonitorHost monitor,
        VaultOperationsService operations,
        DownloadsModeConfirmation downloadsConfirmation,
        Func<AiSettingsViewModel>? aiSettings = null)
        : base(L.Get("Settings_Title"))
    {
        _library = library;
        _themeService = theme;
        _notifications = notifications;
        _shell = shell;
        _operations = operations;
        _downloadsConfirmation = downloadsConfirmation;
        Monitor = monitor;
        _operations.VaultMoved += OnVaultMoved;
        Ai = aiSettings?.Invoke();
        if (Ai is not null)
        {
            // Opening a file from the AI activity log closes Settings.
            Ai.CloseDialog = () => Close(null);
        }

        List<SettingsSection> sections =
        [
            new("appearance", L.Get("Settings_Appearance"), SymbolRegular.PaintBrush20),
            new("library", L.Get("Settings_Library"), SymbolRegular.Library20),
            new("adding", L.Get("Settings_Adding"), SymbolRegular.DocumentAdd20),
            new("deleting", L.Get("Settings_Deleting"), SymbolRegular.Delete20),
            new("chatlist", L.Get("Settings_ChatList"), SymbolRegular.TextBulletListTree20),
            new("windows", L.Get("Settings_System"), SymbolRegular.Window20),
        ];
        if (Ai is not null)
        {
            sections.Add(new("ai", L.Get("Settings_Ai"), SymbolRegular.Bot20));
        }

        Sections = sections;
        _selectedSection = Sections[0];
    }

    public override double Width => 780;

    public override double Height => 580;

    /// <summary>Raised after settings were saved (e.g. the chat list re-sorts).</summary>
    public event EventHandler<AppSettings>? Saved;

    public IReadOnlyList<SettingsSection> Sections { get; }

    /// <summary>The vault monitor's status (Library section).</summary>
    public VaultMonitorHost Monitor { get; }

    /// <summary>Settings › AI (connecting Claude Code and Codex, the activity log); null when the app has no AI UI.</summary>
    public AiSettingsViewModel? Ai { get; }

    [ObservableProperty]
    private SettingsSection _selectedSection;

    [ObservableProperty]
    private ThemePreference _theme;

    [ObservableProperty]
    private TextSize _textSize;

    [ObservableProperty]
    private bool _boldText;

    /// <summary>The fonts this PC can draw (filled by <see cref="LoadAsync"/>).</summary>
    [ObservableProperty]
    private IReadOnlyList<FontOption> _fonts = [];

    [ObservableProperty]
    private FontOption? _selectedFont;

    [ObservableProperty]
    private string _vaultPath = "";

    [ObservableProperty]
    private bool _hasVault;

    [ObservableProperty]
    private ItemMode _defaultAddMode;

    [ObservableProperty]
    private bool _folderSubfoldersAsSubcategories;

    [ObservableProperty]
    private bool _folderAlsoAddToTypeCategories;

    [ObservableProperty]
    private bool _sortFilesByTypeInTypeCategories;

    [ObservableProperty]
    private bool _cleanMyDownloads;

    [ObservableProperty]
    private bool _recycleVaultFiles;

    [ObservableProperty]
    private bool _deleteRealPointerFiles;

    [ObservableProperty]
    private CategoryDeletePolicy _categoryDeletePolicy;

    [ObservableProperty]
    private CategorySort _chatListSort;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _notifyOnVaultChanges;

    public string VersionText => L.Format("Settings_Version", typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "");

    public async Task LoadAsync()
    {
        try
        {
            _settings = await _library.GetSettingsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Loading settings failed.", ex);
            _settings = new AppSettings();
        }

        // Reading which fonts Windows has takes a moment the first time.
        var fonts = await Task.Run(() => UiFont.Available());
        var shown = UiFont.Resolve(_settings.Font, family => fonts.Any(c => c.WindowsFamily == family));

        _loading = true;
        Fonts = [.. fonts.Select(c => new FontOption(c.Font, L.Get(c.NameKey), c.Text))];
        SelectedFont = Fonts.First(f => f.Font == shown);
        Theme = _settings.Theme;
        TextSize = _settings.TextSize;
        BoldText = _settings.BoldText;
        VaultPath = _library.VaultPath ?? _settings.VaultPath ?? L.Get("Settings_VaultNotSet");
        HasVault = (_library.VaultPath ?? _settings.VaultPath) is not null;
        DefaultAddMode = _settings.DefaultAddMode;
        FolderSubfoldersAsSubcategories = _settings.FolderSubfoldersAsSubcategories;
        FolderAlsoAddToTypeCategories = _settings.FolderAlsoAddToTypeCategories;
        SortFilesByTypeInTypeCategories = _settings.SortFilesByTypeInTypeCategories;
        CleanMyDownloads = _settings.CleanMyDownloads;
        RecycleVaultFiles = _settings.RecycleVaultFiles;
        DeleteRealPointerFiles = _settings.DeleteRealPointerFiles;
        CategoryDeletePolicy = _settings.CategoryDeletePolicy;
        ChatListSort = _settings.ChatListSort;
        StartWithWindows = _settings.StartWithWindows;
        CloseToTray = _settings.CloseToTray;
        NotifyOnVaultChanges = _settings.NotifyOnVaultChanges;
        _loading = false;
    }

    public void SelectSection(string key) => SelectedSection = Sections.FirstOrDefault(s => s.Key == key) ?? Sections[0];

    [RelayCommand]
    private void OpenVaultFolder()
    {
        var path = _library.VaultPath;
        if (path is null || !Directory.Exists(path))
        {
            _notifications.Show(L.Format("Category_FolderNotFound", path ?? ""), NotificationKind.Warning);
            return;
        }

        ShellLauncher.OpenFolder(_shell, path);
    }

    // ------------------------------------------------------------------ vault operations (stage 2)

    [RelayCommand]
    private Task MoveVaultAsync() => _operations.MoveVaultAsync();

    [RelayCommand]
    private Task MigrateAsync() => _operations.MigrateAsync();

    [RelayCommand]
    private Task CreateBackupAsync() => _operations.CreateBackupAsync();

    [RelayCommand]
    private Task RestoreAsync() => _operations.RestoreAsync(replacesLibrary: true);

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        var wasPaused = Monitor.IsPaused;
        if (!await Monitor.TogglePauseAsync())
        {
            _notifications.Show(L.Get(wasPaused ? "Monitor_ResumeFailed" : "Monitor_PauseFailed"), NotificationKind.Error);
        }
    }

    private void OnVaultMoved(object? sender, EventArgs e)
    {
        VaultPath = _library.VaultPath ?? VaultPath;
        HasVault = _library.VaultPath is not null;
    }

    protected override void OnClosing()
    {
        _operations.VaultMoved -= OnVaultMoved;
        Ai?.Dispose();
    }

    // The AI section reads its activity log when it is first shown.
    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        if (value.Key == "ai" && Ai is not null)
        {
            _ = Ai.LoadAsync();
        }
    }

    partial void OnThemeChanged(ThemePreference value)
    {
        if (_loading)
        {
            return;
        }

        _themeService.Apply(value);
        Save(_settings with { Theme = value });
    }

    partial void OnTextSizeChanged(TextSize value)
    {
        if (_loading)
        {
            return;
        }

        UiScale.Apply(value);
        Save(_settings with { TextSize = value });
    }

    partial void OnBoldTextChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        UiWeight.Apply(value);
        Save(_settings with { BoldText = value });
    }

    partial void OnSelectedFontChanged(FontOption? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        UiFont.Apply(value.Font);
        Save(_settings with { Font = value.Font });
    }

    partial void OnDefaultAddModeChanged(ItemMode value) => Save(_settings with { DefaultAddMode = value });

    partial void OnFolderSubfoldersAsSubcategoriesChanged(bool value) => Save(_settings with { FolderSubfoldersAsSubcategories = value });

    partial void OnFolderAlsoAddToTypeCategoriesChanged(bool value) => Save(_settings with { FolderAlsoAddToTypeCategories = value });

    partial void OnSortFilesByTypeInTypeCategoriesChanged(bool value) => Save(_settings with { SortFilesByTypeInTypeCategories = value });

    // The app starts and stops the mode through SettingsCache. Switching it on asks first, the first time.
    partial void OnCleanMyDownloadsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            _ = TurnOnCleanMyDownloadsAsync();
        }
        else
        {
            Save(_settings with { CleanMyDownloads = false });
        }
    }

    private async Task TurnOnCleanMyDownloadsAsync()
    {
        bool confirmed;
        try
        {
            confirmed = await _downloadsConfirmation.ConfirmTurnOnAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Asking to turn on Clean My Downloads failed.", ex);
            confirmed = false;
        }

        if (!confirmed)
        {
            // Back off without saving anything.
            _loading = true;
            CleanMyDownloads = false;
            _loading = false;
            return;
        }

        Save(_settings with { CleanMyDownloads = true });
    }

    partial void OnRecycleVaultFilesChanged(bool value) => Save(_settings with { RecycleVaultFiles = value });

    partial void OnDeleteRealPointerFilesChanged(bool value) => Save(_settings with { DeleteRealPointerFiles = value });

    partial void OnCategoryDeletePolicyChanged(CategoryDeletePolicy value) => Save(_settings with { CategoryDeletePolicy = value });

    partial void OnChatListSortChanged(CategorySort value) => Save(_settings with { ChatListSort = value });

    // The app reacts to these through SettingsCache (startup entry, close button, notifications).
    partial void OnStartWithWindowsChanged(bool value) => Save(_settings with { StartWithWindows = value });

    partial void OnCloseToTrayChanged(bool value) => Save(_settings with { CloseToTray = value });

    partial void OnNotifyOnVaultChangesChanged(bool value) => Save(_settings with { NotifyOnVaultChanges = value });

    private async void Save(AppSettings updated)
    {
        if (_loading)
        {
            return;
        }

        // This dialog never changes the vault, but a vault move while it is open makes the loaded path stale.
        updated = updated with { VaultPath = _library.VaultPath ?? updated.VaultPath };
        _settings = updated;
        await _saveGate.WaitAsync();
        try
        {
            await _library.SaveSettingsAsync(updated);
            Saved?.Invoke(this, updated);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Saving settings failed.", ex);
            _notifications.Show(L.Format("Settings_SaveFailed", ex is LibraryException ? ex.Message : ex.GetType().Name), NotificationKind.Error);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
