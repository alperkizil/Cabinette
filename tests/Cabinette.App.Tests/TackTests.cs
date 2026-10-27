using System.Windows;
using System.Windows.Media;
using Cabinette.App.ViewModels.Thread;
using Cabinette.App.Views.Thread;

namespace Cabinette.App.Tests;

/// <summary>
/// The tacks on chat bubbles (SPEC.md section 4): eight push-pin colors picked from the file's id, so a file keeps its color,
/// neighbors differ, and each bubble's outer top corner carries one (top-right on copies, top-left and mirrored on pointers).
/// </summary>
public class TackTests
{
    [Fact]
    public void There_are_eight_different_tack_colors()
    {
        Assert.Equal(8, Tack.Colors.Length);
        Assert.Equal(8, Tack.Colors.Distinct().Count());
    }

    [Fact]
    public void A_file_always_gets_the_same_color()
    {
        foreach (var id in new long[] { 0, 1, 7, 42, 1_000_003, long.MaxValue, -5 })
        {
            var color = Tack.ColorIndex(id);
            Assert.InRange(color, 0, 7);
            Assert.Equal(color, Tack.ColorIndex(id));
        }
    }

    [Fact]
    public void Files_added_one_after_another_get_different_colors_and_all_eight_are_used_evenly()
    {
        var colors = Enumerable.Range(1, 10_000).Select(id => Tack.ColorIndex(id)).ToArray();

        // Any five files in a row (by id) have five different colors, so neighbors never match.
        for (var start = 0; start + 5 <= colors.Length; start++)
        {
            Assert.Equal(5, colors.AsSpan(start, 5).ToArray().Distinct().Count());
        }

        Assert.Equal(8, colors.Distinct().Count());
        Assert.All(colors.GroupBy(c => c), group => Assert.InRange(group.Count(), 1200, 1300));
    }

    [Fact]
    public void Every_chat_bubble_has_a_tack_with_its_file_id_on_its_outer_corner() => GalleryTests.RunOnDispatcher(async () =>
    {
        // Travel 2026 has single bubbles and an album, all pointers; Notes has copies.
        foreach (var name in new[] { "Travel 2026", "Notes" })
        {
            var vm = await BoldTextTests.OpenThreadAsync(name);
            var view = new ThreadView { DataContext = vm };
            var host = BoldTextTests.WindowLikeHost(view);
            using var source = HiddenWpf.Source(host, 1000, 900);
            await GalleryReviewSupport.SettleAsync(view);

            var tacks = Descendants(view).OfType<Tack>().Where(t => t.IsVisible).ToList();
            Assert.NotEmpty(tacks);
            foreach (var tack in tacks)
            {
                var (id, isPointer) = tack.DataContext switch
                {
                    ItemRow row => (row.Item.Id, row.IsPointer),
                    AlbumRow album => (album.Last.Id, album.IsPointer),
                    var other => throw new Xunit.Sdk.XunitException($"{name}: a tack on {other?.GetType().Name ?? "nothing"}"),
                };

                Assert.Equal(id, tack.Seed);
                Assert.False(tack.IsHitTestVisible);
                if (isPointer)
                {
                    Assert.Equal(HorizontalAlignment.Left, tack.HorizontalAlignment);
                    Assert.Equal(-1, Assert.IsType<ScaleTransform>(tack.RenderTransform).ScaleX);
                }
                else
                {
                    Assert.Equal(HorizontalAlignment.Right, tack.HorizontalAlignment);
                    Assert.True(tack.RenderTransform is null || tack.RenderTransform.Value.IsIdentity);
                }
            }

            var bubbles = Descendants(view).OfType<FrameworkElement>().Count(e => e.Name == "Bubble" && e.IsVisible);
            Assert.Equal(bubbles, tacks.Count);
            vm.Dispose();
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child))
            {
                yield return below;
            }
        }
    }
}
