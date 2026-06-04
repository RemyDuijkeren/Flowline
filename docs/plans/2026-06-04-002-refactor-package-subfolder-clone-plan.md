---
status: active
plan-type: refactor
origin: conversation — brainstorm on solution folder structure and IDE experience
---

# refactor: Introduce Package/ subfolder for PAC-managed solution files

---

## Summary

Split the solution folder into two clear ownership zones: `Package/` (PAC-managed) and peer project folders `Plugins/` and `WebResources/` (developer-owned). This gives IDE users (`Solution 'Cr07982'` → `Package`, `Plugins`, `WebResources`) the conventional three-project shape .NET developers expect, and makes it immediately clear what belongs to PAC CLI versus what belongs to the developer.

---

## Problem Frame

Currently `solutions/{SolutionName}/` holds PAC-generated files (`src/`, `{SolutionName}.cdsproj`) and developer projects (`Plugins/`, `WebResources/`) at the same level. In Visual Studio / Rider Solution Explorer, the solution is named after the Dataverse solution (`Cr07982`) and contains a project also named `Cr07982` — creating a confusing duplicate. The `src/` folder reads as C# source to .NET developers but is actually Dataverse XML managed exclusively by PAC CLI.

**Desired IDE result:**

```
Solution 'Cr07982'
├── Package        ← PAC working directory (.cdsproj + src/)
├── Plugins        ← C# plugin assembly project
└── WebResources   ← C# web resources project
```

---

## Scope Boundaries

**In scope:**
- Rename PAC output subfolder to `Package/` and `.cdsproj` to `Package.cdsproj`
- Update all path computation in Flowline CLI that references `src/`, the `.cdsproj`, or the PAC working directory
- Update `.sln` generation to reference `Package\Package.cdsproj` with project name `Package`

**Out of scope:**
- Changing the `Plugins/` or `WebResources/` folder structure
- Adding migration for existing repos (no released version, no migration needed)
- Changing any PAC CLI flags other than `--outputDirectory` and `--solution-folder`

### Deferred to Follow-Up Work
- None identified

---

## Key Technical Decisions

**1. Path split: `slnFolder` vs `packageFolder`**

`slnFolder` (`solutions/{SolutionName}/`) remains the solution root — holds `.sln`, `Plugins/`, `WebResources/`. A new `packageFolder` (`solutions/{SolutionName}/Package/`) becomes the PAC working directory — holds `.cdsproj` and `src/`. Every place that passes `slnFolder` to a PAC command or constructs a `src/` path must use `packageFolder` instead.

**2. Use PAC's own subfolder creation to avoid multi-step moves**

By pre-creating `slnFolder` and passing it as `--outputDirectory`, PAC creates a solution-named subfolder (`solutions/Cr07982/Cr07982/`) inside it. A single `Directory.Move()` renames that to `Package/`. One atomic operation, no partial-move recovery needed.

**3. Rename `.cdsproj` to `Package.cdsproj`**

PAC scans for `*.cdsproj` in its working directory — it does not require the filename to match the solution name. Renaming removes the duplicate-name confusion in IDE and pairs visually with the `Package/` folder name.

**4. `PackageName` constant in `FlowlineCommand`**

A single constant (`"Package"`) and a one-line helper (`PackageFolder(slnFolder)`) centralise the path computation. All command and utility files reference the helper; none hardcode the subfolder name.

---

## System-Wide Impact

Every command that currently passes `slnFolder` to a PAC flag or constructs a `src/` path is affected. The change is mechanical — no behaviour changes, only path resolution.

| Area | What changes |
|---|---|
| `CloneCommand` | Pre-create folder, change `--outputDirectory`, rename, update `.sln` reference |
| `SyncCommand` | `--solution-folder` → `packageFolder`; `src/` dirty-tree and change-summary paths |
| `DeployCommand` + `PacUtils` | `--folder` for `pac solution pack` → `packageFolder/src` |
| `DriftChecker` | Accepts `packageFolder` alongside `slnFolder` for `src/` path construction |
| `SolutionChangeSummary` call sites | `srcPath` argument updated at call sites (already parameterised) |
| `.sln` template | Project entry updated to `Package` / `Package\Package.cdsproj` |

---

## Implementation Units

### U1. Add `PackageName` constant and `PackageFolder` helper

**Goal:** Centralise the `Package/` subfolder name so no command or utility hardcodes it.

**Dependencies:** none

**Files:**
- `src/Flowline/Commands/FlowlineCommand.cs`
- `tests/Flowline.Tests/FlowlineCommandTests.cs`

**Approach:**
- Add `protected const string PackageName = "Package";` alongside the existing `WebResourcesName` and `PluginsName` constants.
- Add `protected static string PackageFolder(string slnFolder) => Path.Combine(slnFolder, PackageName);` as a helper method.

**Patterns to follow:** `AllSolutionsFolderName`, `WebResourcesName`, `PluginsName` in `FlowlineCommand.cs:17-19`

**Test scenarios:**
- `PackageFolder("solutions/Cr07982")` returns `"solutions/Cr07982/Package"` on all platforms (path separator handled by `Path.Combine`)

---

### U2. Update `CloneCommand` to create `Package/` subfolder

**Goal:** After PAC clone, PAC output lives in `Package/` with the `.cdsproj` renamed to `Package.cdsproj`.

**Dependencies:** U1

**Files:**
- `src/Flowline/Commands/CloneCommand.cs`
- `tests/Flowline.Tests/CloneCommandTests.cs` (new)

**Approach:**

Step order in `CloneCommand.ExecuteFlowlineAsync` (around lines 50-78):
1. Pre-create `slnFolder` with `Directory.CreateDirectory(slnFolder)` before invoking PAC.
2. Change PAC clone `--outputDirectory` from `allSolutionsFolder` to `slnFolder`. PAC then creates `slnFolder/{SolutionName}/` with `.cdsproj` and `src/` inside it.
3. `Directory.Move(Path.Combine(slnFolder, projectSln.Name), PackageFolder(slnFolder))` — atomic rename to `Package/`.
4. `File.Move(Path.Combine(PackageFolder(slnFolder), $"{projectSln.Name}.cdsproj"), Path.Combine(PackageFolder(slnFolder), "Package.cdsproj"))` — atomic rename.
5. Update `cdsprojPath` to `Path.Combine(PackageFolder(slnFolder), "Package.cdsproj")`.
6. Update `CreateSolutionFileAsync` arguments: project name = `"Package"`, project path = `Package\Package.cdsproj` (relative to `slnFolder`).
7. In `SeedWebResourceDistFromSrc`: change `srcWebResources` from `Path.Combine(slnFolder, "src", "WebResources")` to `Path.Combine(PackageFolder(slnFolder), "src", "WebResources")`.

**Patterns to follow:** Existing `SetupPluginsProjectAsync` and `SetupWebResourcesProjectAsync` path construction patterns.

**Test scenarios:**
- After clone, `solutions/{Name}/Package/` exists and contains `Package.cdsproj` and `src/`
- After clone, `solutions/{Name}/Plugins/` and `solutions/{Name}/WebResources/` exist as peers of `Package/`
- After clone, `solutions/{Name}/{Name}.sln` contains project reference `Package\Package.cdsproj` with name `Package`
- `solutions/{Name}/{Name}/` (the temp PAC-created subfolder) does not exist after clone completes
- If clone crashes after folder rename but before file rename: `Package/` exists, `{Name}.cdsproj` inside — re-running clone detects partial state and resumes (or fails cleanly without corrupting further)

---

### U3. Update `SyncCommand` to use `packageFolder`

**Goal:** PAC sync and all `src/`-based path operations in sync point to `Package/`.

**Dependencies:** U1

**Files:**
- `src/Flowline/Commands/SyncCommand.cs`
- `tests/Flowline.Tests/SyncCommandTests.cs`

**Approach:**
- `.cdsproj` existence check: change path from `Path.Combine(slnFolder, $"{sln.Name}.cdsproj")` to `Path.Combine(PackageFolder(slnFolder), "Package.cdsproj")`.
- PAC sync `--solution-folder`: change argument from `slnFolder` to `PackageFolder(slnFolder)`.
- Pre-sync dirty-tree guard (`srcPath`): change from `Path.Combine(slnFolder, "src")` to `Path.Combine(PackageFolder(slnFolder), "src")`.
- Post-sync change summary (`srcPath`): same change.

**Patterns to follow:** Existing `slnFolder` usage in `SyncCommand.cs:49-100` (see research findings).

**Test scenarios:**
- Sync validates `.cdsproj` existence at `Package/Package.cdsproj`, not at solution root
- PAC sync is invoked with `--solution-folder` pointing to `Package/` subfolder
- Dirty-tree guard detects uncommitted changes under `Package/src/`, not `src/` at solution root
- Post-sync change summary computes diff from `Package/src/`

---

### U4. Update `DeployCommand` and `PacUtils` to pack from `packageFolder/src`

**Goal:** `pac solution pack` reads from `Package/src/`, not `{slnFolder}/src/`.

**Dependencies:** U1

**Files:**
- `src/Flowline/Utils/PacUtils.cs`
- `src/Flowline/Commands/DeployCommand.cs`
- `tests/Flowline.Tests/PacUtilsTests.cs`

**Approach:**
- `PacUtils.PackSolutionAsync`: the `--folder` argument currently builds `Path.Combine(slnFolder, "src")`. The method signature likely accepts `slnFolder` — either add a `packageFolder` parameter or compute `PackageFolder(slnFolder)` inside. Prefer passing `packageFolder` explicitly to keep the method honest about what path it uses.
- `DeployCommand`: update the call site to pass `PackageFolder(slnFolder)` (or compute `srcPath` from it). Also update `srcPath` for the pre-deploy change summary call.

**Patterns to follow:** `PacUtils.PackSolutionAsync` call sites in `DeployCommand.cs:99-120` and `PacUtils.cs:175-210`.

**Test scenarios:**
- `PackSolutionAsync` invokes PAC with `--folder` pointing to `Package/src/`
- Deploy pre-flight change summary reads from `Package/src/`
- Pack with explicit `packageFolder` argument does not fall back to solution root `src/`

---

### U5. Update `DriftChecker` to accept `packageFolder`

**Goal:** `DriftChecker` compares build artifacts at `slnFolder` with PAC-synced files at `packageFolder/src/` — both paths are needed.

**Dependencies:** U1

**Files:**
- `src/Flowline/Utils/DriftChecker.cs`
- `tests/Flowline.Tests/DriftCheckerTests.cs`

**Approach:**
- `DriftChecker` currently takes `slnFolder` and constructs both `{slnFolder}/WebResources/dist`, `{slnFolder}/Plugins/bin/Release/` (build artifacts — stay at `slnFolder`) and `{slnFolder}/src/WebResources`, `{slnFolder}/src/PluginAssemblies` (PAC files — move to `packageFolder`).
- Add `packageFolder` as a second parameter (or property). Use `slnFolder` for artifact paths; use `packageFolder` for `src/` paths.
- Update all call sites (in `SyncCommand` and wherever else `DriftChecker` is instantiated) to pass `PackageFolder(slnFolder)`.

**Patterns to follow:** `DriftChecker.cs:22-43` — existing two-path comparison pattern.

**Test scenarios:**
- `CheckWebResources` compares `{slnFolder}/WebResources/dist/` against `{packageFolder}/src/WebResources/`
- `CheckPlugins` compares `{slnFolder}/Plugins/bin/Release/` against `{packageFolder}/src/PluginAssemblies/`
- DriftChecker constructed with mismatched `slnFolder`/`packageFolder` (wrong solution) surfaces a detectable mismatch rather than silently passing
- Existing `DriftCheckerTests` pass with updated paths

---

## Deferred Questions

- **PAC `--solution-folder` exact scanning behaviour** — confirmed via user test that PAC scans for `*.cdsproj` in the given folder (does not require filename to match solution name). No further investigation needed.
- **Partial-state recovery on crash** — the two renames (folder + file) are atomic operations on the same drive. The plan does not add explicit resume logic; if crash detection is needed it can be added as a follow-up guard in a future plan.
