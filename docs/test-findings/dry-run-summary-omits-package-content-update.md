# `push --dry-run` summary counts omit the plugin package / assembly content update

- **Status**: **fixed** 2026-07-29. Regression tests added, full suite green, live re-verified.
- **Severity**: medium — the summary line contradicted the detail line directly above/below it. An agent
  or a user reading only the summary concluded "nothing would change" and skipped the real push.
- **Found**: 2026-07-29, live, `flowline push --dry-run` (nupkg package mode) against DEV.

## Repro

In a Flowline project whose plugin project packs to a `.nupkg` (`PluginPackageMode.Auto` → nupkg),
with the package content differing from what's registered in Dataverse but no plugin type / step /
Custom API changes:

```
flowline push --scope plugins --dry-run
```

Observed before the fix:

```
Cr07982.Plugins (0.0.0.0)
✓ Dry run: 0 delete(s), 0 create(s), 0 update(s). Run without --dry-run to apply.
·   ~ Package av_Cr07982.Plugins — would update content
```

A real push of that same state does write: `✓ Package av_Cr07982.Plugins updated`.

The same undercount existed on the classic (non-package) assembly path, where the assembly content
update is tracked as `needsUpdate` and executed in its own phase.

## Root cause

Two distinct halves.

**Classic path** — `WritePlanTree` summed `creates`/`updates` only over
`plan.PluginTypes/Steps/CustomApis/Images/RequestParams/ResponseProps`. `needsUpdate`, the parameter
that drives the very `~ … — would update content` label in the same method, was not in the sum. That
it is a real change is settled elsewhere in the same file: the execute path skips only when
`!needsUpdate && plan.TotalChanges == 0`, and `SyncAssemblyOnlyAsync`'s dry run already reported the
identical event as `"Dry run: 1 update."`.

**Package path** — structural rather than an arithmetic slip. `SyncSolutionFromPackageAsync` called
`WritePlanTree(…, needsUpdate: false, …)` once **per assembly**, each printing its own `Dry run: …`
line, and only then printed the single package create/update line — after the summaries it should have
been counted in. There was no line in the output whose scope matched the package, which owns the
assemblies, so the package write had nowhere correct to be counted.

## Fix

A package push now reports **one** total instead of one summary per assembly:

- `WritePlanTree` returns a summable `PlanCounts` (deletes / creates / updates) and takes
  `writeSummary`, so a caller that aggregates can suppress the per-plan line. Its update count now
  includes `needsUpdate`, which fixes the classic path on its own.
- `SyncSolutionFromPackageAsync` sums every assembly's counts, adds the package's own create *or*
  update, and writes one summary after the package line.
- `SyncPackageStepsOnlyAsync` (R4 no-op path, where the hash matched and no package content is
  written) also writes a single aggregate summary — the sum of the per-assembly plans, with no package
  entry, which is correct for that path.

All in `src/Flowline.Core/Plugins/PluginService.cs`.

Regression tests in `tests/Flowline.Core.Tests/PluginServiceTests.cs` — all four fail against the
pre-fix file and pass after it:

- `SyncSolutionFromPackageAsync_DryRun_ExistingPackageChanged_CountsPackageUpdateInSummary`
- `SyncSolutionFromPackageAsync_DryRun_NewPackage_CountsPackageCreateInSummary`
- `SyncSolutionFromPackageAsync_DryRun_MultipleAssemblies_WritesOneSummaryForThePackage` (asserts
  exactly one `Dry run:` line for a two-assembly package)
- `SyncAsync_DryRun_AssemblyContentChangedOnly_ReportsOneUpdate` (classic path)

## Live verification

`flowline push --scope plugins --dry-run` in the test workspace, package content changed, no
registration changes:

```
Cr07982.Backend (0.0.0.0)
·   ~ Package av_Cr07982.Backend — would update content
✓ Dry run: 0 delete(s), 0 create(s), 1 update(s). Run without --dry-run to apply.
```

One summary, it counts the package write, and it now reads after the package line rather than before
it.
