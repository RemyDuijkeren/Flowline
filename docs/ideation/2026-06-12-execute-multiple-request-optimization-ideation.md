---
date: 2026-06-12
topic: push-performance
focus: Batch Dataverse write operations with ExecuteMultipleRequest for measurable perf gain
mode: repo-grounded
origin: docs/Features/CR-execute-multiple-request.md
---

# Ideation: ExecuteMultipleRequest Batching in Push

## Idea

Batch sequential Dataverse operations into `ExecuteMultipleRequest` (200-item chunks). The 30% gain
is real when there are many operations — most meaningful on first-time registration with large
component sets.

## Where Batching Actually Helps

| Operation | Batchable? | Notes |
|---|---|---|
| Deletes | ✅ Yes | No pre-reads, no response IDs needed, `ReturnResponses = false` |
| Add-to-solution | ✅ Yes | Same — clean candidate |
| Updates | ⚠️ No | Each update calls `WarnIfComponentExistsInOtherSolutionsAsync` first — per-item read dominates |
| Plugin creates | ❌ No | Sequential by design, counts tiny (0–3 warm run) |
| Web resource creates | ❌ No | Already parallel via `Task.WhenAll` |

**Recommendation: batch deletes and add-to-solution only.** Updates and creates stay as individual calls.

## Implementation Note

With `ContinueOnError = false`, a Dataverse fault throws as `OrganizationServiceFault` directly from
the SDK — the `response.Responses` fault-check branch is a dead code path. Worth a comment but not
a bug.

```csharp
static async Task ExecuteMultipleBatchAsync(
    IOrganizationServiceAsync2 service,
    IEnumerable<OrganizationRequest> requests,
    CancellationToken cancellationToken)
{
    foreach (var batch in requests.Chunk(200))
    {
        var req = new ExecuteMultipleRequest
        {
            Requests = new OrganizationRequestCollection(),
            Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = false }
        };
        req.Requests.AddRange(batch);
        await service.ExecuteAsync(req, cancellationToken).ConfigureAwait(false);
    }
}
```

## Priority

Low. Useful on initial registration of large solutions but most warm runs have small delta sets.
Profile before implementing to confirm it's actually a bottleneck.
