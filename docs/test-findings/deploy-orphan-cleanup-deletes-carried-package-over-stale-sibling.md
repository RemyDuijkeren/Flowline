# Deploy's orphan cleanup targets a plugin package the solution still carries

- **Status:** fixed 2026-11-28. The package-scope delete no longer runs for a package that still holds
  a live assembly its imported content accounts for, and deploy warns about the stale assembly before
  the import. The import failure is Dataverse behaviour and is reported, not worked around (below).
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

## The import failure: Dataverse behaviour

Tested in TEST on 2026-11-27 with `pac solution import` of the 1.1.0 zip, outside Flowline:

| State of `Probe` and `Probe2` before the import | Result |
|---|---|
| No steps (the deploy had deleted `Probe`'s; `Probe2` had none) | Same error |
| No steps and no plugin types (both deleted through the Web API) | Same error |
| Package content updated directly with the 1.1.0 nupkg first (Dataverse removed both assemblies) | Import succeeded, 1.1.0.0 |

So a solution import cannot drop an assembly from an existing plugin package, whatever is attached to
it. A direct package update can, once no step uses the assembly, as
[Microsoft documents](https://learn.microsoft.com/power-platform/developer/howto/cli-create-package#plug-in-package-management).
Not tested: whether the direct update also works with the plugin types still in place.

## Decision

Report, don't work around it: the case needs two plugin assemblies in one package, which Flowline never
scaffolds, and Microsoft may fix the import. Deploy keeps the package, deletes the stale assembly's
steps, and warns before the import with the fix: update the package in the Plug-in Registration Tool,
then deploy again. Uploading the package from deploy (option 3) was rejected as a second write path to
TEST and PROD for a rare case. Managed deploys are unaffected: they use stage-and-upgrade and orphan
cleanup only reports.

## Repro state

Consumed. The manual package update removed `Probe` and `Probe2` from TEST, which is now on 1.1.0.0.
The handler tests cover the shape offline (`PluginAssemblyFamilyHandlerTests`, the carried assembly that
is never a candidate).

## Related, pre-existing

A failed import exits 13 (`BuildFailed`). Its message used to say "check the environment and your PAC
login", pointing at auth rather than at the import error pac printed. It now names the import and
points at pac's error (`src/Flowline/Commands/DeployCommand.cs:600`). The exit code is unchanged.
