# `push --force delete-orphans` fails on a package-owned orphan assembly — after deleting its children

- **Status**: not fixed — the correct behavior (redirect to a package delete, skip with a clear
  message, or something else) is the same design question deploy's orphan cleanup already answered
  differently; see "Why not fixed inline".
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

Without `--force delete-orphans` the same orphans are reported correctly and nothing is deleted
(exit 0) — the detection half is fine; only the deletion half is broken.

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
2. **Error shape** — the failure surfaces as the raw Dataverse fault text under exit 1, not as a
   `FlowlineException` with an exit code and a next step (e.g. "delete the `av_Plugins` package" or
   "run `deploy` whose orphan cleanup handles package-owned assemblies").

## Suggested fix direction

Read `packageid` in the orphan query, and for a package-owned orphan either (a) mirror the deploy-side
behavior — collapse to a single `pluginpackage` delete, deleting children first only once that path is
confirmed viable — or (b) refuse it explicitly with a `FlowlineException` that names the owning package
and the command that can remove it, and leave the children alone. Whichever is chosen, move the child
deletes so they cannot run for an assembly whose own delete will be rejected.

## Why not fixed inline

Option (a) means `push` acquires the power to delete an entire plugin package — including any *other*
assemblies that package owns — as a side effect of `--force delete-orphans`. That is a materially
larger blast radius than the flag has today, and deploy's version of it is deliberately surrounded by
degradation guards (`skipRedirectedFindingsThisRun`) and a cascade-ordering scheme. Choosing between
that and the conservative refusal is an owner decision, which `docs/test-goal.md`'s bug-fix policy
routes to a finding rather than an inline fix.

## Note on DEV state

This run deleted the plugin types/steps/images belonging to the orphan `Plugins` assembly in DEV. That
is within the test goal's explicit DEV-is-disposable permission, but the `Plugins` and
`Cr07982.Plugins` assemblies (and their `av_Plugins` / `av_Cr07982.Plugins` packages) are still present
and still orphaned.
