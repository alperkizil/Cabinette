# Bug hunt fix plan

All 13 findings from [README.md](README.md), grouped by root cause.
Items marked **[my default]** are choices I made. The user confirmed all 10 of them on 2026-10-21 (listed at the end).

## How the work runs

- **Three agents in parallel**, each in its own git worktree (A, B and C below). Each agent fixes its findings, turns
  their repro tests (`*.cs.txt` here) into regression tests that assert the fixed behavior, and runs all three test suites.
- **Then me:** merge the three branches into `main` (as for stages 1 and 2), build, run every test, read every diff,
  and update SPEC.md (new behavior, `[my default]` marks), HANDOFF.md and the status column in README.md.
- **Safety:**
  - Tests run in temp folders only. Your real library, your vault and the opt-in Windows tests (Recycle Bin, clipboard,
    menus) stay untouched.
  - `main` before the fixes is `25f5f76`. I'll tag it `before-bughunt-fixes` so you can compare or go back. Nothing is pushed.
- **Contracts:** no contract change is expected. If one turns out to be needed, it will be the smallest additive one and listed.
- **Optional, only with your OK:** a short run of the real app on a throwaway library (Move vault and Restore), driven
  by UI Automation like in session 2.

## A. Move vault and Restore: #1, #2, #3, #4, #11

Files: `Core/Services/LibraryService.Stage2.cs` (move, restore side), `Core/Vault/BackupArchive.cs`, `Core/Vault/VaultFiles.cs`,
`Core/Vault/FileOps.cs`, `App/Services/VaultOperations.cs`.

- **#1 Open thread keeps old paths.**
  - Move vault's change event lists every category, so open threads, the chat list and search reload with the new paths.
  - Today the event has no ids, so the thread decides nothing changed.
- **#2 Cleanup deletes foreign files.**
  - Move vault and Restore record every folder and file they create. On cancel or failure they delete exactly those files,
    then those folders if they are empty.
  - A file someone else put there is never touched, including one whose name collided with the copy.
- **#3 Crafted backup writes outside the vault.**
  - Before the restored library is used, its database is checked:
    - Every category folder name and every vault file name must be one valid name: no `\`, `/` or `:`, no `..`, no
      device names, no full paths.
    - The categories must form a proper tree.
  - If a check fails, the restore stops with "This backup's library is damaged or was changed, so it can't be
    restored." **[my default]**
  - Nothing is created outside the chosen folder, and the previous library is put back (the existing rollback).
  - Pointer paths and settings in a backup are still taken as they are, as today: a backup is your own library.
- **#4 Changes during Move vault are lost.**
  - After copying and verifying, the old vault is listed again and compared with what was copied (size and modification time).
  - Changed and new files are copied again, and deleted ones are removed from the copy. This repeats up to 3 times. If the
    vault still keeps changing, the move stops, cleans up and names the file. **[my default]**
  - Before "Send old vault to Recycle Bin", Cabinette checks whether anything in the old vault changed after the move.
    If something did, it warns and lists the files first. **[my default]**
- **#11 Hidden and system attributes are lost.**
  - Move vault re-creates folders with their attributes. Files already keep theirs.
  - Backups store each file's and folder's attributes in the zip's standard attribute field, and Restore puts them back.
  - Backups made before this fix have no attributes stored, so they restore as today. **[my default]**

## B. Adding and the vault monitor: #5, #6, #7, #9, #10, #12

Files: `Core/Services/LibraryService.Adding.cs`, `LibraryService.Items.cs` (the duplicate check only),
`LibraryService.Stage2.cs` (Migrate only), `LibraryService.Reconcile.cs`, `LibraryService.cs` (startup), `Core/Vault/VaultReconciler.cs`,
`Core/Vault/NameRules.cs`.

- **#6 and #12 Copy staging.**
  - Adds and Migrate copy into one hidden staging folder, `<vault>\.cabinette-tmp`, instead of into the category folder.
    The file is moved into place under the write lock, which on the same drive is a rename. **[my default]**
  - Category folders never hold an open temp file, so renaming a category while a file is copied into it works (#6).
  - The staging folder is emptied every time Cabinette starts (#12).
  - The startup rescan deletes leftovers from earlier versions: hidden `.cabinette-<32 hex>.tmp` files in category folders. **[my default]**
- **#7 An add in flight during Move vault.**
  - When the add commits, it checks that the vault is still the one it copied into.
  - If the vault moved, the add copies the file again, from the original into the new vault. **[my default]**
    - Copy mode never touches the original, so it is still there.
    - If the original is gone by then, that file fails with "The vault moved while this file was being copied. Add it again."
  - Migrate gets the same check.
- **#5 The "missing" mark on vault files.**
  - When an add or the monitor checks for duplicates, a vault item's file is looked for on disk instead of trusting the
    mark, but only while the vault folder itself is reachable. An unplugged drive therefore never re-links anything.
  - The monitor clears the mark when it finds the file in place.
  - Covered: the drive-unplugged case, re-adding a file deleted while monitoring was paused, and the duplicate copy.
- **#10 Stale pointer hash.**
  - A pointer item only counts as "already in Cabinette" if its file still has the size and modification time Cabinette stored.
  - If it changed, small files (16 MB or less) are re-hashed right away and the duplicate check uses the real content.
    Larger ones count as "not a duplicate" and are re-hashed in the background. **[my default]**
- **#9 Folder and file renamed while closed.**
  - The rescan plan keeps one entry per item.
  - The rescan then succeeds: the file keeps its id and tags, the folder becomes a "Holiday" category, and the empty "Trip"
    category goes.

## C. Delete and first run: #8, #13

Files: `Core/Services/LibraryService.Items.cs` (delete only), `LibraryService.Categories.cs`, `LibraryService.cs` (first run),
`Windows/ShellRecycleBin.cs`.

- **#8 "Delete permanently" does nothing after a late Recycle Bin refusal.**
  - **Earlier check:** before anything changes, Cabinette also checks each file's size against its drive's Recycle Bin
    size limit (the same registry key it already reads). "Too big for the Recycle Bin" is then caught up front, where "No"
    leaves everything as it was. **[my default]**
  - **If the shell still refuses after the commit:** Cabinette remembers exactly those files.
    - "Delete permanently" deletes exactly them, for items, a category or Empty Deleted.
    - It never looks item ids up again, so it can't hit a new item that got the same id.
  - **"No" in that late case:** the file stays on disk, but its library entry is already gone. A vault file comes back
    as a new, untagged item at the next rescan, and a pointer's real file simply stays. **[my default]**
- **#13 First run accepts `%LocalAppData%`.**
  - A vault folder that contains Cabinette's data folder is refused, with no "use it anyway". **[my default]**
  - This is the same check Restore already makes, now also in Core's first-run setup.

## Verification

- Each finding's repro test becomes a regression test that asserts the fixed behavior, one test class per agent
  (`BugHuntMoveRestoreTests`, `BugHuntAddMonitorTests`, `BugHuntDeleteFirstRunTests`) in the Core, Windows and App test projects.
- Commands you can run afterwards (no secrets, temp folders only):
  ```
  dotnet build Cabinette.slnx
  dotnet test tests/Cabinette.Core.Tests --filter "FullyQualifiedName~BugHunt"
  dotnet test tests/Cabinette.App.Tests --filter "FullyQualifiedName~BugHunt"
  dotnet test tests/Cabinette.Core.Tests
  dotnet test tests/Cabinette.Windows.Tests
  dotnet test tests/Cabinette.App.Tests
  ```
- Diff of everything against the tag: `git diff before-bughunt-fixes..main --stat`.

## Choices for you ([my default])

1. **Move vault, files changing during the move (#4):** copy what changed, up to 3 rounds, then stop and name the file.
   Alternative: stop at the first change.
2. **Before recycling the old vault (#4):** warn and list files changed after the move. Alternative: no check.
3. **An add in flight during Move vault (#7):** copy again automatically into the new vault. Alternative: fail that
   file with a message.
4. **Crafted or damaged backup (#3):** refuse the whole restore. Alternative: skip the bad categories and files.
5. **Backups made before the fix (#11):** restore as today, without hidden flags. It is worth making a new backup after
   this update.
6. **Staging folder (#6, #12):** hidden `<vault>\.cabinette-tmp`, emptied at start. Old-style temp files in category
   folders are deleted by the startup rescan.
7. **Stale pointer hash (#10):** re-hash pointer files of 16 MB or less right away, larger ones in the background.
8. **Late Recycle Bin refusal (#8):** add the size check up front. After a late refusal, "Delete permanently" deletes
   exactly the refused files, and "No" keeps the file but its tags are lost.
9. **First run (#13):** hard refusal of a vault that contains the data folder, with no override.
10. **How the work runs:** three parallel agents in worktrees, then I merge and review. Alternative: I do it all in this
    session, one group after another (slower, no merges).
