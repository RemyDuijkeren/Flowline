---
title: "feat: Add unmanaged solution guard to ProvisionCommand"
type: feat
status: active
date: 2026-05-18
origin: docs/brainstorms/provision-unmanaged-solution-guard-requirements.md
---

# feat: Add unmanaged solution guard to ProvisionCommand

## Summary

Extends `ProvisionCommand` with a pre-copy safety check: after `--allow-overwrite` passes, two parallel `PacUtils.GetSolutionsAsync` calls (prod + target) feed a pure comparison function that blocks the copy if any unmanaged target solution is managed or absent in prod. No system solution filtering needed — Microsoft solutions are always managed (excluded by `!IsManaged`), and the Default Solution is always unmanaged in both envs (passes the comparison cleanly).

---

## Problem Frame

`pac admin copy` overwrites the target environment entirely. Dev and test environments hold unmanaged solutions under active development; if prod carries managed versions of those solutions, copying prod over dev/test permanently destroys the unmanaged work. The current `--allow-overwrite` flag was designed to skip the "environment already exists" warning and conflates two distinct risk levels. See origin document for full context.

---

## Requirements

- R1. Before executing the environment copy, query both prod and target for their solution lists.
- R2. For each unmanaged solution in target, check whether the same solution (matched by unique name, case-insensitive) exists as unmanaged in prod.
- R3. If any unmanaged target solution is managed in prod — block, exit non-zero, list affected solutions.
- R4. If any unmanaged target solution is absent from prod entirely — block with the same error format.
- R5. `--allow-overwrite` does not bypass the block.
- R6. If every unmanaged target solution also exists as unmanaged in prod — proceed normally.
- R7. New (non-existent) target environments skip the check — automatically satisfied since a freshly created env has no solutions.

**Origin acceptance examples:** AE1 (covers R3), AE2 (covers R4), AE3 (covers R6), AE4 (covers R5), AE5 (covers R7)

---

## Scope Boundaries

- No `--force` or escape hatch of any kind.
- No system solution filtering — not needed.
- No changes to `sync`, `deploy`, `clone`, or any other command.
- No changes to `--allow-overwrite` semantics for the "environment already exists" path.

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline/Commands/ProvisionCommand.cs` — insertion point is after line 113 (`if (!settings.AllowOverwrite)` early-return), before line 118 (copy type determination).
- `src/Flowline/Utils/PacUtils.cs` — `GetSolutionsAsync(environmentUrl, verbose, ct)` returns `List<SolutionInfo>`; `SolutionInfo` exposes `SolutionUniqueName` (`string?`), `IsManaged`, `PublisherUniqueName`. `[assembly: InternalsVisibleTo("Flowline.Tests")]` is declared here.
- `src/Flowline/Commands/CloneCommand.cs` — `FindUnmanagedSourceAsync` shows the pattern for filtering `!s.IsManaged` across environments.
- `tests/Flowline.Tests/PacUtilsTests.cs` — xUnit + FluentAssertions; test structure to mirror.

### Institutional Learnings

- No `docs/solutions/` entries directly relevant to this change.

---

## Key Technical Decisions

- **Pure comparison function, not inline logic**: Extract `FindProblematicSolutions(target, prod)` as an `internal static` method so it's unit-testable without PAC CLI. The PAC CLI calls stay in `ExecuteFlowlineAsync`. This is the minimum split needed for coverage.
- **Parallel solution queries**: Prod and target are independent — query both simultaneously to avoid adding sequential latency before what is already a slow `pac admin copy` operation.
- **No system solution filter**: Microsoft solutions are always managed (excluded by `!IsManaged`). Default Solution is always unmanaged in both prod and target, so it passes the comparison cleanly — no special handling.
- **Null-safe unique name matching**: `SolutionUniqueName` is `string?`. Treat null as non-matching — a solution with no unique name cannot be compared and is excluded from the check (not treated as blocking).
- **Error format**: List each problematic solution with its unique name and reason (managed in prod / absent from prod), using `Console.Error` matching existing patterns in the command.
- **Check placement**: After `--allow-overwrite` early-return (copy never runs without that flag), before copy-type determination. R7 is automatically satisfied — freshly created envs have zero solutions.

---

## Open Questions

### Resolved During Planning

- **Is system solution filtering needed?** No — Microsoft solutions are always managed (`!IsManaged` excludes them); Default Solution is unmanaged in both envs and passes the comparison cleanly.
- **Is R7 handled explicitly?** No — a freshly created env has no solutions, so the check always passes. No special new-env tracking needed.

### Deferred to Implementation

- **Exact `SolutionUniqueName` null frequency**: Verify whether PAC CLI ever returns null unique names in practice. The null-safe fallback (exclude from check) is conservative and safe regardless.

---

## High-Level Technical Design

> *This illustrates the intended approach and is directional guidance for review, not implementation specification. The implementing agent should treat it as context, not code to reproduce.*

```
ExecuteFlowlineAsync (ProvisionCommand)
│
├── [existing] validate prod env, build target URL, check/create target env
├── [existing] check target.Type != "Production"
├── [existing] if !AllowOverwrite → warn + return 0
│
├── [NEW] query solutions in parallel
│   ├── PacUtils.GetSolutionsAsync(prod URL)  ─┐ parallel
│   └── PacUtils.GetSolutionsAsync(target URL) ─┘
│
├── [NEW] FindProblematicSolutions(targetSolutions, prodSolutions)
│   ├── filter target: !IsManaged (null uniqueName → skip)
│   └── for each → check prod has same uniqueName as unmanaged
│       ├── managed in prod  → problematic (R3)
│       └── absent from prod → problematic (R4)
│
├── [NEW] if any problematic → Console.Error (list) → return 1  (hard block)
│
└── [existing] determine copyType, execute pac admin copy, save config, done
```

---

## Implementation Units

### U1. Pure comparison helper in ProvisionCommand

**Goal:** Add `internal static FindProblematicSolutions` to enable unit tests without PAC CLI dependency.

**Requirements:** R2, R3, R4, R6

**Dependencies:** None

**Files:**
- Modify: `src/Flowline/Commands/ProvisionCommand.cs`
- Test: `tests/Flowline.Tests/ProvisionCommandTests.cs` (new file, mirror xUnit + FluentAssertions structure from `tests/Flowline.Tests/PacUtilsTests.cs`)

**Approach:**
- Add `internal static IReadOnlyList<(SolutionInfo Target, string Reason)> FindProblematicSolutions(IEnumerable<SolutionInfo> targetSolutions, IEnumerable<SolutionInfo> prodSolutions)`.
- Filter target to `!IsManaged` solutions with non-null `SolutionUniqueName`. For each, look up in prod by unique name (case-insensitive).
- Return entries where the prod match is managed (`Reason = "managed in prod"`) or absent (`Reason = "absent from prod"`).
- Pure function — no I/O, no PAC CLI dependency.

**Patterns to follow:**
- `!s.IsManaged` filtering in `src/Flowline/Commands/CloneCommand.cs` (`FindUnmanagedSourceAsync`)

**Test scenarios:**
- Happy path: target has unmanaged `MySolution`, prod has it unmanaged → returns empty list.
- Covers AE1: target has unmanaged `MySolution`, prod has it managed → returns one entry, reason `"managed in prod"`.
- Covers AE2: target has unmanaged `WorkInProgress`, prod has no such solution → returns one entry, reason `"absent from prod"`.
- Covers AE3: target has unmanaged `SharedLib`, prod has it unmanaged → returns empty list.
- Edge case: target has zero unmanaged solutions → returns empty list.
- Edge case: unique name matching is case-insensitive (`"mysolution"` matches `"MySolution"`).
- Edge case: target solution with null `SolutionUniqueName` → excluded (not treated as problematic).
- Edge case: target has managed solutions only → all filtered by `!IsManaged`, returns empty list.

**Verification:**
- `ProvisionCommandTests` passes for all scenarios above.

---

### U2. Pre-copy safety check in ProvisionCommand.ExecuteFlowlineAsync

**Goal:** Wire the parallel solution queries and comparison into `ExecuteFlowlineAsync`, blocking the copy and reporting at-risk solutions.

**Requirements:** R1, R3, R4, R5, R6, R7

**Dependencies:** U1

**Files:**
- Modify: `src/Flowline/Commands/ProvisionCommand.cs`

**Approach:**
- After the `if (!settings.AllowOverwrite)` early-return, `await Task.WhenAll` to fetch prod and target solution lists in parallel via `PacUtils.GetSolutionsAsync`.
- Pass both lists to `FindProblematicSolutions`.
- If non-empty: print each entry with `Console.Error` showing the solution unique name and reason, return 1.
- If empty: fall through to existing copy logic unchanged.
- R5 is structural — `--allow-overwrite` behavior is unchanged; the check simply runs after it.

**Patterns to follow:**
- `Console.Error(...)` for fatal user-facing errors in `src/Flowline/Commands/ProvisionCommand.cs`.

**Test scenarios:**
- Integration scenario: `FindProblematicSolutions` returning non-empty → `ExecuteFlowlineAsync` returns 1 before the `pac admin copy` spinner. (Manual verification — PAC CLI dependency makes unit isolation impractical.)
- Integration scenario: `FindProblematicSolutions` returning empty → execution reaches copy type determination.
- Edge case (Covers AE5): freshly created target env → `GetSolutionsAsync` returns empty list → `FindProblematicSolutions` returns empty → copy proceeds.

**Verification:**
- Running `provision test --allow-overwrite` against an env with a known unmanaged solution (managed in prod) prints the solution name and aborts before the `pac admin copy` spinner appears.
- Running the same against an env where all unmanaged target solutions are also unmanaged in prod reaches the copy spinner as before.

---

## System-Wide Impact

- **Interaction graph:** Affects only `ProvisionCommand.ExecuteFlowlineAsync`. No callbacks, middleware, or shared state.
- **Error propagation:** PAC CLI failures in `GetSolutionsAsync` bubble up as exceptions (existing behavior) — no change.
- **Unchanged invariants:** `--allow-overwrite` semantics for the "env already exists" path are unchanged. All other commands (`sync`, `deploy`, `clone`) are unaffected.
- **Latency:** Two parallel `pac solution list` calls add roughly one PAC CLI call's worth of latency before the copy (parallel masks the second). Acceptable given `pac admin copy` is a long async operation.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| PAC CLI rate limiting or auth expiry during the two solution-list calls | Existing exception propagation covers this — same behavior as any other PAC CLI call in the codebase. |

---

## Sources & References

- **Origin document:** [docs/brainstorms/provision-unmanaged-solution-guard-requirements.md](docs/brainstorms/provision-unmanaged-solution-guard-requirements.md)
- `src/Flowline/Commands/ProvisionCommand.cs`
- `src/Flowline/Utils/PacUtils.cs` (`GetSolutionsAsync`, `SolutionInfo`)
- `src/Flowline/Commands/CloneCommand.cs` (`FindUnmanagedSourceAsync` — `IsManaged` filter pattern)
