---
title: Multi-Plugin-Project Support - Plan
type: feat
date: 2026-07-15
topic: multi-plugin-project-support
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
---

# Multi-Plugin-Project Support - Plan

## Goal Capsule

- **Objective:** `flowline push` discovers and pushes every plugin-bearing project in a solution's `.sln`, and correctly resolves each project's build output regardless of assembly name or output-folder shape — not just a single fixed `Plugins.csproj` producing an assembly literally named "Plugins".
- **Product authority:** direct discussion with the maintainer, grounded in the 2026-07-14 pluginpackage/NuGet-support plan and a real legacy plugin project (`E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\Plugins\Plugins.csproj`, outside this repo) that breaks two of Flowline's current hardcoded assumptions.
- **Open blockers:** none — see Outstanding Questions for items deferred to planning.

## Product Contract

### Summary

`flowline push` should stop assuming a solution has exactly one plugin project, always folder-named "Plugins", always producing an assembly literally named "Plugins" at a fixed `net462/publish/` build-output path. Instead, it discovers every plugin-bearing project referenced by the solution's own `.sln`, confirms each one by reflecting its build output for `IPlugin`/`CodeActivity`-derived types, and locates that output wherever the build actually placed it.

### Problem Frame

Two gaps surfaced independently while implementing the pluginpackage/NuGet-support feature (`docs/plans/2026-07-14-001-feat-pluginpackage-nuget-support-plan.md`), and turned out to share one root cause.

First: Flowline's project-mode push (`PushCommand.cs`'s `PreparePluginsForPushAsync`/`ResolvePluginPushPath`) hardcodes the plugin project's folder name ("Plugins"), its build-output location (`bin/Release/net462/publish/`), and its assembly name (the `PluginsName` constant, "Plugins"). A real classic (non-packaged) plugin project — `E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\Plugins\Plugins.csproj` — breaks all three: it sets a custom `<AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>`, has no packaging enabled, and a plain `dotnet build` drops its output straight at `bin/Release/net462/AV.SpotlerAutomate.Plugins.dll` with no `publish` subfolder at all. This predates the NuGet-package feature — it's a gap in the classic path, not something that feature introduced — and is directly relevant to onboarding real legacy projects like this one (the wiki's `Migration-from-spkl.md`/`Migration-from-Daxif.md`/`Migration-from-PACX.md` guides target exactly this kind of project).

Second: `docs/folder-structure.md` assumes exactly one plugin project per solution, at a fixed `Plugins/` path. A dotnet solution can genuinely contain more than one independent plugin-bearing `.csproj` (one per business domain, for example) — a different multiplicity axis than the NuGet-support plan's KD5 (which covers multiple plugin-bearing DLLs bundled inside *one* project's own `.nupkg` via a `ProjectReference` chain, already solved).

Once detection stops depending on a fixed folder name and instead walks the solution's own `.sln` project list, confirming each candidate by reflection, the first gap becomes a special case of the second: Flowline needs generalized "reflect whatever this csproj built, wherever it landed" logic either way. Fixing that shared underlying capability resolves both.

### Key Decisions

- **KD1 — Candidate scope is every `.csproj` referenced by the solution's `.sln`.** Not a folder-tree glob, not an explicit config list. This is the standard, discoverable answer for a dotnet user: the `.sln` is already the authoritative "what's in this solution" list, and anything not wired into it (stray/experimental projects) is skipped by construction.
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

- AE1. **Solution with one plugin project, today's exact shape** (`solutions/MySolution/Plugins/Plugins.csproj`, assembly named "Plugins"). `flowline push` behaves exactly as it does today. Covers R1, R7.
- AE2. **Solution with two independent plugin projects** (e.g. `Plugins.Sales.csproj` and `Plugins.Support.csproj`, both referenced by the `.sln`, neither named "Plugins"). Both are discovered, both build and reflect as `IPlugin`-bearing, both get pushed and registered independently in one `flowline push` run. Covers R1, R2, R4, R5.
- AE3. **Solution with a plugin project plus a non-plugin project** (e.g. `Entities.csproj`, a shared DTO library referenced by the `.sln` but implementing no `IPlugin`/`CodeActivity` type). `Entities.csproj` is silently excluded; only the real plugin project is pushed. Covers R2, R3.
- AE4. **Classic (non-packaged) project with a custom `AssemblyName` and no `publish` subfolder** — the Spotler-shaped project (`<AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>`, plain `dotnet build` output at `bin/Release/net462/AV.SpotlerAutomate.Plugins.dll`). Flowline finds and reflects this DLL correctly despite neither the fixed name nor the fixed subfolder holding. Covers R4.
- AE5. **Two plugin projects, one already on `Nupkg` and one still classic.** Under `PluginPackageMode` staying per-solution (KD4), both projects follow whatever single mode the solution declares (`Auto` by default); the plan does not attempt to push one via NuGet and the other classic based on their individual states. Covers R6.

### Scope Boundaries

- Per-project `PluginPackageMode` override — rejected in KD4, not deferred; the per-solution setting is the intended long-term shape, not a stopgap.
- New registration semantics for `CodeActivity`-derived custom workflow activities beyond what reflection already detects — out of scope; this brainstorm only extends *where* the existing `IPlugin`/`CodeActivity` filter is applied (per-project across a solution), not *what* Flowline does once it finds one.
- Automated migration of a legacy/arbitrary folder layout (e.g. the Spotler example's `src/Plugins` location) into Flowline's own `solutions/<Sln>/` convention — out of scope; this plan makes Flowline tolerate an arbitrary location via `.sln`-membership detection, not reorganize it.

### Outstanding Questions

**Deferred to Planning:**
- Build-cost handling: building every `.sln`-referenced project (including non-plugin ones like `WebResources.csproj`/`Entities.csproj`) just to reflect-and-reject them may be wasteful on a large solution. Planning should decide whether a cheap pre-filter (e.g., skip non-`net4xx`-targeted projects or ones with no `Microsoft.Xrm.Sdk`/`Flowline.Attributes` reference) is worth adding, or whether "build everything, reflect, discard non-matches" is acceptable given `--no-build` already exists as an escape hatch.
- Exact mechanism for parsing `.sln` project references (regex/line-based parsing vs. an MSBuild/solution-parsing library) — implementation detail, not a product decision.
- Whether a discovered plugin project needs any distinct CLI surface (e.g., `--scope` targeting one specific plugin project by name) or whether "push discovers and pushes all of them" is sufficient for v1.

### Sources / Research

- `docs/plans/2026-07-14-001-feat-pluginpackage-nuget-support-plan.md` — KD5/KTD15/KTD16 (multi-DLL-per-package, a different multiplicity axis already solved; same cost/benefit reasoning reused here for KD4).
- `src/Flowline.Core/Services/PluginTypeMetadataScanner.cs:55-56` — existing `IPlugin`/`CodeActivity` reflection filter, reused as-is (KD2).
- `src/Flowline/Commands/PushCommand.cs` (`PreparePluginsForPushAsync`, `ResolvePluginPushPath`) — today's hardcoded single-project assumptions.
- `docs/folder-structure.md` — the fixed `Plugins/` folder convention this plan relaxes (KD5, R8).
- `E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\Plugins\Plugins.csproj` — real external example motivating AE4 (repo-external, not a Flowline source path).
