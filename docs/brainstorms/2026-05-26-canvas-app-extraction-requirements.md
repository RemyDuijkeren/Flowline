---
date: 2026-05-26
topic: canvas-app-extraction
---

# Canvas App Extraction

## Summary

After `flowline clone` and `flowline sync`, Flowline automatically extracts canvas apps from their `.msapp` binaries into a parallel `CanvasApps/` folder within the solution, making canvas app changes visible as YAML diffs in git. The `.msapp` binary is untouched and remains the build artifact for `pac solution pack`.

---

## Problem Frame

Canvas apps in a Dataverse solution are stored as `.msapp` binary files. When `flowline clone` or `flowline sync` pulls a solution, these binaries land in `src/CanvasApps/` as opaque blobs. No line-level diff is possible; PR reviews cannot surface what actually changed in a canvas app between syncs.

The Power Platform CLI historically handled this with `--processCanvasApps` on `pac solution clone/sync`, which used the PASOPA library to produce diffable source from the binary. That flag is now deprecated. The replacement is `pac canvas unpack` (Preview), which produces structured YAML from an `.msapp` using the `SourceCode` layout. Additionally, `.msapp` files are now zip-native, making zip extraction a viable fallback if the PAC command is unavailable.

---

## Requirements

**Extraction trigger**

- R1. `flowline clone` triggers canvas app extraction after the solution is downloaded from Dataverse.
- R2. `flowline sync` triggers canvas app extraction after the solution is synced from Dataverse.
- R3. If no `.msapp` files are present in the solution after clone or sync, extraction is skipped without output.

**Output**

- R4. Each canvas app is extracted to `solutions/<SolutionName>/CanvasApps/<AppName>/` — one subfolder per app, named after the app.
- R5. The extracted YAML in `solutions/<SolutionName>/CanvasApps/` is committed to git as part of the solution (not gitignored).
- R6. The `.msapp` binary in `src/CanvasApps/` is not modified, moved, or removed.

**Resilience**

- R7. If extraction fails for an individual canvas app, Flowline emits a warning for that app and continues — clone or sync does not fail because of an extraction failure.
- R8. On re-extraction (subsequent sync), previously extracted YAML is overwritten to reflect the current `.msapp` state.

**User feedback**

- R9. Flowline reports which canvas apps were extracted (count and names) after each clone or sync.

---

## Acceptance Examples

- AE1. **Covers R3.** Given a solution with no `.msapp` files, when `flowline clone` completes, no canvas app extraction output is shown and no `CanvasApps/` folder is created.

- AE2. **Covers R7.** Given a solution with two canvas apps where extraction fails for one, when `flowline sync` runs, Flowline emits a warning for the failing app, reports the successful extraction of the other, and exits with success.

- AE3. **Covers R8.** Given `CanvasApps/MyApp/` already exists from a prior sync, when `flowline sync` runs and the canvas app has changed in Dataverse, the YAML in `CanvasApps/MyApp/` reflects the updated state.

---

## Success Criteria

- Canvas app changes in Dataverse appear as YAML diffs in `git diff` and PR reviews after `flowline sync`.
- Clone and sync succeed without regression for solutions that contain no canvas apps.
- The `.msapp` binary still packs correctly via `pac solution pack` after extraction runs.

---

## Scope Boundaries

- **Round-trip (pack) not in scope** — editing YAML and repacking to `.msapp` before `flowline push` is deferred until `pac canvas pack` exits Preview and stabilizes.
- **Opt-in flag not in scope** — extraction is automatic; no `--canvas` flag to enable it.
- **PP Git Integration not targeted** — Flowline's extraction works without Managed Environment licenses.
- **`--processCanvasApps` on `pac solution`** — deprecated, not used.

---

## Key Decisions

- **Parallel `CanvasApps/` folder over in-place in `src/`** — keeps `src/` as PAC's territory, consistent with the Plugins/ and WebResources/ pattern where Flowline-managed content lives in parallel folders.
- **Read-only extraction** — the `.msapp` binary stays the build artifact; no round-trip until `pac canvas pack` is stable.
- **Automatic, not opt-in** — convention over configuration; consistent with how Flowline handles Plugins and WebResources scaffolding.
- **`SourceCode` layout** — current supported PAC canvas layout; `Experimental` is deprecated.
- **Primary + zip fallback** — `pac canvas unpack` (Preview) is primary; zip extraction is the fallback since `.msapp` files are zip-native for modern authoring versions.

---

## Dependencies / Assumptions

- `pac canvas unpack` is Preview. If Microsoft removes it, zip extraction covers modern apps. Legacy PASopa-serialized `.msapp` files are not handled by the zip fallback.
- All canvas apps in scope use the modern authoring format (zip-native `.msapp`).
- `solutions/<SolutionName>/CanvasApps/` is not referenced or read by `pac solution pack` — PAC only reads from `src/`.

---

## Outstanding Questions

### Deferred to Planning

- [Affects R4][Technical] How is the `<AppName>` subfolder name derived — strip the `.msapp` extension only, or some other convention?
- [Affects R1, R2][Technical] Does extraction run inside the existing PAC spinner/status block, or as a separate step with its own status line?
- [Technical] `docs/folder-structure.md` needs updating to include `CanvasApps/<AppName>/` in the solution folder spec.
