using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Cabinette.App.Services;
using Cabinette.App.Services.Explorer;
using Cabinette.App.Services.Instance;
using Cabinette.Core.Models;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests from the bug hunt of the stage 3 Explorer entries (2026-10-21, reviewed at 887972a;
/// docs/review/bughunt-2026-10-21-explorer). They failed on the reviewed branch and assert what SPEC section 9 asks for.
/// Each test names its finding.
/// </summary>
public sealed class BugHuntExplorerTests
{
    // ================================================================ #1: no visible notification while the window is behind Explorer

    /// <summary>
    /// SPEC 9: an Explorer entry "shows the usual notification". The usual one (the vault monitor's) goes to a toast
    /// whenever the window isn't the active window, and to the in-app snackbar only when it is. The Explorer entries used
    /// to toast only when the window was hidden or minimized, so with the window open behind Explorer (where it is while
    /// the user right-clicks in Explorer) the outcome went to the snackbar, which dismisses itself after a few seconds
    /// unseen. Both now share <see cref="NotificationService.IsSeen"/>.
    /// </summary>
    [Fact]
    public void E1_A_batch_added_while_the_window_is_open_but_behind_explorer_is_reported_outside_the_window()
    {
        var (visible, active, minimized, onScreen) = Sta.Run(() =>
        {
            // Shown the way ScreenshotRunner.PrepareOffscreen shows windows: off-screen, no taskbar button, not activated.
            var window = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = 200,
                Height = 100,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                return (window.IsVisible, window.IsActive, window.WindowState == WindowState.Minimized, NotificationService.IsSeen(window));
            }
            finally
            {
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        // The state the user is in: Cabinette's window is open and not minimized, Explorer is in front of it.
        Assert.True(visible);
        Assert.False(active);
        Assert.False(minimized);

        // Not seen, so the outcome goes to a toast, as the vault monitor's does.
        Assert.False(onScreen);
    }

    // ================================================================ #2: Restore from first run dropped the waiting selections

    /// <summary>
    /// SPEC 9: with no vault yet, Explorer selections are added "once a vault is chosen". Restore from the first-run screen
    /// also ends with a vault, but it restarts Cabinette, and the restart used to drop the waiting selections. Now the
    /// cleanup keeps them in the data folder and the next start queues them again.
    /// </summary>
    [Fact]
    public void E2_Selections_waiting_at_a_restart_are_kept_for_the_next_start()
    {
        using var folder = new TempDir();
        var file = Path.Combine(folder.Path, "explorer-pending.json");
        var queue = new ExplorerDropQueue();
        queue.Enqueue(new ExplorerDrop(ItemMode.Copy, [@"C:\Users\you\Pictures\a.jpg", @"C:\Users\you\Pictures\Trip"]));
        queue.Enqueue(new ExplorerDrop(ItemMode.Pointer, [@"\\server\share\b.pdf"]));
        var now = DateTimeOffset.Now;

        ExplorerPendingStore.Save(file, queue.TakeAndClose(), now);
        Assert.Equal(0, queue.PendingCount);

        var kept = ExplorerPendingStore.Take(file, now.AddSeconds(5));
        Assert.Collection(
            kept,
            first =>
            {
                Assert.Equal(ItemMode.Copy, first.Mode);
                Assert.Equal([@"C:\Users\you\Pictures\a.jpg", @"C:\Users\you\Pictures\Trip"], first.Paths);
            },
            second =>
            {
                Assert.Equal(ItemMode.Pointer, second.Mode);
                Assert.Equal([@"\\server\share\b.pdf"], second.Paths);
            });
        Assert.False(File.Exists(file)); // read once
        Assert.Empty(ExplorerPendingStore.Take(file, now.AddSeconds(6)));
    }

    [Fact]
    public void E2_Kept_selections_from_a_restart_that_never_happened_are_ignored()
    {
        using var folder = new TempDir();
        var file = Path.Combine(folder.Path, "explorer-pending.json");
        var then = DateTimeOffset.Now;
        ExplorerPendingStore.Save(file, [new ExplorerDrop(ItemMode.Copy, [@"C:\a.txt"])], then);

        Assert.Empty(ExplorerPendingStore.Take(file, then + ExplorerPendingStore.MaxAge + TimeSpan.FromMinutes(1)));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void E2_Kept_selections_are_checked_like_an_add_from_the_local_channel()
    {
        using var folder = new TempDir();
        var file = Path.Combine(folder.Path, "explorer-pending.json");
        var now = DateTimeOffset.Now;
        File.WriteAllText(file, $$"""
            {"SavedAt":"{{now:O}}","Selections":[
              {"command":"add","mode":"copy","paths":["relative\\path.txt"]},
              {"command":"add","mode":"move","paths":["C:\\a.txt"]},
              {"command":"add","mode":"pointer","paths":["C:\\b.txt","C:\\B.TXT"]}]}
            """);

        var kept = Assert.Single(ExplorerPendingStore.Take(file, now));
        Assert.Equal(ItemMode.Pointer, kept.Mode);
        Assert.Equal([@"C:\b.txt"], kept.Paths);
    }

    [Fact]
    public void E2_A_damaged_file_is_ignored_and_removed()
    {
        using var folder = new TempDir();
        var file = Path.Combine(folder.Path, "explorer-pending.json");
        File.WriteAllText(file, "{ not json");

        Assert.Empty(ExplorerPendingStore.Take(file, DateTimeOffset.Now));
        Assert.False(File.Exists(file));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cabinette.App.Tests", Guid.NewGuid().ToString("N")[..8]);

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    // ================================================================ #3: the forwarded "add" has a size limit the COM path doesn't

    /// <summary>
    /// SPEC 9: Explorer hands the whole selection over "however many items are selected". The COM drop has no limit, but the copy
    /// that must pass a selection on over the local channel (another Cabinette runs that COM can't use, e.g. one started as
    /// administrator) sends it as one JSON line, and the running copy drops the connection for lines over
    /// InstanceProtocol.MaxLineLength (4 M characters; LineReader throws, ServeAsync swallows it without a reply). The forwarder
    /// then reports "Cabinette is running but didn't take the files". About 45,000 to 70,000 files, depending on path length.
    /// </summary>
    [Fact]
    public async Task E3_A_very_large_selection_passed_on_over_the_pipe_is_taken()
    {
        var name = "Cabinette-test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<ExplorerDrop>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new InstanceMessageDispatcher();
        ExplorerStartup.RegisterAddCommand(dispatcher, received.TrySetResult);
        await using var server = new InstancePipeServer(name, dispatcher.HandleLineAsync);
        server.Start();

        // Ctrl+A in a large photo folder: 60,000 files, ordinary path lengths.
        string[] paths = [.. Enumerable.Range(0, 60_000).Select(i => $@"C:\Users\you\Pictures\Camera Roll\2026\Summer holiday in Italy\Day {i % 30 + 1:D2}\IMG_{i:D6}.jpg")];
        var message = InstanceModes.AddMessage(ItemMode.Pointer, paths);
        Assert.True(InstanceProtocol.Serialize(message).Length > 4 * 1024 * 1024, "the selection must be larger than the old line limit for this test to mean anything");

        var reply = await InstancePipeClient.SendAsync(name, message, TimeSpan.FromSeconds(20));

        // On 887972a the server dropped the connection without answering (the line limit was 4 M characters).
        Assert.True(reply is { Ok: true }, $"expected the running copy to take the selection, got: {(reply is null ? "no reply" : reply.Error)}");
        var drop = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(paths.Length, drop.Paths.Count);
    }

    /// <summary>Runs a test body on a fresh STA thread (WPF windows need one) and rethrows its exception.</summary>
    private static class Sta
    {
        public static T Run<T>(Func<T> body)
        {
            T result = default!;
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = body();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(1)))
            {
                throw new TimeoutException("STA test body did not finish.");
            }

            failure?.Throw();
            return result;
        }
    }
}
