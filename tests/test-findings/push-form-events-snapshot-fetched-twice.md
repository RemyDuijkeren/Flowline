# `push` prints "Lookup form events..." twice and appears to re-fetch the same snapshot

- **Status**: fixed (cosmetic part) — 2026-07-22. The double fetch itself is confirmed **by design**,
  not a bug; see investigation below.
- **Severity**: low (cosmetic; the "possible minor inefficiency" is real but small and intentional
  — see "Why the double fetch is correct").
- **Found**: 2026-07-21, during routine `push`/`push --scope formevents` testing.

## Repro

Run `flowline push` (default scope, or `--scope formevents` alone) against a project with form event
annotations. The console printed `Lookup form events...` **twice** in the same run, once during the
web-resources/orphan-cleanup phase and once during the registration phase, with no visible
distinction between them.

## Cause

`PushCommand.cs` calls into `FormEventService` twice per push:
```
formEventService.CleanupOrphanedAsync(...)   // PushCommand.cs:228
formEventService.RegisterAsync(...)          // PushCommand.cs:241
```
Both independently call the same internal snapshot lookup
(`FormEventService.cs:70`, `console.Status().FlowlineSpinner().StartAsync(...)`). The fetch really is
run twice — but that's intentional, not a duplicate.

## Why the double fetch is correct

Traced `FormEventService.SyncAsync`, `FormEventReader.LoadSnapshotAsync`, and
`FormEventExecutor.ExecuteAsync`/`BuildFormXml`/`ExecuteByEntityAsync`. `CleanupOrphanedAsync` runs
before web resources are pushed (removes stale/orphaned handlers so a pending web-resource delete
never trips Dataverse's "referenced by N other components" fault); `RegisterAsync` runs after (adds
handlers, which can only safely reference libraries that now exist). Cleanup can write a new
`formxml` to `systemform` in that gap (`FormEventExecutor.cs:289-306`, via `UpdateAsync`/
`ExecuteAsync` with `ConcurrencyBehavior.IfRowVersionMatches`). If registration reused cleanup's
pre-write snapshot, it would plan against stale `FormXml`/`RowVersion` — risking either resurrecting
a handler cleanup just removed, or a concurrency fault on write. So each pass must re-read fresh;
sharing one snapshot across both calls is unsafe in general.

## Fix applied

The double fetch stays. What was actually a defect: both fetches showed the *identical* spinner
text, making an intentional two-phase design read as an accidental duplicate. This project already
has an established pattern for exactly this — `cleanupOnly` already gates every other per-phase
output difference (`suppressWarnings` for reader/planner warnings, the "already up to date" line,
the dry-run preview, and `FormEventExecutor.cs:89`'s own progress label
`cleanupOnly ? "Cleaning forms" : "Updating forms"`). The spinner text was the one output never
wired into that pattern. `FormEventService.cs:70` now uses
`cleanupOnly ? "Checking form events..." : "Registering form events..."`, mirroring the executor's
existing style. No behavior change, no caching, no staleness risk.

## Deferred: fetch-reuse optimization

In the common case, `CleanupOrphanedAsync` queues plan entries (removals-only) but ends up writing
nothing to Dataverse at all (`FormEventExecutor.cs:71-72`'s early return, or a per-form
`handlerChanges.Count > 0 || libraryChanges.Count > 0` check that's `false`). In that sub-case the
second fetch is pure waste and could safely be skipped. Not built: detecting it requires threading a
new "did cleanup actually write" signal out of `ExecuteByEntityAsync`'s inner per-form loop, up
through `ExecuteAsync` and `SyncAsync` to `PushCommand` — disproportionate for a low-severity,
mostly-cosmetic finding. If revisited, the safety precondition is: only skip the re-fetch when
cleanup's `SyncAsync` call performed zero actual `systemform` writes.
