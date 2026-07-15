---
name: flowline
description: Dataverse ALM via the Flowline CLI — plugin registration, web resource sync, and solution deploy for unmanaged Dataverse solutions. Use when the repo has a `.flowline` file at its root, when the user mentions Dataverse plugins, web resources, or solution deploy, or when spkl/PAC plugin registration workflows come up.
---

# Flowline — deterministic Dataverse ALM

## Detect

A Flowline project has `.flowline` at the repo root and `solutions/<SolutionName>/` folders.

- **No `.flowline`, but a plugin-DLL-only task** (register a compiled assembly, no cloned project): use standalone mode — `flowline push <SolutionName> --pluginFile <dll> --dev <url>` (or `--webresources <folder>`). No `clone` needed.
- **No `.flowline`, and the repo has a `spkl.json`, a Daxif config, a PACX folder, or an ALM Accelerator marker**: this is a migration candidate, not a fresh project — defer to the `flowline-migration` skill instead of suggesting `clone`.
- **No `.flowline`, greenfield**: suggest `flowline clone <SolutionName> --prod <url>`.

## Core loop

1. **Edit code.** Registration intent lives in the code, not in the Plugin Registration Tool:
   - C# plugin classes: `[Step]`, `[Filter]`, `[PreImage]`, `[PostImage]`, `[CustomApi]`
   - JS web resources: `// flowline:onload`, `// flowline:onsave`, `// flowline:onchange`, `// flowline:depends`
2. `flowline push --dry-run` → read the plan. Blast radius `targeted` → proceed. Anything else → show the user, ask.
3. `flowline push` → deterministic sync to DEV, including orphan cleanup.
4. `flowline sync` after any Maker Portal changes; commit the result.
5. Promote: `flowline deploy test` → `flowline deploy prod` (DTAP-gated).

If the solution's schema changed, run `flowline generate` to refresh early-bound C# types in `Plugins/Models/` before building — plugin code that references new or changed entities/columns needs it.

## Contract

- **Branch on exit codes, not output text.** Non-zero is always a named code; the error message embeds the fix command verbatim — run it, don't parse prose for it.
- **`push` exiting 0 doesn't mean the task is done.** It means registration succeeded. Verify the actual behavior (the step fires, the form loads the script) before reporting the change complete.
- **Authority rule:** `push` treats the repo as authoritative — anything in DEV not present in source gets deleted (that's the point of orphan cleanup). `sync` treats DEV as authoritative for solution metadata — the repo gets updated. Never push over DEV changes that haven't been synced yet; run `sync` first if in doubt.
- **Diagnose before guessing.** `flowline status` is read-only and reports environment, auth, and git state in one call — run it first when an exit code points at auth or connectivity (4, 10), rather than guessing at the fix.

## Exit codes

Exit codes are a stable public API — they don't change meaning across Flowline versions.

| Code | Name | Meaning | Corrective action |
|------|------|---------|-------------------|
| 0 | Success | Command completed | — |
| 1 | GeneralError | Unexpected/unhandled error | Check error output |
| 3 | NotFound | Solution not in Dataverse or repo | Verify solution name matches `.flowline` |
| 4 | NotAuthenticated | No PAC auth profile | Run: `pac auth create --environment <url>` |
| 10 | ConnectionFailed | Dataverse environment unreachable | Check environment URL in `.flowline` |
| 11 | ConfigInvalid | `.flowline` missing or malformed | Verify `.flowline` exists and is valid |
| 12 | DirtyWorkingDirectory | Uncommitted git changes block the operation | `git commit` or `git stash` first |
| 13 | BuildFailed | `dotnet build` or PAC pack failed | Fix errors in `Plugins/` and retry |
| 14 | VersionConflict | Target environment has a newer solution version | Add the `--force <specifier>` the error names |
| 15 | ValidationFailed | Drift detected, missing dependencies, invalid `--force` value, or schema mismatch | Run `flowline sync` first; check error output — an invalid `--force` value lists the ones that are valid for that command |
| 16 | Timeout | PAC CLI 60-minute limit exceeded | Retry; check environment health |
| 17 | ForceRequired | Destructive operation requires explicit confirmation | Add the `--force <specifier>` the message names, e.g. `--force config`, `--force recreate-assembly` |
| 18 | PartialSuccess | Deploy completed but orphan cleanup failed for some components | Check output for items to remove manually via maker portal |
| 19 | Inconclusive | `drift` couldn't run to completion (empty-input guard skipped the comparison) | Not a pass/fail signal — investigate the printed reason before trusting the result |
| 130 | Cancelled | Ctrl+C / SIGINT, or `deploy`'s first-import confirmation declined interactively | For the confirmation case: re-run with `--force first-import` to proceed non-interactively |

Codes 2 and 5 are intentionally unused.
