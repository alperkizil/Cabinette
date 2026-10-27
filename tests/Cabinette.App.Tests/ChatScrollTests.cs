using System.Windows;
using System.Windows.Controls;
using Cabinette.App.Views.Thread;

namespace Cabinette.App.Tests;

/// <summary>
/// The chat's "stick to the end" (ThreadView.OnChatScrollChanged). With the tacks' extra spacing, a jump to the top of a long
/// chat re-estimated the rows' heights in the same pass, and the extent change pulled the chat straight back to its end: the
/// user couldn't get to the top. A jump away from the end now lets go; a chat at its end still stays there when it resizes.
/// </summary>
[Collection(MotionTiming.Name)]
public sealed class ChatScrollTests
{
    [Fact]
    public void A_jump_to_the_top_of_a_long_chat_stays_at_the_top() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync(GalleryReviewSupport.Files(300));
        var view = new ThreadView { DataContext = vm };
        using var source = HiddenWpf.Source(view, 1000, 700);
        await GalleryReviewSupport.SettleAsync(view);
        vm.IsChatView = true;
        await GalleryReviewSupport.SettleAsync(view);
        var chat = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.ChatList)!;
        Assert.True(chat.ScrollableHeight > 1000, $"scrollable {chat.ScrollableHeight}");
        Assert.True(chat.VerticalOffset >= chat.ScrollableHeight - 2, "a chat opens at its end");

        chat.ScrollToTop();
        await GalleryReviewSupport.SettleAsync(view);
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, chat.VerticalOffset);
        vm.Dispose();
    });

    [Fact]
    public void A_chat_at_its_end_stays_at_its_end_when_it_gets_shorter_or_taller() => GalleryTests.RunOnDispatcher(async () =>
    {
        var (vm, _) = await GalleryReviewSupport.OpenAsync(GalleryReviewSupport.Files(300));
        var view = new ThreadView { DataContext = vm };
        var host = new Border { Child = view, Height = 700, VerticalAlignment = VerticalAlignment.Top };
        using var source = HiddenWpf.Source(host, 1000, 900);
        await GalleryReviewSupport.SettleAsync(view);
        vm.IsChatView = true;
        await GalleryReviewSupport.SettleAsync(view);
        var chat = GalleryReviewSupport.FindVisualChild<ScrollViewer>(view.ChatList)!;

        foreach (var height in new[] { 500.0, 880.0, 640.0 })
        {
            host.Height = height;
            await GalleryReviewSupport.SettleAsync(view);
            Assert.True(chat.VerticalOffset >= chat.ScrollableHeight - 2, $"at {height}: offset {chat.VerticalOffset} of {chat.ScrollableHeight}");
        }

        vm.Dispose();
    });
}
