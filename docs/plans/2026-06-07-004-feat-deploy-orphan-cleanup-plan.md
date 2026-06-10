---
title: "feat: Deploy — Orphan Component Cleanup"
type: feat
status: completed
date: 2026-06-07
origin: docs/brainstorms/2026-06-07-deploy-orphan-cleanup-requirements.md
---

# feat: Deploy — Orphan Component Cleanup

## Summary

Extends `DeployCommand` with pre-import and post-import orphan cleanup backed by two new `Flowline.Core` service objects. `ComponentClassifier` is a pure static class that maps `solutioncomponent.componenttype` integers to classification actions and parses S_new from `Solution.xml`. `OrphanCleanupService` owns all Dataverse SDK interactions: S_old query, cross-solution membership check, report, and deletions. `DeployCommand` gains `DataverseConnector` in its constructor (first SDK dependency for this command) and `--no-delete` reuses `RunMode.NoDelete`.

---

## Problem Frame

Unmanaged solution imports are additive — Dataverse never removes deleted components. The hard-failure case makes this more than hygiene: when a plugin class is removed from the DLL, the orphaned `plugintype` record blocks DLL upload until manually cleaned. This feature closes the gap with managed solution behavior for operational components and is core to Flowline's competitive positioning. See [origin](docs/brainstorms/2026-06-07-deploy-orphan-cleanup-requirements.md) for full problem narrative.

---

## Requirements

- R1. Query `solutioncomponent` on target → S_old (current component set)
- R2. Parse `Solution.xml` `<RootComponents>` from local source (not `customizations.xml` — see Institutional Learnings) → S_new (incoming component set)
- R3. Orphan set = S_old − S_new
- R4. Classify each orphan as AUTO or MANUAL based on component type
- R5. AUTO components: plugin assembly, type, step, step image, custom API (+ request param + response property), web resource, workflow
- R6. MANUAL components: table, column, relationship, form, view, chart, dashboard, security role, global option set, site map, app module, connection reference, environment variable
- R7. Cross-solution check for AUTO: if component found in another solution → REMOVE FROM SOLUTION; if solo → DELETE
- R8. Deletion order: step images → steps → types → assemblies; custom APIs; web resources; workflows (deactivate-then-delete)
- R9. Workflow deactivation via SetStateRequest before delete; failure → downgrade to MANUAL
- R10. REMOVE FROM SOLUTION uses `RemoveSolutionComponentRequest`
- R11. Pre-import step runs before `pac solution pack`
- R12. Dependency-blocked components skipped pre-import, passed to post-import
- R13. Post-import step runs after `pac solution import` completes
- R14. Post-import re-classifies and re-checks; failures are non-blocking
- R15. Post-import failures → MANUAL with note; import result not affected
- R16. Orphan report printed before pre-import actions; post-import actions appended
- R17. `--no-delete` suppresses all AUTO actions; report still prints
- R18. Summary line: "N deleted, N removed, N manual" / "N would be deleted … (--no-delete active). N manual."
- R19. `ComponentClassifier` and `OrphanCleanupService` as distinct service objects

**Origin actors:** A1 (developer deploying to test/UAT/prod), A2 (release manager reviewing orphan report), A3 (consultant managing multiple solutions in one environment)

**Origin flows:** F1 (pre-import step), F2 (post-import step)

**Origin acceptance examples:** AE1 (DELETE solo component), AE2 (REMOVE FROM SOLUTION shared component), AE3 (MANUAL data-bearing), AE4 (post-import fallback for dependency-blocked step), AE5 (--no-delete active), AE6 (workflow deactivation before delete), AE7 (workflow deactivation failure → MANUAL), AE8 (hard failure case: plugin class removed from DLL)

---

## Scope Boundaries

- Managed solution deployment is out of scope (Flowline is unmanaged-first)
- Patch solution support out of scope
- Ownership stamps (`[flowline:solution=...]` on description fields) — deferred; solutioncomponent membership is sufficient authority
- Extended report enrichment (data presence checks, app module references, user assignment counts) — deferred
- State restoration — separate feature (`docs/Features/FR-state-restoration.md`)
- Workflow owner reassignment across environments — out of scope

### Deferred to Follow-Up Work

- Ownership stamps: add to `push` if practice reveals false positives or false negatives in the auto-delete tier
- Extended report enrichment: richer MANUAL labels once the base report is proven useful
- Update `docs/Features/FR-orphan-cleanup.md` flag name (`--save` → `--no-delete`) and shared-component action (SKIP → REMOVE FROM SOLUTION) — separate docs PR

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline/Commands/DeployCommand.cs` — orchestration flow, static helper pattern (`ResolveDtapGate`, `ReadLocalSolutionVersion`), `Settings` inner class
- `src/Flowline.Core/Services/PluginService.cs` — constructor pattern (`IAnsiConsole + FlowlineRuntimeOptions`), RunMode handling, internal readers newed directly
- `src/Flowline.Core/Services/PluginReader.cs` — `GetComponentSolutionMembershipAsync`: cross-solution query via `solutioncomponent` + joined `solution.uniquename`; this is the canonical cross-solution check
- `src/Flowline.Core/Services/WebResourceReader.cs` — `GetWebResourcesForSolutionAsync`: solutioncomponent join by solutionid + componenttype filter; uses `RetrieveAllAsync`
- `src/Flowline.Core/Services/GenerateReader.cs` — `GetSolutionEntityLogicalNamesAsync`: queries `solutioncomponent` by solutionid + componenttype=1, uses `RetrieveAllAsync`; S_old query mirrors this
- `src/Flowline.Core/Services/OrganizationServiceExtensions.cs` — `RetrieveAllAsync` paged extension; **mandatory for all solutioncomponent queries**
- `src/Flowline/Utils/DriftChecker.cs` — pure static class shape; `DriftWarning` record + `DriftCategory` enum; pattern for `ComponentClassifier`
- `src/Flowline/Commands/PushCommand.cs` — `--no-delete` flag declaration and `ResolveRunMode` helper; exact shape to mirror
- `src/Flowline.Core/RunMode.cs` — `RunMode` enum (`Normal`, `NoDelete`, `DryRun`)
- `src/Flowline/Program.cs` — singleton registration pattern for `PluginService`, `WebResourceService`
- `tests/Flowline.Tests/DeployCommandDtapGateTests.cs` — tests static internal methods via `TempPackageFolder` IDisposable helper; no Moq
- `tests/Flowline.Tests/DriftCheckerTests.cs` — IDisposable class with real temp files; `WriteFile` helper pattern

### Institutional Learnings

- **Execution order**: S_old fetch slots after "Validate target environment" and before "Pack". Do not insert before DTAP gate — a gate block would leave S_old fetched with no cleanup executed. (see `managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md`)
- **RetrieveAllAsync mandatory**: solutioncomponent queries on real orgs exceed 5000 components. Raw `RetrieveMultipleAsync` silently truncates — produces phantom orphans and missed orphans. (see `retrieve-multiple-async-silent-truncation-2026-05-29.md`)
- **Pure static classifier**: Mirror `FindProblematicSolutions` (ProvisionCommand) and `ResolveDtapGate` — no live service dependency in the classifier; testable in isolation. (see `provision-safety-guard-unmanaged-solutions-2026-05-18.md`)
- **No bypass for MANUAL data-bearing components**: Error messages must name the resource and required action ("delete via maker portal"). No `--force-delete-data` flag. (see `provision-safety-guard-unmanaged-solutions-2026-05-18.md`)
- **componenttype integer values**: Validate actual values returned by the API against a real environment before hardcoding. Microsoft docs are unreliable for internal Dataverse field values. (see `dataverse-asyncoperation-no-progress-field-2026-06-06.md`)
- **S_new from Solution.xml RootComponents**: Correction to brainstorm doc — S_new component objectids come from `Solution.xml`'s `<RootComponents>` section, not `customizations.xml`. Each `<RootComponent type="N" id="{guid}"/>` gives componenttype and objectid.

---

## Key Technical Decisions

- **S_new from `Solution.xml` `<RootComponents>` (not `customizations.xml`)**: Research found that `customizations.xml` contains entity metadata definitions, not component references. `Solution.xml`'s `<RootComponent type="N" id="{guid}"/>` entries are the component objectids + type codes. (see origin: Key Decisions — query approach)
- **Dataverse SDK for all orphan operations**: `OrphanCleanupService` uses `IOrganizationServiceAsync2` directly (Pattern B). PAC CLI has no surface for `solutioncomponent` queries or delete/remove-from-solution operations. `DeployCommand` gets `DataverseConnector` in its constructor — first SDK dependency for this command.
- **`RetrieveAllAsync` mandatory for all solutioncomponent queries**: Silent truncation at >5000 components creates both phantom orphans and missed orphans. The paged extension already exists in `OrganizationServiceExtensions`. No caller may use raw `RetrieveMultipleAsync` for solutioncomponent.
- **`--no-delete` reuses `RunMode.NoDelete`**: Consistent with `push --no-delete`. `RunMode` enum already exists in `Flowline.Core`. Suppresses all auto-actions; report still prints with "would be" phrasing.
- **Dependency-blocked deferral, not failure**: Pre-import catches dependency errors specifically (not all errors). Deferred components are passed to post-import as a typed set. Post-import failures are non-blocking — logged as MANUAL, deploy result not affected.
- **`componenttype` integers hardcoded in `ComponentClassifier`**: The mapping is a Dataverse invariant that changes only with platform schema upgrades. Hardcode with empirical validation notes; validate against a real org before finalizing values.

---

## Open Questions

### Resolved During Planning

- **S_new source**: `Solution.xml` `<RootComponents>`, not `customizations.xml` — confirmed from codebase research.
- **SDK vs PAC CLI for orphan queries**: SDK required — PAC CLI has no solutioncomponent query or component delete/remove surface.
- **RunMode reuse**: `RunMode.NoDelete` already exists; `--no-delete` maps directly; no new enum value needed.

### Deferred to Implementation

- **`RemoveSolutionComponentRequest` availability**: Verify it is callable via `IOrganizationServiceAsync2.Execute()`. Confirm it accepts solution `uniquename` or requires `solutionid`. Check the SDK version already referenced by Flowline.Core.
- **Async import polling**: Does `pac solution import --async` block until completion, or return before Dataverse finishes? If it returns early, post-import must poll for job completion before re-querying `solutioncomponent`.
- **Custom API child cascade**: Verify whether `customapirequestparameter` and `customapiresponseproperty` auto-cascade on parent `customapi` delete. If not, add explicit child deletes in the deletion order.
- **Managed component filtering in S_old**: Confirm `solutioncomponent` query scoped to the unmanaged solution returns only unmanaged entries. Add `ismanaged = false` filter if needed to avoid phantom orphans from managed layers.
- **`componenttype` integer values**: Validate actual values returned by the API for each of the 9 AUTO types against a real environment before hardcoding in `ComponentClassifier`.

---

## High-Level Technical Design

> *This illustrates the intended approach and is directional guidance for review, not implementation specification.*

```
ExecuteFlowlineAsync (DeployCommand)
  1. Assert repo clean
  2. Resolve targetUrl
  3. Resolve solution name
  4. Validate target env          ← existing
  5. Type guard                   ← existing
  6. DTAP gate                    ← existing
  7. Drift check                  ← existing
  ───────────────────────────────────────────
  8. [PRE-IMPORT]
     S_new = ComponentClassifier.ParseSolutionXmlComponents(Solution.xml)
     service = ConnectToDataverseAsync(dataverseConnector, targetUrl, ct)
     deferred = OrphanCleanupService.RunPreImportAsync(service, sln.Name, S_new, runMode, ct)
       · Query S_old via RetrieveAllAsync (solutioncomponent by solution name)
       · Orphans = S_old − S_new
       · Classify each: ComponentClassifier.Classify(componenttype) → AUTO | MANUAL
       · For AUTO: cross-solution check → DELETE or REMOVE FROM SOLUTION
       · Print report
       · Unless NoDelete: execute in order; collect dependency-blocked → deferred
  ───────────────────────────────────────────
  9.  Pack (pac solution pack)    ← existing
  10. Import (pac solution import)← existing
  ───────────────────────────────────────────
  11. [POST-IMPORT]
      OrphanCleanupService.RunPostImportAsync(service, sln.Name, S_new, deferred, runMode, ct)
        · Re-query solutioncomponent for deferred objectids
        · Re-classify, re-check cross-solution
        · Execute remaining AUTO actions; append to report
        · Failures → MANUAL with note; non-blocking

ComponentClassifier (static):
  Classify(int componentType) → ComponentAction { AutoDelete, Manual }
  ParseSolutionXmlComponents(string path) → IReadOnlyList<(Guid ObjectId, int ComponentType)>

OrphanCleanupService:
  RunPreImportAsync(...)  → IReadOnlyList<OrphanEntry> deferred
  RunPostImportAsync(...) → void
```

---

## Implementation Units

### U1. ComponentClassifier and S_new parser

**Goal:** Pure static class mapping Dataverse `componenttype` integers to classification actions, plus a static parser that reads `<RootComponents>` from `Solution.xml` to produce S_new.

**Requirements:** R2 (S_new), R4, R5, R6

**Dependencies:** None

**Files:**
- Create: `src/Flowline.Core/Services/ComponentClassifier.cs`
- Test: `tests/Flowline.Tests/ComponentClassifierTests.cs`

**Approach:**
- `ComponentAction` enum: `AutoDelete`, `Manual`
- `Classify(int componentType) → ComponentAction` — maps known AUTO componenttype integers to `AutoDelete`; all unknown types default to `Manual`. Annotate integer constants with `// TODO: verify against real org` until empirically validated.
- `ParseSolutionXmlComponents(string solutionXmlPath) → IReadOnlyList<(Guid ObjectId, int ComponentType)>` — reads `Solution.xml`, navigates `ImportExportXml/SolutionManifest/RootComponents`, extracts each `<RootComponent type="N" id="{guid}"/>`. Throws `FlowlineException(ExitCode.NotFound, ...)` if file missing; `FlowlineException(ExitCode.ValidationFailed, ...)` if XML malformed. Returns empty list when `<RootComponents>` exists but has no children (first-deploy scenario).
- Cross-solution promotion (AUTO → REMOVE FROM SOLUTION) happens in `OrphanCleanupService` after the membership check, not in the classifier.

**Patterns to follow:**
- `DriftChecker` — pure static class, record + enum shape
- `DeployCommand.ReadLocalSolutionVersion` — XML parse via `XDocument.Load`, `FlowlineException` on failure

**Test scenarios:**
- Happy path: plugin step componenttype → `AutoDelete`; form componenttype → `Manual`; web resource componenttype → `AutoDelete`; workflow componenttype → `AutoDelete`
- Edge case: unknown componenttype (e.g., 999) → `Manual`
- Edge case: componenttype 0 or negative → `Manual`
- Happy path: `ParseSolutionXmlComponents` with valid Solution.xml containing 3 components → list of 3 {objectid, componenttype} pairs
- Edge case: `ParseSolutionXmlComponents` with empty `<RootComponents>` → empty list
- Error path: `ParseSolutionXmlComponents` file missing → `FlowlineException`
- Error path: `ParseSolutionXmlComponents` malformed XML → `FlowlineException`

**Verification:**
- All test scenarios pass with no Dataverse dependency
- `ComponentClassifier.Classify` returns `AutoDelete` for all 9 AUTO component types from R5
- `ParseSolutionXmlComponents` returns empty list for solutions with no root components

---

### U2. OrphanCleanupService

**Goal:** SDK-based service that queries S_old, performs cross-solution checks, builds the orphan report, and executes deletions and removes-from-solution in the correct order. Returns a deferred set for post-import.

**Requirements:** R1 (S_old), R3 (diff), R7–R10, R11–R15, R16–R18, AE1–AE8

**Dependencies:** U1

**Files:**
- Create: `src/Flowline.Core/Services/OrphanCleanupService.cs`

**Approach:**
- Constructor: `OrphanCleanupService(IAnsiConsole output, FlowlineRuntimeOptions opt)` — mirror `PluginService` shape
- `OrphanEntry` record: `(Guid ObjectId, int ComponentType, string DisplayName, OrphanAction Action)`
- `OrphanAction` enum: `Delete`, `RemoveFromSolution`, `Manual`
- `RunPreImportAsync(IOrganizationServiceAsync2 service, string solutionName, IReadOnlyList<(Guid, int)> sNew, RunMode mode, CancellationToken ct) → IReadOnlyList<OrphanEntry> deferred`:
  - S_old query: `QueryExpression("solutioncomponent")` joined to `solution` by `uniquename = solutionName`, columns `objectid` + `componenttype`. **Must use `service.RetrieveAllAsync()`.** If solution not found in target (first deploy), returns empty list → no orphans → pre-import is a no-op.
  - Diff: orphans = objectids in S_old not in S_new
  - Classify: `ComponentClassifier.Classify(componenttype)` per orphan
  - Cross-solution check for AUTO orphans: query `solutioncomponent` by objectid across all solutions (see `PluginReader.GetComponentSolutionMembershipAsync`). If found in another active solution → `RemoveFromSolution`; solo → `Delete`
  - Print report table; if `mode == RunMode.NoDelete`, print summary with "would be" phrasing and return `[]`
  - Execute deletions in order (R8): step images → steps → types → assemblies; custom APIs (verify child cascade — explicit if needed); web resources; workflows (SetStateRequest statecode=0/Draft statuscode=1, then delete)
  - Catch `FaultException` with dependency error code specifically; add to deferred; continue remainder
  - Workflow deactivation failure → downgrade to `Manual`; add note to report entry
  - Return deferred list
- `RunPostImportAsync(IOrganizationServiceAsync2 service, string solutionName, IReadOnlyList<(Guid, int)> sNew, IReadOnlyList<OrphanEntry> deferred, RunMode mode, CancellationToken ct) → void`:
  - Re-query solutioncomponent for deferred objectids (verify still present; skip if gone)
  - Re-classify and re-check cross-solution membership
  - Execute remaining AUTO actions; append to report
  - Any failure → `Console.Warning` + MANUAL note; do not throw
- Error messages: name component + type + required user action (per AI CLI contract)

**Patterns to follow:**
- `PluginReader.GetComponentSolutionMembershipAsync` — cross-solution query shape
- `PluginService` — constructor, RunMode handling, `Console.Status().FlowlineSpinner()` for async ops
- `OrganizationServiceExtensions.RetrieveAllAsync` — mandatory for S_old query (never raw `RetrieveMultipleAsync`)

**Test scenarios:**
- Test expectation: none — `IOrganizationServiceAsync2` interactions are not mockable in the current test setup. Behavior validated by AE1–AE8 at integration level.

**Verification:**
- `RunPreImportAsync` returns a deferred list; does not throw on dependency errors
- `RunPostImportAsync` does not throw on post-import failures (non-blocking)
- `--no-delete` mode: report prints, no SDK write calls made

---

### U3. DeployCommand integration

**Goal:** Wire `OrphanCleanupService` into `DeployCommand` — inject `DataverseConnector`, add `--no-delete` flag, add pre-import call after drift check, add post-import call after import.

**Requirements:** R11–R18, AE1–AE8

**Dependencies:** U1, U2

**Files:**
- Modify: `src/Flowline/Commands/DeployCommand.cs`

**Approach:**
- Add `DataverseConnector dataverseConnector` and `OrphanCleanupService orphanCleanupService` to constructor parameters
- Add `--no-delete` to `Settings`:
  ```
  [CommandOption("--no-delete")]
  [Description("Report orphan components without deleting them")]
  [DefaultValue(false)]
  public bool NoDelete { get; set; } = false;
  ```
  Mirror `PushCommand.Settings.NoDelete` exactly.
- Resolve `RunMode runMode` from settings: `runMode = settings.NoDelete ? RunMode.NoDelete : RunMode.Normal` (DryRun not applicable to deploy — pack and import are not dryable)
- Parse S_new: `ComponentClassifier.ParseSolutionXmlComponents(...)` using the existing `PackageFolder(slnFolder)` path. Catch `FlowlineException` → `Console.Error(ex.Message)` → return `ExitCode.ValidationFailed`
- Pre-import slot (after drift check, before pack):
  - `var service = await ConnectToDataverseAsync(dataverseConnector, targetUrl, ct)`
  - `var deferred = await orphanCleanupService.RunPreImportAsync(service, sln.Name, sNew, runMode, ct)`
- Post-import slot (after import completes, reuse `service` variable — do not reconnect):
  - `await orphanCleanupService.RunPostImportAsync(service, sln.Name, sNew, deferred, runMode, ct)`
- `service` variable declared before pre-import slot and reused in post-import — keep in scope across both pack and import calls

**Patterns to follow:**
- `PushCommand.Settings.NoDelete` — exact flag declaration
- Existing DTAP gate skip: `Console.Skip(...)` — suppressed-with-notice pattern; `--no-delete` report uses same approach
- `DeployCommand.ReadLocalSolutionVersion` — where to catch `FlowlineException` from static helpers

**Test scenarios:**
- Happy path: `--no-delete` set → `RunMode.NoDelete` resolved; verify no action methods called (via RunMode flow)
- Error path: Solution.xml missing → `FlowlineException` caught → `ExitCode.ValidationFailed` before pack
- Integration: pre-import slot executes after drift check and before `pac solution pack` (execution order verified by reading flow)
- Integration: post-import slot executes after successful `pac solution import`

**Verification:**
- `dotnet build` succeeds; all 201 existing tests pass
- `flowline deploy --help` shows `--no-delete` flag
- Pre-import orphan cleanup fires before pack; post-import fires after import

---

### U4. Registration, wiki, and feature doc updates

**Goal:** Register `OrphanCleanupService` as a singleton in `Program.cs`; update `Command-Reference.md` wiki to document `--no-delete` and orphan cleanup behavior.

**Requirements:** R19 (service design as own objects)

**Dependencies:** U2, U3

**Files:**
- Modify: `src/Flowline/Program.cs`
- Modify: `E:\Code\RemyDuijkeren\Flowline.wiki\Command-Reference.md`

**Approach:**
- `Program.cs`: add `services.AddSingleton<OrphanCleanupService>()` alongside existing service registrations (`PluginService`, `WebResourceService`)
- `Command-Reference.md`: add `--no-delete` row to the `deploy` command options table; add "Orphan cleanup" subsection below the DTAP gate subsection describing pre-import + post-import behavior, report format, MANUAL vs AUTO distinction, and that `--no-delete` suppresses auto-actions

**Patterns to follow:**
- Existing `AddSingleton<PluginService>()` in `Program.cs`
- `Command-Reference.md` deploy section — DTAP gate subsection format and prose style

**Test scenarios:**
- Test expectation: none — registration and documentation only

**Verification:**
- `dotnet build` succeeds with `OrphanCleanupService` registered
- `flowline deploy --help` shows `--no-delete` (verified in U3)
- Wiki deploy section includes `--no-delete` flag row and orphan cleanup subsection

---

## Alternative Approaches Considered

- **Export + unpack target solution (content-based diff)**: Export the deployed solution to a temp folder, unpack with `pac solution unpack`, and compare file-by-file against local source. Rejected for orphan detection — adds significant deploy time (full solution export + unpack) and is content-based rather than component-identity-based. `solutioncomponent` query is faster and gives objectids directly. Content-based diffing may be appropriate for feature #6 (`--dry-run`) where understanding *what changed* in component definitions matters, but not for detecting *which components* to delete.

---

## System-Wide Impact

- **Interaction graph**: `DeployCommand` gains its first Dataverse SDK connection via `DataverseConnector`. The connector is already used by `PluginService`/`WebResourceService` — same MSAL token cache, same auth surface, no new credentials required.
- **Error propagation**: Pre-import `FlowlineException` (missing Solution.xml, SDK connection failure) propagates to `ExitCode.ValidationFailed`. Dependency errors during pre-import execution are caught specifically and deferred, not rethrown. Post-import cleanup failures are logged as MANUAL and do not affect the import result.
- **State lifecycle risk**: If pack or import fails after pre-import cleanup runs, the environment has fewer components than before without the new solution version. This is acceptable — the deleted components were orphans by definition. No rollback is possible or needed.
- **API surface parity**: `--no-delete` is deploy-specific. Not added to `sync`, `push`, or other commands.
- **Integration coverage**: The AE8 scenario (plugin class removed → pre-import cleans orphaned type → pack + import succeed) requires end-to-end integration against a real Dataverse org. Unit tests cannot cover this chain.
- **Unchanged invariants**: All existing `DeployCommand` behavior (type guard, DTAP gate, drift check, pack, import) is unchanged and additive. The existing 201 tests must continue passing without modification.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| `componenttype` integer constants are wrong | Validate against a real environment before hardcoding. Mark constants with `// TODO: verify against real org` during U1 implementation. |
| `RemoveSolutionComponentRequest` not available in current SDK version | Verify in U2 before implementing. Fallback: REMOVE FROM SOLUTION via direct Dataverse Web API DELETE on solutioncomponent record. |
| `pac solution import --async` returns before Dataverse finishes processing | Investigate in U3 — if async, add polling loop before `RunPostImportAsync` call. |
| Custom API child records don't cascade on parent delete | Verify in U2. If not, add explicit child deletes before parent in deletion order. |
| Pre-import cleanup runs, then import fails, leaving environment with fewer components than before | Acceptable — components were orphans by definition. Document in release notes / wiki. |
| Large solutions (>10k components) hit SDK timeout in S_old query | `RetrieveAllAsync` pages at 5000 — validate timeout settings during integration testing. |

---

## Documentation / Operational Notes

- Update `Flowline.wiki/Command-Reference.md` — `--no-delete` flag and orphan cleanup subsection (U4)
- `docs/Features/FR-orphan-cleanup.md` references stale flag name (`--save` → `--no-delete`) and stale shared-component action (SKIP → REMOVE FROM SOLUTION) — update in a separate docs PR after the feature ships
- `docs/Features/FR-state-restoration.md` references `--save` in its description — update reference once `--no-delete` is live

---

## Sources & References

- **Origin document:** [docs/brainstorms/2026-06-07-deploy-orphan-cleanup-requirements.md](docs/brainstorms/2026-06-07-deploy-orphan-cleanup-requirements.md)
- Feature doc: [docs/Features/FR-orphan-cleanup.md](docs/Features/FR-orphan-cleanup.md)
- Managed/unmanaged type guard: [docs/solutions/architecture-patterns/managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md](docs/solutions/architecture-patterns/managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md)
- DTAP gate: [docs/solutions/architecture-patterns/dtap-gate-enforcement-in-deploy-command-2026-06-07.md](docs/solutions/architecture-patterns/dtap-gate-enforcement-in-deploy-command-2026-06-07.md)
- Silent truncation bug: [docs/solutions/logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md](docs/solutions/logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md)
- Provision safety guard: [docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md](docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md)
- AI CLI contract: [docs/solutions/architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md](docs/solutions/architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md)
