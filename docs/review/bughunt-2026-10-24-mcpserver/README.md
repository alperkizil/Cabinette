# Bug hunt, cabinette-mcp.exe (2026-10-24)

**Status: all 5 findings fixed on 2026-10-24** by a fix agent (branch `fix/mcp-server`, `9856795`, merged into main as `f9a31aa`),
together with two notes from a real Codex session. The integrator ran the full suites.

- **Reviewed:** branch `worktree-agent-a10baf7c273746243` (`bd669b4`): `src/Cabinette.Mcp`. It's a stdio MCP server on `ModelContextProtocol.Core` 2.2.0 (Apache-2.0). It makes one pipe call per tool, retries "not ready", starts Cabinette in the tray with no inherited handles (breaking away from the client's job object where allowed), and maps results to MCP content.
- **Reviewer:** Claude Fable 5.1 (review branch `review/mcp-server-bughunt`, `fec3c6d`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.Mcp.Tests/McpReviewTests.cs` (`F*`); the `Dropped_*` tests record leads that didn't hold. The fix agent reverted each fix in turn and saw its tests fail.
- **Verdict on the piece:** careful and well tested. The stdout discipline, the detached launcher, the result mapping and the argument checks held up under adversarial probing, and so did cancellation. The defects were one lifecycle gap and command-line hardening.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 4 (2×2) | After the client closed stdin while a call was pending, the exe kept running until Cabinette answered, forever if it never did. The SDK waits for pending handlers. | The end of stdin cancels the session; pending calls are cancelled and their pipes closed. After 2 s the process exits regardless. |
| 3 | 4 (4×1) | Relative `--app` and `--data-dir` were resolved against the working folder, which the client sets to the open project. A cloned repository could carry its own `Cabinette.exe`. | Both are refused (exit code 2) when not full paths, `C:x` and `\x` included. The launcher also refuses a non-full path. |
| 4 | 4 (2×2) | A quoted full path from the model (`"C:\Data\x.pdf"`) became a nonsense relative path. | Whitespace and one pair of matching quotes are trimmed first (add_files, move_vault, create_backup). |
| 2 | 3 (3×1) | `--data-dir` naming the real data folder reached the real Cabinette, also from a Debug build ("a dev build can never reach the real library" was false). | Compared through `InstanceNames.NormalizeDirectory`: a Debug build refuses it (exit 2), and a Release build treats it as no `--data-dir`. |
| 5 | 2 (2×1) | `--data-dir --app` took `--app` as the folder. | A value starting with `--` counts as missing. |

**From the Codex test** ([the Codex test record](../codex-test-2026-10-24/README.md)):
- `nextOffset` is now always present (`null`) on the last page, in `list_files` and in `read_file`'s metadata.
- The "exactly one of" rules now open the `list_files` and `show_in_cabinette` descriptions.
- The model's remark that every tool repeated the introduction didn't reproduce: a real `tools/list` has it only in the server instructions. Guard tests keep it that way.

## Leads the reviewer dropped (kept as `Dropped_*` tests)
- **Cancellation:** a `notifications/cancelled` from the client closes the pipe to Cabinette within milliseconds. The C# SDK's own client never sends it; Claude Code does.
- **Parallel calls** from one client run concurrently.
- **Images** survive the MCP wire.
- **Client names:** Claude Code sends `claude-code` / "Claude Code"; Codex 0.159.3 sends `codex-mcp-client`. Both map correctly.
- **Stdout** carries only MCP; usage and errors go to stderr.
- **The launcher** uses a full application name (no PATH or working-folder search) and closes both handles; tests prove no handles are inherited.
- **Ids sent as strings** are accepted and passed on as numbers. `D:foo` resolves to `D:\foo`.
- **A cold first call can take up to ~65 s:** a 30 s start wait plus 30 s of "not ready" retries. Spec numbers.

## Left as they are
- The `--data-dir` check catches spellings of the real folder, not an 8.3 short name or a junction pointing at it (dev builds only, and deliberate).
- A client that closes stdin right after sending its requests gets no replies.
