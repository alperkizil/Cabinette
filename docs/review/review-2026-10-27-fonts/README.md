# Review of Bold text and Font (2026-10-27)

Branch `feat/bold-text` at `710d51f` (commits `dba8b0c` Bold text, `710d51f` Font). For this change, which only affects
how text looks, the user chose a Sonnet review for correctness and a Haiku review for mechanical sweeps instead of a Fable
bug hunt. The integrator checked every finding against the code, plus a startup check of its own.

Sonnet's repro tests are on branch `review/fonts-sonnet` (they assert the correct behavior and fail at `710d51f`). The fixed
ones are now regression tests in `tests/Cabinette.App.Tests/FontReviewRegressionTests.cs`. Each was proven by switching its
fix off and seeing its test fail.

## Findings

| # | Severity | Finding | Outcome |
|---|---|---|---|
| M2 | medium | Esc with the Font list open closed the whole Settings dialog: `DialogHost`'s Preview handler ran before the list could close. | Fixed: `DialogHost.IsInOpenDropDown`. Esc that comes from an open drop-down list or popup (its own presentation source) is left to it; the next Esc closes the dialog. This probably also fixes the Settings › AI filter popups' Esc workaround, which loses to the same handler (not checked separately). |
| M1 | low-medium | The font list's items were named by the record's `ToString()` ("FontOption { Font = … }") for screen readers, type-ahead and the closed box. | Fixed: `FontOption.ToString()` returns the name. |
| L3 | low | When "3 selected" shortened to "3", screen readers heard only "3". | Fixed: the count's automation name stays bound to the full text. |
| — | suspected | If Windows failed to list its fonts, Settings could never open (the exception was cached in a `Lazy`). | Fixed: `UiFont.ReadInstalledFamilies` logs the failure and offers every font; a missing one draws as the default. |
| L1 | low | A hand-edited `BoldText` of the wrong type (`"yes"`, `null`, `1`) makes the whole settings read fail, so every setting resets (vault path included). | Left as it is: every older setting already behaves this way, and only a hand-edited database can cause it. Recorded in HANDOFF section 5. |
| L2 | low | In a thread pane narrower than about 265 px (sidebar dragged wide, Large or Extra-large text), the selection bar's first action overlaps "All". | Left as it is: an edge case beyond the window's minimum with the default sidebar. Recorded in HANDOFF section 5. |

Also checked by the reviewers and found fine:
- settings back and forward compatibility, and odd Font values;
- the published app's embedded fonts and the `licenses\` folder;
- 70 screenshot states with Bold text and JetBrains Mono, every text in the right font and weight;
- no layout loop in the selection bar;
- quick setting changes saving in order;
- `TextLookAuditTests` always starting the app with `--sample-data --screenshot`;
- no hardcoded weights, fonts or colors left, every string localized, and all 33 font and license files matching their recorded hashes.

## Integrator's startup check
Throwaway libraries (seeded with DevSeed), each given a crafted settings row, rendered with `--data-dir <temp> --screenshot`:
no new properties, Inter + bold, Lexend, JetBrains Mono + bold, Sitka, an unknown font, `4`, `123`, `null`, `"inter"` and an
object. With the new build all 11 start, keep the vault path and leave the stored settings untouched. With the 0.1.1 build (the
downgrade case) all 11 start as well, ignoring the new properties.
