# `sync`'s dirty-tree gate misses uncommitted changes under `Solution/src/Other/`

- **Status**: fixed — 2026-07-22. Root cause was in the shared `SolutionChangeSummary.ComputeAsync`
  helper, not `SyncCommand` itself.
- **Severity**: high — silently bypasses the dirty-tree safety gate and bumps the live Dev solution
  version, with no warning, whenever the only uncommitted change is to `Other/Solution.xml` or
  `Other/Customizations.xml`.
- **Found**: 2026-07-22, live, dirtying `Solution/src/Other/Customizations.xml` and running
  `flowline sync` against `Cr07982`/DEV.

## Repro (pre-fix)

1. In a cloned Flowline project, append a byte to `Solution/src/Other/Customizations.xml` (or
   `Solution.xml`) without committing.
2. Run `flowline sync`.
3. Expected: reject with `Error: Uncommitted changes in 'Solution/src' — Commit or stash changes
   first, or re-run with --force dirty.` (`ExitCode.DirtyWorkingDirectory`).
4. Actual: no warning at all — sync proceeded straight to `Bump Patch version Cr07982... Version
   bumped: 2.0.3` and began exporting/unpacking from Dataverse, live, against Dev.

## Root cause

`SyncCommand.ExecuteFlowlineAsync` (`src/Flowline/Commands/SyncCommand.cs:81-97`) gates on
`SolutionChangeSummary.ComputeAsync(srcPath, ...).TotalFiles > 0`. But `ComputeAsync`
(`src/Flowline/Utils/SolutionChangeSummary.cs`) only incremented `fileCount` for git-changed paths
that `ParseComponentPath` could resolve to a displayable component. `ParseComponentPath` returns
`null` for `Other/*` outright (line 267-269) — that folder holds solution-wide files
(`Solution.xml`, `Customizations.xml`) with no single-component representation — so those changes
were silently excluded from `TotalFiles`, even though `git status` genuinely reports them as
modified.

Confirmed the same shape of bug in a second caller: `GenerateCommand`'s "dirty-tree guard" for the
generated Models folder (`src/Flowline/Commands/GenerateCommand.cs:205-207`) also gates on
`ComputeAsync(...).TotalFiles > 0` — but a Models folder's plain `.cs` files never match any of
`ParseComponentPath`'s Dataverse-shaped branches (`Entities/`, `Workflows/`, etc.), so that warning
was **complete dead code**: it could never fire, for any file, ever. Not separately verified live
(no live repro needed — the code path is unconditionally unreachable), but confirmed by inspection.

`DeployCommand`'s equivalent dirty-check (`ValidateGitCleanAsync`,
`src/Flowline/Commands/DeployCommand.cs:371-383`) does **not** share this bug — it calls
`GitUtils.GetUncommittedChangesInPathAsync`, a raw `git status --porcelain` with no component
parsing, so `deploy`'s dirty-tree rejection is unaffected.

## Fix applied

`SolutionChangeSummary.ComputeAsync`'s per-file loop now increments `fileCount` (and the
added/removed line counts) for every git-reported changed path under `srcFolder`, before the
`ParseComponentPath` call — the parse result now only gates whether a file contributes to a
displayable `ChangeItem`/`ChangeGroup`, not whether it counts toward `TotalFiles` at all. This fixes
both call sites at once: `SyncCommand`'s dirty-tree gate and `GenerateCommand`'s Models dirty-tree
warning.

Regression test: `ComputeAsync_WithOnlyUnparseableFileChanged_ReturnsNonZeroTotalFilesButNoGroups`
in `tests/Flowline.Tests/SolutionChangeSummaryTests.cs` — a lone `Other/Customizations.xml` change
now yields `TotalFiles == 1` with an empty `Groups` list. Full suite green after the fix
(`dotnet test Flowline.slnx`).

## Live re-verification (post-fix)

Re-ran the exact repro against the rebuilt/reinstalled CLI: dirtying
`Solution/src/Other/Customizations.xml` and running `flowline sync` now correctly throws
`DirtyWorkingDirectory` before any version bump or Dataverse call, naming `Solution/src` in plain
text (no raw Spectre markup).
