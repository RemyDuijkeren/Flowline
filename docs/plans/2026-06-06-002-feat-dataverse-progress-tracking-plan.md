---
title: "feat: Add Dataverse progress tracking to sync and clone"
type: feat
status: abandoned
date: 2026-06-06
---

# feat: Add Dataverse progress tracking to sync and clone

## Abandonment Notice (2026-06-06)

U0 spike disproved the core premise. **`percentcomplete` does not exist on the `asyncoperation` entity** — the field is absent from both the Web API and SQL4CDS (which queries the same underlying store). No implementation was written.

Additional finding: `pac solution sync --async` creates an `asyncoperation` record with `operationtype = 54` ("Execute Async Request", `messagename = ExportSolutionAsync`), not operationtype 202 as the entity reference docs suggested. The available state progression is `statecode 2 → 3` (In Progress → Completed) with no intermediate percentage.

A status-only tracker (spinner label reflecting statecode) is technically possible but was judged not worth the complexity given the existing PAC spinner already conveys elapsed time.

---

## Summary

Replace the time-based PAC spinner ("2.46% of max time allotted") in `flowline sync` and `flowline clone` with a real progress bar driven by `asyncoperation.percentcomplete` from Dataverse. Both commands already call `pac solution sync/clone --async`, which internally submits an `ExportSolutionAsync` job that creates an `asyncoperation` record (operationtype 202). A new `AsyncOperationPoller` service queries that record in parallel with the PAC process and feeds actual job progress to a Spectre.Console progress bar. If Dataverse is unreachable, the command falls back to the current spinner before opening a progress bar. If connected but the job is not found during the discovery window, the progress bar shows but may not advance — PAC drives to completion and the bar reaches 100% on exit.

---

## Problem Frame

`flowline sync` and `flowline clone` can take 1–5 minutes on large solutions. The current spinner shows a string like `Syncing solution MyApp... (execution time: 00:01:28 and 2.46% of max time allotted)`. The percentage is PAC's own time-based estimate (elapsed ÷ max allowed timeout), not the operation's actual progress — it conveys no useful information about how far along the job is. Dataverse's `asyncoperation` entity exposes a `percentcomplete` field that should reflect true job progress.

---

## Requirements

- R1. `flowline sync` shows a progress bar (0–100%) that reflects actual Dataverse job advancement, not elapsed time.
- R2. `flowline clone` shows the same progress bar for the export phase.
- R3. If the Dataverse connection fails, commands fall back to the current spinner. If connected but the async job is not found within the discovery window, the progress bar shows at 0% until PAC completes. No change in exit code or output in either case.
- R4. Progress polling uses the existing `IOrganizationServiceAsync2` — no new HTTP client or authentication mechanism.
- R5. Polling does not interfere with PAC driving the operation to completion; PAC remains the authoritative completion signal.

---

## Scope Boundaries

- No changes to `flowline push` (solution import, operationtype 203) — out of scope for this plan, though `AsyncOperationPoller` should accept operationtype as a parameter to enable reuse later.
- No changes to `flowline provision` (`pac admin copy/create`) — environment copy is tracked via the Power Platform BAP admin API, not Dataverse `asyncoperation`. The target environment does not exist during `admin create`, making `IOrganizationServiceAsync2` inapplicable.
- No new command flags (no `--progress` or `--no-progress`); progress bar is automatic with graceful fallback.
- No change to exit codes, error messages, or any other command output beyond the spinner-to-progress-bar visual swap.

### Deferred to Follow-Up Work

- Progress tracking for `flowline provision`: separate plan needed; requires BAP admin REST API + new `HttpClient` + admin auth scope.
- Progress tracking for `flowline push` (import, operationtype 203): `AsyncOperationPoller` is designed to support this; wire-up is a follow-up.

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline/Commands/SyncCommand.cs` — PAC call wrapped in `Console.Status().FlowlineSpinner().StartAsync()` (lines 85–100); this is the block being replaced.
- `src/Flowline/Commands/CloneCommand.cs` — same pattern in `CloneSolutionFromDataverseAsync` (lines 179–195).
- `src/Flowline/Utils/CommandExtensions.cs` — `SetStatusWithExecutionTime` already parses `Processing asynchronous operation...` lines from PAC stdout and updates the spinner; suppress or adapt this when switching to a progress bar.
- `src/Flowline/Utils/SpinnerExtensions.cs` — `FlowlineSpinner()`, `FlowlineStatus` — the fallback path continues to use these.
- `src/Flowline.Core/Services/WebResourceExecutor.cs` (lines 31–50) — existing `console.Progress().StartAsync()` pattern with `ProgressTask.Increment(1)` to follow.
- `src/Flowline/Commands/FlowlineCommand.cs` — `ConnectToDataverseAsync` protected helper; already used by `PushCommand`.
- `src/Flowline.Core/Services/DataverseConnector.cs` — produces `IOrganizationServiceAsync2` from PAC's cached auth profiles; no additional auth setup required.
- `src/Flowline.Core/Services/OrganizationServiceExtensions.cs` — `RetrieveAllAsync` (paginated queries); single-record polling does not need pagination but follows the same `RetrieveMultipleAsync` pattern.

### Institutional Learnings

- `docs/solutions/logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md` — use `RetrieveAllAsync` for unbounded queries. The poller queries a single record by ID (not unbounded), so `RetrieveMultipleAsync` is acceptable for polling; the discovery query (find latest job by timestamp) should still use a `$top=1` to bound results.

### External References

- [Solution staging, with async import and export](https://learn.microsoft.com/power-platform/alm/solution-async) — confirms `ExportSolutionAsync` creates an `asyncoperation` record; the response contains `AsyncOperationId` and `ExportJobId`. Polling is documented against `statecode`/`statuscode`; `percentcomplete` field exists on the entity but its granularity for export operations is unverified — see Open Questions.
- [AsyncOperation entity reference](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/asyncoperation) — operationtype 202 = "Export Solution Async Operation"; 203 = "Import Solution Async Operation". Fields: `percentcomplete` (double), `statecode` (int), `statuscode` (int), `message` (string).

---

## Key Technical Decisions

- **Parallel polling, PAC drives completion:** PAC is not replaced. The Dataverse poller runs concurrently with the PAC process via a linked `CancellationTokenSource`. When PAC exits, the poller CTS is cancelled. This avoids re-implementing solution export/unpack logic and preserves PAC's error handling.
- **Job discovery by timestamp:** Record `DateTimeOffset.UtcNow` before starting the PAC process; query `asyncoperation` for the most recent record with `operationtype == 202` and `createdon >= startedAfter` within a 10-second window. This avoids depending on PAC stdout format to extract the job ID.
- **`IOrganizationServiceAsync2` for polling, no new HTTP client:** Consistent with the existing codebase. `DataverseConnector` already reads PAC's auth cache; no new auth wiring required.
- **Spectre `Progress()` replaces `Status()` during PAC call:** `WebResourceExecutor` already uses `console.Progress().StartAsync()` with a `ProgressTask`. The same pattern applies here. `Status()` and `Progress()` cannot be nested in Spectre, so the block must be replaced, not wrapped.
- **Graceful fallback:** Two distinct paths. (1) Connection failure: if `ConnectToDataverseAsync` throws before `Progress()` opens, the command takes the `Console.Status().FlowlineSpinner()` path — no progress bar is shown. (2) Job not found: if discovery finds no job within 10 seconds (connection already succeeded, `Progress()` already open), the poller exits silently and the bar stays at 0% until PAC completes, then advances to 100% on PAC exit. No warning to the user in either case.

---

## Open Questions

### Resolved During Planning

- **Which API does `pac solution sync/clone --async` use internally?** `ExportSolutionAsync` — confirmed by PAC's "Processing asynchronous operation..." output and entity reference operationtype 202.
- **Can `IOrganizationServiceAsync2` be reused without new auth setup?** Yes — `DataverseConnector` reads PAC's auth profile cache; `ConnectToDataverseAsync` in `FlowlineCommand` already wraps this.
- **Is provision trackable via `asyncoperation`?** No — `pac admin copy/create` uses the BAP admin API plane. The target environment doesn't exist during `admin create`. Deferred.

### Deferred to Implementation

- **Does `asyncoperation.percentcomplete` advance during solution EXPORT?** The official docs only show `statecode`/`statuscode` polling for both export and import; they do not showcase `percentcomplete` advancing. PAC's own "% of max time allotted" is computed from elapsed time, not from this field. **If `percentcomplete` remains 0 until completion (jumps 0→100), the feature reduces to status-only and violates R1.** Implementer must verify with a real export before wiring the progress bar: run a solution export and log `percentcomplete` on each poll. If it doesn't advance, surface the finding and pause; do not ship a progress bar that shows 0% until a sudden 100%.
- **Race condition on fast exports:** If the export completes before the first poll (< ~1 second), the discovery query may find no in-progress job. For small solutions this is acceptable — the command is fast enough that a spinner suffices. The graceful fallback handles this without error.

---

## High-Level Technical Design

> *This illustrates the intended approach and is directional guidance for review, not implementation specification. The implementing agent should treat it as context, not code to reproduce.*

```
flowline sync / clone
│
├── FindBestProfile(environmentUrl) ────────────────────────┐
│   null → skip progress entirely                           │
│   ConnectViaPacAsync(profile, environmentUrl, ct)         │
│   (DataverseConnector directly — no internal Status()     │
│    spinner; catch InvalidOperationException → null)       ▼
│   [Dataverse connection OK]              [null / failed]
│         │                                     │
│         ▼                                     ▼
│   console.Progress().StartAsync()    Console.Status().FlowlineSpinner()
│         │                                  (existing behavior, unchanged)
│         ├── PAC task (backgrounded) ─────────────────────────────────────┐
│         │                                                                 │
│         └── AsyncOperationPoller.PollAsync()                             │
│               │                                                          │
│               ├── Discovery: query asyncoperation                        │
│               │   where operationtype=202, createdon>=startedAfter       │
│               │   (retry for up to 10s; if not found → noop, cancel)    │
│               │                                                          │
│               └── Polling loop (every 3s):                              │
│                     retrieve asyncoperation by id                        │
│                     → onProgress(percentcomplete)                        │
│                     → update ProgressTask.Value                          │
│                     → exit when statecode==3 OR CT cancelled             │
│                                                                          │
│         PAC exits ──────────────────────────────────────────────────────┘
│         cancel poller CTS
│         await pollerTask (swallow OperationCanceledException)
│
└── command continues (pack, build, drift check, summary)
```

---

## Implementation Units

### U0. Verify `percentcomplete` advances during solution export (spike)

**Goal:** Confirm that `asyncoperation.percentcomplete` produces intermediate values (not 0→100 at completion only) before building U1–U3. If it does not advance, the feature delivers no value and U1–U3 should not be built.

**Requirements:** R1, R2 (gate)

**Dependencies:** None

**Files:**
- No production files. Spike only — disposable script or manual test.

**Approach:**
- Against a real Dataverse environment, trigger a solution export (e.g., via `pac solution export --async` or a direct `ExportSolutionAsync` call).
- On a separate thread, poll `asyncoperation` for the resulting record every 2–3 seconds: retrieve `percentcomplete`, `statecode`, `statuscode`, `message`.
- Log each poll result with a timestamp.
- Observe: does `percentcomplete` produce values between 0 and 100 during the export? Or does it stay at 0 and jump to 100 on completion?

**Exit criteria:**
- **If `percentcomplete` advances** (any intermediate value > 0 before statecode==3): proceed to U1.
- **If `percentcomplete` stays 0 until statecode==3 (jumps 0→100)**: pause. Surface the finding. Do not proceed to U1–U3 as written. The feature as designed delivers no value — bring the observation back to the plan.

**Verification:**
- At least one export observed with timestamped poll log.
- Exit criterion met before any U1 work begins.

---

### U1. `AsyncOperationPoller` service

**Goal:** New service that discovers and polls an `asyncoperation` record by timestamp and operation type, reporting progress via a callback.

**Requirements:** R1, R2, R3, R4, R5

**Dependencies:** U0 (must confirm `percentcomplete` advances before building)

**Files:**
- Create: `src/Flowline.Core/Services/AsyncOperationPoller.cs`
- Test: `tests/Flowline.Core.Tests/Services/AsyncOperationPollerTests.cs`

**Approach:**
- Constructor takes `IOrganizationServiceAsync2`.
- `PollAsync(DateTimeOffset startedAfter, int operationType, Action<double> onProgress, CancellationToken ct)` method.
- Discovery phase: query `asyncoperation` where `createdon >= startedAfter.AddSeconds(-5)` and `operationtype == operationType`, ordered by `createdon desc`, `top=1`. Subtract 5 seconds to absorb client–server clock skew. Retry every 1 second for up to 10 seconds. If not found within 10 seconds, return without error. Note: if concurrent exports are running in the same environment, this query may return a different job; this is accepted — progress tracking is best-effort and the fallback (bar at 0%) is safe.
- Polling phase: once job ID is found, retrieve `asyncoperation` by ID selecting `percentcomplete`, `statecode`, `statuscode`, `message`. Call `onProgress(percentcomplete)` on each successful poll. Poll every 3 seconds.
- Exit conditions: `statecode == 3` (completed) or `CancellationToken` cancelled.
- If `statuscode == 31` (failed) on completion, log message via `Verbose` but do not throw — PAC's own error handling is the authoritative failure path.
- Use `RetrieveMultipleAsync` for discovery (bounded with `$top=1`, not paginated). Use `service.RetrieveAsync("asyncoperation", id, columns)` for polling.

**Execution note:** Start with a test that verifies `PollAsync` calls `onProgress` at each poll interval, and a test that verifies early exit when `statecode==3`.

**Patterns to follow:**
- `src/Flowline.Core/Services/OrganizationServiceExtensions.cs` — `RetrieveMultipleAsync` + `QueryExpression` usage.
- `src/Flowline.Core/Services/WebResourceExecutor.cs` — `SemaphoreSlim` and `Task.WhenAll` patterns (not needed here, but shows how services are structured).

**Test scenarios:**
- Happy path: `onProgress` called with advancing `percentcomplete` values until `statecode==3`, then method returns.
- Happy path: discovery retries until job appears (first 2 queries return empty, 3rd returns the job).
- Edge case: no job found within 10 seconds — method returns without calling `onProgress` and without throwing.
- Edge case: `percentcomplete` stays at 0.0 throughout — `onProgress(0)` called on each poll; method exits cleanly on CT cancel.
- Error path: `statuscode == 31` (failed) on statecode==3 — method returns without throwing; `message` is accessible for caller logging.
- Error path: `CancellationToken` cancelled mid-poll — method exits promptly via `OperationCanceledException` (or equivalent clean exit).

**Verification:**
- All tests pass.
- Service can be instantiated with a mocked `IOrganizationServiceAsync2` in tests without requiring a live Dataverse connection.

---

### U2. Progress bar in `SyncCommand`

**Goal:** Replace the spinner for the PAC sync call with a Spectre progress bar driven by `AsyncOperationPoller`.

**Requirements:** R1, R3, R5

**Dependencies:** U0, U1

**Files:**
- Modify: `src/Flowline/Commands/SyncCommand.cs`
- Modify: `src/Flowline/Utils/CommandExtensions.cs` (suppress `SetStatusWithExecutionTime` when `StatusContext` is null — it already handles `ctx is null` gracefully, so likely no change needed; verify)

**Approach:**
- Add `DataverseConnector` to `SyncCommand` constructor via DI, alongside existing `IAnsiConsole` and `FlowlineRuntimeOptions`.
- Before any `Progress()` block opens, call `DataverseConnector.FindBestProfile(environmentUrl)`. If null, skip progress tracking entirely — fall back to existing `Console.Status().FlowlineSpinner().StartAsync()` block unchanged. No exception handling needed for this step.
- If a profile is found, call `DataverseConnector.ConnectViaPacAsync(profile, environmentUrl, ct)` directly (not via `FlowlineCommand.ConnectToDataverseAsync`, which adds a `Status()` spinner internally). Wrap only this call in `try/catch (InvalidOperationException)` — this is an intentional convention deviation: progress tracking is optional; MSAL auth failures (cache unreadable, session expired) must not fail the command. Catch, ignore, fall back to spinner.
- If `orgService == null` (either path above): fall back to existing `Console.Status().FlowlineSpinner().StartAsync()` block unchanged.
- If `orgService != null`:
  - Record `startedAfter = DateTimeOffset.UtcNow`.
  - Switch to `Console.Progress().StartAsync(async ctx => { ... })`.
  - Inside: add a single `ProgressTask` with description `$"Syncing [bold]{solutionName}[/]"` and `maxValue: 100`.
  - Start PAC task (same `Cli.Wrap(...)...ExecuteAsync(ct).Task` call, not awaited immediately).
  - Create linked `CancellationTokenSource pollerCts`.
  - Start `poller.PollAsync(startedAfter, 202, p => progressTask.Value = p, pollerCts.Token)` — not awaited immediately.
  - `await pacTask` (blocks until PAC exits).
  - `pollerCts.Cancel(); await pollerTask.IgnoreCancelledAsync()`.
  - Set `progressTask.Value = 100` after PAC completes to ensure the bar reaches 100% even if the last poll didn't.
- PAC error handling (`!result.IsSuccess`) is unchanged.
- `SetStatusWithExecutionTime` receives `null` as `ctx` when inside `Progress()` — verify it handles this gracefully (it already checks `ctx is null` at line 54 of `CommandExtensions.cs`).

**Patterns to follow:**
- `src/Flowline.Core/Services/WebResourceExecutor.cs` lines 31–50 — `console.Progress().StartAsync()` with `ProgressTask`.
- `src/Flowline.Core/Services/DataverseConnector.cs` — `FindBestProfile` + `ConnectViaPacAsync` (use directly, not via `FlowlineCommand.ConnectToDataverseAsync`).

**Test scenarios:**
- Integration: when `orgService` is null (connection fails), command completes with spinner and correct exit code.
- Integration: progress bar reaches 100% after PAC exits regardless of what the poller reported last.
- Integration: command exit code and `Console.Ok(...)` output are unchanged for both happy and fallback paths.

**Verification:**
- `flowline sync` on a real environment shows a progress bar with advancing percentage (if `percentcomplete` advances — see deferred question).
- If `percentcomplete` does not advance: surface the finding before proceeding; do not ship.
- `flowline sync` on a machine without Dataverse access (bad auth) falls back to spinner without error.

---

### U3. Progress bar in `CloneCommand`

**Goal:** Apply the same progress bar pattern to the `CloneSolutionFromDataverseAsync` method.

**Requirements:** R2, R3, R5

**Dependencies:** U0, U1, U2 (follow the same pattern established in U2)

**Files:**
- Modify: `src/Flowline/Commands/CloneCommand.cs`

**Approach:**
- Same pattern as U2: try connect, record `startedAfter`, replace spinner with `Progress()` block for the PAC call in `CloneSolutionFromDataverseAsync`.
- `CloneCommand` already receives `IAnsiConsole` and `FlowlineRuntimeOptions` in its constructor — add `DataverseConnector` the same way `SyncCommand` receives it (see U2 for the constructor pattern to follow).
- The rest of `CloneSolutionFromDataverseAsync` (directory moves, rename cdsproj) is unchanged.

**Patterns to follow:**
- U2 pattern exactly — no divergence.

**Test scenarios:**
- Integration: same three scenarios as U2 (fallback, 100% on completion, exit code unchanged).
- Edge case: clone is called when the solution folder already exists — `CloneSolutionFromDataverseAsync` returns early before any PAC call; progress bar path is never reached. Verify this early-return path is unaffected.

**Verification:**
- `flowline clone` on a real environment shows the same progress bar.
- Existing clone behavior (folder creation, cdsproj rename) is unchanged.

---

## System-Wide Impact

- **Interaction graph:** `SyncCommand` and `CloneCommand` both inherit from `FlowlineCommand`. `ConnectToDataverseAsync` is a protected helper there — adding `DataverseConnector` to these two commands follows the same DI pattern already used by `PushCommand`. No shared state affected.
- **Error propagation:** `AsyncOperationPoller` does not throw on job-not-found or on poll errors; all exceptions from polling are swallowed internally or surfaced as clean exits. PAC's own error handling (`!result.IsSuccess`) remains the authoritative failure path.
- **State lifecycle risks:** The poller reads Dataverse state but makes no writes. No transaction or cleanup concerns.
- **API surface parity:** No new public API or CLI flags added. `AsyncOperationPoller` is internal to `Flowline.Core`.
- **Integration coverage:** The progress bar display requires a live Dataverse connection; integration tests on CI should remain offline and test the fallback path only. Real-environment verification is manual.
- **Unchanged invariants:** All post-PAC steps in `SyncCommand` (pack, build, drift check, summary) and `CloneCommand` (directory reorganization) are untouched. Exit codes and error messages are unchanged. `ProvisionCommand` is not modified.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| `percentcomplete` does not advance for solution export — feature delivers no value | Verify empirically in U2 before shipping; if it doesn't advance, pause and report |
| Timestamp-based discovery misses the job (very fast export, or clock skew) | Graceful fallback to spinner; no error surfaced to user |
| Connecting to Dataverse adds latency before PAC starts | Connection runs before the `Progress()` block opens; a failed connect takes the spinner fallback path immediately |
| Poller is not cancelled if PAC exits with error and throws | `pollerCts.Cancel()` runs after `await pacTask` in all branches; wrap in try/finally if needed |
| `Console.Progress()` and `Console.Status()` cannot be nested in Spectre | Plan explicitly replaces (not wraps) the spinner block; never nested |

---

## Sources & References

- External: [Solution staging, async import and export](https://learn.microsoft.com/power-platform/alm/solution-async)
- External: [AsyncOperation entity reference — writable columns](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/asyncoperation#writable-columns-attributes)
- Related code: `src/Flowline.Core/Services/WebResourceExecutor.cs` — `Progress()` pattern
- Related code: `src/Flowline/Commands/FlowlineCommand.cs` — `ConnectToDataverseAsync`
- Institutional: `docs/solutions/logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md`
