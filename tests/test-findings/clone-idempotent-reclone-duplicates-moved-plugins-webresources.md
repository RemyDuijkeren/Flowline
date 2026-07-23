# `clone` re-run creates duplicate `Plugins`/`WebResources` projects when the real ones were moved/renamed

- **Status**: not fixed — needs `SetupPluginsProjectAsync`/`SetupWebResourcesProjectAsync`'s skip-check
  to discover via the solution file (like `push`/`sync`/`deploy` already do) instead of a hardcoded
  literal folder name; more than a one-line fix, and has a real design question (which of N plugin
  projects should suppress the skip? does a `SolutionFileLayout` load before the solution file has a
  cdsproj entry on a genuine first clone behave correctly?).
- **Severity**: high — silently pollutes the solution file with duplicate, empty, wrongly-named
  projects on every idempotent re-clone of a project that has legitimately moved/renamed its
  Plugins/WebResources projects (exactly the flexibility `SolutionFileLayout`/multi-project support
  exists to enable). A subsequent `push` would then face a genuine WebResources-candidate ambiguity
  (two projects both matching WebResources signals) or discover a spurious empty plugin project.
- **Found**: 2026-07-23, live, re-running `flowline clone Cr07982` in a workspace whose Plugins and
  WebResources projects were previously moved+renamed (per this same test workspace's own history:
  `Backend`/`LegacyPlugins` for plugins, a renamed WebResources project).

## Repro

1. In a Flowline project whose Plugins project has been moved/renamed away from the default
   `Plugins/` folder (and/or WebResources moved/renamed away from `WebResources/`) — the exact
   scenario `docs/plans/2026-07-21-001-feat-project-detection-rules-plan.md` was built to support.
2. Run `flowline clone <solution>` again (idempotent re-clone — a supported, expected operation; the
   command prints "Solution already cloned — skipping" etc. for the parts it does handle correctly).
3. Expected: no new Plugins/WebResources scaffolding — the existing, relocated projects are already
   there and already wired into the solution file.
4. Actual: a brand-new `Plugins/Cr07982.Plugins.csproj` and `WebResources/Cr07982.WebResources.csproj`
   get scaffolded (`pac plugin init`, template files written) **and added to the solution file**
   alongside the real, relocated projects.

## Root cause

`CloneCommand.SetupPluginsProjectAsync` (`CloneCommand.cs:549-558`) and
`SetupWebResourcesProjectAsync` (`CloneCommand.cs:647-652`) each gate their "already there — skip"
decision on a **hardcoded literal folder name** under the project root:

```csharp
var pluginsFolder = Path.Combine(slnFolder, "Plugins");
...
if (Directory.Exists(pluginsFolder) && Directory.EnumerateFiles(pluginsFolder, "*.csproj").Any())
{
    Console.Skip("Plugins project already there — skipping");
    return;
}
```

```csharp
var webresourcesFolder = Path.Combine(slnFolder, "WebResources");
var webresourcesCsproj = Path.Combine(webresourcesFolder, WebResourcesProjectFileName(solutionName));
if (File.Exists(webresourcesCsproj))
{
    Console.Skip("WebResources project already there — skipping");
    return;
}
```

Unlike `push`/`sync`/`deploy` — all of which discover the plugin and WebResources projects via
`SolutionFileLayout` (solution-file membership + content signals, per the project-detection-rules
work) — `clone`'s own scaffolding step never learned that lesson. Once a project has genuinely moved
its Plugins/WebResources folders elsewhere, the literal `Plugins`/`WebResources` paths are empty or
absent, so the skip check finds nothing and proceeds to scaffold + register a second, spurious
project.

## Suggested fix direction (not attempted)

Before falling back to the literal-folder scaffold-and-skip check, try
`SolutionFileLayout.LoadAsync(slnFolder)` and check whether it already resolves a plugin project /
WebResources project via solution-file membership; only scaffold when genuinely absent from the
solution file, not merely absent from the default folder name. Needs design attention for: what "skip"
means when the solution has multiple plugin projects (the common multi-project case
`SolutionFileLayout` already supports) — presumably any resolved plugin project count > 0 means skip,
since clone's job is "ensure at least one exists," not "ensure exactly the default one exists"; and
confirming `SolutionFileLayout.LoadAsync` behaves sanely when called mid-clone, before the Dataverse
solution's own `.cdsproj` entry might be freshly written (should be fine on a re-clone, since the
cdsproj entry from the first clone is already there — this only fires on re-clone, not first clone,
per `CloneSolutionFromDataverseAsync`'s own existing `File.Exists(cdsprojPath)` early-skip).

## Workaround

None built into Flowline. Manually delete the spurious `Plugins`/`WebResources` folders and revert the
solution-file addition after an affected re-clone (confirmed clean revert via
`git checkout -- Cr07982.slnx` + `rm -rf Plugins WebResources` in this test workspace, since the
duplicate projects hadn't been built/pushed yet).
