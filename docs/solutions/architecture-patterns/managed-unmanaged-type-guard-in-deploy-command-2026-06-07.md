---
title: Managed/unmanaged type guard in DeployCommand
date: 2026-06-07
category: docs/solutions/architecture-patterns
module: DeployCommand
problem_type: architecture_pattern
component: tooling
severity: high
resolution_type: workflow_improvement
applies_when:
  - CLI deploy command supports both managed and unmanaged package types
  - Target environment may already have an existing solution installed
  - Managed-to-unmanaged type conversion is irreversible in Dataverse
  - No external CI/CD gate enforces package type consistency
related_components:
  - FlowlineValidator
  - ProvisionCommand
tags:
  - deployment
  - managed-solutions
  - unmanaged-solutions
  - guard
  - power-platform
  - dataverse
  - cli
  - deploy-safety
---

# Managed/unmanaged type guard in DeployCommand

## Context

`DeployCommand` accepted `--managed` and imported the packed solution type unconditionally. Dataverse solutions have two mutually exclusive types — **unmanaged** (editable, used in Dev/Test) and **managed** (locked, used in production). The `.flowline` config and `--managed` flag control which package type Flowline packs and imports, but nothing in the deploy path checked the *existing* type of the solution in the target environment.

Two scenarios were silently allowed:

1. `flowline deploy prod --managed` when the target already had an **unmanaged** solution — a destructive type conversion with no rollback path in Dataverse. The import succeeds and the unmanaged solution is gone.
2. `flowline deploy uat` (unmanaged) when the target already had a **managed** solution — invalid in Dataverse; the import either errors deep in the pipeline or produces undefined state.

Both cases represent a class of error where a successful-looking operation leaves the environment worse than a clean failure would. In the first case Dataverse reports success, but the solution's editability has been permanently revoked.

## Guidance

The guard runs in `ExecuteFlowlineAsync` after the target environment is confirmed and the solution name is resolved, before the DTAP gate and before any pack step.

**Execution order:**

```
1. Assert repo clean
2. Resolve target URL
3. Resolve solution name
4. Validate target environment              ← targetEnv confirmed
5. [GUARD] Check existing solution type     ← inserted here
6. DTAP promotion gate
7. Local drift check
8. Pack
9. Import
```

**Implementation in `src/Flowline/Commands/DeployCommand.cs`:**

```csharp
var existingSolution = await Console.Status().FlowlineSpinner().StartAsync(
    $"Checking [bold]{sln.Name}[/]...",
    _ => FlowlineValidator.Default.GetSolutionInfoAsync(targetUrl, sln.Name, includeManaged: true, settings, cancellationToken));

if (existingSolution != null)
{
    if (settings.Managed && !existingSolution.IsManaged)
    {
        Console.Error($"'{sln.Name}' is unmanaged in {targetEnv.DisplayName} — importing managed is irreversible. Remove the unmanaged solution first, or deploy unmanaged.");
        return (int)ExitCode.ValidationFailed;
    }
    if (!settings.Managed && existingSolution.IsManaged)
    {
        Console.Error($"'{sln.Name}' is managed in {targetEnv.DisplayName} — can't import unmanaged over managed. Deploy managed instead.");
        return (int)ExitCode.ValidationFailed;
    }
}
```

**`includeManaged: true` is required.** This flag affects both the PAC CLI query and the cache key (`SolutionKey(environmentUrl, solutionName, includeManaged)` in `FlowlineValidator`). Without it, managed solutions may return `null`, making the guard a silent no-op.

**The outer `if (existingSolution != null)` makes first-time deploys transparent.** If the solution has never been deployed to this environment, both type checks are irrelevant and the command continues normally.

**Neither check is bypassable.** No `--force`, no `--skip-type-check`, no override flag. The Dataverse constraint is absolute — a bypass flag would give false confidence while the operation either fails at the Dataverse layer or succeeds in corrupting the environment.

## Why This Matters

**Irreversibility separates this from other deploy failures.** Most deployment errors recover cleanly — wrong version, wrong environment, missing component — they fail, you fix, you redeploy. The managed-over-unmanaged case is different: Dataverse imports the managed package and reports success. The unmanaged solution is now gone. Customizations not in source control are permanently lost. Recovery requires deleting the solution from the environment and reimporting from scratch, losing all data associations and environment-specific configuration that pointed to it.

**Silent success is worse than loud failure.** The pack-and-import sequence completes without error. The developer sees "Deployed! Your solution is live." The environment corruption is invisible until someone tries to customize the solution and discovers it is managed.

**The guard's cost is near zero.** `FlowlineValidator.GetSolutionInfoAsync` has a 4-hour TTL cache. In the typical deploy path the DTAP gate's predecessor check has already warmed the cache for the target environment. The type check adds one fast lookup.

**Established pattern in `ProvisionCommand`.** `FindProblematicSolutions` applies the same principle to environment provisioning — it blocks copy operations when the target has unmanaged solutions that would be permanently lost. The DeployCommand guard is the per-solution equivalent of that environment-level guard.

## When to Apply

- Any command that imports a solution to an environment where a prior version may already exist — wire this check before pack, before any network write to Dataverse.
- Deploy-adjacent commands (future batch deploy, rollback deploy) that accept a `--managed` flag — the check is one call against the cached validator.
- Commands that write solution state at the environment level — see `ProvisionCommand.FindProblematicSolutions` for the equivalent guard applied to environment copy operations.

Do not add a bypass flag. The constraint is absolute in Dataverse.

## Examples

**Managed import over existing unmanaged — blocked:**

```
flowline deploy prod --managed
Error: 'MySolution' is unmanaged in Production — importing managed is irreversible. Remove the unmanaged solution first, or deploy unmanaged.
```

**Unmanaged import over existing managed — blocked:**

```
flowline deploy uat
Error: 'MySolution' is managed in UAT — can't import unmanaged over managed. Deploy managed instead.
```

**First deployment — no existing solution, guard transparent:**

```
flowline deploy prod --managed
  Checking MySolution...
  [no existing solution — guard skips]
  [DTAP gate passes]
  Packing MySolution...
  Deploying MySolution to Production...
Done! Your solution is live.
```

**Same type repeated — no block:**

```
flowline deploy prod --managed
  Checking MySolution...
  [types match — guard passes]
  [DTAP gate passes]
  Packing MySolution...
  Deploying MySolution to Production...
Done! Your solution is live.
```

## Related

- `src/Flowline/Commands/DeployCommand.cs` — implementation (lines ~76–92)
- `src/Flowline/Validation/FlowlineValidator.cs` — `GetSolutionInfoAsync`, note the `includeManaged` parameter and its effect on the cache key
- `docs/solutions/best-practices/provision-safety-guard-unmanaged-solutions-2026-05-18.md` — same guard principle applied to `ProvisionCommand` (environment provisioning)
- `docs/solutions/architecture-patterns/dtap-gate-enforcement-in-deploy-command-2026-06-07.md` — complementary guard in `DeployCommand` (version/tier promotion enforcement)
