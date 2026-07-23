# Standalone `push --pluginFile` against a PluginPackage-registered assembly crashes with a raw FaultException

- **Status**: fixed. Root cause was broader than "standalone" — any single-plugin-project push
  bypasses `PushCommand`'s multi-target exception wrapping (`DescribePluginPushFailure` returns
  `null` when `targets.Count <= 1`), so any non-`FlowlineException` thrown for a single target
  reached `Program.cs`'s raw-dump `default` case regardless of standalone vs. project mode. Fixed by
  adding a `packageid` precondition check — throwing `FlowlineException` directly — to all three
  classic-assembly write paths (`PluginService.GetOrRegisterAssemblyAsync`,
  `PluginService.SyncAssemblyOnlyAsync`), and by widening + fixing the existing mirror-image check in
  `SyncSolutionFromPackageAsync` (previously only checked the package's primary assembly and threw a
  plain `InvalidOperationException`; now checks every assembly in the package and throws
  `FlowlineException`). See `PluginServiceTests.cs`:
  `SyncSolutionAsync_ExistingPackageOwnedAssembly_ThrowsBeforeAnyDataverseWrite`,
  `SyncAssemblyOnlyAsync_ExistingPackageOwnedAssembly_ThrowsBeforeAnyDataverseWrite`,
  `SyncSolutionFromPackageAsync_ExistingClassicAssembly_ThrowsBeforeAnyDataverseWrite`,
  `SyncSolutionFromPackageAsync_SecondaryAssemblyIsClassic_ThrowsBeforeAnyDataverseWrite`.
- **Severity**: medium.
- **Found**: 2026-07-21, during the initial clone/push/sync/sln-add/deploy test pass against `Cr07982`.

## Repro

1. Push a plugin project in **project mode** where the assembly ends up registered as a
   `PluginPackage` (nupkg) — the common/default shape for a project referencing
   `Microsoft.PowerApps.MSBuild.Plugin` with a `PackageId` set, under `PluginPackageMode.Auto`.
2. From a separate folder (no `.flowline`), run standalone mode against the *same* built assembly:
   `flowline push <solution> --pluginFile <path-to-the-same-dll> --dev <url>`.
3. Result: an unhandled `System.ServiceModel.FaultException<OrganizationServiceFault>` with a raw
   .NET stack trace, instead of a clean `FlowlineException` message.

## Actual output

```
System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault>:
Plugin Assembly 91b5f162-d384-f111-ab0e-70a8a5a1c4d0 cannot be exported as it is
part of a Plugin Package. Please export the Package directly.
  at void Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.ThrowIfResponseIsEmpty(...)
  at async Task<OrganizationResponse> ...ServiceClient.ExecuteAsync(...)
  at async Task Flowline.Core.Plugins.PluginService.AddSolutionComponentAsync(...) in PluginService.cs:1396
  at async (ValueTuple<Entity, bool, int> entity) Flowline.Core.Plugins.PluginService.GetOrRegisterAssemblyAsync(...) in PluginService.cs:919
  ... (full Spectre.Console/PushCommand stack below)
```

This is Flowline's documented "unhandled exception" fallback path (`Program.cs`'s
`SetExceptionHandler`, `default` case — dumps the exception via `AnsiConsole.WriteException`) working
as designed for a genuinely *unexpected* exception. The gap is that this exception is not actually
unexpected — it's a foreseeable, reachable Dataverse platform constraint that Flowline could catch
and translate.

## Root cause

`PluginService.AddSolutionComponentAsync`/`GetOrRegisterAssemblyAsync` (`PluginService.cs:919,1396`)
attempts to add a classic-style `pluginassembly` solution component directly. Dataverse rejects this
with a `FaultException` when that assembly is already registered as part of a `PluginPackage` — a
platform rule ("export the Package directly"), not something Flowline's push path checks for before
attempting the write.

## Why it's reachable in practice

Push the same assembly once in project mode (which registers it via `PluginPackage`/nupkg, Flowline's
default `Auto` mode for a project shaped that way), then later try `--pluginFile` standalone against
that same built DLL — e.g. from a CI job or a script that doesn't know the project was already pushed
as a package.

## Suggested fix direction (not attempted)

Before attempting `AddSolutionComponentAsync` for a classic assembly, query whether an assembly with
that name is already registered as part of a `pluginpackage`, and if so throw a friendly
`FlowlineException` naming the conflict and pointing at the package — mirroring the friendly handling
Flowline already has for the *reverse* conflict (pushing a project as a package when it's already
registered classically gives a clean message: `"Assembly 'X' is already registered in Dataverse as a
classic (non-package) assembly — remove it manually before pushing this project as a plugin package.
Automated migration is not supported."` — confirmed working, `PluginService`/`PushCommand`). The
package-conflict direction needs the same treatment; it currently has none.
