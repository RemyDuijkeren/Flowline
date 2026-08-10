---
title: "flowline push silently overwrote and adopted web resources owned by other solutions"
date: 2026-08-10
category: docs/solutions/logic-errors/
module: WebResourcePlanner
problem_type: logic_error
component: tooling
severity: critical
symptoms:
  - "flowline push silently overwrote the content of a web resource that exists in Dataverse but belongs to another team's solution"
  - "The overwritten resource was adopted into the pushing solution, so a later flowline deploy propagated the local copy downstream"
  - "Two unmanaged solutions layering the same web resource ended up last-deploy-wins, each team's push silently reverting the other's"
  - "Every run reported success even though it had overwritten and claimed a component it did not own"
root_cause: missing_validation
resolution_type: code_fix
related_components:
  - WebResourcePlanner
  - WebResourceReader
  - WebResourceExecutor
tags:
  - web-resources
  - push
  - ownership
  - solution-isolation
  - silent-overwrite
  - dataverse
---

## Problem

`flowline push` could silently overwrite and adopt a web resource owned by a different, unrelated solution — publishing someone else's content with the local file's, and permanently attaching the record to the pushing solution.

The failure path: a local file whose CRM name doesn't exist in the *current* solution but does exist in Dataverse under *another* solution is a "global orphan." `WebResourceReader.LoadSnapshotAsync` collects these into `snapshot.GlobalOrphans` (`src/Flowline.Core/WebResources/WebResourceReader.cs:61-67`). `WebResourcePlanner.Plan` handled a hit unconditionally: build an Update if content differs, then always queue an `AddToSolution`. The pre-fix shape of that branch in `WebResourcePlanner.cs` was:

```csharp
if (snapshot.GlobalOrphans.TryGetValue(name, out var existing))
{
    if (TryBuildUpdate(local, existing, snapshot, clearDependencyXmlWhenUnchanged: false, out var reasonGlobal))
        plan.Updates.Add(new WebResourcePlanAction(name, WebResourceAction.Update, Entity: existing.Entity, Id: existing.Id, Reason: reasonGlobal));
    plan.AddsToSolution.Add(new WebResourcePlanAction(name, WebResourceAction.AddToSolution, Id: existing.Id, SolutionName: targetSolutionName));
    continue;
}
```

No ownership check gated this because none was possible: `WebResourceReader.GetGlobalWebResourcesByNameAsync` (pre-fix) queried only `name, content, displayname, webresourcetype, dependencyxml` and hardcoded the ownership value:

```csharp
return result.Entities
    .Select(e => ToDataverseWebResource(e, new WebResourceOwnership(0, false)))
    .ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase)
    .AsReadOnly();
```

`WebResourceOwnership` in `src/Flowline.Core/Models/WebResourceModels.cs` was, pre-fix, `(int NonDefaultUnmanagedSolutionCount, bool IsInCurrentUnmanagedSolution, bool HasManagedSolutionReference = false)` — every global orphan looked identically unowned, `(0, false, false)`, whether it truly had no owner or was owned by a live ISV-managed solution.

**The diagnostic tell — an asymmetry inside one class.** In the same `Plan` method, the *delete* path (`dataverseNames.Except(localNames)`, still at `WebResourcePlanner.cs:90-113`) discriminated three ownership cases before acting: sole non-default unmanaged owner → `Delete`; shared with another unmanaged solution, or a managed reference → `RemoveFromSolution` (drop only this solution's link); anything unclear → `Skip`. The *adopt* path a few lines above discriminated none. The code was meticulous about deleting a resource it might not fully own, and unguarded about overwriting and permanently claiming one.

## Symptoms

- A local file whose resolved CRM name collided with another team's web resource (most reachable through **verbatim mode**: `docs/plans/2026-06-13-001-feat-webresource-verbatim-mode-plan.md` R3/R4 — any local top-level folder matching a publisher-prefix pattern round-trips as the CRM name verbatim, dropping the solution segment. `WebResources/dist/dh_/lib/validation.js` becomes CRM name `dh_/lib/validation.js`, independent of which team's local repo it's pushed from — a name very likely already owned elsewhere under a shared-namespace layout).
- Push reported success while quietly replacing another solution's web resource content and publishing it live.
- The record got added to the pushing solution (`AddToSolution` in the plan, `AddSolutionComponent` executed unconditionally in `WebResourceExecutor.ExecuteAsync`), so a later `flowline deploy` shipped the local copy downstream — an unmanaged solution import overwrites the target's web resource whenever it's a component of the imported solution.
- Two unmanaged solutions layering the same component: each team's next push reverted the other's last change, both reporting success, with no error surfaced by either side.

## What Didn't Work / What Was Learned

**Two tests looked like a spec for the overwrite behavior — they weren't.** `SyncSolutionAsync_ExistsInOtherSolutionWithDifferentContent_ShouldUpdateAndAddToSolution` and its same-content sibling (`tests/Flowline.Core.Tests/WebResourceServiceTests.cs:337`, `:361`) both call `SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", ...))` and never call the separate `SetupOwnership(webResourceId, ...)` helper (`WebResourceServiceTests.cs:1201-1214`). `SetupGlobalOrphans` (`:1173-1179`) only mocks the `webresource` query, not the `solutioncomponent` ownership query — so `GetOwnershipAsync` returns an empty result set, i.e. genuinely unowned. On top of that, both fixtures name the resource `my_MySolution/shared.js` — `my_` is `MySolution`'s *own* publisher prefix, the same solution doing the push. These tests model **bootstrap adoption of an unowned record**, not **overwriting a foreign-owned one**. Read what a fixture actually mocks before treating its passing assertion as a locked-in specification — a green test proves the path it exercises, not the path its name suggests.

**First attempt at the depends-exemption gated on the wrong field.** Once ownership blocking existed, a `// flowline:depends` declaration was meant to downgrade a block to a reference-only `Skip`. Gating that exemption on `LocalWebResource.DependsOn` was wrong: `WebResourceReader.AutoMatchResxDependencies` (`WebResourceReader.cs:217-254`) enriches `DependsOn` by folder-qualified base-name matching between RESX and JS files, with no `// flowline:depends` annotation behind the added entries. A JS+RESX pair sharing a base name would silently acquire the exemption for a dependency the author never declared. Fixed by adding `LocalWebResource.AnnotatedDependsOn` (`WebResourceModels.cs:64-75`) — the raw parsed annotations, never touched by enrichment — and gating `WebResourcePlanner.CollectDependsOnReferences` (`WebResourcePlanner.cs:250-252`, comment explains the distinction) on that field instead.

**The reference-only warning didn't fire in its main case.** `WebResourceSyncPlan.TotalChanges` (`WebResourceModels.cs:124`) excludes `Skips` by design. A push where every action was a skip hit `WebResourceService`'s `TotalChanges == 0` early-return before reaching `WebResourceExecutor`, so the warning meant to fire for a reference-only skip never rendered — a second, unbranched loop in the no-change path rendered plain skip lines instead. Fixed with a shared `WebResourceExecutor.RenderSkips` (`WebResourceExecutor.cs:103-112`), called from the executor's normal path, the no-change early-return, and dry-run — one render function instead of three copies that could drift again.

## Solution

**1. Ownership is now read, not assumed, for global orphans.**

`WebResourceOwnership` gained `OwningSolutionNames` (`WebResourceModels.cs:87-94`):

```csharp
public record WebResourceOwnership(
    int NonDefaultUnmanagedSolutionCount,
    bool IsInCurrentUnmanagedSolution,
    bool HasManagedSolutionReference = false,
    IReadOnlyList<string>? OwningSolutionNames = null)
{
    public IReadOnlyList<string> OwningSolutionNames { get; init; } = OwningSolutionNames ?? [];
}
```

`WebResourceReader.GetOwnershipAsync` (`WebResourceReader.cs:300-336`) already existed for the current-solution delete path; `GetGlobalWebResourcesByNameAsync` now takes `solutionName` and calls the same ownership resolution instead of hardcoding `(0, false)` (`WebResourceReader.cs:378-397`, via `ResolveOwnershipBoundedAsync` at `:345-365`).

**2. `WebResourcePlanner` refuses the adopt when ownership says no.**

`WebResourcePlanner.cs:30-54`:

```csharp
if (snapshot.GlobalOrphans.TryGetValue(name, out var existing))
{
    var foreignOwned = existing.Ownership.NonDefaultUnmanagedSolutionCount > 0
                     || existing.Ownership.HasManagedSolutionReference;
    if (foreignOwned)
    {
        PlanForeignOwned(local, existing, name, referencedNames, ambiguousReferences, plan, ownershipViolations);
        continue;
    }
    // ... existing Update + AddToSolution, now reached only when truly unowned
}
```

**Both disjuncts are load-bearing.** `NonDefaultUnmanagedSolutionCount` only counts unmanaged owners, so a resource owned solely by a managed (ISV) solution is `(0, false, true)` — a count-only predicate (`> 0`) would route that case straight into the adopt branch it was meant to block. Violations accumulate in `ownershipViolations` and, after the loop, throw `FlowlineException(ExitCode.ValidationFailed, ...)` (`WebResourcePlanner.cs:70-77`) *before* the exists-in-both loop runs any write — a caught violation stops the whole push, not just that one file.

**3. An explicit escape hatch, not a silent one.** `PlanForeignOwned` (`WebResourcePlanner.cs:179-209`) checks `// flowline:depends` annotations first: a name a local file explicitly declared as a dependency becomes a reference-only `Skip` (`ReferencedNotOwnedReason`) instead of a hard block; an annotation that's a bare name matching multiple candidates fails with its own disambiguation message rather than silently picking one or falling through to the generic ownership error.

**4. Ownership fan-out is bounded.** `ResolveOwnershipBoundedAsync` (`WebResourceReader.cs:343-365`) caps concurrent `solutioncomponent` queries at `MaxOwnershipParallelism = 8`, matching `WebResourceExecutor.MaxParallelism` (`WebResourceExecutor.cs:14`) — both govern concurrent requests against the same org, so a cold/bootstrap push with many local files doesn't fan out one unbounded request per file into Dataverse's service-protection limits.

## Why This Works

The bug wasn't "missing a check" in the abstract — it was that a resource reachable through *two different lifecycle actions* (delete-or-remove vs. adopt-and-overwrite) got ownership discrimination on only one of them. The fix doesn't add a new concept; it applies the ownership model the delete path already had (`NonDefaultUnmanagedSolutionCount`, `HasManagedSolutionReference`) to the adopt path, sourced from the same `GetOwnershipAsync` query the delete path already ran. Symmetry, not novelty, closes the hole. The `AnnotatedDependsOn` split and the `RenderSkips` consolidation are the same shape at smaller scale: each exists because a downstream consumer (the exemption gate, the warning renderer) implicitly trusted a field that had picked up meaning from an unrelated process (enrichment, an early-return optimization) it wasn't aware of.

## Prevention

**When a resource is reachable by more than one lifecycle action (create / adopt / update / delete), a guard on one path is not a guard on the others.** Ownership, permission, and validation checks tend to get written where the *risk feels obvious* — usually the destructive-looking path (delete). The moment you add or review a guard like that, go find every other action that can touch the same resource and ask whether it makes the same assumption the guarded path just stopped making. Here, `Delete`/`RemoveFromSolution` and `AddToSolution` both act on records this solution doesn't necessarily own; only one of them checked. The tell was structural, not behavioral: one method, one loop family with a three-way ownership match, sitting next to another loop family with none. That asymmetry is visible on read — it doesn't require reproducing the bug.

**A fixture that mocks no ownership proves the unowned path, not the owned one.** `SetupGlobalOrphans` without `SetupOwnership` is a legitimate, useful test — it's just testing bootstrap adoption, not cross-solution overwrite, no matter what the test's name says. Before trusting a test as a specification for "this behavior is intended," check what its setup actually stubs: an omitted mock call defaults to *empty result*, which reads as "no owner" — semantically identical to "ownership was never asked about." A test name describing the scenario ("ExistsInOtherSolution") can drift out of sync with what the mock setup actually arranges ("same solution's own prefix, no ownership query stubbed at all"). When a green test is cited as proof a risky behavior is intentional, read its arrange block, not its name.

**Enriched/derived fields are not the same field as the one the author wrote.** `DependsOn` (enriched) vs. `AnnotatedDependsOn` (raw) is the general pattern: once a field is downstream of an inference step (auto-matching, defaulting, backfilling), any consumer that needs to answer "did a human actually assert this" must read the pre-enrichment value, not the field that also serves the enriched consumers. Naming the split explicitly (as here) is cheaper than the alternative — a security- or ownership-relevant gate quietly keyed off a field whose value can originate from a heuristic instead of a declaration.

## Related

- [`design-patterns/reverse-relationship-inverts-what-orphaned-means.md`](../design-patterns/reverse-relationship-inverts-what-orphaned-means.md) — the same higher-level lesson (positive attribution beats treating unattributed as safe) in a different subsystem. That one is about *deleting* Custom APIs across plugin projects, with a reversed FK direction; this one is about *adopting and overwriting* web resources across solutions. Sibling occurrences, not the same bug.
- [`design-patterns/webresource-dependency-registration-patterns.md`](../design-patterns/webresource-dependency-registration-patterns.md) — same files and the same "global orphan" vocabulary. Its guidance on exempting annotation-referenced resources from deploy-time orphan *deletion* is the direct sibling of this doc's reference-only skip on push *adoption*. Also the source of the `AutoMatchResxDependencies` enrichment behaviour that made `AnnotatedDependsOn` necessary.
- [`architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md`](../architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md) — prior art for ownership-aware handling (query all memberships; remove-from-solution when shared rather than delete). This fix brings that principle to the adopt path for the first time.
