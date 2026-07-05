---
title: Orphan component cleanup — two-phase deploy pipeline
date: 2026-06-10
last_updated: 2026-07-05 (part 5)
category: docs/solutions/architecture-patterns
module: DeployCommand
problem_type: architecture_pattern
component: tooling
severity: high
resolution_type: workflow_improvement
applies_when:
  - Deploy command imports a Dataverse solution and must remove components no longer in the solution
  - Component set in Solution.xml (S_new) differs from component set currently in Dataverse (S_old)
  - Some orphaned components may be shared with other solutions (RemoveFromSolution vs Delete)
  - Dependency errors block deletion before import but resolve after import completes
  - A --no-delete flag is needed to report what would be deleted without acting
related_components:
  - ComponentClassifier
  - OrphanCleanupService
  - IPostDeployService
  - PostDeployContext
  - ExitCode
tags:
  - dataverse
  - orphan-cleanup
  - deploy
  - two-phase
  - solution-components
  - dependency-order
  - cross-solution
  - exit-codes
---

# Orphan component cleanup — two-phase deploy pipeline

## Context

Unmanaged solution imports in Dataverse are additive. When a plugin class is removed from the DLL or a web resource is deleted from source, the corresponding Dataverse record is never removed by the import. This creates orphan components: records in Dataverse that exist in neither the current solution source nor any successor deployment.

The hard-failure case for plugin development: when a plugin class is removed from the DLL and the developer redeploys, Dataverse blocks the DLL upload because the orphaned `plugintype` record still references the old class. The developer must manually delete the record through the maker portal before the deploy succeeds.

Without automated cleanup, long-lived unmanaged solutions accumulate orphaned plugin types, step configurations, web resources, and workflows that slow execution, pollute the environment, and periodically surface as manual intervention requirements.

The pattern documented here — pre/post-import orphan diffing with two-phase execution — addresses all of these cases systematically during the normal deploy workflow.

## Guidance

### Core principle: diff-then-execute with dependency deferral

The implementation resolves orphans in two phases because Dataverse enforces referential integrity: you cannot delete a `pluginassembly` while `plugintype` records depend on it, and you cannot delete a `plugintype` while `sdkmessageprocessingstep` records depend on it. Rather than computing a topological sort in advance (which requires full knowledge of all dependencies), the pattern:

1. **Pre-import:** classifies and deletes orphans in dependency order; catches dependency errors specifically and defers those entries.
2. **Post-import:** re-queries deferred entries; retries them. At this point the import has resolved most cross-solution dependencies.

### Component classification

`ComponentClassifier` distinguishes components that can be auto-deleted from those requiring manual removal.

```csharp
public enum ComponentAction { AutoDelete, Manual }

public static class ComponentClassifier
{
    private static readonly HashSet<int> AutoTypes =
    [
        91,  // PluginAssembly
        90,  // PluginType
        92,  // SdkMessageProcessingStep
        93,  // SdkMessageProcessingStepImage
        61,  // WebResource
        29,  // Workflow
    ];

    public static ComponentAction Classify(int componentType) =>
        AutoTypes.Contains(componentType) ? ComponentAction.AutoDelete : ComponentAction.Manual;
}
```

**Why only stable componenttype values:** Dataverse `solutioncomponent.componenttype` values below 100 are stable platform constants across all orgs. The CustomApi family uses env-specific component type numbers that differ per org — they cannot be classified by integer. They are detected separately via parallel entity queries (see below).

### S_new: Solution.xml RootComponents, plus entity subcomponent files — not customizations.xml

The diff source must come from the unpacked solution source, not from `customizations.xml` (`customizations.xml` contains entity metadata — attribute definitions, form XML — not component object references). But `Solution.xml` `<RootComponents>` alone under-reports S_new (see 2026-07-05 update below): entity roots can be recorded by `schemaName` instead of `id`, and subcomponents like Forms/Views live under `Entities/<name>/**` with no `<RootComponent>` entry at all.

```csharp
var rootComponents = doc.Root
    ?.Element("SolutionManifest")
    ?.Element("RootComponents")
    ?.Elements("RootComponent")
    ?? [];

var components = new List<(Guid, int)>();
var entityLogicalNames = new List<string>();

foreach (var component in rootComponents)
{
    if (!int.TryParse(component.Attribute("type")?.Value, out var type)) continue;

    if (Guid.TryParse(component.Attribute("id")?.Value, out var id))
    {
        components.Add((id, type));
        continue;
    }

    // Entity roots use schemaName instead of id — caller must resolve to MetadataId live.
    if (type != EntityComponentType) continue;
    var schemaName = component.Attribute("schemaName")?.Value;
    if (!string.IsNullOrEmpty(schemaName))
        entityLogicalNames.Add(schemaName);
}
```

`ScanEntitySubcomponents` separately walks `Entities/<name>/{FormXml,SavedQueries}/**` for GUID-named files, since these are never enumerated as `<RootComponent>` nodes regardless of the entity's `behavior` setting.

### Always use RetrieveAllAsync for solutioncomponent queries

`RetrieveMultipleAsync` silently truncates at 5000 records. A solution with more than 5000 components silently drops entries, causing false positives in the orphan diff (records appear to be orphans because they weren't seen in the first page). All `solutioncomponent` queries must paginate:

```csharp
// Bad: silently truncates at 5000
var result = await service.RetrieveMultipleAsync(query, ct);

// Good: pages through all results
var entities = await service.RetrieveAllAsync(query, ct);
```

See `src/Flowline.Core/Services/OrganizationServiceExtensions.cs` for the paging extension. Applied in all four query methods in `OrphanCleanupService`.

**Update (2026-07-03):** `OrphanCleanupService` now implements `IPostDeployService` and threads its deferred-entry state across the two phases via a private mutable instance field (`_deferred`) rather than an explicit parameter/return value passed by the caller. This is safe only because the service is registered `AddSingleton` and Flowline runs one command per process — see [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) for the interface shape and the tradeoffs of that design.

**Update (2026-07-05): `<RootComponents>` alone under-reports S_new — three false-positive sources found and fixed.** A real deploy flagged 53 "manual" orphans; verification against the actual unpacked solution source showed most were still legitimately part of the solution:

1. **Entity roots recorded by `schemaName`, not `id`.** `<RootComponent type="1" schemaName="account" behavior="1" />` has no `id` attribute — the old parser's `Guid.TryParse(id)` silently dropped these, so every entity added as a root (Account, Contact, Lead, ...) looked orphaned. `ParseSolutionXmlComponents` now returns a `SolutionXmlComponents(Components, EntityLogicalNames)` record; callers with a live connection resolve `EntityLogicalNames` to MetadataIds via `RetrieveEntityRequest { LogicalName = name }` (see `OrphanCleanupService.ResolveEntityMetadataIdsAsync`) and fold the result into the S_new id set before diffing.
2. **Subcomponents unpacked under `Entities/<name>/**` are never listed as `<RootComponent>` nodes at all** — Forms (`FormXml/**/{guid}.xml`, componenttype 60) and Views (`SavedQueries/{guid}.xml`, componenttype 26) exist as tracked, git-diffable files but have no corresponding manifest entry. `ComponentClassifier.ScanEntitySubcomponents(srcRoot)` recovers their GUIDs by filename scan; `DeployCommand.ParseSolutionXml` merges this into S_new alongside the manifest components.
3. **Microsoft system components use a fixed GUID prefix** (`00000000-0000-0000-00aa-...` — confirmed on out-of-box system views) and are never user-deletable regardless of manifest state. `ComponentClassifier.IsWellKnownSystemComponent` excludes them from the orphan set entirely, independent of S_new.

**Update (2026-07-05, part 2): Attribute-type orphans (componenttype 2), manual-report naming, and `PackageSrcRoot`.** Unlike Forms/Views, attributes have no GUID-named file to scan — `Entity.xml` declares them by `LogicalName`/`PhysicalName`, not by their solutioncomponent MetadataId. Closing this gap needed a live metadata round-trip regardless of local source:

- `OrphanCleanupService.ResolveAttributeInfoAsync` issues one `RetrieveMetadataChangesRequest` with `EntityQueryExpression.Criteria` scoped to `context.EntityLogicalNames` (the solution's own entities) and `AttributeQuery.Criteria` filtered to the orphan attribute MetadataIds. Leaving `Criteria` unset would make this the `RetrieveAllEntities`-equivalent full-org metadata walk the SDK docs warn against — scoping to known entities keeps it a single, cheap, bounded call that only fires when Attribute-type orphans exist.
- For each resolved `(entityLogicalName, attributeLogicalName)`, `ComponentClassifier.ScanEntityAttributeLogicalNames(packageSrcRoot, entityLogicalName)` checks whether that LogicalName is still declared in `Entities/<name>/Entity.xml`. If yes, it's a false positive (a customized-but-still-present field on a `behavior="1"` entity like Account) and is suppressed rather than listed. If no, it's a genuine leftover and gets a real name instead of a bare GUID.
- `PostDeployContext` gained `PackageSrcRoot` (the `Package/src` folder), mirroring the existing `WebResourceRoot` precedent — a raw folder reference alongside the parsed `LocalComponents`, for services that need to read unpacked source directly. Note this can't solve the naming problem alone: attributes are keyed by LogicalName in Entity.xml, not the MetadataId GUID a solutioncomponent row carries, so correlating the two still requires the live metadata call above.
- Manual-orphan output also got a label pass: a verified `componenttype → display label` table (`ManualTypeLabels`, sourced from the Solution Component table reference on learn.microsoft.com) replaced the bare `Component(N)` fallback; types outside that table (e.g. env-specific 10000+ codes, same category as the CustomApi family) are shown as "Unrecognized component" rather than guessed at; and the manual bucket is now grouped separately from what Flowline handles automatically, with a pointer to the org's classic solutions list (`/tools/Solution/home_solution.aspx?etn=solution`, a documented Dataverse URL) since "remove via maker portal" alone was a dead end.
- Data-backed manual types (Form/View/Role/ConnectionRole) get names via `OrphanCleanupService.GetEntityNamesAsync`, a generalization of the existing webresource-name lookup (`QueryExpression` on the backing table's `name` column) — no metadata call needed for these, they're plain data rows.

**Update (2026-07-05, part 3): names everywhere, and a real fix for "unrecognized" types via `solutioncomponentdefinition`.** Part 2 only named the Manual bucket — the AutoDelete and CustomApi entries (WebResource, PluginAssembly, PluginType, SdkMessageProcessingStep(Image), Workflow, CustomApi family) still showed bare GUIDs, which defeats the point when deciding whether a delete is safe.

- `NameResolvableTypes` now covers the AutoDelete componenttypes too (column names verified against the existing queries in `PluginReader.cs` — `plugintype.typename`, `sdkmessageprocessingstep.name`, etc.), and the `autoOrphans`/`customApiOrphans` loops in `RunPreImportAsync` resolve names the same way the Manual loop already did — grouped by componenttype (or by entity LogicalName for the CustomApi family), one bulk query per group.
- **`solutioncomponentdefinition` resolves "unrecognized" componenttype codes to a real name.** This is Dataverse's own component-type lookup table — documented for the Web API as `GET solutioncomponentdefinitions?$select=name,solutioncomponenttype` (Power Pages CLI solution docs) — queryable via the SDK exactly like any other entity. `ResolveComponentTypeNamesAsync` queries it for any componenttype not already in `ManualTypeLabels`, turning e.g. `Unrecognized component (type 10064)` into the type's actual name (e.g. `Connection Reference`) wherever Dataverse itself knows what it is. `OrphanEntry.TypeNameResolved` tracks whether this resolved, so `ManualLabel` can drop the "unrecognized" wording once the type is identified — genuinely unresolvable codes (nothing found in `solutioncomponentdefinition` either — e.g. a private/deleted definition) keep the more cautious wording.
- Test-mock note: `OrphanCleanupServiceTests`' `IOrganizationServiceAsync2` mock needed a catch-all default (`Arg.Any<QueryExpression>()` → empty `EntityCollection`) once name resolution started firing unconditionally for every orphan group — real Dataverse never returns a null `EntityCollection`, but NSubstitute does for unconfigured calls, so bulk name queries not explicitly mocked in older tests threw `NullReferenceException` inside `RetrieveAllAsync` until the default was added.

**Update (2026-07-05, part 4): non-entity schemaName roots and CustomApi's GUID-less source — the same under-reporting bug, two more places.** A real deploy flagged every WebResource in the solution (7/7) plus the entire CustomApi family for deletion, even though all of them were still declared in local source. Root cause was the identical class of bug fixed in part 1 for Entity roots, just unhandled for two more shapes:

- **WebResource (and any other `NameResolvableTypes` type) recorded by `schemaName`, not `id`.** `<RootComponent type="61" schemaName="av_pkg/example1.js" behavior="0" />` — in the real `Solution.xml` that surfaced this, every WebResource root used `schemaName` with no `id`, while every PluginAssembly/Workflow/SdkMessageProcessingStep root carried an `id` (n=1 solution — not verified across every org/pac version, but consistent with pac recording by name specifically when the id isn't portable). The old parser's `if (type != EntityComponentType) continue;` silently dropped every non-Entity schemaName root, so WebResource identity was never captured in S_new at all — it looked orphaned regardless of whether the live id matched, on every deploy, not just cross-environment ones. `SolutionXmlComponents` gained `NamedComponents: IReadOnlyList<(int ComponentType, string SchemaName)>` for these; `OrphanCleanupService.ResolveNamedComponentIdsAsync` resolves each group live via `NameResolvableTypes`' entity/name-attribute mapping (same table already used for display-name lookups, now reused in reverse — name→id instead of id→name) and folds the result into the S_new id set, mirroring `ResolveEntityMetadataIdsAsync`.
- **CustomApi source has no GUID anywhere** — `Package/src/customapis/<uniquename>/customapi.xml` (and its `customapirequestparameters/<name>/`, `customapiresponseproperties/<name>/` children) is keyed purely by `uniquename`/folder name, and isn't listed in `Solution.xml` at all (its componenttype is env-specific — see CustomApi detection below). `ComponentClassifier.ScanCustomApiNames(packageSrcRoot)` scans this folder structure; `RunPreImportAsync` resolves each CustomApi-family orphan candidate's live name (already needed for display) and drops it from `customApiOrphans` if that name is still present locally, before cross-solution membership and delete execution ever see it.

Both fixes only ever narrow the orphan set (never widen it) — a type not covered by `NameResolvableTypes`, or a `customapis/` folder that doesn't exist, falls back to the pre-fix behavior exactly, not a worse one.

**`connectionreference`/`bot` manual-bucket entries also got instance names, not just the type label.** These have env-specific componenttype codes like the CustomApi family, so they can't be keyed in `NameResolvableTypes` by componenttype. But `solutioncomponentdefinition.name` for them resolves to literally the backing table's LogicalName (confirmed against a real org — a type resolves to the string `"connectionreference"`/`"bot"`, not a display label like "Connection Reference"). `ResolvedTypeNameAttributes` maps that resolved label to `(IdAttribute, NameAttribute)` — `connectionreferenceid`/`connectionreferencelogicalname` and `botid`/`name` respectively — so `BuildManualEntriesAsync` can query the actual record's name once the type itself resolves, rather than showing a bare GUID next to the type name.

**Manual-bucket entries are informational only — Flowline never auto-deletes or auto-removes them.** Worth restating: everything in the "can't be removed automatically" bucket is a candidate for the human to verify via the maker portal link, not something Flowline acts on. A manual-bucket component that's intentionally still wanted (e.g. a standard Microsoft form the solution legitimately depends on) requires no action — it's flagged because Flowline can't confirm its presence from local source, not because it's scheduled for deletion.

**Update (2026-07-05, part 5): explicit opt-in per componenttype for the manual bucket — the connectionreference/bot fix above was still wrong.** A real deploy against the same solution showed the part-4 fix working for WebResource/CustomApi, but the manual bucket recommended removing two `connectionreference` rows that resolved to `av_sharedcalendlyv2_bffc3` and `cr993_sharedcommondataserviceforapps_d75a1` — names the user confirmed were still actively used by flows in the solution. The bug: resolving a display name via `solutioncomponentdefinition` is cosmetic (it tells you *what* the component is) and had been conflated with verification (*whether* Flowline can confirm it's actually orphaned). `ManualLabel` treated "name resolved" as "confident to recommend," when connectionreference/bot have no local source representation at all — Flowline can't check them against anything.

The fix, per the user's own proposal: an explicit opt-in allowlist, `SupportedManualTypes`, gates which componenttypes are recommended for manual removal at all. A type only joins once it has a real local-source cross-check *and* test coverage for both directions (still-present → suppressed, genuinely-removed → reported):

```csharp
static readonly HashSet<int> SupportedManualTypes = [EntityComponentType, AttributeComponentType]; // 1, 2
```

Everything else — Role, ConnectionRole, connectionreference, bot, and any unrecognized type — is logged via `console.Verbose` only (componenttype + objectid, no recommendation) and excluded entirely from the printed report and the manual count. This is deliberately narrower than the pre-incident behavior; **Form (60) and View (26) were pulled from the allowlist during this same pass** — despite `ScanEntitySubcomponents` existing, it only finds a form/view's file under `Entities/<name>/FormXml|SavedQueries/` for entities this solution actually unpacks. A form on an entity outside that set (the `Form 'Sales Insights'` from the same log — a standard Microsoft form, confirmed by the user, most likely living on an entity like Opportunity that this solution doesn't include as a root component at all) has nothing for the scan to find, so it would false-positive exactly like connectionreference/bot did. Widening `SupportedManualTypes` to include Form/View requires handling that gap first.

The `solutioncomponentdefinition`-based "resolve an unrecognized type's display name" machinery added in part 3 (`ResolveComponentTypeNamesAsync`, `ResolvedTypeNameAttributes`, `OrphanEntry.TypeNameResolved`) was removed rather than kept dormant: once gated by `SupportedManualTypes`, every type that reaches the manual-entry-building code is already a key in `ManualTypeLabels`, so the resolution path was unreachable dead code, not future-proofing. Re-add the equivalent mechanism only when actually opting in a new env-specific type with test coverage to back it.

### CustomApi detection via entity queries

Because CustomApi component type numbers are env-specific, detection uses three parallel entity queries:

```csharp
var caTask    = service.RetrieveAllAsync(EntityQuery("customapi",                 "customapiid",                 idArray), ct);
var paramTask = service.RetrieveAllAsync(EntityQuery("customapirequestparameter", "customapirequestparameterid", idArray), ct);
var propTask  = service.RetrieveAllAsync(EntityQuery("customapiresponseproperty", "customapiresponsepropertyid", idArray), ct);
await Task.WhenAll(caTask, paramTask, propTask);

var result = new Dictionary<Guid, string>();
foreach (var e in caTask.Result)    result[e.Id] = "customapi";
foreach (var e in paramTask.Result) result[e.Id] = "customapirequestparameter";
foreach (var e in propTask.Result)  result[e.Id] = "customapiresponseproperty";
```

This is wrapped in `catch (Exception ex) when (ex is not OperationCanceledException)` — failure downgrades unresolved unknowns to `OrphanAction.Manual` rather than crashing the deploy. This is a deliberate exception to the project-wide "no try/catch around service calls" rule: failure has explicit recovery logic (downgrade, warn, continue).

### Dependency execution order

The deletion order matches the Dataverse dependency chain:

```csharp
// step images → steps → plugin types → assemblies; web resources; workflows
static readonly int[] ExecutionOrder = [93, 92, 90, 91, 61, 29];

// CustomApi: children deleted before parent
static readonly string[] CustomApiEntityOrder = ["customapirequestparameter", "customapiresponseproperty", "customapi"];
```

Violating this order causes `FaultException<OrganizationServiceFault>` with error code `0x80047002`. The pre-import phase catches dependency errors specifically and defers rather than failing:

```csharp
catch (FaultException<OrganizationServiceFault> ex) when (!isPostImport && IsDependencyError(ex))
{
    deferred.Add(entry);
}

static bool IsDependencyError(FaultException<OrganizationServiceFault> ex) =>
    ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
    (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
```

Post-import failures are non-blocking — they return a count, and the deploy command exits with `ExitCode.PartialSuccess = 18`.

### Workflow deactivation before delete

Active workflows cannot be deleted. Deactivate first, then delete. Deactivation failure is recoverable — downgrade to Manual:

```csharp
if (entry.ComponentType == 29 && entry.Action == OrphanAction.Delete)
{
    var deactivated = await TryDeactivateWorkflowAsync(service, entry.ObjectId, ct);
    if (!deactivated)
    {
        output.Warning($"'{entry.DisplayName}' — workflow deactivation failed, remove manually via maker portal.");
        return;
    }
}
```

`TryDeactivateWorkflowAsync` sets `statecode=0` (Draft), `statuscode=1`, and swallows `FaultException<OrganizationServiceFault>` — the second intentional exception to the no-catch rule, with explicit recovery: return false → caller downgrades to Manual.

### Cross-solution membership: RemoveSolutionComponent vs Delete

An orphaned component may still belong to another solution. Query all memberships and use `RemoveSolutionComponent` instead of `Delete` when shared:

```csharp
var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;

// For RemoveFromSolution:
await service.ExecuteAsync(new OrganizationRequest("RemoveSolutionComponent")
{
    ["ComponentId"]        = entry.ObjectId,
    ["ComponentType"]      = entry.ComponentType,
    ["SolutionUniqueName"] = solutionName
}, ct);
```

### Guard: empty S_new prevents mass deletion

If `Solution.xml` has no `<RootComponents>`, the diff produces every existing component as an orphan. Guard and short-circuit:

```csharp
if (sNew.Count == 0)
{
    output.Warning("No components in Solution.xml — orphan check skipped to prevent mass deletion.");
    return [];
}
```

### ConditionOperator.In hard limit

Dataverse `ConditionOperator.In` is limited to 2000 values. Throw before reaching Dataverse — do not silently chunk (chunking changes query semantics and can mask issues):

```csharp
if (ids.Count > 2000)
    throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {ids.Count} IDs (max 2000).");
```

Applied in `IdentifyCustomApiEntityTypesAsync`, `GetCrossSolutionMembershipAsync`, and `GetStillPresentAsync`.

### --no-delete flag: report without acting

`--no-delete` maps to `RunMode.NoDelete`. Pre-import prints the full report but returns an empty deferred list. Post-import is skipped entirely. Mirrors the `--no-delete` flag on `push` — consistent "report without acting" pattern across Flowline commands.

### Deploy command execution order

```
ValidateTarget → ValidateDtap → DriftCheck → ParseSolutionXml →
ConnectDataverse → RunPreImport → Pack → Import → RunPostImport
```

Pre-import runs before Pack: if Dataverse connection or the orphan query fails, the user gets a fast error before the slow pack step.

**Update (2026-07-03):** `RunPreImport`/`RunPostImport` are no longer direct calls to `OrphanCleanupService`. `DeployCommand` builds one `PostDeployContext` per deploy and fans out over every registered `IEnumerable<IPostDeployService>` implementer at each point (`foreach (var postDeployService in postDeployServices) await postDeployService.RunPreImportAsync(...)`, and symmetrically for post-import) — `OrphanCleanupService` is currently the only registered implementer, but the pipeline now runs all of them, not just it. See [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) for why this changed and what it enables.

## Why This Matters

**Without this pattern:**
- Orphaned `plugintype` records block DLL upload — hard failure requiring manual portal intervention.
- Orphaned step configurations keep firing on messages even after the plugin class is gone — silent runtime errors.
- Orphaned workflows may keep running, consuming async job capacity.

**With this pattern:**
- Deploy is idempotent with respect to deletions: running deploy twice leaves the environment in the same state.
- Cross-solution safety: components shared with other solutions are unlinked, not deleted.
- Dependency errors are handled via deferral rather than requiring retry logic from the caller.
- `ExitCode.PartialSuccess = 18` gives CI pipelines a distinct signal: deploy succeeded, cleanup partially failed — human review needed, not a full redeploy.

**The two catches in `OrphanCleanupService` are intentional deviations from the project-wide no-catch rule.** Both have explicit recovery logic: the dependency catch defers the entry; the CustomApi detection catch downgrades unknowns to Manual. Neither swallows silently; both produce visible user-facing output.

## When to Apply

Apply this pattern when all of the following are true:

1. The target is an additive import platform — Dataverse unmanaged solution import never removes components.
2. Components have a stable local manifest — `Solution.xml` `<RootComponents>` is the source of truth.
3. Components have referential integrity — deletion must respect a dependency graph.
4. Components may be shared across deployments — requiring membership checks before delete.

Do not apply the deferral pattern if the platform cascades deletes automatically, or if the full dependency graph is cheaply computable upfront (topological sort is cleaner than catch-and-retry in that case).

## Examples

**Plugin class removed from DLL (hard-failure case resolved):**
```
$ flowline deploy prod
Querying orphan components in MySolution...
Orphan components (2):
  PluginType 3a9f... — delete
  SdkMessageProcessingStep b12c... — delete
2 to delete, 0 to remove from solution, 0 manual
Packing MySolution...
Deploying MySolution to Production...
Deployed! Your solution is live.
```

**Component shared with another solution → RemoveFromSolution:**
```
Orphan components (1):
  WebResource 7d44... — remove from solution
0 to delete, 1 to remove from solution, 0 manual
```

**`--no-delete` for pre-deploy audit:**
```
$ flowline deploy prod --no-delete
Orphan components (3):
  SdkMessageProcessingStep f8a1... — would delete
  PluginType 92cc... — would delete
  PluginAssembly 1d73... — would delete
3 would be deleted, 0 would be removed from solution, 0 manual. (--no-delete active)
```

**Dependency deferral in action:**
```
Deferred: PluginAssembly 1d73... — dependency, will retry post-import
[import completes]
Post-import: retrying deferred orphan cleanup...
PluginAssembly 1d73... deleted
```

**Partial failure exit code for CI (`ExitCode.PartialSuccess = 18`):**
```
WARNING: 1 orphan component couldn't be cleaned up — see above, remove manually via maker portal.
$ echo $?
18
```

Deploy succeeded; manual cleanup required. CI can alert without treating this as a full deploy failure.

## Related

- [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) — `OrphanCleanupService` is now the first implementer of `IPostDeployService`; the interface/DI shape wrapping this algorithm, and the deferred-state instance-field mechanism it relies on
- [retrieve-multiple-async-silent-truncation-2026-05-29.md](../logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md) — why `RetrieveAllAsync` is mandatory for solutioncomponent queries
- [dtap-gate-enforcement-in-deploy-command-2026-06-07.md](dtap-gate-enforcement-in-deploy-command-2026-06-07.md) — other deploy pipeline guards; execution order now includes pre/post-import orphan cleanup phases
- [managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md](managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md) — managed/unmanaged type guard in the same pipeline
- [ai-agent-consumable-cli-contract-2026-06-07.md](ai-agent-consumable-cli-contract-2026-06-07.md) — `ExitCode` enum as stable public API; `PartialSuccess = 18` added in this work
