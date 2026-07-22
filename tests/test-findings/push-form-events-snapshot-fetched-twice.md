# `push` prints "Lookup form events..." twice and appears to re-fetch the same snapshot

- **Status**: not fixed — low severity, not investigated deeply enough to be confident about the
  right fix.
- **Severity**: low (cosmetic + possible minor inefficiency, not a correctness issue observed so far).
- **Found**: 2026-07-21, during routine `push`/`push --scope formevents` testing.

## Repro

Run `flowline push` (default scope, or `--scope formevents` alone) against a project with form event
annotations. The console prints `Lookup form events...` **twice** in the same run, once during the
web-resources/orphan-cleanup phase and once during the registration phase.

## Likely cause (not fully traced)

`PushCommand.cs` calls into `FormEventService` twice per push:
```
formEventService.CleanupOrphanedAsync(...)   // PushCommand.cs:228
formEventService.RegisterAsync(...)          // PushCommand.cs:241
```
Both appear to independently call the same internal snapshot lookup
(`FormEventService.cs:71`, `console.Status().FlowlineSpinner().StartAsync("Lookup form events...", ...)`),
so the same Dataverse form-event snapshot is likely fetched twice in one push instead of once and
reused across both phases.

## Why not fixed

Didn't trace far enough to confirm whether `CleanupOrphanedAsync` and `RegisterAsync` could safely
share one snapshot (e.g. if cleanup mutates state that registration then needs to re-read fresh), so
a fix risks introducing a staleness bug for a minor performance/cosmetic win. Worth a proper look, not
a blind "cache it" patch.
