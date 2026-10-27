# Bug hunt, the thread banner (2026-10-24)

**Status: all 3 findings fixed on 2026-10-24** by the integrator, on `integration/additions` (the banner merged with the Gallery,
date taken and main). Each fix was switched off in turn and its test failed again:
- **1, the focus ring:** the band's toggle styles use their own `Cab.Thread.BandFocusVisual`: a 2 px `Cab.BannerText` ring inside a
  1 px `Cab.OnAccent` outline (dark in both themes), so one tone always reads, the accent-colored Starred band included.
- **2, the dark search box:** the dark theme's `Cab.BannerFallbackBackground` is `#3B4F5C` instead of `#2A3942` (the search box's
  own color): 1.4:1 against the box, 8.5:1 for white text. SPEC section 11's table says so.
- **3, the user's round logo:** `CategoryIconService.ScaleToSquarePng` marks the pictures the user chooses with a PNG `tEXt` chunk
  (`UserPictureMark`, "Cabinette" = "user picture"), and `ThreadBanner.ReadAvatarColor` never takes a marked picture for an
  avatar. Pictures chosen by earlier builds carry no mark and still go through the look-based check (the residual case).
- **Merge note, done:** the six subcategories' watermarks were added on the integration branch (`d0c537c`).

- **Reviewed:** branch `feat/category-banner` (`822550e`, one commit on `1c8e41c`):
  - Core: `CategorySummary.TotalSizeBytes` filled by `BuildSummaries` (distinct items of the subtree, NULL sizes skipped)
  - App: `ThreadBanner` (band color, avatar recognition, contrast math, watermark, "count · size"), `ThreadViewModel.Banner`,
    `ThreadBannerPanel` / `BannerToolsPanel` (the band's layout), the header region of `ThreadView.xaml` (the band, the re-keyed
    toggle styles, `ReadableBandBrushConverter`), the `-minsize` and `thread-search:` screenshot states
  - against SPEC.md section 4 ("Header: a banner", as on `main` after `4799d76`) and section 11
- **Reviewer:** Claude Fable 5.1 (review branch `review/banner-bughunt`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the AI UI bug hunt](../bughunt-2026-10-24-aiui/README.md).
- **Repro tests:** `tests/Cabinette.App.Tests/BannerReviewTests.cs` (`F1`–`F3`). Each fails on the branch for the reason its
  name gives. The color tests read the real theme dictionaries and the view XAML as data (keys, brushes, the focus visual), so
  they follow a fix that changes a key, a color or the focus visual, and they accept a band-text-colored brush or a multi-tone ring.
- **Evidence:** off-screen screenshot runs of the worktree's build (`--sample-data --screenshot`, states `thread:Images`,
  `thread:Travel 2026`, `thread:Deleted`, `starred`, `thread-search:Documents-minsize` with `--text-size xlarge`,
  `starred-minsize`, `thread-search:Travel 2026 --theme dark`, `live-switch`), both themes.
- **Verdict on the piece:** good. The banner does what the spec says in every category kind I could produce (built-in, renamed,
  user with and without a picture, subcategory, Starred, Deleted, search results, an icon not yet generated), the contrast math
  is the WCAG formula with rounding in the safe direction, the count and size follow every size-changing write, the layout holds
  at the minimum window with extra-large text and the search box open, and the live theme switch renders the band
  pixel-identical to a clean start. The three findings are all about things drawn *on* the band that still assume the old
  header's colors: the keyboard focus ring, the search box, and whose picture the band thinks it is showing.

## Findings, by score

| # | Score | Finding | Suggested fix |
|---|---|---|---|
| 1 | 6 (3×2) | **The keyboard focus ring is invisible or faint on every band.** The band's toggle styles keep `Cab.Thread.FocusVisual`, a 2 px ring in `Cab.Accent`. The Starred band *is* the accent (1.0:1 in both themes), and every avatar band is darkened to ~4.5:1 under white, where the gold accent reaches only 1.7–1.9:1 and the green one 1.5–1.7:1 (WCAG 1.4.11 asks 3:1). Only the dark theme's user-picture band passes (3.9:1). A keyboard user tabbing through search, filter, the view switch and select cannot see which has focus; the builder noted "faint" on Starred, it is the same color. Test `F1_The_focus_ring_of_the_band_tools_vanishes_on_the_Starred_band_and_fades_on_avatar_bands` (prints the full table). | Give the band its own focus visual in `Banner`'s resources: a two-tone ring (2 px `Cab.BannerText` inside a 1 px dark outline, as Windows 11 draws focus), which reads on any band; or a ring in the band's text color drawn by the template itself (the adorner layer does not inherit the band's `TextElement.Foreground`, so a `{Binding (TextElement.Foreground)}` in the focus visual would not work). |
| 2 | 4 (2×2) | **In the dark theme the open search box has no visible edge on a user-picture band.** `SearchBoxHost` is a borderless box in `Cab.InputBackground` (#2A3942); `Cab.BannerFallbackBackground` is also #2A3942, so on the band of a category with the user's own picture only the magnifier, the placeholder and the caret float on the band (pixel-checked on `thread-search:Travel 2026 --theme dark`: band and box both (42, 57, 66)). On the old header (#202C33) the same box had 1.2:1. Test `F2_The_search_box_has_no_visible_edge_on_the_dark_themes_user_picture_band`. | Either nudge the dark banner color away from the input color (SPEC 11 allows "nudged slightly for readability"; e.g. #34444F keeps the look), or give `SearchBoxHost` a 1 px border in the band's text color at ~35 % opacity so it reads on every band. |
| 3 | 2 (2×1) | **A round logo the user chose is taken for a generated avatar.** SPEC 4: a category with your own picture uses the theme's banner color, and a category you made shows its own icon as the watermark. `ReadAvatarColor` has no provenance and recognizes a disc with transparent corners and one rim color at ≥3:1 under white: a club badge, an app icon or a brand mark as PNG, put through the picker's `ScaleToSquarePng`, passes. The band takes the logo's color and the watermark is the generic folder glyph, not the logo. The builder's own test asserts this ("a dark round logo of one color does count"), so it is a knowing deviation, but one the spec does not make. Test `F3_A_round_logo_the_user_chose_is_taken_for_a_generated_avatar`. | Record provenance instead of guessing: the generator writes a PNG `tEXt` chunk (`Cabinette-icon: avatar`) and `ScaleToSquarePng` (the picker, the only way a user picture gets in) writes `Cabinette-icon: picture`; `ReadAvatarColor` honors a chunk when present and keeps the heuristic only for unmarked PNGs (libraries made before the banner). Flip the last assertion of `The_users_own_picture_is_never_taken_for_an_avatar` with the fix. |

## Leads the reviewer dropped (kept for the record)
- **Live theme switch with a band shown** (the builder's doubt): the `live-switch` screenshot state (Dark → Light → Dark on
  Documents) and a clean `--theme dark` start differ in no pixel of the band region (y 38–98); the only differences are in the
  chat rows under it. `DynamicResource` in the style setters plus the `MultiBinding` on `Self` re-evaluate as expected.
- **The 50k-item chat-list query** (the builder's doubt, "40 → 100 ms"): measured with `PerformanceTests` on this machine
  (debug, loaded), best of three, twice each: `1c8e41c` 39.5 / 42.4 ms, the branch 43.7 / 51.7 ms. The extra `DISTINCT` subquery
  costs 5–10 ms here, not 2.5×; the 100 ms (and my own first 118 ms) came from a loaded machine. The chat list refreshes at most
  once per 150 ms debounce window and only the thread banner reads `TotalSizeBytes`, so nothing user-visible changes. If it ever
  matters, compute the size only in `GetCategorySummaryAsync` (one row) and leave `GetCategoriesAsync` (the chat list) without it.
- **Count and size freshness:** every write that changes `size_bytes` (`CheckPointerFile`, reconcile's modified and moved files,
  `RelocateAsync`) records the item with its categories *and their ancestors* (`ChangeSet.Item` → `AddWithAncestors`), which
  `ClassifyChange` turns into a header reload; adds, moves, deletes and Empty Deleted go through `Category`/`Categories` the same way.
  Migrate and Move vault keep sizes. Missing pointers keep their last known size and count in the total, like the old count did.
- **Avatar recognition, false negatives:** every built-in avatar and the folder avatar of several names are recognized (the
  builder's test); the rim points sit at 8 % inset, well outside the glyph's middle 56 % and a label's 60 %, at any size from 16 px.
  Generated avatars from `--sample-icons` have the same structure. Older builds' palettes I could not check.
- **Contrast math:** sRGB linearization and the ratio are the WCAG formula (checked against #767676 = 4.54:1); darkening floors
  and lightening ceils, both toward more contrast, and the bisection stops at the first step that reaches 4.5:1.
- **Layout at the extremes:** at the minimum window (900 px) with extra-large text the tools wrap under the text, and with the search
  box open it gets a row of its own above the buttons (`thread-search:Documents-minsize`, `starred-minsize`); nothing overlaps or
  clips. With the sidebar dragged to its 560 px maximum *and* extra-large text the thread pane is 261 DIP while the tool row needs
  272, so the Select toggle loses 11 px: the old header clipped the same way (its columns needed more), and the cure is a
  `MinWidth` on the thread column, outside this branch.
- **Foreground inheritance from the band:** the band sets `TextElement.Foreground` to white (or `Cab.OnAccent` on Starred) and
  it reaches everything inside, including the filter popup. Everything that sits on its own surface sets its own color: the search
  `TextBox` (`Cab.Thread.BareTextBox` sets Foreground, caret and placeholder), its clear button (`Cab.Thread.IconButton`),
  tooltips (the implicit `ToolTip` style), the filter panel's labels, chips, switch and link, the subcategory chips (outside the
  band), and the custom date range's `DatePicker`s (WPF-UI's `DefaultDatePickerStyle` and `DatePickerTextBox` style set their own
  Foreground; checked by loading the compiled dictionaries from `Wpf.Ui`'s resources).
- **Keyboard and screen readers:** the re-keyed toggle styles keep `FocusVisualStyle` and the buttons keep their
  `AutomationProperties.Name`; tab order is unchanged (search box, search, filter, Chat/Grid/Details, select). Only the ring's
  color is the problem (finding 1).
- **Subcategory icon keys from `main`:** `main` (`6fb542f`) added `documents.pdf`, `documents.word`, `executable.msi`,
  `executable.exe`, `ebooks.epub` and `ebooks.pdf`; the branch's `WatermarkFor` predates them and will give them the folder glyph,
  and `Every_built_in_category_has_a_watermark_of_its_own` will fail after the merge. Not a bug of the branch; the merger adds six
  cases to `WatermarkFor` (a PDF, Word, MSI, EXE and EPUB glyph each, below U+FFFF like the others).
- **The Core query:** distinct `(row, item)` pairs over the subtree are the same set `COUNT(DISTINCT ic.item_id)` counts, so a file
  in several categories of one subtree is summed once per row and once per child row; the builder's tests cover it.
- **Starred:** the count and size now come from `GetStarredItemsAsync` (every starred file, Deleted ones included, as the view
  lists them) instead of the summary; correct, at the cost of loading the starred list twice per refresh (the items reload loads
  it too). Fine at any realistic number of stars.
- **Pluralization, localization, huge sizes:** "1 file" / "N files" / "n of m files" come from the existing strings; the size uses
  `ThreadFormat.Size` like the bubbles; the new `Thread_BannerCountAndSize` ("{0} · {1}") exists in the only resx (English).

## Left as they are (minor)
- "2 of 4 files · 3.3 MB" during a search gives the whole category's size next to the match count; the spec does not say what the
  size means while searching, and the builder chose the category. A reader may take 3.3 MB as the size of the two matches.
- The title's tooltip repeats the title when there is no parent path (top-level categories, Starred).
- `-minsize` is stripped before `-still`, so `thread:video-minsize-still` is not understood (`thread:video-still-minsize` is).
- The screenshot runner's `thread:<name>` finds top-level rows only (`thread:Invoices` renders the empty state).
