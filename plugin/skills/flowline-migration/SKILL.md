---
name: flowline-migration
description: Migrates a Dataverse project off spkl, Daxif, PACX, or ALM Accelerator onto Flowline. Use when a repo shows a spkl.json, a Daxif _Config.fsx or *.daxif, a .pacxproj, or an ALM Accelerator-style Azure DevOps pipeline with Power Platform Build Tools tasks, and no .flowline exists yet.
---

# Flowline migration — spkl / Daxif / PACX / ALM Accelerator

<!-- Paired with Flowline.wiki/11-Migration-from-spkl.md through 14-Migration-from-ALM-Accelerator.md (KTD7) — an edit to either side should prompt a check of the other. -->

## Detect

Recognize these signatures unprompted, the same way the `flowline` skill detects `.flowline`:

| Tool | Signature |
|---|---|
| spkl | `spkl.json` at the project root |
| Daxif | `_Config.fsx`, `*.daxif`, other `.fsx` scripts referencing `Daxif`/`DG.Daxif`, or a Daxif NuGet package reference in a `.csproj` |
| PACX | `.pacxproj` at the project root |
| ALM Accelerator | An Azure DevOps pipeline (`azure-pipelines.yml` or `.ado/`) referencing Power Platform Build Tools tasks (`PowerPlatformExportSolution`, `PowerPlatformImportSolution`, etc.), with no code-first plugin/web-resource tooling alongside it. This signature is softer than the other three — confirm with the user before assuming it's ALM Accelerator rather than a hand-rolled pipeline. |

**Dual-signature tie-break:** if `.flowline` is already present *and* one of these signatures is also present (e.g., a leftover `spkl.json` from a completed migration), stay silent — the repo is already migrated, don't re-offer.

On a match, proactively offer: *"This looks like a `<tool>` project — want me to migrate it to Flowline?"*

## Guide

Every guide follows the same two-phase strategy: **Phase 1 (standalone)** replaces the old tool for plugin push, web resource push, and type generation with no project restructuring — run `flowline push <Solution> --pluginFile <dll> --dev <url>` / `--webresources <folder>` from a folder with no `.flowline`, always with `--dry-run` first. **Phase 2 (project)** runs `flowline clone`/`init` to adopt the `.flowline` config and folder convention, then replaces the old tool's registration syntax with Flowline attributes. Don't skip straight to Phase 2 — confirm Phase 1 is stable first, per each guide's own recommendation.

### From spkl (`Flowline.wiki/11-Migration-from-spkl.md`)

- Replace `[CrmPluginRegistration(...)]` with `[Step]`/`[Filter]`/`[PreImage]`/`[PostImage]`. Stage and message come from the **class name** (e.g. `AccountPreUpdatePlugin`), not attribute arguments; use `[Handles]` if the class can't be renamed.
- `[CrmPluginRegistration("dev1_MyApi")]` (links an existing Custom API) becomes `[CustomApi]` (Flowline creates and manages the record). Pin `UniqueName` if the live name doesn't match Flowline's class-name convention.
- Web resources: drop `spkl.json`'s explicit file mapping — Flowline derives the Dataverse name from the folder path under `WebResources/dist/`.
- `spkl earlybound` → `flowline generate`; `spkl unpack` → `flowline clone`/`sync`; `spkl import` → `flowline deploy`.
- No equivalent for `spkl instrument` (reverse-engineering existing registrations) or `SecureConfiguration` — see the wiki guide's "Known gaps" for workarounds.

### From Daxif (`Flowline.wiki/12-Migration-from-Daxif.md`)

- Replace the fluent `RegisterPluginStep<T>(...)` calls in the `Plugin` base class constructor with `[Step]`/`[Filter]`/`[PreImage]`/`[PostImage]` attributes on a plain `IPlugin` class — Flowline detects any `IPlugin` implementor, including through a custom base class.
- `CustomAPI` base class → `[CustomApi]` + `[Input]`/`[Output]`.
- Web resource naming convention (`{prefix}_{solution}/{path}`) is the same, but Daxif's folder already contains the full prefixed path while Flowline derives it from `WebResources/dist/`. Watch the case-sensitivity note in the wiki guide — Flowline lowercases the solution name.
- `.fsx` scripts (`PluginSyncDev.fsx`, `SolutionExportDev.fsx`, etc.) map to `flowline push`/`sync`/`deploy`/`generate` calls — see the wiki guide's script-mapping table.
- No equivalent for Daxif's data migration module, per-step enable/disable, or TypeScript generation via XrmDefinitelyTyped.

### From PACX (`Flowline.wiki/13-Migration-from-PACX.md`)

- PACX covers much more than Flowline (tables, views, relationships, data) — this migration only replaces the **overlap**: plugin push, web resource push, auth, project setup. Keep PACX for everything else.
- `pacx plugin step register` (manual, per step) is replaced entirely by Flowline reading `[Step]`/`[Filter]`/`[PreImage]`/`[PostImage]` attributes from the compiled assembly in one pass.
- Web resources: if an existing file path already starts with `{publisherprefix}_/`, Flowline skips its auto-prefix and reproduces PACX's naming exactly — mirror the PACX folder structure inside `dist/` to keep existing Dataverse names unchanged.
- `.wr.pacx` external references: the Dataverse-duplicate-prevention side is automatic in Flowline; the local shared-file side is a build-tool concern (bundle/copy shared sources into each solution's `dist/`).
- `.pacxproj` → `.flowline`, created by `flowline clone`/`init`.

### From ALM Accelerator (`Flowline.wiki/14-Migration-from-ALM-Accelerator.md`)

- **Read this before guiding:** ALM Accelerator assumed a shared DEV environment with PR-gated promotion; Flowline assumes the team owns DEV and DEV is truth. If the user's team does *not* own DEV (shared maker environment, ISV/AppSource model), Flowline is not the right fit — say so and stop, don't force the migration.
- ALM Accelerator didn't define its own plugin registration attributes — it deployed whatever assembly existed. If the project also uses spkl/Daxif/PACX attributes underneath, run that tool's migration guide for the attribute replacement step first.
- `flowline clone` replaces the canvas-app export → ADO branch commit flow in one command; `flowline deploy {env}` replaces the pipeline's pack + import stages.
- No equivalent for per-environment deployment settings (connection references, env vars) or Solution Checker integration — both need manual or CI-side handling; see the wiki guide's "Known gaps".
- No generated CI/CD templates yet — the wiki guide has GitHub Actions and Azure Pipelines starting points to adapt.

## Verify

After the user completes the steps above, confirm the migration actually works — don't just trust that the steps were followed:

1. Run `flowline push --dry-run` (same command regardless of migration depth).
2. Run the phase-appropriate second check:
   - **Project-mode migration** (Phase 2 done, `.flowline` adopted): `flowline sync`.
   - **Standalone-only migration** (deliberately stopped at Phase 1, no `.flowline`): `pac solution sync` directly — `flowline sync` requires a project and would report `ConfigInvalid` here, which is a mismatch, not a migration failure.
3. If either check exits `12` (`DirtyWorkingDirectory`) right after scaffolding — none of the wiki guides include a commit step between scaffolding and the first push — treat it as "commit the scaffolded `Package/src/` first," not migration failure.
4. Report success or failure honestly, naming which check failed if only one did. Don't report the migration as done just because the guide's steps were followed — only step 2's result decides that. Re-running this verify step after fixing a failure is safe; it doesn't compound the earlier problem.
