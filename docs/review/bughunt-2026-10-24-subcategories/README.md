# Bug hunt, the built-in subcategories (2026-10-24)

**Status: all 5 findings fixed on 2026-10-24** by the integrator, on `feat/builtin-subcategories` (the fix commit follows the review
commits). The integrator switched each fix off in turn and saw its finding's test fail again:
- **1–3, opening touched the vault:** `AddLaterBuiltInsAsync` does nothing while the vault folder can't be found (no rows, no mark; the next
  open tries again), and a folder-creation error (`LibraryException`, `IOException`, `UnauthorizedAccessException`) leaves the library
  opened without them, for the next open to retry. Restoring no longer runs it at the open (that database still names the backup's old
  vault); `RestoreCoreAsync` runs it right after `CompleteRestoreAsync` has pointed the library at the restored vault. Without the vault
  check F3 fails; without the restore-time skip F1 (old vault) fails; without the after-restore call F1 (drive gone) fails; F2 fails only
  with both the vault check and the catch removed.
- **4, hand-made folder:** a third folder mode, `ExistingFolders.Adopt`, used only by the one-time step: a folder with the default name is
  adopted whatever it holds, so the rescan files its files into the built-in category, as the same-name takeover does for a category.
- **5, fallback glyph:** `Glyphs.ForIconKey` maps the six keys to their parents' glyphs, as Code's subkeys are.

**Integrator's change to a repro test:** F2 asserted that the six exist right after opening with the vault drive missing. They can't get
folders then, and the rescan removes categories without folders once the drive is back, while the "added" mark would keep them away for
good. F2 now asserts that nothing is added while the vault is out of reach, and that the next open after the drive is back adds all six
with their folders (the reviewer's own suggested fix, "skip … and leave the mark unwritten so the next open retries").

**Minor notes, settled:** SPEC no longer says deleting one of the six is "final" (like every built-in, it is re-created when a file
needs it). Toasts naming the subcategory alone ("added to PDF") stay as they are, as for Code's subcategories.

- **Reviewed:** branch `feat/builtin-subcategories` (`5f760d5`), one commit adding six built-in subcategories at the user's request:
  Documents › PDF and Word, Executable › MSI and Exe, E-books › EPUB and PDF (SPEC.md Appendix A, "Subcategories added on 2026-10-24").
  - `ExtensionMap`: the six definitions and the new `Keeps` parameter (E-books › PDF keeps PDFs); `TypeSorting.Destination`'s keeps rule
  - `LibraryService.AddLaterBuiltInsAsync`, called from `OpenAsync`: an existing library gets the six once, under parents that still exist, recorded by a second `settings` row (`builtins-added-2026-10-24`)
  - `CategoryIconGenerator`: text-label avatars for the six
  - `LaterBuiltInsTests` and the adjusted tests
- **Reviewer:** Claude Fable 5.1 (review branch `review/builtin-subcategories-bughunt`, `c5c4237`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the AI UI bug hunt](../bughunt-2026-10-24-aiui/README.md).
- **Regression tests:** `tests/Cabinette.Core.Tests/SubcategoriesReviewTests.cs` (F1–F4) and `tests/Cabinette.App.Tests/SubcategoriesReviewTests.cs` (F5).
  Every test fails on `5f760d5` for the reason its finding states (checked by running them there) and is written to pass once the finding is fixed.
- **Verdict on the piece:** the categorization side is right and well tested: the extension split, the keeps rule, the type filter, seeding, the deleted-parent and same-name-takeover cases. The one design mistake is that `AddLaterBuiltInsAsync` creates vault folders from inside `OpenAsync`, so opening a library now touches the vault, which it never did before. Findings 1–3 are that mistake seen from three places; the fix is one change. Don't merge before it.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 8 (4×2) | **Restoring a backup from before 2026-10-24 breaks.** `RestoreCoreAsync` opens the extracted database *before* `CompleteRestoreAsync` re-points it, so `AddLaterBuiltInsAsync` runs with the backup's **old** vault path. On another PC that path doesn't exist: `EnsureDirectory("D:\Cabinette\Documents\PDF")` throws, the open fails, the restore rolls back, and the user sees *"PDF" could not be found. It may have been moved or deleted.* On the same PC, where the old vault still exists, the restore creates the six folders inside it (and picks the restored library's folder names, "PDF (1)" say, from the old vault's contents). Tests `F1_RestoringAnOlderBackup_FailsWhenItsOldVaultDriveIsGone`, `F1_RestoringAnOlderBackup_CreatesFoldersInTheBackupsOldVault`. | Never run the later built-ins against the backup's path: skip them in the restore's `OpenAsync` (it already has the `sweepStaging: false` flag for this situation; add one more, or one `restoring` flag) and run them at the end of `CompleteRestoreAsync`, after `StoreSettings` and `CreateVaultFolders` on the new vault. |
| 2 | 6 (3×2) | **An older library whose vault is out of reach no longer opens.** Vault on a USB disk that isn't plugged in, or a NAS the laptop can't see: the first start after the update throws the same *"PDF" could not be found* from `OpenAsync`, the app shows "Startup failed" and quits, and so it goes at every start until the drive is back, since the "added" mark is written in the transaction that failed. Before the update such a library opened, the monitor said "Vault not found", and pointer files stayed usable. A read-only vault fails the same way (`UnauthorizedAccessException` → "Access to PDF was denied"). Test `F2_AnOlderLibrary_DoesNotOpen_WhileItsVaultDriveIsMissing`. | In `AddLaterBuiltInsAsync`, do nothing (and leave the mark unwritten) when `VaultPath` is set but `Directory.Exists(VaultPath)` is false, and catch the `LibraryException` of a folder that can't be created the same way; the next open, or the monitor's retry when the vault returns, tries again. Opening must not depend on the vault. |
| 3 | 5 (5×1) | **A missing vault folder is brought back empty, and the startup rescan then empties the library.** Vault folder renamed in Explorer while Cabinette was closed, or on a removable drive whose letter now belongs to another disk: the open creates `<vault>\Documents\PDF`, `E-books\EPUB` and the rest, which re-creates the vault root. The reconciler's "vault looks empty" guard asks whether *any* top-level category folder exists; "Documents" now does, so the rescan runs for real: every file is "gone" and is deleted from the library with its tags and stars, and every category whose folder is missing is deleted, user categories included. In the test's library the rescan removed all 3 files and 23 categories; Documents, Executable, E-books and Deleted were all that was left. (The files themselves are safe in the renamed folder; what's lost is the library.) Before the update the open left the disk alone and the rescan reported "The vault folder can't be found". Test `F3_AnOlderLibrary_RecreatesAVaultThatIsNotThere_AndTheRescanThenEmptiesTheLibrary`. | The fix of #2 covers it (no folder creation while the vault root is missing). Worth adding on its own: `EnsureDirectory` in `InsertCategory` should never create the vault root, only folders inside an existing one. |
| 4 | 2 (2×1) | **A hand-made `Documents\PDF` folder gets a twin.** The folder was made in Explorer while Cabinette was closed and holds files. The update runs before the monitor's rescan, so the folder (foreign files, so not adopted) makes the built-in take the folder "PDF (1)"; the rescan then adopts the hand-made folder as a user category, which can only be named "PDF (1)". Result: built-in "PDF" in folder `PDF (1)`, user "PDF (1)" in folder `PDF`. Had the monitor seen the folder once before the update, the same-name takeover the spec promises would have applied. Test `F4_AHandMadePdfFolderUnderDocuments_TakesTheBuiltInRole_InsteadOfATwin`. | For the one-time migration, adopt a sibling folder with the default name even when it holds foreign files (an `ExistingFolders.Adopt` mode for `UniqueFolderName`, used only by `AddLaterBuiltInsAsync`): the rescan then takes its files in as that category's own, which is what the folder's maker meant. |
| 5 | 2 (1×2) | **The fallback avatar shows a folder for the six.** `Glyphs.ForIconKey` maps Code's subcategories to Code's glyph but has no entry for `documents.pdf`, `documents.word`, `executable.msi`, `executable.exe`, `ebooks.epub`, `ebooks.pdf`, so they fall through to `Folder24`, the user-category glyph. Shown until `EnsureDefaultIconsAsync` has saved the generated PNG (the first start after the update, for every older library), whenever the generator is unavailable, and in `--sample-icons` dev runs for every avatar. Test `F5_TheFallbackAvatar_ShowsAFolder_ForTheNewSubcategories`. | Add the six keys to `Glyphs.ForIconKey` with their parents' glyphs, as the Code keys are. |

**Notes for the fixer**
- `LaterBuiltInsTests.AnExistingLibrary_GetsThemOnce_AndItsFilesStayWhereTheyAre` asserts that `Documents\PDF` exists on disk after the open. It stays true with the fixes above (the vault is reachable there); a fix that drops folder creation altogether would instead have the startup rescan delete the six as categories without folders (`RemoveMissingCategory`), so the folders must still be created whenever the vault is there.
- F1's second test deletes the six folders from the source vault after making the backup, because a library made by this build already has them; that is what a pre-update vault looks like.
- F1 and F2 need a drive letter no drive uses (they take the first free one from Z down) and return without asserting if every letter is taken.

## Leads the reviewer dropped (kept for the record)
- **The second `settings` row:** every reader and writer keys on `key` (`LoadSettings`, `StoreSettings`, the mark itself, the two tests the branch adjusted); backup and restore copy the table whole; the Settings dialog, the AI side and the activity log don't read the table.
- **Type filter and type keys:** `GetTypeKey` cuts at the dot, so PDFs and Word files stay "documents" (the branch's test covers it); `AiCommandConversions.TypeKeys`, `SearchFilterViewModel.TypeKeys` and the MCP tool text are top-level lists and unchanged; the thread filter uses the same keys. The AI reports the six as `kind: builtin`.
- **A PDF in E-books › PDF is type "documents"**, so the E-books type filter doesn't show it. The filter has always gone by extension (an mp3 in E-books is Audio), and Appendix A says "none automatically" for that row; wording, not a bug.
- **New libraries and first run:** `Database.Seed` writes parents before children; with no vault chosen yet `AddLaterBuiltInsAsync` creates no folder and only writes the mark, and `InitializeVaultAsync` makes every folder later.
- **Same-name takeover:** a user "PDF" under E-books that held PDFs keeps its id, files, pin and icon, becomes a type category and keeps PDFs through `Keeps`; under Documents it keeps PDFs and sorts other types away, which is what the spec promises. A top-level user "PDF" is not a sibling and is left alone.
- **Deleted parents:** stay deleted (branch test); a parent re-created later by `GetOrCreateBuiltIn` gets its child when a file needs it, by section 5's rule.
- **`Keeps` is read only by `TypeSorting.Destination`.** `GetCategoryKey("pdf")` is `documents.pdf`, so the monitor's root filing, Clean My Downloads, Explorer adds, the AI's auto-categorized adds and "also add to type categories" all go to Documents › PDF; a PDF put by hand into `E-books\PDF` stays there, as files in category folders aren't sorted. Move to category / Add to category never sort.
- **Icons:** `CategoryIconGenerator` has all six (its test iterates every `IconKey`); labels are scaled to `LabelMaxWidthRatio`, so "EPUB" fits the circle.
- **Sample library and screenshots:** built from `BuiltInCategories` with parents resolved; its PDFs stay in Documents, as "files stay where they are" says.
- **Not exercised:** Move vault and Migrate after the update (the six are ordinary categories with folders), a read-only vault at open (same code path as #2, needs an ACL to reproduce), the screenshot states.

## Left as they are (minor)
- Appendix A says deleting one of the six "is final", but section 5's rule still applies: drop a .docx on Documents after deleting Word and Word comes back. The branch's test only covers a reopen.
- Toasts and summaries name the subcategory alone ("3 files added to PDF"), which could be either PDF category; E-books › PDF never receives automatic adds, and Code's subcategories read the same way.
- At the one-time migration `UniqueFolderName` walks a hand-made `Documents\PDF` folder's whole tree (`HoldsForeignFiles`) before deciding; a one-off cost.
