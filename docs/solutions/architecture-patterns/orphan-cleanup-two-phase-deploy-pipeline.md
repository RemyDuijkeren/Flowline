---
title: Orphan component cleanup — two-phase deploy pipeline
date: 2026-06-10
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

### S_new: use Solution.xml RootComponents, not customizations.xml

The diff source must come from `Solution.xml` `<RootComponents>`, not from `customizations.xml`. `customizations.xml` contains entity metadata (attribute definitions, form XML) — not component object references. `<RootComponents>` contains exactly the `(ObjectId, ComponentType)` pairs that map to `solutioncomponent` records.

```csharp
var rootComponents = doc.Root
    ?.Element("SolutionManifest")
    ?.Element("RootComponents")
    ?.Elements("RootComponent")
    ?? [];

foreach (var component in rootComponents)
{
    if (!int.TryParse(component.Attribute("type")?.Value, out var type)) continue;
    if (!Guid.TryParse(component.Attribute("id")?.Value, out var id)) continue;
    result.Add((id, type));
}
```

### Always use RetrieveAllAsync for solutioncomponent queries

`RetrieveMultipleAsync` silently truncates at 5000 records. A solution with more than 5000 components silently drops entries, causing false positives in the orphan diff (records appear to be orphans because they weren't seen in the first page). All `solutioncomponent` queries must paginate:

```csharp
// Bad: silently truncates at 5000
var result = await service.RetrieveMultipleAsync(query, ct);

// Good: pages through all results
var entities = await service.RetrieveAllAsync(query, ct);
```

See `src/Flowline.Core/Services/OrganizationServiceExtensions.cs` for the paging extension. Applied in all four query methods in `OrphanCleanupService`.

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

- [retrieve-multiple-async-silent-truncation-2026-05-29.md](../logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md) — why `RetrieveAllAsync` is mandatory for solutioncomponent queries
- [dtap-gate-enforcement-in-deploy-command-2026-06-07.md](dtap-gate-enforcement-in-deploy-command-2026-06-07.md) — other deploy pipeline guards; execution order now includes pre/post-import orphan cleanup phases
- [managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md](managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md) — managed/unmanaged type guard in the same pipeline
- [ai-agent-consumable-cli-contract-2026-06-07.md](ai-agent-consumable-cli-contract-2026-06-07.md) — `ExitCode` enum as stable public API; `PartialSuccess = 18` added in this work
