# Multi-assembly plugin package: push fails after writing package content, second assembly never registers

- **Status**: not fixed — the premise itself needs checking (does Dataverse register more than one
  `pluginassembly` per `pluginpackage` at all?), which is investigation, not a small fix.
- **Severity**: high — `push` exits 1 *after* the package content write has already committed, so the org
  keeps a package whose second DLL Dataverse ignores, the second assembly's plugin types and steps never
  land, and every later project in the same push is skipped ("Not attempted").
- **Found**: 2026-07-29, live, DEV, tool built from `b79642a`.

## Repro

Give a nupkg-mode plugin project a `ProjectReference` to a second plugin-bearing assembly — the shape
KD5 in the nupkg plan describes as the way a package comes to hold more than one DLL:

```xml
<ItemGroup>
  <ProjectReference Include="..\ProbeExtra\Cr07982.ProbeExtra.csproj" />
</ItemGroup>
```

The second project is a plain net462 class library, strong-named with the same key (a signed assembly
cannot reference an unsigned one), with one `IPlugin` class carrying a `[Step]`. It is deliberately
*not* in the `.slnx`, so it is not discovered as a plugin project of its own.

The package packs correctly — both DLLs land side by side:

```
lib/net462/Cr07982.Backend.dll
lib/net462/Cr07982.ProbeExtra.dll
```

`flowline push --scope plugins`:

```
· Assembly Cr07982.ProbeExtra (1.0.0.0) analyzed
· Package contains 2 plugin-bearing assemblies: Cr07982.Backend, Cr07982.ProbeExtra
✓ Package av_Cr07982.Backend updated
Error: 'Cr07982.Backend' failed to push: Timed out waiting for Dataverse to auto-create plugin
assembly record(s) for: Cr07982.ProbeExtra. Not attempted: Cr07982.LegacyPlugins.
Fix 'Cr07982.Backend', then push again.
```

Exit 1. Note the ordering: `Package av_Cr07982.Backend updated` succeeded first, so the two-DLL package
is now live in the org.

## Root cause, as far as it is understood

`SyncSolutionFromPackageAsync` reflects every plugin-bearing DLL under `lib/<tfm>/` (KD5), writes the
package content, then waits for Dataverse to auto-create one `pluginassembly` per reflected assembly —
a bounded retry of `PackageAssemblyCheckMaxAttempts` (5) × `PackageAssemblyCheckDelay` (1s) in
`src/Flowline.Core/Plugins/PluginService.cs`. Dataverse created the record for the primary assembly
only.

**This is not a timeout.** The record was still absent when queried several minutes later:

```
name                   packageid
Cr07982.Backend        av_Cr07982.Backend
Cr07982.LegacyPlugins  (none)
```

So either:

1. **Dataverse registers exactly one `pluginassembly` per `pluginpackage`** — the primary — and treats
   every other DLL in `lib/<tfm>/` as a runtime dependency, however plugin-bearing it is. If so, the
   multi-assembly package support (KD5/KTD15 — N independently-scoped snapshots, `SiblingAssemblyNames`
   taking a set, `CollectPushedAssemblyNames` reaching into `ReflectedAssemblies`) rests on a premise
   that does not hold, and the honest behavior is to reject or warn at reflection time rather than write
   the package and then fail.
2. Or the probe assembly was missing something Dataverse requires that this repro did not supply, and
   the feature works with the right packaging.

Distinguishing the two is the investigation this finding asks for. Nothing here should be "fixed" until
it is settled — the failure mode is a wrong premise, not a wrong retry count, and raising the retry
count would just make the same wrong outcome take longer.

## Consequence for the package-owned orphan work

If (1) holds, a `pluginpackage` always owns exactly one `pluginassembly`, which means the
shared-package refusal branch in `WarnOrphanAssembliesAsync` — where a package owns an orphan *and*
something the run cannot account for — is unreachable in a real org and exists purely as a safety
guard. That is the branch
`docs/test-findings/push-delete-orphans-fails-on-package-owned-assembly.md` records as unit-tested only;
this is why it could not be constructed live. The guard should stay either way: it costs one query and
it is what stops a delete from taking a live assembly with it.

## Suggested fix direction

Settle the premise first. If Dataverse really is one-assembly-per-package, the reflection step should
say so plainly when it finds a second plugin-bearing DLL — before the content write, not after — and the
multi-assembly machinery becomes dead weight worth deleting. If instead the packaging was at fault, the
repro above is the test case, and the retry's failure message should distinguish "Dataverse has not
created it yet" from "Dataverse is never going to create it".

Independently of which: the content write should not commit before the push knows it can finish, or the
failure message should say that the package in the org now contains a DLL that did not register.

## Note on DEV state

DEV was restored: the `ProjectReference` was reverted, the probe project deleted, and a clean
`push --scope plugins` re-wrote `av_Cr07982.Backend` back to its single-assembly content. Verified
afterwards — two unmanaged assemblies (`Cr07982.Backend`, `Cr07982.LegacyPlugins`), their two steps, and
no leftover probe records.
