# Build validation fails with NU5017 on a freshly scaffolded Plugins project under SDK 11

- **Status:** fixed for new scaffolds. Projects scaffolded before the fix need a one-line edit (below).
- **Severity:** high on SDK 11. Every build of the Plugins project that has to pack fails, so `clone`
  stops before writing `AGENTS.md`, `CLAUDE.md` and `deploymentSettings.json`, `pull` stops after
  the export with exit 13. `push plugins` builds the same project, so any build of it that repacks
  fails too (not run on a pre-fix project).
- **Found:** 2026-11-26, round "pull connection reference cleanup" in
  [`../end-to-end-test-log.md`](../end-to-end-test-log.md). Root cause found 2026-11-27.
- **Component:** `src/Flowline/Services/ProjectScaffolder.cs`, the Plugins scaffold after
  `pac plugin init`.
- **Machine:** Linux, .NET SDKs 10.0.403 and 11.0.100, `Microsoft.PowerApps.MSBuild.Plugin` 1.52.1
  (the template floats `1.*`). 2.16.1, the latest on nuget.org, fails the same way.

## Root cause

`pac plugin init` writes `<TargetFramework>net462</TargetFramework>`. The PowerApps plugin targets
(`build/Microsoft.PowerApps.MSBuild.Plugin.targets`, target `GetTfmPublishedFiles`) put the publish
folder into the package and then set `IncludeBuildOutput=false`. Their own comment says that property
must not be set earlier, because it relies on the per-framework step running in a separate project
instance.

SDK 11 changed that. In `NuGet.Build.Tasks.Pack.targets`, `_WalkEachTargetPerFramework` now builds a
project with no `TargetFrameworks` without the `TargetFramework=` additional property, so the step runs
in the same instance as the pack task. A `-v:diag` log shows the pack task receiving the dll and pdb
in `BuildOutputInPackage` together with `IncludeBuildOutput=False` on 11.0.100, and `True` on
10.0.403. With `False` the published files are dropped, the package is empty, and NuGet raises NU5017.

## Why it looked intermittent

- **SDK choice follows the working directory.** The Flowline repo's `global.json` pins 10.0.4xx, so
  every hand build launched from there ran on SDK 10 and passed. Flowline runs `dotnet` in the
  workspace, which has no `global.json`, so it got 11.0.100.
- **Passes on SDK 11 were a skipped pack.** When a package from an SDK 10 build is already in `bin/`
  and the version is unchanged, `GenerateNuspec` is skipped as up to date and the build succeeds,
  shipping that older package even after a source change. A version change (a new commit moves
  MinVer's version) forces the pack, and it fails. Reproduced: SDK 10 build, SDK 11 build passes,
  SDK 11 build with `-p:MinVerVersionOverride=0.0.2` fails.
- **Clean and rebuild make it worse.** A wiped `bin/` and `obj/` fails 6 of 6 times on SDK 11 and
  0 of 6 on SDK 10.

## Fix

The scaffolder rewrites the singular `<TargetFramework>` to `<TargetFrameworks>` right after
`pac plugin init`. With the plural form, SDK 11 runs the per-framework step in its own instance, as
SDK 10 did. The output path (`bin/<Config>/net462/`), the publish folder and the package contents
(`lib/net462/<Name>.Plugins.dll`) are identical on both SDKs. Verified live on SDK 11: a fresh `clone`
built first time, and `pull --bump none` after wiping `bin/` and `obj/` repacked a package with the dll.
A second fresh clone (`FlowlineDeployTest`) then passed `push`, `pull` with the default bump, and
`pack --binaries-from-build`, all building on SDK 11.

## Existing projects

In `Plugins/<Name>.Plugins.csproj`, change `<TargetFramework>net462</TargetFramework>` to
`<TargetFrameworks>net462</TargetFrameworks>`. Flowline does not migrate existing projects.
