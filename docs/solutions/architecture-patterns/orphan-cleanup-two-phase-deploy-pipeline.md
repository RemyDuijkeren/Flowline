---
title: Orphan component cleanup, two-phase deploy pipeline
date: 2026-06-10
last_updated: 2026-11-01
category: docs/solutions/architecture-patterns
module: DeployCommand
problem_type: architecture_pattern
component: tooling
root_cause: platform_limitation
severity: high
resolution_type: workflow_improvement
applies_when:
  - Deploy imports an unmanaged Dataverse solution and must remove components that were deleted from source
  - Orphan candidates may be shared with other solutions or be system components the solution never owned
  - Deleting a component can fail on a dependency that only resolves after the import
  - A component type is not yet trusted enough to be deleted without a human decision
  - Managed solutions rely on Dataverse's own Upgrade import to remove components
related_components:
  - OrphanCleanupService
  - IOrphanHandler
  - IPostDeployService
  - PostDeployContext
  - ComponentProvenance
  - ExitCode
  - DriftCommand
tags:
  - dataverse
  - orphan-cleanup
  - deploy
  - orphan-handler
  - two-phase
  - dependency-deferral
  - cross-solution
  - false-positive-prevention
  - exit-codes
---

# Orphan component cleanup, two-phase deploy pipeline

## Context

An unmanaged solution import in Dataverse only adds and updates. Remove a plugin class, web resource or workflow from source and the live record stays. These orphans block later DLL uploads (an orphaned plugin type still references the old class), keep firing as steps or workflows, and pile up in long-lived environments.

`deploy` must therefore remove what source no longer declares, without touching components another solution owns, Microsoft system components, or anything whose absence from source Flowline cannot actually prove. Most of the design is about the last clause: a false positive here deletes live customization.

## Guidance

### Model: compare, then hand to handlers, then execute

`OrphanCleanupService` (`src/Flowline.Core/OrphanCleanup/OrphanCleanupService.cs`) does three things.

1. **Compare.** `CompareAsync` (`:151-187`) reads the solution's live components, builds the expected set from the unpacked solution source, and treats the difference as orphan candidates. System components (fixed `00000000-0000-0000-00aa-` id prefix, `ComponentClassifier.IsWellKnownSystemComponent`) are removed from the candidate set up front.
2. **Dispatch.** `DispatchToHandlersAsync` (`:353`) gives each still-unclaimed candidate to the `IOrphanHandler` implementations in `FamilyOrder` (`:65-74`): PluginAssemblyFamily, WebResource, Workflow, CustomApiFamily, Bot, ConnectionReference, Role, EntityFamily (`src/Flowline.Core/OrphanCleanup/Handlers/`). A handler owns matching, live querying and classification for one component family. `ComponentClassifier.cs` is now only scanner and parser helpers for the local source (`ParseLocalSource`, `ParseSolutionXmlComponents`, `Scan*`); the old type-to-action classifier is gone.
3. **Execute.** `ExecuteInOrderAsync` (`:874`) runs the surviving entries in handler order, then each family's own sequence hint.

The compared source is the unpacked zip that is about to be imported, not the working tree (`src/Flowline.Core/Deploy/IPostDeployService.cs:17-23`). Whatever ships is what gets compared.

### Handler contract: maturity, priority, timing

Terms are defined in `CONCEPTS.md` (Orphan handler, Handler status, Orphan priority).

| Axis | Set by | Values | Effect |
|---|---|---|---|
| Handler status (`HandlerStatus.cs`) | the handler, hardcoded | Silent, Report, Guarded, Auto | Silent logs verbosely only. Report surfaces, never deletes. Guarded deletes only under `--force delete-orphans`. Auto deletes. |
| Orphan priority (`OrphanPriority.cs`) | the handler, per instance | Prio1 blocks deployment, Prio2 still runs deleted logic, Prio3 safe to clean up | Report ordering and wording. |
| Orphan timing (`OrphanTiming.cs`) | the handler, per finding | PreImportEligible, PostImportOnly | Whether the entry may be tried before import. |

Current status per handler: Auto for PluginAssemblyFamily and CustomApiFamily; Guarded for WebResource; Report for Workflow, Bot, ConnectionReference, Role and EntityFamily (each `Status` property in `Handlers/*.cs`). `IsReportOnly` (`:508`) is the single mapping from status plus consent to "never execute". Entries marked report-only are skipped inside `ExecuteInOrderAsync`, so there is one chokepoint, not a check per handler. No handler emits PostImportOnly today; the mechanism exists and is exercised only through `RunPreImportAsync` splitting entries (`:131`).

CustomApiFamily, Bot and ConnectionReference have environment-specific component type numbers, so they cannot match by number. Each queries its own backing table, and `DispatchToHandlersAsync` runs those three concurrently on the same candidate batch so one failing table cannot hide another's findings (`:88`, `:420-428`).

A provenance layer (`ComponentProvenance`, `IComponentProvenanceLookup`) attaches Declared, NeverInSource or Undetermined to reported entries from git history of the source. It informs the report. It never changes what gets deleted.

### Execution and dependency deferral

Dataverse refuses to delete a record with dependents (assembly with plugin types, type with steps). Rather than compute a topological order, Flowline orders by family and sequence hint, tries each entry, and treats a dependency fault pre-import as "retry later".

- `TryExecuteEntryAsync` (`:894`) catches `FaultException<OrganizationServiceFault>` pre-import when `IsDependencyError` (`:1144`: error code `0x80047002` or "depend" in the message) is true, and adds the entry to a private `_deferred` list (`:98`, `:917`).
- `RunPostImportAsync` (`:515`) merges `_deferred` with the post-import-only list, re-checks each is still live and still not in source, re-evaluates cross-solution membership, and runs them again. A post-import fault is a warning plus a failed count.
- The state lives in an instance field because the service is a singleton and one command runs per process. The interface and DI shape are in [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md); registration is the forwarding pair at `src/Flowline/Program.cs:387-388`, so `DriftCommand` and the fan-out share one instance.
- Active workflows cannot be deleted. `TryDeactivateWorkflowAsync` (`:951`) drafts the workflow first; if that fails the entry is warned about and left for manual removal (`:906-912`).
- Remaining failures surface as `ExitCode.PartialSuccess = 18` (`src/Flowline.Core/ExitCode.cs`): the deploy worked, cleanup did not fully. `ExitCode.Inconclusive = 19` is returned by `drift` when the comparison was skipped.

### Pipeline order

`DeployCommand.ExecuteFlowlineAsync` (`src/Flowline/Commands/DeployCommand.cs`):

```
validate target -> git-clean check -> DTAP gate -> first-import confirm ->
resolve version and PACK (:288) -> unpack imported zip ->
pre-import services (:332-333) -> import (:345) -> post-import services (:348-349)
```

Pre-import runs after pack, not before it as an earlier version of this doc said. `--dry-run` stops after pre-import (`:338-342`). Pre-import services run for dry run and real deploy alike, so the orphan report is identical.

### Cross-solution components: remove, do not delete

A candidate may belong to another solution. Handlers only ever propose Delete; the orchestrator overrides to RemoveFromSolution when the component is in any other relevant solution (`:436-463`, same re-check post-import at `:550`). Deleting would break the other solution. Removing the membership leaves the record.

### Guards and why

Each guard exists because the matching false positive happened.

- **Empty expected set aborts.** No components parsed from `Solution.xml` would make every live component an orphan. `CompareAsync` warns and returns a skipped result (`:232`). An empty live set is skipped the same way (`:223`). A skipped result is distinct from "no orphans", which is why `drift` can return Inconclusive.
- **Identity shapes that are not plain ids.** A real deploy flagged 53 orphans that were still in source. Entity roots and web resources are recorded by `schemaName`, not id; roles are reconciled by name on import so their id can differ per environment; global option sets are metadata, not table rows; forms and views sit in per-entity folders with no root entry. `CompareAsync` resolves each shape live and unions the result into the expected set (`:240-280`). A shape not resolved is a false orphan, so each new component type needs its local-identity scan before it is allowed to report.
- **Unresolvable identity means skip, never "orphaned".** If a candidate's identity attribute comes back null, local-source verification never ran. Treat that as unverified, not removed (`EntityDetectionHelper.cs:56-60`). Copying an existing handler's default-to-orphan branch once reintroduced exactly this bug.
- **One failure domain per backing table.** Entity-detected handlers each query separately through `DataverseFaultTolerance.TryQueryAsync`; a table missing in some edition (for example no Copilot Studio) only removes that table's candidates, with a warning. A business fault degrades silently; other faults (network, auth, throttling) warn. "Could not ask" is never recorded as "not found".
- **Unclaimed candidates are never acted on.** Anything no handler claims goes to a verbose-only preview (`:432-437`). Connection reference and bot false positives showed that resolving a display name is not verification. A type earns a handler by having a local-source cross-check and tests in both directions.
- **Web resource annotation exemption reads the imported source.** A web resource referenced by a `// flowline:depends` annotation is exempt. The scan reads `WebResources` under the unpacked imported source, never `WebResources/dist` (`src/Flowline.Core/OrphanCleanup/Handlers/WebResourceHandler.cs:20-23`), because deploy ships the committed source and `dist` may not exist on a CI agent. Web resource entries also carry their live dependents into the report.
- **Paging.** All `solutioncomponent` queries use `RetrieveAllAsync` (`src/Flowline.Core/Dataverse/DataverseExtensions.cs`). A plain `RetrieveMultipleAsync` truncates at 5000 and turns the unseen tail into false orphans.
- **`ConditionOperator.In` ceiling.** Dataverse practical limit is 2000 values. `EntityNameLookup` throws at that count (`EntityNameLookup.cs:56-68`) rather than chunking, so an oversized batch degrades like any other failed query instead of silently changing semantics.
- **No delete without trust.** Report and Silent handlers cannot delete regardless of flags. Guarded needs `--force delete-orphans` (`DeployCommand.cs:316`, carried as `DeleteOrphansConsent`).

### Report-only modes and managed solutions

`ResolveRunMode` (`DeployCommand.cs:822`): `--dry-run` gives DryRun, `--no-delete` or a managed solution gives NoDelete, otherwise Normal. Both report-only modes print the full report and skip execution and post-import cleanup (`RunPreImportAsync :118-126`, `RunPostImportAsync :532`).

Managed targets are report-only because the service's `Delete` and `RemoveSolutionComponent` calls target components owned by the installed managed layer. Observed behavior, confirmed in Microsoft documentation for managed processes and for `RemoveSolutionComponent` (unmanaged solutions only): Dataverse rejects such deletes outside its own Upgrade or uninstall path. A managed redeploy of an existing solution uses `--stage-and-upgrade`, and Dataverse removes components itself. The report stays useful as a preview of what the Upgrade removes. `BuildReportOnlyHint` (`:142`) words the reason: dry run, `--no-delete`, managed first install (cleanup waits for a later upgrade), or no hint for an installed managed solution because the report already names who removes things.

## Why This Matters

- Without cleanup, orphaned plugin types block DLL upload, orphaned steps and workflows keep executing removed logic.
- With it, deploy is idempotent for deletions, shared components are unlinked rather than deleted, and CI gets a distinct exit code for "deployed, cleanup incomplete".
- The expensive failure is the false positive. Maturity levels let a new handler ship as Silent or Report, collect live evidence, and be promoted one rung at a time.
- The catches in this service (dependency deferral, workflow deactivation, per-table query isolation) are deliberate exceptions to the repo's no-catch-around-service-calls rule: each has explicit recovery and visible output.

## When to Apply

- A platform import that never removes what source dropped (Dataverse unmanaged import).
- Components with referential integrity, where deleting in the wrong order faults.
- Components shared across solutions, so membership must be checked before delete.
- Adding a new component type to cleanup: write a handler, start at Silent or Report, add its local-identity scan and tests for still-present and genuinely-removed, then promote.

Skip the deferral approach if the platform cascades deletes, or if the dependency graph is cheap to compute upfront.

## Examples

```
$ flowline deploy prod --no-delete
Orphan components (3):
  SdkMessageProcessingStep f8a1...   would delete
  PluginType 92cc...                 would delete
  PluginAssembly 1d73...             would delete
(--no-delete active)
```

```
Deferred: PluginAssembly 1d73... dependency, will retry post-import
[import completes]
Post-import: running orphan cleanup...
```

A post-import failure leaves exit code 18; a skipped comparison in `drift` leaves 19.

## Related

- [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) for the `IPostDeployService` interface, DI registration and deferred-state design.
- [dataverse-orphan-assembly-delete-blocked-by-step-dependencies.md](../integration-issues/dataverse-orphan-assembly-delete-blocked-by-step-dependencies.md) for the dependency fault this deferral handles.
- [reverse-relationship-inverts-what-orphaned-means.md](../design-patterns/reverse-relationship-inverts-what-orphaned-means.md) for why "orphaned" depends on the direction of the comparison.
- [retrieve-multiple-async-silent-truncation-2026-05-29.md](../logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md) for the paging rule.
- [../../plans/2026-07-08-001-refactor-orphan-cleanup-handler-architecture-plan.md](../../plans/2026-07-08-001-refactor-orphan-cleanup-handler-architecture-plan.md) for the handler architecture decision.
