---
title: Deploy — Orphan Component Cleanup
date: 2026-06-07
status: ready-for-planning
origin: brainstorm session 2026-06-07
idea-source: docs/ideation/2026-06-07-deploy-command-ideation.md (idea #7)
feature-doc: docs/Features/FR-orphan-cleanup.md
---

# Deploy — Orphan Component Cleanup

## Summary

Integrate orphan component cleanup into `flowline deploy` as both a **pre-import** and **post-import** step. Query the target environment's `solutioncomponent` table to diff against the incoming solution's component list. Auto-delete safe operational orphans (plugin steps, web resources, workflows) and produce an explicit report of data-bearing orphans requiring manual action.

Two auto-actions: **DELETE** (component only in this solution) and **REMOVE FROM SOLUTION** (component shared with other solutions). The classifier and cleanup service live in their own objects for iterative extension. The `--no-delete` flag suppresses all auto-actions; the report still prints.

**Strategic importance:** This is Flowline's direct answer to ALM teams that require managed solutions for deployment hygiene. With orphan cleanup, unmanaged acts like managed for the components that matter most. This feature needs to be solid and extensible — it is core to Flowline's competitive positioning.

---

## Problem Frame

**Problem:** Unmanaged solution import is purely additive. When deploying v2.0 to a target running v1.0, Dataverse adds and updates — but never removes deleted components. Plugin steps keep firing, web resources stay published, workflows keep running. The environment silently drifts from the intended state after every deploy.

**Hard failure case:** When a plugin class is removed from the DLL, Dataverse rejects the DLL upload because the orphaned `plugintype` record references a class that no longer exists. The deploy is blocked until the orphan is deleted first. Cleanup is not optional in this scenario — it is a prerequisite for deploy success.

**Strategic position:** The primary practical objection to unmanaged solutions in enterprise Power Platform projects is "managed keeps the environment clean after each deploy." This feature closes that gap for operational components and provides an explicit, reviewable report for data-bearing components. Combined with Flowline's Git-native workflow, unmanaged becomes a credible alternative for teams that need both branch-based development and deployment hygiene.

---

## Actors

- **A1 — Developer deploying to test/UAT/prod:** runs `flowline deploy`, expects the environment to match source control after deploy
- **A2 — Release manager reviewing orphan report:** wants clear, actionable output for manual actions
- **A3 — Consultant managing multiple solutions in one environment:** needs cross-solution safety before any auto-delete

---

## Key Flows

### F1 — Pre-import step

1. Query `solutioncomponent` on target for this solution → S_old (current component set)
2. Parse `customizations.xml` from local solution source → S_new (incoming component set)
3. Orphans = S_old − S_new
4. For each orphan: classify as AUTO or MANUAL (see requirements R4–R6)
5. For AUTO candidates: query cross-solution membership; downgrade action to REMOVE FROM SOLUTION if shared
6. Print orphan report (DELETE / REMOVE FROM SOLUTION / MANUAL)
7. Unless `--no-delete`: execute deletions and removals in safe order (R8–R9); skip dependency-blocked components (pass to F2)

### F2 — Post-import step

1. After `pac solution import` completes: re-query `solutioncomponent` on target
2. Diff against S_new again — dependency-blocked components from F1 may now be removable
3. For remaining orphans: re-classify and re-check cross-solution membership
4. Execute remaining AUTO deletions and REMOVE FROM SOLUTION operations
5. Append post-import actions to orphan report

---

## Requirements

### Component diff (R1–R3)

**R1.** Query `solutioncomponent` on the target environment for the deploying solution to produce S_old.

**R2.** Parse `customizations.xml` from the local solution source folder to produce S_new.

**R3.** Orphan set = S_old − S_new. Components in S_new not in S_old are not orphans — they are new components handled by the import.

### Component classification (R4–R7)

**R4.** Classify each orphan as AUTO or MANUAL based on component type before executing any actions.

**R5.** AUTO components — auto-deleted (solo) or auto-removed from solution (shared):

| Component | Logical name | Notes |
|---|---|---|
| Plugin assembly | `pluginassembly` | |
| Plugin type | `plugintype` | |
| Plugin step | `sdkmessageprocessingstep` | |
| Step image | `sdkmessageprocessingstepimage` | |
| Custom API | `customapi` | |
| Custom API request parameter | `customapirequestparameter` | Cascades from Custom API delete |
| Custom API response property | `customapiresponseproperty` | Cascades from Custom API delete |
| Web resource | `webresource` | |
| Workflow / classic process | `workflow` | Deactivate before delete (see R9) |

**R6.** MANUAL components — listed in report, never auto-acted upon:

| Component | Why |
|---|---|
| Table (entity) | Data loss risk |
| Column (attribute) | Data loss risk |
| Relationship | Referential integrity |
| Form, View, Chart, Dashboard | May be referenced by app modules or other solutions |
| Security role | Users depend on it |
| Global option set | Columns reference it |
| Site map, App module | Navigation / app container |
| Connection reference | Flows depend on it |
| Environment variable | Flows and plugins depend on it |

**R7.** For each AUTO candidate: query `solutioncomponent` across all solutions in the target for the component's objectid. If found in at least one other active solution: action = REMOVE FROM SOLUTION (using `RemoveSolutionComponentRequest`). If found only in this solution: action = DELETE.

### Execution order and deactivation (R8–R10)

**R8.** Deletion order: step images → plugin steps → plugin types → plugin assemblies; then custom APIs (request parameters and response properties cascade from parent delete); then web resources; then workflows (deactivate-then-delete).

**R9.** Workflow deactivation: before deleting a workflow, set `statecode` to Draft via SetStateRequest. If deactivation fails, skip deletion and add the component to the MANUAL list with a note: "deactivation failed — delete manually."

**R10.** Dependency-blocked components: if a delete call returns a dependency error, catch it specifically, skip the component, and pass it to the post-import step (F2). Do not swallow other errors.

### Pre-import step (R11–R12)

**R11.** Run F1 before `pac solution import`. Pre-import cleanup is required to handle the plugin-class-removal case: orphaned `plugintype` records must be deleted before Dataverse will accept the new DLL.

**R12.** Pre-import step executes AUTO deletions and REMOVE FROM SOLUTION operations that succeed without dependency errors. Dependency-blocked components are deferred to F2.

### Post-import step (R13–R15)

**R13.** Run F2 after `pac solution import` completes. Post-import re-scans for orphans that could not be cleaned pre-import.

**R14.** Re-classify and re-check cross-solution membership in F2 — state may have changed during import.

**R15.** Post-import failures do not roll back the import. Report them as MANUAL with a note.

### Report and flags (R16–R18)

**R16.** Print an orphan report before executing pre-import actions. Append post-import actions to the same report. Report shows component name, type, and action.

**R17.** `--no-delete` flag on `deploy` suppresses all AUTO actions (DELETE and REMOVE FROM SOLUTION). Report still prints with what would have happened. Consistent with `push --no-delete`.

**R18.** Report summary line: "N deleted, N removed from solution, N require manual action." When `--no-delete` is set: "N would be deleted, N would be removed from solution (--no-delete active). N require manual action."

### Service design (R19)

**R19.** Implement `ComponentClassifier` and `OrphanCleanupService` as distinct service objects. DeployCommand orchestrates; services own the domain logic. Component type handling must be extensible without modifying deploy orchestration — this is a hard requirement, not a nice-to-have.

---

## Acceptance Examples

**AE1 — DELETE: solo component**

```
flowline deploy test
  Checking MySolution...
  Orphan: OldHandler (Plugin type) — only in this solution
  Action: DELETE
  Report: OldHandler | Plugin type | DELETE
  Deleted OldHandler.
```

**AE2 — REMOVE FROM SOLUTION: shared component**

```
flowline deploy test
  Orphan: pub_common/js/shared.js (Web resource) — also in 'CommonSolution'
  Action: REMOVE FROM SOLUTION
  Report: pub_common/js/shared.js | Web resource | REMOVE FROM SOLUTION — also in 'CommonSolution'
  Removed from solution.
```

**AE3 — MANUAL: data-bearing component**

```
flowline deploy test
  Orphan: legacy_data (Table) — MANUAL
  Report: legacy_data | Table | MANUAL
  No auto-action taken.
```

**AE4 — Post-import fallback: dependency-blocked step**

```
Pre-import: AccountPlugin:PostCreate (Plugin step) — blocked by dependency on AccountPlugin assembly.
Skip pre-import.
[pac solution import runs]
Post-import: Re-scan. AccountPlugin:PostCreate — dependency resolved. DELETE.
Report: AccountPlugin:PostCreate | Plugin step | DELETE (post-import)
```

**AE5 — `--no-delete` active**

```
flowline deploy test --no-delete
  [Orphan report prints]
  2 would be deleted, 1 would be removed from solution (--no-delete active). 3 require manual action.
  [No deletions or removals executed]
```

**AE6 — Workflow deactivation before delete**

```
Orphan: Send Legacy Notification (Workflow)
Pre-delete: SetStateRequest → Draft. Success.
Delete succeeds.
Report: Send Legacy Notification | Workflow | DELETE
```

**AE7 — Workflow deactivation failure → MANUAL**

```
Orphan: Send Legacy Notification (Workflow)
Pre-delete: SetStateRequest → Draft. Failed (insufficient privilege).
Skip deletion.
Report: Send Legacy Notification | Workflow | MANUAL — deactivation failed, delete manually
```

**AE8 — Hard failure case: plugin class removed from DLL**

```
MyPlugin.OldHandler removed from DLL in v2.0.
Pre-import: OldHandler (plugintype) → classified AUTO → solo → DELETE.
Delete executes before pac solution import.
DLL upload succeeds — no orphaned plugintype references the removed class.
```

---

## Success Criteria

1. After deploy, no orphaned plugin steps, web resources, or workflows remain for components removed from the solution.
2. Hard failure case (plugin class removed) resolves automatically — deploy succeeds without manual pre-cleanup.
3. Shared components are never deleted — REMOVE FROM SOLUTION preserves them for other solutions.
4. Every deploy produces an actionable report of MANUAL components.
5. `--no-delete` suppresses all auto-actions; report still prints.
6. No data-bearing components (tables, columns, forms, roles, etc.) are auto-acted upon.
7. Component handling is extensible without modifying DeployCommand.

---

## Scope Boundaries

### In scope

- Pre-import orphan cleanup for operational components
- Post-import orphan cleanup for dependency-blocked orphans
- Cross-solution safety check (DELETE vs REMOVE FROM SOLUTION)
- Orphan report for MANUAL components
- `--no-delete` flag

### Deferred for later

- **Ownership stamps** (`[flowline:solution=...]` on `description` fields) — deferred entirely. solutioncomponent membership + component type is sufficient authority for initial implementation. Add only if practice reveals false positives or false negatives.
- **Extended report enrichment** (data presence checks, app module references, user assignment counts) — described in FR-orphan-cleanup.md; not part of initial implementation.
- **State restoration** — separate feature (`FR-state-restoration.md`); wraps the same deploy step independently.

### Outside this feature's scope

- Managed solution deployment (Flowline is unmanaged-first)
- Patch solution support
- Workflow owner reassignment across environments
- Data migration for deleted tables

---

## Key Decisions

**Pre-import AND post-import (not pre-only).** Pre-import handles the hard failure case — plugin class removal requires deleting the orphaned `plugintype` record before Dataverse will accept the new DLL. Post-import catches dependency-blocked orphans: some components cannot be deleted before the new version is imported (the new DLL or component it depends on isn't present yet). Both steps are needed for comprehensive cleanup.

**DELETE vs REMOVE FROM SOLUTION.** Cross-solution `solutioncomponent` query determines which operation applies. REMOVE FROM SOLUTION is the correct Dataverse operation when a component is shared — it unlinks the component from this solution without destroying the record. Deleting a shared component would silently break other solutions. SKIP (the original approach in FR-orphan-cleanup.md) was rejected because it leaves the orphan in the solution with no resolution.

**Auto-delete by default, `--no-delete` to suppress.** Mirrors managed solution behavior. The default is action, not review. `--no-delete` is for cautious first-deploys or environments where the operator wants to review first. Consistent with `push --no-delete`.

**Query approach for orphan detection (primary).** Query `solutioncomponent` at deploy time. Fast, ID-based, no extra files. **Alternative considered and documented:** export the target solution, unpack to temp folder, compare with local source XML. This is content-based and adds significant deploy time (full solution export + unpack). Better suited to `--dry-run` (feature #6) where content-level diff is the point. Not appropriate for orphan detection where component identity (objectid) is sufficient.

**Service layer mandatory.** `ComponentClassifier` and `OrphanCleanupService` as own objects. DeployCommand orchestrates; services own the domain logic. This enables component type handling to be extended iteratively without touching deploy orchestration. This is a hard requirement — the feature is expected to grow as more component types are covered and more edge cases are discovered.

**Ownership stamps deferred.** FR-orphan-cleanup.md proposed stamps on `description` fields as confirmation of Flowline management. Valid idea, but adds write-side coupling on every `push` and is not needed for initial implementation. solutioncomponent membership and component type are sufficient. Add stamps only if practice shows they're needed.

---

## Dependencies and Assumptions

- **`solutioncomponent` Web API is available** — standard Dataverse API; assumed available on all supported environments.
- **`RemoveSolutionComponentRequest` is available** — standard SDK message. Needs verification: is this exposed through PAC CLI, or does it require a direct Dataverse Web API call?
- **`customizations.xml` lists all component objectids** — assumes local source XML is complete and accurate after `flowline sync`. Needs verification for large solutions or solutions with managed layers.
- **Post-import query reflects final state** — assumes a `solutioncomponent` query after `pac solution import` returns the actual post-import state. Async import may require polling for completion before re-querying.
- **Cross-solution query returns complete results** — requires read access to all solution definitions in the target. Restricted service accounts may return incomplete results.

---

## Outstanding Questions

- **`RemoveSolutionComponentRequest` parameters** — does it take the solution uniquename or solutionid? Verify before implementation.
- **Async import timing** — does Flowline need to poll for import completion before running the post-import step? `pac solution import --async` may return before Dataverse finishes processing.
- **Custom API child record cascade** — are `customapirequestparameter` and `customapiresponseproperty` records automatically deleted when the parent `customapi` is deleted? If not, must explicitly delete child records first in the right order.
- **Managed component filtering** — does the `solutioncomponent` query return managed components from managed layers installed in the same environment? Must filter to unmanaged-only to avoid falsely classifying managed components as orphans.
