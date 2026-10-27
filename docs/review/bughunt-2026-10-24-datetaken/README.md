# Bug hunt, date taken: the background reader and `item_dates` (2026-10-24)

**Status: both findings fixed on 2026-10-24** by the integrator, on `integration/additions` (date taken merged with the Gallery,
banner and main). Each fix was switched off in turn and its test failed again:
- **1, the camera's time drifted with this PC's zone:** `ShellDateTakenReader.AsCameraTime` returns the instant with the offset this
  PC had when reading (undoing Windows' conversion right away gives the camera's wall clock); videos follow the same rule. Core stores
  `taken_at` with its offset (`DbValues.ToTextWithOffset`, read back by `ParseTimeWithOffset`; the old UTC text still parses, and
  earlier builds read the new text as the instant). The Gallery sections and labels a date taken by its wall-clock time and still
  sorts by the instant; file and added dates stay instants shown in this PC's zone. New test
  `GalleryTests.A_date_taken_is_sectioned_by_the_day_the_camera_showed` (zone-independent) covers the sectioning. The builder's
  `Jpeg_ReturnsTheInstantWhoseLocalTimeIsTheCameraTime` and the Gallery tooltip test now expect the offset and the wall clock.
  Rows stored as UTC are not re-read: no build that stored them was released.
- **2, photos before 1904 lost their date:** the 1904 floor applies to `System.Media.DateEncoded` only; photos keep only the
  zero-FILETIME check (before 1601-01-02).

**Status: open**, handed to the integrator with failing repro tests.

- **Reviewed:** branch `feat/date-taken` (`39afa12`, one commit on top of `1c8e41c`):
  - `Cabinette.Windows/ShellDateTakenReader.cs`, `Interop/PropertyHandlerInterop.cs`: `System.Photo.DateTaken`, else `System.Media.DateEncoded`, through the file type's property handler (shell as fallback)
  - `Cabinette.Core`: `Data/Schema.sql` (`item_dates`), `Database.cs` (orphan cleanup), `Categorization/DateTakenTypes.cs`, `Data/ItemReader.cs` (bulk lookup), `Services/LibraryService.DatesTaken.cs` (the worker: queued checks, the one-time pass, batches of 32, the throttle, events at most once a second), hooks in `LibraryService.cs` and `Vault/BackupArchive.cs`
  - `Cabinette.App`: the reader passed at open and restore, `ThreadItemViewModel.SameContent`
- **Reviewer:** Claude Fable 5.1 (review branch `review/date-taken-bughunt`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Repro tests:** `tests/Cabinette.Windows.Tests/DateTakenReviewTests.cs` and `tests/Cabinette.Core.Tests/DateTakenReviewTests.cs`. The `F` tests fail on the branch for the reason their finding names; the `Lead_` tests record scenarios that were checked and hold.
- **Verdict on the piece:** solid where it is hard. The worker never does I/O under the write lock, stops at once on dispose (also while it waits for a lock a vault move holds), stores a result only while the file is still at the path that was read, survives a vault move or a delete-and-reuse of the id in the middle of a read, opens with the vault on a missing drive, and raises few events. Both findings are about *what* is stored, not how: the value of a photo's date is tied to this PC's time zone at the time of reading, and a QuickTime-only cut-off is applied to photos.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 6 (3×2) | A photo's EXIF date is the camera's wall-clock time without a zone. Windows gives it as a UTC FILETIME computed with *this PC's zone at the time of reading*; the reader passes that instant on and `item_dates.taken_at` stores it as UTC. Shown as local time, the wall clock drifts as soon as the PC's zone is not the one at reading time: a laptop that travels or has "set time zone automatically" on, a zone corrected after first run (Windows set up in UTC, fixed later: every date read before is 3 h off for good), a portable backup restored on a PC in another zone. 15:30 becomes 12:30; a photo at 01:30 lands on the previous day's section. Explorer re-reads the file each time, so its "Date taken" never moves. Videos (MP4 stores UTC) are not affected. Tests: Windows `F1_ExifWallClock_IsKeptAsTheCameraTime_NotOnlyAsAnInstantOfThisPcsZone` (3 cases), Core `F1_ADateTaken_KeepsItsWallClock_ThroughTheDatabase`. | Keep the wall clock in the value and in the database. Reader: `utc.ToLocalTime()` on this PC undoes the conversion Windows made (same zone rules), so return `new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock))`; do the same for videos, so one rule holds ("the clock on the wall here when it was taken"). Core: store `taken_at` with its offset (`yyyy-MM-ddTHH:mm:ss.fffffffzzz`, a `DbValues.ToTextWithOffset`) and parse it in `ItemReader` with `RoundtripKind`; the existing `ParseTime` still reads such text (as UTC), so an older build keeps opening the library. The Gallery shows and sections by `TakenAt.Value.DateTime`, sorts by the instant. Rows already stored (text ending in `Z`) are read once more: let the pass take them too (`OR d.taken_at LIKE '%Z'`), it is a one-time cost. No contract changes: `IDateTakenReader` and `LibraryItem.TakenAt` already carry a `DateTimeOffset`. |
| 2 | 2 (2×1) | Any date before 1904-01-02 is treated as "not set". The cut-off belongs to QuickTime/MP4 creation times (seconds since 1904-01-01), but it is applied to photos too: a scanned photo whose archivist set the EXIF date to the day it was taken (1890s, 1900s) shows no date taken in Cabinette, while Explorer's "Date taken" column and `System.Photo.DateTaken` both give 15/06/1900 (checked on this PC). Test: `F2_PhotoDatedBefore1904_KeepsItsDate`. | Apply `EarliestUtc` to `System.Media.DateEncoded` only; for both keys keep dropping a zero FILETIME (1601). |

## Leads the reviewer dropped (kept for the record)
Each has a `Lead_` test unless noted.
- **Rename, move, delete while a file is being read:** `SHCreateStreamOnFileEx(STGM_READ | STGM_SHARE_DENY_NONE)` grants `FILE_SHARE_DELETE`; `File.Move` and `File.Delete` succeed while the reader's stream is open (also checked by hand with P/Invoke).
- **Handle leak:** 3,000 reads (photo, video, no date) move the process handle count by less than 100.
- **Vault moved while a file is read:** the result is dropped (path changed), the item is queued again and read in the new vault, one row.
- **Item deleted for good while read, its id reused** by a file of the same name in the same category: the stale result is stored, then the add's own check sees another size and reads the new file; the end state is right. Residual: a same-size, same-mtime replacement inside the read window would keep the old date, which is contrived.
- **Dispose while the worker waits for the write lock** that a vault move holds: the worker ends at once (`WhenDatesTakenIdleAsync` completes before the move releases the lock).
- **Vault on a missing drive letter:** opens, reads nothing, raises nothing, no rows; once the path is back the photo is read.
- **A reader that throws:** the file keeps no row and is read on the next start.
- **Events during the pass:** 200 photos at 2 ms each → 1–4 `Items` events, no `CategoryIds`. In the App, `MainViewModel.ApplyPendingChangesAsync` treats every non-Viewed change as a chat-list refresh (a merge of category summaries) and a search re-run; a first pass over 50,000 photos (about 1–2 minutes at ~1 ms a file plus equal pauses) means some 60–120 of those. The builder knows; a `LibraryChangeKind.Dates` flag (additive) that the shell ignores for the chat list would remove it. Not scored: cost, not a failure.
- **Long paths** (> 300 characters) are read, through the handler and through the shell, on this PC (`LongPathsEnabled` = 1 and the apphost manifest). Not verifiable here for a PC with the registry default (0): `SHCreateStreamOnFileEx` may then fail and the file would be stored as "no date".
- **`.mov`:** read (MP4 content). **HEIC:** no sample at hand; the handler is registered for `.heic`/`.heif` on this PC ({a38b883c…}); whether it reads EXIF without the HEVC codec is unverified (the builder said so too).
- **EXIF `0000:00:00 00:00:00`** reads as no date.
- **A file another process holds with `FileShare.None`** reads as "no date" and is stored as such until the file changes; a writer that shares read is read fine. The builder's known gap (the reader can't say "couldn't look"). If it bites, let `ReadDateTaken` throw a dedicated exception for "could not open" so the Core leaves no row (a small, additive contract change).
- **Same size, same mtime edits** (ExifTool `-P`) aren't seen by the date worker; the vault monitor doesn't see them either (`SameSizeAndTime`), so this is the existing rule, not new.
- **FAT32 pointer drives:** `LastWriteTimeUtc` shifts by an hour at a DST change, so every pointer photo on such a drive is read once more per switch (cost only; dates unchanged, no events).
- **Pass cost on slow pointer roots at every start:** 50,000 pointer files on a NAS at ~20 ms a stat is about 17 minutes of metadata reads at 50% duty, each start. By design ("again when a pointer file changes").
- **Older build, newer database:** the `ON DELETE CASCADE` is declared on `item_dates`, so an older build deleting items still removes the rows; the new build's orphan cleanup at open covers hand-edited databases. Backups made by either side open on the other (no version bump). By reading, not by test.
- **Thread view:** `ThreadItemViewModel.Update` raises `PropertyChanged("")` per landed item, resets thumbnails only on a path or mtime change.
- **Queue cost of ordinary writes:** every item a star, tag or view names costs the worker one query and one stat; negligible.
- **Open path:** `DELETE FROM item_dates WHERE item_id NOT IN (SELECT id FROM items)` at open is milliseconds on 50,000 items.

## Left as they are (minor)
- The App's restore passes the reader to the restored library, which it disposes at once before restarting; the pass starts and is cancelled, harmless.
- `passAfter = null` (the pass stops for the session) also when *storing* a pass batch fails; the comment says only loading.
- The pass pauses only after a batch that is still full after the path filter; a batch with a skipped candidate gets no pause.
- A OneDrive placeholder downloaded later is not read again (size and time don't change); builder-known.
