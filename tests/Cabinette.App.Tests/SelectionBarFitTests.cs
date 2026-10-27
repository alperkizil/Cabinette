using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Cabinette.App.Services;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>
/// The thread's selection bar (it was cut off at extra-large text and the narrowest window): from a wide
/// pane down to the narrowest one, with Bold text and a wide font, every action stays whole and nothing overlaps, the bar
/// settles instead of flipping between two looks, and switching Bold text or the font fits it again.
/// </summary>
[Collection(MotionTiming.Name)]
public class SelectionBarFitTests
{
    [Theory]
    [InlineData(false, AppFont.SegoeUIVariable)]
    [InlineData(true, AppFont.SegoeUIVariable)]
    [InlineData(true, AppFont.JetBrainsMono)]
    [InlineData(true, AppFont.Lexend)]
    public void Every_action_fits_without_overlapping_at_any_width(bool bold, AppFont font) => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, view, host) = await OpenAsync(bold, font, width: 900);
        using var source = HiddenWpf.Source(host, 900, 700);
        vm.SelectOnly(vm.Items.TakeLast(3).ToList());

        // 312 is the pane at the narrowest window with extra-large text (900 px / 1.3 - the 380 of the chat list).
        foreach (var width in new[] { 900, 700, 560, 480, 420, 380, 340, 312 })
        {
            host.Width = width;
            await GalleryReviewSupport.SettleAsync(view);
            var layouts = CountLayouts(view);
            Assert.True(layouts <= 3, $"at {width}: the bar laid out {layouts} times in a moment (it keeps changing its look)");
            CheckBar(view, $"{font}, bold {bold}, width {width}");
        }

        vm.Dispose();
    });

    [Fact]
    public void Switching_bold_text_or_the_font_fits_the_bar_again() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, view, host) = await OpenAsync(bold: false, AppFont.SegoeUIVariable, width: 560);
        using var source = HiddenWpf.Source(host, 560, 700);
        vm.SelectOnly(vm.Items.TakeLast(3).ToList());
        await GalleryReviewSupport.SettleAsync(view);
        CheckBar(view, "regular");

        UiWeight.Apply(true, host.Resources);
        UiFont.Apply(AppFont.JetBrainsMono, host.Resources);
        await GalleryReviewSupport.SettleAsync(view);
        CheckBar(view, "bold, JetBrains Mono");

        UiWeight.Apply(false, host.Resources);
        UiFont.Apply(AppFont.SegoeUIVariable, host.Resources);
        await GalleryReviewSupport.SettleAsync(view);
        CheckBar(view, "regular again");
        vm.Dispose();
    });

    private static async Task<(ViewModels.Thread.ThreadViewModel Vm, ThreadView View, Border Host)> OpenAsync(bool bold, AppFont font, double width)
    {
        var vm = await BoldTextTests.OpenThreadAsync("Images");
        var view = new ThreadView { DataContext = vm };
        var host = BoldTextTests.WindowLikeHost(view);
        host.Width = width;
        host.HorizontalAlignment = HorizontalAlignment.Left;
        UiWeight.Apply(bold, host.Resources);
        UiFont.Apply(font, host.Resources);
        host.SetResourceReference(TextElement.FontFamilyProperty, UiFont.TextKey);
        return (vm, view, host);
    }

    /// <summary>All seven actions inside the bar, after "Select all" and each after the one before.</summary>
    private static void CheckBar(ThreadView view, string when)
    {
        var bar = (FrameworkElement)view.FindName("SelectionBar");
        var selectAll = (FrameworkElement)view.FindName("SelectAllButton");
        var actions = ((Panel)view.FindName("SelectionActions")).Children.OfType<Button>().ToList();
        Assert.Equal(7, actions.Count);
        var previous = selectAll.TranslatePoint(new Point(selectAll.ActualWidth, 0), bar).X;
        foreach (var action in actions)
        {
            var left = action.TranslatePoint(default, bar).X;
            Assert.True(left >= previous - 0.5, $"{when}: an action starts at {left:0.#}, over what ends at {previous:0.#}");
            previous = left + action.ActualWidth;
        }

        Assert.True(previous <= bar.ActualWidth + 0.5, $"{when}: the last action ends at {previous:0.#}, past the bar's {bar.ActualWidth:0.#}");
    }

    private static int CountLayouts(FrameworkElement view)
    {
        var bar = (FrameworkElement)view.FindName("SelectionBar");
        var count = 0;
        void Count(object? sender, EventArgs e) => count++;
        bar.LayoutUpdated += Count;
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(150));
        bar.LayoutUpdated -= Count;
        return count;
    }
}
