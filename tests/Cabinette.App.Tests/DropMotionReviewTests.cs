using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Motion;
using Cabinette.App.Views.Shell;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests from the bug hunt of the drag-and-drop motion (2026-10-21, docs/review/bughunt-2026-10-21-motion).
/// They asserted the buggy behavior on the reviewed branch and now assert the fixed one. Real WPF elements live in a
/// hidden HwndSource far off-screen (WS_POPUP without WS_VISIBLE), so nothing ever shows on the desktop; the dispatcher
/// is pumped so animation clocks advance. <see cref="MotionSettings.Override"/> is per thread, and each test runs on
/// its own STA thread.
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class DropMotionReviewTests
{
    private const double RowHeight = 72;
    private const double RowStep = 74; // 72 + Margin 1 top and bottom, like Shell.RowItem

    // ------------------------------------------------------------------ #1 interrupted glide loses its jumper chrome

    [Fact]
    public void A_refresh_during_a_glide_keeps_the_jumper_on_top_and_opaque()
        => WithMotion(true, () =>
        {
            var rows = new ObservableCollection<CategoryRowViewModel>(Enumerable.Range(1, 8).Select(i => Row(i)));
            var list = BuildList(rows);
            using var source = HiddenSource(list, 380, 600);
            list.UpdateLayout();
            Assert.True(list.IsVisible, "the list must count as visible for the motion to run at all");

            var motion = new ChatListMotion(list);
            var mover = rows[4];

            // Refresh 1: the fifth row received a file and jumps to the top (last-upload sort).
            motion.Capture(rows);
            rows.Move(4, 0);
            motion.Play(rows);

            var container = Container(list, mover);
            var shift = Assert.IsType<TranslateTransform>(container.RenderTransform);
            Assert.Equal(1, Panel.GetZIndex(container));
            Assert.NotEqual(DependencyProperty.UnsetValue, Chrome(container).ReadLocalValue(Border.BackgroundProperty)); // opaque while over the others

            // Let the 420 ms glide run for a while: the row is drawn somewhere between its old and its new place.
            Assert.True(PumpUntil(() => shift.Y is > 20 and < 4 * RowStep - 20, TimeSpan.FromSeconds(2)), $"glide never got under way (Y = {shift.Y})");
            Pump(TimeSpan.FromMilliseconds(60));
            var midFlight = shift.Y;
            Assert.InRange(midFlight, 20, 4 * RowStep - 20);

            // Refresh 2, while still in flight (the second file of the same drop, the vault monitor...): the order is unchanged.
            motion.Capture(rows);
            motion.Play(rows);

            // The row keeps gliding from where it was drawn (good)...
            var same = Assert.IsType<TranslateTransform>(container.RenderTransform);
            Assert.True(PumpUntil(() => same.Y is > 5 and < 4 * RowStep, TimeSpan.FromSeconds(2)), $"the continued glide never got under way (Y = {same.Y})");
            Assert.InRange(same.Y, 5, midFlight + 1);

            // ...and keeps the chrome that makes the glide readable: on top of the rows still making room, opaque.
            // (It used to become a see-through row at z-index 0: the second refresh has no order change, so the
            // in-flight jumper was re-planned as a plain shift.)
            Assert.Equal(1, Panel.GetZIndex(container));
            Assert.NotEqual(DependencyProperty.UnsetValue, Chrome(container).ReadLocalValue(Border.BackgroundProperty));

            // Once it lands, the chrome goes.
            Assert.True(PumpUntil(() => Panel.GetZIndex(container) == 0, TimeSpan.FromSeconds(3)), "the glide never finished");
            Assert.Equal(DependencyProperty.UnsetValue, Chrome(container).ReadLocalValue(Border.BackgroundProperty));
        });

    // ------------------------------------------------------------------ #2 the search box hid the window overlay

    /// <summary>
    /// Why MainWindow takes drags over text boxes outside the thread itself (handledEventsToo): WPF's TextBox marks a
    /// file drag handled and refuses it, which used to hide the "Drop to sort automatically" overlay over the
    /// chat-list search box and drop nothing there.
    /// </summary>
    [Fact]
    public void An_editable_text_box_handles_a_file_drag_over_and_refuses_the_drop()
        => RunSta(() =>
        {
            var box = new TextBox { Width = 300, Height = 32 };
            using var source = HiddenSource(box, 320, 60);
            box.UpdateLayout();

            var e = DragArgs(new DataObject(DataFormats.FileDrop, new[] { @"C:\Windows\Media\Alarm01.wav" }), box, new Point(10, 10));
            e.RoutedEvent = DragDrop.DragOverEvent;
            box.RaiseEvent(e);

            Assert.True(e.Handled); // only a handledEventsToo handler sees it
            Assert.Equal(DragDropEffects.None, e.Effects); // so MainWindow must set Copy itself

            var drop = DragArgs(new DataObject(DataFormats.FileDrop, new[] { @"C:\Windows\Media\Alarm01.wav" }), box, new Point(10, 10));
            drop.RoutedEvent = DragDrop.DropEvent;
            box.RaiseEvent(drop);
            // The drop itself stays unhandled and the box inserts nothing, so it bubbles on to MainWindow.OnRootDrop,
            // which adds the files (OLE only delivers it once the DragOver allowed a drop, hence the fix above).
            Assert.False(drop.Handled);
            Assert.Equal("", box.Text);
        });

    // ------------------------------------------------------------------ #4 internal bubble drags at the window level

    [Fact]
    public void A_bubble_dragged_out_of_a_thread_is_not_a_file_drop_for_the_window()
    {
        var outside = new DataObject(DataFormats.FileDrop, new[] { @"C:\Windows\Media\Alarm01.wav" });
        Assert.True(MainWindow.IsOutsideFileDrop(outside));

        var bubble = new DataObject(DataFormats.FileDrop, new[] { @"C:\Windows\Media\Alarm01.wav" });
        bubble.SetData(Cabinette.App.Views.Thread.ThreadView.InternalDragFormat, true);
        Assert.False(MainWindow.IsOutsideFileDrop(bubble));

        Assert.False(MainWindow.IsOutsideFileDrop(new DataObject(DataFormats.UnicodeText, "text")));
    }

    // ------------------------------------------------------------------ #6 a recycled container kept the received pulse

    [Fact]
    public void A_row_container_recycled_during_the_receive_motion_stops_showing_the_highlight()
        => WithMotion(true, () =>
        {
            var zone = new Grid { Visibility = Visibility.Collapsed, DataContext = "row A" };

            DropZone.SetIsActive(zone, true);   // dragged over row A
            DropZone.Receive(zone);            // dropped on it
            DropZone.SetIsActive(zone, false); // highlight cleared; the 420 ms "received" motion plays on

            // The user scrolls right away: the container is recycled for row B, which is no drop target, so the binding
            // delivers false again and OnIsActiveChanged never runs. The old row's motion must not play on row B.
            zone.DataContext = "row B";
            DropZone.SetIsActive(zone, false);
            Assert.Equal(Visibility.Collapsed, zone.Visibility);
            Assert.False(zone.HasAnimatedProperties);
        });

    // ------------------------------------------------------------------ helpers

    private static CategoryRowViewModel Row(int id)
    {
        var category = new Category { Id = id, Name = "Category " + id, FolderName = "cat" + id, CreatedAt = DateTimeOffset.Now };
        return new CategoryRowViewModel(new CategorySummary { Category = category, ItemCount = 1, LastItemName = "file.txt" }, icons: null!);
    }

    private static ListBox BuildList(ObservableCollection<CategoryRowViewModel> rows)
    {
        // The same virtualization set-up as Shell.ListBox, and a row template with the "Bd" border of Shell.RowItem.
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, RowHeight));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 1, 8, 1)));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        var list = new ListBox { ItemsSource = rows, ItemContainerStyle = style, Width = 380, Height = 600 };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        return list;
    }

    private static ListBoxItem Container(ListBox list, object item) => Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));

    private static Border Chrome(ListBoxItem container) => Assert.IsType<Border>(container.Template.FindName("Bd", container));

    /// <summary>A real DragEventArgs (its constructor is internal), as WPF's drop target would build it.</summary>
    private static DragEventArgs DragArgs(IDataObject data, DependencyObject target, Point point)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single(c => c.GetParameters().Length == 5);
        return (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, target, point]);
    }

    /// <summary>A window far off-screen and never shown (no WS_VISIBLE), so layout and IsVisible work without anything appearing.</summary>
    private static HwndSource HiddenSource(Visual root, int width, int height)
    {
        const int WsPopup = unchecked((int)0x80000000);
        var parameters = new HwndSourceParameters("Cabinette motion review") { Width = width, Height = height, PositionX = -20000, PositionY = -20000, WindowStyle = WsPopup };
        return new HwndSource(parameters) { RootVisual = root };
    }

    /// <summary>Dispatches messages (so animation clocks tick) for the given time.</summary>
    private static void Pump(TimeSpan duration) => PumpUntil(() => false, duration);

    /// <summary>Dispatches messages until <paramref name="done"/> holds or <paramref name="timeout"/> passes.</summary>
    private static bool PumpUntil(Func<bool> done, TimeSpan timeout)
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

    private static void WithMotion(bool enabled, Action test) => RunSta(() =>
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

    private static void RunSta(Action action)
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
