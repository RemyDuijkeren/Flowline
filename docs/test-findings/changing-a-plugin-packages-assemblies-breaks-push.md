# Adding or dropping an assembly in an existing plugin package breaks the push

- **Status**: **fixed 2026-07-31, both halves live-verified.** Dropping an assembly clears its blocking
  registrations before the content update, which is the remedy Microsoft documents. Adding an assembly
  now works too: `push` creates the `pluginassembly` record itself when the confirm step expires, then
  writes the content again so the plugin types populate. The platform gap is unchanged — Flowline
  compensates for it rather than fixing it.
- **Severity**: was high — both modes exited 1 *after* the package content write had committed.
- **Found**: 2026-07-29, live, DEV. Investigated and corrected 2026-07-30.

## The premise this finding originally got wrong

The first version of this finding guessed that Dataverse registers only one `pluginassembly` per
`pluginpackage`, and that Flowline's multi-assembly support rested on a false premise. **That is wrong.**
Microsoft documents the opposite, in
[Build and package plug-in code](https://learn.microsoft.com/power-apps/developer/data-platform/build-and-package#dependent-assemblies):

> When you upload your NuGet package, any assemblies that contain classes that implement the `IPlugin`
> interface are registered in the `PluginAssembly` table and associated with the plug-in package.

And it is live-verified: a `.nupkg` containing two plugin-bearing DLLs, pushed when **no package existed
yet**, registered both assemblies under one package:

```
name                packageid           version
Cr07982.Backend     av_Cr07982.Backend  0.0.0.0
Cr07982.ProbeExtra  av_Cr07982.Backend  1.0.0.0
```

Both are also `solutioncomponent` rows (componenttype 91) of the solution. So multi-assembly packages
work, Flowline's support for them is well-founded, and the second DLL genuinely did contain an `IPlugin`
implementation (`public class ProbeExtraPostUpdatePlugin : IPlugin`, SDK-style project, `net462`, signed
with the same key — every documented requirement met).

The real constraint is narrower and only bites on **update** of a package that already exists.

## Failure 1 — adding an assembly to an existing package

Add a `ProjectReference` to a second plugin-bearing project so the `.nupkg` grows from one DLL to two,
then push into a solution whose package already exists:

```
· Package contains 2 plugin-bearing assemblies: Cr07982.Backend, Cr07982.ProbeExtra
✓ Package av_Cr07982.Backend updated
Error: 'Cr07982.Backend' failed to push: Timed out waiting for Dataverse to auto-create plugin
assembly record(s) for: Cr07982.ProbeExtra. Not attempted: Cr07982.LegacyPlugins.
```

Exit 1. Dataverse never creates the record — **this is not a timeout**. The row was still absent when
queried minutes later, and the identical package content registers both assemblies fine when the package
is created from scratch. Raising `PackageAssemblyCheckMaxAttempts` would only make the same wrong
outcome take longer.

The server clearly does re-scan content on update — it just re-scans *within* assemblies it already
knows. Adding a new **class** to an existing assembly works on update (verified: a new `[CustomApi]`
class registered its plugin type on a plain content update, with no version change anywhere). Adding a
new **assembly** does not. Microsoft's documentation says uploading registers "any assemblies that
contain classes that implement the `IPlugin` interface" and carves out no exception for updates, so this
looks like an undocumented platform gap rather than intended behavior.

### It is not about the version number

Worth stating because it is the obvious first guess, and it is wrong. From
[Update a plug-in package](https://learn.microsoft.com/power-platform/developer/howto/cli-create-package#plug-in-package-management):

> The version of the plug-in package or plug-in assembly is not a factor in any upgrade behaviors. You
> can update the version of the plug-in assembly as you need.
>
> The name and version of the plug-in package cannot be changed once created.

So bumping the `.nupkg` version would not make the new assembly register, and the package's server-side
version could not be changed to match even if it did. This matches every observation in this session:
the fixture's `.nupkg` version stayed `0.0.0-alpha.0.4` across a dozen pushes while code changes, new
classes, new Custom APIs and removed classes all applied normally. Content is synced by content, not by
version.

Note this is the opposite of *classic* (non-package) assembly registration, where the assembly version
genuinely is semantic: a build/revision change is an in-place upgrade, while a major/minor change makes
Dataverse treat it as a different assembly and leaves existing steps pointing at the old one
([Assembly versioning](https://learn.microsoft.com/power-apps/developer/data-platform/register-plug-in#assembly-versioning)).
Those rules govern solution import of classic assemblies. They do not carry over to plugin packages.

## Failure 2 — dropping an assembly from an existing package

The reverse direction fails too, for a different reason. Remove the `ProjectReference` so the `.nupkg`
goes back to one DLL, and push while the dropped assembly's plugin type still has a step:

```
Error: 'Cr07982.Backend' failed to push: Unable to delete
'Cr07982.ProbeExtra.ProbeExtraPostUpdatePlugin' plugintype due to 1 step(s) registered on it.
Please delete step registrations and try the operation again.
```

This one is **not a platform bug** — it is documented, and so is the remedy
([Update a plug-in package](https://learn.microsoft.com/power-platform/developer/howto/cli-create-package#plug-in-package-management)):

> If your update removes any plug-in assemblies, or types which are used in plug-in step registrations,
> the update will be rejected. You must manually remove any step registrations that use plug-in
> assemblies or plug-in types that you want to remove with your update.

Flowline already did exactly that for plugin types (KD4/KTD13 — delete a to-be-removed type's steps and
Custom APIs *before* the content update). The mitigation was scoped to assemblies the push still
reflects, so an assembly that disappeared from the package entirely had no plan of its own, nothing
cleared its steps, and Dataverse rejected the whole update.

### Fixed 2026-07-30

`SyncSolutionFromPackageAsync` now queries the assemblies registered to the existing package, treats any
whose name the local `.nupkg` no longer carries as dropped, and clears their blocking registrations —
images, Custom API parameters/properties, steps, then the Custom APIs themselves — before the content
write. Plugin types and the assembly record are left to the content update, which is what removes them,
the same division of labour KD4 already relied on. `--dry-run` previews each dropped assembly, and
`--no-delete` skips the clearing with a warning that the update will be rejected, rather than deleting
behind the flag's back. Custom APIs are attributed to the dropped assembly's own plugin types, never
taken prefix-wide.

Live-verified on the exact repro: the push that previously died on `Unable to delete … due to 1 step(s)`
now reports

```
! Cr07982.ProbeExtra.dll no longer in the package — clearing its registrations so the update can remove it.
✓ Package av_Cr07982.Backend updated
```

and exits 0, with the assembly and its step confirmed gone from Dataverse afterwards and the surviving
assembly untouched. Two regression tests: one that the dropped assembly's step and Custom API are
deleted while its `pluginassembly` record is not, one that an assembly still present in the `.nupkg` is
never mistaken for dropped.

### Fixed 2026-07-31

`push` compares the reflected assembly set against the assemblies registered to the package before the
content write — the same query the drop fix runs, read the other way round. When the confirm step then
expires with an assembly still unregistered, it creates the `pluginassembly` record itself, writes the
package content a second time so the plugin types populate, and reloads the snapshots.

The order is load-bearing. Two content writes with no create between them registered nothing, both
times: the write populates types for assemblies Dataverse already knows about, and it never learns of a
new one from content alone.

No `--force` gates it. The push has no other way to succeed, creating a record is additive, and refusing
would only strand the user. It reports what it registered rather than acting silently.

**The field set, arrived at empirically — this is the part worth not rediscovering.** Two rejections
define it:

| Fields set | Result |
|---|---|
| `name`, `packageid` | `'<assembly>' is not allowed to be registered in full-trust mode, assembly must be registered in isolation.` |
| `+ isolationmode` (Sandbox, 2) | `Unable to load plug-in assembly.` |
| `+ version`, `culture`, `publickeytoken` | Accepted. |

A package-owned row carries no `content` of its own — the bytes live in the package — so Dataverse
resolves *which* DLL in the package the row refers to from the assembly's full identity. A name alone
does not identify it. This is the opposite of the classic path, which deliberately leaves `culture` and
`publickeytoken` unset because there Dataverse reads identity out of the uploaded content field; here
there is no such field to read. `sourcetype` proved unnecessary and is not set.

Only the live gate could find this. The unit tests were green against the insufficient set, because a
mock accepts whatever it is given.

**It genuinely runs.** The probe plugin was changed to throw `InvalidPluginExecutionException
("FLOWLINE-U3-EXECUTED")`, pushed, and a real contact update came back with exactly that message — so
the sandbox loads and executes the type out of the package content for a Flowline-registered assembly.
A no-op plugin passing would have proved nothing: a silently skipped step looks identical.

That makes self-registration strictly better than the delete-and-recreate alternative, which destroys
every assembly, plugin type and step registration in the package and churns every record GUID. The
remaining work is Flowline-side: create the row before the content write, then let the existing
confirm-with-retry path pick the assembly up — a much smaller change than a `--force recreate-package`.

Worth pinning down before implementing: the minimal required field set. The probe set
`version`/`culture`/`publickeytoken`/`sourcetype` alongside `isolationmode` in one go, so only
`isolationmode` is *known* to be required; the rest may be optional or may be overwritten by the content
update anyway.

Either way the failure should not reach the user after the content write has committed, and the message
should stop calling it a timeout.

## Side benefit: this is what finally exercised the refusal branch

Failure 2's setup is the first realistic construction of the shared-package refusal in
`WarnOrphanAssembliesAsync` — a package owning both an orphan (`Cr07982.ProbeExtra`, dropped from the
nupkg) and an assembly that is not an orphan (`Cr07982.Backend`, which the same push registers). It
behaved correctly, refusing to delete a package that would have taken a live assembly with it:

```
! Cr07982.ProbeExtra.dll in environment — no local source. Package av_Cr07982.Backend owns
  assemblies that aren't orphans — not deleting it.
```

The live run also showed the original wording ("owns assemblies this solution doesn't") was inaccurate
for this, the commonest shape — the solution does have `Cr07982.Backend`; it simply isn't an orphan. The
message was corrected as a result.

## Note on DEV state

Restored. The probe project and its `ProjectReference` are gone, the multi-assembly package was deleted
via orphan cleanup, and a clean push recreated `av_Cr07982.Backend` with its single assembly. Verified
afterwards: two unmanaged assemblies (`Cr07982.Backend`, `Cr07982.LegacyPlugins`) and their two steps,
nothing else.
