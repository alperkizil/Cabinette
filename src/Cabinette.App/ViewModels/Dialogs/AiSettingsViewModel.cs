using System.IO;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>Whether a copy button just copied.</summary>
public enum CopyState
{
    Ready,
    Copied,
    Failed,
}

/// <summary>One registration command with its copy button.</summary>
public sealed partial class AiCommandLineViewModel : ObservableObject
{
    /// <summary>How long the button says "Copied".</summary>
    public static readonly TimeSpan FeedbackTime = TimeSpan.FromSeconds(2);

    private readonly IClipboardWriter _clipboard;
    private int _copies;

    public AiCommandLineViewModel(string title, string text, IClipboardWriter clipboard)
    {
        Title = title;
        _text = text;
        _clipboard = clipboard;
    }

    /// <summary>"Claude Code", "Codex".</summary>
    public string Title { get; }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private CopyState _state;

    public string CopyLabel => L.Get(State switch
    {
        CopyState.Copied => "SettingsAi_Copied",
        CopyState.Failed => "SettingsAi_CopyFailed",
        _ => "SettingsAi_Copy",
    });

    [RelayCommand]
    private async Task CopyAsync()
    {
        var copy = ++_copies;
        bool copied;
        try
        {
            copied = _clipboard.TrySetText(Text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Copying a registration command failed.", ex);
            copied = false;
        }

        State = copied ? CopyState.Copied : CopyState.Failed;
        await Task.Delay(FeedbackTime);
        if (copy == _copies)
        {
            State = CopyState.Ready;
        }
    }
}

/// <summary>
/// Settings › AI (SPEC.md section 10): the commands that connect Claude Code and Codex (with copy buttons and the
/// installed path filled in), and the AI activity log. Dev runs (<c>--data-dir</c>) show commands for "cabinette-dev"
/// that pass their data folder and Cabinette.exe on, so they never replace the real registration; their
/// cabinette-mcp.exe is the repository's own build when there is none next to Cabinette.exe.
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AiDevRun? _dev;
    private readonly string? _devSearchStart;
    private Task? _loading;
    private bool _previewing;

    public AiSettingsViewModel(
        IAiActivityLog log,
        ILibraryService library,
        IShellNavigator navigator,
        IClipboardWriter clipboard,
        CommandLineOptions options,
        AppPaths paths)
        : this(log, library, navigator, clipboard, AiConnectCommands.DefaultExePath, DevRun(options, paths), null, AppContext.BaseDirectory)
    {
    }

    /// <param name="exePath">Where cabinette-mcp.exe belongs (next to Cabinette.exe).</param>
    /// <param name="activity">The activity view model (tests); null creates the app's.</param>
    /// <param name="devSearchStart">
    /// For a dev run: where to start looking for the repository's own build when cabinette-mcp.exe isn't at
    /// <paramref name="exePath"/> (see <see cref="AiConnectCommands.Locate"/>); null looks only there.
    /// </param>
    internal AiSettingsViewModel(
        IAiActivityLog log,
        ILibraryService library,
        IShellNavigator navigator,
        IClipboardWriter clipboard,
        string exePath,
        AiDevRun? dev,
        AiActivityViewModel? activity,
        string? devSearchStart = null)
    {
        _dev = dev;
        _devSearchStart = dev is null ? null : devSearchStart;
        Commands = AiConnectCommands.Build(exePath, dev);
        ClaudeCode = new AiCommandLineViewModel(L.Get("SettingsAi_ClaudeCode"), Commands.ClaudeAdd, clipboard);
        Codex = new AiCommandLineViewModel(L.Get("SettingsAi_Codex"), Commands.CodexAdd, clipboard);
        Activity = activity ?? new AiActivityViewModel(log, library, navigator, () => CloseDialog?.Invoke());
    }

    /// <summary>Closes the Settings dialog (set by it): opening a file from the log closes Settings.</summary>
    public Action? CloseDialog { get; set; }

    public AiConnectCommands Commands { get; private set; }

    public AiCommandLineViewModel ClaudeCode { get; }

    public AiCommandLineViewModel Codex { get; }

    public string ClaudeRemove => Commands.ClaudeRemove;

    public string CodexRemove => Commands.CodexRemove;

    /// <summary>For a dev run: the commands use another name, the dev data folder and this Cabinette.exe. Else null.</summary>
    public string? DevNote => _dev is null ? null : L.Format("SettingsAi_DevNote", Commands.ServerName);

    /// <summary>cabinette-mcp.exe wasn't found: a hint (reinstall, or for a dev run, build it), else null.</summary>
    [ObservableProperty]
    private string? _exeMissingText;

    public AiActivityViewModel Activity { get; }

    /// <summary>Finds cabinette-mcp.exe and reads the first page of activity (once; off the UI thread).</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public void Dispose() => Activity.Dispose();

    /// <summary>Screenshots: commands for an installed copy at <paramref name="exePath"/>, as if the file were there.</summary>
    internal void UsePreviewPath(string exePath)
    {
        _previewing = true;
        ShowCommands(AiConnectCommands.Build(exePath, _dev));
        ExeMissingText = null;
    }

    private void ShowCommands(AiConnectCommands commands)
    {
        Commands = commands;
        ClaudeCode.Text = Commands.ClaudeAdd;
        Codex.Text = Commands.CodexAdd;
        OnPropertyChanged(nameof(Commands));
        OnPropertyChanged(nameof(ClaudeRemove));
        OnPropertyChanged(nameof(CodexRemove));
    }

    private async Task LoadCoreAsync()
    {
        var shown = Commands;
        var devSearchStart = _devSearchStart;
        AiExeLocation location;
        try
        {
            location = await Task.Run(() => AiConnectCommands.Locate(shown.ExePath, devSearchStart));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Looking for cabinette-mcp.exe failed.", ex);
            location = new AiExeLocation(shown.ExePath, Exists: true);
        }

        // A preview path may have replaced the real one meanwhile.
        if (!_previewing && ReferenceEquals(Commands, shown))
        {
            if (!string.Equals(location.Path, shown.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                ShowCommands(AiConnectCommands.Build(location.Path, _dev));
            }

            if (!location.Exists)
            {
                var folder = Path.GetDirectoryName(location.Path) ?? location.Path;
                ExeMissingText = _dev is null ? L.Format("SettingsAi_ExeMissing", folder) : L.Format("SettingsAi_ExeMissingDev", folder);
            }
        }

        await Activity.LoadAsync();
    }

    /// <summary>A dev run (<c>--data-dir</c>): its full data folder and the running Cabinette.exe. Else null.</summary>
    private static AiDevRun? DevRun(CommandLineOptions options, AppPaths paths)
        => string.IsNullOrWhiteSpace(options.DataDirectory) ? null : new AiDevRun(paths.DataDirectory, AiConnectCommands.RunningAppPath);
}
