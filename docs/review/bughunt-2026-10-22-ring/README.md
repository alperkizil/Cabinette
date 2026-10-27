# Bug hunt, drop-card upload ring (2026-10-22)

**Status: both findings and the robustness lead fixed on 2026-10-22**, after the merge of the ring branch (`939388e`).

- **Reviewed:** branch `worktree-agent-ade617aaca3da15fd` (`098f33e`): the cosmetic upload ring the user asked for. After a drop,
  the card's icon turns into a ring that fills, a check pops, then the card fades (`DropRing`, `DropZone.PlayRing`/`UndoRing`/`StopRing`).
- **Reviewer:** Claude Fable 5.1. It used real WPF elements in a hidden, never-shown window with the dispatcher pumped, plus
  off-screen renders.
- **Verification:** the integrator (Claude Opus 5.5) re-checked the findings against the code. The new regression test for #2
  failed without the fix (the card lingered 1624 ms) and passes with it.
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.App.Tests/DropRingReviewTests.cs`.
- **Verdict on the state machine:** sound. The reviewer couldn't get a ring, check, hidden icon, faded dim or frame, or
  half-scaled card stuck through any interrupt path: enter/leave/enter inside the undo window, a drop during it, thread switch
  or Home mid-ring, animations off mid-ring. None of the 2026-10-21 motion fixes regressed.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 3 (1×3) | On the chat-list background the "Adding N files" progress card (after 250 ms) covers the drop pill, so the ring played almost entirely under it. | That pill keeps the short "received" pop; the ring stays on the thread, window and empty-state cards, where nothing covers it. |
| 2 | 2 (1×2) | The 1.75 s card lingered on top of a newer highlight of another zone (drop on the title bar, then drag into the thread; or drop on the list background, then over a row). | A zone that starts showing ends any other zone's "received" motion with the short exit (`DropZone._receiving`, per thread). |
| — | lead | `DropRing.OnRender` froze its pens, which deep-froze the shared theme brush; a brush that can't freeze (animated) would have crashed layout. Not reachable with today's themes. | Pens are no longer frozen. Two characterization tests became regression tests. |

## Leads the reviewer dropped (kept for the record)
- **Generation guards are load-bearing:** a replaced clock still raises Completed. All three Completed handlers check the
  generation; tested.
- **Leftover clocks:** after enter/leave/enter inside the 200 ms windows, the ring's clocks stay attached at opacity 0 until the
  next Collapse. Visually plain, and the next drop restarts from empty.
- **Drop during the undo fade:** a drop during UndoRing's fade snaps the half-visible ring to empty for one frame (1×1, by design).
- **Window overlay:** a second drop within its 120 ms show delay while the first ring plays gets no feedback (1×1).
- **Unchanged:** pill layout, ring rendering edge cases (0 and 1 progress, large arc), input and timers.

## Not covered
Real OLE drags on a real screen with the ring (the user checked the other motions on 2026-10-22), high DPI, and toggling
Windows' Animation effects live.
