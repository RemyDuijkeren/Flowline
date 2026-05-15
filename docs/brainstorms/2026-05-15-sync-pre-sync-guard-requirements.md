---
date: 2026-05-15
topic: sync-pre-sync-dirty-tree-guard
---

# SyncCommand: Pre-Sync Dirty-Tree Guard

## Summary

Add a pre-sync safety check to `SyncCommand` that aborts before calling `pac solution sync` if uncommitted changes exist in `solutions/<Name>/src/`. Introduces a `--force` flag to bypass the guard when deliberate overwrite is intended.

---

## Problem Frame

`pac solution sync` overwrites local XML files in `solutions/<Name>/src/` with no warning. A developer who has half-edited a form component and then runs sync to pick up a colleague's change loses that work silently. Git can recover it — but only if they notice, check the diff, and act before the next commit.

`DeployCommand` already enforces a repo-clean gate before deploying (`GitUtils.AssertRepoCleanAsync`), recognizing that acting on a dirty tree risks data loss. `SyncCommand` has no equivalent guard, even though its data-loss scenario is equally concrete: local edits to tracked component XML are exactly the artifact a developer might be mid-way through when they run sync.

---

## Key Flows

- F1. **Sync with clean src/**
  - **Trigger:** Developer runs `flowline sync` with no uncommitted changes in `solutions/<Name>/src/`
  - **Steps:** Guard runs `git status --porcelain solutions/<Name>/src/` → no output → guard passes → sync proceeds as normal
  - **Outcome:** Normal sync flow; no additional output from the guard
  - **Covered by:** R1, R3

- F2. **Sync with dirty src/**
  - **Trigger:** Developer runs `flowline sync` with uncommitted changes in `solutions/<Name>/src/`
  - **Steps:** Guard runs `git status --porcelain solutions/<Name>/src/` → output present → guard lists dirty files → aborts with non-zero exit code
  - **Outcome:** Sync does not run; developer sees which files are dirty and what to do
  - **Covered by:** R1, R2, R4

- F3. **Sync with `--force` on dirty src/**
  - **Trigger:** Developer runs `flowline sync --force` with uncommitted changes in `solutions/<Name>/src/`
  - **Steps:** Guard is skipped entirely → sync proceeds as normal
  - **Outcome:** Files in `src/` may be overwritten; no guard output
  - **Covered by:** R5

---

## Requirements

**Guard**

- R1. Before `pac solution sync` is invoked, run `git status --porcelain` scoped to `solutions/<Name>/src/`.
- R2. If any changes are found — modified, staged, deleted, or untracked files — abort with exit code 1 and print the list of affected paths.
- R3. If no changes are found, proceed without any additional output.
- R4. The error message must name each dirty file and instruct the developer to stash or commit first, or pass `--force` to skip.

**Flag**

- R5. Add a `--force` flag to `SyncCommand.Settings`. When set, skip the dirty-tree check entirely. The flag does not affect any other sync behavior.

---

## Acceptance Examples

- AE1. **Covers R1, R2, R4.** Given `solutions/MySolution/src/FormXml/form_123.xml` is modified but not committed, when `flowline sync` runs, then sync aborts, the error output names that file, and exit code is 1.

- AE2. **Covers R2.** Given `solutions/MySolution/src/Entities/newEntity.xml` is an untracked new file, when `flowline sync` runs, then sync aborts and the untracked file appears in the error output.

- AE3. **Covers R3.** Given `solutions/MySolution/src/` has no uncommitted changes, when `flowline sync` runs, then the guard produces no output and sync proceeds normally.

- AE4. **Covers R5.** Given `solutions/MySolution/src/FormXml/form_123.xml` is modified, when `flowline sync --force` runs, then the guard is skipped and sync proceeds as normal.

- AE5. **Covers R1, R3.** Given uncommitted changes exist only in `solutions/<Name>/Plugins/` or `solutions/<Name>/WebResources/` (not in `src/`), when `flowline sync` runs, then the guard passes and sync proceeds normally.

---

## Success Criteria

- A developer who runs sync with half-edited solution XML gets an error listing those files — not a silent overwrite.
- `flowline sync --force` bypasses the check for deliberate overwrite scenarios.
- A clean-folder sync runs without new output or perceptible latency beyond a single `git status` call.

---

## Scope Boundaries

- No interactive stash prompt — developer must stash manually before re-running.
- No repo-wide dirty check — only `solutions/<Name>/src/` is in scope.
- No auto-stash-and-restore around the sync.
- `DeployCommand`'s existing repo-wide `AssertRepoCleanAsync` is unchanged.
- `--force` only bypasses the dirty-tree check; it does not affect any other sync behavior.

---

## Key Decisions

- **Hard abort, not warning:** Warning-only would let sync overwrite files immediately after printing it, defeating the purpose.
- **Scoped to `src/` only, not the full solution folder:** `pac solution sync` only writes to `solutions/<Name>/src/`. Checking `Plugins/` or `WebResources/` would block a common workflow where plugin code is uncommitted while the developer syncs to capture complementary solution XML changes from DEV.
- **Untracked files included:** Manually-created XML in `src/` can be overwritten by sync; excluding untracked files leaves a gap in the protection. XML edits in `src/` should always be committed before running other commands.
- **`--force` bypass required:** Without an escape hatch, a developer who deliberately wants the Dataverse version has no path forward.

---

## Dependencies / Assumptions

- `GitUtils.IsRepoCleanAsync` (`src/Flowline/Utils/GitUtils.cs:117`) runs `git status --porcelain` repo-wide with no path parameter. A path-scoped variant or inline check is needed; this is a planning decision.
- The `src/` path (`solutions/<Name>/src/`) is derived from `projectSln.Name` and `RootFolder`, both available after the existing solution-validation block in `SyncCommand.ExecuteFlowlineAsync`. The guard must be placed after solution validation (to know the folder) and before `EnsureMapFilePathAsync` (the first write operation).
- Message wording follows the tone-of-voice guide (`docs/tone-of-voice.md`).

---

## Outstanding Questions

### Deferred to Planning

- [Affects R1][Technical] Add a new `GitUtils.HasUncommittedChangesAsync(string path)` overload, or inline the path-scoped `git status` call directly in `SyncCommand`? Inline is simpler; a shared helper is reusable for future path-scoped checks.
- [Affects R1][Technical] Confirm the exact insertion point in `SyncCommand.ExecuteFlowlineAsync` — should be after line 53 (`.cdsproj` existence check) and before line 55 (`EnsureMapFilePathAsync`).