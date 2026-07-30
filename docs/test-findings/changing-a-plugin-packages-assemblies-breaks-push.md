# Adding or dropping an assembly in an existing plugin package breaks the push

- **Status**: not fixed — the platform constraint is now understood, but choosing what `push` should do
  about it (refuse early, or delete and recreate the package) is a product decision.
- **Severity**: high — both failure modes exit 1 *after* the package content write has committed, and the
  "add" case's message blames a timeout that will never resolve no matter how long it waits.
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

Likely mechanism, consistent with Microsoft's note that "you can't change the name and version of the
plug-in package (on the server) once created" and with KTD4 (Flowline omits `version` on update because
it is create-time-only): the server re-syncs plugin *types* inside already-registered assemblies on a
content update, but never enumerates *new* assemblies.

## Failure 2 — dropping an assembly from an existing package

The reverse direction fails too, for a different reason. Remove the `ProjectReference` so the `.nupkg`
goes back to one DLL, and push while the dropped assembly's plugin type still has a step:

```
Error: 'Cr07982.Backend' failed to push: Unable to delete
'Cr07982.ProbeExtra.ProbeExtraPostUpdatePlugin' plugintype due to 1 step(s) registered on it.
Please delete step registrations and try the operation again.
```

Flowline already mitigates exactly this hazard for plugin types (KD4/KTD13 — delete a to-be-removed
type's steps and Custom APIs *before* the content update). The mitigation is scoped to assemblies the
push still reflects, so an assembly that disappears from the package entirely takes its steps with it
into the content write, and Dataverse rejects the whole update.

## What does work

Deleting the package and letting the next push recreate it. Both failures above were recovered that way:
drop the project from the solution file so its assemblies become orphans, `push --force delete-orphans`
(which now deletes the owning package — see
`push-delete-orphans-fails-on-package-owned-assembly.md`), restore the solution file, push again.

## Suggested fix direction

Detect the mismatch *before* the content write, by comparing the reflected assembly set against the
assemblies already registered to the package. Then either:

- **(a)** refuse with a message naming the added or dropped assembly and telling the user the package has
  to be recreated, which is honest and cheap; or
- **(b)** do the recreate itself — delete the package and create it fresh — which is what a user
  currently has to do by hand, but it is a destructive act that deserves a `--force` specifier of its own.

Whichever is chosen, failure 2's step-cleanup should extend to assemblies that vanish from the package,
not just types that vanish from a surviving assembly. And neither failure should reach the user after the
content write has already committed.

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
