---
title: "Sync-First ALM: Remove Pac Mapping and Pack Directly from src/"
date: 2026-05-17
category: docs/solutions/architecture-patterns/
module: flowline-cli
problem_type: architecture_pattern
component: development_workflow
severity: high
applies_when:
  - pac solution sync is the canonical source-of-truth for src/
  - toolchain uses both pac and MSBuild in the same pipeline
  - mapping files cause warnings or correctness risks
  - "WebResources/dist/ must stay in sync with src/WebResources/"
  - team owns their DEV environment (not ISV/ALM Accelerator flow)
symptoms:
  - pac warns about mapping file format mismatches
  - two mapping files required for two different tools
  - dotnet build could deploy locally-modified artifacts not confirmed in DEV
  - web resource dist/ diverges silently from src/ after sync
root_cause: missing_workflow_step
resolution_type: workflow_improvement
related_components:
  - tooling
  - documentation
tags:
  - pac-solution
  - mapping-removal
  - sync-first
  - drift-detection
  - dotnet-build
  - dataverse
  - webresources
  - alm
---

# Sync-First ALM: Remove Pac Mapping and Pack Directly from src/

## Context

Flowline CLI managed Dataverse ALM workflows using `pac solution sync` and `pac solution pack`. A two-file mapping system was in place:

- `MappingPac.xml` — passed to `pac solution sync --map` and `pac solution pack --map`
- `MappingBuild.xml` — consumed by `dotnet build` via `SolutionPackageMapFilePath` MSBuild property (set dynamically by `EnsureMapFilePathAsync`)

The mapping redirected pac to download/read files from non-standard paths — directing web resources to `WebResources/src/` and plugin assemblies to `Plugins/bin/Release/` rather than the canonical `src/` folder pac expects.

This created three compounding problems:

1. **Pac sync warnings.** `pac solution sync --map` downloads files to mapped locations, but pac still validates against its expected `src/` layout when checking whether the solution can repack. Result: "Solution may not repack" warnings on every sync.

2. **Correctness risk on deploy.** `pac solution pack --map` with the mapping pointed at `Plugins/bin/Release/` and `WebResources/dist/`. These are local build artifacts — they may contain code that was never pushed to DEV and therefore never validated there. Deploying a zip built from local artifacts violates the ALM invariant: "deploy only what is confirmed in DEV."

3. **Two-format fragility.** `pac` and MSBuild expect different XML schemas and path conventions, so two mapping files had to be maintained in sync with each other and with actual folder locations.

## Guidance

**Remove all mapping. Use `src/` as the single source of truth.**

The correct architecture for sync-first Dataverse ALM:

```
source code → push → Dataverse DEV → sync → src/ → pack → deploy to test/prod
```

**Step 1: Sync without `--map`**

```csharp
args.Add("solution").Add("sync")
    .Add("--solution-folder").Add(slnFolder)
    .Add("--environment").Add(environmentUrl)
    .Add("--packagetype").Add("Unmanaged")
    .Add("--async");
// No --map argument
```

`pac solution sync` (no `--map`) downloads all solution components directly into `src/` using pac's native layout. No warnings, no redirects.

**Step 2: Pack directly from `src/`**

```csharp
CommandResult result = await Cli.Wrap(cmdName)
    .WithArguments(args =>
        args.AddIfNotNull(prefixArgs)
            .Add("solution")
            .Add("pack")
            .Add("--folder").Add(Path.Combine(slnFolder, "src"))
            .Add("--zipFile").Add(zipFile)
            .Add("--packageType").Add(packageType))
    .ExecuteAsync(cancellationToken).Task;
```

The zip produced reflects exactly what is in Dataverse DEV — no local build artifacts, no mapping indirection.

**Step 3: Remove `EnsureMapFilePathAsync` and `dotnet build` for pack**

`CloneCommand` previously called `DotNetUtils.EnsureMapFilePathAsync` to inject `SolutionPackageMapFilePath` into the `.cdsproj`, then called `dotnet build` to produce the zip. Both are unnecessary when packing directly via `pac solution pack --folder src/`.

**Step 4: Replace Dataverse SDK download with file copy**

`pac solution clone` already downloads web resources into `src/WebResources/`. The previous implementation made a separate Dataverse SDK call to download them again. Replace with a direct file copy:

```csharp
private void SeedWebResourceDistFromSrc(string slnFolder, Settings settings)
{
    var srcWebResources = Path.Combine(slnFolder, "src", "WebResources");
    var distFolder = Path.Combine(slnFolder, "WebResources", "dist");

    if (!Directory.Exists(srcWebResources)) { Console.Skip("No WebResources in src — skipping dist seed"); return; }
    if (Directory.EnumerateFiles(distFolder, "*.*", SearchOption.AllDirectories).Any()) { Console.Skip("WebResources/dist already populated — skipping"); return; }

    foreach (var srcFile in Directory.EnumerateFiles(srcWebResources, "*.*", SearchOption.AllDirectories))
    {
        var relPath = Path.GetRelativePath(srcWebResources, srcFile);
        var destFile = Path.Combine(distFolder, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        File.Copy(srcFile, destFile, overwrite: false);
    }
    Console.Success("WebResources/dist seeded from src");
}
```

**Step 5: Add `DriftChecker` for non-blocking post-sync warnings**

Without mapping, local build artifacts (`WebResources/dist/`, `Plugins/bin/Release/`) no longer participate in the ALM chain. Add a drift check after sync that compares them against what sync downloaded:

```csharp
var driftWarnings = await DriftChecker.CheckAsync(slnFolder, cancellationToken);
foreach (var w in driftWarnings)
{
    var hint = w.Category switch
    {
        DriftCategory.ContentDiffers     => $"Check who changed '{w.RelativePath}' in Dataverse",
        DriftCategory.NewInDataverse     => $"Check who changed '{w.RelativePath}' in Dataverse — run 'flowline push' to re-sync",
        DriftCategory.OnlyLocal          => $"Local change not in Dataverse — run 'flowline push' ({w.RelativePath})",
        DriftCategory.PluginSizeMismatch => $"Local plugin build may differ from what is deployed — rebuild and push if intentional ({w.RelativePath})",
        _                                => w.RelativePath
    };
    Console.Warning(hint);
}
```

Comparison strategy:
- **Web resources:** SHA-256 hash comparison between `src/WebResources/` and `WebResources/dist/`
- **Plugins:** file size comparison with a 10 KB threshold between `src/PluginAssemblies/` and `Plugins/bin/Release/`

## Why This Matters

**ALM correctness.** The key invariant of sync-first Dataverse ALM: deploy only packs from `src/` (confirmed DEV state), never from local build artifacts. Mapping files broke this invariant by pointing `pac solution pack` at `Plugins/bin/Release/` and `WebResources/dist/` — artifacts that could contain unvalidated local changes.

**Pac's sync/pack contract.** `pac solution sync` and `pac solution pack` are designed as a symmetric pair using the same `src/` folder layout. Using `--map` with sync but not pack (or vice versa) breaks this symmetry and causes "Solution may not repack" warnings. Removing mapping restores the symmetric contract.

**Elimination of hidden state.** Two mapping files that must stay synchronized with each other and with actual folder locations are a maintenance hazard. A single `src/` directory as source of truth has no hidden state.

**Strategic scope.** This architecture — "sync-first, not pack-first" — is appropriate for teams that own their DEV environment. Teams that do not own DEV should use ALM Accelerator instead.

## When to Apply

Apply when:

- Building or maintaining a Dataverse ALM CLI that wraps `pac solution sync` and `pac solution pack`
- You see `pac solution sync` producing "Solution may not repack" warnings
- Your pack step reads from local build artifact folders (`bin/Release/`, `dist/`) rather than from `src/`
- You have mapping files (`Mapping*.xml`) that redirect pac to non-standard paths
- You want to guarantee that deploy zips contain only what was validated in DEV

Do not apply when:

- The team does not control the DEV environment (use ALM Accelerator)
- You need to pack from custom folder layouts that genuinely differ from pac's native `src/` structure

## Examples

**Before: sync with mapping**
```csharp
args.Add("solution").Add("sync")
    .Add("--solution-folder").Add(slnFolder)
    .Add("--environment").Add(environmentUrl)
    .AddIf(useMapping, "--map", Path.Combine(slnFolder, "MappingPac.xml"));
```

**After: sync without mapping**
```csharp
args.Add("solution").Add("sync")
    .Add("--solution-folder").Add(slnFolder)
    .Add("--environment").Add(environmentUrl)
    .Add("--packagetype").Add("Unmanaged")
    .Add("--async");
```

**Before: pack via dotnet build with mapping**
```csharp
if (await DotNetUtils.EnsureMapFilePathAsync(cdsprojPath, useMapping, cancellationToken) != 0) return 1;
if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, verbose, cancellationToken) != 0) return 1;
```

**After: pack directly from src/**
```csharp
CommandResult result = await Cli.Wrap(cmdName)
    .WithArguments(args =>
        args.AddIfNotNull(prefixArgs)
            .Add("solution")
            .Add("pack")
            .Add("--folder").Add(Path.Combine(slnFolder, "src"))
            .Add("--zipFile").Add(zipFile)
            .Add("--packageType").Add(packageType))
    .ExecuteAsync(cancellationToken).Task;
```

**Anti-pattern: silently swallowing drift check exceptions**
```csharp
// Do not do this
try { var warnings = await DriftChecker.CheckAsync(slnFolder, cancellationToken); ... }
catch { Console.Skip("Drift check unavailable"); }
```

Let `DriftChecker.CheckAsync` throw. Only catch exceptions when you have specific recovery logic — silent swallowing hides real problems.

## Related

- `docs/solutions/logic-errors/sync-overwrites-uncommitted-src-without-warning-2026-05-15.md` — pre-sync dirty-tree guard that prevents sync from silently overwriting `src/`; complements this pattern (guard first, then sync cleanly)
- `docs/brainstorms/2026-05-16-build-validation-and-pac-warnings-requirements.md` — requirements source for this refactor (R1–R15, acceptance examples, scope)
- `docs/plans/2026-05-17-001-refactor-remove-mapping-replace-dotnet-build-plan.md` — implementation plan covering U1–U6
- `pac solution sync` CLI — `--map`, `--packagetype`, `--async` flags
- `pac solution pack` CLI — `--folder`, `--zipFile`, `--packageType` flags
- ALM Accelerator (Power Platform) — recommended for teams that do not own their DEV environment