---
title: Typed post-deploy service protocol — IPostDeployService fan-out over IEnumerable<T>
date: 2026-07-03
last_updated: 2026-07-10
category: docs/solutions/architecture-patterns
module: DeployCommand
problem_type: architecture_pattern
component: tooling
severity: medium
applies_when:
  - Multiple independent behaviors must run during a command's pre-import/post-import (or similarly phased) lifecycle, such as cleanup, backup, or state restoration
  - A command is directly constructor-injecting one concrete singleton service, coupling it to a single implementation instead of an abstraction
  - New deploy-time or phase-time capabilities are anticipated and the interface must accommodate them without future breaking changes
  - Every registered implementation of a capability should execute (fan-out via foreach over IEnumerable<T>), as opposed to selecting exactly one by discriminator (e.g. IGenerator's SingleOrDefault pattern)
  - State needs to flow from an earlier phase (pre-import) to a later phase (post-import) without adding parameters to the shared interface, and the service is registered as a singleton in a single-command-per-process CLI
related_components:
  - OrphanCleanupService
  - DeployCommand
  - PostDeployContext
  - DeploySolutionInfo
  - IGenerator
  - Program.cs
tags:
  - post-deploy
  - dependency-injection
  - ienumerable-di
  - fan-out
  - interface-extraction
  - orphan-cleanup
  - di-silent-empty
  - deploy-pipeline
---

# Typed post-deploy service protocol — IPostDeployService fan-out over IEnumerable<T>

## Context

`DeployCommand` called `orphanCleanupService.RunPreImportAsync(...)` and `orphanCleanupService.RunPostImportAsync(...)` directly against the concrete `OrphanCleanupService` type, with an explicit `deferred` list threaded between the two calls (`deferred = await RunPreImportAsync(...)`, then `RunPostImportAsync(deferred, ...)`).

Multiple more capabilities were already anticipated on the exact same pre-import/post-import shape: classic-workflow state restoration (a separate follow-up plan, `docs/brainstorms/2026-06-12-deploy-state-restoration-requirements.md`), a pre-deploy backup step (sketched in `docs/ideation/IDEAS.md:181-190`), and a Dataverse-triggered-flow callback re-registration fix (GitHub issue #1 — a post-import-only "detect broken flows, run a double off/on cycle" step). Left ad-hoc, each new capability would add another bespoke pair of calls and another merge point inside `DeployCommand`, and the command would accumulate knowledge of every capability's concrete type and return shape. Idea 6 (`docs/ideation/2026-07-01-post-deploy-environment-config-ideation.html`) formalized the existing two-call pattern into a protocol, `IPostDeployService`, before the second implementer got built.

## Guidance

Extract a typed protocol for the two hook points, then have the orchestrating command depend on `IEnumerable<TProtocol>` instead of a concrete type:

```csharp
// src/Flowline.Core/Services/IPostDeployService.cs
public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    string SolutionName,
    IReadOnlyList<(Guid ObjectId, int ComponentType)> LocalComponents,
    RunMode Mode,
    string? WebResourceRoot);

public interface IPostDeployService
{
    Task RunPreImportAsync(PostDeployContext context, CancellationToken ct);
    Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct);
}
```

`OrphanCleanupService : IPostDeployService` keeps its retry state, but since the interface carries no state parameter between calls, the old `deferred` list became a private mutable field: `IReadOnlyList<OrphanEntry> _deferred = [];`, reset at the top of `RunPreImportAsync`, read directly (not passed in) by `RunPostImportAsync`.

Six design decisions drove the shape:

1. **Stateful service instances, no state parameter on the interface.** Each implementer manages its own retry/deferred state privately rather than the interface threading state between calls. This is safe *specifically because* `OrphanCleanupService` is registered `AddSingleton` and Flowline is a single-command-per-process CLI: each `deploy` invocation is a fresh process, so there's no concurrent-deploy scenario where instance state from one deploy could leak into another. This is a **repo-specific safety argument, not a general one** — the same pattern would be unsafe in a long-lived server process handling concurrent requests, or if this CLI ever grew a batch/multi-deploy-per-process mode.

2. **One shared per-run context, not per-capability parameters.** `PostDeployContext` bundles every input any current or anticipated implementer might need (Dataverse connection, solution name, parsed local components, run mode, web-resource root) rather than growing the interface's parameter list per implementer. `CancellationToken` deliberately stays a separate trailing parameter on both hook methods rather than becoming a context field — matching the convention of every other async method in the codebase.

3. **Per-capability CLI toggles do NOT belong on the shared context.** A future state-restoration service's `--no-restore` opt-out flag is per-capability, not per-run, so it belongs on that service's own constructor-injected settings object — mirroring how `OrphanCleanupService` already receives `FlowlineRuntimeOptions` separately from any per-run context, rather than as a new field bolted onto `PostDeployContext`. Generalizable rule: **a shared multi-implementer context should only carry inputs every implementer might plausibly need; a toggle specific to one implementer's behavior is that implementer's own concern.**

4. **Fan-out (`foreach`, all implementers run), not select-one.** Chosen deliberately over mirroring the codebase's only other multi-implementation interface, `IGenerator` (`src/Flowline/Generators/IGenerator.cs`), which is select-one via `postDeployServices.SingleOrDefault(g => g.Type == ...)` in `GenerateCommand.cs`. Post-deploy capabilities are meant to compose — run orphan cleanup AND state restoration AND backup, not pick one. `IPostDeployService` has no discriminator property; every registered implementer participates in every deploy. This was new ground for the codebase; no prior "run all matching" precedent existed to copy.

5. **Verify future fit by design walk-through, not by building the future consumers.** Before committing to the `PostDeployContext` shape, the plan walked through how it would fit the two anticipated future implementers (state restoration: needs only `.Service`/`.SolutionName`, ignores the rest; backup: needs `.Service`/`.SolutionName`, post-import hook is a no-op returning 0) and confirmed zero interface changes would be needed for either. This is a cheap, valuable step before adding fields to a shared context: reason about the next 1-2 real consumers concretely instead of guessing abstractly.

6. **Aggregation logic extracted as a testable static helper.** `internal static bool ShouldReportPartialSuccess(int cleanupFailures) => cleanupFailures > 0;` in `DeployCommand.cs` follows the established `ResolveDtapGate` convention (see `tests/Flowline.Tests/DeployCommandDtapGateTests.cs`) — the only way to unit-test `DeployCommand` logic without constructing the whole command, since its constructor needs live `DataverseConnector`/PAC-CLI dependencies with no test doubles in this codebase.

**Update (2026-07-10): `PostDeployContext`'s flat fields consolidated into a `DeploySolutionInfo` sub-record.** The shape shown in the Guidance code sample above (a flat `SolutionName: string`) is out of date — three `IPostDeployService` implementers exist now (`OrphanCleanupService`, `BackupService`, `SolutionCheckService`), and all three read solution name and environment URL. `DeploySolutionInfo(Name, EnvironmentUrl, IncludeManaged, ExistsInTarget)` replaces the flat `SolutionName`/`EnvironmentUrl` fields with one nested record, adding two new facts (`IncludeManaged`, `ExistsInTarget`) needed to decide managed-vs-unmanaged import behavior — see [orphan-cleanup-two-phase-deploy-pipeline.md](orphan-cleanup-two-phase-deploy-pipeline.md)'s 2026-07-10 update for the full context:

```csharp
public sealed record DeploySolutionInfo(
    string Name,
    string EnvironmentUrl,
    bool IncludeManaged,
    bool ExistsInTarget);

public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    DeploySolutionInfo Solution,
    RunMode Mode,
    string PackagePath,
    string PackageSrcRoot);
```

This went through a wrong-first-draft before landing here, worth recording because it's a reusable lesson for this exact pattern: the first attempt added a pre-rendered `OrphanNoDeleteHint` string field directly to `PostDeployContext`, computed by `DeployCommand` and printed verbatim by `OrphanCleanupService`. Flagged as backwards against design decision 2 above: a shared context feeding multiple consumers should carry the raw facts a consumer needs to decide for itself, not a string one consumer already rendered on another's behalf. `DeploySolutionInfo` plus a new `OrphanCleanupService.BuildNoDeleteHint(DeploySolutionInfo)` pure function fixed it — the context stays a data carrier, and the consumer that owns the printed report decides what to render from it.

Also note, independent of this update: `LocalComponents` (`IReadOnlyList<(Guid ObjectId, int ComponentType)>`) and `WebResourceRoot` (`string?`), both shown in this doc's original Guidance sample above, are gone from the current record — `WebResourceRoot`'s removal is documented in the two-phase doc's part-7 update; `LocalComponents`' removal isn't documented anywhere else, noted here for the record. `DeployCommand`'s fan-out loop (Examples section below) also now filters via `activeServices = postDeployServices.Where(s => !IsSkipped(s))` (skipping `SolutionCheckService`/`BackupService` per `--skip-solution-check`/`--no-backup`), rather than the plain unconditional `foreach` shown there — this and the `LocalComponents`/`WebResourceRoot` removal both predate this specific update and are flagged here for a future refresh pass rather than fixed inline.

**Update (2026-07-10): `PackageSrcRoot` is now always an unpacked temp copy of the imported zip, never a direct `Package/src` reference.** `DeployCommand` used to pass `Path.Combine(PackageFolder(slnFolder), "src")` straight through as `PackageSrcRoot`, which only stayed correct because `deploy` always packed from that exact folder immediately beforehand. That assumption broke once `deploy` gained a `--path <zip>` flag and a local build-once artifact cache (both let `packagePath` come from something other than "just packed from the current `Package/src`") — see `docs/plans/2026-07-10-001-feat-deploy-artifact-reuse-plan.md`. `DeployCommand` now runs `pac solution unpack` on whatever `packagePath` was actually resolved (fresh pack, cache reuse, or `--path`) into a fresh temp directory, deletes it in a `finally` block, and passes that temp directory as `PackageSrcRoot` instead. `OrphanCleanupService` (the only consumer that reads `PackageSrcRoot`, via `ComponentClassifier.ParseLocalSource`) is unaffected by this change beyond now seeing guaranteed-accurate content — the folder shape it walks (`Other/Solution.xml`, `Entities/`, etc.) is unchanged, since `pac solution unpack` reproduces the same layout `pac solution pack` reads from.

## Why This Matters

Without this pattern, each new post-deploy capability adds another bespoke pair of calls and another merge point directly inside `DeployCommand`, and the command accumulates knowledge of every capability's concrete type and return shape. With more implementers already anticipated (state restoration, backup, flow-callback repair), that would mean `DeployCommand` growing a third and fourth hand-written call pair, each slightly different, each requiring `DeployCommand` itself to change.

**The gotcha, confirmed during code review — the most important cost of this exact pattern shape:** switching `DeployCommand`'s dependency from a required concrete type to `IEnumerable<IPostDeployService>` changes a DI failure mode from loud to silent. In Microsoft.Extensions.DependencyInjection, resolving `IEnumerable<T>` when zero implementations of `T` are registered returns an **empty collection** — it does NOT throw `InvalidOperationException`, unlike resolving `T` directly (which throws immediately if unregistered). Concretely: if `Program.cs:63`'s `services.AddSingleton<IPostDeployService, OrphanCleanupService>();` line is ever accidentally removed (bad merge, careless refactor), both `foreach` loops in `DeployCommand` silently iterate zero times. Orphan cleanup never runs. The deploy still reports full success (exit code 0) — no error, no warning, nothing in the console output signals anything went wrong.

This is the fundamental tradeoff of the `IEnumerable<T>` "plugin collection" DI pattern: it trades a fail-fast arity guarantee for "zero-or-more is valid," and that tradeoff is invisible unless you know to look for it. This was a deliberate, discussed decision NOT to add a guard (e.g., a composition-root arity check after `services.BuildServiceProvider()`, or a DI-resolution test) — treated as the same class of "don't validate scenarios that only occur via deliberate future human error" as other guard-adding decisions this project avoids. The tradeoff is documented here so a future engineer touching this DI wiring knows the failure mode is silent, even though no guard was added.

## When to Apply

Apply this pattern when you have 2+ capabilities that hook the same two points in a pipeline (here: pre-import/post-import of a deploy), each capability is independently useful/testable/shippable, and you want adding capability N+1 to require zero changes to the orchestrating command.

Don't apply it prematurely for a single implementer with no second one in sight — this refactor was explicitly planned to land "alongside the first post-deploy service" (i.e. when the second implementer is imminent), not built speculatively in isolation for a hypothetical future.

## Examples

**`DeployCommand` — before (direct concrete-type calls, explicit state threading):**

```csharp
var deferred = await orphanCleanupService.RunPreImportAsync(
    service, solutionName, localComponents, mode, webResourceRoot, ct);
// ... PackSolutionAsync, ImportSolutionAsync ...
cleanupFailures += await orphanCleanupService.RunPostImportAsync(
    deferred, service, solutionName, ct);
```

**`DeployCommand` — after (fan-out over the protocol, shared context, no explicit state threading):**

```csharp
var context = new PostDeployContext(service, solutionName, localComponents, mode, webResourceRoot);

foreach (var service in postDeployServices)
    await service.RunPreImportAsync(context, ct);   // before PackSolutionAsync
// ... PackSolutionAsync, ImportSolutionAsync ...
foreach (var service in postDeployServices)
    cleanupFailures += await service.RunPostImportAsync(context, ct);   // after ImportSolutionAsync
```

**`DeployCommand` — current shape (2026-07-10, reflects the `DeploySolutionInfo` update above and two new real implementers, `BackupService`/`SolutionCheckService`):**

```csharp
var solutionInfo = new DeploySolutionInfo(sln.Name, targetEnv.EnvironmentUrl!, sln.IncludeManaged, existingSolutionInTarget);
var postDeployContext = new PostDeployContext(service, solutionInfo, runMode, packagePath, packageSrcRoot);

bool IsSkipped(IPostDeployService s) =>
    settings.SkipSolutionCheck && s is SolutionCheckService ||
    settings.NoBackup && s is BackupService;

var activeServices = postDeployServices.Where(s => !IsSkipped(s)).ToList();

foreach (var postDeployService in activeServices)
    await postDeployService.RunPreImportAsync(postDeployContext, cancellationToken);
// ... PackSolutionAsync, ImportSolutionAsync ...
foreach (var postDeployService in activeServices)
    cleanupFailures += await postDeployService.RunPostImportAsync(postDeployContext, cancellationToken);
```

The `IsSkipped` filter is new since the "after" example above — `--skip-solution-check`/`--no-backup` now opt specific implementers out of both loops. An earlier version applied the filter only to the pre-import loop, leaving post-import unfiltered — harmless in practice since `BackupService`/`SolutionCheckService`'s `RunPostImportAsync` are both no-ops, but fixed for consistency (`activeServices` now backs both loops) rather than left as a latent asymmetry. `LocalComponents`/`WebResourceRoot`, both present on the original `PostDeployContext` shown above, are gone — see the `DeploySolutionInfo` update earlier in this doc.

**`Program.cs:63` — DI registration before/after:**

```csharp
// Before
services.AddSingleton<OrphanCleanupService>();

// After
services.AddSingleton<IPostDeployService, OrphanCleanupService>();
```

If this line is ever dropped, `DeployCommand`'s constructor-injected `IEnumerable<IPostDeployService> postDeployServices` resolves as empty rather than throwing — the deploy runs, reports success, and silently skips orphan cleanup.

## Related

- [orphan-cleanup-two-phase-deploy-pipeline.md](orphan-cleanup-two-phase-deploy-pipeline.md) — the two-phase orphan-diffing algorithm `OrphanCleanupService` still implements; its own 2026-07-03 update covers the `IPostDeployService` fan-out transition described here, and its 2026-07-10 update covers the managed-solution handling that reuses this protocol's `PostDeployContext`.
- [ai-agent-consumable-cli-contract-2026-06-07.md](ai-agent-consumable-cli-contract-2026-06-07.md) — `ExitCode.PartialSuccess = 18`, unchanged by this refactor; `DeployCommand` still returns it when the summed `cleanupFailures` across all `IPostDeployService` implementers is non-zero.
- GitHub issue #1 ("Post-deploy: Auto-fix missing callback registrations for Dataverse-triggered flows") — a third real candidate `IPostDeployService` implementer (post-import-only), not yet built.
