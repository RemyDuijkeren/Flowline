---
title: Single-Solution Project Layout - Plan
type: refactor
date: 2026-07-17
topic: single-solution-project-layout
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Single-Solution Project Layout - Plan

## Goal Capsule

- **Objective:** Collapse Flowline's project structure from a `solutions/<Name>/`-wrapped, array-based multi-solution model to exactly one Dataverse solution per project root — `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, the `.sln`, and `.flowline` all live directly at the project root.
- **Product authority:** Direct maintainer dialogue (`ce-brainstorm`), grounded in inspection of two real Flowline projects (a client engagement, a personal test project) and verification against the current source (`FlowlineCommand.cs`, `ProjectConfig.cs`, `ProjectSolution.cs`, `InvocationLogger.cs`, `GitUtils.cs`, `DeployCommand.cs`, `SyncCommand.cs`, `StatusCommand.cs`, `DataverseContextGenerator.cs`, `PushCommand.cs`, `GenerateCommand.cs`, `CloneCommand.cs`).
- **Open blockers:** None.

## Product Contract

### Summary

Flowline moves to exactly one Dataverse solution per project root. `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, the `.sln`, and `.flowline` live directly at the root instead of under a `solutions/<Name>/` wrapper. `.flowline`'s schema drops the `Solutions` array in favor of a single versioned `Solution` object and renames its required `Name` field to `UniqueName`. In project mode, `.flowline` supplies the expected project identity; the unpacked package or an explicitly supplied artifact supplies an observed content identity that must match it. Fresh `clone` and standalone commands use their required positional solution argument because no project identity exists yet. This lands as part of the push to v1.0, as a hard cutover with no back-compat execution layer and no automated migration tooling, but with explicit legacy-schema detection and an actionable manual-migration error.

### Problem Frame

The `solutions/<Name>/` wrapper was added anticipating other solution-independent folders that never materialized. Inspection of the two real Flowline projects in active use shows exactly one solution folder each, with nothing else living at that level. The wrapper's cost is real and ongoing: every command that resolves project paths (`deploy`, `sync`, `status`, `generate`, project-mode `push`) builds them through an extra directory level and collection-selection/path-routing logic, backed by a `Solutions[]` array config shape that no real project has ever populated with more than one entry.

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

### Key Decisions

- **Lands before v1.0, date moves.** Treated as a pre-release hard cutover, not a post-1.0 breaking change requiring a stability-protecting migration story.
- **No automated migration or back-compat execution path — old layout dropped entirely, but detected explicitly.** The two known existing repos are hand-migrated or recreated by the maintainer directly. Flowline's commands do not execute against the old `solutions/<Name>/` layout or array-form `.flowline`. The config loader does detect the legacy `Solutions` property/schema version and fails with a clear `FlowlineException` that points to documented manual-migration steps; relying on JSON deserialization or a later missing-path/null failure is not acceptable because the current loader can otherwise turn an unreadable or semantically obsolete config into an apparently blank project.
- **`.flowline`'s `Solution` config becomes a single object, not an array.** The array's only proven use — `StatusCommand`'s multi-row grid — belongs conceptually to a future, separate umbrella command that would discover multiple `.flowline` files, not to this file's own schema.
- **The new `.flowline` schema is explicitly versioned from its first release.** `SchemaVersion: 1` distinguishes the supported single-solution shape from the legacy unversioned `Solutions[]` shape and gives future breaking config changes a reliable validation/migration boundary.
- **`Solution.Name` is retained as project identity but renamed to required `Solution.UniqueName`.** Removing it would make `.flowline` cease to be self-describing and would leave artifact-only CI deployment with no independent project binding: if `Package/` is absent, any supplied zip would otherwise be allowed to define which solution Flowline deploys. `UniqueName` instead records the expected project identity, while local or artifact `Solution.xml` records observed content identity. Flowline validates them case-insensitively and fails closed on mismatch. This is safer than removing the field, lets `status`, diagnostics, and pre-execution telemetry identify the intended project before package-source validation, and uses Dataverse's precise vocabulary; operations that genuinely require package source still fail when it is absent. The duplicated value is an intentional invariant, not an uncontrolled second source of truth: `.flowline` says what the project is allowed to operate on; `Solution.xml` proves what the current content actually is.
- **Solution identity resolution and validation are context-sensitive and centralized.** In normal project mode, required `.flowline Solution.UniqueName` is the expected identity. Commands that consume local source validate `Package/src/Other/Solution.xml` against it; `deploy --path` validates the artifact's `Other/Solution.xml` against it even when local `Package/` is absent. Fresh `clone` validates its required `<solution>` argument remotely and writes the canonical returned unique name into `.flowline`; standalone `push`/`generate` continue using their required positional argument because no project exists. `InvocationLogger` uses configured `Solution.UniqueName` in project mode and remains best-effort for fresh clone/standalone execution, so telemetry never becomes a command precondition.
- **Repeated clone behavior is explicit.** Re-running `flowline clone <same-name>` in an initialized project is an idempotent resume/repair path. Running `flowline clone <different-name>` in an initialized project fails before `.flowline` is saved or any `.sln`/project files are created or changed. Existing incomplete `Package/` state produces a targeted recovery error.
- **`DeployCommand`'s `--solution` flag and `SyncCommand`'s positional `[solution]` argument are removed.** There is only ever one configured solution, so per-invocation selection is unnecessary. `Push`/`Generate`/`Clone`'s positional `[solution]` arguments stay (see R8) — standalone mode needs them, and project mode now validates a passed value against the single configured solution instead of selecting among several.
- **`StatusCommand` keeps its existing grid-rendering mechanism unchanged.** It now always renders exactly one row; no redesign to a non-grid layout.
- **The `.sln` file stays named after the Dataverse solution's unique name**, unchanged from today's `clone` logic — just written to the project root instead of a subfolder.
- **`Package/`, `Plugins/`, and `WebResources/` keep their current names, unchanged by the relocation.** Renaming `Package` was considered — its meaning was partly carried by the enclosing `solutions/<Name>/` folder, which this plan removes — but rejected as separate, disproportionate scope: it's an established term (`CONCEPTS.md`, the `PackageName` constant, the milestone history, both real existing repos) whose disambiguation job moves to the sibling `.sln` file's name rather than disappearing.
- **No `src/` wrapper — the layout stays flat, even with multi-plugin-project support.** Considered nesting `Plugins*/`/`WebResources/` under `src/`, paired with `tests/`, matching common dotnet convention (David Fowler's widely-cited `.NET project structure` reference; Microsoft's own "Organizing and testing projects with the .NET CLI" tutorial both recommend `src/`+`tests/`). The deciding factor was project count, not convention-following for its own sake: the multi-plugin-project-support plan (`docs/plans/2026-07-15-001-...`) means a solution *can* have more than one plugin project, but in practice — based on the maintainer's experience with Dataverse projects generally, not a measured statistic — the common case stays at `Package/`, `Plugins/`, `WebResources/` (+ maybe `Plugins.Tests/`), and the rare larger case adds only one or two more plugin projects — never the dozens-of-projects scale (e.g. ASP.NET Core's own repo) where `src/`/`tests/`/`tools/` grouping actually earns its keep. Even the outlier case tops out around 5-6 root folders, well within what a flat listing scans fine without a container. Both the Fowler reference and Microsoft's own tutorial explicitly carve out small/few-project scenarios as legitimate exceptions to the `src/` convention; Dapper (a widely-used, well-regarded minimal library) skips `src/`/`test/` entirely for the same reason. Flowline's realistic project-count distribution matches that exception, not the large-repo case the convention exists for.
- **The same-environment, multiple-solution case is solved with documentation, not new tooling.** See Multi-Solution Workflow Guidance below for the two supported patterns.
- **Project paths and identity are represented once, not reconstructed independently by every command.** The implementation introduces a central project-layout/identity abstraction (for example, `FlowlineProjectLayout`) exposing the project root, `Package/`, `artifacts/`, solution file discovery, and identity resolution. Commands consume it rather than replacing each old `solutions/<Name>/` expression with a new collection of root-relative literals.
- **Deploy source scope remains intentional after flattening.** The git-clean guard and artifact-cache source key are based on deployment-affecting inputs, not blindly on the entire project root. Documentation, tests, `CHANGES.md`, and other non-packaging files do not block a deploy or invalidate a reusable artifact merely because they now share the root with solution projects. Packaging-affecting `.flowline` settings and projects discovered through `.sln` membership remain in scope.

### Multi-Solution Workflow Guidance

This plan removes Flowline's only built-in multi-solution capability — the `.flowline` `Solutions[]` array and `StatusCommand`'s multi-row grid. Microsoft's own Power Platform ALM guidance still recognizes a legitimate case this leaves unaddressed: multiple solutions in the same development environment, for "distinct and independent functional areas that don't share components." Two patterns cover it, documentation only, no new Flowline tooling:

- **Separate repo per solution.** Each solution gets its own repo, its own `.flowline`, its own git history. The default answer, no caveats.
- **A `solutions/<Name>/` folder inside one repo, containing multiple independent, self-contained Flowline projects** — each with its own `.flowline`, `Package/`, `Plugins*/`, `WebResources/` at its own root, one level under `solutions/`. This already works with zero code changes: `FindProjectRoot` (`FlowlineCommand.cs:62-72`) walks upward from the current directory to the nearest `.flowline`, and `CloneCommand` (`CloneCommand.cs:46`) has no requirement that its target folder be fresh or empty — running `flowline clone` from inside `solutions/SolutionB/` just works, unaware of any sibling. Shared git history and shared `AGENTS.md`/`CLAUDE.md` context are the benefit over a separate repo.

**Known limitation, both patterns:** neither shares environment config (`ProdUrl`/`DevUrl`/auth) across sibling `.flowline` files — each carries its own copy. Solved only by the deferred umbrella `.flowline` (see Scope Boundaries), not by this plan.

### Requirements

**Folder layout**
- R1. `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, and `CHANGES.md` live directly at a Flowline project's root, flat, with no `src/` wrapper and no `solutions/<Name>/` folder. `CHANGES.md` follows the near-universal `CHANGELOG.md`-at-root convention (matching this repo's own `CHANGELOG.md`), unlike `DATAVERSE_CONTEXT.md`, which moves into `docs/` — it's domain/schema reference material, not a changelog.
- R2. `tests/` and `docs/` are recognized root-level folders in the documented layout, but neither is scaffolded with placeholder content by `flowline clone` — creating and populating them is left to the user, whenever they're needed. Exception: `clone` still creates `docs/` as needed to write `DATAVERSE_CONTEXT.md` (R1), same as it does today for the old location — this isn't scaffolding a convention, just the existing generation behavior landing at its new path.
- R3. `flowline clone` scaffolds the root-level layout for freshly cloned projects, including its own `AGENTS.md` template content (`CloneCommand.cs:139-228`), which today hardcodes the old `solutions/{{solutionName}}/` structure diagram and multi-solution positional-argument guidance (`CloneCommand.cs:181,188,221,223`) — this is rewritten to match the new layout and command surface (R8).
- R4. Every command that today constructs paths through `solutions/<Name>/` (`clone`, `deploy`, `sync`, `status`, project-mode `generate`, project-mode `push`, `drift`, `DataverseContextGenerator`) resolves paths from the project root directly instead. These paths are exposed through one central project-layout abstraction; commands do not each grow their own `Path.Combine(RootFolder, ...)` implementation.

**Configuration schema**
- R5. `.flowline`'s config adds required `SchemaVersion: 1` and replaces the `Solutions` array with a single `Solution` object. The former required `Name` becomes required, non-empty `UniqueName`; `IncludeManaged`, `PluginPackageMode`, and `Generate` (including all of `GenerateConfig`'s optional fields) keep their current shape and defaults within it. `PluginPackageMode` is explicitly retained; it must not be lost merely because the former collection type is removed.
- R6. A central solution-identity service distinguishes **expected project identity** from **observed content identity**:
  - normal project mode: `.flowline Solution.UniqueName` is expected identity;
  - local-source commands: `Package/src/Other/Solution.xml` supplies observed identity and must match the expected identity case-insensitively;
  - `deploy --path`: the supplied zip's `Other/Solution.xml` supplies observed identity and must match the expected identity, whether or not local `Package/` exists;
  - fresh `clone`: the required `<solution>` argument is validated against Dataverse and the canonical returned unique name is written to `.flowline` only after successful validation;
  - standalone `push`/`generate`: their positional solution argument remains authoritative because no `.flowline` project exists.
  A passed project-mode `push`/`generate` solution argument is validated case-insensitively against configured `Solution.UniqueName` and errors on mismatch.
- R7. When a command requires local `Package/src/Other/Solution.xml` and it is missing, malformed, unreadable, has no non-empty `SolutionManifest/UniqueName`, or does not match `.flowline Solution.UniqueName`, the command fails with a clear `FlowlineException` naming the path and expected/observed values where available, and suggesting a fix (e.g. restore `Package/` from git, or re-run `flowline clone`) — not a raw, unhandled exception. Explicit-artifact deploy applies equivalent structural and identity validation to the zip entry. `InvocationLogger` uses configured `Solution.UniqueName` in project mode; for fresh clone and standalone execution it logs a supplied/resolved name when available and otherwise omits the tag, so telemetry never makes command execution fail.

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

Keeping `UniqueName` is deliberately safer than removing solution identity from config:

- **Self-describing project:** `.flowline` alone states which Dataverse solution the project represents; `status`, diagnostics, and telemetry do not need to infer basic identity from an optional or temporarily unavailable working tree.
- **Artifact binding:** cross-job `deploy --path` can prove that the supplied zip belongs to this project even when the checkout contains `.flowline` but not `Package/`. Without `UniqueName`, the artifact would choose the deployment identity itself, so accidentally passing another solution's valid zip would not be detected.
- **Expected versus observed validation:** duplication is controlled by assigning distinct roles. Config is the expected identity; local/artifact manifests are observed identity. A mismatch is a validation failure and is never auto-reconciled silently.
- **Stable domain identifier:** Dataverse solution unique name is the correct durable identifier, and naming the property `UniqueName` avoids ambiguity with display names or filesystem names.
- **External precedent:** PACX, an adjacent Dataverse CLI, also defines a project as the nearest directory marker containing one explicit default solution name. Flowline keeps the useful explicit binding while improving safety: unlike PACX's permissive fallback when a project file cannot be loaded, Flowline rejects invalid project configuration and identity mismatches.

**Command surface**
- R8. `DeployCommand`'s `--solution` flag and `SyncCommand`'s positional `[solution]` argument are removed outright — both are pure project-mode selectors with no other use. `PushCommand`'s and `GenerateCommand`'s positional `[solution]` argument stays — standalone mode requires it as its only source of the target Dataverse solution name (`PushCommand.cs:434-440`, `GenerateCommand.cs:118`), since standalone mode has no `.flowline` to read one from. In project mode, a passed `[solution]` value is now validated against the single configured solution (error on mismatch) rather than used to select among several. `CloneCommand`'s positional `<solution>` argument is unrelated (it names the new solution to clone, not a selector among configured ones) and stays unchanged, as do `DeployCommand`/`DriftCommand`'s `<target>` and `ProvisionCommand`'s `[role]` (deploy-target/environment-role, not solution selectors).
- R9. `StatusCommand` renders its existing grid output unchanged in mechanism, producing a single row.

**Documentation**
- R10. `docs/folder-structure.md` and the repo's own `CLAUDE.md` (its "Folder structure when a user uses Flowline" section — the diagram and the "never at the repo root" / "sibling folders under `solutions/`" rules) are rewritten to describe the root-level layout, replacing the `solutions/<Name>/` description. The folder-structure documentation includes concise manual cutover steps for moving the contents of a single legacy `solutions/<Name>/` directory to the project root, converting `.flowline` to `SchemaVersion: 1`, and renaming the retained single solution's `Name` to `UniqueName`; this is documentation, not an automated migration feature.
- R11. Affected GitHub Wiki pages — verified by content, not assumed: `01-Getting-Started.md`, `03-Command-Reference.md`, `06-Sync.md`, `07-Deploy.md`, `09-Generate-Early-Bound-Types.md`, `10-AI-Agents.md`, `13-Migration-from-spkl.md`, `14-Migration-from-Daxif.md`, `15-Migration-from-ALM-Accelerator.md`, `16-Migration-from-PACX.md` — are updated in the same body of work. `01-Getting-Started.md` links to or contains the manual pre-v1 layout cutover note. `03-Command-Reference.md` documents the exact `--solution`/`[solution]` surface R8 removes, the multi-row `status` example R9 supersedes, and the expected-versus-observed identity contract for normal, standalone, clone, and explicit-artifact modes.
- R12. Documentation describes both supported patterns for the same-environment, multiple-solution case — separate repo, or a nested `solutions/<Name>/` folder of independent projects — including the limitation that neither pattern shares environment config across sibling `.flowline` files.

**Lifecycle and safety**
- R13. Config loading distinguishes four states: no `.flowline` exists; valid `SchemaVersion: 1`; syntactically/semantically invalid v1; and unsupported/legacy schema. Valid v1 requires a non-null `Solution` with non-empty `UniqueName`. An existing invalid or legacy file is never converted into `new ProjectConfig()` and silently treated as an empty project. Legacy `Solutions`/`Name`, a missing or empty `UniqueName`, or a missing/unsupported schema version produces `FlowlineException(ExitCode.ConfigInvalid, ...)` with the detected config path and manual-cutover documentation reference.
- R14. Before `clone` saves `.flowline` or writes project files, it resolves three possible identities: the requested argument, existing configured `Solution.UniqueName`, and any existing local `Package/src/Other/Solution.xml`. Every present value must match case-insensitively. A matching initialized project permits the existing idempotent resume behavior; any mismatch fails with `ExitCode.ValidationFailed` and names the conflicting values. An existing `Package/` without a readable manifest or `Package.cdsproj` fails with a targeted incomplete-project recovery message. For a fresh project, clone validates the requested solution against Dataverse, then saves the canonical returned unique name only after that validation succeeds.
- R15. `deploy --path` reads unique name, version, and managed state from the artifact manifest in one parse. The artifact unique name must always match configured `.flowline Solution.UniqueName` case-insensitively, including artifact-only cross-job CI where local `Package/` is absent. If a valid local package manifest also exists, it independently must match the same configured identity. Any mismatch fails before contacting or mutating the target environment. The artifact's managed state continues to be validated against `Solution.IncludeManaged`.
- R16. Flattening does not automatically redefine the deploy git-clean guard or reusable-artifact cache key as "everything below project root." A central deployment-input scope includes `Package/`, packaging/build project files discovered from the `.sln`, and configuration values that affect produced artifacts; it excludes `docs/`, `tests/`, `CHANGES.md`, agent instruction files, and other non-packaging material. Both the clean check and source revision/cache calculation use this same scope so their behavior cannot diverge.

## Acceptance and Verification

The implementation is not complete until automated tests cover the following contract. Pure path/config/identity behavior is unit-tested without PAC or Dataverse; existing command seams or focused integration tests cover command routing where practical.

| Area | Required scenarios |
|---|---|
| Config schema | Valid v1 round-trip preserves required `UniqueName`, `IncludeManaged`, `PluginPackageMode`, and every `Generate` field; missing/empty `UniqueName`, unsupported `SchemaVersion`, legacy `Solutions[]`/`Name`, malformed JSON, null `Solution`, and invalid enum values fail as `ConfigInvalid` without falling back to an empty config. |
| Identity resolver | Configured expected identity is available without local source; a valid local manifest with the same `UniqueName` passes; missing, malformed, unreadable, empty-name, or mismatching local manifest produces the R7 error; comparisons are case-insensitive. |
| Fresh/repeated clone | Fresh clone works before `.flowline` or `Package/` exists and persists the remotely validated canonical unique name; repeating the same solution resumes idempotently; request/config/manifest mismatch is rejected before config or file mutation; incomplete `Package/` state produces the recovery error. |
| Command modes | Normal project commands use configured expected identity and validate observed source identity where applicable; standalone `push`/`generate` continue using their argument; project-mode arguments are validated against config; project telemetry uses configured `UniqueName`; telemetry does not fail when identity is not yet available in fresh clone/standalone mode. |
| Explicit artifact deploy | Artifact deployment works without local `Package/` but still requires `.flowline Solution.UniqueName`; malformed/missing artifact manifest fails clearly; artifact/config or local-package/config mismatch fails before target access; matching names and managed-mode validation continue normally. |
| Project discovery | Commands invoked from project subdirectories resolve the nearest `.flowline`; two sibling independent projects under an umbrella `solutions/` directory resolve their own nearest roots and do not see each other's config. |
| Layout paths | Clone, sync, deploy, status, drift, push, generate, context generation, solution build, artifacts, and `CHANGES.md` all use the root-level layout through the central abstraction; no production command retains hardcoded `solutions/<Name>` path construction. |
| Deploy source scope | Changes under deployment inputs block deploy/invalidate cache; changes only under `docs/`, `tests/`, `CHANGES.md`, or agent files do neither; the clean guard and cache use identical scope membership. |
| CLI surface | `deploy --solution` and `sync [solution]` are rejected/absent from help; clone and standalone positional arguments remain; status renders exactly one row. |

### Scope Boundaries

**Deferred for later**
- An umbrella/workspace-level `.flowline` that discovers and aggregates across nested `.flowline` files (e.g., a hypothetical `flowline status --all`) — explicitly parked, not designed as part of this work.
- Any tooling that specifically detects or assists the nested `solutions/<Name>/` multi-project pattern (in `flowline clone` or elsewhere) — it already works via existing upward directory search and stays a documented convention, not a built feature.

**Outside this work**
- Automated migration tooling or execution compatibility for repos on the old layout. Legacy detection, a clear error, and manual cutover documentation are required by R10/R11/R13 and are not considered migration tooling.
- Any user-visible behavior change to standalone-mode `push`/`generate` — verified otherwise unaffected; standalone mode never touches `solutions/<Name>/` path construction or `.flowline` (`PushCommand.cs:283`, `PushCommand.cs:354`, `GenerateCommand.cs:356`). The shared identity resolver merely formalizes their existing positional argument as the authoritative source in standalone mode.

### Dependencies / Assumptions

- Assumes the only real-world Flowline projects affected today are the two inspected repos; both are hand-migrated by the maintainer outside this plan's scope.
- Assumes v1.0's milestone date (`STRATEGY.md`, currently 2026-07-20) moves to accommodate this work landing first.
- This plan lands before `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md`, which also edits `docs/folder-structure.md` (its KD5/R8 relaxes the fixed `Plugins/` folder-name assumption via `.sln`-membership discovery). That plan rebases onto this one's root-level layout rather than the two landing independently.
- The deployment-input scope in R16 must use the same `.sln`-membership discovery introduced or finalized by the multi-plugin-project-support work; do not freeze a hardcoded `Plugins/`-only cache/clean scope that immediately becomes stale when that plan lands.

### Sources / Research

- Two real Flowline projects inspected on disk — a client engagement project and a personal test project — each holding exactly one solution folder under `solutions/`, with nothing else at that level.
- Current `solutions/<Name>/` path construction sites: `FlowlineCommand.cs:25` (`AllSolutionsFolderName` constant), `CloneCommand.cs:63,339` (including the actual folder-creation call), `DriftCommand.cs:41`, `DeployCommand.cs:68`, `SyncCommand.cs:64`, `StatusCommand.cs:146,165`, `DataverseContextGenerator.cs:23`, `PushCommand.cs:285,356`, `GenerateCommand.cs:166`. Note: some sites reference the `AllSolutionsFolderName` constant, others hardcode the literal `"solutions"` string directly — an existing inconsistency ce-plan should reconcile while touching these call sites anyway.
- `.flowline`'s current array schema and its consumers: `StatusCommand.cs:74` (`config.Solutions.ToList()`) is the one functional consumer of the array's multiplicity; `InvocationLogger.cs:33` incidentally joins solution names for a telemetry tag at any count.
- Command lifecycle conflict behind context-sensitive identity: `FlowlineCommand.cs:97-99` loads config and invokes `InvocationLogger` before `ExecuteFlowlineAsync`; `CloneCommand.cs:46` explicitly permits execution without a project/package; standalone `push`/`generate` likewise obtain identity from their arguments.
- Current config failure behavior motivating R13: `ProjectConfig.Load` (`ProjectConfig.cs:248-264`) catches any deserialization/read exception and returns `null`, while `FlowlineCommand.cs:97` replaces `null` with `new ProjectConfig()`; legacy/invalid config therefore needs an explicit fail-closed distinction from an absent file.
- Existing config field that R5 must preserve: `ProjectSolution.cs:23-27` defines `IncludeManaged`, `PluginPackageMode`, and nullable `Generate`.
- Existing project identity field renamed rather than removed by R5/R6: `ProjectSolution.cs:22` defines `Name`; the new schema calls it `UniqueName` to match Dataverse terminology and assigns it the explicit expected-identity role.
- Existing clone idempotency seam behind R14: `CloneCommand.cs:322-331` skips the Dataverse clone whenever `Package.cdsproj` already exists without first comparing its manifest identity to the requested clone argument; config is currently saved earlier at `CloneCommand.cs:60`.
- Explicit-artifact identity source behind R15: `DeployCommand.cs:69-90` treats `--path` as an artifact that need not come from the local tree and already reads its `Other/Solution.xml` for version/managed state; the same parse can return the observed `UniqueName` for validation against configured expected identity.
- Deploy scope widening behind R16: `DeployCommand.cs:338-345` scopes the dirty check to `slnFolder`, and `DeployCommand.cs:94` passes that same folder to `GitUtils.GetLastCommitShaForPathAsync`; blindly redefining `slnFolder` as the project root would make unrelated root files affect safety and cache behavior.
- PACX comparison, public wiki `pacx-project-init` (`github.com/neronotte/Greg.Xrm.Command/wiki/pacx-project-init`) — an adjacent Dataverse CLI defines a project as a nearest-directory `.pacxproj` marker with one explicit default solution name and authentication profile, inherited by child directories. This corroborates one solution binding per project marker while not prescribing Flowline's physical source layout.
- PACX implementation inspected at `E:\Code\Greg.Xrm.Command`: `PacxProjectDefinition.cs:8-13` stores version, suspend state, auth profile, and one `SolutionName`; `FolderTree.cs:26-46` and `PacxProjectRepository.cs:11-18` resolve the nearest parent marker; `InitProjectCommandExecutor.cs:58-80` validates the connection and remote solution before saving. PACX's explicit solution binding motivated retaining `Solution.UniqueName`; its loader's catch-and-return-null behavior (`PacxProjectRepository.cs:20-45`) also reinforces Flowline R13's stricter fail-closed contract rather than serving as a behavior to copy.
- Standalone-mode isolation, confirming this plan doesn't touch it: `PushCommand.cs:83,113,283,354`, `GenerateCommand.cs:130,356`.
- Project-root discovery mechanism underpinning the nested-folder multi-solution pattern: `FlowlineCommand.cs:62-72` (`FindProjectRoot`), `CloneCommand.cs:46` (`RequiresProject => false`).
- PACX (Greg.Xrm.Command, a direct competitor) independently validates the nested multi-solution pattern: `pacx project init` creates a `.pacxproj` local settings-override file discovered via the same upward-directory-search mechanism (`PacxProjectRepository.cs:13`, `RecurseBackFolderContainingFile`), and its own documentation names this exact scenario a "segmented solution approach" (`Greg.Xrm.Command.Core/Commands/Projects/InitProjectCommand.cs:26`). Unlike `.flowline`, `.pacxproj` is a non-gating convenience file — every PACX command works with none present — which is why R13's fail-closed design is a deliberate divergence, not an oversight: `.flowline` has no equivalent global-fallback config model to degrade to.
- Microsoft Learn, "Organize your solutions in Power Platform" (`learn.microsoft.com/en-us/power-platform/alm/organize-solutions`) — single-solution and same-environment-multiple-solution strategy tiers.
- David Fowler, ".NET project structure" (`gist.github.com/davidfowl/ed7564297c61fe9ab814`) — the widely-cited `src`/`tests`/`docs`/`artifacts` convention, including its own explicit exception for single/few-project scenarios.
- Microsoft Learn, "Organizing and testing projects with the .NET CLI" (`learn.microsoft.com/en-us/dotnet/core/tutorials/testing-with-cli`) — official `src`/`test` tutorial, itself noting the split is a preference some developers skip (`dotnet/docs#26395`).
- Dapper (widely-used, well-regarded minimal library) — real-world example with no `src`/`test` folder split, cited as precedent for the flat choice at Flowline's realistic project-count scale.
