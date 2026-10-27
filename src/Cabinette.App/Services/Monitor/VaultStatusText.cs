using System.Globalization;
using Cabinette.App.Localization;
using Cabinette.Core.Models;

namespace Cabinette.App.Services.Monitor;

/// <summary>What the tray icon shows.</summary>
public enum MonitorIndicator
{
    /// <summary>Not watching (no monitor in this run, or it failed to start).</summary>
    Off,
    Idle,
    Syncing,
    Paused,

    /// <summary>The vault folder is missing or unreadable.</summary>
    Error,
}

/// <summary>User-facing texts for the vault monitor's status (tray tooltip, tray menu, Settings › Library).</summary>
public static class VaultStatusText
{
    /// <summary>Windows cuts tray tooltips at 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    public static MonitorIndicator Indicator(VaultMonitorStatus? status, bool available)
    {
        if (!available || status is null)
        {
            return MonitorIndicator.Off;
        }

        return status.State switch
        {
            VaultMonitorState.Idle => MonitorIndicator.Idle,
            VaultMonitorState.Syncing => MonitorIndicator.Syncing,
            VaultMonitorState.Paused => MonitorIndicator.Paused,
            VaultMonitorState.VaultUnavailable => MonitorIndicator.Error,

            // Stopped while available = starting up (the startup rescan has not begun yet).
            _ => MonitorIndicator.Syncing,
        };
    }

    /// <summary>"Cabinette · up to date", "Cabinette · syncing 12 changes…", ...</summary>
    public static string Tooltip(VaultMonitorStatus? status, bool available)
    {
        var text = !available || status is null
            ? L.Get("Tray_TipOff")
            : status.State switch
            {
                VaultMonitorState.Idle => L.Get("Tray_TipUpToDate"),
                VaultMonitorState.Syncing => Count(status.PendingChanges, "Tray_TipSyncing", "Tray_TipSyncingOne", "Tray_TipSyncingUnknown"),
                VaultMonitorState.Paused => L.Get("Tray_TipPaused"),
                VaultMonitorState.VaultUnavailable => L.Get("Tray_TipVaultMissing"),
                _ => L.Get("Tray_TipStarting"),
            };
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    /// <summary>One line for Settings and the tray menu: "Watching · last sync 12:03", "Syncing 12 changes…", ...</summary>
    public static string Line(VaultMonitorStatus? status, bool available, DateTimeOffset now)
    {
        if (!available || status is null)
        {
            return L.Get("Monitor_Off");
        }

        return status.State switch
        {
            VaultMonitorState.Idle => status.LastSyncAt is { } at
                ? L.Format("Monitor_Watching", FormatSyncTime(at, now))
                : L.Get("Monitor_WatchingNoSync"),
            VaultMonitorState.Syncing => Count(status.PendingChanges, "Monitor_Syncing", "Monitor_SyncingOne", "Monitor_SyncingUnknown"),
            VaultMonitorState.Paused => L.Get("Monitor_Paused"),
            VaultMonitorState.VaultUnavailable => L.Get("Monitor_VaultMissing"),
            _ => L.Get("Monitor_Starting"),
        };
    }

    /// <summary>A second, smaller line with details (why the vault is unavailable, what pausing means).</summary>
    public static string? Detail(VaultMonitorStatus? status, bool available)
    {
        if (!available || status is null)
        {
            return L.Get("Monitor_OffHint");
        }

        return status.State switch
        {
            VaultMonitorState.VaultUnavailable => string.IsNullOrWhiteSpace(status.Message)
                ? L.Get("Monitor_VaultMissingHint")
                : $"{status.Message} {L.Get("Monitor_VaultMissingHint")}",
            VaultMonitorState.Paused => L.Get("Monitor_PausedHint"),
            _ => null,
        };
    }

    /// <summary>"12:03" today, otherwise the short date and time.</summary>
    public static string FormatSyncTime(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("t", culture)
            : local.ToString("g", culture);
    }

    private static string Count(int pending, string countKey, string oneKey, string unknownKey) => pending switch
    {
        <= 0 => L.Get(unknownKey),
        1 => L.Get(oneKey),
        _ => L.Format(countKey, pending.ToString("N0", CultureInfo.CurrentCulture)),
    };
}
