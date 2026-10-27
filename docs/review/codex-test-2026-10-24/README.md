# Codex path test, phase 1 (2026-10-24, run by the integrator through codex exec)

## Setup
- Codex CLI 0.159.3, model gpt-6.1-sol, effort high, `-s read-only`, `codex exec --json`.
- The server was attached for that run only, with `-c mcp_servers.cabinette_test.*`; the user's Codex config was never changed. Afterwards `codex mcp list` showed the same three servers as before (codex_app, cua_repl, node_repl).
- `cabinette-mcp.exe`: a Debug build of fix/mcp-server at 8b1c853 (worktree .claude\worktrees\codex-test), run with `--data-dir <temp>` and no `--app`, and no Cabinette.exe next to it.
- Backend: a throwaway fake pipe server (scratchpad `fakecab`, never committed) on the temp folder's pipe, with canned data.
- Prompt: 14 steps covering list, search, read (text, extracted text, image, refused binary), get_files, add, tag, delete, backup and status, a busy move_vault, and a 70 s slow migrate_pointers.

## Gotcha found by the harness itself
- `-c 'mcp_servers.x.args=["--data-dir","C:\\Users\\..."]'` passed from Git Bash reached Codex with single backslashes. TOML then rejected `\U`, Codex fell back to a raw string, and it failed with "expected a sequence".
- TOML literal strings work: `['--data-dir','C:\Users\...']`.
- For the README: in a hand-edited config.toml, Windows paths need single quotes or doubled backslashes. `codex mcp add` on the command line is unaffected.

## Results (fake log matched Codex's intent for every call)
- **What worked:**
  - Codex saw all 25 tools with no schema problems.
  - The client name arrived as "Codex" (`codex-mcp-client`), and `v=1`.
  - Arguments were exact: camelCase, ids as numbers, Windows backslash paths preserved.
  - Non-ASCII ("Şirket: Örnek A.Ş.") came through intact.
  - The image arrived as a real MCP image block, which the model saw as an image.
  - A refused read came back with `isError: true` and the message, and the model understood it.
  - No `cabinette-mcp.exe` was left running after Codex exited.
- **Codex approval:** tools marked destructive (delete_files, move_vault) are stopped by Codex's own approval: "MCP tool call requires approval, but approval policy is never" in exec mode. Interactive Codex asks the user. Non-destructive changes (add_files, tag_files, create_backup) ran without approval. This is Codex's behavior, good for safety; the README should say it.
- **Timeout:** the 70 s migrate_pointers reply was waited for without a timeout, so Codex 0.159.3's MCP tool timeout is longer than 60 s, or not applied by default. The real app replies at once anyway.

## Real server findings (sent to the fix/mcp-server agent)
1. Every tool description repeats the long intro paragraph; it belongs only in the server instructions.
2. `nextOffset` is omitted (null not serialized) where the descriptions say null at the end.
3. The "exactly one of" selector rules (show_in_cabinette, list_files) should be up front in the descriptions.

## Not findings: artifacts of the fake's canned data
The model also flagged these, but they come from the stand-in, not the server:
- add/tag results not echoing the request
- wrong totals
- `createdAt` 0001
- `subcategoryCount` 0
- missing category paths and hash in get_files
- no backup exclusion count

## Phase 2 (after merges, needs the user's OK: tray icon and toasts)
The same run against a real dev Cabinette on a throwaway library.

## Phase 2: real dev Cabinette (2026-10-24, the user's OK, on the user's desktop)
- **Setup:**
  - A throwaway library seeded by `tools/Cabinette.DevSeed` in `%TEMP%\cabinette-desktop-check`.
  - The dev Cabinette ran visibly with `--data-dir`.
  - `cabinette-mcp.exe` (Debug) ran with full `--data-dir` and `--app`.
  - Codex used `-c ...default_tools_approval_mode="approve"` and `tool_timeout_sec=300` for this run only.
- **All 13 steps succeeded with no errors:**
  - list, create a category, add six sample files in one call, search
  - read: PDF, docx and md text; a PNG as an image (a non-alpha PNG is sent as JPEG, 1920×1200 → 1568×980, by design)
  - tag, star, rename, note
  - delete (the vault copy went to the Recycle Bin)
  - show_in_cabinette
  - backup and vault move, both "succeeded" at the first status check, with the old vault kept
  - list tags
- **The xlsx** came back as empty text without an error. The sample really has no cell text (empty sharedStrings, no values), so this is correct.
- **The activity log:** 20 entries, all with source "Codex", none failed.
- **Claude Code's headless run** (`claude -p`) couldn't start: the CLI's own sign-in had expired ("OAuth session expired"). The `cabinette-dev` registration itself showed "Connected" in `claude mcp get`.

## Phase 2b: Claude Code against the same dev Cabinette (after the user renewed the CLI sign-in)
- **Setup:** `claude -p --model sonnet --allowedTools mcp__cabinette-dev`, through the local-scope `cabinette-dev` registration.
- **12 of 13 steps succeeded.** The log has 20 entries with source "Claude Code"; the one marked failed is step 12.
- **Step 12** asked, by a mistake in the test prompt, to move the vault to the folder it already used (`vault2`). Cabinette refused it correctly: "The new vault folder can't be the current vault, be inside it or contain it." The move itself was exercised in the Codex run.
- **Behavior confirmed live:**
  - Re-added files came back as duplicates (no second copy) and were added to the new category.
  - Tag and star calls on files that already had them reported `count: 0` (the review fix F3b).
  - A rename keeps the file in its home category's folder.
