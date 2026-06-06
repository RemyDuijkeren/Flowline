---
title: "Dataverse asyncoperation Has No percentcomplete Field — operationtype Is 54 Not 202"
date: 2026-06-06
category: docs/solutions/documentation-gaps
module: dataverse-async-operations
problem_type: documentation_gap
component: tooling
severity: medium
applies_when:
  - Implementing progress tracking for pac solution sync or clone --async operations
  - Querying asyncoperation entity to monitor Dataverse job advancement
  - Relying on Microsoft entity reference docs for asyncoperation operationtype values
tags:
  - dataverse
  - asyncoperation
  - pac-cli
  - percentcomplete
  - operationtype
  - progress-tracking
  - web-api
---

# Dataverse asyncoperation Has No percentcomplete Field — operationtype Is 54 Not 202

## Context

Implementing a real progress bar (0–100%) for `flowline sync` and `flowline clone` required polling Dataverse's `asyncoperation` entity. Two assumptions from Microsoft's documentation turned out to be wrong:

1. **`asyncoperation.percentcomplete` exists** — this field appears in some Microsoft examples and forum answers, implying it reflects job completion percentage.
2. **`pac solution sync/clone --async` creates operationtype 202** — the entity reference docs list operationtype 202 as "Export Solution Async", which seemed to match the `ExportSolutionAsync` message.

Neither assumption held up under empirical testing against a real Dataverse environment.

## Guidance

**`asyncoperation.percentcomplete` does not exist.** The field is absent from the entity — not null, not zero, simply not present. Querying it via the Web API returns HTTP 400 "Could not find property 'percentcomplete'". SQL4CDS (which queries the same underlying store) shows no such column. Avoid any polling loop or progress display that depends on this field.

**`pac solution sync/clone --async` uses operationtype 54**, not 202. The `asyncoperation` record created by these PAC commands has:
- `operationtype = 54` ("Execute Async Request")
- `messagename = ExportSolutionAsync`

The Microsoft entity reference listing of operationtype 202 for "Export Solution Async" does not match what PAC CLI actually produces.

**The available state signal is binary.** The only progression observable on the `asyncoperation` record is:
- `statecode = 2` / `statuscode = 2` → In Progress
- `statecode = 3` / `statuscode = 30` → Succeeded

There is no intermediate percentage. A status-only tracker (spinner label reflecting statecode) is technically possible but adds complexity over the existing PAC spinner, which already conveys elapsed time. It was judged not worth implementing.

## Why This Matters

Any implementation that depends on `percentcomplete` will fail at runtime with an HTTP 400 from the Web API. Any query filtering by `operationtype = 202` will return zero results and miss the job entirely. Both gaps cause silent failure rather than a loud error, which makes debugging slow.

The combination means that real-time progress tracking for `pac solution sync/clone --async` operations is not feasible via the `asyncoperation` entity as of June 2026.

## When to Apply

- Attempting to show a real Dataverse job progress bar for sync or clone commands
- Writing polling logic that queries `asyncoperation` for PAC-submitted solution jobs
- Relying on Microsoft entity reference docs for `asyncoperation.operationtype` values

## Examples

**Web API query that fails:**
```
GET [org]/api/data/v9.2/asyncoperations?$select=statecode,statuscode,percentcomplete&$filter=operationtype eq 202
```
→ HTTP 400: `Could not find property 'percentcomplete'`

**SQL4CDS query that shows reality:**
```sql
SELECT TOP 10 operationtype, messagename, statecode, statuscode, createdon
FROM asyncoperation
ORDER BY createdon DESC
```
Result while `pac solution sync --async` is running:
```
operationtype | messagename           | statecode | statuscode
54            | ExportSolutionAsync   | 2         | 2
```
No `percentcomplete` column exists in the result set.

**Correct Web API query for monitoring:**
```
GET [org]/api/data/v9.2/asyncoperations?$select=statecode,statuscode,messagename&$filter=operationtype eq 54 and messagename eq 'ExportSolutionAsync'&$orderby=createdon desc&$top=1
```
Poll `statecode` until it reaches 3 (Succeeded) or an error state.

## Related

- `docs/plans/2026-06-06-002-feat-dataverse-progress-tracking-plan.md` — the abandoned plan that produced these findings (U0 spike)
