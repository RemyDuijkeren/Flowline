---
title: cdsproj ProjectReference builds a project together but does not inject its assembly unless the solution already registers it
date: 2026-07-21
category: docs/solutions/tooling-decisions
module: clone / SolutionFileLayout
problem_type: tooling_decision
component: tooling
severity: medium
applies_when:
  - Considering a cdsproj-to-plugin/WebResources ProjectReference (the Scott Durow / Contoso ALM pattern) as an explicit solution-membership signal for project detection
  - A `dotnet build` of a `.cdsproj` fails with "Unable to find assembly registration configuration ... in the destination: obj\Release\Metadata\PluginAssemblies"
  - A cdsproj ProjectReference fails with MSB4057 "The target GetProjectOutputPath does not exist"
  - A cdsproj ProjectReference to a modern-TFM project fails restore with NU1201 (framework incompatibility)
  - Asking whether the pac-generated `.cdsproj` can target net8/net10 instead of net462
  - Referencing a NuGet-packaged plugin project (`PluginPackageMode.Nupkg`/`Auto`) from a `.cdsproj`
  - Expecting a `.cdsproj` build to pick up a plugin's freshly rebuilt bytes without a resync
  - Needing the packed zip's plugin assembly to reflect local build output instead of the last-synced Dataverse content (the actual job for `pac solution pack --map`)
tags:
  - cdsproj
  - project-reference
  - powerapps-msbuild-solution
  - plugin-assembly-registration
  - plugin-package
  - nupkg
  - solution-packager
  - solution-packager-map-file
  - membership-signal
  - project-detection
  - msbuild
  - net462
  - contoso-alm
---

# cdsproj ProjectReference builds a project together but does not inject its assembly unless the solution already registers it

## Context

Flowline resolves which projects belong to a Dataverse solution by reading the `.slnx`/`.sln`
membership list and classifying entries (`SolutionFileLayout` and its per-type resolvers). In a
crowded repo the WebResources classification falls back to elimination + weighted signals, which is
inherently heuristic (see
[SolutionFileLayout — consolidating three ad-hoc project resolvers](../architecture-patterns/solutionfilelayout-project-detection-consolidation.md)).

A tempting alternative — used by Scott Durow's Microsoft ALM Contoso sample — is to add a normal
dotnet `<ProjectReference>` from the `.cdsproj` to the plugin/WebResources projects. It promises two
things at once: an **explicit, definitive membership declaration** (no heuristics) and
**build-together** ergonomics. Before adopting it, the mechanism was verified empirically against
`pac` 2.9.3, .NET SDK 10.0.302, and `Microsoft.PowerApps.MSBuild.Solution`/`.Plugin` **1.52.1**. The
result splits the two promises apart: the membership half is real, the build-together half carries a
hard precondition that does not fit Flowline's model.

Re-verified against the current latest release of both SDKs, **2.9.3** (2026-07-10) — identical
failure text, identical target, identical line number (`Microsoft.PowerApps.MSBuild.Solution.targets:102`).
Nothing about this changed across the entire 2.x line (2.1.1 → 2.9.3, all of 2026); it is not a bug
that got fixed in a newer release.

## Guidance

### A cdsproj ProjectReference builds the referenced project, but pack-injection is conditional

Adding `<ProjectReference Include="MyPlugin\MyPlugin.csproj" />` to the cdsproj and running
`dotnet build -c Release` **does** build the plugin as part of the cdsproj build. It does **not**
freely add the assembly to the packed `.zip`. The SDK's `ProcessCdsProjectReferencesOutputs` target
(`Microsoft.PowerApps.MSBuild.Solution.targets:97-103`) copies the built output into a metadata
working dir and expects a **pre-existing plugin-assembly registration** to slot it into. With a
freshly `pac solution init`'d solution — whose `Customizations.xml` carries an empty
`<SolutionPluginAssemblies />` — there is nothing to slot into, and the build fails hard:

```
error : Unable to find assembly registration configuration for ...\MyPlugin.dll
        in the destination: obj\Release\Metadata\PluginAssemblies
```

So injection requires the **unpacked solution to already register the assembly** (a `PluginAssembly`
component in the committed solution XML) — but "requires" is a precondition to a successful build,
not a description of what ends up in the zip. It never creates the registration, and it does **not**
refresh the binary either — see the correction below.

**Correction, verified by hash comparison across two builds:** the packed zip's plugin `.dll` is a
byte-for-byte copy of whatever already sits in `Solution/src/PluginAssemblies/<name>/<name>.dll`
*before* the build starts — not the referenced project's fresh build output. Verified directly: seed
`src/PluginAssemblies/.../Cr07982LegacyPlugins.dll` with the current build's bytes (hash A), change the
plugin's source, `dotnet build` the cdsproj clean (succeeds — the referenced project's *new* build
output hashes to B ≠ A), then extract the packed zip and hash the `.dll` inside it: it is **A**, not
B. The reference-then-build cycle validates assembly identity (name/version/culture/public-key-token)
and requires the old file to still physically exist; it does not overwrite it with what was just
built. Getting a fresh build into the zip still requires the normal loop — register/push the new
build to Dataverse, then resync (`pac solution unpack` / `flowline sync`) so `src/PluginAssemblies/`
itself is updated — or use `pac solution pack --map` (see below) to point the pack step directly at
the build output. This is why the Contoso flow *looks* like it refreshes the binary on every build:
in practice, each build is preceded by a resync from an org where the just-registered plugin already
landed, not by the `ProjectReference` doing the refreshing.

### No ProjectReference attribute skips the injection check

`ReferenceOutputAssembly="false"` does **not** help — the injection target enumerates the raw
`@(ProjectReference)` item list and calls `GetProjectOutputPath` on each, ignoring that metadata.
There is no `Condition` on the target and no documented property gating it.

The only reliable skip is to **override the target with a no-op** in the cdsproj (last definition
wins, so place it before `</Project>`):

```xml
<!-- build-order coupling only: never inject a referenced assembly into the pack -->
<Target Name="ProcessCdsProjectReferencesOutputs" BeforeTargets="PowerAppsPackage" />
```

Verified: build succeeds, the referenced project still builds together, the solution still packs,
and the DLL is **not** in the zip (only `customizations.xml`, `solution.xml`,
`[Content_Types].xml`). This is the universal neutralizer — it works for plugin *and* WebResources
references.

### A non-plugin reference does NOT pass by default — it errors

An earlier assumption that a NoTargets/WebResources project "references harmlessly because it has no
`GetProjectOutputPath`" is **false**. The SDK calls that target on every reference and errors when it
is missing:

```
error MSB4057: The target "GetProjectOutputPath" does not exist in the project.
```

So a WebResources reference needs the neutralizer target too — it is not exempt.

### A NuGet-packaged plugin (`PluginPackage`) never works, registered or not

The classic-assembly case above at least *builds* once the assembly is already registered. A plugin
project packaged as a `PluginPackage` nupkg (`PluginPackageMode.Nupkg`, or `Auto` resolving to nupkg —
Flowline's own default whenever the project produces one) **always** fails the reference, regardless
of registration state:

```
error : Unable to find assembly registration configuration for ...\MyPlugin.dll
        in the destination: obj\Debug\Metadata\PluginAssemblies
```

Same error text as the "not yet registered" case, but the cause is structural, not a missing sync:
the SDK's injection target only ever looks under `Solution/src/PluginAssemblies/` (the classic,
non-package registration format). A `PluginPackage` lives in a completely different unpacked-solution
location, `Solution/src/pluginpackages/<name>/`, which this target never inspects — so there is no
registered/synced state that would make the reference succeed. Confirmed unfixed on the current
latest release, `Microsoft.PowerApps.MSBuild.Solution`/`.Plugin` **2.9.3** — identical error, same
line number, as on 1.52.1.

Practical corollary: since Flowline defaults new plugin projects to `PluginPackageMode.Auto`, which
resolves to nupkg for any project that produces one (the common shape for a project referencing
`Microsoft.PowerApps.MSBuild.Plugin` with a `PackageId` set), a cdsproj `ProjectReference` to a
Flowline-scaffolded plugin project will typically hit *this* failure, not the "just needs a sync"
one.

The neutralizer target above (`ProcessCdsProjectReferencesOutputs` overridden to a no-op) fixes this
case too — verified. It overrides the exact target that performs the check, so it never runs
regardless of *why* it would have failed; there is nothing nupkg-specific for it to trip on once the
check itself doesn't execute.

### The actual mechanism for fresh injection: `pac solution pack --map`

If the goal is "the packed zip reflects my local build output," `ProjectReference` is not the tool —
`pac solution pack`/`unpack`'s `--map`/`-m` option is. It takes a mapping XML that redirects specific
unpacked-solution files to an alternate location at pack time:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Mapping>
    <FileToFile map="PluginAssemblies\**\MyPlugin.dll" to="..\MyPlugin\bin\Release\MyPlugin.dll" />
</Mapping>
```

`pac solution pack -z out.zip -f unpacked_folder -m map.xml` then reads `MyPlugin.dll`'s content from
the mapped build-output path instead of the unpacked folder — this is what actually gets a fresh local
build into the zip without a registration/resync round-trip first. It operates on a raw `pac solution
pack` invocation, not on the `.cdsproj`'s own `dotnet build`/`ProjectReference` graph — there is no
evidence the `Microsoft.PowerApps.MSBuild.Solution` SDK's `dotnet build` path exposes an equivalent
hook. Flowline doesn't use this mechanism at all: `push`/`sync` register plugins directly against
Dataverse via the Web API and pull the result back down, bypassing the packed-zip path entirely.

### A cross-TFM reference fails restore; bypass with SkipGetTargetFrameworkProperties

Referencing a modern-TFM project (e.g. a `net8.0` NoTargets WebResources project) from the `net462`
cdsproj fails at restore:

```
error NU1201: Project WebRes is not compatible with net462. Project WebRes supports: net8.0
```

NuGet enforces TFM compatibility on project references. Bypass it per-reference with
`SkipGetTargetFrameworkProperties="true"`. Full working shape for a build-order-only, no-inject,
cross-TFM reference (verified to build and pack clean):

```xml
<ProjectReference Include="WebRes\WebRes.csproj"
                  ReferenceOutputAssembly="false"
                  SkipGetTargetFrameworkProperties="true" />
<!-- plus, once, in the cdsproj: -->
<Target Name="ProcessCdsProjectReferencesOutputs" BeforeTargets="PowerAppsPackage" />
```

### The pac-generated .cdsproj cannot meaningfully target net8/net10

The `.cdsproj` is an **old-style MSBuild project** (`ToolsVersion="15.0"`, the 2003 xmlns, imports
`Microsoft.Common.props/targets` and `Microsoft.PowerApps.VisualStudio.Solution.targets`). Old-style
projects are driven by `<TargetFrameworkVersion>v4.6.2</TargetFrameworkVersion>`, **not** by
`<TargetFramework>netX</TargetFramework>`:

- Setting `<TargetFramework>net10.0</TargetFramework>` while `TargetFrameworkVersion` is present →
  the net10 value is ignored; the project still builds as net4.6.2.
- Removing `TargetFrameworkVersion` to force net10 → `MSB3644: reference assemblies for
  .NETFramework,Version=v4.0 were not found`. `TargetFramework` never takes over because it is not an
  SDK-style project.

It is also moot: the cdsproj **compiles nothing** (`CoreCompile` is stubbed by the SDK at
`Microsoft.PowerApps.MSBuild.Solution.targets:48`), produces no assembly, and only runs
SolutionPackager to zip the solution XML. Its TFM is a formality. Retargeting would mean rewriting
the cdsproj SDK-style, which the PowerApps solution SDK's import model is not built for — and it buys
nothing. (Plugin projects must stay net462 regardless: the Dataverse plugin sandbox runs .NET
Framework.)

## Anti-pattern

Adopting the Contoso cdsproj-ProjectReference pattern wholesale, expecting "reference → assembly
appears in the solution zip" for free. In Flowline's model the assembly registration lives in
Dataverse and is applied out-of-band by `push` (derived from `[Step]` attributes), not necessarily
in the committed solution XML. A naive cdsproj ProjectReference then **breaks the root
`dotnet build`** with the missing-registration error rather than helping. If the pattern is adopted,
it must be as a **membership signal only** — reference present, injection neutralized — never as the
pack mechanism.

## Trade-off for the detection decision

Using cdsproj ProjectReferences as an explicit membership signal is viable and would retire the
WebResources heuristic's guesswork, but the cost is now fully mapped: per reference,
`ReferenceOutputAssembly="false"` (+ `SkipGetTargetFrameworkProperties="true"` when cross-TFM), plus
one neutralizer `<Target>` written into the cdsproj — a single override that turns out to cover every
case tested (classic plugin, nupkg plugin, WebResources). That is real editing of a `pac`-generated
file, in tension with the "pac owns its files, Flowline only places them" principle
(`docs/plans/2026-07-20-001-refactor-project-layout-naming-plan.md` KD2). The current
membership-by-`.slnx` + elimination approach needs zero cdsproj edits. The reference approach buys
certainty; it costs three non-obvious MSBuild incantations in a vendor file, and still doesn't get you
"fresh build in the zip" (that's `pac solution pack --map`, a separate and unrelated mechanism).
Recorded here so the decision is made against the verified mechanism, not the marketing of "builds
together automatically."
