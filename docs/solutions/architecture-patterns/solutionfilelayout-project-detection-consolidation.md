---
title: SolutionFileLayout — consolidating three ad-hoc project resolvers behind one loud-by-default rule
date: 2026-07-21
category: docs/solutions/architecture-patterns/
module: Flowline.Core.Services (SolutionFileLayout)
problem_type: architecture_pattern
component: project_detection
severity: high
applies_when:
  - "Multiple call sites each open and parse the same config/manifest file, and each applies its own ad-hoc rules for classifying entries in it"
  - "One entry type has a strong, unambiguous discriminator (a unique file extension) while a related type has only a weak one (a substring match on file content), and the two evolved on different loud/quiet policies"
  - "A 'required' domain object is currently treated as optional, so its absence degrades silently into a path that may not exist rather than failing the command"
  - "Excluding a project/file type from a detection rule (e.g. PCF, a test project) needs its own resolver so the exclusion is stated once and reused, not re-implemented at each detection call site"
  - "A structurally similar future project type (Portals, Code Apps) will make a by-elimination detection rule ambiguous once it exists, and the risk needs to be bounded and recorded rather than solved speculatively"
related_components:
  - SolutionFileLayout
  - DataverseSolutionProjectResolver
  - WebResourcesProjectResolver
  - PcfProjectResolver
  - PluginProjectResolver
  - DeployCommand
tags:
  - solutionfilelayout
  - project-detection
  - webresources-detection
  - pcf-exclusion
  - elimination-plus-signals
  - fail-loud
  - dataverse-solution-project
  - drift-gate
---

# SolutionFileLayout — consolidating three ad-hoc project resolvers behind one loud-by-default rule

## Context

Flowline resolved its three per-project-root project types — the Dataverse solution project (`.cdsproj`), plugin projects, and the WebResources project — through three separate call sites, each re-opening and re-parsing the same solution file (`.sln`/`.slnx`) and each applying its own rules:

| | Dataverse solution project | Plugin projects | WebResources project |
|---|---|---|---|
| Primary signal | `Extension == ".cdsproj"` — unique, definitive | `.csproj` → net4x → reflect for `IPlugin`/`CodeActivity` — strong, cheap, over-inclusive | `.csproj` → text contains `Microsoft.Build.NoTargets` — **one loose substring** |
| Zero matches | throw `ConfigInvalid` | degrade (advisory) / throw (`push`) | **silent degrade to a conventional path that may not exist** |
| Two-plus matches | throw — refuses to pick | supported (multiple projects, by design) | **silent — picked alphabetically first** |
| No solution file | throw `NotFound` | fallback to `Plugins/Plugins.csproj` | fallback to `WebResources/WebResources.csproj` |
| Wrong-answer cost | wrong solution synced/deployed | deletes a live plugin registration | **silences `deploy`'s drift gate** |

The WebResources resolver was the weak one because its discriminator was structurally weaker than the other two's from the start. `.cdsproj` is a unique extension — no ambiguity is possible. Plugin detection is thorough and loud on the stated rule: cheap, over-inclusive, and it never guesses on a path that drives a destructive op (it reflects the actual build output for `IPlugin`/`CodeActivity` types before registering or deleting anything). WebResources detection, by contrast, was one substring check (`Microsoft.Build.NoTargets`) against project file text, with no fallback rule for a project that doesn't declare that SDK, and no refusal when more than one project matched — it just took the alphabetically first one.

This missed real, non-adversarial project shapes. A WebResources project converted to a plain SDK with an empty `CoreCompile` target and an `Exec` running `npm run build` — no `NoTargets`, no folder named `WebResources` — was invisible to the substring check, silently degrading `deploy`'s drift gate rather than failing loud. And nothing distinguished a WebResources project from a PCF control wrapped in a `.csproj` (rather than PCF's own `.pcfproj`), since both are `.csproj` files with web-tooling markers.

## Guidance

### Consolidate the read, not just the rules

`SolutionFileLayout` (`src/Flowline.Core/Services/SolutionFileLayout.cs`) reads the solution file exactly once per `LoadAsync` call and hands the parsed project list to per-type resolvers it composes — `DataverseSolutionProjectResolver`, `PluginProjectResolver`, `WebResourcesProjectResolver` — rather than each resolver re-reading the file. Each resolver is a pure function over an already-parsed `IReadOnlyList<MsBuildSolutionProject>`, so it is unit-tested with fixtures and no command (`SolutionFileLayout.cs:58-81`).

Per-project resolution is lazy and cached (`Lazy<T>` per property, `SolutionFileLayout.cs:36-38`), so a command that never touches WebResources (e.g. `generate`, which only reads `PluginProjects`) never triggers that validation. Coupling every command to every project type's validation would be loud in the wrong place.

### Make the weak rule as strong as the strong ones: elimination first, signals only to rank

`WebResourcesProjectResolver.Resolve` (`src/Flowline.Core/Services/WebResourcesProjectResolver.cs:73-118`) treats WebResources detection as a by-elimination-plus-signal problem rather than a positive-match-on-a-substring problem:

1. Start from every solution-referenced, on-disk `.csproj`.
2. Exclude plugin projects — but keep one that carries a *strong* WebResources signal even if the over-inclusive plugin pre-filter swept it up (so a WebResources project inheriting its framework from a `Directory.Build.props` isn't lost).
3. Exclude PCF projects (see below).
4. Exclude test projects (filename *ends with* `Test`/`Tests`, or references a test SDK — a substring match wrongly excluded a `TestApp.WebResources` project).
5. Score each survivor on weighted content signals (`ScoreSignals`) and take the unique top score:
   - a `flowline:` annotation comment in a `.ts`/`.js` file — very strong (positive proof the source is Flowline-managed, not just any JS project)
   - a suppressed compile target or the `NoTargets` SDK, or a `dist/` folder — strong
   - a `package.json` build script, a bundler config, a `WebResources`-named folder, or web asset files — medium
6. The winner must carry at least one signal. Zero candidates, or a lone survivor scoring zero, resolves to **`null`** — no confident WebResources project.
7. Two-plus candidates tied at the top score throw `ConfigInvalid` naming them, never a silent pick.

The elimination step plus the signal floor is what makes the old weak substring check unnecessary — with PCF, plugins, and tests excluded, a single signalled survivor is correct by construction, and a signalless survivor is treated as "no confident WebResources project" rather than blindly returned.

### Give the exclusion its own resolver, even in draft form

`PcfProjectResolver` (`src/Flowline.Core/Plugins/PcfProjectResolver.cs`) exists solely so WebResources detection can exclude PCF controls — Flowline doesn't pack, push, or register PCF today. It checks `.pcfproj` by extension first (PCF's own project templates never use `.csproj`), then a `.csproj`-wrapped case: a `Microsoft.PowerApps.MSBuild.Pcf` package reference, or (for a reference pulled in transitively, e.g. via `Directory.Build.props`) a sibling `ControlManifest.Input.xml` (`PcfProjectResolver.cs:32-48`). It is documented in its own remarks as a draft — good enough to exclude, not verified against the range of shapes PCF tooling can produce, and due its own verification pass if PCF becomes a first-class Flowline project type.

The pattern generalizes: when a detection rule needs "and definitely not X" as one of its conditions, give X's check its own resolver rather than inlining an ad-hoc exclusion at the call site — it is then reusable, testable, and honestly scoped (a draft can say so in its own doc comment).

### Reverse a "quiet none/quiet fallback" policy explicitly, in both directions

Two related policies flipped as part of this change, and both needed to be stated as decisions, not just code diffs:

- **An expected project's absence is now loud, but not fatal.** WebResources used to silently degrade to a conventional path that might not exist. It now resolves to `null` when no confident WebResources project is found, and each command *skips the web-resource work with a loud warning* — a WebResources project is expected but not required (a plugin-only or migrated repo is legitimate), and the loud warning is what replaces the silent skip that could otherwise revert un-synced resources on deploy. This is safe because a *real* WebResources project always carries a signal and is rescued from the plugin set, so `null` genuinely means there is nothing to handle. The one hard failure kept is a *tie* — two-plus plausible WebResources projects — because skipping there would ignore resources the user demonstrably has. (An earlier iteration made absence a hard `ConfigInvalid`; that was softened during review — "expected" turned out to be the right strength, not "required".)
- **No solution file at all is now an error, not a fallback.** The solution file is Flowline's config; the previous conventional fallbacks (`Plugins/Plugins.csproj`, `WebResources/WebResources.csproj`) let a command run against guessed paths in a repo with no config at all. `SolutionFileLayout.LoadAsync` now throws `NotFound` pointing at stand-alone mode (`flowline push --pluginFile <dll>`) as the way to push without a solution file (`SolutionFileLayout.cs:95-105`). This only affects a non-`clone` command run against a repo with genuinely no solution file — `clone` itself scaffolds by composing literal paths and never calls `SolutionFileLayout`, and a project still on the older flat `Package/` folder naming (rather than `Solution/`) is unaffected, since it still has a solution file referencing its projects.

## Anti-pattern

Letting one of several structurally-similar detection rules degrade quietly while its siblings fail loud. If `.cdsproj` detection refuses to guess between two candidates, and plugin detection refuses to guess which build output is the real plugin assembly, a WebResources substring match that silently picks the alphabetically-first candidate (or a path that doesn't exist) is not a smaller version of the same problem — it is the one place in the system where a wrong guess ships silently, and the cost (a deploy drift gate that stops catching real drift) is exactly as expensive as the loud failures the other two rules were built to prevent.

## Known future risk (recorded, not solved)

Power Apps Portals and Code Apps projects are both TypeScript/React projects built with the same tooling (Rollup/webpack/vite, `package.json`) a WebResources project uses — structurally indistinguishable from it by content signal, and not excluded today. The risk is bounded by the elimination rule's own arithmetic: a Portal or Code App project is added to a solution *after* the WebResources project already exists, so its only effect is to create a **second** web-like candidate — the single-candidate case (the common one) is never affected. The gap only matters once a repo has more than one web-like `.csproj`; a positive Portals/Code-Apps exclusion (mirroring `PcfProjectResolver`) is the fix, deferred until Flowline supports either project type or a user actually hits the multi-candidate ambiguity.
