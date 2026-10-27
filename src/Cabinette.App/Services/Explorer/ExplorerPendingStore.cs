using System.IO;
using System.Text.Json;
using Cabinette.App.Services.Instance;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Services.Explorer;

/// <summary>
/// Keeps Explorer selections that were still waiting for a vault when Cabinette restarted (Restore from the first-run
/// screen restarts it) for the next start, which queues them again: they are added once the restored library is open.
/// The file lives in the data folder and is read once, then deleted. Selections are stored as the local channel's "add"
/// messages and checked the same way when read back. A file older than <see cref="MaxAge"/> is ignored: it belongs to a
/// restart that never happened, and adding those files much later would be a surprise. Not on the UI thread.
/// </summary>
internal static class ExplorerPendingStore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public static void Save(string file, IReadOnlyList<ExplorerDrop> selections, DateTimeOffset now)
    {
        try
        {
            var saved = new SavedSelections
            {
                SavedAt = now,
                Selections = [.. selections.Select(s => InstanceModes.AddMessage(s.Mode, s.Paths))],
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(saved));
            AppLog.Info($"Restarting: kept {selections.Count} waiting Explorer selection(s) for the next start.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Restarting: {selections.Count} waiting Explorer selection(s) could not be kept and are not added.", ex);
        }
    }

    /// <summary>The selections saved within <see cref="MaxAge"/> of <paramref name="now"/>; the file is deleted either way.</summary>
    public static IReadOnlyList<ExplorerDrop> Take(string file, DateTimeOffset now)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SavedSelections>(File.ReadAllText(file));
            if (saved is null || (now - saved.SavedAt).Duration() > MaxAge)
            {
                AppLog.Info("Ignored Explorer selections kept by an earlier restart: they are too old.");
                return [];
            }

            var selections = new List<ExplorerDrop>();
            foreach (var message in saved.Selections)
            {
                if (ExplorerStartup.TryReadAddMessage(message, out var selection, out _))
                {
                    selections.Add(selection);
                }
            }

            AppLog.Info($"Queued {selections.Count} Explorer selection(s) kept by the restart.");
            return selections;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Explorer selections kept by an earlier restart could not be read.", ex);
            return [];
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Deleting the kept Explorer selections failed.", ex);
            }
        }
    }

    private sealed class SavedSelections
    {
        public DateTimeOffset SavedAt { get; init; }

        public List<InstanceMessage> Selections { get; init; } = [];
    }
}
