# Orphan detection false-flags a live, in-solution nupkg plugin-package assembly — plugin assemblies are matched by a non-portable GUID instead of their (stable) assembly name

- **Status**: root cause **not fixed** (architectural — the cross-environment identity model for plugin
  assemblies), but **mitigated 2026-07-31**: `PluginAssemblyFamilyHandler` is now `HandlerStatus.Guarded`,
  so a default deploy **surfaces the false orphan and does not delete it** (no exit-18 PartialSuccess).
  It is still deleted if the user passes `--force delete-orphans`, so the false-positive is gated, not
  gone. The real fix (match plugin assemblies by name, then the handler is safe to auto-delete) is still
  open. See "Suggested fix direction" below and `HandlerStatus`.
- **Severity**: **high for the deploy/promotion path**, where it is unfixable by the user; **lower on
  the push/DEV path**, where a `sync` refreshes the stale ids (see "DEV vs deploy-target" below).
- **Now fully observed** (updated 2026-07-31 by a real re-deploy of `FlowlineDeployTest`):
  - `flowline drift` (read-only) reports a **live, current, in-solution** package plugin assembly as an
    orphan whose owning package "would delete" — reproduced on two environments and two solutions.
  - A real unmanaged `deploy test` over the already-present solution **acts on it**: the package delete
    is deferred to post-import, then the post-import cleanup **tries to delete the live package and
    fails** — `The PluginPackage(…) component cannot be deleted because it is referenced by 1 other
    components` — because the import just re-populated it. Result: **exit 18 (PartialSuccess)** with
    `1 orphan component couldn't be cleaned up — remove manually via maker portal`, pointing the user at
    their **own live plugin**.
  - So the real-world impact is *not* silent deletion (the dependency protects the package from actually
    being deleted) — it is that **every re-deploy of a nupkg-plugin solution to a target returns exit 18
    and a false manual-cleanup alarm**. Breaks a green CI/CD deploy and misdirects the user.
- **Found**: 2026-07-31, live. First seen on TEST after a real `deploy test`; then reproduced by a pure
  read-only `flowline drift dev` with no deploy involved — which is what rules out a TEST-specific or
  killed-deploy artifact.

## Symptom

`flowline drift dev` (read-only, no deploy) reports the **live, current** Backend plugin package as a
deletable orphan:

```
PluginPackage 89ca7a81-248c-f111-ab0f-6045bd8e2733
    (owns PluginAssembly 'Cr07982.Backend' (93ca7a81-248c-f111-ab0f-6045bd8e2733)) — would delete
```

`Cr07982.Backend` is a current plugin project (in the `.slnx`, committed under
`Solution/src/pluginpackages/av_Cr07982.Backend/`, listed in `Solution.xml` as a RootComponent).
`drift test` reports the same thing against TEST (different live GUID, `10a1719c…`). On DEV the live
classic `Cr07982.LegacyPlugins` is *also* false-flagged; both current projects are.

## Root cause

The orphan diff's "in-solution" set (S_new) is parsed from the on-disk `Solution/src` by
`ComponentClassifier.ParseSolutionXmlComponents` (`src/Flowline.Core/OrphanCleanup/ComponentClassifier.cs`).
For each `RootComponent`, when an `id` GUID is present it is captured **by GUID and the `schemaName` is
ignored** (lines 108-114):

```csharp
if (Guid.TryParse(component.Attribute("id")?.Value, out var id))
{
    components.Add((id, type));
    continue;                       // <-- schemaName (the assembly strong-name) never read when id present
}
```

Plugin assemblies (component type **91**) are recorded in `Solution.xml` with **both** a GUID id and
their strong-name `schemaName`:

```xml
<RootComponent type="91" id="{31d733bd-e984-f111-ab0e-70a8a5a1c4d0}"
    schemaName="Cr07982.Backend, Version=0.0.0.0, Culture=neutral, PublicKeyToken=48c2f23af73ee643" ... />
```

So S_new holds the assembly by its **on-disk GUID only**, and the orphan diff marks any live
`pluginassembly` whose GUID isn't in S_new as an orphan (redirecting a package-owned assembly to a
`pluginpackage`-delete finding in `PluginAssemblyFamilyHandler`).

**The GUID is not a stable identity for a plugin assembly across environments.** Measured live
2026-07-31, the Backend assembly's `pluginassemblyid` is different in every place:

| Source | `Cr07982.Backend` id | how it got there |
|---|---|---|
| on-disk `Solution.xml` (S_new) | `31d733bd-e984-f111…` | last `sync`/unpack committed to `Solution/src` |
| DEV live | `93ca7a81-248c-f111…` | `push` re-registers the assembly, minting a new id |
| TEST live | `10a1719c-cf8c-f111…` | solution `import` re-mints the package's assembly id |

All three differ, so the on-disk id matches no live environment → the live assembly is flagged
everywhere. The assembly **name** (`Cr07982.Backend`), by contrast, is identical in all four places and
is sitting unused in the `schemaName` attribute.

`ComponentClassifier` already reconciles **by name** every other component whose id isn't portable —
WebResource/Entity/OptionSet/Role via `schemaName`/`NamedComponents`, CustomApi/Bot/ConnectionReference
via dedicated name scans. Plugin assemblies are the one non-portable type still matched by GUID, only
because they *carry* a GUID id in `Solution.xml` and thus take the GUID branch.

## Membership is the discriminator (why only current projects are flagged, not every stale assembly)

Verified via `solutioncomponent` query on TEST (solution `Cr07982`, id `00000001-…-009b`): the flagged
Backend assembly `10a1719c` and its package `a24e152d` **are** members of the solution. The two other
live package assemblies on TEST (`Cr07982.Plugins`/`av_Cr07982.Plugins` `7f6078a2`, `Plugins`/`av_Plugins`
`856078a2`) are env-level leftovers **not** in the solution, so orphan detection never considers them —
they show only as "not tracked, no action taken" and are correctly left alone. So the bug specifically
hits **live, current, in-solution** package assemblies, not random environment cruft.

## Cleanest reproduction — a brand-new normal solution (FlowlineDeployTest, 2026-07-31)

To remove the "single fixture with an abnormal default-solution identity" caveat, the whole flow was
repeated on a **fresh, normal** solution created for the purpose:

1. Created unmanaged `FlowlineDeployTest` in DEV (normal solutionid `31232c5e-6aff-4ff5-b401-aad58b276d96`,
   **not** a default-solution id) via `pac solution import` of a minimal zip.
2. `flowline clone` → added one `[Step("account")]` plugin (`AccountPostCreatePlugin`) + one web resource
   → `push` (registered package `av_FlowlineDeployTest.Plugins`) → `sync` (1.0.1, captured to
   `Solution/src` with DEV's assembly id `14c3bcbb-d88c-f111`) → commit.
3. `deploy test --force first-import` → **clean first import**, checker 0 findings, "No solution
   components — skipping orphan check" (correct: nothing there yet), exit 0.
4. Immediately after, read-only `drift test` reports **exactly one** orphan — the solution's own live
   plugin package — and nothing else:

```
Orphan components (1):
  Prio1 — blocks deployment:
    PluginPackage a4ee77fc-b0a4-4718-8db9-43298593644f
        (owns PluginAssembly 'FlowlineDeployTest.Plugins' (1ec43993-d98c-f111-8076-3833c5c9dcb3)) — would delete
```

Live TEST assembly id `1ec43993-d98c-f111` (minted at import) ≠ committed `14c3bcbb-d88c-f111` (DEV's,
from `sync`). No genuine orphans exist on this solution, so this is the false positive in isolation —
a clean, normal solution false-flags its own just-imported plugin package on the very next drift. This
reproduces the finding independent of the Cr07982 fixture and its default-solution identity.

## DEV (push) vs deploy-target — why this is fixable only in Flowline, and only matters for deploy

- **On DEV / push path**: the on-disk `Solution/src` is simply stale relative to DEV's re-registered
  ids, and a `sync dev` would export DEV and rewrite `Solution.xml` with DEV's current ids, after which
  `drift dev` matches and is clean. That is the already-documented "pushed but not yet synced shows as
  drift" behavior — arguably not a bug on its own.
- **On a deploy target (TEST/UAT/PROD)**: there is **no** remedy. You cannot `sync` a deploy target
  (`deploy dev` is rejected; targets are import-only), and even a freshly-synced `Solution.xml` carrying
  DEV's id will still not match the target's own import-minted id. So a nupkg-plugin solution deployed to
  a target will, on the next `drift`/`deploy` against it, **always** false-flag its own package, and a
  real re-`deploy` **always exits 18 PartialSuccess** on the failed cleanup (see "failure mode" below).
  The only real fix is to stop matching plugin assemblies by GUID.

## Suggested fix direction (not attempted — architectural)

Reconcile live plugin assemblies against source **by name**, not GUID:

1. In `ParseSolutionXmlComponents`, for type-91 RootComponents that carry a `schemaName`, additionally
   harvest the simple assembly name (strong-name up to the first comma, e.g. `Cr07982.Backend`) into a
   name-keyed local identifier set — alongside, not instead of, the GUID.
2. In the orphan diff (`OrphanCleanupService.CompareAsync` / `BuildLocalIdentifierHarvest`), resolve
   each live `pluginassembly` candidate's `name` and treat it as in-solution if that name is in the
   committed set — mirroring the existing WebResource/CustomApi/Bot name reconciliation.

Why not inline: this changes the identity model for plugin assemblies and has high blast radius — too
loose and a *genuine* orphan (a renamed-away package, the old `Plugins`) stops being detected; wrong
name extraction and a live plugin is deleted. Needs its own design + regression tests (package vs
classic, renamed-away still detected, same-name/different-public-key-token cases).

## Failure mode precision (for the fixer)

Observed order on a real re-deploy to a target that already holds the package (`FlowlineDeployTest`,
exit 18): mark live package for delete → **deferred** to post-import (dependency) → import re-populates
the package → post-import cleanup attempts the package delete → **Dataverse refuses it**
(`cannot be deleted because it is referenced by 1 other components` — the plugin type the import just
recreated) → `1 orphan component couldn't be cleaned up — remove manually`, **exit 18 PartialSuccess**.

So the package is *not* actually deleted (the live child protects it), but:
- **Every re-deploy of the solution exits 18** with a false "couldn't clean up orphan, remove manually
  via maker portal" pointing at the user's own live plugin — breaks CI/CD green and misleads.
- Under `--no-delete`/managed it instead persists as permanent false drift (`drift` exits 15 forever on
  a cleanly-promoted environment).
- Note this also means deploy's post-import cleanup has the **same package-owned-delete weakness** that
  `push` had before `push-delete-orphans-fails-on-package-owned-assembly.md` was fixed — but here the
  right fix is upstream (don't flag it at all via name matching), since the "orphan" is the live package
  and deleting it is never actually desired.

## Reproducibility caveats

- The real-deploy behavior is now **observed**, not inferred: a real re-`deploy test` of
  `FlowlineDeployTest` acted on the false orphan and exited 18 PartialSuccess (see "Failure mode"). The
  earlier inferred "delete-then-reimport churn" was **wrong** — the delete is dependency-blocked and
  fails, so the plugin survives; the real damage is the exit code + false alarm.
- **Resolved:** originally seen on `Cr07982`, which has an abnormal default-solution identity
  (solutionid `00000001-…-009b`). Now also reproduced on `FlowlineDeployTest`, a fresh **normal**
  solution (see "Cleanest reproduction" above), so the default-solution identity is ruled out as a
  cause. Reproduced on **two** solutions and **two** environments (`drift dev`, `drift test`).
- Distinct from the earlier `false-positive-orphan-dotted-classic-assembly-name` finding (that was the
  push/local-build path resolving unpacked DLL names); this is the `Solution.xml`-GUID matching path.
