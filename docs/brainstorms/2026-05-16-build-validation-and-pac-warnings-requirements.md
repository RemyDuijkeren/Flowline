---
date: 2026-05-16
topic: build-validation-and-pac-warnings
---

# Remove Mapping — Simplify Pack and Deploy

## Summary

Remove all mapping files (`MappingPac.xml`, `MappingBuild.xml`) and all mapping logic from Flowline. Sync and clone download everything to `src/` directly. Pack and deploy use `src/` directly. Replace `dotnet build` in clone and deploy with `pac solution pack --folder src/` (no `--map`).

---

## Problem Frame

Flowline uses two mapping files to redirect where `pac solution sync` extracts files and where `pac solution pack` reads them from:

- `MappingPac.xml` — redirects web resources from `src/WebResources/` to `WebResources/dist/` and plugin DLLs from `src/PluginAssemblies/` to `Plugins/bin/Release/`
- `MappingBuild.xml` — a second file needed because one file could not be made to work for both directions

This creates cascading problems: web resources added in Dataverse produce "Solution may not repack" warnings during sync, clone's `dotnet build` conflicts with the Release mapping because it only produces Debug binaries, and `CloneWebResourcesFromDataverseAsync` exists solely to bootstrap `dist/` because sync skips web resource files when mapping is active.

More fundamentally: mapping in the pack direction is a **correctness risk**. After the correct workflow (push → sync), `src/` holds exactly what was verified in Dataverse DEV. Packing from `dist/` or `Plugins/bin/` instead means the deploy could include locally built changes that were never pushed to DEV. The safe and deterministic path is:

```
source → push → Dataverse DEV → sync → src/ → pack → deploy
```

`src/` is the record of what was confirmed in DEV. That is what should be packed and deployed. Mapping added complexity and a correctness risk with no benefit in this ALM workflow.

---

## Requirements

**Remove all mapping**

- R1. Remove the `--map` flag from `pac solution sync` in `SyncCommand`.
- R2. Remove the `--map` flag from `pac solution sync` in `CloneCommand`.
- R3. Delete `MappingPac.xml` and `MappingBuild.xml` from the solution scaffold.
- R4. Remove `WriteMappingFilesAsync` from `CloneCommand`.
- R5. Remove `EnsureMapFilePathAsync` from `CloneCommand` and `SyncCommand`.
- R6. Remove `CloneWebResourcesFromDataverseAsync` from `CloneCommand` — web resources now land in `src/WebResources/` via the no-mapping sync; a separate download to `dist/` is no longer needed.

**Sync cleanup**

- R7. Remove the commented-out `dotnet build` block from `SyncCommand`.

**Post-sync drift check**

- R8. After sync completes, if `WebResources/dist/` exists, compare each file in `src/WebResources/` against its counterpart in `dist/` by content hash. Report three categories:
  - Content differs — file exists in both but Dataverse holds a different version than local dist/
  - New in Dataverse — file exists in `src/WebResources/` but not in `dist/` (added directly in Dataverse)
  - Only local — file exists in `dist/` but not in `src/WebResources/` (local change not yet pushed)
- R9. After sync completes, if `Plugins/bin/Release/` contains a DLL matching the assembly name in `src/PluginAssemblies/`, compare file sizes. Warn if the size difference exceeds a reasonable threshold (to be determined in planning — accounts for minor build metadata variation). Include the hint: "Local plugin build may differ from what is deployed — rebuild and push if intentional."
- R10. Drift check warnings are non-blocking — sync exits 0 regardless. Each warning includes an action hint: "run 'flowline push'" for local-only files, "check who changed this in Dataverse" for Dataverse-only or differing files.
- R11. If neither `dist/` nor `Plugins/bin/Release/` exists, skip the drift check silently — the developer has not built locally and no comparison is possible.

**Clone validation**

- R12. Replace `BuildSolutionAsync` (dotnet build) in `CloneCommand` with `pac solution pack --folder src/`. Since everything downloaded to `src/` (R2), pack succeeds without `dist/` or compiled plugin binaries. Clone exits non-zero if pack fails — a failing pack indicates the downloaded solution structure is broken.

**Deploy pre-flight**

- R13. Replace the lazy `dotnet build` in `DeployCommand` with `pac solution pack --folder src/`.
- R14. `pac solution pack` runs unconditionally before the deploy export step — not only when no package file exists.
- R15. Deploy exits non-zero with a clear error if `pac solution pack` fails.

---

## Acceptance Examples

- AE1. **Covers R1, R7.** Given a sync that includes web resources added directly in Dataverse, when sync runs without `--map`, then those web resource files land in `src/WebResources/` and no warnings are emitted.

- AE2. **Covers R2, R6, R12.** Given a fresh clone with no compiled plugin binaries and no `dist/` folder, when clone completes, then web resource files and plugin DLLs are in `src/`, `pac solution pack --folder src/` succeeds, and clone exits 0.

- AE3. **Covers R13, R14, R15.** Given a deploy run immediately after sync (src/ is populated, dist/ does not exist, Plugins/bin/ does not exist), when deploy runs, then `pac solution pack --folder src/` succeeds and the export step proceeds — no dependency on dist/ or Plugins/bin/.

- AE4. **Covers R14.** Given a prior package zip already on disk, when deploy runs, then `pac solution pack` still runs unconditionally and produces a fresh package before export.

- AE5. **Covers R8, R10.** Given a sync after which `dist/` contains `images/logo.png` at version A, but `src/WebResources/` contains `images/logo.png` at version B (changed directly in Dataverse), when the drift check runs, then Flowline prints a warning naming the file and suggests checking who changed it in Dataverse.

- AE6. **Covers R11.** Given a sync on a machine with no `dist/` folder and no `Plugins/bin/Release/`, when sync completes, then no drift check runs and no warnings are emitted about missing local builds.

---

## Success Criteria

- Sync never emits "may not repack" warnings regardless of what was added in Dataverse.
- Clone succeeds on a machine with no compiled plugin binaries and no `dist/` folder.
- Deploy packs exactly what was confirmed in Dataverse DEV — no locally built artifacts can silently sneak into the package.
- After a push → sync cycle, a developer whose local build matches what was pushed sees no drift warnings. A developer who finds their Dataverse state diverged from local sees a specific, actionable warning.
- `MappingPac.xml`, `MappingBuild.xml`, `WriteMappingFilesAsync`, `EnsureMapFilePathAsync`, and `CloneWebResourcesFromDataverseAsync` are gone.
- `dotnet build` is gone from all Flowline commands.

---

## Scope Boundaries

- `dotnet build` in any Flowline command — excluded. Developer's responsibility.
- Populating `dist/` as part of clone or sync — out of scope. `dist/` is a local build artifact; the developer's build pipeline owns it. Flowline no longer depends on it.
- Idea #4 (`.flowline-sync` metadata stamp) and idea #8 (sync-before-push gate) — deferred; related but independent.
- Full binary comparison of plugin DLLs — size is the proxy; exact binary match is unreliable due to build metadata variation.
- Blocking sync on drift — drift check is informational only, never a gate.
- **Pack-flow / ISV-style builds** — out of scope. Flowline is sync-first: `deploy` packs from `src/`, which holds exactly what was confirmed in Dataverse DEV. Source-driven reproducible builds (no shared DEV env, local artifacts as source of truth, AppSource distribution) are a different model; use `pac solution pack` or ALM Accelerator directly for those workflows.

---

## Key Decisions

- **No mapping anywhere**: Mapping in the download direction caused "may not repack" warnings and a two-file complexity problem. Mapping in the pack direction is a correctness risk — it could deploy locally built artifacts that were never verified in DEV. Removing it makes the workflow deterministic: sync brings down what is in Dataverse, pack puts exactly that into the solution zip.
- **`src/` as the pack source**: After sync, `src/` contains what Dataverse DEV holds — web resource files, plugin DLLs, solution XML. `pac solution pack --folder src/` packs that state exactly. No other input needed.
- **`pac solution pack` replaces `dotnet build`**: Pack validates whether the solution can be zipped for import, without compiling code. `dotnet build` compiled source — not Flowline's responsibility.
- **`CloneWebResourcesFromDataverseAsync` removed**: It existed to populate `dist/` for mapping. Without mapping, web resources land in `src/` via sync and `dist/` is not needed by Flowline.

---

## Dependencies / Assumptions

- `pac solution pack --folder src/` without `--map` packs all files found in `src/`, including binary web resource files and plugin DLLs placed there by `pac solution sync`. Confirmed by PACX's approach and PAC CLI behaviour.
- Web resource binary files (PNG, JS, CSS, etc.) and plugin DLLs will be committed to git as part of `src/`. This is accepted: `src/` is the audit trail of what was in Dataverse.
- The `.cdsproj` project file may reference mapping files (e.g., in MSBuild properties). Planning must verify these references are removed or no longer cause errors when developers run `dotnet build` themselves outside Flowline.
- `UseMapping` flag on `ProjectSolution` and any related config fields become obsolete. Planning should remove or ignore them.

---

## Outstanding Questions

### Resolve Before Planning

_(none)_

### Deferred to Planning

- **[Affects R3, R5]** [Needs research] Identify all places `MappingPac.xml` and `MappingBuild.xml` paths are referenced — `.cdsproj` MSBuild properties, `EnsureMapFilePathAsync`, any config fields — and confirm removal does not break `dotnet build` when developers run it outside Flowline.
- **[Affects R6]** [Needs research] Confirm `CloneWebResourcesFromDataverseAsync` can be removed cleanly. Check whether it does anything beyond downloading web resources to `dist/` (e.g., registering them, writing config) that would need to be preserved or moved.
- **[Affects R13]** [Needs research] Confirm the exact `pac solution pack` invocation: output zip path, working directory, and whether any flags beyond `--folder src/` are needed for a valid solution zip.
- **[Affects R9]** [Needs research] Determine the right size-difference threshold for the plugin DLL drift check. Minor variation is expected due to build metadata (timestamps, GUIDs embedded by the compiler); a threshold of a few KB should filter noise without masking real drift. Validate with sample builds.
