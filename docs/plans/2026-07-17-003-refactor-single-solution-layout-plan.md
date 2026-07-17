---
title: Single-Solution Project Layout - Plan
type: refactor
date: 2026-07-17
topic: single-solution-project-layout
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
deepened: 2026-07-17
---

# Single-Solution Project Layout - Plan

## Goal Capsule

- **Objective:** Collapse Flowline's project structure from a `solutions/<Name>/`-wrapped, array-based multi-solution model to exactly one Dataverse solution per project root — `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, the `.sln`, and `.flowline` all live directly at the project root.
- **Product authority:** Direct maintainer dialogue (`ce-brainstorm`), grounded in inspection of two real Flowline projects (a client engagement, a personal test project) and verification against current source (`FlowlineCommand.cs`, `ProjectConfig.cs`, `ProjectSolution.cs`, `InvocationLogger.cs`, `GitUtils.cs`, `DeployCommand.cs`, `SyncCommand.cs`, `StatusCommand.cs`, `DataverseContextGenerator.cs`, `PushCommand.cs`, `GenerateCommand.cs`, `CloneCommand.cs`, `BackupService.cs`, `PacUtils.cs`).
- **Open blockers:** None.

## Product Contract

### Summary

Flowline moves to exactly one Dataverse solution per project root. `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, the `.sln`, and `.flowline` live directly at the root instead of under a `solutions/<Name>/` wrapper. `.flowline`'s schema drops the `Solutions` array in favor of a single versioned `Solution` object and renames its required `Name` field to `UniqueName`, read directly wherever a command needs the solution's identity — matching today's usage of `Name`, with no new cross-validation against `Package/`'s or an artifact's content. This lands as part of the push to v1.0, as a hard cutover with no back-compat execution layer and no automated migration tooling, but with explicit legacy-schema detection and an actionable manual-migration error.

### Problem Frame

The `solutions/<Name>/` wrapper was added anticipating other solution-independent folders that never materialized. Inspection of the two real Flowline projects in active use shows exactly one solution folder each, with nothing else living at that level. The wrapper's cost is real and ongoing: every command that resolves project paths (`deploy`, `sync`, `status`, project-mode `generate`, project-mode `push`) builds them through an extra directory level and collection-selection/path-routing logic, backed by a `Solutions[]` array config shape that no real project has ever populated with more than one entry.

Microsoft's own Power Platform ALM guidance corroborates the default: a single-solution strategy is "recommended for small-medium scale implementations, scenarios where future modularization is unlikely" — matching Flowline's solo/small-team consultant persona (`STRATEGY.md`). The guidance also recognizes a legitimate second tier — multiple solutions in the same development environment, for "distinct and independent functional areas that don't share components" — which this plan addresses through documented workflow patterns rather than new tooling (see Multi-Solution Workflow Guidance below).

```text
Before                              After
ProjectRoot/                        ProjectRoot/
  .flowline                           .flowline
  .gitignore                          .gitignore
  AGENTS.md                           AGENTS.md
  solutions/                          Package/
    Cr07982/                            Package.cdsproj
      Cr07982.sln                       src/
      Package/                        Plugins/
        Package.cdsproj                 Plugins.csproj
        src/                         WebResources/
      Plugins/                         WebResources.csproj
        Plugins.csproj               tests/            (user-created, when needed)
      WebResources/                 docs/              (user-created; clone also
        WebResources.csproj              writes DATAVERSE_CONTEXT.md here)
      artifacts/                      artifacts/
      DATAVERSE_CONTEXT.md            CHANGES.md
      CHANGES.md                     Cr07982.sln
```

### Requirements

**Folder layout**
- R1. `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, and `CHANGES.md` live directly at a Flowline project's root, flat, with no `src/` wrapper and no `solutions/<Name>/` folder. `CHANGES.md` follows the near-universal `CHANGELOG.md`-at-root convention (matching this repo's own `CHANGELOG.md`), unlike `DATAVERSE_CONTEXT.md`, which moves into `docs/` — it's domain/schema reference material, not a changelog.
- R2. `tests/` and `docs/` are recognized root-level folders in the documented layout, but neither is scaffolded with placeholder content by `flowline clone` — creating and populating them is left to the user, whenever they're needed. Exception: `clone` still creates `docs/` as needed to write `DATAVERSE_CONTEXT.md` (R1), same as it does today for the old location — this isn't scaffolding a convention, just the existing generation behavior landing at its new path.
- R3. `flowline clone` scaffolds the root-level layout for freshly cloned projects, including its own `AGENTS.md` template content (`CloneCommand.cs:139-228`), which today hardcodes the old `solutions/{{solutionName}}/` structure diagram and multi-solution positional-argument guidance (`CloneCommand.cs:181,188,221,223`) — this is rewritten to match the new layout and command surface (R8).
- R4. Every command that today constructs paths through `solutions/<Name>/` (`clone`, `deploy`, `sync`, `status`, project-mode `generate`, project-mode `push`, `drift`, `DataverseContextGenerator`) resolves paths from the project root directly instead. These paths are exposed through `FlowlineCommand<T>`'s existing shared path helpers; commands do not each grow their own `Path.Combine(RootFolder, ...)` implementation.

**Configuration schema**
- R5. `.flowline`'s config adds required `SchemaVersion: 1` and replaces the `Solutions` array with a single `Solution` object. The former required `Name` becomes required, non-empty `UniqueName`; `IncludeManaged`, `PluginPackageMode`, and `Generate` (including all of `GenerateConfig`'s optional fields) keep their current shape and defaults within it. `PluginPackageMode` is explicitly retained; it must not be lost merely because the former collection type is removed.
- R6. `.flowline`'s `Solution.UniqueName` is read directly wherever a command needs the Dataverse solution's identity: normal project commands read it from config; fresh `clone` validates its required `<solution>` argument against Dataverse and writes the canonical returned unique name to `.flowline` only after that validation succeeds; standalone `push`/`generate` continue using their existing positional argument, unchanged. No command cross-validates `UniqueName` against `Package/`'s or an artifact's `Solution.xml` content — this matches today's behavior, just with the config schema collapsed and the field renamed. A passed project-mode `push`/`generate` solution argument is validated case-insensitively against configured `Solution.UniqueName` and errors on mismatch (unchanged from today's equivalent check against `Name`).
- R7. When a command requires local `Package/src/Other/Solution.xml` to build or pack and it is missing, malformed, or unreadable, the command fails with a clear `FlowlineException` naming the path and suggesting a fix (e.g. restore `Package/` from git, or re-run `flowline clone`) — not a raw, unhandled exception. This is a content-availability precondition, not an identity check: it does not compare the manifest's unique name against `.flowline`'s configured `UniqueName`. `InvocationLogger` reads configured `Solution.UniqueName` directly in project mode; for fresh clone and standalone execution it logs the supplied argument when available and otherwise omits the tag, so telemetry never makes command execution fail.

The schema transition is intentionally explicit:

```jsonc
// Before (unsupported after the cutover)
{
  "DevUrl": "https://contoso-dev.crm.dynamics.com",
  "ProdUrl": "https://contoso.crm.dynamics.com",
  "Solutions": [
    {
      "Name": "Cr07982",
      "IncludeManaged": true,
      "PluginPackageMode": "Auto",
      "Generate": {
        "Namespace": "Cr07982.Models",
        "ServiceContextName": "Cr07982Context"
      }
    }
  ]
}
```

```jsonc
// After (SchemaVersion 1)
{
  "SchemaVersion": 1,
  "DevUrl": "https://contoso-dev.crm.dynamics.com",
  "ProdUrl": "https://contoso.crm.dynamics.com",
  "Solution": {
    "UniqueName": "Cr07982",
    "IncludeManaged": true,
    "PluginPackageMode": "Auto",
    "Generate": {
      "Namespace": "Cr07982.Models",
      "ServiceContextName": "Cr07982Context"
    }
  }
}
```

JSON-with-comments is shown for documentation readability; the actual `.flowline` files remain ordinary JSON without comments.

**Command surface**
- R8. `DeployCommand`'s `--solution` flag, `SyncCommand`'s positional `[solution]` argument, and `DriftCommand`'s positional `[solution]` argument (`DriftCommand.cs:25-27`) are removed outright — all three are pure project-mode selectors with no other use; like `Sync`, `Drift` never overrides `RequiresProject`, so it has no standalone mode and its selector has nothing left to select once only one solution exists. `PushCommand`'s and `GenerateCommand`'s positional `[solution]` argument stays — standalone mode requires it as its only source of the target Dataverse solution name (`PushCommand.cs:434-440`, `GenerateCommand.cs:118`), since standalone mode has no `.flowline` to read one from. In project mode, a passed `[solution]` value is now validated against the single configured solution (error on mismatch) rather than used to select among several. `CloneCommand`'s positional `<solution>` argument is unrelated (it names the new solution to clone, not a selector among configured ones) and stays unchanged, as does `DeployCommand`/`DriftCommand`'s `<target>` and `ProvisionCommand`'s `[role]` (deploy-target/environment-role, not solution selectors).
- R9. `StatusCommand` renders its existing grid output unchanged in mechanism, producing a single row.

**Documentation**
- R10. `docs/folder-structure.md` and the repo's own `CLAUDE.md` (its "Folder structure when a user uses Flowline" section — the diagram and the "never at the repo root" / "sibling folders under `solutions/`" rules) are rewritten to describe the root-level layout, replacing the `solutions/<Name>/` description. The folder-structure documentation includes concise manual cutover steps for moving the contents of a single legacy `solutions/<Name>/` directory to the project root, converting `.flowline` to `SchemaVersion: 1`, and renaming the retained single solution's `Name` to `UniqueName`; this is documentation, not an automated migration feature.
- R11. Affected GitHub Wiki pages — verified by content, not assumed: `01-Getting-Started.md`, `03-Command-Reference.md`, `06-Sync.md`, `07-Deploy.md`, `09-Generate-Early-Bound-Types.md`, `10-AI-Agents.md`, `13-Migration-from-spkl.md`, `14-Migration-from-Daxif.md`, `15-Migration-from-ALM-Accelerator.md`, `16-Migration-from-PACX.md` — are updated in the same body of work. `01-Getting-Started.md` links to or contains the manual pre-v1 layout cutover note. `03-Command-Reference.md` documents the exact `--solution`/`[solution]` surface R8 removes and the multi-row `status` example R9 supersedes.
- R12. Documentation describes both supported patterns for the same-environment, multiple-solution case — separate repo, or a nested `solutions/<Name>/` folder of independent projects — including the limitation that neither pattern shares environment config across sibling `.flowline` files.

**Lifecycle and safety**
- R13. Config loading distinguishes four states: no `.flowline` exists; valid `SchemaVersion: 1` (a null `Solution` is valid here — the post-`provision`, pre-`clone` bootstrap state, since `provision` saves `.flowline` with only URL fields, before any solution is cloned); syntactically/semantically invalid v1 (a non-null `Solution` with a missing or empty `UniqueName`); and unsupported/legacy schema. An existing invalid or legacy file is never converted into `new ProjectConfig()` and silently treated as an empty project. Legacy `Solutions`/`Name`, an invalid non-null `Solution`, or a missing/unsupported schema version produces `FlowlineException(ExitCode.ConfigInvalid, ...)` with the detected config path and manual-cutover documentation reference.
- R14. `clone`'s existing idempotency check is unchanged: it skips re-cloning if `Package.cdsproj` already exists (`CloneCommand.cs:322-331`, just relocated to the root layout), and an existing `Package/` without a readable manifest or `Package.cdsproj` still fails with today's targeted incomplete-project recovery message. For a fresh project, clone validates the requested solution against Dataverse, then saves the canonical returned unique name to `.flowline` only after that validation succeeds. No new cross-check against a previously configured or existing local identity is added.
- R15. Flattening does not automatically redefine the deploy git-clean guard or reusable-artifact cache key as "everything below project root." A shared deployment-input scope includes `Package/`, packaging/build project files discovered from the `.sln`, and configuration values that affect produced artifacts; it excludes `docs/`, `tests/`, `CHANGES.md`, agent instruction files, and other non-packaging material. Both the clean check and source revision/cache calculation use this same scope so their behavior cannot diverge.

### Multi-Solution Workflow Guidance

This plan removes Flowline's only built-in multi-solution capability — the `.flowline` `Solutions[]` array and `StatusCommand`'s multi-row grid. Microsoft's own Power Platform ALM guidance still recognizes a legitimate case this leaves unaddressed: multiple solutions in the same development environment, for "distinct and independent functional areas that don't share components." Two patterns cover it, documentation only, no new Flowline tooling:

- **Separate repo per solution.** Each solution gets its own repo, its own `.flowline`, its own git history. The default answer, no caveats.
- **A `solutions/<Name>/` folder inside one repo, containing multiple independent, self-contained Flowline projects** — each with its own `.flowline`, `Package/`, `Plugins*/`, `WebResources/` at its own root, one level under `solutions/`. This already works with zero code changes: `FindProjectRoot` (`FlowlineCommand.cs:62-72`) walks upward from the current directory to the nearest `.flowline`, and `CloneCommand` (`CloneCommand.cs:46`) has no requirement that its target folder be fresh or empty — running `flowline clone` from inside `solutions/SolutionB/` just works, unaware of any sibling. Shared git history and shared `AGENTS.md`/`CLAUDE.md` context are the benefit over a separate repo.

**Known limitation, both patterns:** neither shares environment config (`ProdUrl`/`DevUrl`/auth) across sibling `.flowline` files — each carries its own copy. Solved only by the deferred umbrella `.flowline` (see Scope Boundaries), not by this plan.

### Scope Boundaries

**Deferred for later**
- An umbrella/workspace-level `.flowline` that discovers and aggregates across nested `.flowline` files (e.g., a hypothetical `flowline status --all`) — explicitly parked, not designed as part of this work.
- Any tooling that specifically detects or assists the nested `solutions/<Name>/` multi-project pattern (in `flowline clone` or elsewhere) — it already works via existing upward directory search and stays a documented convention, not a built feature.
- Real `.sln`-membership discovery for the deploy-input scope (R15/U6) — this plan ships a minimal explicit path list; the sibling multi-plugin-project-support plan's discovery mechanism replaces it once that work lands.

**Outside this work**
- Automated migration tooling or execution compatibility for repos on the old layout. Legacy detection, a clear error, and manual cutover documentation are required by R10/R11/R13 and are not considered migration tooling.
- Any user-visible behavior change to standalone-mode `push`/`generate` — verified otherwise unaffected; standalone mode never touches `solutions/<Name>/` path construction or `.flowline` (`PushCommand.cs:283`, `PushCommand.cs:354`, `GenerateCommand.cs:356`).
- Any change to `deploy --path`'s identity handling — it continues reading only version and managed state from the artifact manifest (`DeployCommand.cs:69-90`), unchanged from today; no new unique-name comparison between the artifact, local `Package/`, and `.flowline` is added.

### Dependencies / Assumptions

- Assumes the only real-world Flowline projects affected today are the two inspected repos; both are hand-migrated by the maintainer outside this plan's scope.
- This lands before v1.0 as a pre-release hard cutover; assumes v1.0's milestone date (`STRATEGY.md`, currently 2026-07-20) moves to accommodate it landing first.
- This plan lands before `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md`, which also edits `docs/folder-structure.md` (its KD5/R8 relaxes the fixed `Plugins/` folder-name assumption via `.sln`-membership discovery). That plan's Goal Capsule now records this plan as a blocker; it rebases onto this one's root-level layout rather than the two landing independently.
- U6's deployment-input scope must adopt the same `.sln`-membership discovery introduced or finalized by the multi-plugin-project-support work; its minimal explicit path list is not a permanent design.

### Sources / Research

- Two real Flowline projects inspected on disk — a client engagement project and a personal test project — each holding exactly one solution folder under `solutions/`, with nothing else at that level.
- Microsoft Learn, "Organize your solutions in Power Platform" (`learn.microsoft.com/en-us/power-platform/alm/organize-solutions`) — single-solution and same-environment-multiple-solution strategy tiers.
- David Fowler, ".NET project structure" (`gist.github.com/davidfowl/ed7564297c61fe9ab814`) — the widely-cited `src`/`tests`/`docs`/`artifacts` convention, including its own explicit exception for single/few-project scenarios.
- Microsoft Learn, "Organizing and testing projects with the .NET CLI" (`learn.microsoft.com/en-us/dotnet/core/tutorials/testing-with-cli`) — official `src`/`test` tutorial, itself noting the split is a preference some developers skip (`dotnet/docs#26395`).
- Dapper (widely-used, well-regarded minimal library) — real-world example with no `src`/`test` folder split, cited as precedent for the flat choice at Flowline's realistic project-count scale.
- PACX comparison, public wiki `pacx-project-init` (`github.com/neronotte/Greg.Xrm.Command/wiki/pacx-project-init`) and implementation inspected at `E:\Code\Greg.Xrm.Command` (`PacxProjectDefinition.cs:8-13`, `FolderTree.cs:26-46`, `PacxProjectRepository.cs:11-45`, `InitProjectCommandExecutor.cs:58-80`) — an adjacent Dataverse CLI defines a project as a nearest-directory marker with one explicit default solution name, corroborating one solution binding per project marker. Its loader's catch-and-return-null behavior contrasts with, and reinforces, this plan's stricter fail-closed config contract (R13) rather than serving as a behavior to copy.

## Planning Contract

**Product Contract preservation:** unchanged from the requirements-only artifact `ce-brainstorm` produced — no R-IDs were added, removed, or renumbered during planning; wording tightened only where research (below) sharpened an existing decision's rationale.

### Key Technical Decisions

- **KTD1 — No automated migration or back-compat execution path, but legacy schema is detected explicitly.** Flowline's commands do not execute against the old `solutions/<Name>/` layout or array-form `.flowline`. The config loader detects the legacy `Solutions`/`Name` shape (or a missing/unsupported `SchemaVersion`) and fails with a clear `FlowlineException` pointing at the manual-cutover documentation (R13). Relying on JSON deserialization exceptions or a later missing-path/null failure is not acceptable: `ProjectConfig.Load` (`ProjectConfig.cs:255-264`) today catches any read/deserialize exception and returns `null`, and `FlowlineCommand.cs:97` replaces that `null` with `new ProjectConfig()` — meaning an unreadable or semantically obsolete config can silently present as a blank project today. R13 closes that gap for the legacy-schema case specifically.
- **KTD1b — A null `Solution` is valid v1, not invalid.** `ProvisionCommand.cs:64-66,173` saves `.flowline` with only URL fields before any solution is cloned — a real, common sequence (`provision` runs before `clone`). `ProjectConfig.Save()` writes `SchemaVersion: 1` on every save regardless of whether `Solution` is populated yet, so this state is distinguishable from a genuinely legacy file (no `SchemaVersion` at all) rather than being misclassified as invalid the first time anything loads config between `provision` and `clone`.
- **KTD2 — The new `.flowline` schema is explicitly versioned from its first release.** `SchemaVersion: 1` distinguishes the supported single-solution shape from the legacy unversioned `Solutions[]` shape and gives future breaking config changes a reliable validation/migration boundary.
- **KTD3 — `Solution.Name` is retained as project identity but renamed to required `Solution.UniqueName`.** Removing it would make `.flowline` cease to be self-describing — `status`, diagnostics, and telemetry would have no way to identify the project without reading local package content first. `UniqueName` is read directly wherever a command needs the solution's identity, the same way today's `Name` field is used — no new cross-validation against `Package/`'s or an artifact's `Solution.xml` is added. This is the minimal option: it matches current behavior with the schema collapsed from array to object and the field renamed to Dataverse's own vocabulary for clarity, not a new safety feature.
- **KTD4 — Repeated clone behavior matches today's idempotency check.** Re-running `flowline clone <same-name>` in an initialized project resumes/repairs without re-cloning if `Package.cdsproj` already exists — unchanged from `CloneCommand.cs:322-331`'s current behavior, just relocated to the root layout. Clone writes the Dataverse-confirmed unique name to `.flowline` after a successful clone; no new cross-check against a previously configured or existing local identity is added. An existing `Package/` without a readable manifest or `Package.cdsproj` still produces today's targeted incomplete-project recovery error.
- **KTD5 — `--solution`/`[solution]` removal is scoped precisely.** `DeployCommand`'s `--solution` flag, `SyncCommand`'s positional `[solution]` argument, and `DriftCommand`'s positional `[solution]` argument are removed — all three are pure project-mode selectors with no other use, and (like `Sync`) `Drift` has no standalone mode for the argument to fall back to. `PushCommand`'s and `GenerateCommand`'s positional `[solution]` argument stays, since standalone mode needs it as its only identity source; project mode now validates a passed value against the single configured solution instead of selecting among several.
- **KTD6 — `StatusCommand` keeps its existing grid-rendering mechanism unchanged.** It now always renders exactly one row; no redesign to a non-grid layout.
- **KTD7 — The `.sln` file stays named after the Dataverse solution's unique name**, unchanged from today's `clone` logic — just written to the project root instead of a subfolder.
- **KTD8 — `Package/`, `Plugins/`, and `WebResources/` keep their current names, unchanged by the relocation.** Renaming `Package` was considered — its meaning was partly carried by the enclosing `solutions/<Name>/` folder, which this plan removes — but rejected as separate, disproportionate scope: it's an established term (`CONCEPTS.md`, the `PackageName` constant, the milestone history, both real existing repos) whose disambiguation job moves to the sibling `.sln` file's name rather than disappearing.
- **KTD9 — No `src/` wrapper — the layout stays flat, even with multi-plugin-project support.** Considered nesting `Plugins*/`/`WebResources/` under `src/`, paired with `tests/`, matching common dotnet convention (David Fowler's widely-cited `.NET project structure` reference; Microsoft's own "Organizing and testing projects with the .NET CLI" tutorial both recommend `src/`+`tests/`). The deciding factor was project count, not convention-following for its own sake: the multi-plugin-project-support plan means a solution *can* have more than one plugin project, but in practice — based on the maintainer's experience with Dataverse projects generally, not a measured statistic — the common case stays at `Package/`, `Plugins/`, `WebResources/` (+ maybe `Plugins.Tests/`), and the rare larger case adds only one or two more plugin projects — never the dozens-of-projects scale (e.g. ASP.NET Core's own repo) where `src/`/`tests/`/`tools/` grouping actually earns its keep. Both the Fowler reference and Microsoft's own tutorial explicitly carve out small/few-project scenarios as legitimate exceptions to the `src/` convention; Dapper skips `src/`/`test/` entirely for the same reason.
- **KTD10 — No new `FlowlineProjectLayout` type; extend `FlowlineCommand<T>`'s existing shared statics.** `FlowlineCommand.cs:25-28` already centralizes `PackageName`, `PackageFolder()`, `PluginsName`, `WebResourcesName` as protected members inherited by every command — this already satisfies "paths represented once." The only change needed is removing `AllSolutionsFolderName` and feeding these existing helpers `RootFolder` directly instead of a `solutions/<Name>/`-prefixed `slnFolder`. Introducing a new named abstraction for this would duplicate machinery the codebase already has, for no additional requirement it doesn't already satisfy.
- **KTD11 — Legacy-schema detection is a raw JSON pre-parse check, not a custom converter.** `ProjectConfig.Load` parses the raw JSON with `JsonDocument` before strongly-typed deserialization, checking for `SchemaVersion == 1`, absence of a legacy `Solutions` property, and a non-empty `Solution.UniqueName`. A custom `JsonConverter` was considered and rejected: the detection logic is a one-time gate at load time, not a per-property conversion rule, and a converter would entangle schema-version branching with `System.Text.Json`'s serialization pipeline for no benefit over a straightforward pre-check.
- **KTD12 — The deployment-input scope (R15) ships a minimal explicit path list now, not `.sln`-membership discovery.** That discovery mechanism is the sibling multi-plugin-project-support plan's responsibility (per Dependencies); building it here would duplicate work already scoped elsewhere. The list this plan ships is deliberately narrow (`Package/`, the known `Plugins`/`WebResources` project files) so it can be swapped for real `.sln`-membership discovery without redesigning the shared helper that consumes it.
- **KTD13 — Deploy source scope stays intentional after flattening, not "everything below root."** The git-clean guard and artifact-cache source key are based on deployment-affecting inputs (Package/, packaging/build project files, packaging-affecting `.flowline` settings), not the entire project root. Documentation, tests, `CHANGES.md`, and other non-packaging files sharing the root with solution projects must not block a deploy or invalidate a reusable artifact merely because flattening put them at the same directory level.

### Sources / Research (technical)

- Current `solutions/<Name>/` path construction sites: `FlowlineCommand.cs:25` (`AllSolutionsFolderName` constant), `CloneCommand.cs:63,339` (including the actual folder-creation call), `DriftCommand.cs:41`, `DeployCommand.cs:68`, `SyncCommand.cs:64`, `StatusCommand.cs:146,165`, `DataverseContextGenerator.cs:23`, `PushCommand.cs:285,356`, `GenerateCommand.cs:166`. Note: some sites reference the `AllSolutionsFolderName` constant, others hardcode the literal `"solutions"` string directly — reconciled naturally once every site routes through `RootFolder` (KTD10).
- `.flowline`'s current array schema and its consumers: `StatusCommand.cs:74` (`config.Solutions.ToList()`) is the one functional consumer of the array's multiplicity; `InvocationLogger.cs:33` incidentally joins solution names for a telemetry tag at any count.
- Every current caller of `ProjectSolution.Name` — verified by a full-repo grep, not just the files named above, per `docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md` ("grep the whole source tree for every caller before implementing an identity-key rename"): `CloneCommand.cs` (10 call sites), `DeployCommand.cs` (16 call sites across logging, DTAP gate, cache messaging, packing), `DriftCommand.cs:42,50`, `FlowlineCommand.cs:219,220,222,224`, `GenerateCommand.cs:138`, `InvocationLogger.cs:33`, `PushCommand.cs:260`, `StatusCommand.cs:96,160`, `SyncCommand.cs` (7 call sites), `BackupService.cs:13`, `PacUtils.cs:183` — the last two were not named in the brainstorm's own file lists and are added here as a direct consequence of the grep-before-rename discipline that learning documents.
- `ProjectConfig.cs`'s actual current API is a `HashSet<ProjectSolution>` (custom `NameComparer`) with `AddOrUpdateSolution(ProjectSolution)`, `AddOrUpdateSolution(string, bool)`, and `GetOrUpdateSolution(string?, bool?, FlowlineSettings?)` — including an existing "if no name given and exactly one solution configured, auto-select it" shortcut (`ProjectConfig.cs:200-215`) and an interactive `IncludeManaged`-conflict overwrite-confirmation prompt (`ProjectConfig.cs:223-240`, `ConsoleHelper.Confirm`). U1 collapses this to a single-object equivalent that preserves both behaviors — confirmed by the existing regression tests in `tests/Flowline.Tests/ProjectConfigTests.cs` (`GetOrUpdateSolution_NoName_SingleSolution_IncludeManagedDiffers_*`, `AddOrUpdateSolution_Preserves*`).
- `docs/solutions/architecture-patterns/package-subfolder-ide-layout-2026-06-04.md` — documents the exact existing `Directory.Move`/`File.Move` rename dance clone uses to turn PAC's raw output into `Package/Package.cdsproj`; U3 changes only its target folder, not its mechanism.
- `docs/solutions/build-errors/flowlineexception-exitcode-assembly-boundary.md` — confirms `FlowlineException`/`ExitCode` live in `Flowline.Core`, already available via existing `using Flowline;` in every command file this plan touches.
- Existing project identity field renamed rather than removed by R5/R6: `ProjectSolution.cs:22` defines `Name`; the new schema calls it `UniqueName` to match Dataverse terminology. It is read directly, the same way `Name` is read today — no new validation role is assigned to it.
- Test project and framework: `tests/Flowline.Tests/` uses xUnit (`[Fact]`) and FluentAssertions (`.Should()`), confirmed from `ProjectConfigTests.cs`, `CloneCommandTests.cs`, and the `DeployCommand*Tests.cs` family. No existing test file covers `InvocationLogger` — U5 adds one.
- `deploy --path`'s current identity handling, confirming Scope Boundaries' "no change" carve-out: `DeployCommand.cs:69-90` reads only version/managed state from the artifact's `Other/Solution.xml` today, with no unique-name comparison.
- Deploy scope widening behind R15/U6: `DeployCommand.cs:339` (`ValidateGitCleanAsync`, calling `GitUtils.GetUncommittedChangesInPathAsync(slnFolder, RootFolder, ...)`) and `DeployCommand.cs:94` (`GitUtils.GetLastCommitShaForPathAsync(slnFolder, RootFolder, ...)`) both currently scope to `slnFolder`; blindly redefining that as the flattened project root would make unrelated root files (docs/, tests/, CHANGES.md) affect deploy safety and cache behavior.
- `ValidateLocalState` (`DeployCommand.cs:348-353`) checks `Package/Package.cdsproj` existence — a different file and purpose than R7's `Package/src/Other/Solution.xml` check — and is unaffected by this plan (Scope Boundaries).
- Project-root discovery mechanism underpinning the nested-folder multi-solution pattern: `FlowlineCommand.cs:62-72` (`FindProjectRoot`), `CloneCommand.cs:46` (`RequiresProject => false`).

## Implementation Units

### U1. Config schema: collapse `Solutions` to a single `Solution`, add `SchemaVersion`, rename `Name` to `UniqueName`

**Goal:** `.flowline`'s config model changes from a `Solutions: HashSet<ProjectSolution>` to a single `Solution: ProjectSolution?` property with required `SchemaVersion: 1`, and `ProjectSolution.Name` is renamed to `UniqueName`. Legacy-schema and invalid-config detection uses a raw JSON pre-parse check before strongly-typed deserialization (KTD11).

**Requirements:** R5, R6 (schema shape), R13

**Dependencies:** None

**Files:**
- Modify: `src/Flowline/Config/ProjectConfig.cs`
- Modify: `src/Flowline/Config/ProjectSolution.cs`
- Modify: `tests/Flowline.Tests/ProjectConfigTests.cs`

**Approach:** Rename `ProjectSolution.Name` to `UniqueName` (same semantics); drop `NameComparer` and the `HashSet` — a single object needs no equality-by-name comparer. Replace `ProjectConfig.Solutions` with `public ProjectSolution? Solution { get; set; }` and add `public int? SchemaVersion { get; set; }`, keeping it nullable so a missing key deserializes to `null` (distinguishable from an explicit `1`) — needed for legacy detection below. `ProjectConfig.Save()` sets `SchemaVersion` to `1` before serializing whenever it isn't already set, so every save (including `provision`'s URL-only save, before any solution is cloned) writes a valid, versioned config even with `Solution` still null (KTD1b). Collapse `AddOrUpdateSolution(ProjectSolution)`, `AddOrUpdateSolution(string, bool)`, and `GetOrUpdateSolution(string?, bool?, FlowlineSettings?)` into a single-object equivalent that preserves the existing interactive overwrite-confirmation UX and the `PluginPackageMode`/`Generate` preservation behavior the current tests pin down — since there's only ever one solution now, the `uniqueName` parameter serves only the R8 mismatch-validation case, not lookup/selection. In `ProjectConfig.Load()`, parse the raw JSON with `JsonDocument` before calling `JsonSerializer.Deserialize<ProjectConfig>` (KTD11): check for `SchemaVersion == 1` and absence of a legacy `Solutions` property; when `Solution` is present, its `UniqueName` must be non-empty. A missing `SchemaVersion` or a present legacy `Solutions` property fails as legacy; a present `Solution` with an empty/missing `UniqueName` fails as invalid; a `SchemaVersion: 1` config with `Solution` entirely absent is valid (KTD1b). Any failure throws `FlowlineException(ExitCode.ConfigInvalid, ...)` naming the config path and the manual-cutover doc, replacing today's catch-and-return-`null` behavior for this specific case.

**Patterns to follow:** `ProjectConfig.cs`'s existing `GetOrUpdate*Url` methods for the interactive-confirm pattern (`ConsoleHelper.Confirm(..., settings, "config")`).

**Test scenarios:**
- Happy path: a valid v1 config (`SchemaVersion: 1`, `Solution: {UniqueName, IncludeManaged, PluginPackageMode, Generate}`) round-trips every field through JSON serialize/deserialize.
- Edge case: a `SchemaVersion: 1` config with no `Solution` at all (post-`provision`, pre-`clone`) loads successfully, not `ConfigInvalid`.
- Edge case: `Solution.UniqueName` present but empty/whitespace → `ConfigInvalid`.
- Error path: legacy `Solutions: [...]` array present (with or without `SchemaVersion`) → `ConfigInvalid`, message names the manual-cutover doc.
- Error path: `SchemaVersion` missing entirely → `ConfigInvalid`.
- Error path: `SchemaVersion` present but not `1` → `ConfigInvalid`.
- Error path: malformed/unparseable JSON → `ConfigInvalid`, not a silent null/empty config.
- Regression: `IncludeManaged` conflict on the collapsed get-or-create method still requires `--force config` (mirrors `GetOrUpdateSolution_NoName_SingleSolution_IncludeManagedDiffers_NoForce_ThrowsForceRequired`).
- Regression: `Generate`/`PluginPackageMode` survive an `IncludeManaged` update (mirrors `AddOrUpdateSolution_PreservesGenerate_WhenUpdatingIncludeManaged`, `AddOrUpdateSolution_PreservesPluginPackageMode_WhenCallerThreadsItThrough`).

**Verification:** `dotnet test tests/Flowline.Tests --filter FullyQualifiedName~ProjectConfigTests` passes; a manually-crafted legacy-shape `.flowline` fixture fails closed with `ConfigInvalid`, not a null/empty config.

---

### U2. Path consolidation: remove `AllSolutionsFolderName`, route every command through `RootFolder` directly

**Goal:** Every command that today builds `Path.Combine(RootFolder, "solutions", name)` (or the `AllSolutionsFolderName` constant) resolves paths from `RootFolder` directly, using `FlowlineCommand<T>`'s existing shared statics — no new class introduced (KTD10).

**Requirements:** R1, R4

**Dependencies:** U1

**Files:**
- Modify: `src/Flowline/Commands/FlowlineCommand.cs` (remove `AllSolutionsFolderName`)
- Modify: `src/Flowline/Commands/CloneCommand.cs`
- Modify: `src/Flowline/Commands/DeployCommand.cs`
- Modify: `src/Flowline/Commands/SyncCommand.cs`
- Modify: `src/Flowline/Commands/StatusCommand.cs`
- Modify: `src/Flowline/Commands/DriftCommand.cs`
- Modify: `src/Flowline/Commands/PushCommand.cs` (project-mode branch only)
- Modify: `src/Flowline/Commands/GenerateCommand.cs` (project-mode branch only)
- Modify: `src/Flowline/Services/DataverseContextGenerator.cs`
- Modify: `tests/Flowline.Tests/CloneCommandTests.cs`, `DeployCommandArtifactCacheTests.cs`, `DeployCommandCacheMessagingTests.cs`, `DeployCommandCiArtifactTests.cs`, `DeployCommandDtapGateTests.cs`, `DeployCommandFirstImportTests.cs`, `DeployCommandForceTests.cs`, `DeployCommandPostDeployTests.cs`, `DeployCommandSolutionManifestTests.cs`, `SyncCommandTests.cs`, `StatusCommandTests.cs`, `PushCommandTests.cs`, `GenerateCommandTests.cs`

**Approach:** Replace every `Path.Combine(RootFolder, AllSolutionsFolderName, name)` / `Path.Combine(RootFolder, "solutions", name)` with `RootFolder` directly — the `slnFolder` local variable most commands already use can simply be assigned `RootFolder`, minimizing diff noise in the method bodies below it. Before starting, grep `AllSolutionsFolderName|"solutions"` across `src/` to confirm this file list is still complete, per the caller-grep discipline this plan's own research relied on. `DataverseContextGenerator.cs` needs two related changes, not just a path-segment drop: (1) `GenerateAsync`'s `contextFilePath` (`DataverseContextGenerator.cs:23`, currently `Path.Combine(repoRootPath, "solutions", solutionName, "DATAVERSE_CONTEXT.md")`) must retarget to `Path.Combine(repoRootPath, "docs", "DATAVERSE_CONTEXT.md")` per R1/R2, not simply drop the solution-name segment and land at root; (2) `SelfHealAgentsMdAsync`'s `importLine`/`linkLine` construction (`DataverseContextGenerator.cs:47-48`, currently `@solutions/{solutionName}/DATAVERSE_CONTEXT.md` and a matching relative link) must be updated to the new `docs/DATAVERSE_CONTEXT.md` path — otherwise every `flowline sync` re-injects the stale pre-cutover path into `AGENTS.md` even after U3's clone-time template rewrite.

**Test scenarios:**
- Happy path: each command resolves `Package/`, the `Plugins`/`WebResources` projects, and `artifacts/` at `RootFolder` directly (no `solutions/<name>/` segment) — extend each command's existing tests to assert on the new paths.
- Regression: existing tests for these commands (already covering non-path behavior) continue passing unchanged.

**Verification:** `dotnet test tests/Flowline.Tests` (full suite) green; `grep -rn "AllSolutionsFolderName\|Path.Combine(RootFolder, \"solutions\"" src/` returns no hits.

---

### U3. Clone rewrite: scaffold at root, `AGENTS.md` template

**Goal:** `flowline clone` scaffolds the flat root-level layout (no `solutions/<Name>/` folder) and rewrites its own `AGENTS.md` template to describe the new layout and command surface instead of the old one.

**Requirements:** R2, R3, R7 (content-availability precondition, scoped to clone/other build-needing paths), R14

**Dependencies:** U1, U2, U4 (the `AGENTS.md` template rewrite below asserts the `--solution`/`[solution]` selector is gone, which U4 is what actually removes it)

**Files:**
- Modify: `src/Flowline/Commands/CloneCommand.cs` (`ScaffoldAgentsFileAsync`, `CloneSolutionFromDataverseAsync`, the `Directory.Move`/`File.Move` rename dance)
- Modify: `src/Flowline/Commands/DeployCommand.cs` (`ReadLocalSolutionVersion`/`ParseSolutionManifest` at `DeployCommand.cs:641-651` — wrap the unguarded `XDocument.Load` for the malformed/unreadable half of R7; the missing-file case is already handled)
- Modify: `tests/Flowline.Tests/CloneCommandTests.cs`, `DeployCommandSolutionManifestTests.cs`

**Approach:** The PAC-clone-then-rename dance (`Directory.Move`/`File.Move`, per `docs/solutions/architecture-patterns/package-subfolder-ide-layout-2026-06-04.md`) is unchanged in mechanism — only its target changes from `solutions/<Name>/` to `RootFolder`. Rewrite the `AGENTS.md` template's project-structure diagram and its "In repos with multiple solutions, pass the solution name as the first argument" line (now false — R8 removed that selector); replace with a pointer to the Multi-Solution Workflow Guidance's nested-`solutions/<Name>/`-folder pattern for the rare multi-solution case. Separately, `ReadLocalSolutionVersion` (`DeployCommand.cs:641-649`) already throws `FlowlineException(NotFound)` when `Solution.xml` is missing, but its `XDocument.Load(solutionXmlPath)` call is unguarded — a malformed or locked file raises a raw `XmlException`/`IOException` instead. Wrap that call in a try/catch that rethrows as `FlowlineException(ExitCode.ConfigInvalid, ...)` naming the path and suggesting `flowline clone`/restore-from-git, completing R7's malformed/unreadable half (the missing-file half needs no change).

**Test scenarios:**
- Happy path: fresh clone into an empty directory produces `Package/`, `<SolutionName>.sln`, `Plugins/`, `WebResources/`, `artifacts/` at root; `AGENTS.md` reflects the new layout and command surface.
- Regression: re-running clone with the same solution resumes idempotently (unchanged behavior, now at root).
- Regression: `Package/` present without `Package.cdsproj` still produces the existing targeted recovery error.
- Edge case: `AGENTS.md` is not overwritten if it already exists (existing behavior, `CloneCommand.cs:142-146`).

**Verification:** `dotnet test tests/Flowline.Tests --filter FullyQualifiedName~CloneCommandTests` passes; a real `flowline clone` against a test Dataverse solution (manual/real-org smoke, not unit-testable) produces the flat layout.

---

### U4. Command surface: remove `Deploy --solution` / `Sync [solution]`; validate `Push`/`Generate` positional against config

**Goal:** `DeployCommand`'s `--solution` flag and `SyncCommand`'s positional `[solution]` argument are removed. `PushCommand`/`GenerateCommand` keep their positional argument; in project mode a passed value is validated case-insensitively against `config.Solution.UniqueName` and errors on mismatch.

**Requirements:** R8

**Dependencies:** U1

**Files:**
- Modify: `src/Flowline/Commands/DeployCommand.cs` (Settings class — remove `--solution` option)
- Modify: `src/Flowline/Commands/SyncCommand.cs` (Settings class — remove positional `[solution]`)
- Modify: `src/Flowline/Commands/DriftCommand.cs` (Settings class — remove positional `[solution]` at `DriftCommand.cs:25-27`; `<target>` stays)
- Modify: `src/Flowline/Commands/PushCommand.cs` (project-mode validation branch)
- Modify: `src/Flowline/Commands/GenerateCommand.cs` (project-mode validation branch, the `Config!.GetOrUpdateSolution(settings.Solution, ...)` call site at `GenerateCommand.cs:128-130`)
- Modify: `tests/Flowline.Tests/PushCommandTests.cs`, `GenerateCommandTests.cs`, `SyncCommandTests.cs`, and the `DeployCommand*Tests.cs` family

**Approach:** Deploy/Sync/Drift's removal is a pure CLI-surface deletion — no replacement logic needed. Push/Generate's project-mode branch changes from "select among configured solutions by name" to "if a name was passed, compare it case-insensitively to `config.Solution.UniqueName`; mismatch throws `FlowlineException(ExitCode.ValidationFailed, ...)`".

**Test scenarios:**
- Happy path: `flowline push` / `flowline generate` with no positional argument in project mode uses the configured solution.
- Happy path: passing the correct `UniqueName` case-insensitively as the positional argument succeeds.
- Error path: passing a mismatched name in project mode fails with a clear validation error before any Dataverse call.
- Regression: `flowline deploy --help` / `flowline sync --help` / `flowline drift --help` no longer list a solution selector.
- Regression: standalone `push`/`generate` (no `.flowline`) still require and use their positional argument exactly as today.

**Verification:** `dotnet test tests/Flowline.Tests --filter FullyQualifiedName~PushCommandTests|GenerateCommandTests|SyncCommandTests|DeployCommand` passes; CLI help text reviewed manually for the removed options.

---

### U5. `StatusCommand` single row; `InvocationLogger` reads `UniqueName` directly

**Goal:** `StatusCommand` keeps its existing grid-rendering mechanism, now always producing one row. `InvocationLogger` reads `config.Solution.UniqueName` directly instead of joining a collection.

**Requirements:** R6 (`InvocationLogger` identity), R7 (`InvocationLogger` fail-safe telemetry), R9

**Dependencies:** U1

**Files:**
- Modify: `src/Flowline/Commands/StatusCommand.cs` (`solutions = config.Solutions.ToList()` → single-item construction from `config.Solution`)
- Modify: `src/Flowline/Commands/InvocationLogger.cs` (`cfg.Solutions.Select(s => s.Name)` → `cfg.Solution?.UniqueName`)
- Modify: `tests/Flowline.Tests/StatusCommandTests.cs`, `StatusGridTests.cs`
- New: `tests/Flowline.Tests/InvocationLoggerTests.cs` (no existing coverage found for this class)

**Approach:** `StatusGrid.BuildGridRows` is unaffected (already handles any row count) — only the caller-side collection-to-single-item adaptation in `StatusCommand.cs` changes.

**Test scenarios:**
- Happy path: `flowline status` renders exactly one row for the configured solution.
- Happy path: invocation logging records `config.Solution.UniqueName` when a `.flowline` project is present.
- Edge case: invocation logging omits the solution tag (not a placeholder/empty string) when no `.flowline` exists yet (fresh clone / standalone), matching R7's "telemetry never makes execution fail" contract.

**Verification:** `dotnet test tests/Flowline.Tests --filter FullyQualifiedName~StatusCommandTests|StatusGridTests|InvocationLoggerTests` passes.

---

### U6. Deploy source scope: git-clean guard and cache key scoped to deployment inputs

**Goal:** `DeployCommand`'s git-clean guard (`ValidateGitCleanAsync`) and artifact-cache source key (`GitUtils.GetLastCommitShaForPathAsync`) use a shared, explicit deployment-input path list instead of the whole flattened root — so `docs/`, `tests/`, `CHANGES.md`, and agent instruction files never block a deploy or invalidate a reusable artifact. Ships a minimal explicit list now (KTD12); the sibling multi-plugin-project-support plan's `.sln`-membership discovery replaces the placeholder list later.

**Requirements:** R15

**Dependencies:** U2

**Files:**
- Modify: `src/Flowline/Commands/DeployCommand.cs` (`ValidateGitCleanAsync` at `DeployCommand.cs:339`, the cache-key call at `DeployCommand.cs:94`)
- Modify: `src/Flowline/Utils/GitUtils.cs` (`GetUncommittedChangesInPathAsync`/`GetLastCommitShaForPathAsync` at `GitUtils.cs:167,181` — both are single-`path` today; the deployment-input scope is multiple sibling paths)
- Modify: `tests/Flowline.Tests/DeployCommandArtifactCacheTests.cs`, `DeployCommandCacheMessagingTests.cs`, `GitUtilsTests.cs`

**Approach:** Both call sites currently pass `slnFolder`. Introduce one shared method (not a new class, per KTD10's same anti-over-abstraction posture) that returns the explicit deployment-input path list; both the clean-check and the cache-key calculation call it, so their scope cannot diverge (R15's own requirement). `GetUncommittedChangesInPathAsync`/`GetLastCommitShaForPathAsync` each take exactly one `path` today and cannot express a multi-path scope as-is — extend both to accept `IEnumerable<string>` (git natively supports multiple pathspecs in one `git status -- path1 path2`/`git log -- path1 path2` invocation, so this is one process call, not a per-path loop); `GetLastCommitShaForPathAsync`'s multi-path form returns the single most-recent commit SHA touching any of the given paths, so the cache key still resolves to one value.

**Test scenarios:**
- Happy path: uncommitted changes under `Package/` or a plugin project block deploy; the same change invalidates the artifact cache.
- Regression: uncommitted changes only under `docs/`, `tests/`, `CHANGES.md`, or `AGENTS.md`/`CLAUDE.md` block neither.
- Regression: existing artifact-cache and clean-guard test scenarios pass with the new scope.

**Verification:** `dotnet test tests/Flowline.Tests --filter FullyQualifiedName~DeployCommandArtifactCacheTests|DeployCommandCacheMessagingTests|GitUtilsTests` passes.

---

### U7. Documentation: `folder-structure.md`, `CLAUDE.md`, wiki pages

**Goal:** `docs/folder-structure.md`, the repo's own `CLAUDE.md`, and the ten affected GitHub Wiki pages describe the root-level layout, the new command surface, and the manual pre-v1 cutover steps — replacing every `solutions/<Name>/` reference.

**Requirements:** R10, R11, R12

**Dependencies:** U1, U2, U3, U4, U5, U6 (documents the final shipped behavior)

**Files:**
- Modify: `docs/folder-structure.md`
- Modify: `CLAUDE.md` (its "Folder structure when a user uses Flowline" section)
- Modify: `Flowline.wiki/01-Getting-Started.md`, `03-Command-Reference.md`, `06-Sync.md`, `07-Deploy.md`, `09-Generate-Early-Bound-Types.md`, `10-AI-Agents.md`, `13-Migration-from-spkl.md`, `14-Migration-from-Daxif.md`, `15-Migration-from-ALM-Accelerator.md`, `16-Migration-from-PACX.md`

**Approach:** `docs/folder-structure.md` is the canonical spec — rewrite its folder hierarchy overview, component breakdown, and design-principles sections to the flat layout, and add the manual cutover steps (R10). Propagate the same layout description to `CLAUDE.md` and each wiki page, plus `03-Command-Reference.md`'s removed-flag documentation (R8/R11) and the multi-row `status` example (R9).

**Test scenarios:** Test expectation: none — documentation only, no behavior change.

**Verification:** `grep -rn "solutions/<" docs/folder-structure.md CLAUDE.md` and the equivalent sweep across `Flowline.wiki/` return no stale references.

## Verification Contract

| Command | Applicability | Gate |
|---|---|---|
| `dotnet build` (full solution) | All units | Builds cleanly, no new warnings |
| `dotnet test tests/Flowline.Tests` | All units | Full suite green, including new/updated scenarios per unit |
| `grep -rn "AllSolutionsFolderName\|Path.Combine(RootFolder, \"solutions\"" src/` | U2 | No hits |
| `grep -rn "solutions/<" docs/folder-structure.md CLAUDE.md` + wiki sweep | U7 | No hits |
| Real-org smoke: `flowline clone` → `push` → `sync` → `deploy` → `status` → `drift` against a test Dataverse solution | U1-U6 | Full cycle succeeds against the flat layout (mirrors STRATEGY.md's existing "tested against real org" milestone pattern) |

## Definition of Done

**Global:**
- All 15 requirements (R1-R15) satisfied and traceable to at least one Implementation Unit.
- `dotnet test tests/Flowline.Tests` passes in full.
- `docs/folder-structure.md`, `CLAUDE.md`, and the ten named wiki pages updated (U7); no stale `solutions/<Name>/` references remain in any of them.
- No dead code remains from the old `Solutions` HashSet/array model — `AddOrUpdateSolution`/`GetOrUpdateSolution`'s old multi-arg surface is fully replaced, not left alongside the new single-object API.
- The two known existing repos (ROVM, MyFlowTest) are hand-migrated by the maintainer — outside this plan's scope, but noted here since it's a real precondition for those repos to keep working post-cutover.

**Per-unit:** each unit's own **Verification** field is satisfied before it is considered complete.
