using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cabinette.App.Views.Motion;

namespace Cabinette.App.Tests;

/// <summary>
/// Real WPF elements for motion tests, without ever showing anything: a hidden HwndSource far off-screen (WS_POPUP
/// without WS_VISIBLE), the dispatcher pumped so animation clocks advance, and each test on its own STA thread (where
/// <see cref="MotionSettings.Override"/> applies).
/// </summary>
internal static class HiddenWpf
{
    /// <summary>A window far off-screen and never shown (no WS_VISIBLE), so layout, loading and animations work without anything appearing.</summary>
    public static HwndSource Source(Visual root, int width, int height)
    {
        const int WsPopup = unchecked((int)0x80000000);
        var parameters = new HwndSourceParameters("Cabinette motion test") { Width = width, Height = height, PositionX = -20000, PositionY = -20000, WindowStyle = WsPopup };
        return new HwndSource(parameters) { RootVisual = root };
    }

    /// <summary>Dispatches messages (so animation clocks tick) for the given time.</summary>
    public static void Pump(TimeSpan duration) => PumpUntil(() => false, duration);

    /// <summary>Dispatches messages until <paramref name="done"/> holds or <paramref name="timeout"/> passes.</summary>
    public static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var elapsed = Stopwatch.StartNew();
        while (!done())
        {
            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        return true;
    }

    public static void WithMotion(bool enabled, Action test) => RunSta(() =>
    {
        MotionSettings.Override = enabled;
        try
        {
            test();
        }
        finally
        {
            MotionSettings.Override = null;
        }
    });

    public static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
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
    }
}
