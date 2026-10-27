using Cabinette.App.Localization;

namespace Cabinette.App.Services.Downloads;

/// <summary>
/// The short confirm shown the first time Clean My Downloads mode is switched on: "New downloads will be moved into your
/// vault and the originals sent to the Recycle Bin." Once confirmed, switching it on again doesn't ask. Remembered in
/// state.json, except in sample-data and screenshot runs (which never write the data folder for it).
/// </summary>
public sealed class DownloadsModeConfirmation
{
    private readonly IDialogService _dialogs;
    private readonly AppStateStore _state;
    private readonly bool _remember;
    private bool _confirmedThisRun;

    public DownloadsModeConfirmation(IDialogService dialogs, AppStateStore state, CommandLineOptions options)
    {
        _dialogs = dialogs;
        _state = state;
        _remember = !options.IsSandboxed;
    }

    /// <summary>True when the mode may be switched on: confirmed now, or confirmed before.</summary>
    public async Task<bool> ConfirmTurnOnAsync()
    {
        if (_confirmedThisRun || (_remember && _state.Load().CleanDownloadsConfirmed))
        {
            return true;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            L.Get("Downloads_ConfirmTitle"),
            L.Get("Downloads_ConfirmMessage"),
            L.Get("Downloads_ConfirmButton"));
        if (!confirmed)
        {
            return false;
        }

        _confirmedThisRun = true;
        if (_remember)
        {
            _state.Update(state => state with { CleanDownloadsConfirmed = true });
        }

        return true;
    }
}
