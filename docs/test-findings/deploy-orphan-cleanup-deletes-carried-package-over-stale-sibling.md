# Deploy's orphan cleanup targets a plugin package the solution still carries

- **Status:** open, not fixed inline. The guard hole is reproduced; the import failure next to it has
  no established cause, and the remedy is a product decision (below).
- **Severity:** high. The cleanup plans to delete the live plugin package that the deploy is about to
  import. In this run Dataverse refused the delete on a dependency, so nothing was lost.
- **Found:** 2026-11-27, round "changes since 0.18.0" in
  [`../end-to-end-test-log.md`](../end-to-end-test-log.md), on a real `deploy test`.
- **Component:** `src/Flowline.Core/OrphanCleanup/Handlers/PluginAssemblyFamilyHandler.cs`, the
  package-content exclusion and the package-scope redirect. Present since 2026-08-19/20 (`44cb447`,
  `f74cb1e`), so it predates 0.18.0.

## What happened

TEST held package `av_FlowlineDeployTest.Plugins` (`a4ee77fc`) with three assemblies:
`FlowlineDeployTest.Plugins` (in the solution being deployed) and two leftovers from the August
rounds, `FlowlineDeployTest.Probe` and `FlowlineDeployTest.Probe2`, each still with a plugin type.
The deployed package content carries only `FlowlineDeployTest.Plugins.dll`.

`flowline deploy test` (1.1.0.0 over 1.0.6.0):

1. The orphan report listed `PluginPackage a4ee77fc (owns PluginAssembly 'FlowlineDeployTest.Probe')
   — delete`. `drift test` earlier reported the same package as `would delete` over `Probe2`.
2. Pre-import, it deleted the `Probe` step and Custom API `av_KeepMe`, then logged `Deferred:
   PluginPackage a4ee77fc … — dependency, will retry post-import`. Dataverse refused the delete.
3. The import failed: `Import failed: Unable to delete plug-in assembly as it is part of plugin
   package`. Exit 13, message `Deploy failed — check the environment and your PAC login.`

## Root cause of the guard hole

A package-owned orphan assembly is redirected to a delete of its whole package. The guard against
destroying live content (`protectedPackageIds`) only covers a package when one of the orphan
*candidates* is in that package's local content. The assembly the solution actually carries
(`FlowlineDeployTest.Plugins`) is in the manifest, so it is never a candidate and never protects its
package. A carried package with one stale sibling is therefore marked for deletion.

The comment above the redirect states the intent: a package the imported solution no longer carries
yields no exclusions, and only then does the package-delete path run (R12). The code checks the
candidate, not whether the package is carried.

**Offline repro** (scratch test against the existing `OrphanCleanupServiceTests` harness, not kept):
package `av_FlowlineDeployTest.Plugins` in the local content with `FlowlineDeployTest.Plugins.dll`;
live assemblies A (in the manifest) and B (`FlowlineDeployTest.Probe`, not in content) under the
same package. `RunPreImportAsync` printed `PluginPackage … (owns PluginAssembly
'FlowlineDeployTest.Probe') — delete` and called `DeleteAsync("pluginpackage", <id>)`.

## The import failure (cause not established)

The package was still in place when the import failed, so the guard hole did not cause it. The
hypothesis: Dataverse cannot drop an assembly from a package's content while that assembly still
has plugin types. Both leftovers still have one (`Probe.ProbePlugin`, `Probe2.Probe2Plugin`, queried
in TEST after the run). `push` handles the same shape in DEV by clearing the dropped assembly's
registrations first (`… no longer in the package — clearing its registrations so the update can
remove it`); deploy has no such step. Not confirmed.

## Decision needed

What deploy should do with a stale assembly inside a package it still carries:

- leave it registered but inert and only delete its steps, which is what the guard's Fix 2 comment
  describes;
- clear its plugin types before the import, as `push` does, so the import can drop it;
- stop the deploy and name it.

Whatever the choice, the package-scope delete must not run for a package the imported solution
carries.

## Repro state kept

TEST still has `Probe` and `Probe2` with their plugin types, and `FlowlineDeployTest` at 1.0.6.0.
`flowline drift test` shows the planned package delete without writing anything; use it as the
before-and-after check for a fix.

## Related, pre-existing

A failed import exits 13 (`BuildFailed`) with "check the environment and your PAC login", which
points an agent at auth rather than at the import error pac printed
(`src/Flowline/Commands/DeployCommand.cs:600`).
