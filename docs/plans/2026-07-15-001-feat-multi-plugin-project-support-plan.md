---
title: Multi-Plugin-Project Support - Plan
type: feat
date: 2026-07-15
topic: multi-plugin-project-support
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
planned: 2026-07-20
---

# Multi-Plugin-Project Support - Plan

## Goal Capsule

- **Objective:** `flowline push` discovers and pushes every plugin-bearing project in a solution's `.sln`, and correctly resolves each project's build output regardless of assembly name or output-folder shape — not just a single fixed `Plugins.csproj` producing an assembly literally named "Plugins".
- **Product authority:** direct discussion with the maintainer, grounded in the 2026-07-14 pluginpackage/NuGet-support plan and a real legacy plugin project (`E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\Plugins\Plugins.csproj`, outside this repo) that breaks two of Flowline's current hardcoded assumptions.
- **Open blockers:** One — sequencing, not scope. Ships in v1.0 (2026-08-01) as the second of three layout-related plans. `docs/plans/2026-07-19-001-feat-sln-add-slnx-support-plan.md` ships first and owns the shared solution-file reader (its KD8); KD1's candidate enumeration consumes that reader rather than parsing the solution file itself. That plan's R9a also restates this plan's `.sln` language as "solution file (`.sln` or `.slnx`)". Previously blocked by `docs/plans/2026-07-17-003-refactor-single-solution-layout-plan.md`, which has since been **implemented** (`e6d9f5b` collapse `Solutions[]`→`Solution`, `3c5d423` remove `AllSolutionsFolderName`, `a34277a` rewrite `folder-structure.md`, post-merge fixes `068e224`/`9b14798`). This plan has been rebased onto the root-level layout: `Package/`, `Plugins/`, `WebResources/` and the `.sln` now live directly at the project root, with no `solutions/<Name>/` wrapper. KD5/R8's `docs/folder-structure.md` edits target the current root-level text.

## Product Contract

### Summary

`flowline push` should stop assuming a solution has exactly one plugin project, always folder-named "Plugins", always producing an assembly literally named "Plugins" at a fixed `net462/publish/` build-output path. Instead, it discovers every plugin-bearing project referenced by the solution's own `.sln`, confirms each one by reflecting its build output for `IPlugin`/`CodeActivity`-derived types, and locates that output wherever the build actually placed it.

### Problem Frame

Two gaps surfaced independently while implementing the pluginpackage/NuGet-support feature (`docs/plans/2026-07-14-001-feat-pluginpackage-nuget-support-plan.md`), and turned out to share one root cause.

First: Flowline's project-mode push (`PushCommand.cs`'s `PreparePluginsForPushAsync`/`ResolvePluginPushPath`) hardcodes the plugin project's folder name ("Plugins"), its build-output location (`bin/Release/net462/publish/`), and its assembly name (the `PluginsName` constant, "Plugins"). A real classic (non-packaged) plugin project — `E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\Plugins\Plugins.csproj` — breaks all three: it sets a custom `<AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>`, has no packaging enabled, and a plain `dotnet build` drops its output straight at `bin/Release/net462/AV.SpotlerAutomate.Plugins.dll` with no `publish` subfolder at all. This predates the NuGet-package feature — it's a gap in the classic path, not something that feature introduced — and is directly relevant to onboarding real legacy projects like this one (the wiki's `Migration-from-spkl.md`/`Migration-from-Daxif.md`/`Migration-from-PACX.md` guides target exactly this kind of project).

Second: `docs/folder-structure.md` assumes exactly one plugin project per solution, at a fixed `Plugins/` path. A dotnet solution can genuinely contain more than one independent plugin-bearing `.csproj` (one per business domain, for example) — a different multiplicity axis than the NuGet-support plan's KD5 (which covers multiple plugin-bearing DLLs bundled inside *one* project's own `.nupkg` via a `ProjectReference` chain, already solved).

Once detection stops depending on a fixed folder name and instead walks the solution's own `.sln` project list, confirming each candidate by reflection, the first gap becomes a special case of the second: Flowline needs generalized "reflect whatever this csproj built, wherever it landed" logic either way. Fixing that shared underlying capability resolves both.

### Key Decisions

- **KD1 — Candidate scope is every `.csproj` referenced by the solution file (`.sln` or `.slnx`).** Not a folder-tree glob, not an explicit config list. Enumeration uses the shared reader from the solution-file-wiring plan (its KD8), so this plan adds no second parser. This is the standard, discoverable answer for a dotnet user: the `.sln` is already the authoritative "what's in this solution" list, and anything not wired into it (stray/experimental projects) is skipped by construction.
- **KD2 — Confirmation signal reuses the existing `IPlugin`/`CodeActivity` reflection filter.** A candidate project is a "plugin project" only if its build output contains a type deriving `Microsoft.Xrm.Sdk.IPlugin` or `System.Activities.CodeActivity` — the exact filter `PluginTypeMetadataScanner.cs` already applies per-DLL for the NuGet path (`IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin")` / `"System.Activities.CodeActivity"`, `PluginTypeMetadataScanner.cs:55-56` — this scanning logic was extracted out of `PluginAssemblyReader.cs` during a later maintainability pass), just applied per-project across the solution instead of assumed for one fixed folder. A project that reflects to zero matching types (WebResources, Entities, shared DTOs) is silently not a plugin project — no separate exclusion list needed.
- **KD3 — Generalized build-output discovery is the shared fix underlying both original gaps.** Locating and reflecting "whatever DLL/`.nupkg` this specific csproj produced" (correct assembly name, correct output path) has to work for any csproj in the candidate set, not just one hardcoded folder/name — which is exactly what the Spotler-style classic-path gap needed too. One piece of work, not two.
- **KD4 — `PluginPackageMode` (`Auto`/`Nupkg`/`Dll`) stays a single per-solution setting, applied uniformly to every detected plugin project.** Considered and rejected: a per-project override. Most solutions have exactly one plugin project; a solution with genuinely different modes across multiple plugin projects would itself be an edge case, and the config-maintenance cost of per-project overrides isn't worth it for that rarity — the same cost/benefit judgment already made for KD5 in the NuGet-support plan.
- **KD5 — `docs/folder-structure.md`'s fixed-name convention needs relaxing.** "The plugins project is always named `Plugins` (`Plugins.csproj`)" is no longer a detection requirement once discovery is `.sln`-membership-driven — any project name/location works as long as it's referenced by the solution's `.sln`.

### Requirements

**Discovery**
- R1. `flowline push` (project mode) enumerates every `.csproj` referenced by the target solution's `.sln` file as a plugin-project candidate.
- R2. For each candidate, Flowline builds it (respecting `--no-build` the same way the current single-project flow does) and reflects its output to determine whether it's a plugin project — a type deriving `IPlugin` or `CodeActivity` present, per KD2.
- R3. A candidate whose reflected output contains no `IPlugin`/`CodeActivity`-derived type is silently excluded from the push — not reported as an error.
- R4. Each confirmed plugin project resolves its own actual build-output path and its own actual assembly name (from the built artifact, not an assumed constant) — generalizing today's single-"Plugins"-project resolution to work for any assembly name and any output-folder shape (e.g. no `net462/publish/` subfolder).

**Push behavior**
- R5. All confirmed plugin projects in a solution are pushed within a single `flowline push` invocation, each registered independently (its own `pluginassembly`/`pluginpackage`, plugin types, steps, custom APIs) under the same target Dataverse solution.
- R6. `PluginPackageMode` (`Auto`/`Nupkg`/`Dll`) is read once per solution and applied identically to every confirmed plugin project in that push (KD4).

**Compatibility**
- R7. A solution with exactly one plugin project (today's only supported shape) continues to work unchanged — this is a superset of today's behavior, not a breaking change to the common case.
- R8. `docs/folder-structure.md` is updated to describe discovery-by-`.sln`-membership rather than a fixed `Plugins/` folder name (KD5).

### Acceptance Examples

- AE1. **Solution with one plugin project, today's exact shape** (`Plugins/Plugins.csproj` at the project root, assembly named "Plugins"). `flowline push` behaves exactly as it does today. Covers R1, R7.
- AE2. **Solution with two independent plugin projects** (e.g. `Plugins.Sales.csproj` and `Plugins.Support.csproj`, both referenced by the `.sln`, neither named "Plugins"). Both are discovered, both build and reflect as `IPlugin`-bearing, both get pushed and registered independently in one `flowline push` run. Covers R1, R2, R4, R5.
- AE3. **Solution with a plugin project plus a non-plugin project** (e.g. `Entities.csproj`, a shared DTO library referenced by the `.sln` but implementing no `IPlugin`/`CodeActivity` type). `Entities.csproj` is silently excluded; only the real plugin project is pushed. Covers R2, R3.
- AE4. **Classic (non-packaged) project with a custom `AssemblyName` and no `publish` subfolder** — the Spotler-shaped project (`<AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>`, plain `dotnet build` output at `bin/Release/net462/AV.SpotlerAutomate.Plugins.dll`). Flowline finds and reflects this DLL correctly despite neither the fixed name nor the fixed subfolder holding. Covers R4.
- AE5. **Two plugin projects, one already on `Nupkg` and one still classic.** Under `PluginPackageMode` staying per-solution (KD4), both projects follow whatever single mode the solution declares (`Auto` by default); the plan does not attempt to push one via NuGet and the other classic based on their individual states. Covers R6.

### Scope Boundaries

- Per-project `PluginPackageMode` override — rejected in KD4, not deferred; the per-solution setting is the intended long-term shape, not a stopgap.
- New registration semantics for `CodeActivity`-derived custom workflow activities beyond what reflection already detects — out of scope; this brainstorm only extends *where* the existing `IPlugin`/`CodeActivity` filter is applied (per-project across a solution), not *what* Flowline does once it finds one.
- Automated migration of a legacy/arbitrary folder layout (e.g. the Spotler example's `src/Plugins` location) into Flowline's root-level `Plugins/` convention — out of scope; this plan makes Flowline tolerate an arbitrary location via `.sln`-membership detection, not reorganize it.
- Extending `.sln`-driven discovery to `WebResources/` (`WebResources.csproj`) and `Package/` (`Package.cdsproj`) — **adjacent and endorsed, but a separate plan: `docs/plans/2026-07-20-001-refactor-project-layout-naming-plan.md` (KD5).** Both are already registered in the generated `.sln` (`CloneCommand.cs:387-450`, `:545-552`), so the same KD1 principle applies and would retire the remaining `WebResourcesName`/`PackageName` constants. Deliberately not folded in here: this plan's contract is scoped to plugin projects, and generalising it mid-flight would widen the blast radius of an already cross-cutting change. Note that plan also renames the scaffolded projects to solution-identity names (`DWE_Base.Plugins.csproj`), which changes AE1's "assembly named Plugins" shape — the discovery work here is what makes it possible, so this plan lands first. Design rationale: `docs/others/folder-structure-analysis.md` §4.1–§4.2.

### Outstanding Questions

**Deferred to Planning:**
- Build-cost handling: building every `.sln`-referenced project (including non-plugin ones like `WebResources.csproj`/`Entities.csproj`) just to reflect-and-reject them may be wasteful on a large solution. Planning should decide whether a cheap pre-filter (e.g., skip non-`net4xx`-targeted projects or ones with no `Microsoft.Xrm.Sdk`/`Flowline.Attributes` reference) is worth adding, or whether "build everything, reflect, discard non-matches" is acceptable given `--no-build` already exists as an escape hatch.
- Whether a discovered plugin project needs any distinct CLI surface (e.g., `--scope` targeting one specific plugin project by name) or whether "push discovers and pushes all of them" is sufficient for v1.

---

## Planning Contract

**Product Contract preservation:** unchanged. KD1's wording was widened from "`.sln`" to "solution file (`.sln` or `.slnx`)" per the wiring plan's R9a; the decision is the same.

### Key Technical Decisions

- **KTD1 — Consume `MsBuildSolutionReader`; build no parser here.** The wiring plan (`docs/plans/2026-07-19-001`, KD8/U2) ships the reader first, already handling both formats, path normalization, and error mapping. This plan calls it. If that plan slips, this one blocks rather than growing a second parser.

- **KTD2 — The reflection filter already exists; reuse it verbatim.** `src/Flowline.Core/Plugins/PluginTypeMetadataScanner.cs:55-56` implements exactly KD2's test:
  ```csharp
  var isPlugin   = IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin");
  var isWorkflow = IsDerivedFrom(type, "System.Activities.CodeActivity");
  ```
  and `src/Flowline.Core/Plugins/PluginAssemblyReader.cs:16,81` supplies the `MetadataLoadContext` idiom. Neither is reimplemented — this plan changes *where* the filter is applied (per project, across a solution) not *what* it tests.

- **KTD3 — Build-output resolution reads the project's real `AssemblyName` and output path.** Today `PushCommand` assumes the folder name, the assembly name `Plugins`, and a `bin/Release/net462/publish/` shape. The Spotler-shaped project (`<AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>`, output straight at `bin/Release/net462/`) breaks all three. Resolution derives from MSBuild's own evaluation of each project rather than from convention, which is also the prerequisite that lets the layout plan rename assemblies at all.

- **KTD4 — Cheap pre-filter before building candidates, and it must not silently exclude.** Building every solution project just to reflect and discard non-matches is wasteful on a large solution. A pre-filter (no `Microsoft.Xrm.Sdk`/`Flowline.Attributes` reference, or a non-`net4x` target) skips obvious non-candidates. Anything the pre-filter drops is reported under `--verbose`, never silently — a silent exclusion here reads identically to "your plugin project isn't registered" and would be miserable to debug.

- **KTD5 — Caller sweep is a required step, not an implied one.** Two documented incidents in this repo say a plan's `Files:` list under-enumerates when an identity key changes: `docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md` (a missed consumer) and `docs/solutions/design-patterns/promoting-field-to-identity-key-changes-edit-semantics.md` (the same class of miss, twice in one change). Project identity is moving from folder-name constant to solution-file membership, so U5 makes the grep explicit and verifiable.

- **KTD6 — Testable logic goes in `internal static` helpers.** The suite tests `PushCommand.ResolveStandalonePluginFilePath` and peers this way; instance flow through `ExecuteFlowlineAsync` is effectively untestable here. Discovery and resolution must be reachable without constructing the command.

### Patterns to Follow

- `src/Flowline.Core/Plugins/PluginTypeMetadataScanner.cs` — the reflection filter, reused as-is
- `src/Flowline.Core/Plugins/PluginAssemblyReader.cs` — `MetadataLoadContext` lifetime
- `src/Flowline/Commands/DriftCommand.cs` — command shape with `internal static` helpers
- Core is already path-parameterized (handlers take `packageSrcRoot` as an argument), so this work stays in the `Flowline` project plus a Core-side resolver

### Risks

- **Build cost scales with solution size** — KTD4's pre-filter bounds it, but a solution with many projects still pays. `--no-build` remains the escape hatch.
- **Reflection needs built output**, so discovery is ordered after build; a project that fails to build must produce a clear error rather than being silently treated as "not a plugin project".

---

## Implementation Units

### U1. Candidate enumeration from the solution file

**Goal:** Produce the candidate project list from solution-file membership.
**Requirements:** R1, KD1.
**Dependencies:** wiring plan U2 (`MsBuildSolutionReader`).
**Files:** `src/Flowline/Commands/PushCommand.cs`, `tests/Flowline.Tests/PushCommandTests.cs`
**Approach:** Replace fixed-folder detection with a call to the reader, filtered to `.csproj`. Expose as an `internal static` helper taking the project list so tests need no disk (KTD6).
**Test scenarios:**
- A solution with one plugin project yields exactly that candidate (today's shape still works — covers R7/AE1).
- A solution with two plugin projects yields both (covers AE2).
- A solution containing `.cdsproj` and non-plugin `.csproj` entries yields only `.csproj` candidates at this stage.
- A project referenced by the solution file but missing on disk produces an actionable error naming the path.
- Enumeration is stable and order-independent.
**Verification:** candidate list matches solution-file membership for both formats.

### U2. Generalized build-output resolution

**Goal:** Find whatever each candidate actually built, wherever it landed.
**Requirements:** R4, KD3.
**Dependencies:** U1.
**Files:** `src/Flowline.Core/Plugins/` (new resolver), `src/Flowline/Commands/PushCommand.cs`, `tests/Flowline.Core.Tests/` (resolver tests)
**Approach:** Resolve each project's real `AssemblyName` and output path instead of assuming `Plugins.dll` under `net462/publish/`. Handles both the packaged (`.nupkg`) and classic (`.dll`) shapes per KD4's single per-solution `PluginPackageMode`.
**Test scenarios:**
- Default-shaped project (`Plugins.csproj` → `Plugins.dll` under `net462/publish/`) resolves as it does today.
- **Spotler shape**: custom `<AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>` with output at `bin/Release/net462/` and no `publish` subfolder resolves correctly (covers R4/AE4) — this is the concrete regression the unit exists for.
- A project with `AppendTargetFrameworkToOutputPath=false` resolves.
- A project whose assembly name differs from both its folder and its file name resolves.
- An unbuilt project produces a clear "build first" error, not a null path.
- `.nupkg` output resolves under `Nupkg` mode; `.dll` under `Dll` mode.
**Verification:** the real external Spotler project resolves without configuration.

### U3. Plugin confirmation by reflection

**Goal:** Confirm which candidates are actually plugin projects.
**Requirements:** R2, R3, KD2.
**Dependencies:** U2.
**Files:** `src/Flowline/Commands/PushCommand.cs`, `tests/Flowline.Tests/PushCommandTests.cs`
**Approach:** Apply the existing `PluginTypeMetadataScanner` filter per project (KTD2). Zero matching types → silently excluded from the push (R3), but reported under `--verbose` together with anything KTD4's pre-filter dropped.
**Test scenarios:**
- A project with an `IPlugin` implementation is confirmed.
- A project with only a `CodeActivity` implementation is confirmed.
- A project with neither (`Entities.csproj`, a DTO library) is excluded and no error is raised (covers R3/AE3).
- Exclusion is visible under `--verbose` and absent from default output.
- The WebResources project — which builds no assembly at all — is excluded without an error.
**Verification:** a mixed solution pushes only real plugin projects.

### U4. Push every confirmed project in one invocation

**Goal:** Multiple plugin projects register independently in a single `flowline push`.
**Requirements:** R5, R6, KD4.
**Dependencies:** U3.
**Files:** `src/Flowline/Commands/PushCommand.cs`, `tests/Flowline.Tests/PushCommandTests.cs`
**Approach:** Loop confirmed projects, each getting its own `pluginassembly`/`pluginpackage`, types, steps, and custom APIs under the same target Dataverse solution. `PluginPackageMode` is read once per solution and applied to all (KD4/R6). Preserve the existing per-assembly identity-change and drift behaviour unchanged.
**Test scenarios:**
- Two plugin projects each register their own assembly and their own steps (covers R5/AE2).
- Both follow the solution's single `PluginPackageMode` even when their build outputs differ in shape (covers R6/AE5).
- One project failing to push reports which one and does not silently skip the others.
- A single-plugin-project solution behaves exactly as before (covers R7).
- Orphan-cleanup interaction: an assembly no longer produced by any solution project is still handled by existing cleanup rules.
**Verification:** a two-plugin-project solution pushes both in one run against a real org.

### U5. Caller sweep for the identity-key change

**Goal:** No consumer keeps resolving plugin projects the old way.
**Requirements:** R7 (compatibility), KTD5.
**Dependencies:** U1-U4.
**Files:** whatever the sweep finds — expected: `src/Flowline/Commands/PushCommand.cs`, `src/Flowline/Commands/DeployCommand.cs`, `src/Flowline/Utils/PluginWebResourceDriftChecker.cs`, `src/Flowline/Commands/StatusCommand.cs`
**Approach:** Grep the tree for every caller of build-output resolution, assembly-name resolution, and plugin-project detection; confirm each threads through the new discovery. This is a distinct unit precisely because the repo has been burned twice by trusting a plan's file list (KTD5).
**Execution note:** Do the grep before changing anything, and record the call-site list in the PR description — the point is a reviewable enumeration, not a claim that it was checked.
**Test scenarios:**
- Every identified call site has coverage proving it resolves via discovery rather than a fixed name.
- A regression test asserts no source file outside the discovery layer composes a plugin path from a hardcoded project name.
**Verification:** grep output shows no remaining fixed-name plugin-path construction.

### U6. Relax the documented naming convention

**Goal:** Docs stop stating a rule the code no longer enforces.
**Requirements:** R8, KD5.
**Dependencies:** U1-U4.
**Files:** `docs/folder-structure.md`
**Approach:** Replace "the plugins project is always named `Plugins`" with discovery-by-solution-file-membership. Coordinate with the layout plan's R11, which edits the same file — land whichever runs second on top of the first rather than in parallel.
**Test expectation:** none — documentation.
**Verification:** no remaining claim that the plugin project must be named `Plugins`.

---

## Verification Contract

- `dotnet build Flowline.slnx` clean; `dotnet test Flowline.slnx` green.
- The external Spotler-shaped project (`AV.SpotlerAutomate.Plugins`, no `publish` subfolder) is discovered and pushed with no configuration.
- A two-plugin-project solution pushes both independently in one `flowline push` against a real org.
- A single-plugin-project solution is byte-for-byte unchanged in behaviour.
- Caller-sweep enumeration recorded in the PR.

## Definition of Done

- `flowline push` discovers every plugin-bearing project from the solution file and pushes each independently.
- Build-output resolution works for any assembly name and output shape.
- Non-plugin projects are excluded silently by default and visibly under `--verbose`.
- `PluginsName` is no longer used for plugin discovery (its removal completes in the layout plan).
- `docs/folder-structure.md` describes discovery, not a fixed name.

---

### Sources / Research

- `docs/plans/2026-07-14-001-feat-pluginpackage-nuget-support-plan.md` — KD5/KTD15/KTD16 (multi-DLL-per-package, a different multiplicity axis already solved; same cost/benefit reasoning reused here for KD4).
- `src/Flowline.Core/Services/PluginTypeMetadataScanner.cs:55-56` — existing `IPlugin`/`CodeActivity` reflection filter, reused as-is (KD2).
- `src/Flowline/Commands/PushCommand.cs` (`PreparePluginsForPushAsync`, `ResolvePluginPushPath`) — today's hardcoded single-project assumptions.
- `docs/folder-structure.md` — the fixed `Plugins/` folder convention this plan relaxes (KD5, R8).
- `E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\Plugins\Plugins.csproj` — real external example motivating AE4 (repo-external, not a Flowline source path).
