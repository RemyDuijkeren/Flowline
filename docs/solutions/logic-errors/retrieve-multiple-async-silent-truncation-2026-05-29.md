---
title: "RetrieveMultipleAsync silently truncates results at default page size"
date: 2026-05-29
last_updated: 2026-07-07
category: docs/solutions/logic-errors/
module: flowline-cli
problem_type: logic_error
component: tooling
severity: high
symptoms:
  - "GenerateReader returned an incomplete entity list for large solutions — entities past ~5000 were absent from generated early-bound types with no error"
  - "WebResourceReader silently skipped web resources whose solutioncomponent entries fell beyond the page boundary"
  - "Commands exited with success; output looked complete; no exception or warning was raised"
root_cause: wrong_api
resolution_type: code_fix
related_components:
  - generate-reader
  - web-resource-reader
tags:
  - dataverse
  - paging
  - retrieve-multiple
  - silent-truncation
  - organization-service
  - extension-method
---

# RetrieveMultipleAsync silently truncates results at default page size

## Problem

`IOrganizationServiceAsync2.RetrieveMultipleAsync` returns at most one page of results (default page size ~5000 records). When a Dataverse query matches more records than that, the SDK silently returns only the first page — no error, no exception, no `MoreRecords` check fails loudly. Callers that read `.Entities` directly receive a partial result with no indication that data was missing.

## Symptoms

- `flowline generate` produces type stubs for only a subset of entities in large solutions; entities past the ~5000 mark are simply absent from generated output.
- `flowline push` (webresources) silently skips web resources whose `solutioncomponent` entries fall beyond the page boundary in large orgs.
- Both failures are non-obvious: the command exits with success, output looks complete, and no exception is thrown.

## What Didn't Work

- **Increasing `Count` on a single call** — Dataverse enforces a server-side page cap. Requesting more than the limit is silently clamped; `MoreRecords` is still `true` when additional pages exist.
- **Assuming `TopCount` helps** — `TopCount` is a hard upper bound, not a "give me everything" knob. It does not bypass pagination; it stops earlier.
- **Not checking `MoreRecords`** — `EntityCollection.MoreRecords` is the only reliable signal that another page exists. Any call that does not inspect this flag after each response silently truncates results.
- **Setting `PageInfo.Count = 5000`** — this sets the page size but does not iterate pages; a single call still returns only one page even when `MoreRecords` is `true`.

## Solution

Add a `RetrieveAllAsync` extension method on `IOrganizationServiceAsync2` that drives the `MoreRecords` / `PagingCookie` loop and returns a flat `List<Entity>`.

**`src/Flowline.Core/Services/DataverseExtensions.cs`**

```csharp
public static async Task<List<Entity>> RetrieveAllAsync(
    this IOrganizationServiceAsync2 service,
    QueryExpression query,
    CancellationToken cancellationToken = default)
{
    var all = new List<Entity>();
    query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, ReturnTotalRecordCount = false };

    EntityCollection page;
    do
    {
        page = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        all.AddRange(page.Entities);
        query.PageInfo.PageNumber++;
        query.PageInfo.PagingCookie = page.PagingCookie;
    } while (page.MoreRecords);

    return all;
}
```

**Before** (silently truncates at ~5000):

```csharp
var result = await service.RetrieveMultipleAsync(query, cancellationToken);
var entities = result.Entities; // may be incomplete
```

**After** (fetches all pages):

```csharp
var entities = await service.RetrieveAllAsync(query, cancellationToken);
```

### Call sites fixed

| File | Method | Query target |
|------|--------|-------------|
| `src/Flowline.Core/Services/GenerateReader.cs` | `GetSolutionEntityLogicalNamesAsync` | `solutioncomponent` (entities in solution) |
| `src/Flowline.Core/Services/GenerateReader.cs` | `GetSolutionCustomApiMessageNamesAsync` | `customapi` joined to `solutioncomponent` |
| `src/Flowline.Core/Services/WebResourceReader.cs` | `GetWebResourcesForSolutionAsync` | `webresource` joined to `solutioncomponent` |
| `src/Flowline.Core/Services/OrphanCleanupService.cs` | `QuerySolutionComponentsAsync` | `solutioncomponent` (S_old query for orphan diff) |
| `src/Flowline.Core/Services/OrphanCleanupService.cs` | `GetCrossSolutionMembershipAsync` | `solutioncomponent` (cross-solution membership check) |
| `src/Flowline.Core/Services/OrphanCleanupService.cs` | `GetStillPresentAsync` | `solutioncomponent` (post-import re-check of deferred components) |
| `src/Flowline.Core/Services/OrphanCleanupService.cs` | `IdentifyEntityDetectedTypesAsync` (renamed from `IdentifyCustomApiEntityTypesAsync`) | `customapi`, `customapirequestparameter`, `customapiresponseproperty`, `bot`, `connectionreference` |

### Call sites that don't need paging

| File | Method | Why it's safe |
|------|--------|---------------|
| `WebResourceReader` | `GetOwnershipAsync` | Queries `solutioncomponent` for a single `webresourceid` — always a small, bounded set |
| `WebResourceReader` | `GetGlobalWebResourcesByNameAsync` | Input is a list of local orphan filenames you control — always bounded |
| `PluginReader` | All methods | Scoped to a specific assembly — steps, types, and images for one assembly are always far below 5000 |

## Why This Works

Dataverse paginates server-side. Each `RetrieveMultipleAsync` response carries:

- `MoreRecords` — `true` when at least one additional page exists.
- `PagingCookie` — an opaque token that must be passed back on the next request so Dataverse knows where to resume. Without it, Dataverse re-starts from page 1 on every call.
- `PageInfo.PageNumber` — must be incremented on each iteration alongside the cookie.

The loop continues until `MoreRecords` is `false`, at which point the final page has been consumed.

`ReturnTotalRecordCount = false` is intentional — requesting the count forces an extra server-side aggregation per page, adding latency with no benefit for callers that only need records.

## Prevention

**Identifying future call sites that need paging**

Any `RetrieveMultipleAsync` call where result count depends on user data (solution size, org size, number of components) is a candidate. Ask: "Could a customer with a large org have more than 5000 matching records?" If yes, use `RetrieveAllAsync`.

Patterns to audit:
- Queries against `solutioncomponent` without a `TopCount` or single-ID filter
- Queries against `webresource`, `pluginstep`, `sdkmessageprocessingstep`, `customapi` scoped only by solution
- Any query that drives a "list all X in solution" or "list all X in org" operation

**Code review rule**

Search for `RetrieveMultipleAsync` calls. For each one, verify it is either:
1. Bounded by a known-small input set (single ID, `TopCount = 1`, list you control), or
2. Replaced with `RetrieveAllAsync`

Never read `.Entities` after `RetrieveMultipleAsync` for a solution-scoped or org-scoped query without confirming `MoreRecords` is handled.

**Unit test approach**

Mock `RetrieveMultipleAsync` to return `MoreRecords = true` on the first call and `MoreRecords = false` on the second. Confirm `PagingCookie` is threaded through and both pages' entities appear in the final result.

## Related Issues

- Introduced alongside `flowline generate` command — entity discovery for `pac modelbuilder build` was the first call site that triggered the issue in practice.
- Related pattern: [`docs/solutions/architecture-patterns/sync-first-remove-mapping-replace-dotnet-build-2026-05-17.md`](../architecture-patterns/sync-first-remove-mapping-replace-dotnet-build-2026-05-17.md) — covers the broader principle of not silently swallowing data divergence in Dataverse operations.
