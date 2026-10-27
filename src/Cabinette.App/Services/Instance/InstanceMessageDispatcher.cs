using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Instance;

/// <summary>
/// Routes incoming messages to handlers by command. Unknown commands, bad lines and failing handlers are answered
/// with an error reply and never throw, so a bad client cannot disturb the running app.
/// </summary>
public sealed class InstanceMessageDispatcher
{
    private readonly Dictionary<string, Func<InstanceMessage, Task<InstanceReply>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public void Register(string command, Func<InstanceMessage, Task<InstanceReply>> handler)
    {
        lock (_gate)
        {
            _handlers[command] = handler;
        }
    }

    public bool IsRegistered(string command)
    {
        lock (_gate)
        {
            return _handlers.ContainsKey(command);
        }
    }

    public async Task<InstanceReply> DispatchAsync(InstanceMessage message)
    {
        Func<InstanceMessage, Task<InstanceReply>>? handler;
        lock (_gate)
        {
            _handlers.TryGetValue(message.Command, out handler);
        }

        if (handler is null)
        {
            AppLog.Info($"Ignored an unknown command from another instance: '{Truncate(message.Command)}'.");
            return InstanceReply.Failed($"unknown command '{Truncate(message.Command)}'");
        }

        try
        {
            return await handler(message).ConfigureAwait(false) ?? InstanceReply.Success;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Handling '{message.Command}' from another instance failed.", ex);
            return InstanceReply.Failed("the command failed");
        }
    }

    /// <summary>One line in, one reply line out.</summary>
    public async Task<string> HandleLineAsync(string line)
    {
        if (!InstanceProtocol.TryParseMessage(line, out var message, out var error))
        {
            AppLog.Info($"Ignored a malformed message from another instance ({error}).");
            return InstanceProtocol.Serialize(InstanceReply.Failed(error));
        }

        var reply = await DispatchAsync(message).ConfigureAwait(false);
        return InstanceProtocol.Serialize(reply);
    }

    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40] + "…";
}

/// <summary>The "add" command's mode names ("copy", "pointer") as <see cref="ItemMode"/>.</summary>
public static class InstanceModes
{
    /// <summary>"copy" or "pointer", as the "add" command spells the mode.</summary>
    public static string Name(ItemMode mode) => mode == ItemMode.Pointer ? InstanceProtocol.PointerMode : InstanceProtocol.CopyMode;

    /// <summary>Reads an "add" mode ("copy" or "pointer", already trimmed and lower-cased by <see cref="InstanceProtocol.TryParseMessage"/>).</summary>
    public static bool TryParse(string? text, out ItemMode mode)
    {
        switch (text)
        {
            case InstanceProtocol.CopyMode:
                mode = ItemMode.Copy;
                return true;
            case InstanceProtocol.PointerMode:
                mode = ItemMode.Pointer;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>An "add" message for one Explorer selection.</summary>
    public static InstanceMessage AddMessage(ItemMode mode, IReadOnlyList<string> paths) => InstanceMessage.Add(Name(mode), paths);
}
