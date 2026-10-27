using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>
/// Themed progress for long vault operations (move, migrate, backup, restore): stage, files x of y, bytes, current file
/// and a cancel button. Pass it as the operation's <see cref="IProgress{T}"/> (reports from any thread are coalesced
/// onto the UI thread) and its <see cref="Token"/> as the cancellation token; close it with <see cref="Complete"/>.
/// </summary>
public sealed partial class ProgressDialogViewModel : DialogViewModelBase, IProgress<VaultOperationProgress>
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;
    private VaultOperationProgress? _latest;
    private int _applyQueued;
    private bool _completed;

    public ProgressDialogViewModel(string title, bool canCancel = true)
        : base(title)
    {
        CanCancel = canCancel;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _stageText = L.Get("Progress_Preparing");
    }

    public override double Width => 480;

    public bool CanCancel { get; }

    public CancellationToken Token => _cts.Token;

    /// <summary>"Step 1 of 2 · Copying linked files" for multi-step operations, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStep))]
    private string? _stepText;

    [ObservableProperty]
    private string _stageText;

    /// <summary>"312 of 1,204 files · 1.2 GB of 4.5 GB".</summary>
    [ObservableProperty]
    private string? _countText;

    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCancelCommand))]
    private bool _isCancelling;

    public bool HasStep => StepText is not null;

    /// <summary>Starts step <paramref name="number"/> of <paramref name="count"/>; progress resets.</summary>
    public void BeginStep(int number, int count, string text)
    {
        StepText = L.Format("Progress_Step", number, count, text);
        StageText = L.Get("Progress_Preparing");
        CountText = null;
        CurrentFile = null;
        Fraction = 0;
        IsIndeterminate = true;
    }

    public void Report(VaultOperationProgress value)
    {
        _latest = value;
        if (Interlocked.Exchange(ref _applyQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyLatest);
        }
    }

    /// <summary>Closes the dialog when the operation is over (success, failure or cancelled).</summary>
    public void Complete()
    {
        _completed = true;
        Close(null);
    }

    /// <summary>Enter does nothing here.</summary>
    public override void Accept()
    {
    }

    /// <summary>Esc requests cancellation instead of hiding a still-running operation.</summary>
    public override void Dismiss()
    {
        if (CanCancel && RequestCancelCommand.CanExecute(null))
        {
            RequestCancelCommand.Execute(null);
        }
    }

    /// <summary>Closed from outside (e.g. the app is exiting) while still running: cancel the operation.</summary>
    protected override void OnClosing()
    {
        if (!_completed)
        {
            _cts.Cancel();
        }
    }

    private bool CanRequestCancel() => CanCancel && !IsCancelling && !IsClosed;

    [RelayCommand(CanExecute = nameof(CanRequestCancel))]
    private void RequestCancel()
    {
        IsCancelling = true;
        StageText = L.Get("Progress_Cancelling");
        _cts.Cancel();
    }

    private void ApplyLatest()
    {
        Interlocked.Exchange(ref _applyQueued, 0);
        if (_latest is not { } p || IsClosed)
        {
            return;
        }

        if (!IsCancelling)
        {
            StageText = StageTextFor(p.Stage);
        }

        var culture = CultureInfo.CurrentCulture;
        var parts = new List<string>(2);
        if (p.TotalFiles > 0)
        {
            parts.Add(L.Format("Progress_Files", Math.Min(p.ProcessedFiles, p.TotalFiles).ToString("N0", culture), p.TotalFiles.ToString("N0", culture)));
        }

        if (p.TotalBytes > 0)
        {
            parts.Add(L.Format("Progress_Bytes", Glyphs.FileSize(Math.Min(p.BytesProcessed, p.TotalBytes)), Glyphs.FileSize(p.TotalBytes)));
        }

        CountText = parts.Count > 0 ? string.Join(" · ", parts) : null;
        CurrentFile = string.IsNullOrEmpty(p.CurrentFile) ? null : System.IO.Path.GetFileName(p.CurrentFile.TrimEnd('\\', '/'));

        if (p.TotalBytes > 0)
        {
            IsIndeterminate = false;
            Fraction = Math.Clamp((double)p.BytesProcessed / p.TotalBytes, 0, 1);
        }
        else if (p.TotalFiles > 0)
        {
            IsIndeterminate = false;
            Fraction = Math.Clamp((double)p.ProcessedFiles / p.TotalFiles, 0, 1);
        }
        else
        {
            IsIndeterminate = true;
        }
    }

    /// <summary>Core reports short English stage names; the UI shows its own text for the known ones.</summary>
    public static string StageTextFor(string? stage)
    {
        var key = (stage ?? "").Trim().ToLowerInvariant() switch
        {
            "preparing" or "prepare" or "starting" => "Progress_Preparing",
            "scanning" or "scan" or "counting" or "count" or "listing" => "Progress_Scanning",
            "copying" or "copy" => "Progress_Copying",
            "verifying" or "verify" or "hashing" or "checking" => "Progress_Verifying",
            "rechecking" => "Progress_Rechecking",
            "switching" or "switch" or "finalizing" or "finishing" or "updating" or "database" => "Progress_Finishing",
            "migrating" or "migrate" => "Progress_Copying",
            "snapshot" or "zipping" or "zip" or "compressing" or "writing" => "Progress_Zipping",
            "extracting" or "extract" or "unzipping" or "restoring" => "Progress_Extracting",
            "cleanup" or "cleaning" => "Progress_Finishing",
            _ => "Progress_Working",
        };
        return L.Get(key);
    }
}
