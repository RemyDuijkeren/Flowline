---
title: "feat: Pre-flight managed/unmanaged guard in DeployCommand"
type: feat
status: completed
date: 2026-06-07
origin: docs/brainstorms/2026-06-07-deploy-managed-unmanaged-guard-requirements.md
---

# feat: Pre-flight managed/unmanaged guard in DeployCommand

## Summary

Inserts a single pre-flight call to `FlowlineValidator.GetSolutionInfoAsync` in `DeployCommand.ExecuteFlowlineAsync` — after the target env is resolved, before the pack step. If the solution is already installed with the opposite type, Flowline blocks immediately with a clear, non-bypassable error. Null result (first deploy) is allowed. Cache covers repeated invocations at no API cost.

---

## Problem Frame

`DeployCommand` packs and imports without checking whether the solution already exists in the target and what type it is. Two failure modes result:

- **Managed over unmanaged**: pac fails with "already installed as unmanaged" — environment left in an undefined state. Irreversible without portal intervention.
- **Unmanaged over managed**: pac rejects the import anyway, but only after the pack step, with a cryptic error.

Both waste time. Case 1 is also destructive.

---

## Requirements

- R1. Before packing, fetch `SolutionInfo` from the target env for the solution being deployed.
- R2. If solution is unmanaged in target and deploy is `--managed`: hard block, non-bypassable, no pack.
- R3. If solution is managed in target and deploy is unmanaged: hard block, non-bypassable, no pack.
- R4. If `SolutionInfo` is null (solution not yet in target): allow — first deploy proceeds normally.
- R5. If types match (managed→managed or unmanaged→unmanaged): allow — no guard needed.
- R6. No `--force` or any other bypass for R2 or R3.

---

## Scope Boundaries

- No changes to any other command.
- No `--force` bypass.
- No check for unmanaged active layers on top of managed (different problem, different fix).
- No new CLI flags.

---

## Context & Research

### Relevant Code

- `src/Flowline/Commands/DeployCommand.cs` — insertion point: after `targetEnv` display (line 66), before `slnFolder` path construction (line 68). Both `targetEnv` and `sln` are resolved by this point.
- `src/Flowline/Validation/FlowlineValidator.cs:107` — `GetSolutionInfoAsync(environmentUrl, solutionName, includeManaged, settings, ct)` returns `SolutionInfo?`. Backed by a 4-hour TTL cache keyed on `(environmentUrl, solutionName, includeManaged)`.
- `src/Flowline/Commands/ProvisionCommand.cs:169` — `FindProblematicSolutions` implements the same managed/unmanaged cross-check pattern. Past learning: guard is non-bypassable even with `--force`.
- `src/Flowline/Utils/PacUtils.cs:456` — `SolutionInfo.IsManaged` (bool).

### Institutional Learnings

- `docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md` — established that the managed/unmanaged guard is non-bypassable even with `--force`; data loss is permanent.

---

## Key Technical Decisions

- **No extraction**: The guard is two `if` blocks on a boolean field — no pure function needed. Unlike `FindProblematicSolutions` in `ProvisionCommand`, there's no multi-item comparison logic worth isolating.
- **`includeManaged: true`**: Pass `true` so the cache entry covers both managed and unmanaged solutions. `false` would create a distinct cache key but use the same underlying data — use `true` as the complete-information default.
- **Spinner wraps the call**: Wrap in `Console.Status().FlowlineSpinner()` for consistency with other validator calls in the command. Cache hit makes this nearly instant.
- **Insertion before drift check**: The type guard blocks before any local work (drift check, pack) — fail-fast is the goal. Drift check and cdsproj check remain in their current positions after the guard.
- **Non-bypassable structurally**: No conditional on `settings.Force`. The guard always runs regardless of any flag.

---

## High-Level Technical Design

> Directional guidance, not implementation specification.

```
ExecuteFlowlineAsync (DeployCommand)
│
├── [existing] resolve targetUrl, sln
├── [existing] fetch + validate targetEnv
├── [existing] print target env display
│
├── [NEW] GetSolutionInfoAsync(targetUrl, sln.Name, includeManaged: true, settings, ct)
│   ├── null  → allow (first deploy, case 3)
│   ├── managed in target, deploy is --managed  → allow (case 4)
│   ├── unmanaged in target, deploy is unmanaged → allow (case 4)
│   ├── unmanaged in target, deploy is --managed → Console.Error + return 1  (case 1, non-bypassable)
│   └── managed in target, deploy is unmanaged  → Console.Error + return 1  (case 2, non-bypassable)
│
├── [existing] check cdsproj exists
├── [existing] drift check (skip if --force)
├── [existing] pack
└── [existing] import + done
```

---

## Implementation Units

### U1. Pre-flight type guard in DeployCommand

**Goal:** Block managed-over-unmanaged and unmanaged-over-managed before any pack work starts.

**Requirements:** R1, R2, R3, R4, R5, R6

**Dependencies:** None — `FlowlineValidator.Default.GetSolutionInfoAsync` already exists.

**Files:**
- Modify: `src/Flowline/Commands/DeployCommand.cs`

**Approach:**

After the target env display line and before `slnFolder` path construction:

1. Wrap `FlowlineValidator.Default.GetSolutionInfoAsync(targetUrl, sln.Name, includeManaged: true, settings, cancellationToken)` in a spinner (`"Checking [bold]{sln.Name}[/]..."`).
2. If result is null → continue (first deploy allowed).
3. If `result.IsManaged == false && settings.Managed == true` → `Console.Error(...)`, return `ExitCode.ValidationFailed`.
4. If `result.IsManaged == true && settings.Managed == false` → `Console.Error(...)`, return `ExitCode.ValidationFailed`.
5. Otherwise → continue.

**Error messages** (tone: direct, actionable, no filler):

- Case 1: `"'{sln.Name}' is unmanaged in {targetEnv.DisplayName} — importing managed is irreversible. Remove the unmanaged solution first, or deploy unmanaged."`
- Case 2: `"'{sln.Name}' is managed in {targetEnv.DisplayName} — can't import unmanaged over managed. Deploy managed instead."`

**Test scenarios** (manual verification — no PAC CLI isolation path):

- `flowline deploy <target> --managed` when target has solution as unmanaged → exits before pack with case-1 error. No pack spinner appears.
- `flowline deploy <target>` (unmanaged) when target has solution as managed → exits before pack with case-2 error. No pack spinner appears.
- `flowline deploy <target>` when solution is not yet in target → proceeds normally past the guard.
- `flowline deploy <target> --managed` when target already has solution as managed → proceeds normally past the guard.
- `flowline deploy <target>` when target already has solution as unmanaged → proceeds normally past the guard.
- `flowline deploy <target> --managed --force` when target has solution as unmanaged → still blocked (R6 — `--force` has no effect on this guard).

**Verification:**

Run against a known target environment where the solution state is controlled. Confirm pack spinner never appears in the two blocked cases. Confirm the guard is absent from the spinner log in the two allowed first-deploy / type-match cases.

---

## System-Wide Impact

- Affects only `DeployCommand.ExecuteFlowlineAsync`. No other commands, middleware, or shared state.
- Adds one cached API call per deploy invocation. Cache TTL is 4 hours — repeated deploys within the same session pay no API cost.
- `ProvisionCommand` unchanged — it already has its own unmanaged guard.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| `GetSolutionInfoAsync` returns null for a solution that exists (PAC CLI auth/connectivity failure) | Null is treated as first deploy — guard allows. This is the conservative direction for first deploy; the subsequent import will fail with a clear PAC error if auth is truly broken. |

---

## Sources & References

- **Origin document:** [docs/brainstorms/2026-06-07-deploy-managed-unmanaged-guard-requirements.md](docs/brainstorms/2026-06-07-deploy-managed-unmanaged-guard-requirements.md)
- [docs/ideation/2026-06-07-deploy-command-ideation.md](docs/ideation/2026-06-07-deploy-command-ideation.md) — idea #1
- `src/Flowline/Commands/DeployCommand.cs`
- `src/Flowline/Validation/FlowlineValidator.cs:107` (`GetSolutionInfoAsync`)
- `src/Flowline/Commands/ProvisionCommand.cs:169` (`FindProblematicSolutions` — guard pattern reference)
- `docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md`
