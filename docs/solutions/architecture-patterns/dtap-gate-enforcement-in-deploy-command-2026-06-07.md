---
title: DTAP gate enforcement in DeployCommand
date: 2026-06-07
category: docs/solutions/architecture-patterns
module: DeployCommand
problem_type: architecture_pattern
component: tooling
severity: high
resolution_type: workflow_improvement
applies_when:
  - CLI deploy command can target any DTAP tier
  - No external CI/CD gate enforces promotion order
  - Solution must reach lower tiers before higher tiers
  - Dev environments require sync workflow, not deploy
related_components:
  - FlowlineValidator
  - ProjectConfig
tags:
  - deployment
  - dtap
  - environment-tiers
  - guard
  - power-platform
  - dataverse
  - cli
  - deploy-safety
---

# DTAP gate enforcement in DeployCommand

## Context

`DeployCommand` deployed to any configured URL unconditionally. The `.flowline` config declares a DTAP topology via `ProdUrl`, `UatUrl`, `TestUrl`, `DevUrl` (all nullable), but nothing in the deploy path read those values. Developers could deploy directly to Prod without the solution passing through UAT or Test first, and could run `flowline deploy dev` even though `sync` owns the Dev workflow — `deploy` packs from source XML and imports to the target, which would overwrite live Dev changes.

No runtime enforcement meant the topology config was decorative.

## Guidance

Two pure static helpers plus a gate wired into `ExecuteFlowlineAsync`.

### `ResolveDtapGate` — classify the target

```csharp
internal static DtapGateDecision ResolveDtapGate(ProjectConfig config, string targetUrl)
```

Returns `DtapGateDecision(Outcome, PredecessorUrl?, PredecessorLabel?)`. Three outcomes:

| Outcome | Meaning |
|---|---|
| `DevBlock` | Target matches `DevUrl` — non-bypassable; `sync` is the correct command |
| `Skip` | Target URL not recognised in config, or no predecessor configured below target |
| `Check` | Target is a known non-Dev tier; nearest configured predecessor returned |

DTAP order: Dev(0) < Test(1) < UAT(2) < Prod(3). Predecessor resolution walks down from the target — nearest configured tier wins, not strict immediate predecessor. Prod with no UAT but a configured Test uses Test. URL matching is case-insensitive, trailing-slash normalised.

```csharp
internal enum DtapGateOutcome { DevBlock, Skip, Check }
internal sealed record DtapGateDecision(DtapGateOutcome Outcome, string? PredecessorUrl = null, string? PredecessorLabel = null);
```

### `ReadLocalSolutionVersion` — read version from source XML

```csharp
internal static string ReadLocalSolutionVersion(string packageFolderPath)
// Reads: packageFolderPath/src/Other/Solution.xml
// XPath: ImportExportXml/SolutionManifest/Version
// Throws FlowlineException if file missing or version empty/absent
```

Only called when `Outcome == Check` — avoids XML parsing when the gate will be skipped.

### Gate wiring in `ExecuteFlowlineAsync`

Runs after the managed/unmanaged type guard, before drift check and pack:

```csharp
var dtapDecision = ResolveDtapGate(Config!, targetUrl);

if (dtapDecision.Outcome == DtapGateOutcome.DevBlock)
{
    Console.Error("Dev is a development environment — use 'sync' to push changes there, not 'deploy'.");
    return (int)ExitCode.ValidationFailed;  // non-bypassable
}

if (dtapDecision.Outcome == DtapGateOutcome.Check)
{
    // Throws FlowlineException if Solution.xml missing or version absent — also non-bypassable
    localVersion = ReadLocalSolutionVersion(PackageFolder(slnFolder));

    if (settings.SkipDtapCheck)
    {
        Console.Skip($"Skipping DTAP gate — '{sln.Name}' not verified in {dtapDecision.PredecessorLabel}.");
    }
    else
    {
        var predecessorInfo = await GetSolutionInfoAsync(dtapDecision.PredecessorUrl!, ...);

        if (predecessorInfo == null)
            Console.Error($"'{sln.Name}' hasn't been deployed to {dtapDecision.PredecessorLabel} yet — promote there first, or use --skip-dtap-check.");
        else if (predecessorInfo.VersionNumber == null || predVer < localVer)
            Console.Error($"'{sln.Name}' in {dtapDecision.PredecessorLabel} is v{predecessorInfo.VersionNumber ?? "unknown"} — promote v{localVersion} there first, or use --skip-dtap-check.");
    }
}
```

### `--skip-dtap-check` flag

Declared on `DeployCommand.Settings`, not `FlowlineSettings` — DTAP gate is deploy-only:

```csharp
[CommandOption("--skip-dtap-check")]
[Description("Skip DTAP promotion checks")]
[DefaultValue(false)]
public bool SkipDtapCheck { get; set; } = false;
```

Bypasses the existence and version checks but still reads local `Solution.xml` (version must exist). `DevBlock` is never bypassable — the workflow itself is wrong, not just the version.

## Why This Matters

**Skipping tiers is silent without enforcement.** Prod receives a version that never passed UAT or Test. Bugs that UAT would have caught reach production.

**Dev is the wrong target for `deploy`.** `sync` exports from a live Dev environment and unpacks to source XML. `deploy` packs from source XML and imports to a target. Running `deploy dev` would import the current source state into Dev, overwriting any live in-progress work.

**Nearest-predecessor, not strict immediate predecessor.** Teams with a two-environment setup (Prod + Test, no UAT) get enforcement without needing placeholder UAT entries. The gate finds the nearest configured tier below the target.

**`System.Version` for 4-part Dataverse versions.** Dataverse uses `major.minor.build.revision` (e.g., `1.2.0.4`). String comparison gives wrong ordering (`1.10.0.0 < 1.9.0.0`). `Version.TryParse` handles this correctly; parse failure is treated as a block (conservative).

## When to Apply

- New deploy-adjacent commands that target specific DTAP tiers — wire `ResolveDtapGate` before any network call to the target.
- Any command that should distinguish Dev from other tiers — the `DevBlock` outcome is reusable.
- Adding a new tier alias (e.g., `staging`) — add it to the URL switch in `ExecuteFlowlineAsync` and extend the predecessor chain in `ResolveDtapGate`.
- Teams that skip a tier in config get correct behaviour automatically via `FirstConfigured` — no special case needed.

Do not add `--skip-dtap-check` to `sync`, `push`, or other commands. The gate is deploy-specific.

## Examples

**Gate fires — solution absent in predecessor:**

```
flowline deploy prod
Error: 'MySolution' hasn't been deployed to UAT yet — promote there first, or use --skip-dtap-check.
```

**Gate fires — predecessor version behind local:**

```
flowline deploy prod
Error: 'MySolution' in UAT is v1.1.0.0 — promote v1.2.0.0 there first, or use --skip-dtap-check.
```

**Gate passes — predecessor version current:**

```
flowline deploy prod
Target: Production (https://prod.crm.dynamics.com)
Checking MySolution in UAT... ✓
Packing MySolution...
```

**Dev target blocked unconditionally:**

```
flowline deploy dev
Error: Dev is a development environment — use 'sync' to push changes there, not 'deploy'.
```

**Hotfix bypass:**

```
flowline deploy prod --skip-dtap-check
  Skipping DTAP gate — 'MySolution' not verified in UAT.
Packing MySolution...
```

**Two-environment config (Prod + Test, no UAT) — Test is used as predecessor:**

```json
{ "ProdUrl": "https://prod.crm.dynamics.com/", "TestUrl": "https://test.crm.dynamics.com/" }
```

```
flowline deploy prod
Checking MySolution in Test... ✓
```

**Raw URL matching a configured tier — gate still runs:**

```
flowline deploy https://prod.crm.dynamics.com/
# URL matches ProdUrl → gate runs as Prod
```

**Raw URL not in config — gate skipped:**

```
flowline deploy https://sandbox.crm.dynamics.com/
# URL not in config → Skip outcome → no predecessor check
```

## Related

- `src/Flowline/Commands/DeployCommand.cs` — implementation
- `tests/Flowline.Tests/DeployCommandDtapGateTests.cs` — 17 unit tests for pure helpers
- `docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md` — same guard pattern applied in `ProvisionCommand`
- `docs/solutions/conventions/flowline-add-environment-2026-06-06.md` — adding new environments that DTAP gate will pick up
- `docs/solutions/logic-errors/pac-sync-version-order-2026-05-21.md` — version correctness prerequisite for the version check
