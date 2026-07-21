---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
execution: code
title: SolutionLayout — one place that reads the solution file and resolves every project
date: 2026-07-21
plan_id: 2026-07-21-001
---

# SolutionLayout — one place that reads the solution file and resolves every project

## Goal Capsule

The `.sln`/`.slnx` **is** Flowline's folder configuration ([[project_sln_is_the_config]]) — the
counterpart to `.flowline`, which `ProjectConfig` owns. There is no counterpart class for the
solution file. Instead, three resolvers each open it separately and each applies its own rules:
`ProjectLayoutResolver.ResolvePackageProjectAsync` (`.cdsproj`),
`PluginProjectResolver.DiscoverAsync` (plugins), and
`ProjectLayoutResolver.ResolveWebResourcesProjectAsync` (WebResources). The rules drifted apart —
the package resolver fails loud on ambiguity, the WebResources one degrades silently — and the file
is parsed three-plus times per command.

This plan introduces **`SolutionLayout`**: the solution-file analog of `ProjectConfig`. It reads the
solution file once, classifies every project, verifies the layout, and is the single place the rest
of Flowline asks "where is the package project / plugins / WebResources project". Per-type detection
lives in helper resolvers it composes, so the rules are stated once, consistently, and tested in
isolation. It also fixes the two live defects the code review surfaced (a silently-degrading
WebResources resolver, an alphabetical silent pick) and reverses two now-wrong policies (a
WebResources project is *required*, and a missing solution file is an *error*, not a fallback).

## Background — the audit (fact-verified against source)

| | Dataverse solution | Plugin projects | WebResources |
|---|---|---|---|
| Entry point | `ProjectLayoutResolver.ResolvePackageProjectAsync` | `PluginProjectResolver.DiscoverAsync` | `ProjectLayoutResolver.ResolveWebResourcesProjectAsync` |
| Primary signal | `Extension == ".cdsproj"` (`MsBuildSolutionProject.cs:22`) | `.csproj` → net4x → reflect to `IPlugin`/`CodeActivity` | `.csproj` → text contains `Microsoft.Build.NoTargets` (`ProjectLayoutResolver.cs:139`) |
| Strength | Definitive (extension is unique) | Strong (framework + SDK markers + reflection; defers to reflection on `Directory.Build.props`/`ProjectReference`) | **Weak (one loose substring)** |
| Zero matches | throw `ConfigInvalid` | degrade (advisory) / throw (push) | **silent degrade to a path that may not exist** |
| Two+ matches | throw — refuses to pick | supported (list) | **silent — picks alphabetically first** |
| No solution file | throw `NotFound` | **fallback** to `Plugins/Plugins.csproj` | **fallback** to `WebResources/WebResources.csproj` |
| Wrong-answer cost | wrong solution synced/deployed | **deletes a live registration** | silences the deploy drift gate |

**Why `.csproj` is the hard part.** `.cdsproj` is unique to the package project — extension settles
it. But plugin, WebResources, test, and DTO projects are *all* `.csproj`, so plugin and WebResources
detection must further discriminate. Plugin does it thoroughly and loudly on the stated rule
(`PluginProjectResolver.cs:154-162`): *cheap, over-inclusive, loud when uncertain, never guess on a
path that drives a destructive op.* WebResources violates all three clauses.

**Real-world shapes** (verified on disk):
- `contoso .../mda-client-hooks/ClientHooks.csproj` — a genuine web-resources project with an empty
  `CoreCompile` target and an `Exec` running `npm run build`. **No `NoTargets`, no "WebResources" in
  the folder name.** The current substring misses it entirely.
- `contoso .../image-grid-pcf.pcfproj` — a PCF control. Extension `.pcfproj` (excluded by the
  `.csproj` gate); positive markers `Microsoft.PowerApps.MSBuild.Pcf` + `ControlManifest.Input.xml`;
  targets `net462`.

**Takeaway:** what makes a project a WebResources project is *behavioural* (compiles nothing, emits
web assets) far more than its SDK or folder name. Content signals are universal; SDK and folder-name
are Flowline-convention bonuses. And with a WebResources project *required* (R5) and plugin/PCF/test
excluded, the WebResources project is largely identifiable **by elimination**, with signals as the
tiebreak.

## Product Contract

### Requirements

**The central class**
- **R1.** A new `SolutionLayout` type is the single entry point for resolving projects and paths from
  the solution file — the `.sln`/`.slnx` analog of `ProjectConfig`. It reads the file once and
  exposes the package project + folder, the plugin projects, and the WebResources project.
- **R2.** Per-type detection lives in helper resolvers `SolutionLayout` composes
  (`DataverseSolutionProjectResolver`, `PluginProjectsResolver`, `WebResourcesProjectResolver`, and a
  draft `PcfProjectResolver`), so each rule set is stated once and unit-tested without a command.
- **R3.** Classification is text-and-folder only — no MSBuild evaluation, no build (R-plugin-reflect
  below is the one exception, and only `push` triggers it). Cheap and over-inclusive, like the
  current plugin pre-filter.
- **R4.** The solution file is read and parsed **once** per `SolutionLayout` load; consumers that
  need several project types get them from one instance, not three reads.

**Policy reversals**
- **R5.** A WebResources project is **required**. A valid solution file that yields no WebResources
  project is a `ConfigInvalid` error — Flowline always scaffolds one (empty is fine; nothing to push
  is fine). *Missing plugins is the opposite:* zero plugin projects is a legitimate, common state and
  never an error.
- **R6.** **No solution file is an error** (`ConfigInvalid`/`NotFound` with the fix in the message) —
  the solution file is the config, and stand-alone mode (`push --pluginFile`) covers the
  no-config case. The conventional fallbacks (`Plugins/Plugins.csproj`,
  `WebResources/WebResources.csproj`) are **removed**. The one accepted invalid state is *during*
  `clone`, while it scaffolds the structure before the solution file exists — clone composes literals
  and never calls `SolutionLayout`.

**Per-type rules**
- **R7.** Dataverse solution: exactly one `.cdsproj`. Zero or two+ → `ConfigInvalid`; referenced but
  missing on disk → `NotFound`. (Unchanged from today; folded into the class.)
- **R8.** Plugin projects: solution-referenced `.csproj` surviving the pre-filter; zero is fine.
  Reflection confirms the plugin assembly only where a build exists (`push`); "nothing loaded" throws
  rather than guessing (unchanged — it guards deletion of live registrations).
- **R9.** WebResources: identified among non-plugin, non-PCF, non-test `.csproj` by elimination plus
  weighted signals (KD3). Exactly one required (R5); two+ or an unresolvable ambiguity →
  `ConfigInvalid` naming them; never a silent pick.
- **R10.** PCF projects are never mistaken for WebResources. `.pcfproj` is excluded by extension; a
  `.csproj`-wrapped PCF (`Microsoft.PowerApps.MSBuild.Pcf` or a sibling `ControlManifest.Input.xml`)
  is excluded positively. PCF is only *excluded* in this plan — see KD5 for the draft.

**Convention over configuration**
- **R11.** No `.flowline` setting and no required marker property. An explicit
  marker/config pointer is the documented last resort **only if** users report misdetection —
  recorded, not built. ([[feedback_convention_over_config]])

### Acceptance Examples

- **AE1. Scaffolded repo.** `WebResources/DWE_Base.WebResources.csproj` (NoTargets, `dist/`, `.ts`
  sources), `Plugins/DWE_Base.Plugins.csproj`, `Solution/DWE_Base.cdsproj`. `SolutionLayout` resolves
  all three from one read. Covers R1, R4.
- **AE2. Relocated + renamed WebResources folder.** Moved to `src/WebAssets/`, still NoTargets, still
  has `dist/`+assets. Folder-name signal misses; SDK+content carry it. Resolved. Covers R9.
- **AE3. Converted-SDK WebResources (ClientHooks shape).** No NoTargets, folder not named
  WebResources, empty-compile + `dist/` + web assets, and it is the only non-plugin non-PCF `.csproj`.
  Resolved by elimination + content signals. Covers R9.
- **AE4. Flowline-annotated source.** A `.ts` carrying a Flowline annotation is present → strong
  positive, resolves even against a second non-plugin project. Covers R9.
- **AE5. PCF alongside WebResources.** A `.pcfproj` control and a real WebResources project referenced;
  only the WebResources project resolves. Covers R10.
- **AE6. PCF wrapped as `.csproj`.** A `.csproj` carrying `Microsoft.PowerApps.MSBuild.Pcf` is excluded
  even with web assets. Covers R10.
- **AE7. No WebResources project.** Valid solution file, plugins only, no WebResources project → throws
  `ConfigInvalid` telling the user to scaffold/add one. Covers R5.
- **AE8. Two WebResources projects.** Throws `ConfigInvalid` naming both. Covers R9.
- **AE9. No plugins.** Valid solution file with a package + WebResources project and zero plugin
  projects → resolves fine, no error; `push` has nothing to register. Covers R5, R8.
- **AE10. No solution file.** Any resolve throws `ConfigInvalid` naming stand-alone mode as the way to
  push without one. No fallback path returned. Covers R6.
- **AE11. Mid-clone.** `clone` scaffolds `Solution/`, `Plugins/`, `WebResources/` before the solution
  file lists them, composing literals and never calling `SolutionLayout` — the one accepted invalid
  state. Covers R6.

### Scope Boundaries

- **Not** changing what plugin reflection does (`ResolvePluginAssembly` is sound) — only moving it
  under the facade.
- **Not** building PCF handling (pack/push/register) — PCF is excluded now, drafted for later (KD5).
- **Not** adding Power Apps Portals or Code Apps detection — flagged as future work (KD6), because
  they resemble WebResources and will break by-elimination once supported.
- **Not** building an explicit marker/config pointer (R11).

## Key Decisions

- **KD1 — `SolutionLayout` is the facade; resolvers are the parts.** `SolutionLayout.LoadAsync(slnFolder)`
  reads the file once, runs the per-type resolvers over the shared project list, verifies (R5/R7/R9),
  and exposes `PackageProjectPath`, `PackageFolder`, `PluginProjects`, `WebResourcesProject`. Consumers
  replace their three separate resolver calls with one load + property reads. Name is provisional — it
  parallels `ProjectConfig` for `.flowline`; alternatives `SolutionFileConfig` / `MsBuildSolutionFile`
  noted, decide at implementation. Verification lives in the class, not scattered across callers.
- **KD2 — Helper resolvers are internal and pure over a project list.** Each takes the parsed
  `IReadOnlyList<MsBuildSolutionProject>` + folder and returns its verdict, so it is unit-tested with
  fixtures and no command. `DataverseSolutionProjectResolver` and `WebResourcesProjectResolver` are new
  (splitting today's `ProjectLayoutResolver`); `PluginProjectsResolver` is today's
  `PluginProjectResolver` logic moved under the facade.
- **KD3 — WebResources: elimination first, weighted signals to rank.** Candidates are solution
  `.csproj` that are not the Dataverse solution project, not a plugin project (net4x + Xrm.Sdk/CrmSdk
  marker — cheap, no build), not PCF (R10), not a test project (name `test`, or a test SDK reference).

  **The strongest rule is arithmetic: exactly one surviving candidate is the WebResources project with
  high confidence** — a greenfield Flowline project scaffolds exactly one, and Portals/Code Apps (KD6)
  are added *later*, so their presence would create a *second* candidate and move detection onto the
  multi-candidate path. The signals below only decide *between* candidates when more than one survives;
  with one, take it. Confidence is the sum of:
  - a `.ts`/`.js` file carrying a **Flowline annotation** — the `flowline:` comment marker the web
    resource pipeline already parses (`// flowline:onload …`, plus the `//!` and `/*!` forms and
    `flowline:depends`; `FormEventAnnotationParser.cs:28-29`). Very strong — positive proof this source
    is Flowline-managed web resources, not an arbitrary JS project.
  - an empty/suppressed compile (`<Target Name="CoreCompile" />`, or `Sdk=…NoTargets`) (strong)
  - `Sdk="Microsoft.Build.NoTargets…"`, attribute-anchored not substring (strong)
  - a `dist/` folder (strong; absent before first `npm build`, so positive-only, never disqualifying)
  - a `package.json` with a `build` script (medium — shared with PCF/Portals, needs the exclusions first)
  - a bundler config `rollup.config.*` / `webpack.*` / `vite.*` (medium — also shared)
  - folder name contains `WebResources`, not `test` (medium — convention)
  - `.ts`/`.js`/`.html`/`.css` assets in the tree (medium — shared, needs exclusions first)

  With R5 (exactly one expected) and the exclusions applied, a single remaining candidate is the
  WebResources project even on weak signals (elimination). Signals decide only when more than one
  candidate remains; if they can't decide, throw (R9) rather than pick.
- **KD4 — Loud on can't-decide, and "none" is now an error for WebResources (R5).** Two+ confident →
  throw. Ambiguous → throw. Zero → throw (R5 reversed the old "quiet none"). Plugins keep the quiet
  zero (R8). This is the policy the package resolver already holds, applied everywhere.
- **KD5 — PCF: exclude now, draft the detection, verify when built.** `PcfProjectResolver` ships as a
  draft: extension `.pcfproj`, or a `.csproj` with `Microsoft.PowerApps.MSBuild.Pcf` / a sibling
  `ControlManifest.Input.xml`. Used only to *exclude* from WebResources for now. When PCF becomes a
  first-class Flowline project type, its detection gets its own verification pass against real controls
  — the draft is a starting point, not a settled contract.
- **KD6 — Future: Power Apps Portals and Code Apps will break elimination — but only in multi-candidate
  repos.** Both are TS/JS/React projects built with Rollup/webpack/vite + `package.json` — structurally
  indistinguishable from a WebResources project by content signals, and *not* excluded today. The risk
  is confined by KD3's arithmetic rule: they are added to a solution *after* the WebResources project
  exists, so their only effect is to create a **second** web-like candidate. As long as a repo has one
  web-like candidate, detection is safe; the false-positive can only arise once a Portal/Code App sits
  *beside* a WebResources project, which is exactly the multi-candidate path. So the required future
  work is a positive Portal/Code-App exclusion that fires on the multi-candidate path — the single
  candidate case never needs it. Recorded so the assumption (these are added later) is visible, not
  silent; it is the product owner's stated assumption, safe because its failure mode is bounded to
  multi-candidate repos.
- **KD7 — Removing the fallbacks is safe because clone never used them.** The conventional fallbacks
  served the no-solution-file case. Clone scaffolds with literals (`ScaffoldedPackageFolder`,
  `SetupPluginsProjectAsync`) and never calls the resolvers, so removing the fallbacks only affects a
  non-clone command run in a repo with no solution file — which R6 makes an error pointing at
  stand-alone mode. `folder-structure.md` §6 (old-layout repos) is unaffected: those have a solution
  file referencing their projects.
- **KD8 — Rename the "Package" concept to "Dataverse solution project" (pending final sign-off).** The
  `Package*` names (`PackageFolder`, `ResolvePackageProjectAsync`, `packageFolder`, `PackageSrcRoot`,
  the deleted `PackageName`) are legacy from the old `Package/` folder and `Package.cdsproj` filename,
  both since renamed. The name is now doubly wrong: it describes neither the folder (`Solution/`) nor
  the concept, and it collides with the Dataverse **plugin package** (`.nupkg`, `IsPackagePush`,
  `SyncSolutionFromPackageAsync`) — a genuinely different thing. Microsoft's own term, grounded in the
  `pac solution` reference ("Commands for working with **Dataverse solution projects**";
  `pac solution init` "Initializes a directory with a new **Dataverse solution project**"), is
  **Dataverse solution project** for the `.cdsproj`. Recommended rename: `DataverseSolutionProject`
  (project/`.cdsproj`), `DataverseSolutionFolder` (was `PackageFolder`), `DataverseSolutionSource`
  (was `PackageSrcRoot` / `src/`), `DataverseSolutionProjectResolver` (already in KD2). Chosen over
  bare `SolutionProject` because unqualified "solution" in this very plan means the MSBuild
  `.sln`/`.slnx` (`SolutionLayout`, `MsBuildSolutionProject`), so the "Dataverse" qualifier is what
  keeps the two apart. Final form is the product owner's call — this KD records the recommendation and
  the CONCEPTS.md term to define.

## Deferred to Implementation

- Per-signal weights and the confidence bar in KD3 — tune against the AE fixtures; "any strong, or two
  mediums, or a single remaining candidate" is the starting rule.
- Whether `PluginProjectResolver` is renamed to `PluginProjectsResolver` or kept and merely called by
  the facade — decide by which produces the smaller, clearer diff.
- The final `SolutionLayout` class name (KD1) and the `DataverseSolutionProject` rename form (KD8).

## Implementation Units

### U1. `SolutionLayout` facade + one-read classification

**Goal:** one class reads the solution file once and exposes all project types; no solution file throws (R6).
**Requirements:** R1, R2, R3, R4, R6.
**Files:** new `src/Flowline.Core/Services/SolutionLayout.cs`; new tests.
**Approach:** `LoadAsync` finds and parses the file (throws if absent), runs the resolvers over the shared list, verifies, and caches the results on the instance. Start by delegating to the existing resolver bodies so the facade lands before their internals change.
**Verification:** one parse per load (assert via a counting reader or by construction); absent solution file throws with the stand-alone hint.

### U2. Split and harden the project resolvers

**Goal:** cdsproj, plugin, and WebResources detection are three tested resolvers with consistent loud-on-uncertain rules.
**Requirements:** R7, R8, R9, R10, KD3, KD4.
**Files:** new `DataverseSolutionProjectResolver`, `WebResourcesProjectResolver`; move plugin logic to `PluginProjectsResolver`; retire `ProjectLayoutResolver`.
**Approach:** WebResources = elimination (not package/plugin/PCF/test) + weighted signals (KD3); required-and-exactly-one (R5/R9); PCF exclusion (R10). Keep it build-free (R3).
**Test scenarios:** AE2, AE3, AE4, AE5, AE6, AE7, AE8, AE9.
**Verification:** the ClientHooks shape resolves; a relocated/converted project that the old substring missed now throws at the drift gate instead of passing blind (closes review finding A); two WebResources projects throw (closes finding F); a PCF project is never selected.

### U3. Draft `PcfProjectResolver` (exclusion only)

**Goal:** PCF is reliably excluded; a documented draft exists for future detection.
**Requirements:** R10, KD5.
**Files:** new `PcfProjectResolver` (draft).
**Approach:** `.pcfproj` extension, or `.csproj` with `Microsoft.PowerApps.MSBuild.Pcf` / sibling `ControlManifest.Input.xml`. Exposed for the WebResources exclusion; not yet a supported project type.
**Test scenarios:** AE5, AE6.
**Verification:** both real reference PCF shapes are excluded.

### U4. Wire consumers to `SolutionLayout`; remove the fallbacks

**Goal:** every command resolves through `SolutionLayout`; no conventional fallback remains.
**Requirements:** R1, R4, R6, KD7.
**Files:** `DeployCommand`, `SyncCommand`, `PushCommand`, `StatusCommand`, `GenerateCommand`, `DriftCommand`, `PluginWebResourceDriftChecker`, `NamespaceDeriver`.
**Approach:** replace the three separate resolver calls per command with one `SolutionLayout.LoadAsync`. Delete `ConventionalCandidate` / `ConventionalWebResourcesProject`. Confirm clone still scaffolds with literals only.
**Verification:** full suite green; a no-solution-file repo errors (not falls back) on every non-clone command; clone still works end to end.

### U5. Docs, audit, and the future note

**Goal:** the detection rules and the future gaps are written where the next reader finds them.
**Requirements:** R5, R6, R10, R11, KD5, KD6.
**Files:** `docs/folder-structure.md` (§3 discovery, §6 old-layout — reconcile the 122-vs-132 contradiction the review found), `CONCEPTS.md` (define `SolutionLayout`; rename the "Package folder" glossary entry to "Dataverse solution project" per KD8), wiki `08-WebResources-Project.md`; a `docs/solutions/` entry recording the three-detection audit and the SolutionLayout consolidation.
**Note:** the KD8 rename (`Package*` → `DataverseSolution*`) is a mechanical sweep touching many files; land it as its own unit/commit so it does not obscure the detection logic in review.
**Approach:** state the per-type rules and the loud/quiet policy; record the marker/config last resort (R11) without building it; record PCF as excluded-and-drafted (KD5) and Portals/Code Apps as future (KD6).
**Verification:** doc claims cite source `file:line`; no claim the code doesn't back.

## Verification Contract

- `dotnet build Flowline.slnx` clean; `dotnet test Flowline.slnx` green with no new skips.
- One solution-file parse per `SolutionLayout` load.
- The ClientHooks shape (no NoTargets, no WebResources folder name) resolves as the WebResources project.
- A relocated/converted WebResources project the old substring missed now makes the deploy drift gate
  throw instead of silently passing (closes review finding A at the source).
- Two WebResources projects throw (closes finding F).
- A PCF project is never selected (R10); both reference shapes covered.
- A repo with no solution file errors on every non-clone command, naming stand-alone mode (R6).
- A valid solution file with no WebResources project throws (R5); with no plugin project resolves (R8).

## Definition of Done

- `SolutionLayout` is the one place that reads the solution file and resolves projects; the three
  ad-hoc resolvers are gone or moved under it.
- WebResources detection meets the standard the package and plugin resolvers already hold: loud when
  it can't decide, and — per R5 — an error when the required project is absent.
- The fallbacks are removed; no solution file is an error pointing at stand-alone mode.
- PCF is excluded and drafted; Portals/Code Apps are recorded as future work (KD6).
- Docs describe the rules; the audit and consolidation are captured in `docs/solutions/`.
