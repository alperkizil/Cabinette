using System.Globalization;
using System.Windows.Threading;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;

namespace Cabinette.App.Tests;

/// <summary>
/// Where the grouped AI notices go (SPEC.md section 10, "Toasts"), with a fake surface and clock: nothing reaches the
/// real tray or the desktop.
/// </summary>
public sealed class AiChangeNotifierTests
{
    private const string Claude = "Claude Code";
    private const string Codex = "Codex";

    private static AiChange Added(string client, int count, string category = "Images", long id = 7)
        => new(client, AiChangeKind.FilesAdded, count) { CategoryName = category, CategoryId = id };

    [Fact]
    public void Not_looking_at_Cabinette_shows_a_toast_titled_with_the_client() => Run(t =>
    {
        t.Notifier.Report(Added(Claude, 3));
        t.Notifier.Report(new AiChange(Claude, AiChangeKind.FilesDeleted, 2));
        t.Advance(TimeSpan.FromSeconds(2));
        t.Notifier.FlushDue();

        var toast = Assert.Single(t.Surface.Toasts);
        Assert.Equal("Claude Code", toast.Title);
        Assert.Equal("3 files added to Images\n2 files deleted (Recycle Bin)", toast.Body);
        Assert.Empty(t.Surface.InApp);

        toast.OnClick();
        Assert.Equal([(new AiCategoryTarget(7, "Images"), true)], t.Opened);
    });

    [Fact]
    public void Looking_at_Cabinette_shows_an_in_app_notice_with_show() => Run(t =>
    {
        t.Surface.IsWindowSeen = true;
        t.Notifier.Report(Added(Claude, 3));
        t.FlushAfterWindow();

        var notice = Assert.Single(t.Surface.InApp);
        Assert.Equal("Claude Code added 3 files to Images", notice.Message);
        Assert.Equal(NotificationKind.Info, notice.Kind);
        Assert.Equal("Show", notice.ActionText);
        Assert.Empty(t.Surface.Toasts);

        notice.Action!();
        Assert.Equal([(new AiCategoryTarget(7, "Images"), false)], t.Opened);
    });

    [Fact]
    public void Several_categories_offer_no_show() => Run(t =>
    {
        t.Surface.IsWindowSeen = true;
        t.Notifier.Report(Added(Claude, 1, "Images", 7));
        t.Notifier.Report(Added(Claude, 1, "Audio", 8));
        t.FlushAfterWindow();

        var notice = Assert.Single(t.Surface.InApp);
        Assert.Null(notice.ActionText);
        Assert.Null(notice.Action);
    });

    [Fact]
    public void Without_a_tray_the_visible_window_gets_the_notice_and_a_hidden_one_nothing() => Run(t =>
    {
        t.Surface.IsTrayAvailable = false;
        t.Notifier.Report(Added(Claude, 1));
        t.FlushAfterWindow();
        Assert.Single(t.Surface.InApp);

        t.Surface.IsWindowVisible = false;
        t.Notifier.Report(Added(Claude, 1));
        t.FlushAfterWindow();
        Assert.Single(t.Surface.InApp);
        Assert.Empty(t.Surface.Toasts);
    });

    [Fact]
    public void Notifications_off_drop_changes_but_not_a_failed_vault_operation() => Run(t =>
    {
        t.NotificationsOn = false;
        t.Notifier.Report(Added(Claude, 3));
        t.Notifier.Report(new AiChange(Claude, AiChangeKind.OperationFailed) { Detail = "Not enough space on D:." });
        t.FlushAfterWindow();

        var toast = Assert.Single(t.Surface.Toasts);
        Assert.Equal("Vault operation stopped: Not enough space on D:.", toast.Body);
    });

    [Fact]
    public void Turning_notifications_off_before_the_notice_is_due_drops_it() => Run(t =>
    {
        t.Notifier.Report(Added(Claude, 3));
        t.NotificationsOn = false;
        t.FlushAfterWindow();

        Assert.Empty(t.Surface.Toasts);
        Assert.Equal(0, t.Notifier.PendingCount);
    });

    [Fact]
    public void A_failure_shown_in_the_window_is_a_warning() => Run(t =>
    {
        t.Surface.IsWindowSeen = true;
        t.Notifier.Report(new AiChange(Claude, AiChangeKind.OperationFailed) { Detail = "Cancelled." });
        t.FlushAfterWindow();

        Assert.Equal(NotificationKind.Warning, Assert.Single(t.Surface.InApp).Kind);
    });

    [Fact]
    public void Nothing_while_quitting() => Run(t =>
    {
        t.Quitting = true;
        t.Notifier.Report(Added(Claude, 3));
        Assert.Equal(0, t.Notifier.PendingCount);

        // Quitting after the change arrived: still nothing.
        t.Quitting = false;
        t.Notifier.Report(new AiChange(Claude, AiChangeKind.OperationFailed));
        t.Quitting = true;
        t.FlushAfterWindow();

        Assert.Empty(t.Surface.Toasts);
        Assert.Empty(t.Surface.InApp);
    });

    [Fact]
    public void Each_client_gets_its_own_merged_toast() => Run(t =>
    {
        t.Notifier.Report(Added(Claude, 2));
        t.Notifier.Report(Added(Codex, 1, "Audio", 8));
        t.Notifier.Report(Added(Claude, 1));
        t.FlushAfterWindow();

        Assert.Equal(
            [("Claude Code", "3 files added to Images"), ("Codex", "1 file added to Audio")],
            t.Surface.Toasts.Select(toast => (toast.Title, toast.Body)).ToList());
    });

    [Fact]
    public void A_notice_waits_for_quiet_but_never_longer_than_the_merge_window() => Run(t =>
    {
        t.Notifier.Report(Added(Claude, 1));
        t.Advance(TimeSpan.FromSeconds(1));
        t.Notifier.Report(Added(Claude, 1));
        t.Advance(TimeSpan.FromSeconds(1));
        t.Notifier.FlushDue();
        Assert.Empty(t.Surface.Toasts); // 1.5 s of quiet after the second change: due at 2.5 s.

        t.Advance(TimeSpan.FromSeconds(0.5));
        t.Notifier.FlushDue();
        Assert.Equal("2 files added to Images", Assert.Single(t.Surface.Toasts).Body);

        // A steady stream: shown 5 s after its first change.
        for (var second = 0; second < 5; second++)
        {
            t.Notifier.Report(Added(Claude, 1));
            t.Advance(TimeSpan.FromSeconds(1));
            t.Notifier.FlushDue();
        }

        Assert.Equal(2, t.Surface.Toasts.Count);
        Assert.Equal("5 files added to Images", t.Surface.Toasts[1].Body);
    });

    [Fact]
    public void Changes_reported_from_other_threads_reach_the_ui_thread() => Run(t =>
    {
        Task.WaitAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => t.Notifier.Report(Added(Claude, 1)))).ToArray());

        Assert.True(HiddenWpf.PumpUntil(() => t.Notifier.PendingCount == 4, TimeSpan.FromSeconds(5)));
        t.FlushAfterWindow();
        Assert.Equal("4 files added to Images", Assert.Single(t.Surface.Toasts).Body);
    });

    [Fact]
    public void The_timer_shows_the_notice_after_the_quiet_delay()
    {
        HiddenWpf.RunSta(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var surface = new FakeSurface();
            using var notifier = new AiChangeNotifier(surface, () => true, () => false, (_, _) => Task.CompletedTask, Dispatcher.CurrentDispatcher, TimeProvider.System);

            notifier.Report(Added(Claude, 1));

            Assert.True(HiddenWpf.PumpUntil(() => surface.Toasts.Count == 1, TimeSpan.FromSeconds(10)));
            Assert.Equal(0, notifier.PendingCount);
        });
    }

    [Fact]
    public void A_full_batch_is_shown_at_once() => Run(t =>
    {
        for (var i = 0; i < AiChangeNotifier.MaxPendingPerClient; i++)
        {
            t.Notifier.Report(new AiChange(Claude, AiChangeKind.FilesTagged));
        }

        Assert.Equal($"{AiChangeNotifier.MaxPendingPerClient} files tagged", Assert.Single(t.Surface.Toasts).Body);
        Assert.Equal(0, t.Notifier.PendingCount);
    });

    [Fact]
    public void After_dispose_nothing_is_kept_or_shown() => Run(t =>
    {
        t.Notifier.Report(Added(Claude, 1));
        t.Notifier.Dispose();
        t.Notifier.Report(Added(Claude, 1));
        t.FlushAfterWindow();

        Assert.Equal(0, t.Notifier.PendingCount);
        Assert.Empty(t.Surface.Toasts);
    });

    [Fact]
    public void Report_never_throws_for_bad_input() => Run(t =>
    {
        t.Notifier.Report(null!);
        t.Notifier.Report(new AiChange(null!, AiChangeKind.FilesDeleted, -3));
        t.FlushAfterWindow();

        // A negative count words as nothing; a missing client is "An AI app".
        Assert.Empty(t.Surface.Toasts);
    });

    // ------------------------------------------------------------------ harness

    private static void Run(Action<Harness> test) => HiddenWpf.RunSta(() =>
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        using var harness = new Harness();
        test(harness);
    });

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Notifier = new AiChangeNotifier(
                Surface,
                () => NotificationsOn,
                () => Quitting,
                (target, activate) =>
                {
                    Opened.Add((target, activate));
                    return Task.CompletedTask;
                },
                Dispatcher.CurrentDispatcher,
                Clock);
        }

        public FakeSurface Surface { get; } = new();

        public ManualClock Clock { get; } = new();

        public AiChangeNotifier Notifier { get; }

        public bool NotificationsOn { get; set; } = true;

        public bool Quitting { get; set; }

        public List<(AiCategoryTarget? Target, bool Activate)> Opened { get; } = [];

        public void Advance(TimeSpan by) => Clock.Now += by;

        /// <summary>Past every merge window, then shows what is due.</summary>
        public void FlushAfterWindow()
        {
            Advance(AiChangeText.MergeWindow + TimeSpan.FromSeconds(1));
            Notifier.FlushDue();
        }

        public void Dispose() => Notifier.Dispose();
    }

    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class FakeSurface : IAiNoticeSurface
    {
        public bool IsWindowSeen { get; set; }

        public bool IsWindowVisible { get; set; } = true;

        public bool IsTrayAvailable { get; set; } = true;

        public List<(string Title, string Body, Action OnClick)> Toasts { get; } = [];

        public List<(string Message, NotificationKind Kind, string? ActionText, Action? Action)> InApp { get; } = [];

        public void ShowToast(string title, string body, Action onClick) => Toasts.Add((title, body, onClick));

        public void ShowInApp(string message, NotificationKind kind, string? actionText, Action? action) => InApp.Add((message, kind, actionText, action));
    }
}
