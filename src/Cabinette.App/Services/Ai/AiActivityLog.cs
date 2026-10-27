using Cabinette.Core.Models;
using Cabinette.Core.Services;

namespace Cabinette.App.Services.Ai;

/// <summary>The AI activity log on top of the library's activity_log table.</summary>
public sealed class AiActivityLog : IAiActivityLog
{
    private readonly ILibraryService _library;
    private readonly TimeProvider _time;

    public AiActivityLog(ILibraryService library, TimeProvider? time = null)
    {
        _library = library;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<ActivityEntry>? EntryAdded;

    public async Task<ActivityEntry?> LogAsync(string client, string tool, AiActivityDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ActivityEntry stored;
        try
        {
            stored = await _library.AddActivityAsync(new ActivityEntry
            {
                At = _time.GetLocalNow(),
                Source = client,
                Action = tool,
                DetailsJson = details.ToJson(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"The AI activity log entry for '{tool}' could not be stored.", ex);
            return null;
        }

        try
        {
            EntryAdded?.Invoke(this, stored);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Telling the activity viewer about a new entry failed.", ex);
        }

        return stored;
    }

    public Task<ActivityPage> GetAsync(ActivityQuery query, CancellationToken ct = default) => _library.GetActivityAsync(query, ct);
}
