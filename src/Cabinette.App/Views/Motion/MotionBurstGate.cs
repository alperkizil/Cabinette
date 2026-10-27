namespace Cabinette.App.Views.Motion;

/// <summary>
/// Coalesces bursts of animated changes. Up to <c>maxPerWindow</c> changes within <c>window</c> animate; more than
/// that (a vault-monitor storm, a big copy arriving file by file) starts a storm, during which changes are shown
/// without motion until things have been quiet for <c>calmDown</c>. Time is passed in, so it is easy to test.
/// </summary>
internal sealed class MotionBurstGate
{
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly TimeSpan _calmDown;
    private readonly Queue<DateTimeOffset> _recent = new();
    private DateTimeOffset? _last;

    public MotionBurstGate(int maxPerWindow, TimeSpan window, TimeSpan calmDown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerWindow, 1);
        _maxPerWindow = maxPerWindow;
        _window = window;
        _calmDown = calmDown;
    }

    /// <summary>The chat list's gate: 4 animated refreshes per 2 seconds, then still until 1.5 seconds of quiet.</summary>
    public static MotionBurstGate ForChatList() => new(4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5));

    public bool IsStorm { get; private set; }

    /// <summary>Records a change at <paramref name="now"/>. True: animate it. False: show it without motion.</summary>
    public bool TryAnimate(DateTimeOffset now)
    {
        if (IsStorm && _last is { } last && now - last >= _calmDown)
        {
            IsStorm = false;
            _recent.Clear();
        }

        _last = now;
        while (_recent.Count > 0 && now - _recent.Peek() >= _window)
        {
            _recent.Dequeue();
        }

        _recent.Enqueue(now);
        if (_recent.Count > _maxPerWindow)
        {
            IsStorm = true;
        }

        return !IsStorm;
    }
}
