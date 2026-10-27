namespace Cabinette.App.Views.Motion;

/// <summary>What one chat-list refresh animates.</summary>
/// <param name="Glide">Moved rows glide to their new places (and new rows fade in).</param>
/// <param name="Pop">Unread badges that appeared or grew pop.</param>
internal readonly record struct ChatListMotionPlan(bool Glide, bool Pop)
{
    public static ChatListMotionPlan None => default;
}

/// <summary>
/// Decides how much of a chat-list refresh animates, so the list stays calm when many things change at once.
/// </summary>
internal static class ChatListMotionRules
{
    /// <summary>More rows changing order than this (a re-sort, a storm) are shown in place without gliding.</summary>
    public const int MaxJumpers = 3;

    /// <summary>More badges growing at once than this don't pop.</summary>
    public const int MaxBadgePops = 4;

    /// <param name="motionEnabled">See <see cref="MotionSettings"/>.</param>
    /// <param name="moves">Rows that would glide or fade in.</param>
    /// <param name="jumpers">Rows among them that changed order.</param>
    /// <param name="badgePops">Visible badges that appeared or grew.</param>
    /// <param name="gate">Bursts of refreshes; only refreshes with something to animate are counted.</param>
    /// <param name="now">The time of this refresh.</param>
    public static ChatListMotionPlan Decide(bool motionEnabled, int moves, int jumpers, int badgePops, MotionBurstGate gate, DateTimeOffset now)
    {
        if (!motionEnabled || (moves == 0 && badgePops == 0))
        {
            return ChatListMotionPlan.None;
        }

        if (!gate.TryAnimate(now))
        {
            return ChatListMotionPlan.None;
        }

        return new ChatListMotionPlan(moves > 0 && jumpers <= MaxJumpers, badgePops > 0 && badgePops <= MaxBadgePops);
    }
}
