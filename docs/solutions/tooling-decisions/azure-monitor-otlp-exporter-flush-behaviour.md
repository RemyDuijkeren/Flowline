---
title: "Azure Monitor OpenTelemetry Exporter: what ForceFlush actually bounds"
date: 2026-09-20
category: docs/solutions/tooling-decisions/
module: "Flowline.Diagnostics"
problem_type: tooling_decision
component: tooling
applies_when:
  - "Exporting Activities to Application Insights from a short-lived CLI process"
  - "A command must not pay an unbounded cost when the ingestion endpoint is blocked or proxied"
  - "Rewriting activity tag values on the way out of the process"
tags:
  - opentelemetry
  - application-insights
  - azure-monitor-exporter
  - forceflush
  - telemetry
  - cli-observability
---

## Context

Wave 4 of Flowline's observability work exports the CLI's Activities to Application Insights.
The plan (`docs/plans/2026-09-20-1047-feat-telemetry-app-insights-plan.md`, U1) assumed
`ForceFlush(timeout)` on the provider is the synchronous, bounded call that decides how long a
blocked endpoint can delay a command (assumption A3, decision KTD8). U1 measured it.

Measured with `OpenTelemetry` 1.19.0 and `Azure.Monitor.OpenTelemetry.Exporter` 1.9.0 on
net10.0, in a standalone console process so no other `ActivityListener` could confound the result.

## Findings

**1. The restore graph is clean.** Both packages restore and build alongside
`Microsoft.PowerPlatform.Dataverse.Client` 1.2.27 and the pinned
`System.Security.Cryptography.Xml` transitive with zero warnings. Assumption A1 holds.

**2. A `BaseProcessor<Activity>.OnEnd` implementation can rewrite tag values and the exporter sends
the rewritten ones.** Calling `data.SetTag(key, newValue)` inside `OnEnd` is observed by a
downstream in-memory exporter. Assumption A2 holds; this is the shape `ScrubbingProcessor` uses.

**3. `ForceFlush` does not wait for the HTTP transmission. `Dispose` does.** This is the finding
that matters.

| Endpoint | `ForceFlush(3000)` | `Dispose()` |
|---|---|---|
| Local socket that accepts and never answers | returns `true` in **1 ms** | **~4.0 s** |
| Blackholed route (198.51.100.1) | returns `true` in **1 ms** | **~4.0 s** |
| Real ingestion host, bogus instrumentation key | returns `true` in ~1.0 s | **~4.0 s** |
| Real ingestion host through a refusing proxy | returns `true` in 1 ms | **82 ms** |

`ForceFlush` returning `true` says the batch processor handed the batch to the exporter, not that
anything left the machine. The transmission is settled during `Dispose`, and the timeout argument
passed to `ForceFlush` does not bound it. So "ForceFlush(3s) then dispose" bounds nothing: the
worst case observed is roughly four seconds regardless of the argument, and that cost is paid on a
*reachable* endpoint too, not only a blocked one.

**The working shape is to bound the whole teardown ourselves:**

```csharp
var teardown = Task.Run(() => { provider.ForceFlush(bound); provider.Dispose(); });
teardown.Wait(bound);   // abandons the send on a background thread if it overruns
```

`Task.Run` uses a thread-pool thread, which is a background thread, so an abandoned send does not
hold process exit open. Measured: the wait returns at exactly the bound and the abandoned work
finishes ~1 s later on its own.

**4. The standard proxy environment variables are honoured with no configuration.** With
`HTTPS_PROXY`/`HTTP_PROXY` pointing at a refusing port, teardown completed in 82 ms instead of the
~4 s blackhole path, which is only possible if the request went to the proxy. Assumption A4 holds,
and a refusing proxy degrades fast rather than costing the full bound (R12).

**5. A 64 KB string tag survives the client-side pipeline untruncated.** No limit is imposed by the
SDK or the processor. Application Insights applies its own limit on a custom dimension value
server-side; that is not observable from the client and is verified against the live resource in U6.

**6. Three exporter defaults contradict the plan and cost a short-lived process real time.**
`TracesPerSecond` defaults to 5, which is rate-limited sampling: spans arrived stamped
`microsoft.sample_rate: 25`, while the plan's scope boundaries rule sampling out. `EnableLiveMetrics`,
`EnableStandardMetrics` and `EnablePerformanceCounters` all default on and are metric signals this
plan does not send; performance counters were observed arriving. Separately, the exporter's statsbeat
accounted for roughly two of the four seconds a teardown took, and is disabled through
`APPLICATIONINSIGHTS_STATSBEAT_DISABLED`, set in-process.

**7. How long the bound actually has to be.** With statsbeat and the metric signals off, teardown still
runs about three seconds regardless of the endpoint, so the bound is what every command pays, not just
a blocked one. Measured against the live resource, sending one span per process and then exiting:

| Bound | Span arrived in App Insights |
|---|---|
| 250 ms | no |
| 700 ms | yes (2/2) |
| 1000 ms | yes (2/2) |
| 1200 ms | yes (2/2) |
| 1500 ms | yes (2/2) |

The abandoned teardown thread dies at process exit, so a bound below what the transmission needs loses
the span silently: it is dropped in memory rather than spooled.

**A second signal changes the answer.** Once the Serilog stream is exported as well, the two pipelines
tear down side by side and both have to finish inside the same bound. Measured the same way, at
1500 ms six identical runs delivered five, and a run whose send was abandoned after the server had
already accepted it was retried from the offline store on a later run and arrived twice. Flowline uses
3000 ms for that reason. Sequencing the two teardowns instead of running them side by side is worse
still: the spans consume the whole bound and no log record leaves at all.

**8b. A send abandoned at the bound is spooled after all, and forwarded by a later run.** Measured
across six identical runs at both 1500 ms and 3000 ms: five delivered immediately and the sixth was
written to the exporter's offline store and re-sent later. So the bound decides how much arrives
*promptly*, not how much arrives. Two consequences follow. A blob whose send was accepted by the
server but abandoned before the response was read is retried, so a line can arrive twice — observed
as 30 duplicated lines in one batch. And the store is shared per instrumentation key under the
system temp directory by default, so a backlog that can never succeed (for example blobs written by
a spike using a bogus key) is retried by every later run and competes with that run's own export.
Flowline sets `StorageDirectory` under its own storage root for that reason.

**8. Offline storage spools only on a transmission that fails, not on one that is abandoned.** With a
short `Retry.NetworkTimeout` the send fails fast, lands in `StorageDirectory`, and a later run drains
it — verified. That is the shape a "never wait, always forward later" design would need; it is not what
a short bound gives you.

## Consequence for the plan

KTD8's three call sites (after `RunAsync`, `ProcessExit`, SIGTERM) and its idempotency requirement
stand. Its mechanism does not: the idempotent flush runs `ForceFlush` **and** `Dispose` inside one
bounded background wait rather than calling `ForceFlush(3s)` and then disposing inline. Without
that, every command pays up to four seconds on exit, which is exactly what R7 forbids.
