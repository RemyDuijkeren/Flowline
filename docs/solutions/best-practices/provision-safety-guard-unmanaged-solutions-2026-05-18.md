---
title: "Safety guard for unmanaged solutions in ProvisionCommand"
date: 2026-05-18
category: docs/solutions/best-practices/
module: provision
problem_type: best_practice
component: tooling
severity: critical
applies_when:
  - Running provision with --allow-overwrite against an existing dev/test environment
  - Target environment holds unmanaged Dataverse solutions under active development
  - Unmanaged target solutions are absent from prod or exist as managed in prod
tags:
  - dataverse
  - unmanaged-solutions
  - pac-admin-copy
  - safety-guard
  - provision
  - data-loss
---

# Safety guard for unmanaged solutions in ProvisionCommand

## Context

`pac admin copy` is a full environment overwrite — it replaces the entire target environment's content with a copy of the source. Dev and test environments typically hold unmanaged Dataverse solutions: partially built features, solutions under active iteration, or work-in-progress that has never been promoted to prod.

Two conditions make an unmanaged solution on the target irretrievably lost after a copy:

1. **The solution is absent from prod.** It exists only in the target. After copy, it is gone — no recovery path.
2. **The solution exists in prod as managed.** The unmanaged target version (editable, with source code) is replaced by the managed prod version (locked, not editable). The unmanaged work is permanently lost.

Before the guard was added, `--allow-overwrite` conflated two distinct risk levels: bypassing an "environment already exists" friction warning (low risk) and authorising permanent data loss (high risk). A developer who set `--allow-overwrite` to skip the existence check would also, silently, authorise destruction of any unmanaged solutions in the target.

## Guidance

Before invoking `pac admin copy`, fetch the solution list from both prod and the target in parallel, then identify unmanaged target solutions that would be permanently lost. Block with a hard error and list every at-risk solution by name and reason.

**Call site in `ProvisionCommand.ExecuteFlowlineAsync`** — after the `--allow-overwrite` early-return check, before the copy:

```csharp
var prodTask   = PacUtils.GetSolutionsAsync(prodEnv.EnvironmentUrl!,   settings.Verbose, cancellationToken);
var targetTask = PacUtils.GetSolutionsAsync(targetEnv.EnvironmentUrl!, settings.Verbose, cancellationToken);
await Task.WhenAll(prodTask, targetTask);
var prodSolutions   = prodTask.Result;
var targetSolutions = targetTask.Result;

var problematic = FindProblematicSolutions(targetSolutions, prodSolutions);
if (problematic.Count > 0)
{
    Console.Error("Target environment has unmanaged solutions that would be permanently lost:");
    foreach (var (solution, reason) in problematic)
        Console.Error($"  {solution.SolutionUniqueName} ({reason})");
    return 1;
}
```

**`FindProblematicSolutions` — pure static method, no PAC CLI dependency:**

```csharp
internal static IReadOnlyList<(SolutionInfo Target, string Reason)> FindProblematicSolutions(
    IEnumerable<SolutionInfo> targetSolutions,
    IEnumerable<SolutionInfo> prodSolutions)
{
    var prodByName = prodSolutions
        .Where(s => s.SolutionUniqueName != null)
        .ToDictionary(s => s.SolutionUniqueName!, StringComparer.OrdinalIgnoreCase);

    return targetSolutions
        .Where(s => !s.IsManaged && s.SolutionUniqueName != null)
        .Select(s =>
        {
            if (!prodByName.TryGetValue(s.SolutionUniqueName!, out var prodMatch))
                return (Target: s, Reason: "absent from prod");
            if (prodMatch.IsManaged)
                return (Target: s, Reason: "managed in prod");
            return (Target: s, Reason: "");
        })
        .Where(x => x.Reason != "")
        .ToList();
}
```

Key implementation decisions:

- **No escape hatch.** There is no `--force` flag to bypass this check. The risk is permanent data loss; the correct action is to delete the unmanaged solutions manually before running provision, not to bypass the guard.
- **Cross-environment comparison, not target-only.** Checking only "does the target have any unmanaged solutions?" would block safe cases where prod also has the solution as unmanaged. The comparison is: "is this unmanaged target solution at risk given what prod contains?"
- **`--allow-overwrite` scope unchanged.** The flag bypasses the "environment already exists" warning only. It does not and must not bypass the data-loss check.
- **No system solution filtering needed.** Microsoft system solutions are always managed — excluded by the `!s.IsManaged` filter. The Default Solution is always unmanaged in both environments, so it passes the cross-environment comparison cleanly.
- **`Task.WhenAll` for parallel fetch.** `pac admin copy` is a slow operation. Fetching both solution lists sequentially would add visible latency. Parallel fetch keeps the pre-flight check fast.
- **Null-safe.** Solutions with a null `SolutionUniqueName` are excluded from both the prod dictionary and the target scan.

## Why This Matters

`pac admin copy` has no undo. Once a target environment is overwritten:

- Unmanaged solutions absent from prod no longer exist anywhere.
- Unmanaged solutions that were managed in prod are replaced with their locked managed versions — editability and the source code relationship are gone.

The guard makes this category of data loss impossible to trigger accidentally. A developer who wants to intentionally clean the target must first manually delete the at-risk unmanaged solutions, then run provision. This forces a conscious decision rather than an accidental one.

Keeping the check logic in a pure `internal static` method (`FindProblematicSolutions`) means it can be unit tested without a live PAC CLI or Dataverse connection.

## When to Apply

Apply this pattern whenever a CLI command wraps an operation that:

- Is **irreversible** (environment copy, environment delete, bulk record delete)
- **Destroys data the CLI itself created or manages** (solutions synced via `flowline sync`, environments provisioned by Flowline)
- Has an **existing bypass flag** (`--allow-overwrite`, `--force`) that might be misread as authorising the destructive sub-operation

Do not add an escape hatch for the data-loss check. If blocking feels too strict, the correct fix is to tighten the detection (reduce false positives) — not to add a flag that lets users skip it.

## Examples

**Blocked — unmanaged solution is managed in prod:**

Target has `MySolution` as unmanaged. Prod has `MySolution` as managed (deployed via release pipeline). Running `provision test --allow-overwrite` exits with:

```
Target environment has unmanaged solutions that would be permanently lost:
  MySolution (managed in prod)
```

Resolution: export or delete `MySolution` from the target, then re-run.

**Blocked — unmanaged solution is absent from prod:**

Target has `WorkInProgress` as unmanaged. Prod has no solution by that name. Running `provision dev --allow-overwrite` exits with:

```
Target environment has unmanaged solutions that would be permanently lost:
  WorkInProgress (absent from prod)
```

Resolution: commit or export the solution, delete it from the target, then re-run.

**Allowed — unmanaged solution also unmanaged in prod:**

Target has `SharedLib` as unmanaged. Prod also has `SharedLib` as unmanaged. `FindProblematicSolutions` returns empty — provision continues. The copy will restore `SharedLib` from prod, which is already in the same state.

**Unit test coverage (all in `ProvisionCommandTests.cs`):**

```csharp
FindProblematicSolutions([Unmanaged("SharedLib")],    [Unmanaged("SharedLib")])  // → empty (safe)
FindProblematicSolutions([Unmanaged("MySolution")],   [Managed("MySolution")])   // → "managed in prod"
FindProblematicSolutions([Unmanaged("WorkInProgress")], [])                      // → "absent from prod"
FindProblematicSolutions([Managed("ManagedSolution")], [])                       // → empty (no unmanaged on target)
FindProblematicSolutions([], [])                                                  // → empty
FindProblematicSolutions([Unmanaged("mysolution")],   [Unmanaged("MySolution")]) // → empty (case-insensitive)
FindProblematicSolutions([{SolutionUniqueName: null, IsManaged: false}], [])     // → empty (null excluded)
```

## Related

- `src/Flowline/Commands/ProvisionCommand.cs` — implementation (`FindProblematicSolutions` + call site)
- `tests/Flowline.Tests/ProvisionCommandTests.cs` — 7 unit test scenarios
- `src/Flowline/Utils/PacUtils.cs` — `GetSolutionsAsync` (fetches solution list via PAC CLI)
- `docs/solutions/logic-errors/sync-overwrites-uncommitted-src-without-warning-2026-05-15.md` — related pattern: pre-sync guard that prevents `flowline sync` from overwriting uncommitted `src/` changes
