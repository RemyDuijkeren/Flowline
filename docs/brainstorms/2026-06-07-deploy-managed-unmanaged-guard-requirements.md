---
date: 2026-06-07
topic: deploy-managed-unmanaged-guard
status: ready-for-planning
---

# Requirements: Pre-flight Managed/Unmanaged Guard in DeployCommand

## Problem

`DeployCommand` packs the solution and attempts `pac solution import` without checking whether the solution already exists in the target environment and what type it is. This causes two failure modes:

1. **Managed over unmanaged** — pac fails with "The solution is already installed as an unmanaged solution" and the environment is left in an undefined state. This is irreversible without manual portal intervention.
2. **Unmanaged over managed** — pac rejects the import anyway, but only after Flowline has already spent time packing. The error is buried in pac output rather than surfaced clearly.

Both cases waste time and produce cryptic errors. Case 1 is also irreversible.

## Proposed Behaviour

Before packing, Flowline fetches the solution's current install state from the target environment. If a type mismatch is detected, Flowline blocks immediately with a clear error message — no pack step, no import attempt.

### Case 1: Managed import → solution is unmanaged in target

**Block. Non-bypassable. No flag overrides this.**

Error message (illustrative):
```
[name] is installed as unmanaged in [env]. Importing managed over unmanaged is irreversible.
Remove the unmanaged solution first, or deploy unmanaged instead.
```

This mirrors the pattern established in `ProvisionCommand.FindProblematicSolutions` and is consistent with the documented past learning: this guard is non-bypassable even with `--force`.

### Case 2: Unmanaged import → solution is managed in target

**Block. Non-bypassable.**

Rationale: pac would reject this at import time regardless. Blocking pre-pack gives the developer a clear, early message instead of a cryptic pac error.

Error message (illustrative):
```
[name] is installed as managed in [env]. Managed solutions can't be overwritten with unmanaged.
Deploy managed instead.
```

### Case 3: Solution not yet in target (first deploy)

**Allow.** A null result from `GetSolutionInfoAsync` means the solution isn't installed — proceed normally.

### Case 4: Types match (managed→managed or unmanaged→unmanaged)

**Allow.** No guard needed.

## Scope

**In scope:**
- Pre-flight type check in `DeployCommand` before the pack step
- Both mismatch directions (managed→unmanaged and unmanaged→managed)
- Clear, actionable error messages distinguishing the two cases
- Reuse of `FlowlineValidator.GetSolutionInfoAsync` with its existing 4-hour cache

**Out of scope:**
- Detecting unmanaged active layers on top of a managed solution (different problem, different fix)
- Any change to `ProvisionCommand` (already has its own guard)
- `--force` or any bypass path for either blocked case

## Success Criteria

- `flowline deploy <target> --managed` when target has the solution as unmanaged → exits before packing with a clear, non-bypassable error
- `flowline deploy <target>` (unmanaged) when target has the solution as managed → exits before packing with a clear, non-bypassable error
- `flowline deploy <target>` when solution is not yet in target → proceeds normally (first deploy is allowed)
- `flowline deploy <target>` when type matches → proceeds normally
- No new API calls beyond what `FlowlineValidator.GetSolutionInfoAsync` already provides (cache handles repeated invocations)

## Key References

- `src/Flowline/Commands/DeployCommand.cs` — current prototype; guard inserts after `targetEnv` is resolved, before the pack step
- `src/Flowline/Validation/FlowlineValidator.cs:107` — `GetSolutionInfoAsync` already fetches `SolutionInfo.IsManaged`
- `src/Flowline/Commands/ProvisionCommand.cs:169` — `FindProblematicSolutions` implements the same check pattern for provisioning
- Past learning: `docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md`
