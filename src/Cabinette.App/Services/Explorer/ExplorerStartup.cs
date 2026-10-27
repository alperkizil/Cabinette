using System.IO;
using Cabinette.App.Services.Instance;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Services.Explorer;

/// <summary>What this process does once it knows whether it is the running Cabinette.</summary>
public enum StartupRole
{
    /// <summary>This process is Cabinette (it holds the single-instance mutex).</summary>
    Primary,

    /// <summary>Another copy runs: ask it to show its window, then exit.</summary>
    ActivateRunning,

    /// <summary>Another copy runs and this one was started with <c>--tray</c>: exit without doing anything.</summary>
    ExitQuietly,

    /// <summary>
    /// COM started this process for an Explorer entry, but another copy runs (COM couldn't use it, for example because it
    /// runs as administrator): take the drop, pass it on over the local channel, exit.
    /// </summary>
    ForwardExplorerDrop,
}

/// <summary>Which Explorer entries' COM classes a process serves.</summary>
public enum ExplorerClasses
{
    /// <summary>Screenshot and sample-data runs: none.</summary>
    None,

    /// <summary>The installed entries (<see cref="ExplorerIntegration.CopyDropTargetClsid"/>, ...).</summary>
    Installed,

    /// <summary><c>--data-dir</c> dev runs: the dev entries, so they never take files meant for the installed Cabinette.</summary>
    Dev,
}

/// <summary>The Explorer-integration decisions made at startup, kept free of UI so they can be tested.</summary>
public static class ExplorerStartup
{
    /// <summary>How long a copy started only to pass a drop on waits for Explorer's Drop call before giving up.</summary>
    public static readonly TimeSpan ForwarderWaitForDrop = TimeSpan.FromSeconds(30);

    /// <summary>How long that copy tries to reach the running Cabinette.</summary>
    public static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(15);

    public static StartupRole Decide(bool isPrimary, CommandLineOptions options)
    {
        if (isPrimary)
        {
            return StartupRole.Primary;
        }

        if (options.IsEmbedding)
        {
            return StartupRole.ForwardExplorerDrop;
        }

        return options.StartInTray ? StartupRole.ExitQuietly : StartupRole.ActivateRunning;
    }

    public static ExplorerClasses ClassesFor(CommandLineOptions options)
    {
        if (options.IsSandboxed)
        {
            return ExplorerClasses.None;
        }

        return options.DataDirectory is not null ? ExplorerClasses.Dev : ExplorerClasses.Installed;
    }

    /// <summary>The server for <paramref name="classes"/>, or null for <see cref="ExplorerClasses.None"/>.</summary>
    public static IExplorerDropServer? CreateServer(ExplorerClasses classes) => classes switch
    {
        ExplorerClasses.Installed => ExplorerDropServer.ForInstalledApp(),
        ExplorerClasses.Dev => ExplorerDropServer.ForDevRuns(),
        _ => null,
    };

    /// <summary>
    /// Validates an "add" message from another instance: the mode must be "copy" or "pointer", and there must be at least
    /// one path, every one of them fully qualified (<c>C:\…</c> or <c>\\server\share\…</c>). Nothing is read from disk here.
    /// </summary>
    public static bool TryReadAddMessage(InstanceMessage message, out ExplorerDrop drop, out string error)
    {
        drop = new ExplorerDrop(default, []);
        if (!InstanceModes.TryParse(message.Mode, out var mode))
        {
            error = "mode must be 'copy' or 'pointer'";
            return false;
        }

        if (message.Paths is not { Count: > 0 } paths)
        {
            error = "no paths";
            return false;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                error = "every path must be a full path";
                return false;
            }
        }

        drop = new ExplorerDrop(mode, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        error = "";
        return true;
    }

    /// <summary>
    /// Handles "add" on the local channel: a valid message is passed to <paramref name="accept"/> (the same path as a drop),
    /// which returns false when Cabinette can't take files any more (it is closing, or first run ended without a vault).
    /// </summary>
    public static void RegisterAddCommand(InstanceMessageDispatcher dispatcher, Func<ExplorerDrop, bool> accept)
    {
        dispatcher.Register(InstanceCommands.Add, message =>
        {
            if (!TryReadAddMessage(message, out var drop, out var error))
            {
                AppLog.Info($"Refused an add message from another instance ({error}).");
                return Task.FromResult(InstanceReply.Failed(error));
            }

            return Task.FromResult(accept(drop) ? InstanceReply.Success : InstanceReply.Failed("Cabinette can't add files right now"));
        });
    }
}
