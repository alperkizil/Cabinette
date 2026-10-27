# Bug hunt, drag-and-drop motion (2026-10-21)

**Status: 5 of 6 findings fixed on 2026-10-21** after the merge of the motion branch (`a8c340c`); #5 turned out not to be a
bug (checked on the user's screen on 2026-10-22).

- **Reviewed:** branch `worktree-agent-aa395aa8c3dd3a16d` (`e89215d`), the "playful" drag-and-drop animations:
  `Views/Motion/*`, `ChatListMotion`, the drop overlays in `ThreadView`, `ChatListPane`, `EmptyStateView`, and the new
  window overlay in `MainWindow`.
- **Reviewer:** Claude Fable 5.1, with repro tests on real WPF elements in a hidden, never-shown window, and off-screen
  renders (`--screenshot`).
- **Verification:** the integrator (Claude Opus 5.5) re-checked each finding against the code. The two repro tests
  that became regression tests were run against the unfixed code: both failed there, and both pass now.
- **Scoring:** as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md): Impact (1–5) × Likelihood (1–3).
- **Regression tests:** `tests/Cabinette.App.Tests/DropMotionReviewTests.cs`.
- **Reviewer's overall verdict:** the state machines are sound. It traced every transition of the highlights and the
  recycling paths and couldn't get a highlight stuck, half-scaled, or a stale transform onto a recycled row.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 4 (2×2) | A second refresh while a row was gliding to the top (more files for the same category within ~0.4 s) re-planned it as a plain shift. It lost its z-order and opaque background, so it slid see-through under the rows making room. | Rows mid-glide are remembered (`ChatListMotion` `MotionJumping`) and stay jumpers when re-planned. |
| 2 | 4 (2×2) | Over the chat-list search box, WPF's TextBox marks a file drag handled with "no drop". The "Drop to sort automatically" overlay blinked off, the cursor said "can't drop", and nothing could be dropped there. | `MainWindow` takes drags over text boxes outside the thread (a handledEventsToo DragOver handler); the drop then reaches `OnRootDrop` as usual. |
| 3 | 4 (2×2) | Crossing the pane splitter on the way into the thread flashed the window overlay (no grace period on show). | The window overlay shows only after 120 ms of hovering; a pane taking the drag cancels it. |
| 4 | 2 (2×1) | A bubble dragged out of a thread (it carries its files) got the window overlay, and dropping it re-added your own files. | `MainWindow.IsOutsideFileDrop` ignores drags that carry `ThreadView.InternalDragFormat`. |
| 5 | — | While the thread or window overlay fades, renders show a pale "veil" over neighbouring content. | **Not a bug** (closed 2026-10-22): it is the card being see-through mid-fade, so a dark picture behind it shows through as a grey box. Checked on the user's screen with a real drag; the user is fine with it. |
| 6 | 1 (1×1) | A row container recycled during the "received" pulse (scrolling right after a drop) kept showing the pulse on the new row. | `DropZone` stops a fade or pulse when the zone's DataContext changes. |

Also fixed: `MotionSettings.Override` was a process-wide static, so tests that force motion on or off could affect
each other when xUnit ran them in parallel. It is now per thread (WPF elements belong to one thread anyway).

## Leads the reviewer dropped (kept for the record)
The watchdog vs the animations; enter/leave/enter, a drop mid-animation, Esc; `FillBehavior`; recycling resets; the
FLIP math (also with pinned rows); the balance of `RowsChanging`/`RowsChanged`; coalescing; hit testing; leaks; theme
brushes. All hold. A row moving to a place outside the visible range just disappears instead of gliding out
(cosmetic).

## Not covered
Real OLE drags (handover timing, Esc, drops on the title bar, high DPI), toggling Windows' Animation effects live, and
#5 on a real screen.

## Pre-existing, noted during the review
On leaving the window, WPF raises DragLeave at (0,0) relative to the last target, so `ChatListPane.OnDragLeave` sees an
"inside" point. The row or background highlight then stays until the mouse button is released (the thread and window
overlays use timers and are fine).
