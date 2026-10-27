**7 findings: 4 High, 3 Medium.** Static review only; no files changed and no tests run.

1. **Title: A crafted backup can write an executable outside the restored vault**
   - **Severity:** High
   - **Where:** `src/Cabinette.Core/Vault/BackupArchive.cs:325` (`RestoreCoreAsync`); `src/Cabinette.Core/Data/CategoryTree.cs:160` (`FolderPath`); `src/Cabinette.Core/Services/LibraryService.Reconcile.cs:357` (`TryFileRootFile`).
   - **What goes wrong:** A crafted backup can contain a valid database whose top-level Executable category has an absolute `folder_name` pointing to the victim’s Windows Startup folder, plus `vault/payload.exe`. Restore accepts both. After restart, the monitor auto-files the loose executable into that category, placing it in Startup.
   - **Evidence:** Restore opens the supplied database without validating its stored path components. Category paths use:
     ```csharp
     Path.Combine(vaultPath, RelativeFolder(id))
     ```
     An absolute database folder path overrides `vaultPath`. Root-file reconciliation subsequently executes:
     ```csharp
     ctx.MoveFile(file.Info.FullName, Path.Combine(folder, name));
     ```
     ZIP-entry traversal checks do not cover these database-derived destinations.
   - **Confidence:** Likely.
   - **Fix idea:** Import into a trusted schema and validate category/file-name components and resolved copy-mode paths before opening the restored library or enabling monitoring.

2. **Title: Move/restore rollback permanently deletes unrelated files added to the destination**
   - **Severity:** High
   - **Where:** `src/Cabinette.Core/Services/LibraryService.Stage2.cs:205` (`MoveVaultLockedAsync`); `src/Cabinette.Core/Vault/BackupArchive.cs:358` (`RestoreCoreAsync`).
   - **What goes wrong:** Start a lengthy move or restore into an empty folder. While it runs, copy an unrelated document into that destination using Explorer. Cancel the operation—or encounter an extraction/copy failure—and Cabinette permanently deletes that document during cleanup. A destination-name collision can itself trigger the failure and then delete the conflicting user file.
   - **Evidence:** Both failure paths recursively remove the entire destination:
     ```csharp
     FileOps.TryDeleteTree(target, keepRoot: targetExisted);
     FileOps.TryDeleteTree(vault, keepRoot: vaultExisted);
     ```
     `keepRoot` preserves only the directory itself; cleanup does not distinguish files created by the operation from subsequently added files.
   - **Confidence:** Likely.
   - **Fix idea:** Journal created files and directories. Remove only those files on failure, then remove created directories only when empty.

3. **Title: An in-flight import can commit its file into the old vault after a move**
   - **Severity:** High
   - **Where:** `src/Cabinette.Core/Services/LibraryService.Adding.cs:338` (`AddCopyAsync`), especially lines 378–401.
   - **What goes wrong:** Begin importing a large file, then move the vault while that copy is still running. Move skips the hidden temporary file and switches the library to the new vault. When the import acquires the write lock, it finalizes the file in the old vault but inserts a copy-mode row that resolves under the new vault. The subsequent monitor rescan can delete that row and its metadata as missing.
   - **Evidence:** `AddCopyAsync` captures `vault` before copying, then reuses it inside the later transaction:
     ```csharp
     var vault = RequireVault();
     var currentTemp = Path.Combine(tree.FolderPath(vault, targetId), tempName);
     var finalPath = Path.Combine(tree.FolderPath(vault, targetId), finalName);
     ```
     It never checks that `ctx.VaultPath` still equals that captured vault. File intake does not register itself with `AppLifetime.BeginOperation`, so the UI permits this overlap.
   - **Confidence:** Likely.
   - **Fix idea:** Coordinate vault moves with in-flight imports, or detect a changed vault under the write lock and safely transfer/retry the staged copy before inserting its row.

4. **Title: Move verification misses source edits and newly added files**
   - **Severity:** High
   - **Where:** `src/Cabinette.Core/Services/LibraryService.Stage2.cs:117` (`MoveVaultLockedAsync`), especially lines 141–178.
   - **What goes wrong:** During a long move, another application edits a source file after Cabinette has copied it, or Explorer adds a file after enumeration. Move still reports successful verification and switches to a stale/incomplete vault. Accepting the offered deletion of the old vault removes the only current version or the uncopied file.
   - **Evidence:** The source inventory is collected once. Verification hashes only the destination and compares it with the hash computed during copying:
     ```csharp
     hash = await FileOps.HashFileAsync(destination, ...);
     if (size != file.Size || !hash.AsSpan().SequenceEqual(hashes[i]))
     ```
     There is no final source-content or inventory comparison. The write lock excludes Cabinette writes, but external applications remain free to modify the source.
   - **Confidence:** Likely.
   - **Fix idea:** Detect and reconcile source changes before switching. Revalidate the old vault against the verified copy before offering to remove it.

5. **Title: A late Recycle Bin refusal makes the permanent-delete retry ineffective**
   - **Severity:** Medium
   - **Where:** `src/Cabinette.Core/Services/LibraryService.Items.cs:680` (`DeleteItemsAfterCommit`); `src/Cabinette.App/Services/RecycleBinFallback.cs:31` (`RunAsync`).
   - **What goes wrong:** Delete a file that resides on a recyclable volume but is too large for its Recycle Bin. The shell refuses it after Cabinette has committed deletion of its database row. The UI asks whether to delete permanently; accepting retries the same item IDs, which no longer exist, so the file remains. Declining also leaves its library entry and tags deleted. Category deletion can similarly leave the retry targeting an already-deleted category.
   - **Evidence:** Item rows are deleted before the post-commit `DeleteFiles` callback. That callback can return `RecycleBinUnavailable`; the fallback then invokes the original deletion delegate again with `PermanentWhenNoRecycleBin = true`.
   - **Confidence:** Likely.
   - **Fix idea:** Retain recoverable metadata for refused files and make the retry operate on those actual pending deletions. This is an incomplete interaction between earlier findings **#5 and #10**.

6. **Title: The open thread keeps old file paths after moving the vault**
   - **Severity:** Medium
   - **Where:** `src/Cabinette.App/ViewModels/Thread/ThreadViewModel.cs:716` (`ClassifyChange`); `src/Cabinette.Core/Services/LibraryService.Stage2.cs:195` (`MoveVaultLockedAsync`).
   - **What goes wrong:** Open a populated thread, move the vault, and return to that thread without navigating elsewhere. Its cached items still reference the old vault. Open/Copy/Show in folder use the old files; if the old vault was recycled, those actions fail despite the new copies existing. Keeping the old vault can instead cause the user to edit an obsolete copy.
   - **Evidence:** Move raises `Settings | Categories | Items` without category or item IDs. `ClassifyChange` reloads items only when one of those ID collections matches the thread; a general Categories flag refreshes only the header.
   - **Confidence:** Likely.
   - **Fix idea:** Include affected IDs in the move event or treat an unscoped Items change as a full item refresh. This is a gap in the event filtering associated with earlier finding **#6**.

7. **Title: “Found again” still fails for copy files when monitoring is paused**
   - **Severity:** Medium
   - **Where:** `src/Cabinette.Core/Services/LibraryService.Items.cs:520` (`IsFileGone`); `src/Cabinette.Core/Services/LibraryService.Adding.cs:385` (`AddCopyAsync`).
   - **What goes wrong:** Pause monitoring, delete a vault file externally, then add the same content again in copy mode. Cabinette reports a duplicate and deletes the newly staged copy instead of restoring the missing file. Resuming monitoring can then remove the original entry and its tags/categories.
   - **Evidence:** Copy-mode existence is inferred solely from:
     ```csharp
     : row.IsMissing != 0;
     ```
     The stale false flag makes `FindByHash` return `Missing: false`, causing `AddCopyAsync` to discard its temporary file as a duplicate.
   - **Confidence:** Likely.
   - **Fix idea:** Check the resolved copy-file path before discarding an incoming duplicate. Earlier finding **#2** remains incomplete for copy files whose missing state has not been refreshed.

**Areas you reviewed thoroughly**

- Core add, duplicate/relink, background hashing, category/item deletion, rehoming, transactions and undo journal.
- Vault monitor/reconciler; move, migration, backup and restore; relevant regression tests.
- App file intake, vault operations, lifetime, change-event routing, and single-instance pipe implementation.

**Areas you did not get to**

- A complete Windows COM/native-handle and shell-extension audit.
- Full XAML, media-viewer, clipboard and thumbnail implementation coverage.
- Runtime reproduction, builds, tests, or large-library benchmarks, as prohibited by the review setup.