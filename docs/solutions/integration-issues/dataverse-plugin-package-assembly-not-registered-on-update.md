---
title: "Dataverse plug-in package doesn't register an assembly added after the package was created"
date: 2026-08-19
module: Flowline.Core.Deploy.PluginPackageAssemblyCheckService
problem_type: integration_issue
component: tooling
severity: high
symptoms:
  - "flowline deploy imports a solution, prints \"Solution Imported successfully\", exits 0 — and a newly added plugin-bearing assembly inside a .nupkg plug-in package never runs in the target"
  - "The target's own re-export names one assembly in the manifest while the package content carries two, so the gap silently propagates to the next environment down the promotion chain"
  - "Hand-registering the missing pluginassembly record in the target, without also fixing the source, causes the next deploy's orphan cleanup to delete the whole plug-in package"
root_cause: platform_limitation
resolution_type: code_fix
tags:
  - dataverse
  - plugin-package
  - pluginassembly
  - solution-import
  - deploy
  - orphan-cleanup
  - push
related_components:
  - PluginPackageAssemblyCheckService
  - PluginAssemblyFamilyHandler
  - PluginService.RegisterPackageAssemblyDirectlyAsync
---

# Dataverse plug-in package doesn't register an assembly added after the package was created

## Problem

A Dataverse plug-in package (`pluginpackage`, the `.nupkg`-based registration model) registers every
plugin-bearing assembly it contains when the package is *created*. When an existing package is
*updated* — a plugin project added to a `.nupkg` build that already exists in the target — the newly
added assembly's DLL content lands in the package, but no `pluginassembly` record is created for it.
The assembly is present, never registered, and never runs.

`flowline push` already compensated for this on the environment it writes to directly: it waits for
the registration it was promised and, if the wait expires, creates the record itself
(`PluginService.RegisterPackageAssemblyDirectlyAsync`,
`src/Flowline.Core/Plugins/PluginService.cs:714`). That fix is local to that one environment — nothing
in the exported solution carries it forward.

## Measurement

Measured 2026-08-17 against a target already holding the same package, three solution imports:

| Variant | `PluginAssembly` root component in the manifest | Package content | Result in target |
|---|---|---|---|
| Content only | absent | new | one row, added assembly absent |
| Component present | present | unchanged | one row, added assembly absent |
| Component present | present | new again | one row, added assembly absent |

Every run reported `Solution Imported successfully`, exit 0, import job 100%. The target's own
re-export then hands back a manifest naming one assembly and a package containing two, so the next
environment down the chain inherits the same hole. `DeployCommand` never referenced `PluginService`
before this fix — its only plug-in assembly work was orphan cleanup, which deletes records and never
creates them.

**Scope of this measurement — read narrowly.** Three unmanaged solution imports, into two Sandbox
environments, in one tenant, on 2026-08-17. **Managed import, and importing into an environment that
has never held the package, are explicitly unmeasured.** Microsoft issue
[microsoft/powerplatform-build-tools#1465](https://github.com/microsoft/powerplatform-build-tools/issues/1465)
is open and tracks this behaviour; if Microsoft answers it, or fixes the platform, re-verify before
trusting this document over the current platform behaviour.

## Response

Flowline doesn't predict this gap before an import — a check that says "the platform will not
register this" hardcodes today's bug and needs code removed by hand the day Microsoft fixes it.
Instead, `deploy` **observes** after every import:

- `PluginPackageAssemblyCheckService` (`src/Flowline.Core/Deploy/PluginPackageAssemblyCheckService.cs`)
  reflects each imported plug-in package's `.nupkg` content for plugin-bearing assemblies and polls
  the target for a `pluginassembly` record per assembly. Anything still missing is named — assembly,
  version, package — with the remedy below and a note that the finding repeats every later deploy
  until it's applied. Registered in `Program.cs` last, after orphan cleanup, so it evaluates the state
  a deploy actually leaves behind.
- It never writes to the target (`ExitCode.PartialSuccess`, 18, instead of a repair). A day where
  Dataverse registers the assembly on its own, the same code path reports nothing and exits 0 — no
  Flowline change required.
- `push` prints one warning the moment its own self-registration fallback fires, saying the record is
  local to that environment and that every deploy target needs the same fix
  (`src/Flowline.Core/Plugins/PluginService.cs:752`).
- Orphan cleanup no longer misreads this gap as a component removed on purpose. It matched a live
  `pluginassembly` against the type-91 root components in the imported `Solution.xml`, and "absent
  from the manifest" used to mean "removed from source" — true for a classic assembly, false for one a
  package still carries in its content but the source environment never registered. Hand-registering
  the record in the target (the remedy below) used to make the *next* deploy delete the entire
  package. `PluginAssemblyFamilyHandler` now checks package content, not just the manifest, before
  treating a package-owned assembly as an orphan
  (`src/Flowline.Core/OrphanCleanup/Handlers/PluginAssemblyFamilyHandler.cs`).

## Remedy

Until the platform issue is resolved, or Flowline gains a repair step of its own (tracked as a planned
feature — see the wiki's Planned Features page), the fix is manual, one target at a time:

> Create the `pluginassembly` record under that package in the target, with `isolationmode` sandbox
> and the assembly's own version, culture and public key token, then deploy again so the content write
> populates its plugin types.

Through the Plugin Registration Tool, the maker portal, or a Web API call. Do this per target — it
does not travel with the next promotion.

## Related

- `docs/plans/2026-08-19-001-feat-deploy-package-assembly-check-plan.md` — the plan that added the
  check, the orphan-cleanup fix, and the push-side promotion note.
- `docs/plans/2026-07-30-001-fix-plugin-package-assembly-set-plan.md` — the earlier push-side fix and
  its own platform findings.
- [dataverse-orphan-assembly-delete-blocked-by-step-dependencies.md](dataverse-orphan-assembly-delete-blocked-by-step-dependencies.md)
  — a different orphan-assembly-deletion pitfall in the same area of the codebase.
- Microsoft issue [microsoft/powerplatform-build-tools#1465](https://github.com/microsoft/powerplatform-build-tools/issues/1465).
