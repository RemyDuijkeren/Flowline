# `clone` re-run creates duplicate `Plugins`/`WebResources` projects when the real ones were moved/renamed

- **Status**: fixed — 2026-07-23.
- **Severity**: high — silently pollutes the solution file with duplicate, empty, wrongly-named
  projects on every idempotent re-clone of a project that has legitimately moved/renamed its
  Plugins/WebResources projects (exactly the flexibility `SolutionFileLayout`/multi-project support
  exists to enable). A subsequent `push` would then face a genuine WebResources-candidate ambiguity
  (two projects both matching WebResources signals) or discover a spurious empty plugin project.
- **Found**: 2026-07-23, live, re-running `flowline clone Cr07982` in a workspace whose Plugins and
  WebResources projects were previously moved+renamed (per this same test workspace's own history:
  `Backend`/`LegacyPlugins` for plugins, a renamed WebResources project).

## Repro (pre-fix)

1. In a Flowline project whose Plugins project has been moved/renamed away from the default
   `Plugins/` folder (and/or WebResources moved/renamed away from `WebResources/`) — the exact
   scenario `docs/plans/2026-07-21-001-feat-project-detection-rules-plan.md` was built to support.
2. Run `flowline clone <solution>` again (idempotent re-clone — a supported, expected operation; the
   command prints "Solution already cloned — skipping" etc. for the parts it does handle correctly).
3. Expected: no new Plugins/WebResources scaffolding — the existing, relocated projects are already
   there and already wired into the solution file.
4. Actual: a brand-new `Plugins/Cr07982.Plugins.csproj` and `WebResources/Cr07982.WebResources.csproj`
   got scaffolded (`pac plugin init`, template files written) **and added to the solution file**
   alongside the real, relocated projects.

## Root cause

`CloneCommand.SetupPluginsProjectAsync` and `SetupWebResourcesProjectAsync` each gated their
"already there — skip" decision on a **hardcoded literal folder name** under the project root
(`Directory.Exists(slnFolder/Plugins)` / `File.Exists(slnFolder/WebResources/<Name>.WebResources.csproj)`),
never consulting the solution file. Unlike `push`/`sync`/`deploy` — all of which discover the plugin
and WebResources projects via `SolutionFileLayout` (solution-file membership + content signals, per the
project-detection-rules work) — `clone`'s own scaffolding step never learned that lesson. Once a
project has genuinely moved its Plugins/WebResources folders elsewhere, the literal path is empty or
absent, so the skip check found nothing and proceeded to scaffold + register a second, spurious
project.

## Fix applied

By the time these two setup methods run, `CreateSolutionFileAsync` has already written the `.cdsproj`
entry, so `SolutionFileLayout.LoadAsync(slnFolder)` is loadable at that point. `ExecuteAsync` now loads
the layout once (shared between both setup calls, matching `SolutionFileLayout`'s one-read contract)
and passes it down. Two new `internal static` predicates make the skip decision:

```csharp
internal static bool PluginsProjectAlreadyRegistered(string pluginsFolder, SolutionFileLayout layout) =>
    (Directory.Exists(pluginsFolder) && Directory.EnumerateFiles(pluginsFolder, "*.csproj").Any())
    || layout.PluginProjects.Count > 0;

internal static bool WebResourcesProjectAlreadyRegistered(string webresourcesCsproj, SolutionFileLayout layout) =>
    File.Exists(webresourcesCsproj) || layout.WebResourcesProjectPath is not null;
```

Design decisions (confirmed with the user before implementing, not assumed):
- **OR, not a replacement** — the literal-folder check stays alongside the new solution-file check
  rather than being replaced by it, so a solution-file read that somehow misses a real default-folder
  project still can't cause a duplicate scaffold on top of it.
- **A genuine WebResources tie propagates, not swallowed.** `WebResourcesProjectPath` throws
  `ExitCode.ConfigInvalid` when two candidates tie for the top score (KD4) — that exception is left to
  bubble out of `WebResourcesProjectAlreadyRegistered` and stop clone, rather than being caught and
  falling through to scaffold a third default-named project on top of an already-unresolved ambiguity.
- **A `SolutionFileLayout.LoadAsync` failure for any other reason also stops clone** rather than being
  caught and falling back to the old literal-folder-only check — a solution file clone just wrote that
  can't be read back is itself worth surfacing immediately, not masking.

Regression tests: `CloneCommandTests.cs` — `PluginsProjectAlreadyRegistered_NothingAnywhere_ReturnsFalse`,
`PluginsProjectAlreadyRegistered_DefaultFolderHasAProject_ReturnsTrue`,
`PluginsProjectAlreadyRegistered_MovedAndRenamedElsewhereInSolutionFile_ReturnsTrue` (the exact live
bug shape), the three `WebResourcesProjectAlreadyRegistered_*` mirrors, and
`WebResourcesProjectAlreadyRegistered_GenuineTieInSolutionFile_PropagatesConfigInvalid`. Full suite
green after the fix (923 in Flowline.Tests, 1021 in Flowline.Core.Tests).

## Live re-verification (post-fix)

Rebuilt, reinstalled, re-ran the exact repro (`flowline clone Cr07982 -a` in this test workspace, which
has `Backend/Cr07982.Backend.csproj` + `LegacyPlugins/Cr07982.LegacyPlugins.csproj` as moved/renamed
plugin projects and `src/ClientAssets/Cr07982.ClientAssets.csproj` as the moved/renamed WebResources
project, all already registered in `Cr07982.slnx`): output now reads `Plugins project already there —
skipping` / `WebResources project already there — skipping`, and `git status --short` confirmed
`Cr07982.slnx` and every other tracked file untouched — no duplicate scaffold, no solution-file
mutation.

## Related, separate issue found during re-verification — fixed

The same live re-clone run also created an **untracked** `WebResources/public/` folder (seeded
JS/image files, no `.csproj`) even though the real WebResources project lives at
`src/ClientAssets/`. Root cause: `CloneCommand.SeedWebResourceDistFromSrc` hardcoded
`Path.Combine(slnFolder, "WebResources", "public")` as the seed destination instead of resolving the
WebResources project's actual folder. This didn't corrupt the solution file (no `.csproj`, nothing got
registered) but left stray files nobody builds or pushes, in a project whose WebResources project has
moved. Cleaned up from the test workspace (`rm -rf WebResources`) after confirming.

Fixed: `SetupWebResourcesProjectAsync` now returns the WebResources project's real folder — the
existing one (default location, or resolved via `SolutionFileLayout` for a moved/renamed project) it
detected via the new `internal static` `ResolveExistingWebResourcesFolder`, or the folder it just
scaffolded — and `ExecuteAsync` threads that folder into `SeedWebResourceDistFromSrc` instead of the
seed step guessing the default path itself. `WebResourcesProjectAlreadyRegistered` (used by `push`-side
tests) now delegates to `ResolveExistingWebResourcesFolder` rather than duplicating its OR logic, so
there is one source of truth for "where is the WebResources project" and "does one already exist."

Regression tests: `CloneCommandTests.cs` —
`ResolveExistingWebResourcesFolder_NothingAnywhere_ReturnsNull`,
`ResolveExistingWebResourcesFolder_DefaultFolderHasAProject_ReturnsDefaultFolder`,
`ResolveExistingWebResourcesFolder_MovedAndRenamedElsewhereInSolutionFile_ReturnsThatFolder`. Full
suite green (926 in Flowline.Tests, 1021 in Flowline.Core.Tests).

Live re-verified: rebuilt, reinstalled, re-ran the same repro (`flowline clone Cr07982 -a` in this test
workspace). Output now reads `WebResources/public already populated — skipping` — the seed check
correctly evaluated `src/ClientAssets/public` (the real, already-populated project folder) instead of
creating a fresh `WebResources/public`. `git status --short` confirmed no untracked `WebResources/`
folder and no other changes.
