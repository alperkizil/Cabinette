**7 findings: 5 High, 2 Medium.** Static review only; no files changed, builds run, or tests executed.

1. **Title: Cancelling a restore or vault move can permanently delete unrelated destination files.**

   **Severity:** High

   **Where:** [src/Cabinette.Core/Vault/BackupArchive.cs:358](../../../src/Cabinette.Core/Vault/BackupArchive.cs#L358), `RestoreCoreAsync`; [src/Cabinette.Core/Services/LibraryService.Stage2.cs:205](../../../src/Cabinette.Core/Services/LibraryService.Stage2.cs#L205), `MoveVaultLockedAsync`.

   **What goes wrong:** Start a large restore into an empty folder. While extraction runs, move an unrelated document into that folder using Explorer, then cancel the restore. Cleanup permanently deletes that document along with the extracted files. A failed or cancelled vault move has the same problem. A concurrent file causing an extraction name collision can also trigger this cleanup.

   **Evidence:**

   ```csharp
   FileOps.TryDeleteTree(vault, keepRoot: vaultExisted);
   FileOps.TryDeleteTree(target, keepRoot: targetExisted);
   ```

   Both failure paths remove the destination’s entire contents. `keepRoot` preserves only the directory itself. Neither path tracks which files this operation actually created.

   **Confidence:** Likely

   **Fix idea:** Extract/copy into a private staging directory, or journal created entries and remove only those entries on failure.

2. **Title: Vault-move verification misses changes made to source files after copying.**

   **Severity:** High

   **Where:** [src/Cabinette.Core/Services/LibraryService.Stage2.cs:141](../../../src/Cabinette.Core/Services/LibraryService.Stage2.cs#L141), `MoveVaultLockedAsync`.

   **What goes wrong:** Start moving a large vault. After a document has been copied, save another edit to its original location while the remaining files are copying. Verification succeeds, and Cabinette switches to the older document in the new vault. Accepting “Send old vault to Recycle Bin” then removes the only current version from its working location; the permanent-delete fallback can destroy it.

   **Evidence:**

   ```csharp
   hashes[i] = await FileOps.CopyAndHashAsync(file.FullPath, destination, ...);
   hash = await FileOps.HashFileAsync(destination, ...);
   if (size != file.Size || !hash.AsSpan().SequenceEqual(hashes[i]))
   ```

   Verification compares the destination against the bytes previously copied, not against the source’s current contents. Source streams allow writers, and the library write lock does not prevent external editors from saving. Files added to the source after enumeration are similarly omitted.

   **Confidence:** Likely

   **Fix idea:** Protect or snapshot the source throughout the move, or detect source changes and refuse the switch. Do not offer source removal unless the copied state is still current.

3. **Title: An in-flight add can commit its file into the old vault after a move.**

   **Severity:** High

   **Where:** [src/Cabinette.Core/Services/LibraryService.Adding.cs:338](../../../src/Cabinette.Core/Services/LibraryService.Adding.cs#L338), `AddCopyAsync`; [src/Cabinette.App/Services/FileIntake.cs:207](../../../src/Cabinette.App/Services/FileIntake.cs#L207), `AddPathsCoreAsync`.

   **What goes wrong:** Begin importing a large file, then open Settings and move the vault before that import finishes. Move excludes the import’s temporary file and switches the vault path. The import subsequently acquires the write lock and completes successfully—but places its final file in the old vault. Its database row resolves to the new vault, where the file does not exist. Recycling the old vault also removes the orphaned import.

   **Evidence:**

   ```csharp
   var vault = RequireVault();
   // Later, inside WriteCoreAsync:
   var currentTemp = Path.Combine(tree.FolderPath(vault, targetId), tempName);
   var finalPath = Path.Combine(tree.FolderPath(vault, targetId), finalName);
   ```

   The commit uses the captured vault without checking `ctx.VaultPath`. Copying occurs outside the write lock. File intake does not register with `AppLifetime.BeginOperation`, so the UI permits this overlap.

   **Confidence:** Likely

   **Fix idea:** Revalidate the vault generation/path before committing an add and relocate or retry its temporary file when necessary. Alternatively, serialize moves against complete intake operations.

4. **Title: The embedded backup database bypasses restore’s path-containment checks.**

   **Severity:** High

   **Where:** [src/Cabinette.Core/Vault/BackupArchive.cs:325](../../../src/Cabinette.Core/Vault/BackupArchive.cs#L325), `RestoreCoreAsync`; [src/Cabinette.Core/Data/CategoryTree.cs:160](../../../src/Cabinette.Core/Data/CategoryTree.cs#L160), `FolderPath`; [src/Cabinette.Core/Services/LibraryService.cs:193](../../../src/Cabinette.Core/Services/LibraryService.cs#L193), `CreateVaultFolders`.

   **What goes wrong:** Supply an otherwise valid backup whose database contains a top-level category with `folder_name = '..\outside'` or an absolute directory path. All ZIP entry names can be safe. During restore, Cabinette nevertheless creates the database-specified directory outside the selected vault, wherever the current account has permission.

   **Evidence:**

   ```csharp
   public string FolderPath(string vaultPath, long id)
       => Path.Combine(vaultPath, RelativeFolder(id));
   ctx.EnsureDirectory(ctx.Tree.FolderPath(vault, category.Id));
   ```

   ZIP paths are validated, but restored category path components are loaded directly from SQLite and used by `CompleteRestoreAsync`. Normal category-creation validation is bypassed. Restored item filenames likewise lack containment validation when resolved.

   **Confidence:** Likely

   **Fix idea:** Validate the embedded database before activating it: require safe single-component folder/file names, valid category relationships, and canonical copy-item paths contained within the new vault.

5. **Title: A late Recycle Bin refusal destroys metadata before the permanent-delete retry.**

   **Severity:** High

   **Where:** [src/Cabinette.Core/Services/LibraryService.Items.cs:680](../../../src/Cabinette.Core/Services/LibraryService.Items.cs#L680), `DeleteItemsAfterCommit`; [src/Cabinette.App/Services/RecycleBinFallback.cs:31](../../../src/Cabinette.App/Services/RecycleBinFallback.cs#L31), `RunAsync`.

   **What goes wrong:** Delete a pointer item whose file is too large for its drive’s Recycle Bin. The volume precheck passes, but the shell refuses the actual operation. By then, the item, memberships, and tags have been deleted from SQLite. Declining permanent deletion does not restore them. Accepting it retries the deleted item ID, finds no row, and reports success while leaving the real file untouched. Category deletion can instead retry a category that no longer exists.

   This is an incomplete integration of stage 1 findings **#5 and #10**.

   **Evidence:**

   ```csharp
   ctx.Execute("DELETE FROM items WHERE id IN ...", ...);
   ctx.OnCommitted(() => { /* DeleteFiles can report RecycleBinUnavailable */ });
   await delete((options ?? new DeleteItemsOptions()) with { PermanentWhenNoRecycleBin = true });
   ```

   The UI assumes refusal happened before changes, but Core also emits that error after committing them. Pointer files are not monitored, so their lost metadata is not even followed by automatic re-import.

   **Confidence:** Likely

   **Fix idea:** Preserve or restore the records for refused files before presenting the fallback. Retry an explicit failed-item plan rather than IDs whose rows have already been deleted.

6. **Title: Renaming a folder and its contained file while closed can make every rescan fail.**

   **Severity:** Medium

   **Where:** [src/Cabinette.Core/Vault/VaultReconciler.cs:951](../../../src/Cabinette.Core/Vault/VaultReconciler.cs#L951), `LoadAllItems`; also `PairFoldersByContent`, `CollectFiles`, and `PairFiles`.

   **What goes wrong:** Create category `Trip` containing one tagged file, `a.jpg`. Close Cabinette, rename the folder to `Holiday`, and rename its file to `b.jpg` without changing its contents. On startup, folder matching cannot recognize it by filename. The full scan then plans both deletion and movement of the same item ID. Applying that plan fails with a foreign-key error and rolls back; subsequent full rescans repeat the failure.

   **Evidence:**

   ```csharp
   var items = ItemsOf(category.CategoryId!.Value); // Loads this home's items.
   // Later, LoadAllItems loads them again:
   foreach (var item in connection.Query<PlanItem>(ItemSql, ...))
       AddLoaded(item);
   ```

   `AddLoaded` appends without deduplicating. `PlanItem` is a class, and `_missingItems` uses reference equality, so both objects survive. Content matching removes only one object from the missing set. Applying the plan deletes the row first, then attempts to add a membership for the moved, now nonexistent item.

   **Confidence:** Likely

   **Fix idea:** Maintain one `PlanItem` per database ID throughout planning, including when upgrading partially loaded homes to a full load.

7. **Title: Deduplication trusts stale records and discards valid imports.**

   **Severity:** Medium

   **Where:** [src/Cabinette.Core/Services/LibraryService.Items.cs:492](../../../src/Cabinette.Core/Services/LibraryService.Items.cs#L492), `FindByHash` and `IsFileGone`; [src/Cabinette.Core/Services/LibraryService.Adding.cs:384](../../../src/Cabinette.Core/Services/LibraryService.Adding.cs#L384), `AddCopyAsync`.

   **What goes wrong:** Two concrete cases remain:

   - Pause monitoring, delete a vault file externally, then re-add its original. Its unchanged `is_missing = 0` makes Cabinette discard the replacement as a duplicate, leaving the library file absent. This is an incomplete case of stage 1 **#2**.
   - Add a pointer containing version A, edit that real file to version B, then import another file containing version A. The pointer’s cached hash still matches A, so Cabinette discards the new copy and directs the user to B.

   **Evidence:**

   ```csharp
   row.ItemMode == ItemMode.Pointer
       ? string.IsNullOrEmpty(row.PointerPath) || !File.Exists(row.PointerPath)
       : row.IsMissing != 0;
   ```

   Copy candidates are not checked for physical existence. Pointer candidates are checked only for existence, not whether their stored hash still describes their contents. A supposedly live match causes `AddCopyAsync` to delete the temporary import.

   **Confidence:** Likely

   **Fix idea:** Check candidate existence for both modes, and revalidate changed candidate contents before declaring a duplicate. Monitoring can be paused or behind, and pointers are deliberately unwatched.

**Areas reviewed thoroughly**

- Core transaction/undo handling; add, deduplication, hashing, rename, membership, and deletion paths.
- Vault watcher/reconciler; move, migration, backup, and restore implementation.
- App intake and vault-operation coordination, recycle fallback, monitor lifecycle, and single-instance protocol/security.
- Relevant regression tests and the two previously fixed stage 2 issues.

**Areas not covered thoroughly**

- WPF thread rendering, previews, selection, search/filter UI, and accessibility.
- Thumbnail/media pipelines and most shell-menu COM/resource lifetimes.
- Runtime Windows behavior, performance measurements, and exhaustive junction, symlink, and long-path cases.