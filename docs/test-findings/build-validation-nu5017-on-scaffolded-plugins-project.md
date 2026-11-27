# Build validation fails with NU5017 on a freshly scaffolded Plugins project

- **Status:** open, not fixed inline
- **Severity:** medium. It is intermittent: 2 of 4 Flowline builds on one fresh clone failed.
  `clone` then stops before writing `AGENTS.md`, `CLAUDE.md` and `deploymentSettings.json`, and
  `pull` stops after the export with exit 13.
- **Found:** 2026-11-26, round "pull connection reference cleanup" in
  [`../end-to-end-test-log.md`](../end-to-end-test-log.md). First seen in an earlier session's `clone`
  smoke the same day.
- **Component:** the build step `clone` and `pull` share (`src/Flowline/Utils/DotNetUtils.cs`,
  `BuildArguments`), against the template Plugins project.
- **Machine:** Linux, .NET SDKs 10.0.403 and 11.0.100 installed; no `global.json`, so SDK 11.0.100
  builds.

## Repro

1. Empty folder with `git init` and one commit.
2. `flowline clone <solution> --env <dev-url>` for a solution with no plugin assemblies.
3. Observed: `NuGet.Build.Tasks.Pack.targets(232,5): error NU5017: Cannot create a package that has no
   dependencies nor content.` on `Plugins/<Name>.Plugins.csproj`, then `✗ Build failed`, exit 13.
4. Re-run the same `clone`: the build passes in 2s and clone completes, exit 0.
5. `flowline pull` (default bump, build on) on that clone: the same NU5017 after the export, exit 13.
6. Every hand build succeeded:
   - Right after the failure: `dotnet build <Name>.slnx`, the same with `--configuration Debug
     -t:Rebuild`, and a copy of `Plugins/` built alone under SDK 10.0.403 and under 11.0.100.
   - After wiping every `bin/` and `obj/`: `dotnet build <Name>.slnx --configuration Debug`.
   - After changing the version in `Solution.xml`, which is what a bump does.
7. A later `flowline pull --bump none` with the build on, on the same clone, also passed (exit 0).

So the failure does not follow from Flowline's arguments, from a cold `obj/`, or from a version
change alone. It is intermittent. The branch it was found on (`feat/pull-tidy-connrefs`) changes no
build, template or `dotnet` invocation code.

## Root cause

Not established. In the clone log, `<Name>.Plugins -> .../bin/Debug/net462/publish/` is printed and
the pack fails about 100 ms later. That points at the pack step that
`Microsoft.PowerApps.MSBuild.Plugin` runs against the publish folder, and at the state that `clone`
or `pull` leaves just before the build. The `pull` run that failed had just written a new version
(1.0.1) into `Solution.xml`. Neither a cold build nor a version change reproduces it by hand. The
remaining suspects are timing in the publish-then-pack sequence and something in the environment
that Flowline's child process gets.

## Suggested direction

Loop a hand build of a fresh clone (wipe `bin/` and `obj/` each time) to see whether it ever fails
outside Flowline. If it does, it is a tooling race in the PowerApps plugin pack targets under SDK 11,
not a Flowline bug. If it never does, diff the environment Flowline passes to its child `dotnet`
against the shell's.

Not fixed inline because the root cause is unknown, and it sits in the build tooling shared by every
command rather than in the slice under test.

## Workaround

Re-run `clone`, or run `pull --no-build`.
