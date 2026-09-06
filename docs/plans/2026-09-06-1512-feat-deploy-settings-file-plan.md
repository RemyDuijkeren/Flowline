---
title: Deploy Settings File Integration - Plan
type: feat
date: 2026-09-06
topic: deploy-settings-file
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Deploy Settings File Integration - Plan

## Goal Capsule

- **Objective:** A pipeline can import a solution and land that environment's configuration in one command, instead of running two.
- **Product authority:** This document, for the deploy surface only. The settings file, the apply path, discovery, ordering and exit codes belong to [`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md).
- **Open blockers:** Two, both in Outstanding Questions. Neither is answerable without the configure command existing.

---

## Product Contract

### Summary

Add `deploy --settings-file <path>`: the PAC-native sections go to `pac solution import --settings-file` so environment variables and connection references are applied by the import itself, and the Flowline-owned sections apply after the import through a post-deploy participant.

### Problem Frame

`configure` makes an environment match a settings file, but a pipeline that deploys and then configures runs two commands against the same target, authenticating twice and importing before it knows whether the configuration is even applicable.

The deeper reason to integrate is that Microsoft already applies half the file for free. `pac solution import --settings-file` sets environment variable values and binds connection references during the import, and validates that connection references are usable by the connection's owner. Doing that half through the import inherits semantics and validation Flowline would otherwise reimplement.

This was scoped inside the configure plan and lifted out after document review found six defects in that single unit, against one or two elsewhere. It is the only work in that plan that modifies published behaviour, so it carries a different risk profile and deserves its own plan.

<!-- ce-section: work-relationships -->
### How This Work Fits Together

This plan owns **the deploy surface** for settings files.

- **The configure command** ([`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md)) — *Depends on* it entirely. This plan adds a second caller of that plan's apply path and changes nothing about how the file is read, ordered or reported.
- **Auto-applying configuration when a file exists** — *Depends on* this plan. This plan keeps the flag opt-in; applying a file automatically whenever one exists for the target, with references validated before packing, is the next step and is not scoped here.

### Key Decisions

- **The deploy-time apply runs as an `IPostDeployService`, not inline.** Deploy's exit code comes solely from `ResolvePostImportExitCode` (`src/Flowline/Commands/DeployCommand.cs:354`), so inline code has no seam and its result would be invisible to that resolution. Registering a participant also puts state writes in the post-import phase, where dependency-blocked writes succeed. Governs R2, R4.
- **The file's two halves reach the environment by different routes.** PAC-native sections are applied by the import; Flowline-owned sections after it. Neither is authoritative over the other — the file is. Governs R1, R2.

### Requirements

- R1. `flowline deploy <env> --settings-file <path>` applies the whole file: the PAC-native sections through `pac solution import --settings-file`, the Flowline-owned sections after the import.
- R2. The Flowline-owned sections apply through a post-deploy participant, and its outcome reaches deploy's existing exit-code resolution rather than bypassing it.
- R3. The file is validated through the configure plan's reader before `pac` is invoked, so a malformed file fails with `ConfigInvalid` (11) rather than being converted to `BuildFailed` (13) with plugin-flavoured remediation.
- R4. Deploy's behaviour is unchanged when the flag is absent. The participant is inert and every existing deploy keeps its current output and exit codes.
- R5. Because `configure` is idempotent, running it after a deploy that applied the same file changes nothing. A value that differs between the two runs means the file changed.

### Acceptance Examples

- AE1. `deploy test --settings-file <path>` imports a solution whose file declares two environment variable values and one connection reference. The import applies all three. Running `configure test` immediately afterwards reports no change for them. Covers R1, R5.
- AE2. A deploy with no `--settings-file` produces byte-identical output and the same exit code as before this change. Covers R4.
- AE3. A malformed settings file fails before `pac` is invoked, with `ConfigInvalid`. Covers R3.

### Scope Boundaries

Outside this work:

- Everything the configure plan owns: the file format, discovery, apply ordering, reporting and exit-code selection.
- Auto-applying a file that was not asked for by flag.

### Dependencies / Assumptions

- Depends on the configure command shipping first. This plan adds a caller, not a capability.
- `pac solution import --settings-file` ignores unrecognised top-level sections — verified 2026-09-06 by a real import against a live environment, with both PAC-native sections empty. Known sections applying correctly beside unknown ones is still untested, and this is observed behaviour rather than documented contract.

### Outstanding Questions

**Resolve Before Planning**

- What does `deploy --dry-run --settings-file` do? Deploy returns 0 before the import and before the post-import phase (`src/Flowline/Commands/DeployCommand.cs:335-339`), so the combination currently reports nothing and exits Success. Either reject it as an unsupported combination, or run the apply pipeline in dry-run from the pre-import hook with deploy's exit code unaffected.
- Should an all-skipped configuration apply set `Inconclusive` inside a deploy at all? `ResolvePostImportExitCode` returns 19 whenever any outcome sets that flag, and deploy then prints "Deploy landed, but verification couldn't complete" — wording about the wrong phase for a configuration skip.

### Known defects to design around

Document review found these in the original unit. They are recorded so the next planning pass starts from them rather than rediscovering them.

- `PostDeployContext` carries no settings-file path (`src/Flowline.Core/Services/IPostDeployService.cs`). The participant is a DI singleton receiving only the context, so the path needs a field there and `DeployCommand` must populate it.
- Registration order is load-bearing and documented at the site (`src/Flowline/Program.cs:335`). The participant belongs after `OrphanCleanupService`, because cleanup can deactivate a workflow, and before `PluginPackageAssemblyCheckService`, which must stay last so it observes final state.
- `ResolvePostImportExitCode` honours only `AssemblyNotRegistered` as a preferred code, so `Timeout` (16) and `ConfigInvalid` (11) cannot travel through `PostDeployOutcome`. Either extend the resolver or degrade them to `PartialSuccess` and report the real cause in output. Never throw out of `RunPostImportAsync` — that skips every later participant and the resolver.
- Deploy's import arguments are built inline in a `Cli.Wrap` lambda and nothing in either test project asserts a PAC argument list. The construction needs extracting into an `internal static` helper before the flag's presence can be tested.
- `AddIfNotNull` takes a `string[]` and is reserved for `prefixArgs` (`src/Flowline/Utils/ArgumentsBuilderExtensions.cs:27`), so the path goes through the two-argument `AddIf`.
- Deploy's `Inconclusive` console string speaks about verification, not configuration, and would need broadening alongside the exit-code doc comments.

### Sources / Research

- [`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md) — the command this extends.
- [`docs/solutions/architecture-patterns/post-deploy-service-di-fanout-protocol.md`](../solutions/architecture-patterns/post-deploy-service-di-fanout-protocol.md) — the protocol R2 registers on.
- [`docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md`](../solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md) — why state writes belong in the post-import phase.
