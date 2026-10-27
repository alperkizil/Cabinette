# Bug hunt, read_file content reading (2026-10-24)

**Status: all 6 findings fixed on 2026-10-24** by a fix agent (branch `fix/file-content`, `ceacc44`, merged into main as `a63765e`).
The same round implemented the user's Office decision. The integrator read the process-start code and ran the full suites.

- **Reviewed:** branch `worktree-agent-acf9ae78a9d897f4b` (`6dfc5c2`):
  - `Services/Ai/FileContent*.cs`: text sniffing and byte paging; IFilter text in character pages with a cache; WIC images scaled down; refusals
  - the hidden child mode `Cabinette.exe --extract-text`, decided in `Program.Main`
  - `Windows/FilterTextExtractor.cs`, `IFilterRegistry.cs`
- **Reviewer:** Claude Fable 5.1 (review branch `review/file-content-bughunt`, `2edf241`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.App.Tests/FileContentReviewTests.cs`, `tests/Cabinette.Windows.Tests/FilterReviewTests.cs`. The fix agent reverted each fix in turn and saw its tests fail.
- **Verdict on the piece:** the safety-critical part is solid:
  - No argument mix makes the child fall through into a normal app start.
  - The job object, the handle list, and the timeout and kill are correct.
  - The text pager survived random walks over every byte offset in six encodings.
  - A 9000×9000 PNG decodes with +7 MB.

## Background
During this piece's development (2026-10-24 00:32) a scratch script started the branch's `Cabinette.exe` with no arguments. That normal start wrote the user's real data folder and Run key; both were cleaned up with the user's OK. The child mode exists so that this can't happen through the product. Finding 1 was about the same failure mode.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 2 | 6 (2×3) | The test "a child that writes too much is stopped" was hollow: cmd.exe rejected the quoted `type` command, so the asserted error came from an early exit, not the overflow. | Both overflow tests really overflow (a control run proves 200,000 bytes), and they check the message and that a long-lived child is killed. |
| 1 | 5 (5×1) | `ChildProcessRunner` read only `ArgumentList`; a caller filling `ProcessStartInfo.Arguments` started the program with no arguments. For Cabinette.exe that is a normal app start, the 00:32 incident's failure mode. No current caller hit it. | The runner takes arguments one by one, never a `ProcessStartInfo`. `ChildProcess.Start` refuses to start any Cabinette.exe without a child flag. |
| 6 | 3 (3×1) | "Wrote too much" was checked only on cancellation, so a child that exited before the overflow was noticed returned empty text. | Every path fails with "wrote more text"; tested deterministically with a fake child. |
| 3 | 2 (1×2) | `.lnk` and `.class` were refused as "text extension, binary content". | Shortcuts and compiled code (`.lnk`, `.class`, `.pyc`, `.pyo`) are refused as what they are. |
| 4 | 2 (2×1) | An offset near `long.MaxValue` wrapped while aligning and threw an `IOException`. | The offset is clamped to the file first; the empty last page comes back. |
| 5 | 1 (1×1) | The filter's buffer size wrapped for `maxChars = int.MaxValue`. | Computed in long. |

## The Office decision (user, 2026-10-24)
Office's IFilter loads the .NET Framework, which crashes (0xC0000409) in a CET-compatible process. The user chose: only the extraction child runs without CET, at Low integrity.
- **How the child starts:** `CreateProcessAsUser` with a token derived from Cabinette's own. `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)` strips its privileges, then it's lowered to Low (`S-1-16-4096`). It also gets the creation attribute that turns user-mode shadow stacks off (Windows 10 19041 and later).
- **Unchanged:** the handle list (only its two output pipes), the kill-on-close job with a 2 GB cap, and the 30 s timeout.
- **Measured on running children:**
  - The only privilege is SeChangeNotifyPrivilege; integrity is Low; shadow stacks are off.
  - docx, xlsx, pptx, pdf, rtf, html and doc all extract.
  - The child reads the user's files, but writing to their folders is denied.
  - `Cabinette.exe` keeps its CETCOMPAT flag; a test checks this.
- **Proofs:** with CET left on, docx crashed again. With the token left at Medium, the integrity tests failed. With a plain token copy, the privilege test failed: the child held SeShutdown, SeUndock, SeIncreaseWorkingSet and SeTimeZone.

## Leads the reviewer dropped (kept for the record)
- **Fall-through into a normal start:** none (`=` forms, missing or relative paths, reordering, extra arguments, `-Embedding` all exit with code 44).
- **Handle inheritance, job, struct layouts, timeout and kill paths:** correct.
- **Huge PNGs:** WIC scales them while decoding.
- **By design:** EXIF orientation applies to JPEG only. An ASCII-only PDF without an IFilter is read as text.
- **An image denied by its ACL** gives `UnauthorizedAccessException`, not `IOException` (not pursued).
- **Two thread-pool threads per extraction:** performance only.
- **A filter calling MessageBox** could still show it (not provable).

## Left as they are
- The child inherits TEMP, which a Low process can't write to. None of this PC's filters need it; a third-party filter that does fails with a clear error.
- UTF-16 without a BOM is refused (SPEC: a BOM, else UTF-8). In a double-byte ANSI code page, an offset in the middle of a character isn't detected.
