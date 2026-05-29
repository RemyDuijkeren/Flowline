---
title: "feat: flowline generate command"
type: feat
status: active
date: 2026-05-29
origin: docs/brainstorms/2026-05-28-generate-command-requirements.md
---

# feat: flowline generate command

## Summary

Adds `flowline generate` — a thin wrapper around `pac modelbuilder build` that auto-discovers solution entities and custom APIs from the live DEV environment, derives or reads the namespace from `.flowline`, and generates early-bound C# types into `Plugins/Models/` via a temp-folder swap for failure safety. Namespace and extra tables are persisted per solution in `.flowline`.

---

## Problem Frame

Plugin developers need early-bound C# types from `pac modelbuilder build`, but the command requires a specific set of flags, an entity filter derived from the live solution, and a namespace matching the plugin project. Flowline already knows the solution, the DEV environment, and the Plugins project — making it the right place to automate this step. (see origin: `docs/brainstorms/2026-05-28-generate-command-requirements.md`)

---

## Requirements

- **R1.** Fixed flags: `-sgca --suppressINotifyPattern --emitfieldsclasses`
- **R2.** Output always `Plugins/Models/` relative to the solution folder; not configurable
- **R3.** Entity filter (`-enf`) built from solution entities + `extraTables`, joined with `;`, deduplicated
- **R4.** Namespace derived (in order) from: `.flowline` `generate.namespace` → `<RootNamespace>` in csproj → `<PackageId>` in csproj → csproj filename without extension + `.Models` → `<SolutionName>.Models` if csproj absent
- **R5.** Derived namespace saved to `.flowline` only after `pac modelbuilder build` succeeds
- **R6.** `--verbose`: print exact `pac modelbuilder build` command before execution
- **R7.** `--namespace <ns>`: set namespace, save to `.flowline`, use for this run (permanent, not ephemeral)
- **R8.** `--extra-tables <table1,table2>`: set (replace, not add to) `extraTables` list in `.flowline` for this run
- **R9.** `[solution]` positional argument: select solution; auto-selected when exactly one exists
- **R10.** Solution has custom APIs → add `--generatesdkmessages --messagenamesfilter <names>` automatically
- **R11.** No custom APIs in solution → omit message generation flags
- **R12.** Entity logical names + custom API message names discovered from configured DEV environment
- **R13.** No DEV URL configured → fail with clear error; direct user to `flowline provision` or set `--dev`
- **R14.** Entity names from solution + `extraTables` deduplicated before building the filter
- **R15.** `.flowline` per-solution config stores `generate.namespace` and `generate.extraTables`
- **R16.** Config updated in-place; other solution fields preserved
- **R17.** `pac modelbuilder build` targets `Plugins/Models~` (temp folder); on success rename replaces `Plugins/Models/`; on failure temp discarded, `Plugins/Models/` unchanged

**Origin acceptance examples:** AE1–AE2b (covers R4–R5), AE3 (covers R3, R14), AE4 (covers R13), AE5 (covers R7), AE6 (covers R17)

---

## Scope Boundaries

- Output path not configurable — always `Plugins/Models/`; users needing different paths run `pac modelbuilder build` directly
- No `builderSettings.json` (`-wstf`) — Flowline owns the flags
- No `dotnet build` after generate
- No change-detection — always re-runs regardless of whether solution changed
- No CI / offline support — requires live DEV + active `pac` auth session; teams needing types in CI commit `Plugins/Models/` to source control
- Multi-project solutions not supported in v1 — tracked in GitHub issue #2; `--project` flag is the post-v1 path
- No separate `Models.csproj` — generated types live inside the Plugins project

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline/Config/ProjectConfig.cs` — `ProjectSolution` currently has `Name` and `IncludeManaged`; needs `GenerateConfig` nested class; `AddOrUpdateSolution` and `GetOrUpdateSolution` patterns for in-place config mutation; `Save()` for persistence
- `src/Flowline/Commands/FlowlineCommand.cs` — base class; `FlowlineCommand<TSettings>`, `ExecuteFlowlineAsync`, `GetAndCheckEnvironmentInfoAsync` (DEV URL validation pattern), `GetOrUpdateSolution` pattern; `PluginsName` constant
- `src/Flowline/Commands/PushCommand.cs` — reference for `Settings` class with `[CommandOption]`/`[CommandArgument]`, solution-scoped command wiring
- `src/Flowline/Utils/PacUtils.cs` — `GetBestPacCommandAsync()` for pac resolution; CliWrap invocation pattern; add new `ModelBuilderBuildAsync` (or inline into command)
- `src/Flowline.Core/Services/PluginReader.cs` — Dataverse query patterns using `QueryExpression`, `ServiceClient`; `solutioncomponent` join pattern for solution membership; custom API publisher-prefix query (existing approach — **not** solution-component-scoped; new discovery must use solution ID-based join)
- `src/Flowline.Core/Services/DataverseConnector.cs` — `ConnectViaPacAsync()` for token-reuse connection; injected into commands
- `src/Flowline/Commands/SyncCommand.cs` — dirty-tree guard pattern (from institutional learnings); spinner + `Console.Ok/Skip/Warning` rhythm
- `tests/Flowline.Tests/ProjectConfigTests.cs` — existing tests for `ProjectConfig`; extend for `GenerateConfig`
- `tests/Flowline.Tests/PacUtilsTests.cs` — injectable delegate seam (`CheckCommandExistsFunc`) for testing PAC calls without a real CLI; replicate for any testable wrapper logic

### Institutional Learnings

- **Dirty-tree guard**: guard before overwriting local generated files; matches `SyncCommand` pattern for `src/`; should check `Plugins/Models/` is not dirty before proceeding (or warn)
- **No silent exceptions** (memory rule): never swallow exceptions; let them bubble unless explicit recovery logic exists (the temp-folder discard on failure is explicit recovery — acceptable)
- `pac plugin init --name` sets `<PackageId>`, not `<RootNamespace>`; real-world projects (e.g., `MyFlowTest/solutions/Cr07982/Plugins/Plugins.csproj`) confirm: no `<RootNamespace>` present; `<PackageId>` is the user-controlled name and can contain a company prefix (e.g., `Contoso.Plugins`)

---

## Key Technical Decisions

- **`GenerateConfig` as nested class on `ProjectSolution`**: consistent with how `ProjectSolution` already owns solution-scoped config; no top-level config additions needed; JSON serialization preserves existing fields via `AddOrUpdateSolution` in-place semantics
- **`pac modelbuilder build` output to `Plugins/Models~` temp folder (R17)**: pac never deletes files — only adds/updates; deleting before pac leaves no output if pac fails; temp-folder rename-on-success gives both total stale-file cleanup and failure safety; `Directory.Move` is atomic on same volume (typical case)
- **Namespace derivation order** (R4): `.flowline` first (fast path for repeat runs); `<RootNamespace>` second (explicit MSBuild override); `<PackageId>` third (set by `pac plugin init --name`, common in real projects); csproj filename fourth (always `Plugins.csproj` → `Plugins.Models`, less useful but predictable); `<SolutionName>.Models` only when csproj absent entirely
- **R5 save-after-success**: namespace saved to `.flowline` only after `pac modelbuilder build` exits 0 — prevents a stale namespace being persisted if the user's first run fails mid-flight
- **Solution-component-scoped entity discovery**: existing `PluginReader.GetRegisteredCustomApisAsync` queries by publisher prefix — not solution-scoped; need `solutioncomponent` join (componenttype=1 for Entity, componenttype=10013 for CustomAPI) to get only entities and custom APIs registered as components of the target solution; new `GenerateReader` service in `src/Flowline.Core/Services/` follows `PluginReader` query style
- **Custom API message names via `solutioncomponent`** (R10–R11): query `customapi` records joined to `solutioncomponent` for the solution; use `uniquename` as the message name filter; when none found, omit `--generatesdkmessages` and `--messagenamesfilter` entirely
- **`--extra-tables` replace semantics** (R8): not additive; replaces the full `extraTables` list; consistent with how `pac` flags work and how `.flowline` stores config; user must re-specify all entries when adding one
- **No `DataverseConnector` injection into `GenerateCommand`**: `GenerateCommand` only needs the Dataverse connection for the discovery query, and the existing `DataverseConnector.ConnectViaPacAsync()` provides the right pattern; inject `DataverseConnector` via constructor (same as `PushCommand`)

---

## Open Questions

### Resolved During Planning

- **Dirty-tree guard for `Plugins/Models/`**: warn if `Plugins/Models/` has uncommitted changes before proceeding; follows SyncCommand's dirty-tree guard pattern; generation replaces the folder entirely, so uncommitted edits would be lost
- **`--messagenamesfilter` format**: space- or comma-separated list of message names passed directly to pac; confirm format during implementation with `pac modelbuilder build --help`
- **Temp folder naming**: `Plugins/Models~` — tilde suffix is conventional for temp build output in .NET tooling; `Directory.Move` replaces `Plugins/Models/` (delete existing first, then move)
- **Solution ID vs unique name for `solutioncomponent` query**: `solutioncomponent.solutionid` is a GUID; resolve solution unique name → ID first (already done by `GetAndCheckSolutionAsync`), then use GUID for component queries

### Deferred to Implementation

- **Exact componenttype codes**: verify `solutioncomponent.componenttype` values for Entity (expected: 1) and CustomAPI (expected: 10013) against Dataverse metadata during implementation
- **`Directory.Move` cross-volume behavior**: if the temp folder and destination are on different volumes (e.g., symlinks), `Directory.Move` throws; detect and fall back to copy+delete if needed — low risk for typical dev setups
- **`pac modelbuilder build` exact flag syntax**: verify `--messagenamesfilter` accepts a semicolon-separated list (consistent with `-enf`) or space/comma-separated; check `pac modelbuilder build --help` during implementation

---

## High-Level Technical Design

> *Directional guidance for review, not implementation specification.*

```
flowline generate [solution] [--namespace ns] [--extra-tables t1,t2] [--dev url]

1. Resolve solution name from arg or .flowline (auto-select if one exists)         → R9
2. Read DevUrl from .flowline / --dev flag; fail if absent                          → R13
3. Read GenerateConfig (namespace, extraTables) from .flowline                      → R15
4. If --namespace supplied: update GenerateConfig.Namespace                         → R7
5. If --extra-tables supplied: update GenerateConfig.ExtraTables                    → R8
6. If GenerateConfig.Namespace null: derive from Plugins/Plugins.csproj             → R4
7. Warn if Plugins/Models/ has uncommitted changes (dirty-tree guard)
8. Connect to DEV via DataverseConnector.ConnectViaPacAsync()
9. Query solutioncomponent → entity logical names                                   → R12, R3
10. Query solutioncomponent → custom API message names                              → R10, R11
11. Deduplicate entity names with extraTables                                       → R14
12. Build pac command:
      pac modelbuilder build
        -o Plugins/Models~
        -enf "entity1;entity2;..."
        -sgca --suppressINotifyPattern --emitfieldsclasses
        -n <namespace>
        [--generatesdkmessages --messagenamesfilter "api1;api2"]
13. --verbose: print command                                                         → R6
14. Run pac (CliWrap); temp folder = Plugins/Models~                                → R17
15. On success: delete Plugins/Models/ (if exists), rename Plugins/Models~ → Plugins/Models/;
               save namespace to .flowline if derived (not already in config)       → R5, R16, R17
16. On failure: delete Plugins/Models~ (if exists); Plugins/Models/ unchanged       → R17
```

---

## Implementation Units

### U1. Extend `ProjectSolution` with `GenerateConfig`

**Goal:** Add per-solution generate config (namespace + extra tables) to the config model; ensure JSON round-trips correctly and existing solution fields are preserved.

**Requirements:** R7, R8, R15, R16

**Dependencies:** None

**Files:**
- Modify: `src/Flowline/Config/ProjectConfig.cs`
- Modify (extend): `tests/Flowline.Tests/ProjectConfigTests.cs`

**Approach:**
- Add `GenerateConfig` nested class: `Namespace` (string?) and `ExtraTables` (string[]?) — both nullable so absent fields serialize as omitted (use `JsonIgnore(Condition = WhenWritingNull)`)
- Add `Generate` property to `ProjectSolution` (type `GenerateConfig?`, nullable)
- `AddOrUpdateSolution` updated to carry `Generate` through in-place replacement (preserve existing `Generate` when called without it)
- No new `GetOrUpdate`-style methods needed — `GenerateCommand` reads/writes `Generate` directly after resolving the solution

**Test scenarios:**
- Round-trip: `ProjectSolution` with `Generate = null` → serialize → deserialize → `Generate` is null
- Round-trip: `GenerateConfig { Namespace = "A.Models", ExtraTables = ["account"] }` → serialize → deserialize → values match
- Existing solution fields (`Name`, `IncludeManaged`) preserved when `Generate` is added via deserialization
- `AddOrUpdateSolution` preserves `Generate` when called only with `Name` + `IncludeManaged`
- Null `ExtraTables` omitted from JSON (not serialized as `null`)

**Verification:**
- All new `ProjectConfigTests` pass
- Existing `ProjectConfigTests` pass (no regressions)

---

### U2. Solution-scoped entity and custom API discovery

**Goal:** Query the DEV environment for (a) entity logical names registered as solution components and (b) custom API message names registered as solution components, both scoped to the target solution by solution ID.

**Requirements:** R10, R11, R12, R14

**Dependencies:** None

**Files:**
- Create: `src/Flowline.Core/Services/GenerateReader.cs`

**Approach:**
- New `GenerateReader` class (stateless static methods or a class following `PluginReader` style)
- `GetSolutionEntityLogicalNamesAsync(IOrganizationServiceAsync2 service, Guid solutionId, CancellationToken ct)` → `IReadOnlyList<string>`
  - Query `solutioncomponent` filtered by `solutionid` = solutionId and `componenttype` = 1 (Entity)
  - Join to `EntityMetadata` to resolve MetadataIds → logical names (use `RetrieveAllEntitiesRequest` or `RetrieveEntityRequest` per ID)
  - Alternative: query `entity` table directly via `QueryExpression` joining `solutioncomponent` (simpler, no metadata API)
  - Verify which approach is cleaner during implementation (see deferred questions)
- `GetSolutionCustomApiMessageNamesAsync(IOrganizationServiceAsync2 service, Guid solutionId, CancellationToken ct)` → `IReadOnlyList<string>`
  - Query `customapi` joined to `solutioncomponent` where `solutionid` = solutionId
  - Return `customapi.uniquename` — this is the message name used by `--messagenamesfilter`
- Both methods run in parallel from `GenerateCommand` (same pattern as `PluginReader.LoadSnapshotAsync` round-1 parallel tasks)
- No unit tests — requires live Dataverse; correctness verified by smoke test during implementation

**Patterns to follow:**
- `src/Flowline.Core/Services/PluginReader.cs` — `QueryExpression` style, parallel task pattern
- `GetComponentSolutionMembershipAsync` for `solutioncomponent` join technique

**Verification:**
- Smoke test: `flowline generate` on a known solution returns the expected entity filter (verified manually)
- Empty custom API list → `--generatesdkmessages` flag absent in pac command output (verbose mode)

---

### U3. Namespace derivation

**Goal:** Derive the model namespace from `Plugins/Plugins.csproj` via fallback chain, or from `<SolutionName>.Models` if the csproj is absent.

**Requirements:** R4, R5

**Dependencies:** None

**Files:**
- Create: `src/Flowline/Utils/NamespaceDeriver.cs`
- Create: `tests/Flowline.Tests/NamespaceDeriverTests.cs`

**Approach:**
- Static class `NamespaceDeriver` with `Derive(string slnFolder, string solutionName)` → `string`
- Csproj path = `{slnFolder}/Plugins/Plugins.csproj`
- If csproj absent: return `{solutionName}.Models` (R4 fallback 4)
- If csproj present: parse as XML (`XDocument.Load`) and check in order:
  1. `<RootNamespace>` → if non-empty, return `{value}.Models`
  2. `<PackageId>` → if non-empty, return `{value}.Models`
  3. Csproj filename without extension → return `{name}.Models` (always `Plugins.Models` for standard projects)
- Return the first non-empty match; `.Models` appended only once

**Test scenarios:**
- Csproj absent → returns `{SolutionName}.Models` (covers AE2b)
- Csproj has `<RootNamespace>Contoso.Plugins</RootNamespace>` → returns `Contoso.Plugins.Models` (covers AE1)
- Csproj has no `<RootNamespace>` but has `<PackageId>Contoso.Plugins</PackageId>` → returns `Contoso.Plugins.Models` (covers AE2)
- Csproj has neither `<RootNamespace>` nor `<PackageId>` → returns `Plugins.Models` (filename fallback)
- Csproj has `<RootNamespace>` empty string → treated as absent, falls through to `<PackageId>`
- Solution name `MyApp`, no csproj → `MyApp.Models`

**Verification:**
- All `NamespaceDeriverTests` pass
- AE1, AE2, AE2b scenarios verified against test output

---

### U4. `GenerateCommand`

**Goal:** Wire everything together — resolve solution, derive/read namespace, discover entities and custom APIs from DEV, build and run `pac modelbuilder build` into a temp folder, swap on success.

**Requirements:** R1–R17 (all)

**Dependencies:** U1, U2, U3

**Files:**
- Create: `src/Flowline/Commands/GenerateCommand.cs`

**Approach:**
- `GenerateCommand(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions runtimeOptions)` — constructor injection following `PushCommand` pattern
- Nested `Settings : FlowlineSettings` with:
  - `[CommandArgument(0, "[solution]")] string? Solution`
  - `[CommandOption("--namespace")] string? Namespace` — saves to `.flowline`
  - `[CommandOption("--extra-tables")] string? ExtraTables` — comma-separated, replaces full list
  - `[CommandOption("--dev")] string? DevUrl`
- `ExecuteFlowlineAsync`:
  1. Resolve solution via `Config!.GetOrUpdateSolution(settings.Solution)` → fail if null (R9, R13)
  2. Resolve DEV URL via `Config!.GetOrUpdateDevUrl(settings.DevUrl)` → fail if null (R13)
  3. Apply `--namespace` → update `solution.Generate.Namespace`, mark as user-supplied (R7)
  4. Apply `--extra-tables` → split on comma, update `solution.Generate.ExtraTables` (R8)
  5. Derive namespace if `solution.Generate.Namespace` null → call `NamespaceDeriver.Derive(slnFolder, solution.Name)` (R4); track `namespaceWasDerived = true`
  6. Check dirty-tree for `Plugins/Models/` — warn if uncommitted changes
  7. Connect to DEV: `await _connector.ConnectViaPacAsync(devUrl, ct)`
  8. In parallel: `GenerateReader.GetSolutionEntityLogicalNamesAsync(...)` + `GenerateReader.GetSolutionCustomApiMessageNamesAsync(...)` (R10–R12)
  9. Deduplicate entities + extraTables (R14)
  10. Build pac args; if `--verbose`, print command (R6)
  11. Run `pac modelbuilder build -o {tempFolder} ...` via `PacUtils.GetBestPacCommandAsync()` + CliWrap; `WithValidation(CommandResultValidation.None)`; check `result.IsSuccess` (R1, R2, R17)
  12. On success: delete `Plugins/Models/` if exists; `Directory.Move(tempFolder, modelsFolder)`; if `namespaceWasDerived`, save namespace to `.flowline` + `Config.Save()` (R5, R16, R17)
  13. On failure: delete `tempFolder` if exists; surface pac stderr as error; exit non-zero (R17)
- Paths: `slnFolder = {RootFolder}/solutions/{solution.Name}`, `modelsFolder = {slnFolder}/Plugins/Models`, `tempFolder = {slnFolder}/Plugins/Models~`

**Patterns to follow:**
- `src/Flowline/Commands/PushCommand.cs` — constructor injection, Settings class, solution resolution, spinner usage
- `src/Flowline/Utils/PacUtils.cs` — `GetBestPacCommandAsync()` + CliWrap invocation with `WithValidation(CommandResultValidation.None)`
- `src/Flowline/Commands/SyncCommand.cs` — dirty-tree guard, `Console.Ok`/`Warning` rhythm

**Test scenarios:**
- Integration tests not included in this unit (live PAC + Dataverse required); covered by manual smoke test
- Unit-testable logic lives in U1 (config), U2 (deferred to integration), U3 (namespace derivation)

**Verification:**
- Build passes
- `flowline generate` on a solution with entities: `Plugins/Models/` populated with `.cs` files
- `flowline generate` on a solution with custom APIs: pac command includes `--generatesdkmessages --messagenamesfilter` in verbose output
- AE6: simulated pac failure leaves `Plugins/Models/` unchanged and `Plugins/Models~` does not exist
- AE5: `--namespace NewNs.Models` → `.flowline` updated, pac receives `-n NewNs.Models`

---

### U5. Register `GenerateCommand` in `Program.cs`

**Goal:** Make `flowline generate` available as a CLI command.

**Requirements:** Supports all requirements (entry point)

**Dependencies:** U4

**Files:**
- Modify: `src/Flowline/Program.cs`

**Approach:**
- Add `services.AddSingleton<DataverseConnector>()` if not already registered (it is — already registered for `PushCommand`)
- Add `config.AddCommand<GenerateCommand>("generate")` with description and examples following the pattern of existing commands

**Test scenarios:**
- Test expectation: none — trivial registration; verified by build + smoke test

**Verification:**
- `flowline --help` shows `generate` in the command list
- `flowline generate --help` shows `--namespace`, `--extra-tables`, `--dev` options

---

## System-Wide Impact

- **Config schema change:** `ProjectSolution` gains optional `Generate` property; existing `.flowline` files without `Generate` deserialize cleanly (property is null); backwards compatible
- **New service:** `GenerateReader` added to `Flowline.Core.Services`; stateless, no DI registration needed (instantiated directly from command)
- **No changes to existing commands:** `CloneCommand`, `SyncCommand`, `PushCommand`, `DeployCommand` unchanged
- **Error propagation:** pac failure surfaces via exit code + stderr; `Plugins/Models/` unchanged (R17); temp folder discarded; `GenerateCommand` exits non-zero
- **State lifecycle:** `Plugins/Models/` is fully replaced on each successful run (stale files removed); any manual edits to generated files are intentionally overwritten — this is the documented design
- **API surface parity:** `GenerateCommand` is a new command; no existing commands or config fields modified in breaking ways

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| `pac modelbuilder build` output structure changes | pac is the owner; flags are opinionated defaults; if flags change, command fails loudly (no silent validation) |
| `Directory.Move` fails cross-volume | Detect `IOException` on move; fall back to `Directory.Copy` + `Directory.Delete`; low risk for typical dev machines |
| `solutioncomponent` componenttype codes differ from expected | Verify codes during implementation before coding the query; use Dataverse metadata browser or `pac` to confirm |
| Namespace derivation derives `Plugins.Models` (filename fallback) when user expected company prefix | R5 saves on success; user sees the derived namespace and can override with `--namespace`; R7 persists the override permanently |
| `Plugins/Models~` left behind if process killed mid-generation | Next run recreates temp folder; `Directory.Move` will fail if temp exists — delete temp at start of run (before pac invocation) to ensure clean state |

---

## Sources & References

- **Origin document:** [docs/brainstorms/2026-05-28-generate-command-requirements.md](docs/brainstorms/2026-05-28-generate-command-requirements.md)
- **GitHub issue #2:** multi-project support (--project flag, post-v1)
- `pac modelbuilder build` reference: https://learn.microsoft.com/en-us/power-platform/developer/cli/reference/modelbuilder
- Dataverse `solutioncomponent` component type codes: https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/reference/solutioncomponent
