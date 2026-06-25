---
title: "Orphan plugin assembly deletion fails when child plugin types have step dependencies"
date: 2026-06-25
module: Flowline.Core.Services.PluginService
problem_type: integration_issue
component: tooling
severity: high
symptoms:
  - "flowline push --scope plugins --force throws FaultException when deleting orphan plugin assemblies"
  - "Error: PluginAssembly cannot be deleted because it is referenced by 2 other components"
  - "Deletion fails even after removing the assembly from the named solution via RemoveSolutionComponent"
root_cause: logic_error
resolution_type: code_fix
tags:
  - dataverse-api
  - plugin-assembly
  - dependency-management
  - cascade-delete
  - orphan-cleanup
related_components:
  - PluginService.WarnOrphanAssembliesAsync
  - sdkmessageprocessingstep
  - plugintype
---

# Orphan plugin assembly deletion fails when child plugin types have step dependencies

## Problem

`flowline push --scope plugins --force` fails with a Dataverse `FaultException` when attempting to delete an orphan plugin assembly — an assembly present in the Dataverse environment with no local source file. The deletion is blocked because Dataverse's dependency check fires before any cascade can run, and that check finds plugin steps (`sdkmessageprocessingstep`) referencing plugin types (`plugintype`) that belong to the assembly.

## Symptoms

- `flowline push --scope plugins --force` exits with an unhandled `System.ServiceModel.FaultException`.
- Error message: `The PluginAssembly(<guid>) component cannot be deleted because it is referenced by 2 other components.`
- Error fires in `WarnOrphanAssembliesAsync` (`PluginService.cs`) at the `DeleteAsync("pluginassembly", ...)` call.
- The assembly is not missing from the solution — it is registered in Dataverse and has live plugin types, steps, and optionally images and custom APIs.

## What Didn't Work

**Theory 1 — Remove solution component records first.**
Queried `solutioncomponents` for the assembly GUID and found 2 records: one for the named solution (`Cr07982`) and one for `Default`. Assumed those were the "2 other components" blocking deletion. Added a `RemoveSolutionComponent("Cr07982")` call before `DeleteAsync`, which removed the assembly from the named solution. `DeleteAsync` still failed with the same error. The `solutioncomponent` membership records and the blocking dependency records are completely unrelated — removing a component from a solution does not clear the step-to-plugin-type dependency records that block deletion.

**Theory 2 — Rely on Dataverse cascade.**
Assumed `DeleteAsync("pluginassembly", ...)` would cascade-delete all child records (plugin types, steps, images, custom APIs) automatically. This assumption is wrong in the presence of external dependency records. Dataverse's deletion sequence is: run dependency check → if blocked, abort entirely. The cascade only executes after the dependency check passes. Because the plugin types are referenced by active plugin steps, the check never passes and the cascade never runs.

## Solution

Replaced the single `DeleteAsync("pluginassembly", ...)` call in `WarnOrphanAssembliesAsync` (`PluginService.cs`) with an explicit child-deletion sequence that mirrors `RunDeletesAsync` in `PluginExecutor.cs`. A `RegistrationSnapshot` is loaded for the orphan assembly (already done for the cascade-log display path; now done unconditionally when `willDelete` is true), then all children are deleted in reverse dependency order before the assembly itself is deleted.

**Before:**

```csharp
if (willDelete)
    await service.DeleteAsync("pluginassembly", entity.Id, cancellationToken).ConfigureAwait(false);
```

**After:**

```csharp
RegistrationSnapshot? orphanSnapshot = null;
if (showCascade || willDelete)
{
    var stub = new PluginAssemblyMetadata("", "", [], "", "", null, "", []);
    orphanSnapshot = await _reader.LoadSnapshotAsync(service, entity.Id, stub, solutionName, cancellationToken).ConfigureAwait(false);
}

// ...cascade log display if showCascade...

if (willDelete && orphanSnapshot != null)
{
    // Dataverse blocks assembly DeleteAsync when its child plugin types are referenced by
    // steps or custom API entries (dependency check fires before cascade runs).
    // Must delete children manually in reverse dependency order — same as RunDeletesAsync.
    foreach (var e in orphanSnapshot.Images)
        await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
    foreach (var e in orphanSnapshot.ResponseProps)
        await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
    foreach (var e in orphanSnapshot.RequestParams)
        await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
    foreach (var e in orphanSnapshot.Steps)
        await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
    foreach (var e in orphanSnapshot.CustomApis)
        await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
    foreach (var (_, pluginType) in orphanSnapshot.PluginTypes)
        await service.DeleteAsync(pluginType.LogicalName, pluginType.Id, cancellationToken).ConfigureAwait(false);
    await service.DeleteAsync("pluginassembly", entity.Id, cancellationToken).ConfigureAwait(false);
}
```

The `RegistrationSnapshot` already contains all child record sets (`Images`, `ResponseProps`, `RequestParams`, `Steps`, `CustomApis`, `PluginTypes`), so no new queries are needed beyond the existing snapshot load.

## Why This Works

Dataverse enforces a dependency graph for solution components. When a plugin type (`componenttype=90`) is referenced by a plugin step (`componenttype=92`) via a `Solution Internal` dependency (`dependencytype=2`), the dependency record blocks deletion of anything that would cascade to that plugin type — including its parent assembly. The dependency check runs first, before any cascade or child-delete logic, and if it finds blocking records it aborts the entire operation.

The fix resolves this by explicitly deleting the children that hold those dependency records — steps, custom APIs, images, request params, response props — before attempting to delete the plugin types, and the plugin types before the assembly. With no children remaining, no blocking dependency records exist and `DeleteAsync("pluginassembly", ...)` succeeds.

This matches the deletion order already used in `RunDeletesAsync` (`PluginExecutor.cs`) for planned deletions. The orphan path in `WarnOrphanAssembliesAsync` was bypassing that logic and calling `DeleteAsync` directly on the assembly. The correct pattern is documented in [orphan-cleanup-two-phase-deploy-pipeline.md](../architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md) for the `deploy` command — this fix brings `push --force` inline with that same order.

**Diagnostic tool:** `RetrieveDependenciesForDelete(ComponentType=91, ObjectId=<assemblyId>)` via the Dataverse Web API returns the exact blocking dependency records. Use this first when investigating any "cannot be deleted because it is referenced" error — it identifies the component types and dependency types involved, and eliminates false leads like solution component membership.

```http
GET /api/data/v9.2/RetrieveDependenciesForDelete(ComponentType=91,ObjectId=<assemblyId>)
```

Response example showing a step blocking a plugin type:
```json
{
  "dependentcomponenttype": 92,
  "dependentcomponentobjectid": "<step-guid>",
  "requiredcomponenttype": 90,
  "requiredcomponentobjectid": "<plugintype-guid>",
  "dependencytype": 2
}
```

## Prevention

- Any code path that deletes a `pluginassembly` record directly must first delete children in the correct order: images → response properties → request parameters → steps → custom APIs → plugin types → assembly. Never assume Dataverse cascade will handle this automatically.
- When adding a new deletion code path (cleanup commands, force-delete utilities), reuse or call `RunDeletesAsync` instead of issuing `DeleteAsync` on top-level entities directly. The correct order is already encoded there — duplication creates divergence and bugs.
- When Dataverse returns "cannot be deleted because it is referenced by N other components", call `RetrieveDependenciesForDelete` immediately. Do not assume the N components are solution membership records — they are almost always actual entity dependency records (steps, custom APIs, etc.).
- `solutioncomponent` records (the assembly's presence in a named solution) are distinct from dependency records (plugin steps referencing plugin types). Removing a component from a solution does not clear its dependency graph.

## Related

- [orphan-cleanup-two-phase-deploy-pipeline.md](../architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md) — correct deletion order pattern for the `deploy` command's `OrphanCleanupService`; this bug is `push --force` not following that same order in `WarnOrphanAssembliesAsync`
- `PluginExecutor.cs` — `RunDeletesAsync`: canonical deletion order for plugin registration records
- Dataverse Web API: `RetrieveDependenciesForDelete` — diagnostic endpoint for blocked deletions
