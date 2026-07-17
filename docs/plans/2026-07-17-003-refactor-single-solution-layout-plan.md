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
- **Product authority:** Direct maintainer dialogue (`ce-brainstorm`), grounded in inspection of two real Flowline projects (a client engagement, a personal test project) and verification against the current source (`FlowlineCommand.cs`, `DeployCommand.cs`, `SyncCommand.cs`, `StatusCommand.cs`, `DataverseContextGenerator.cs`, `PushCommand.cs`, `GenerateCommand.cs`, `CloneCommand.cs`).
- **Open blockers:** None.

## Product Contract

### Summary

Flowline moves to exactly one Dataverse solution per project root. `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, the `.sln`, and `.flowline` live directly at the root instead of under a `solutions/<Name>/` wrapper. `.flowline`'s schema drops the `Solutions` array and the `Name` field in favor of a single `Solution` object, with solution identity always read from the packed solution's own `Solution.xml`. This lands as part of the push to v1.0, as a hard cutover with no back-compat layer and no migration tooling.

### Problem Frame

The `solutions/<Name>/` wrapper was added anticipating other solution-independent folders that never materialized. Inspection of the two real Flowline projects in active use shows exactly one solution folder each, with nothing else living at that level. The wrapper's cost is real and ongoing: every command that resolves project paths (`deploy`, `sync`, `status`, `generate`, project-mode `push`) builds them through an extra directory level and a solution-name lookup, backed by a `Solutions[]` array config shape that no real project has ever populated with more than one entry.

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
        Plugins.csproj               artifacts/
      WebResources/                 Cr07982.sln
        WebResources.csproj
      artifacts/
      DATAVERSE_CONTEXT.md
      CHANGES.md
```

### Key Decisions

- **Lands before v1.0, date moves.** Treated as a pre-release hard cutover, not a post-1.0 breaking change requiring a stability-protecting migration story.
- **No migration path — old layout dropped entirely.** The two known existing repos are hand-migrated or recreated by the maintainer directly. Flowline's code carries no support for the old `solutions/<Name>/` layout or the array-form `.flowline`, and no special error handling is added for repos still on it — whatever error naturally occurs from the schema/path mismatch is acceptable at this scale.
- **`.flowline`'s `Solution` config becomes a single object, not an array.** The array's only proven use — `StatusCommand`'s multi-row grid — belongs conceptually to a future, separate umbrella command that would discover multiple `.flowline` files, not to this file's own schema.
- **`Solution.Name` is removed from `.flowline` entirely.** Solution identity is always derived from `Package/src/Other/Solution.xml`, eliminating a second, driftable copy of information the packed solution source already holds authoritatively. `Name`'s original purpose — routing to `solutions/<Name>/` — no longer exists once the folder is gone.
- **The `--solution` CLI flag is removed from every command that exposes it.** There is only ever one configured solution, so per-invocation selection is unnecessary.
- **`StatusCommand` keeps its existing grid-rendering mechanism unchanged.** It now always renders exactly one row; no redesign to a non-grid layout.
- **The `.sln` file stays named after the Dataverse solution's unique name**, unchanged from today's `clone` logic — just written to the project root instead of a subfolder.
- **The same-environment, multiple-solution case is solved with documentation, not new tooling.** Two supported patterns: a separate repo per solution, or a `solutions/<Name>/` folder inside one repo containing multiple independent, self-contained Flowline projects (each with its own `.flowline` at its own root). The second pattern already works with zero code changes — `FindProjectRoot` (`FlowlineCommand.cs:62-72`) walks upward from the current directory to the nearest `.flowline`, and `CloneCommand` (`CloneCommand.cs:46`) has no requirement that its target folder be fresh or empty. Neither pattern shares environment config (`ProdUrl`/`DevUrl`/auth) across sibling `.flowline` files — each carries its own copy.

### Requirements

**Folder layout**
- R1. `Package/`, `Plugins/`, `WebResources/`, `artifacts/`, `DATAVERSE_CONTEXT.md`, and `CHANGES.md` live directly at a Flowline project's root, not nested under a `solutions/<Name>/` folder.
- R2. `flowline clone` scaffolds the root-level layout for freshly cloned projects.
- R3. Every command that today constructs paths through `solutions/<Name>/` (`deploy`, `sync`, `status`, `generate`, project-mode `push`, `DataverseContextGenerator`) resolves paths from the project root directly instead.

**Configuration schema**
- R4. `.flowline`'s config replaces the `Solutions` array with a single `Solution` object; `IncludeManaged` and `Generate` settings keep their current shape within it.
- R5. `.flowline`'s `Solution` object carries no `Name` field. Every command that needs the Dataverse solution's unique name reads it from `Package/src/Other/Solution.xml`.

**Command surface**
- R6. The `--solution` flag is removed from every command that exposes it today.
- R7. `StatusCommand` renders its existing grid output unchanged in mechanism, producing a single row.

**Documentation**
- R8. `docs/folder-structure.md` is rewritten to describe the root-level layout, replacing the `solutions/<Name>/` description.
- R9. Affected GitHub Wiki pages (`Getting-Started.md`, `Command-Reference.md`, and any migration guide referencing the old layout) are updated in the same body of work.
- R10. Documentation describes both supported patterns for the same-environment, multiple-solution case — separate repo, or a nested `solutions/<Name>/` folder of independent projects — including the limitation that neither pattern shares environment config across sibling `.flowline` files.

### Scope Boundaries

**Deferred for later**
- An umbrella/workspace-level `.flowline` that discovers and aggregates across nested `.flowline` files (e.g., a hypothetical `flowline status --all`) — explicitly parked, not designed as part of this work.
- Any tooling that specifically detects or assists the nested `solutions/<Name>/` multi-project pattern (in `flowline clone` or elsewhere) — it already works via existing upward directory search and stays a documented convention, not a built feature.

**Outside this work**
- Automated migration tooling for repos on the old layout.
- Any change to standalone-mode `push`/`generate` — verified unaffected; standalone mode never touches `solutions/<Name>/` path construction or `.flowline` (`PushCommand.cs:283`, `PushCommand.cs:354`, `GenerateCommand.cs:356`).

### Dependencies / Assumptions

- Assumes the only real-world Flowline projects affected today are the two inspected repos; both are hand-migrated by the maintainer outside this plan's scope.
- Assumes v1.0's milestone date (`STRATEGY.md`, currently 2026-07-20) moves to accommodate this work landing first.

### Sources / Research

- Two real Flowline projects inspected on disk — a client engagement project and a personal test project — each holding exactly one solution folder under `solutions/`, with nothing else at that level.
- Current `solutions/<Name>/` path construction sites: `FlowlineCommand.cs:25` (`AllSolutionsFolderName` constant), `DeployCommand.cs:68`, `SyncCommand.cs:64`, `StatusCommand.cs:146,165`, `DataverseContextGenerator.cs:23`, `PushCommand.cs:285,356`, `GenerateCommand.cs:166`.
- `.flowline`'s current array schema and its one real consumer: `StatusCommand.cs:74` (`config.Solutions.ToList()`), `InvocationLogger.cs:33`.
- Standalone-mode isolation, confirming this plan doesn't touch it: `PushCommand.cs:83,113,283,354`, `GenerateCommand.cs:130,356`.
- Project-root discovery mechanism underpinning the nested-folder multi-solution pattern: `FlowlineCommand.cs:62-72` (`FindProjectRoot`), `CloneCommand.cs:46` (`RequiresProject => false`).
- Microsoft Learn, "Organize your solutions in Power Platform" (`learn.microsoft.com/en-us/power-platform/alm/organize-solutions`) — single-solution and same-environment-multiple-solution strategy tiers.
