# Bug hunt, the Gallery view for Images (2026-10-24)

**Status: both findings settled on 2026-10-24** by the integrator, on `integration/additions` (the Gallery merged with the banner, date
taken and main):
- **1, a sort click lost the chat's place:** `IThreadViewHost.ScrollGalleryToStart()` (additive, a no-op by default so test hosts don't
  change) scrolls only the Gallery list to its top; `ApplyGallerySort` calls it instead of `ScrollToEnd`. With the old call back, F1
  fails again.
- **2, SPEC described a white frame:** already fixed on main by `2b9efee` (the user asked for green/blue frames after the first
  screenshots, after this branch was cut); F2 passes on the integration branch without a change.

- **Reviewed:** branch `feat/gallery` (`c90b189`, one commit on top of the foundation `1c8e41c`):
  - `ViewModels/Thread/Gallery.cs` (`GalleryBuilder`: Gallery order, day sections, rows; `GalleryMetrics`), `ThreadViewModel.Gallery.cs` (where it is offered, the sort header, cell sizes, the keyboard cursor) and the hooks in `ThreadViewModel.cs`, `.Selection.cs`, `.Actions.cs`
  - `Views/Thread/GalleryPrint.cs` (frame and fake shadow in `OnRender`), the Gallery templates in `ThreadTemplates.xaml`, the 4th toggle, sort bar and virtualized list in `ThreadView.xaml`, `ThreadView.Gallery.cs` (metrics, Ctrl+wheel, Date menu, keys)
  - `ThreadViewState` (per-category views and sorts, the cell size in `state.json`), `AppState.GallerySize`, the theme keys `Cab.GalleryFrameCopy` / `Cab.GalleryFramePointer`, the strings, sample data and screenshot states
- **Reviewer:** Claude Fable 5.1 (review branch `review/gallery-bughunt`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the AI UI bug hunt](../bughunt-2026-10-24-aiui/README.md).
- **Repro tests:** `tests/Cabinette.App.Tests/GalleryReviewTests.cs` (`F1`, `F2`; they fail on the branch). The same file keeps ten
  `GalleryReviewChecks` that held, as regression coverage of the Gallery through the real `ThreadView` in a hidden window (the
  builder had left the keyboard and the view untested): 25,000 files virtualized, keys, focus scrolling, search, live changes,
  recycled frames, a live theme switch, size steps, clicks and date edge cases. They take the keyboard focus, so they sit in the
  `MotionTiming` collection: run in parallel with other hidden-window keyboard tests they steal each other's focus (seen once with
  `AiUiReviewTests.F2_tab_reaches_links…`, which passes alone).
- **Verdict on the piece:** solid. The order, sections and rows are pure and fast (25,000 files sort in 17–31 ms, re-cut in 1 ms), the
  list really virtualizes with recycling and no effects, the keyboard cursor and the selection follow the Gallery order everywhere,
  and the frames survive recycling, live mode changes and a theme switch. Two findings, both small.

## Findings, by score

| # | Score | Finding | Suggested fix |
|---|---|---|---|
| 1 | 4 (2×2) | A sort click in the Gallery loses the reader's place in the chat. `ApplyGallerySort` asks the host to `ScrollToEnd()`, which the view implements as "Gallery to its top" **and** "stick the chat to its bottom and scroll it there" (`_stickToBottom = true`, `_chatScroll.ScrollToEnd()`). Someone reading older files in Chat, switching to the Gallery, clicking Name (or choosing a date in the Date menu) and coming back lands at the very end. A trip to the Grid and back keeps the place. | Give the Gallery its own scroll request: an `IThreadViewHost.ScrollToStart()` (additive; the interface is not in the contract list) that only does `FindScrollViewer(GalleryList)?.ScrollToTop()`, and call it from `ApplyGallerySort`. Keep `ScrollToEnd()` for the loads and resets, where sticking the chat to its bottom is wanted. Test: `F1_a_sort_click_in_the_Gallery_loses_the_place_in_the_chat`. |
| 2 | 3 (1×3) | SPEC.md, the source of truth, still describes the white print frame: section 4 says "a thin light frame … in dark mode the frame is dimmer than white", and section 11's table has "Gallery print frame `#FFFFFF` / `#B9B4AA`". The branch frames each print in its mode's color (green `Cab.GalleryFrameCopy`, blue `Cab.GalleryFramePointer`) and removed `Cab.GalleryFrame`, without touching the SPEC. The next agent that reads it (the banner and date-taken branches are being built against the same foundation) builds or checks the wrong thing. | Update section 4's bullet (a thin frame in the file's mode color, green for copies and blue for pointers, as the chat bubbles; muted in dark mode) and replace the section 11 row with the four values: light `#A9CC8A` / `#98BCE3`, dark `#2F7A5F` / `#3A6A96`. Test: `F2_SPEC_still_describes_a_white_print_frame` (reads the values from the theme files, so it stays right if they are nudged). |

**Integration note:** `main` and the sibling branches (`feat/category-banner`, `feat/date-taken`, `feat/builtin-subcategories`) still
define `Cab.GalleryFrame` in `Themes/Light.xaml` and `Dark.xaml`; nothing consumes it anywhere, so merging only brings a textual
conflict on those two lines. The existing test `The_print_frame_is_green_for_copies…` asserts the old key is gone, so keeping both by
mistake shows up at once.

## Leads the reviewer dropped (kept for the record)
- **The view is now set after the path lookup in `LoadAsync`:** every caller awaits the load (`MainViewModel.ShowThreadAsync`, the
  thread harness, the screenshot runner, the tests), and the shell binds the view only after it, so nothing sees the Chat default or an
  overwritten choice. Quick thread switches create a view model per open and dispose stale ones after their load; a cancelled or
  failing path lookup (a deleted category) opens the thread in Chat without an error.
- **Stale "offered" state:** categories cannot be re-parented (no library API), renaming is covered by the built-in key, and a
  deleted Images is simply not offered on the next open.
- **Remembered views and sorts:** a remembered Gallery never opens outside Images; a Grid or Details choice in Images is kept; the sort
  is per category and its "All in" row shares the id. The cell size is stored through `AppStateStore` (`CommandLineOptions` and
  `AppStateStore` are both registered, so the installed app persists it; sample-data and screenshot runs keep it in memory); an
  older `state.json`, a corrupt one or `"GallerySize": "Huge"` fall back to Medium.
- **25,000 files:** 4,000+ rows, fewer than 60 realized before and after a trip to the end, `VirtualizationMode.Recycling`, no
  `Effect` on any cell, thumbnails asked at the cell size (doubled to 256 px by the loader, as the Grid does). Sorting on the UI
  thread costs 17 ms (date, size) to 31 ms (type) and re-cutting the rows 1 ms, so live changes cannot stutter. The row padding
  (at least 20 px at the narrowest pane) absorbs the scroll bar, so the rightmost cell is never clipped.
- **Keyboard through the real view:** arrows, Home and End move and select; the first press starts on the first cell on screen;
  Shift ranges and Ctrl+A follow the Gallery order; the cursor recovers when its file leaves the thread; Delete, Enter, Escape and
  F2 still reach the thread's handlers. Ctrl+wheel and Ctrl+plus/minus could not be driven (the real keyboard's modifiers are read),
  but their handlers are three lines over `StepGalleryCellSize`, which is tested.
- **Frames and shadows:** right after container recycling at the top, middle and end of 1,200 files, after a live pointer→copy
  change (existing test) and after swapping Light.xaml for Dark.xaml live; every brush is a `DynamicResource`.
- **Size steps:** the row that was first on screen stays first on screen (its cells re-cut into the new column count).
- **Search and filters in the Gallery:** rows follow the matches, the no-results state shows and the sort bar hides, clearing
  restores the sections.
- **Live adds, removes and renames:** rows are diffed by key (unique: a day's ticks, a row's first id), the name order is kept.
- **Dates:** the sections use the same local day and header rules as the chat (`ThreadFormat.DateHeader`: Today, Yesterday,
  weekday, long date); dates in the future, 1980, `DateTimeOffset.MinValue` and `MaxValue` sort and label without error; a missing
  date taken uses the file date, then the added date, and the tooltip says which.
- **Mouse:** a click on a cell selects it, a click in the space around it clears, right-click selects; the viewer, select-all and
  the bulk actions run in the Gallery order (existing tests).
- **Text sizes:** the pane is scaled by a `LayoutTransform`, so the cell metrics, columns and thumbnails stay consistent.
- **Screenshots** (`--sample-data --screenshot`, states gallery, -small, -large, -name, -select, -narrow): frames green and blue,
  sections and sorts as specified, the 4th toggle and the sort bar fit at the minimum width with medium text.

## Left as they are (minor)
- `TakenAt` is converted to local time for the sections, so the date-taken branch must store a camera's wall-clock time with the
  right offset, or evening photos land on the next day.
- Choosing "Date taken" or "Date added" from the Date menu switches the sort to Date even when sorted by Size or Type (the
  builder's choice; the spec allows it).
- Ctrl+Shift+= (Ctrl++ on a US keyboard) does not grow the cells; Ctrl+= and Ctrl+wheel do.
- A trackpad's fine Ctrl+wheel deltas can step two sizes in one gesture.
- Files that arrive at the top shift the Gallery under a reader who scrolled down (the Grid does the same; the chat counts them).
- Day headers go stale past midnight (as the chat's).
- At the minimum width with extra-large text the 4th toggle pushes "Select files" out of the header (known; the header is being
  rewritten on the banner branch).
