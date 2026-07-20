---
title: "feat: Solution versioning — auto patch bump and git tagging on sync"
type: feat
status: completed
date: 2026-05-21
origin: docs/brainstorms/solution-versioning-requirements.md
---

# feat: Solution versioning — auto patch bump and git tagging on sync

> **2026-07-20 — the git-tagging half of this plan is dropped. R4, R5 and Phase 2's `--no-tag`
> flag will not be built, and `GitUtils.CreateTagAsync` has been deleted.**
>
> Tagging at HEAD only makes sense against committed code, and a sync leaves the working tree
> dirty by design. Making the tag meaningful would mean Flowline committing on the user's behalf,
> which was rejected. That settled a broader rule: **Flowline performs no git write operations —
> commits, tags and branches are the user's to make.** Flowline reads git state (branch, remote,
> uncommitted paths) and nothing more.
>
> The version-bump half (R1–R3) is unaffected and shipped.

## Summary

After all existing sync steps succeed, `SyncCommand` reads the current Dataverse solution version via `pac solution online-version`, increments the patch component (or major/minor via `--bump`), writes the new version back to Dataverse, and creates an immutable bare-version git tag at HEAD (e.g., `1.0.6`). `--no-tag` suppresses tagging for a run. MinVer in `Plugins.csproj` is already scaffolded and needs no changes — bare version tags are its default.

---

## Problem Frame

`pac solution sync` does not auto-increment the solution version the way a UI export does. Developers have no audit trail of which Dataverse state was captured when, and the plugin assembly version is unrelated to the solution version. See origin document for full context.

---

## Requirements

- R1. After successful sync, SyncCommand reads current solution version from Dataverse via `pac solution online-version`.
- R2. SyncCommand increments the patch component by default and writes the new version back to Dataverse via `pac solution online-version`.
- R3. `--bump [major|minor|patch]` flag controls which component is incremented (default: patch). Bumping major/minor resets lower components to zero.
- R4. SyncCommand creates an immutable git tag at HEAD after bumping. `--no-tag` suppresses tag creation.
- R5. Tag format is always `{major}.{minor}.{patch}` — bare version, no prefix, no fourth component.
- R6. MinVer is scaffolded into `Plugins.csproj` by CloneCommand (already done).
- R7. Plugin AssemblyVersion derived from git tags via MinVer — no hardcoded version in `.csproj` (already done).
- R8. DeployCommand does not create or modify git tags (already true — no change).

**Origin flows:** F1 (Build → push → sync cycle), F2 (Deploy to test or prod)
**Origin acceptance examples:** AE1 (covers R1, R2, R3), AE2 (covers R4, R5), AE2b (covers R3), AE3 (covers R6, R7), AE4 (covers R2, R7)

---

## Scope Boundaries

- No moving env tags (`prod-current`, `test-current`).
- No CI/CD pipeline wiring.
- No changelog or release notes generation.
- Major and minor bumps are always manual — Flowline never auto-selects them.
- No environment tracking in `.flowline` config.
- DeployCommand unchanged.
- R6, R7, R8 are already satisfied — no implementation needed.

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline/Commands/SyncCommand.cs` — insertion point for version bump + tag: after `summary.WriteTree(...)` (line 127), before `Console.Done(...)` (line 129). All existing steps (pac sync → pack → dotnet build → drift check → summary) run first.
- `src/Flowline/Utils/PacUtils.cs` — `GetBestPacCommandAsync()` + `ExecuteBufferedAsync` + `WithValidation(CommandResultValidation.None)` pattern for all PAC calls. `SolutionInfo.VersionNumber` exists but comes from `pac solution list`, not `pac solution online-version`. New methods follow `GetPublisherCustomizationPrefixAsync` as the closest pattern (tabular PAC output parsing).
- `src/Flowline/Utils/GitUtils.cs` — `GetUncommittedChangesInPathAsync` pattern: CliWrap with optional `WithWorkingDirectory`, `ExecuteBufferedAsync`, `WithValidation(CommandResultValidation.None)`. No LibGit2Sharp.
- `tests/Flowline.Tests/PacUtilsTests.cs` — `CheckCommandExistsFunc` injection for mocking PAC CLI. xUnit + FluentAssertions.
- `tests/Flowline.Tests/GitUtilsTests.cs` — real temp git repos with `RunGit` helper. Pattern to follow for `CreateTagAsync` tests.
- `src/Flowline/Commands/CloneCommand.cs:297-306` — `dotnet add package MinVer` already called in `SetupPluginsProjectAsync`. No changes needed.

### Institutional Learnings

- No `docs/solutions/` entries directly relevant.

---

## Key Technical Decisions

- **Version bump after all sync steps:** Version and tag only update when pack, build, and drift check all pass. A failed sync doesn't change any version metadata. If sync succeeds but version bump fails (PAC error, git error), the sync applied — user re-runs. Sync is idempotent; version bump is not automatically retried.
- **`pac solution online-version` for read and write:** Single PAC CLI command handles both operations. `pac solution version` (local XML) is not used — `pac solution sync` downloads the updated version naturally.
- **Version bump as internal static pure function:** `BumpVersion(string version, string component)` extracted as `internal static` in `SyncCommand` — testable via `InternalsVisibleTo` without PAC CLI or git. Same pattern as the `FindProblematicSolutions` extraction in `ProvisionCommand`.
- **Dataverse version is 4-part; "patch" = 3rd component:** Dataverse uses `major.minor.build.revision`. The "patch" in requirements maps to the 3rd component (build). On any bump, the 4th component (revision) always resets to 0. So `1.0.0.1` → bump patch → `1.0.1.0`. Git tag uses the first 3 parts: `1.0.1`. Write back in 4-part form.
- **`--bump` as string, not enum:** Keeps the Spectre.Console.Cli option declaration minimal. Validation against `major|minor|patch` is done in the execute method. Default is `"patch"` when the option is absent.
- **`CreateTagAsync` throws `FlowlineException` on duplicate tag:** Tag already exists = version conflict. Error message should suggest `--no-tag` or manual removal.

---

## Open Questions

### Resolved During Planning

- **Tag at HEAD (pre-commit) acceptable?** Yes. `--no-tag` suppresses for runs where this is not wanted. (see origin: docs/brainstorms/solution-versioning-requirements.md)
- **`pac solution online-version` for both read and write?** Yes — single command, both operations. (see origin)
- **Tag prefix?** None. Bare version tags, one-solution-per-repo recommendation, shared namespace. (see origin)
- **R6, R7, R8 need implementation?** No — MinVer already scaffolded, DeployCommand already unchanged.

- **`pac solution online-version` output format confirmed:** Line-based text output. Version is on the `Solution Version: ` line. Example full output:
  ```
  Connected as remy@automatevalue.com
  Connected to... AutomateValue Dev

  Listing all Solutions from the current Dataverse organization...
  Unique Name: Cr07982
  Solution Display Name: AV Default Solution
  Solution Version: 1.0.0.1
  ```
  Parse: find the line starting with `Solution Version:`, split on `: `, take the trimmed right-hand side.
- **Dataverse version format is 4-part (`major.minor.build.revision`):** "Patch" in requirements maps to the 3rd component (build). On any bump, the 4th component (revision) resets to 0. Examples: `1.0.0.1` → bump patch → `1.0.1.0`; `1.0.0.1` → bump minor → `1.1.0.0`. Git tag uses 3-part only: `1.0.1`.
- **`--environment` flag confirmed:** `pac solution online-version` accepts `--environment <url>`. Always pass it explicitly — consistent with all other PAC calls in PacUtils and avoids dependence on active PAC auth context.
- **Minimum PAC CLI version for `pac solution online-version`:** Verify the command is available in the PAC CLI versions developers are likely using. Surface a clear error if not.

---

## Implementation Units

### U1. PacUtils — Solution version read/write

**Goal:** Add `GetSolutionVersionAsync` and `SetSolutionVersionAsync` using `pac solution online-version`.

**Requirements:** R1, R2

**Dependencies:** None

**Files:**
- Modify: `src/Flowline/Utils/PacUtils.cs`
- Test: `tests/Flowline.Tests/PacUtilsTests.cs`

**Approach:**
- `GetSolutionVersionAsync(string solutionName, string environmentUrl, CancellationToken)` → calls `pac solution online-version --solution-name <name> --environment <url>`, parses `Solution Version:` line, returns version string
- `SetSolutionVersionAsync(string solutionName, string version, string environmentUrl, CancellationToken)` → calls `pac solution online-version --solution-name <name> --environment <url> --solution-version <version>`
- Both use `GetBestPacCommandAsync` + `ExecuteBufferedAsync` + `WithValidation(CommandResultValidation.None)` + explicit exit code check, throwing `FlowlineException` on failure
- `ParseVersionFromPacOutput(string output)` → `internal static string?` pure function, testable without PAC CLI

**Patterns to follow:**
- `GetPublisherCustomizationPrefixAsync` in `PacUtils.cs` — tabular PAC output parsing pattern
- `CheckCommandExistsFunc` injection for testability

**Test scenarios:**
- Happy path: `ParseVersionFromPacOutput` with the confirmed output format extracts `"1.0.0.1"` from the `Solution Version: 1.0.0.1` line
- Edge case: multiple lines before the version line (Connected as..., Listing...) — parser skips them correctly
- Edge case: empty or whitespace-only output → returns `null` without throwing
- Error path: PAC CLI non-zero exit code → `FlowlineException` thrown

**Verification:**
- `GetSolutionVersionAsync` returns a non-empty version string on success
- `SetSolutionVersionAsync` exits with 0 on success

---

### U2. GitUtils — Tag creation

**Goal:** Add `CreateTagAsync` to create an immutable local git tag.

**Requirements:** R4, R5

**Dependencies:** None

**Files:**
- Modify: `src/Flowline/Utils/GitUtils.cs`
- Test: `tests/Flowline.Tests/GitUtilsTests.cs`

**Approach:**
- `CreateTagAsync(string tagName, string? workingDirectory, CancellationToken)` → runs `git tag <tagName>` via CliWrap
- Optional `workingDirectory` via `WithWorkingDirectory` — same pattern as `GetUncommittedChangesInPathAsync`
- `WithValidation(CommandResultValidation.None)` + explicit exit code check
- Non-zero exit → `FlowlineException` with message including tag name and hint about `--no-tag`

**Patterns to follow:**
- `GetUncommittedChangesInPathAsync` in `GitUtils.cs` — CliWrap with optional working directory
- `GitUtilsTests` temp repo + `RunGit` helper pattern

**Test scenarios:**
- Happy path: `CreateTagAsync("1.0.6", root, ct)` → `git tag` lists `1.0.6` in the temp repo
- Error path: duplicate tag (same name at same commit) → `FlowlineException` thrown
- Edge case: `workingDirectory = null` → uses process working directory without error

**Verification:**
- After `CreateTagAsync("1.0.6", root, ct)`, `git tag` in that repo lists `1.0.6`

---

### U3. SyncCommand — Version bump flags and wiring

**Goal:** Add `--bump` and `--no-tag` flags; run version bump + tag after all existing sync steps succeed.

**Requirements:** R1, R2, R3, R4, R5

**Dependencies:** U1, U2

**Files:**
- Modify: `src/Flowline/Commands/SyncCommand.cs`
- Test: `tests/Flowline.Tests/SyncCommandTests.cs`

**Approach:**
- Add to `Settings`:
  - `[CommandOption("--bump")]` → `string? Bump` (null = patch default; validated to `major`, `minor`, or `patch`)
  - `[CommandOption("--no-tag")]` → `bool NoTag`
- After `summary.WriteTree(...)` and before `Console.Done(...)`:
  1. `PacUtils.GetSolutionVersionAsync(slnInfo.SolutionUniqueName!, devEnv.EnvironmentUrl!, ct)` → `currentVersion`
  2. `BumpVersion(currentVersion, settings.Bump ?? "patch")` → `newVersion`
  3. `PacUtils.SetSolutionVersionAsync(slnInfo.SolutionUniqueName!, newVersion, devEnv.EnvironmentUrl!, ct)`
  4. Unless `settings.NoTag`: `GitUtils.CreateTagAsync(ToTagVersion(newVersion), RootFolder, ct)`
  5. Update `Console.Done` to include new version and tag
- `BumpVersion(string version, string component)` → `internal static string` pure function in `SyncCommand`
- `ToTagVersion(string version)` → `internal static string`: strips 4th `.0` component if present; returns 3-part bare version

**Patterns to follow:**
- `SyncCommand.Settings` — existing flag declarations (`[CommandOption]`, `[Description]`)
- `BumpVersion` / `ToTagVersion` follow `FindProblematicSolutions` in `ProvisionCommand` — internal static testable method

**Test scenarios:**
- Happy path: `BumpVersion("1.0.0.1", "patch")` → `"1.0.1.0"` (3rd component incremented, 4th reset to 0)
- Happy path: `BumpVersion("1.2.5.3", "minor")` → `"1.3.0.0"` (2nd incremented, 3rd+4th reset)
- Happy path: `BumpVersion("1.2.5.3", "major")` → `"2.0.0.0"` (1st incremented, rest reset)
- Happy path: `ToTagVersion("1.0.1.0")` → `"1.0.1"` (3-part bare, 4th dropped)
- Error path: `--bump xyz` → validation error, exit non-zero before sync starts

**Verification:**
- After a successful sync with `--bump patch`: Dataverse solution version incremented by one patch, git tag matching 3-part version exists at HEAD
- `--no-tag` flag: version bumped in Dataverse, no git tag created
- `--bump minor` on `1.2.5`: Dataverse version becomes `1.3.0`, tag `1.3.0` created

---

### U4. CloneCommand — MinVer scaffolding verification

**Goal:** Confirm existing MinVer scaffolding is correct for bare version tags; no changes expected.

**Requirements:** R6, R7

**Dependencies:** None

**Files:**
- Verify (no change expected): `src/Flowline/Commands/CloneCommand.cs`

**Approach:**
- Read `SetupPluginsProjectAsync` in `CloneCommand.cs` to confirm:
  1. `dotnet add package MinVer` is called (confirmed: lines 297-306)
  2. No `<MinVerTagPrefix>` or `<MinVerVersionOverride>` is injected into the generated `.csproj`
  3. No prefix configuration in any scaffolded file that would conflict with bare `1.0.0` tags
- If all three hold: no action. If a prefix is found: remove it.

**Verification:**
- `SetupPluginsProjectAsync` adds MinVer package and injects no tag prefix configuration

---

## System-Wide Impact

- **Interaction graph:** Two new PAC CLI calls (online-version read + write) and one git call (tag) append to the SyncCommand flow. No existing observers, hooks, or callbacks touched.
- **Error propagation:** `FlowlineException` from version bump or tag creation exits non-zero. The Dataverse sync already applied — re-running sync is safe (idempotent). If the version write succeeds but git tag fails, Dataverse holds the new version but the repo has no tag; user can manually tag or re-run with `--no-tag`.
- **State lifecycle risks:** Partial failure (sync OK, version bump fails) leaves Dataverse and git in a consistent pre-bump state. Partial failure (version bumped, tag fails) leaves a version mismatch between Dataverse and git tags — surfaced clearly in the error message.
- **API surface parity:** `--bump` and `--no-tag` are CLI-only flags; no agent tool surface change.
- **Integration coverage:** End-to-end verification (PAC online-version read + write + git tag in sequence) requires a real Dataverse environment. Unit tests cover version parsing and bump logic. GitUtils tests cover tag creation with real temp repos.
- **Unchanged invariants:** All existing `SyncCommand` flags and steps are unchanged. `DeployCommand` is not touched. `CloneCommand` MinVer scaffolding is not changed.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| Partial failure: sync OK, tag creation fails (tag exists from prior run) | Surface `FlowlineException` with message suggesting `git tag -d <version>` or `--no-tag`; do not silently skip |
| `pac solution online-version` unavailable in older PAC CLI versions | Verify minimum version; surface clear error if version too old |

---

## Sources & References

- **Origin document:** [docs/brainstorms/solution-versioning-requirements.md](docs/brainstorms/solution-versioning-requirements.md)
- Related code: `src/Flowline/Commands/SyncCommand.cs`, `src/Flowline/Utils/PacUtils.cs`, `src/Flowline/Utils/GitUtils.cs`, `src/Flowline/Commands/CloneCommand.cs`
- Related tests: `tests/Flowline.Tests/PacUtilsTests.cs`, `tests/Flowline.Tests/GitUtilsTests.cs`, `tests/Flowline.Tests/SyncCommandTests.cs`
- MinVer tag prefix docs: https://github.com/adamralph/minver#can-i-prefix-my-tag-names
