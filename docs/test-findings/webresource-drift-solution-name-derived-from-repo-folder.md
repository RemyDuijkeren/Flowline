# Web-resource drift reported every file twice whenever the repo folder isn't named after the solution

- **Status**: **fixed** 2026-07-29 (uncommitted in the source working tree — awaits commit
  authorization). Regression tests added, full suite green, live re-verified.
- **Severity**: high — `sync` reported phantom drift for every web resource, and `deploy` *blocked* on
  it, forcing `--force drift` (which suppresses the check that would catch real drift).
- **Found**: 2026-07-29, live, `flowline sync` in `C:\Code\FlowlineTryOutByClaude` (solution `Cr07982`).

## Repro

Any Flowline project whose repo folder name differs from the Dataverse solution unique name — the
normal case, since `clone` runs inside a folder the user already named:

```
C:\Code\FlowlineTryOutByClaude\    <- repo folder
  .flowline                        <- Solution.UniqueName = "Cr07982"
  ClientAssets/dist/example1.js    <- built web resources
  Solution/src/WebResources/av_Cr07982/example1.js   <- what pac unpacks
```

```
flowline sync
```

Observed — every web resource reported twice, in both directions at once:

```
! Dataverse doesn't match local Plugins / WebResources:
  - 'av_Cr07982\example.js' added in Dataverse — add to local WebResources, or push to remove
  - 'av_Cr07982\example1.js' added in Dataverse — add to local WebResources, or push to remove
  … (one per file, plus images\claude-v1.png)
  - 'example.js' local only, not in Dataverse — push to upload
  - 'example1.js' local only, not in Dataverse — push to upload
  … (the same files again, unprefixed)
```

…on a repo that had just been pushed and synced, with byte-identical content on both sides.

`deploy` filters the same warnings to `OnlyLocal`/`PluginSizeMismatch` and throws
`ExitCode.ValidationFailed` (`DeployCommand.cs:453-455`) unless `--force drift` is passed, so a clean
repo could not deploy without bypassing drift detection entirely. (Consistent with the earlier session
recorded in `docs/test-goal.md`, which reached a successful `deploy prod --dry-run` only by passing
`--force drift`.)

## Root cause

`PluginWebResourceDriftChecker.GetWebResourceSrcHashes` derived the solution name from the **repo
folder name** to find the publisher-prefixed unpack root:

```csharp
var solutionName = Path.GetFileName(slnFolder.TrimEnd(…));                 // "FlowlineTryOutByClaude"
var pattern = publisherPrefix != null ? $"{publisherPrefix}_{solutionName}" // "av_FlowlineTryOutByClaude"
                                      : $"*_{solutionName}";
var publisherRoot = Directory.EnumerateDirectories(srcWebFolder, pattern, …).FirstOrDefault();  // null
```

`pac solution unpack` writes web resources under `src/WebResources/<prefix>_<solutionUniqueName>/`
(here `av_Cr07982`). With `publisherRoot` null, the fallback keys every Dataverse-side file by its path
*relative to `src/WebResources`* — i.e. `av_Cr07982\example1.js` — while the local side is keyed
relative to `dist/` — i.e. `example1.js`. No key ever matches, so each file lands in both the
"in Dataverse, not local" and the "local only" bucket.

The whole `slnFolder` parameter existed only to produce that name, so it was never a folder the checker
needed — just a wrong source for the solution name.

Why it wasn't caught: every existing web-resource test in `PluginWebResourceDriftCheckerTests` writes
its Dataverse-side files **directly** under `Solution/src/WebResources/` with no publisher-prefixed
root, which is the fallback branch. The publisher-root branch — the one `pac` actually produces — had
no coverage at all.

## Fix

Pass the real solution unique name instead of deriving it from the folder:

- `PluginWebResourceDriftChecker.CheckAsync` / `CheckWebResources` / `GetWebResourceSrcHashes` take
  `solutionUniqueName` in place of `slnFolder` (`src/Flowline/Utils/PluginWebResourceDriftChecker.cs`).
- `SyncCommand` passes `projectSln.UniqueName`; `DeployCommand.ValidateLocalStateAsync` takes and
  passes `sln.UniqueName`.

Regression tests (`tests/Flowline.Tests/PluginWebResourceDriftCheckerTests.cs`) now exercise the
publisher-prefixed layout, from a temp repo folder whose name is a GUID — so they only pass when the
name comes from config:

- `Check_PublisherPrefixedSrcRoot_MatchingContent_NoWarning`
- `Check_PublisherPrefixedSrcRoot_RepoFolderNotNamedAfterSolution_NoPhantomDrift` (incl. nested `images/`)
- `Check_PublisherPrefixedSrcRoot_UnknownPrefix_StillResolvesBySolutionName` (the `*_<solution>` path)
- `Check_PublisherPrefixedSrcRoot_ContentDiffers_ReportedOnceNotTwice`

Full suite after the fix: 1972 passed, 0 failed, 4 skipped (the skips are the live-connect tests).

## Live verification

Rebuilt and reinstalled the global tool, then ran `flowline deploy prod --dry-run` in the same
workspace that had just produced the phantom drift: the local-drift stage passed with no `Only local:`
warnings and without `--force drift`, and the deploy proceeded through packing, the solution checker,
and the environment backup.
