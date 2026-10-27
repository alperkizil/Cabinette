using Cabinette.App.Views.Motion;

namespace Cabinette.App.Tests;

/// <summary>The pure parts of the drag-and-drop motion: the chat list's FLIP offsets, its rules and the burst gate.</summary>
public sealed class DropMotionLogicTests
{
    private const double RowHeight = 74;
    private const double Viewport = 600;

    // ------------------------------------------------------------------ which rows changed order

    [Fact]
    public void A_category_moving_to_the_top_is_the_only_jumper()
    {
        string[] before = ["Images", "Work", "Documents", "Notes", "Audio", "Video"];
        string[] after = ["Video", "Images", "Work", "Documents", "Notes", "Audio"];

        Assert.Equal(["Video"], FlipPlanner.FindJumpers(before, after));
    }

    [Fact]
    public void A_category_moving_below_the_pins_is_the_only_jumper()
    {
        string[] before = ["Images", "Work", "Documents", "Notes", "Code", "Links"];
        string[] after = ["Images", "Work", "Code", "Documents", "Notes", "Links"];

        Assert.Equal(["Code"], FlipPlanner.FindJumpers(before, after));
    }

    [Fact]
    public void An_unchanged_order_has_no_jumpers()
    {
        string[] rows = ["A", "B", "C"];

        Assert.Empty(FlipPlanner.FindJumpers(rows, rows));
    }

    [Fact]
    public void On_a_swap_the_row_that_moved_up_is_the_jumper()
    {
        Assert.Equal(["B"], FlipPlanner.FindJumpers(["A", "B", "C"], ["B", "A", "C"]));
    }

    [Fact]
    public void Reversing_the_list_makes_all_but_one_row_jump()
    {
        string[] before = ["A", "B", "C", "D", "E"];

        Assert.Equal(4, FlipPlanner.FindJumpers(before, before.Reverse().ToArray()).Count);
    }

    [Fact]
    public void Added_and_removed_rows_are_not_jumpers()
    {
        // "N" is new and "C" is gone: the others kept their order.
        Assert.Empty(FlipPlanner.FindJumpers(["A", "B", "C", "D"], ["N", "A", "B", "D"]));
    }

    // ------------------------------------------------------------------ FLIP offsets

    [Fact]
    public void A_realized_row_starts_where_it_was_drawn()
    {
        var moves = Plan(new FlipRow<string>("Code", 6, 2, OldTop: 444, NewTop: 148, RowHeight), jumpers: ["Code"]);

        var move = Assert.Single(moves);
        Assert.Equal(296, move.Offset);
        Assert.True(move.IsJumper);
    }

    [Fact]
    public void The_rows_making_room_shift_by_one_row_and_are_not_jumpers()
    {
        var moves = Plan(new FlipRow<string>("Documents", 2, 3, OldTop: 148, NewTop: 222, RowHeight));

        var move = Assert.Single(moves);
        Assert.Equal(-RowHeight, move.Offset);
        Assert.False(move.IsJumper);
    }

    [Fact]
    public void A_row_that_was_below_the_visible_rows_rises_in_from_the_bottom_edge()
    {
        var moves = Plan(new FlipRow<string>("Video", 40, 2, OldTop: null, NewTop: 148, RowHeight));

        Assert.Equal(Viewport - 148, Assert.Single(moves).Offset);
    }

    [Fact]
    public void A_row_that_was_above_the_visible_rows_drops_in_from_the_top_edge()
    {
        var moves = Plan(new FlipRow<string>("Archives", 0, 5, OldTop: null, NewTop: 370, RowHeight));

        Assert.Equal(-(370 + RowHeight), Assert.Single(moves).Offset);
    }

    [Fact]
    public void A_long_jump_starts_just_outside_the_visible_area()
    {
        // Realized far below (the virtualization cache), now at the top: it starts at the bottom edge, not 1500 px away.
        var fromBelow = Plan(new FlipRow<string>("Far", 30, 0, OldTop: 1500, NewTop: 0, RowHeight));
        var fromAbove = Plan(new FlipRow<string>("Far", 0, 30, OldTop: -900, NewTop: 300, RowHeight));

        Assert.Equal(Viewport, Assert.Single(fromBelow).Offset);
        Assert.Equal(-(300 + RowHeight), Assert.Single(fromAbove).Offset);
    }

    [Fact]
    public void Rows_that_stayed_put_new_rows_and_rows_never_on_screen_are_not_moved()
    {
        var moves = PlanAll(
            new FlipRow<string>("Same", 1, 1, OldTop: 74, NewTop: 74.2, RowHeight),
            new FlipRow<string>("New", -1, 0, OldTop: null, NewTop: 0, RowHeight),
            new FlipRow<string>("Hidden", 20, 21, OldTop: 1480, NewTop: 1554, RowHeight),
            new FlipRow<string>("Scrolled", 9, 9, OldTop: null, NewTop: 500, RowHeight));

        Assert.Empty(moves);
    }

    [Fact]
    public void A_row_leaving_the_visible_area_still_glides_out()
    {
        var moves = Plan(new FlipRow<string>("Out", 1, 12, OldTop: 74, NewTop: 888, RowHeight));

        Assert.Equal(74 - 888, Assert.Single(moves).Offset);
    }

    // ------------------------------------------------------------------ coalescing bursts

    [Fact]
    public void The_gate_lets_four_changes_animate_then_waits_for_quiet()
    {
        var gate = MotionBurstGate.ForChatList();
        var t = DateTimeOffset.UnixEpoch;

        // Four changes 250 ms apart animate; the fifth within 2 s starts a storm.
        Assert.True(gate.TryAnimate(t));
        Assert.True(gate.TryAnimate(t.AddMilliseconds(250)));
        Assert.True(gate.TryAnimate(t.AddMilliseconds(500)));
        Assert.True(gate.TryAnimate(t.AddMilliseconds(750)));
        Assert.False(gate.TryAnimate(t.AddMilliseconds(1000)));
        Assert.True(gate.IsStorm);

        // While changes keep coming (each less than 1.5 s after the last), the storm goes on, even past the window.
        Assert.False(gate.TryAnimate(t.AddMilliseconds(2200)));
        Assert.False(gate.TryAnimate(t.AddMilliseconds(3600)));

        // 1.5 s of quiet ends it.
        Assert.True(gate.TryAnimate(t.AddMilliseconds(5100)));
        Assert.False(gate.IsStorm);
    }

    [Fact]
    public void Changes_spread_out_over_time_always_animate()
    {
        var gate = MotionBurstGate.ForChatList();
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(gate.TryAnimate(t.AddMilliseconds(i * 600)));
        }
    }

    [Fact]
    public void Nothing_animates_with_animations_off_and_the_gate_is_not_used()
    {
        var gate = new MotionBurstGate(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var t = DateTimeOffset.UnixEpoch;

        Assert.Equal(ChatListMotionPlan.None, ChatListMotionRules.Decide(false, 3, 1, 1, gate, t));
        Assert.Equal(ChatListMotionPlan.None, ChatListMotionRules.Decide(true, 0, 0, 0, gate, t));

        // The gate (one per window) still has its one change left.
        Assert.Equal(new ChatListMotionPlan(true, true), ChatListMotionRules.Decide(true, 3, 1, 1, gate, t));
        Assert.Equal(ChatListMotionPlan.None, ChatListMotionRules.Decide(true, 3, 1, 1, gate, t.AddMilliseconds(100)));
    }

    [Fact]
    public void A_resort_shows_in_place_and_a_crowd_of_badges_does_not_pop()
    {
        var t = DateTimeOffset.UnixEpoch;

        Assert.Equal(
            new ChatListMotionPlan(Glide: false, Pop: true),
            ChatListMotionRules.Decide(true, 12, ChatListMotionRules.MaxJumpers + 1, 1, MotionBurstGate.ForChatList(), t));
        Assert.Equal(
            new ChatListMotionPlan(Glide: true, Pop: false),
            ChatListMotionRules.Decide(true, 4, 1, ChatListMotionRules.MaxBadgePops + 1, MotionBurstGate.ForChatList(), t));
    }

    [Fact]
    public void With_A_to_Z_sort_only_the_badge_pops()
    {
        // The row didn't move (no moves, no jumpers), its badge grew.
        Assert.Equal(
            new ChatListMotionPlan(Glide: false, Pop: true),
            ChatListMotionRules.Decide(true, 0, 0, 1, MotionBurstGate.ForChatList(), DateTimeOffset.UnixEpoch));
    }

    private static List<FlipMove<string>> Plan(FlipRow<string> row, HashSet<string>? jumpers = null)
        => FlipPlanner.Plan([row], Viewport, jumpers ?? []);

    private static List<FlipMove<string>> PlanAll(params FlipRow<string>[] rows) => FlipPlanner.Plan(rows, Viewport, new HashSet<string>());
}
