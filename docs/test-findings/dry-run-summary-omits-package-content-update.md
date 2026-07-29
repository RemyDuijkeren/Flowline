# `push --dry-run` summary counts omit the plugin package / assembly content update

- **Status**: not fixed — the count line is per-assembly but the package update is per-package, so
  deciding which line owns it is a design call, not a mechanical fix (see "Why not fixed inline").
- **Severity**: medium — the summary line contradicts the detail line directly above/below it. An agent
  or a user reading only the summary concludes "nothing would change" and skips the real push.
- **Found**: 2026-07-29, live, `flowline push --dry-run` (nupkg package mode) against DEV.

## Repro

In a Flowline project whose plugin project packs to a `.nupkg` (`PluginPackageMode.Auto` → nupkg),
with the package content differing from what's registered in Dataverse but no plugin type / step /
Custom API changes:

```
flowline push --scope plugins --dry-run
```

Observed:

```
Cr07982.Plugins (0.0.0.0)
✓ Dry run: 0 delete(s), 0 create(s), 0 update(s). Run without --dry-run to apply.
·   ~ Package av_Cr07982.Plugins — would update content
```

A real push of that same state does write: `✓ Package av_Cr07982.Plugins updated`.

The same undercount shows on the classic (non-package) assembly path, where the assembly content
update is tracked as `needsUpdate` and executed in its own phase:

- `PluginService.cs:1215-1222` — `creates`/`updates` are summed only over
  `plan.PluginTypes/Steps/CustomApis/Images/RequestParams/ResponseProps`. `needsUpdate` (the parameter
  that drives the very `~ … — would update content` label at `PluginService.cs:1089-1091`) is not in
  the sum.
- `PluginService.cs:208` proves it *is* a change: the real-run path skips only when
  `!needsUpdate && plan.TotalChanges == 0`.
- `PluginService.cs:96-100` (`SyncAssemblyOnlyAsync`) already establishes the opposite, correct
  convention for the same event: `console.Ok("Dry run: 1 update. Run without --dry-run to apply.")`.

On the package path the omission is structural rather than an oversight in the sum: `SyncSolutionFromPackageAsync`
calls `WritePlanTree(metadata, needsUpdate: false, …)` once **per assembly** (`PluginService.cs:426-427`)
— each printing its own `Dry run: …` line — and only then prints the single package create/update line
(`PluginService.cs:429-434`), after the summaries it should have been counted in.

## Root cause

The dry-run summary is rendered per plugin *assembly*, but "package content would be updated" is a
fact about the *package*, which is one level above the assemblies (and can own several). There is no
line in the current output whose scope matches the package, so the package update has nowhere correct
to be counted.

## Suggested fix direction

Make the package path print one aggregate summary after the per-assembly trees instead of one summary
per assembly — sum every assembly's plan plus the package create/update — and suppress
`WritePlanTree`'s own summary when it is called as part of a package push. On the classic path the fix
is then a one-liner in the same place (`updates + (needsUpdate ? 1 : 0)`), consistent with
`SyncAssemblyOnlyAsync`'s existing "Dry run: 1 update."

## Why not fixed inline

Two reasonable answers exist for the package path (aggregate summary vs. attributing the package
update to the primary assembly's summary), and they produce different output shapes for the
multi-assembly package case. That is a UX/design decision for the owner, and the bug-fix policy in
`docs/test-goal.md` reserves those for a finding rather than an inline fix. The classic-path one-liner
was not applied on its own because shipping half of a two-path inconsistency would leave the two
summaries disagreeing with each other instead of with the detail line.
