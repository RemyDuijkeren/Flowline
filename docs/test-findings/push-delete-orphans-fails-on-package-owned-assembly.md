# `push --force delete-orphans` fails on a package-owned orphan assembly — after deleting its children

- **Status**: **fixed 2026-07-29, fully live-verified against DEV (delete half 07-29, refusal half and
  multi-assembly collapse 07-30)** — push now deletes the owning package when that package owns nothing
  but orphans, and refuses, touching nothing, when it owns anything else. See "Fix" below.
- **Severity**: high — the run deletes the orphan's plugin types, steps and images, *then* fails on the
  assembly itself with a raw Dataverse fault and exit 1, leaving Dataverse in a half-cleaned state that
  a re-run does not obviously recover.
- **Found**: 2026-07-29, live, `flowline push --scope plugins --force delete-orphans` against DEV.

## Repro

Requires an orphan plugin assembly that is owned by a plugin *package* — the normal shape after a
nupkg-mode plugin project is renamed, since the old package and its assembly both stay behind:

```
pac org fetch --xml "<fetch><entity name='pluginassembly'><attribute name='name'/><attribute name='packageid'/></entity></fetch>"

name                   packageid
Plugins                av_Plugins              <- package-owned
Cr07982.Plugins        av_Cr07982.Plugins      <- package-owned
Cr07982.LegacyPlugins  (none)                  <- classic
```

```
flowline push --scope plugins --force delete-orphans
```

Observed:

```
! Plugins.dll in environment — no local source. Deleting.
·   Plugins.AatYourServiceApi — cascade delete
·   Plugins.MyFirstPostUpdatePlugin — cascade delete
·   Plugins.Plugin1 — cascade delete
·   Plugins.MyFirstPostUpdatePlugin: Update of account — cascade delete
·   Post Image — cascade delete
Error: 'Cr07982.LegacyPlugins' failed to push: Unable to delete plug-in assembly as it is part of
plugin package. Already in the org: Cr07982.Backend. Fix 'Cr07982.LegacyPlugins', then push again.
```

Exit 1 (`GeneralError`). The five cascade deletes above the error line had already been executed.

Without `--force delete-orphans` the same orphans are reported and nothing is deleted (exit 0) — but
the report was wrong too, promising that `--force delete-orphans` would delete an assembly that cannot
be deleted at all.

## Root cause

`PluginService.WarnOrphanAssembliesAsync` (`src/Flowline.Core/Plugins/PluginService.cs:685-750`)
queries `pluginassembly` with `ColumnSet("pluginassemblyid", "name")` — it never reads `packageid` —
then, when `--force delete-orphans` is set, deletes the children in reverse dependency order and
finishes with `service.DeleteAsync("pluginassembly", entity.Id, …)`. Dataverse refuses that delete for
any assembly with a `packageid` ("Unable to delete plug-in assembly as it is part of plugin package"),
so the last call throws after every child delete has already committed. There is no transaction.

Flowline already knows this rule elsewhere: `PluginAssemblyFamilyHandler` (deploy's orphan cleanup)
live-checks `packageid` for exactly this reason and **redirects** an orphaned package-owned assembly to
a `pluginpackage` delete finding instead
(`src/Flowline.Core/OrphanCleanup/Handlers/PluginAssemblyFamilyHandler.cs:69-76, 141-162`), collapsing
multiple assemblies of one package into a single package delete. Push's own orphan path predates that
and never got the same treatment.

Two consequences worth separating:

1. **Ordering** — children are deleted before the parent delete is known to be possible. Even once the
   parent case is handled, the children of an assembly that cannot be deleted should not be destroyed
   first.
2. **Error shape** — the failure surfaces as the raw Dataverse fault text under exit 1, with no next
   step. Worse, the multi-project failure wrapper
   (`src/Flowline/Commands/PushCommand.cs:356-369`) attributes it to whichever project's pass was
   running — `Cr07982.LegacyPlugins` — and says "Fix 'Cr07982.LegacyPlugins', then push again", which
   has nothing to do with the orphan `Plugins` / `av_Plugins` that actually failed. The wrapper is
   behaving correctly; the inner failure just should never have reached it.

## Fix

Routing to `deploy` was rejected as the answer: `deploy` imports into a *target* environment, so its
orphan cleanup can never clean DEV. `push` is the only cleanup tool DEV has, so `push` had to grow the
capability rather than delegate it.

`WarnOrphanAssembliesAsync` now reads `packageid` alongside the orphan, then runs one org-wide
`pluginassembly where packageid In (…)` query (deliberately *not* solution-scoped — the orphan query
only sees this solution's members, and a package delete removes everything it owns anywhere) plus a
`pluginpackage` name lookup. A package is deletable when every assembly it owns is in this run's orphan
set:

- **Deletable** — children are deleted as before, then the `pluginpackage` is deleted (once per
  package, after the loop, so every orphan sibling in that package has had its children cleared first).
  The assembly and its plugin types cascade away with the package. The one-assembly leftover from a
  rename is the common case; a renamed multi-assembly nupkg project is the same rule.
- **Not deletable** — the package also owns an assembly this run can't account for (another solution's,
  or one this push just registered). Nothing is touched, including the orphan's children, and the
  warning names the package to remove by hand.

The refusal only holds if the *next* pass respects it. `WarnOrphanStepsAsync` runs immediately after and
selects solution-member steps whose owning assembly is not in the pushed set — which a refused orphan is
by definition — so it would have deleted those same steps a few lines later, in the same run that said
"remove the package yourself". `WarnOrphanAssembliesAsync` now returns the refused assembly ids and the
step query excludes them server-side, the same shape as the existing pushed-assembly exclusion.

Deleting the children never unlocks the assembly delete — Dataverse rejects it on `packageid` alone, as
the observed run proves — so the "delete children, then the assembly" shape was removed for the
package-owned case entirely rather than reordered.

Detection-side messages were part of the fix, not just deletion: `Use --force delete-orphans to delete`
now names the package it would remove, and the blocked case never prints cascade/`would delete` lines
for children that will not be touched. Reported as a warning, not a `FlowlineException` — a refused
orphan cleanup matches the no-`--force` behavior (warn, exit 0) rather than failing the push.

Covered by six tests in `tests/Flowline.Core.Tests/PluginServiceTests.cs`: the classic (non-package)
orphan baseline, the fully-owned package delete, the shared-package refusal, the orphan-step pass
honouring that refusal, the blocked `--dry-run` printing no cascade lines, and the without-`--force`
hint text. Three more landed with the follow-up work described below.

## Follow-up in the same session

Two further changes came out of this, both recorded in the CHANGELOG and in `docs/test-goal.md`'s
session-5 note rather than in their own finding files, since neither needed a judgment call:

1. **The orphan passes now run on the plugin-package path too.** A solution whose plugin projects all
   pack to a `.nupkg` had no orphan pass anywhere — the check ran only on the classic path, and the
   documented fallback (deploy's orphan cleanup) cannot reach a development environment.
2. **A critical pre-existing bug this exposed**: a snapshot's `CustomApis`/`RequestParams`/
   `ResponseProps` are resolved publisher-prefix-wide, not per assembly, and the orphan path deleted the
   whole list — so clearing one orphan assembly deleted every Custom API sharing the publisher prefix,
   silently. Now filtered to the APIs bound to the orphan's own plugin types, and listed in the cascade.
   Found because adding the package path made an existing test fail.

Line numbers in "Root cause" above predate the fix and no longer resolve.

### Not carried over from deploy

`PluginAssemblyFamilyHandler`'s degradation guards (`skipRedirectedFindingsThisRun`) and its CustomApi
sequence-hint scheme have no analogue here — push's orphan path deletes synchronously from a snapshot
it just loaded, with no finding pipeline to degrade and no cross-family ordering to arrange. Push's
own child deletes already cover CustomApis, so the ordering that scheme exists to produce is inherent.

## Live verification (2026-07-29, DEV, tool `0.13.1-alpha.0.14`)

Version caveat: with no new commit, MinVer keeps the same height, so a repack produces the *same*
version string with different code. `dotnet tool install` will then silently reuse the cached package —
clear `~/.nuget/packages/flowline/<version>` before reinstalling, and check the installed
`Flowline.Core.dll` timestamp rather than trusting `flowline --version`.

Exact repro re-run in `C:\Code\FlowlineTryOutByClaude` against DEV.

Pre-state — two package-owned orphans, one live package, one classic assembly:

```
name                   packageid
Plugins                av_Plugins
Cr07982.Plugins        av_Cr07982.Plugins
Cr07982.Backend        av_Cr07982.Backend   <- live, pushed by this run
Cr07982.LegacyPlugins  (none)               <- classic
```

`flowline push --scope plugins` (no force) — exit 0, nothing deleted, hint now names each package:

```
! Plugins.dll in environment — no local source. Use --force delete-orphans to delete package av_Plugins.
! Cr07982.Plugins.dll in environment — no local source. Use --force delete-orphans to delete package av_Cr07982.Plugins.
```

`flowline push --scope plugins --force delete-orphans` — **exit 0**, no Dataverse fault (previously
exit 1 after committing the child deletes):

```
! Plugins.dll in environment — no local source. Deleting package av_Plugins.
! Cr07982.Plugins.dll in environment — no local source. Deleting package av_Cr07982.Plugins.
·   Cr07982.Plugins.FlagAccountPostCreatePlugin — cascade delete
·   Cr07982.Plugins.Plugin1 — cascade delete
·   Cr07982.Plugins.FlagAccountPostCreatePlugin: Create of account — cascade delete
```

The evidence that the delete-children-then-delete-package path works is `Cr07982.Plugins`, which
printed three cascade lines and went away. `Plugins.dll` printed none; the likely reason is that the
original failed run had already destroyed its children (the half-cleaned state this finding describes),
but that is inference — an empty snapshot looks identical, and the assembly is now gone either way.

Post-state confirms both packages and both assemblies are gone, and that the exclusion of pushed
assemblies held — `av_Cr07982.Backend` was updated by the same run and survived:

```
pluginassembly:  Cr07982.Backend (av_Cr07982.Backend), Cr07982.LegacyPlugins (none)
pluginpackage:   av_Cr07982.Backend
```

An immediate re-run prints no orphan warnings at all, exit 0. The run log
(`%LOCALAPPDATA%\Flowline\logs\…-push.log`) carries the same lines with no markup leakage.

**The refusal branch is live-verified too (2026-07-30), and it is a realistic case, not a defensive
one.** It needs a package owning both an orphan and a non-orphan, which a multi-assembly package
provides: push a `.nupkg` carrying two plugin-bearing DLLs, then drop one of them from the project. The
dropped assembly stays registered in Dataverse with no local source (an orphan), while its package still
owns the other assembly — which the same push registers. Deleting that package would have destroyed a
live assembly. It refused:

```
! Cr07982.ProbeExtra.dll in environment — no local source. Package av_Cr07982.Backend owns
  assemblies that aren't orphans — not deleting it.
```

Nothing was deleted, and the package survived. The run also corrected the message: the original wording
("owns assemblies this solution doesn't") is wrong for this, the commonest shape — the solution *does*
have `Cr07982.Backend`; it simply isn't an orphan.

The **fully-owned multi-assembly collapse** was verified in the same session: with both of that
package's assemblies orphaned, the two of them produced one package delete, not two, each with its own
cascade.

Getting there required building a genuine two-DLL package, which exposed two separate platform-level
bugs in the package update path — see
`docs/test-findings/changing-a-plugin-packages-assemblies-breaks-push.md`. That finding also
corrects an earlier guess of mine, that Dataverse registers only one assembly per package: Microsoft
documents the opposite and a from-scratch package create registers both.

**Verified live instead: the Custom API scoping fix** (see "Follow-up" below), with a discriminating
probe. Two `[CustomApi]` classes were planted, one in the package project and one in the classic
project, giving `av_BackendProbe` and `av_LegacyProbe` under the same publisher prefix but different
assemblies. A standalone push of only the Backend `.nupkg` with `--force delete-orphans` orphans the
classic assembly, and its cascade printed and deleted `av_LegacyProbe` while leaving `av_BackendProbe`
untouched:

```
! Cr07982.LegacyPlugins.dll in environment — no local source. Deleting.
·   av_LegacyProbe — cascade delete
·   Cr07982.LegacyPlugins.LegacyAuditPostUpdatePlugin — cascade delete
·   Cr07982.LegacyPlugins.LegacyProbeApi — cascade delete
·   Cr07982.LegacyPlugins.LegacyAuditPostUpdatePlugin: Update of contact — cascade delete
```

Before the fix both APIs would have gone, and neither would have been named in the output. The same run
under `--dry-run` printed the same four lines as `would delete (cascade)` and changed nothing —
re-queried afterwards, both APIs and both assemblies were still present. The fixture was then restored:
the orphaned assembly re-pushed, both probe classes deleted, and a final push removed their APIs.

## Note on DEV state

This run deleted the plugin types/steps/images belonging to the orphan `Plugins` assembly in DEV. That
was within the test goal's explicit DEV-is-disposable permission. The re-run on the fixed build (see
"Live verification") cleared the rest: both packages and both assemblies are gone, and DEV now holds
only `Cr07982.Backend` / `av_Cr07982.Backend` and the classic `Cr07982.LegacyPlugins`.

One thing that run also settled: `ResolveOrphanPackagesAsync` filters `pluginassembly` on `packageid`,
which nothing in this codebase did before (`ResolvePackageIdsAsync` only *selects* it). It works — both
packages resolved as fully orphaned rather than falling into the fail-safe refusal, which is what a
faulting or unfilterable lookup would have produced.
