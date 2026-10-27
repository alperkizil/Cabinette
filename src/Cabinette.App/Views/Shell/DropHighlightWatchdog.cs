using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Cabinette.App.Views.Shell;

/// <summary>
/// Keeps a drag-and-drop highlight from getting stuck. DragLeave does not always fire with a position outside the
/// control (a drag cancelled with Esc, or a cursor leaving the window quickly), which left the "Drop to add" highlight
/// on screen for good. After the last drag event, this checks the physical mouse buttons: while one is still held the
/// drag is in progress and the highlight stays; once they are all released the drag is over and it is cleared.
/// </summary>
internal sealed class DropHighlightWatchdog
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    private readonly DispatcherTimer _timer;

    public DropHighlightWatchdog(Action clear)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            if (IsDown(VkLButton) || IsDown(VkRButton) || IsDown(VkMButton))
            {
                return; // Still dragging; check again on the next tick.
            }

            _timer.Stop();
            clear();
        };
    }

    /// <summary>Call on every DragEnter/DragOver/DragLeave.</summary>
    public void Touch()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Call when the highlight was cleared for another reason (drop, left the control).</summary>
    public void Stop() => _timer.Stop();

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
