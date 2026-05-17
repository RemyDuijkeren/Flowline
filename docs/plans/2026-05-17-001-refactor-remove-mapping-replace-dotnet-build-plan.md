---
date: 2026-05-17
title: "refactor: Remove mapping, replace dotnet build with pac solution pack"
type: refactor
status: active
origin: docs/brainstorms/2026-05-16-build-validation-and-pac-warnings-requirements.md
---

# refactor: Remove mapping, replace dotnet build with pac solution pack

## Summary

Remove all mapping infrastructure from Flowline, replace `dotnet build` in clone and deploy with `pac solution pack --folder src/`, and add a non-blocking post-sync drift check comparing `src/` against local build artifacts.

Correct ALM chain after this change: `source → push → Dataverse DEV → sync → src/ → pack → deploy`. `src/` is the record of what was confirmed in DEV; deploy packs that exactly. (see origin: `docs/brainstorms/2026-05-16-build-validation-and-pac-warnings-requirements.md`)

---

## Problem Frame

Flowline's mapping system (`MappingPac.xml`, `MappingBuild.xml`, `UseMapping`) created three cascading problems:

1. **"Solution may not repack" warnings during sync** — when web resources are added directly in Dataverse, PAC skips them from `src/` and expects them at the mapped path, which doesn't exist.
2. **dotnet build failure in clone** — `BuildSolutionAsync` uses Debug configuration, but `MappingBuild.xml` maps plugin DLLs to `Release/`, so the build always fails when no Release binary exists.
3. **Correctness risk in deploy** — mapping during pack redirects from `src/` to `dist/` and `Plugins/bin/Release/`, meaning a deploy could ship locally modified artifacts that were never pushed to or confirmed in DEV.

The fix is simple: remove mapping everywhere, pack from `src/` (which sync populates), and validate packability at clone and deploy time using `pac solution pack` instead of `dotnet build`.

---

## Requirements Trace

| Unit | Requirements |
|---|---|
| U1 | R1, R5 (sync), R7 |
| U2 | R2, R3, R4, R5 (clone) |
| U3 | R6 |
| U4 | R12 |
| U5 | R13, R14, R15 |
| U6 | R8, R9, R10, R11 |

---

## Key Technical Decisions

**Remove `UseMapping` field from `ProjectSolution` config entirely.** The field has no purpose without mapping. Existing `.flowline` configs with `"UseMapping": true` will still deserialize cleanly — the JSON deserializer ignores unknown fields. No migration needed.

**Remove `--no-map` flag from `CloneCommand.Settings`.** With mapping gone, the flag is meaningless. Any user relying on it is already getting the behavior they want (no mapping). Breaking change is acceptable — the flag had no visible effect in the ALM workflow.

**`pac solution pack --folder src/ --zipFile bin/<Name>_unmanaged.zip`.** Output path convention: `solutions/<Name>/bin/<Name>_unmanaged.zip` (or `_managed.zip`). The `bin/` subfolder should be in `.gitignore`. Zip produced before each deploy, never cached from a previous run.

**Drift check at threshold: 10 KB for plugin DLLs.** Minor build metadata variation (timestamps, embedded GUIDs) adds well under 1 KB. A real change typically shifts the DLL by several KB or more. 10 KB is a conservative initial threshold; adjust after testing real builds (see deferred implementation notes).

**`EnsureMapFilePathAsync` removed, not renamed.** It modified `.cdsproj` to add/remove `SolutionPackageMapFilePath`. With mapping gone, `SolutionPackageMapFilePath` should never appear. Note: existing repos may already have it in their `.cdsproj` — the implementation should strip it as a one-time migration (or document that developers run `flowline clone` again). Deferred to implementation.

**Drift check as a standalone helper, not inline in SyncCommand.** The comparison logic (file hash, directory walk, DLL size) is testable in isolation. Extract to `src/Flowline/Utils/DriftChecker.cs` with a thin caller in `SyncCommand`.

**Remove `dataverseConnector` and `webResourceService` from `CloneCommand` constructor.** Both are used only in `CloneWebResourcesFromDataverseAsync` and `ConnectToDataverseAsync`. Once R6 removes those methods, the DI dependencies are dead.

---

## Scope Boundaries

### In Scope
All requirements R1–R15 from the origin document.

### Deferred to Follow-Up Work
- Strip existing `SolutionPackageMapFilePath` from users' already-cloned `.cdsproj` files (migration). For now, `EnsureMapFilePathAsync` being removed means it won't be added again; existing ones don't break anything (MSBuild looks for `MappingBuild.xml` which no longer exists and fails silently on dotnet build — but that's the developer's concern, not Flowline's).
- Drift check threshold tuning after real DLL build comparison.
- Pack-flow / ISV-style source-driven builds — explicitly out of scope. (see origin: `docs/brainstorms/2026-05-16-build-validation-and-pac-warnings-requirements.md`)

### Non-Goals
- `dotnet build` in any form — the developer's build pipeline, not Flowline's.
- Populating `dist/` during sync or clone.
- Blocking sync on drift — drift check is informational only.

---

## Dependency Graph

```
U1 (SyncCommand mapping removal)
U2 (mapping infrastructure removal) ──┐
U3 (CloneWebResources removal)        ├──► U4 (pac pack in Clone)
                                       │
U5 (pac pack in Deploy)  [independent]
U6 (drift check) ──depends on── U1
```

---

## Implementation Units

### U1. Remove mapping from SyncCommand

**Goal:** Remove `EnsureMapFilePathAsync` call, `--map` flag, and commented-out `dotnet build` block from `SyncCommand`. (R1, R5-sync, R7)

**Requirements:** R1, R5, R7

**Dependencies:** none

**Files:**
- `src/Flowline/Commands/SyncCommand.cs`
- `tests/Flowline.Tests/SyncCommandTests.cs`

**Approach:**
- Delete line 72: `if (await DotNetUtils.EnsureMapFilePathAsync(...) != 0) return 1;`
- Delete lines 87: `.AddIf(projectSln.UseMapping, "--map", Path.Combine(slnFolder, MappingPacFileName))`
- Delete lines 102–107: the entire commented-out `dotnet build` block
- No new logic

**Patterns to follow:** The existing removal of `--map` in the `AddIf` call is a direct deletion.

**Test scenarios:**
- `Settings_Force_ShouldDefaultToFalse` — existing test, verify it still passes
- No new unit tests needed for pure removal; correctness verified by integration (pac sync runs without `--map` flag)

**Verification:** Code compiles. `git diff` on `SyncCommand.cs` shows only deletions. No `EnsureMapFilePathAsync`, `UseMapping`, `MappingPacFileName`, or commented `dotnet build` remain.

---

### U2. Remove mapping infrastructure from CloneCommand and shared code

**Goal:** Remove all mapping-generation logic, the `UseMapping` config field, and the two mapping constants from the shared base class. (R2, R3, R4, R5-clone)

**Requirements:** R2, R3, R4, R5

**Dependencies:** U1 (avoids conflict on `DotNetUtils.cs`)

**Files:**
- `src/Flowline/Commands/CloneCommand.cs`
- `src/Flowline/Commands/FlowlineCommand.cs`
- `src/Flowline/Utils/DotNetUtils.cs`
- `src/Flowline/Config/ProjectConfig.cs`
- `CLAUDE.md`
- `docs/folder-structure.md` (check and update if present)
- `tests/Flowline.Tests/DotNetUtilsTests.cs`
- `tests/Flowline.Tests/ProjectConfigTests.cs`

**Approach:**
- `CloneCommand.cs`:
  - Remove `--no-map` option from `Settings`
  - In `CloneSolutionFromDataverseAsync`: remove the `tempPacMap` block (lines 139–164) and the `if (projectSln.UseMapping)` branch — just call pac solution clone without `--map`
  - Remove `WriteMappingFilesAsync` method (lines 407–426)
  - Remove `PacMappingContent` and `BuildMappingContent` constants (lines 387–405)
  - Remove `EnsureMapFilePathAsync` call (line 102)
  - Remove `useMapping: !settings.NoMap` arg from `GetAndCheckSolutionAsync` calls (line 72)
- `FlowlineCommand.cs`: Delete `MappingPacFileName` and `MappingBuildFileName` constants (lines 18–19)
- `DotNetUtils.cs`: Delete `EnsureMapFilePathAsync` method. Do NOT remove `BuildSolutionAsync` or `DotnetBuild` enum yet — CloneCommand and DeployCommand still call them; those callers are removed in U4 and U5. Clean up dead code in U5.
- `ProjectConfig.cs`: Remove `UseMapping` field from `ProjectSolution` (line 249), and all references: `AddOrUpdateSolution` overloads, `GetOrUpdateSolution`, the `useMapping` parameter chain
- `CLAUDE.md`: Remove `MappingBuild.xml` and `MappingPac.xml` from the folder structure diagram

**Patterns to follow:** `DotNetUtilsTests.cs` uses temp-directory pattern (IDisposable). Tests for `EnsureMapFilePathAsync` become dead — delete them.

**Test scenarios:**
- `DotNetUtilsTests`: Delete all 4 `EnsureMapFilePathAsync_*` tests — method no longer exists
- `ProjectConfigTests`: Any test asserting `UseMapping = true` default — delete or update to remove UseMapping assertions
- `CloneCommand.Settings`: Verify `--no-map` option no longer appears in help output (manual check acceptable)
- `AddOrUpdateSolution` with no useMapping parameter — verify it still compiles and config round-trips correctly

**Verification:** Code compiles. No references to `EnsureMapFilePathAsync`, `UseMapping`, `MappingPac`, `MappingBuild`, `DotnetBuild`, `WriteMappingFilesAsync`, `PacMappingContent`, or `BuildMappingContent` remain in `src/`. CLAUDE.md folder diagram no longer lists mapping files.

---

### U3. Remove CloneWebResourcesFromDataverseAsync from CloneCommand

**Goal:** Remove the Dataverse web resource download step from clone, and the DI dependencies it requires. (R6)

**Requirements:** R6

**Dependencies:** none

**Files:**
- `src/Flowline/Commands/CloneCommand.cs`

**Approach:**
- Remove call to `CloneWebResourcesFromDataverseAsync` (line 106)
- Delete `CloneWebResourcesFromDataverseAsync` private method (lines 338–355)
- Delete `ConnectToDataverseAsync` private method (lines 358–385) — only used by `CloneWebResourcesFromDataverseAsync`
- Remove `dataverseConnector` and `webResourceService` from the constructor — they have no remaining callers after U3
- Remove `using Microsoft.PowerPlatform.Dataverse.Client;` if it becomes unused
- Verify DI registration in `Program.cs` is still needed (other commands may inject these services)

**Patterns to follow:** `webResourceService` and `dataverseConnector` are registered in DI and used by other commands (e.g., PushCommand). Do NOT remove them from `Program.cs` — only remove them from `CloneCommand`'s constructor.

**Test scenarios:**
- `Test expectation: none` — pure deletion; no behavioral logic remains to test. Integration: clone succeeds without needing Dataverse connectivity for web resources.

**Verification:** `CloneCommand` constructor takes only `(IAnsiConsole, FlowlineRuntimeOptions)`. No `dataverseConnector` or `webResourceService` parameters. Code compiles.

---

### U4. Replace dotnet build with pac solution pack validation in CloneCommand

**Goal:** After downloading the solution, validate it can be packed with `pac solution pack --folder src/`. Exit non-zero if pack fails — indicates broken solution structure. (R12)

**Requirements:** R12 — Covers AE2.

**Dependencies:** U2, U3

**Files:**
- `src/Flowline/Commands/CloneCommand.cs`

**Approach:**
- Replace the two `BuildSolutionAsync` calls (lines 109–114) with `pac solution pack --folder src/ --zipFile <temp_zip_path>` using `PacUtils.GetBestPacCommandAsync()`
- `pac solution pack` invocation:
  - `--folder`: `<slnFolder>/src/`
  - `--zipFile`: `<slnFolder>/bin/<Name>_unmanaged.zip` (create `bin/` if needed)
  - For managed: `--packagetype Both` with `--zipFile <slnFolder>/bin/<Name>_managed.zip`
- Exit non-zero if pac returns non-zero — clear error message: `"Pack validation failed — downloaded solution structure may be broken"`
- Keep the zip on disk (useful for first deploy); log its path at verbose level
- Add `bin/` to `.gitignore` for the solution folder if one exists, or document in output

**Technical design (directional):**
```
pac solution pack
  --folder  solutions/<Name>/src/
  --zipFile solutions/<Name>/bin/<Name>_unmanaged.zip
```
For `IncludeManaged`:
```
pac solution pack
  --folder  solutions/<Name>/src/
  --zipFile solutions/<Name>/bin/<Name>_managed.zip
  --packagetype Both
```
*Directional guidance only — not implementation specification.*

**Patterns to follow:** Mirror the existing `pac solution sync` call style in `CloneSolutionFromDataverseAsync` — use `PacUtils.GetBestPacCommandAsync`, `Cli.Wrap`, `.WithValidation(CommandResultValidation.None)`, spinner. Message should follow tone-of-voice guide.

**Test scenarios:**
- `Test expectation: none` — `pac solution pack` requires a real PAC install and solution folder; integration-level. Verify manually: fresh clone on a machine with no `dist/` and no compiled plugin binaries succeeds (AE2).

**Verification:** Clone exits 0 when pac pack succeeds. Clone exits 1 with clear error when pac pack fails. No `dotnet build` call remains in `CloneCommand.cs`.

---

### U5. Replace lazy dotnet build with unconditional pac solution pack in DeployCommand

**Goal:** Before every deploy, pack `src/` with `pac solution pack`. No caching, no lazy build, no dependency on `dist/` or compiled binaries. (R13, R14, R15 — Covers AE3, AE4.)

**Requirements:** R13, R14, R15

**Dependencies:** none (independent of other units, but benefits from seeing U4's pack invocation pattern)

**Files:**
- `src/Flowline/Commands/DeployCommand.cs`

**Approach:**
- Remove the `if (!File.Exists(packagePath))` lazy-build block (lines 107–132)
- Remove the `buildType` and `packagePath` variables that pointed at `bin/Debug/<Name>.zip`
- Add unconditional `pac solution pack --folder <slnFolder>/src/ --zipFile <packagePath>` before the import step
- Define new `packagePath` as `<slnFolder>/bin/<Name>_unmanaged.zip` (or `_managed.zip`)
- Fail fast with exit code 1 and clear error if pack returns non-zero: `"Pack failed — check src/ is populated (run 'flowline sync' first)"`
- Pass `packagePath` to the existing `pac solution import --path <packagePath>` call (unchanged)

**Technical design (directional):**
```
// before import:
pac solution pack
  --folder  solutions/<Name>/src/
  --zipFile solutions/<Name>/bin/<Name>_unmanaged.zip

// then (unchanged):
pac solution import
  --path    solutions/<Name>/bin/<Name>_unmanaged.zip
  --environment <targetUrl>
  --async
```
*Directional guidance only.*

**Patterns to follow:** Mirror `CloneSolutionFromDataverseAsync` style for the pac wrap. `DeployCommand` does not inherit `FlowlineCommand<T>` — use `PacUtils.GetBestPacCommandAsync` directly (already imported in DeployCommand). See the existing `pac solution import` block for the CLI pattern to follow.

**Test scenarios:**
- `Test expectation: none` for integration (requires PAC + live env). Manual verification:
  - AE3: Deploy immediately after sync (no `dist/`, no `Plugins/bin/`) → pack succeeds from `src/`, import proceeds
  - AE4: Prior zip already on disk → `pac solution pack` runs anyway, overwrites it, import uses fresh zip

**Cleanup:** After removing the `dotnet build` call here and in U4 (CloneCommand), `DotNetUtils.BuildSolutionAsync` and the `DotnetBuild` enum have no callers. Remove them from `src/Flowline/Utils/DotNetUtils.cs` as part of this unit.

**Verification:** `DeployCommand.cs` contains no `dotnet build` call. Pack runs unconditionally. Exit code 1 with message if pack fails. Existing import logic unchanged. `DotNetUtils.cs` contains no `BuildSolutionAsync` or `DotnetBuild`.

---

### U6. Add post-sync drift check to SyncCommand

**Goal:** After sync, if local build artifacts exist, compare them against what sync downloaded into `src/`. Emit non-blocking warnings with action hints for any drift found. (R8, R9, R10, R11 — Covers AE5, AE6.)

**Requirements:** R8, R9, R10, R11

**Dependencies:** U1

**Files:**
- `src/Flowline/Commands/SyncCommand.cs`
- `src/Flowline/Utils/DriftChecker.cs` (new)
- `tests/Flowline.Tests/DriftCheckerTests.cs` (new)

**Approach:**

*`DriftChecker.cs` (new helper):*
- Static or instance class with one entry point: `CheckAsync(slnFolder, cancellationToken)` returning a list of `DriftWarning` records
- Web resource check (R8): if `<slnFolder>/WebResources/dist/` exists and has files, walk `<slnFolder>/src/WebResources/` recursively; for each file compute SHA-256 hash; compare against same relative path in `dist/`; categorize as: `ContentDiffers`, `NewInDataverse`, `OnlyLocal`
- Plugin check (R9): if `<slnFolder>/Plugins/bin/Release/` exists, find DLLs matching the name of any file in `<slnFolder>/src/PluginAssemblies/`; compare file sizes; if difference exceeds threshold (start: 10 KB), emit `PluginSizeMismatch` warning
- R11: if neither `dist/` nor `Plugins/bin/Release/` exists, return empty list (no warnings)

*`SyncCommand.cs`:*
- After the existing `SolutionChangeSummary.ComputeAsync` block, call `DriftChecker.CheckAsync(slnFolder, cancellationToken)`
- For each warning, call `Console.Warning(...)` with the action hint:
  - `ContentDiffers`: "Check who changed `{file}` in Dataverse"
  - `NewInDataverse`: "Check who changed `{file}` in Dataverse — run 'flowline push' to re-sync"
  - `OnlyLocal`: "Local change not in Dataverse — run 'flowline push'"
  - `PluginSizeMismatch`: "Local plugin build may differ from what is deployed — rebuild and push if intentional"
- All warnings after the success line, before the final rocket emoji line; sync always returns 0

**Technical design (directional):**
```csharp
// DriftWarning record (directional):
record DriftWarning(DriftCategory Category, string RelativePath);
enum DriftCategory { ContentDiffers, NewInDataverse, OnlyLocal, PluginSizeMismatch }
```
*Directional guidance only.*

**Patterns to follow:**
- Hash computation: `SHA256.HashData(File.ReadAllBytes(path))` — same approach as web resource push content detection
- Directory walk: `Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)` — same as web resource push
- `Console.Warning(...)` — match tone-of-voice guide
- Wrap drift check in `try/catch` like the existing `SolutionChangeSummary` block — drift failure must never block sync

**Test scenarios (for `DriftCheckerTests.cs`, using temp-directory fixture like `DotNetUtilsTests`):**
- Neither `dist/` nor `Plugins/bin/Release/` exists → returns empty list (covers R11, AE6)
- `dist/` exists but is empty → returns empty list
- File identical in `src/WebResources/` and `dist/` → no warning for that file
- File in both with different content → `ContentDiffers` warning for that file (covers AE5)
- File in `src/WebResources/` but not in `dist/` → `NewInDataverse` warning
- File in `dist/` but not in `src/WebResources/` → `OnlyLocal` warning
- Multiple files with mixed states → correct category per file
- `Plugins/bin/Release/` exists, DLL within 10 KB of `src/PluginAssemblies/` counterpart → no warning
- `Plugins/bin/Release/` exists, DLL size differs by > 10 KB → `PluginSizeMismatch` warning
- `Plugins/bin/Release/` exists, no matching DLL name in `src/PluginAssemblies/` → no warning
- `DriftChecker.CheckAsync` throws internally → exception propagates (caller wraps in try/catch)

**Verification:** `DriftCheckerTests` pass. Sync exits 0 regardless of warnings found. No warnings when neither `dist/` nor `Plugins/bin/Release/` exists. Warnings include file name and action hint.

---

## Deferred Implementation Notes

- **`SolutionPackageMapFilePath` in existing `.cdsproj` files**: After removing `EnsureMapFilePathAsync`, any user repo that was cloned before this change may still have `<SolutionPackageMapFilePath>$(MSBuildProjectDirectory)\MappingBuild.xml</SolutionPackageMapFilePath>` in their `.cdsproj`. This causes dotnet build (run by the developer) to warn about a missing `MappingBuild.xml`. Defer to a follow-up: either add a migration step to strip it, or document that developers should remove it manually.
- **Plugin DLL size threshold (10 KB)**: Initial value. Validate against actual sample builds before declaring final. Adjust if real metadata variation exceeds 10 KB or if real functional drift goes undetected.
- **Zip output path**: Confirm `solutions/<Name>/bin/` is in `.gitignore` or add it. Otherwise every deploy produces a committed artifact.
- **`pac solution pack --packagetype` flag**: For managed solutions (`sln.IncludeManaged`), verify whether `--packagetype Both` is the correct flag for `pac solution pack` or if it requires two separate pack calls. Research against PAC CLI docs at implementation time.

---

## Dependencies / Assumptions

- `pac solution pack --folder src/` without `--map` packs all files found in `src/`, including web resource binaries and plugin DLLs placed there by `pac solution sync`. Confirmed as the intended behavior.
- Web resource binary files and plugin DLLs will be committed to git as part of `src/`. This is accepted: `src/` is the audit trail of what was in Dataverse.
- `DotnetBuild` enum and `BuildSolutionAsync` become dead code only after U4 (CloneCommand) and U5 (DeployCommand) remove their callers. Remove them in U5 as the final cleanup step.
- `DotNetUtils.AssertDotNetInstalledAsync` remains — it is called by `FlowlineValidator` for the setup check. `DotNetUtils.cs` file stays; only the mapping/build methods are removed.
- No `.cdsproj` scaffold templates exist in the repo — `MappingPac.xml` and `MappingBuild.xml` are generated at runtime by `WriteMappingFilesAsync`. Removing that method (U2) eliminates R3.
