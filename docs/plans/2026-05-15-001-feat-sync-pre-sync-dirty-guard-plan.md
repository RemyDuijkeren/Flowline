---
title: "feat: Add pre-sync dirty-tree guard to SyncCommand"
type: feat
status: active
date: 2026-05-15
origin: docs/brainstorms/2026-05-15-sync-pre-sync-guard-requirements.md
---

# feat: Add pre-sync dirty-tree guard to SyncCommand

## Summary

Extends `GitUtils` with a path-scoped dirty-check method, then wires it into `SyncCommand` as a hard-abort guard scoped to `solutions/<Name>/src/` — the exact directory `pac solution sync` overwrites. A new `--force` flag bypasses the guard. Two implementation units: the helper first, the command wiring second.

---

## Problem Frame

`pac solution sync` silently overwrites unpacked XML in `solutions/<Name>/src/` with no warning. Developers who have half-edited a form component and then run sync to pick up a colleague's change lose that work with no recovery path other than `git diff` — if they notice. `DeployCommand` already enforces a repo-clean gate via `GitUtils.AssertRepoCleanAsync`; `SyncCommand` has none.

See origin document for full problem frame and acceptance examples.

---

## Requirements

- R1. Before `pac solution sync` is invoked, run `git status --porcelain` scoped to `solutions/<Name>/src/`.
- R2. If any changes are found — modified, staged, deleted, or untracked — abort with exit code 1 and print the list of affected paths.
- R3. If no changes are found, proceed without any additional output.
- R4. The error message must name each dirty file and instruct the developer to stash or commit first, or pass `--force` to skip.
- R5. Add a `--force` flag to `SyncCommand.Settings`. When set, skip the dirty-tree check entirely.

**Origin flows:** F1 (clean sync), F2 (dirty abort), F3 (--force bypass)
**Origin acceptance examples:** AE1 (modified file aborts), AE2 (untracked file aborts), AE3 (clean proceeds), AE4 (--force bypasses), AE5 (Plugins/WebResources changes do not block)

---

## Scope Boundaries

- No interactive stash prompt — developer stashes manually before re-running.
- No repo-wide dirty check — only `solutions/<Name>/src/` is in scope.
- No auto-stash-and-restore.
- `DeployCommand`'s existing repo-wide `AssertRepoCleanAsync` is unchanged.
- `--force` only bypasses the dirty-tree check; all other sync behaviour is unaffected.

### Deferred to Follow-Up Work

- Sync-before-push gate (ideation idea #8): depends on `.flowline-sync` metadata stamp shipping first; separate plan.

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline/Utils/GitUtils.cs` — existing `IsRepoCleanAsync` runs `git status --porcelain` repo-wide via `Cli.Wrap("git").ExecuteBufferedAsync`. New method follows the same shape but adds a path argument.
- `src/Flowline/Commands/DeployCommand.cs:38` — `GitUtils.AssertRepoCleanAsync` used as a hard abort gate; model for the guard behaviour in SyncCommand.
- `src/Flowline/Commands/SyncCommand.cs:47–55` — `slnFolder` derived from `projectSln.Name` + `RootFolder`; `.cdsproj` existence check at line ~53 is the insertion point's predecessor; `EnsureMapFilePathAsync` at line ~55 is the first write operation (guard must fire before it).
- `src/Flowline/Commands/PushCommand.cs` — example of `[CommandOption]` settings property pattern to follow for `--force`.
- `tests/Flowline.Tests/PushCommandTests.cs` — canonical test structure: xUnit `[Fact]`, FluentAssertions, `IDisposable` temp directory, naming convention `MethodName_WithCondition_ShouldResult`.

### Institutional Learnings

- No directly applicable `docs/solutions/` entries found for git-status integration tests.

### External References

- None required — approach is fully grounded in existing CliWrap and Spectre.Console.Cli patterns.

---

## Key Technical Decisions

- **New `GitUtils` method, not inline in `SyncCommand`:** all git operations live in `GitUtils`; consistency matters more than brevity here. A shared helper also enables the future sync-before-push gate (ideation idea #8) without duplication.
- **Returns file paths, not a boolean:** the guard needs to print which files are dirty. Returning a list avoids a second `git status` call just to enumerate them.
- **Empty list = clean on exception:** `IsRepoCleanAsync` returns `false` (blocks) on exception; this method deliberately diverges and returns empty (passes) — git is already validated upstream by `EnsureGitAsync`, so exceptions here are practically unreachable and blocking the developer would be unnecessary friction.
- **`--force` skips the check entirely:** no partial bypass. Matches the mental model of `--force` everywhere else in CLIs.

---

## Open Questions

### Resolved During Planning

- **GitUtils helper vs. inline:** new `GitUtils` method chosen for consistency and reuse (see Key Technical Decisions).
- **Insertion point:** after `.cdsproj` existence check (~line 53, which establishes `slnFolder`) and before `EnsureMapFilePathAsync` (~line 55, the first write). This is the earliest point where `srcPath` is knowable and no mutation has occurred.

### Deferred to Implementation

- Exact error message wording: follow `docs/tone-of-voice.md`; the AE previews in the origin doc are directional.
- Whether `git status --porcelain -- <path>` (with `--` separator) is necessary vs. `git status --porcelain <path>` — verify during implementation (the `--` prevents git from interpreting the path as a revision when the path starts with `-`; it is best practice).

---

## Implementation Units

### U1. Path-scoped dirty-check helper in GitUtils

**Goal:** Add a method to `GitUtils` that runs `git status --porcelain` scoped to a given path and returns the list of dirty file paths. Empty list means clean.

**Requirements:** R1, R2, R3 (provides the mechanism all three depend on)

**Dependencies:** None

**Files:**
- Modify: `src/Flowline/Utils/GitUtils.cs`
- Test: `tests/Flowline.Tests/GitUtilsTests.cs` (new file)

**Approach:**
- New `public static async Task<IReadOnlyList<string>> HasUncommittedChangesInPathAsync(string path, CancellationToken cancellationToken = default)` method.
- Runs `git status --porcelain -- <path>` via `Cli.Wrap("git").ExecuteBufferedAsync`.
- Parses `StandardOutput` line by line: each non-empty line is a dirty-file entry (two-character status + space + path); strip the status prefix to get the file path.
- On exception: return empty list (guard passes rather than blocking — note: `IsRepoCleanAsync` returns `false` on exception, which blocks; this method deliberately diverges and favours unblocked developer flow since git is already validated upstream by `EnsureGitAsync`).

**Patterns to follow:**
- `src/Flowline/Utils/GitUtils.cs` — `IsRepoCleanAsync` shape: `Cli.Wrap("git")`, `.ExecuteBufferedAsync`, try/catch returning safe default.

**Test scenarios:**

Tests require a temp git repo. Create a temp directory, `git init`, `git config user.email/name`, create an initial commit, then exercise the method.

- Happy path: `git init` → clean repo → `HasUncommittedChangesInPathAsync(repoPath)` returns empty list. **Covers AE3.**
- Happy path: create and stage a file → call with containing folder → returns that file's path in the list. **Covers AE1.**
- Edge case (untracked file): create a file but do not stage → call with containing folder → untracked file appears in the list. **Covers AE2.**
- Edge case (path scoping): create a dirty file in `Plugins/`; call with `src/` path → returns empty list. **Covers AE5.**
- Edge case (deleted file): delete a tracked file without staging the deletion → call with folder → deleted file appears.
- Error path: call with a non-existent path → returns empty list without throwing.

**Verification:**
- All test scenarios pass.
- `GitUtils.cs` has no regressions in existing methods.

---

### U2. `--force` flag and guard wiring in SyncCommand

**Goal:** Add `--force` to `SyncCommand.Settings` and insert the guard call that aborts with a file-list error when dirty src files are found.

**Requirements:** R1, R2, R3, R4, R5

**Dependencies:** U1

**Files:**
- Modify: `src/Flowline/Commands/SyncCommand.cs`
- Test: `tests/Flowline.Tests/SyncCommandTests.cs` (new file)

**Approach:**
- Add `[CommandOption("--force")]` property (`bool Force`, default `false`) to `SyncCommand.Settings`. Description wording per tone-of-voice guide.
- In `ExecuteFlowlineAsync`, derive `srcPath = Path.Combine(slnFolder, "src")` immediately after the `.cdsproj` existence check (post line ~53) and before `EnsureMapFilePathAsync` (line ~55).
- If `!settings.Force`: call `GitUtils.HasUncommittedChangesInPathAsync(srcPath, cancellationToken)`.
- If the result is non-empty: call `Console.Error(...)` with a message listing the files, then `return 1`.
- If empty or `--force` is set: fall through to existing sync flow unchanged.

**Patterns to follow:**
- `src/Flowline/Commands/PushCommand.cs` — `[CommandOption]` settings property shape.
- `src/Flowline/Commands/DeployCommand.cs:38` — hard-abort pattern before a destructive operation.
- `docs/tone-of-voice.md` — error message wording.

**Test scenarios:**

Settings unit tests (no git required):

- Happy path: `new SyncCommand.Settings()` has `Force == false` by default.
- Happy path: `new SyncCommand.Settings { Force = true }` has `Force == true`.

Integration scenario (manual verification — full `SyncCommand` execution requires pac CLI and env auth not available in unit tests):

- AE4 (manual): given a dirty `solutions/<Name>/src/` file, `flowline sync --force` proceeds without abort. **Covers AE4.**

**Verification:**
- Settings tests pass.
- `flowline sync` with a clean `src/` folder runs without new output (existing behaviour preserved).
- `flowline sync` with a modified tracked file in `src/` prints the dirty-file error and exits 1.
- `flowline sync --force` with dirty `src/` skips the guard and proceeds.

---

## System-Wide Impact

- **Unchanged invariants:** `DeployCommand.AssertRepoCleanAsync` (repo-wide) is untouched. `SyncCommand`'s existing pac call, build step, and git commit nudge are unaffected.
- **Error propagation:** guard exits with code 1 before any write operations occur — `EnsureMapFilePathAsync`, pac, and dotnet build do not run on a dirty abort.
- **API surface parity:** `--force` is a new CLI flag; document in `--help` output automatically via Spectre.Console.Cli `[Description]`.
- **Integration coverage:** AE5 (Plugins/WebResources dirty, src clean → guard passes) is covered in U1 tests via the path-scoping scenario.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| Test temp-git-repo setup is flaky on Windows (path separators, git config) | Mirror the `PushCommandTests` `IDisposable` pattern; use `Path.Combine`; set `user.email`/`user.name` in git config before first commit to avoid "Author identity unknown" errors |
| `git status --porcelain <path>` output format includes two-char status + space + filename — parsing must handle both staged (`M `) and untracked (`?? `) prefixes | Parse by stripping the first three characters of each line; add test scenarios for each prefix type |
| `slnFolder` may not end with a path separator on some OS — `Path.Combine(slnFolder, "src")` handles this correctly | Use `Path.Combine` throughout, never string concatenation |

---

## Sources & References

- **Origin document:** [docs/brainstorms/2026-05-15-sync-pre-sync-guard-requirements.md](docs/brainstorms/2026-05-15-sync-pre-sync-guard-requirements.md)
- `src/Flowline/Utils/GitUtils.cs` — `IsRepoCleanAsync` pattern
- `src/Flowline/Commands/DeployCommand.cs` — `AssertRepoCleanAsync` hard-gate pattern
- `src/Flowline/Commands/SyncCommand.cs` — insertion point context
- `tests/Flowline.Tests/PushCommandTests.cs` — canonical test structure