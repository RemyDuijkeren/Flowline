---
title: Mandatory --force Specifier - Plan
type: refactor
date: 2026-07-11
topic: force-specifier
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Mandatory --force Specifier - Plan

## Goal Capsule

- **Objective:** Replace bare `-f`/`--force` with a mandatory `--force <specifier>` across the CLI, so every force-gated hazard is named explicitly and never blanket-approved by accident.
- **Product authority:** `STRATEGY.md` (Flowline targets solo/small-team Dataverse consultants running interactive and CI/non-interactive invocations); this brainstorm's dialogue.
- **Open blockers:** None. Hard break confirmed — Flowline has no prior release to preserve compatibility with.

## Product Contract

### Summary

Replace the CLI's bare `-f`/`--force` boolean with a mandatory `--force <specifier>` value on every command that gates a destructive or overwrite operation. Each hazard gets its own named specifier value; a universal `all` value approves everything a given command has; bare `--force` with no value is a hard parse error everywhere, with no exceptions and no deprecation window.

### Problem Frame

One boolean `--force` currently gates every force-issue on a command indiscriminately. On `push` alone, a single `--force` silently approves up to four unrelated hazards at once — including recreating a plugin assembly, which deletes and recreates all its step and image registrations and their GUIDs. A user or CI script intending to approve one low-stakes hazard has no way to avoid approving a high-stakes one in the same invocation, and the current error text ("Use --force to allow") doesn't tell them what they're actually agreeing to.

### Key Decisions

- **Hard break, no deprecation window.** Bare `--force` becomes a parse error immediately; there is no prior release to protect compatibility for.
- **Per-command specifier vocabulary, not one global list.** Each command declares only the specifier values meaningful to it, matching the `--scope` pattern already used by `push` (`PushCommand.cs:34-36`). A single shared enum across all commands was considered and rejected — `--help <command>` would either show irrelevant values or need per-command filtering anyway, at which point it's the per-command approach with extra indirection.
- **`config` is the one specifier shared across commands**, because the hazard it names — overwriting an already-set value in `.flowline` — is genuinely cross-cutting infrastructure (`FlowlineCommand.cs:144-148,195` calling into `ProjectConfig.cs`), not a command-specific operation like the others.
- **`all` always means "every specifier this command has,"** including `config` when present. It is the one universal escape hatch, but a conscious one — typing the word `all` is a deliberate choice, unlike today's silent-by-omission blanket approval.
- **push's two orphan-deletion hazards (assembly, step) are merged into one `delete-orphans` value** rather than split by blast radius, keeping the vocabulary small — a handful of fixed, known categories — rather than fragmenting by internal implementation detail.
- **Specifier names follow a verb-object shape** (`delete-orphans`, `recreate-assembly`) naming the actual Dataverse operation, matching Flowline's own internal vocabulary (e.g. `PluginService.cs` logs `"...Deleting."` for the orphan case). `form-handlers`, `dirty`, `config`, and `drift` stay single-concept nouns — there's no competing meaning elsewhere in Flowline for those, so no verb is needed to disambiguate.
- **Error messages name the exact required specifier(s)**, replacing today's generic `"...Use --force to allow"` text, so the CLI is self-documenting at the point of failure.

### Requirements

**Force specifier surface**
- R1. `--force`/`-f` takes a required value; invoking it with no value is a parse error on every command, with no fallback interpretation.
- R2. `--force <value>` is repeatable (`--force x --force y`), matching the existing `--scope` option shape (`PushCommand.cs:34-36`), not a comma-separated list.
- R3. `-f` keeps working as a short alias and also requires a value (`-f orphans`).
- R4. `--force all` is accepted on every command that has at least one specifier value, and resolves to every hazard that command gates, including `config` where present.
- R5. Passing a specifier value not valid for the current command is a validation error naming the values that are valid for that command.

**Per-command specifier vocabulary**

| R-ID | Command | Specifier values | Hazard(s) covered |
|---|---|---|---|
| R6 | `push` | `delete-orphans`, `recreate-assembly`, `form-handlers`, `config`, `all` | Delete orphaned plugin assembly/step with no local source (`PluginService.cs:277,363`); assembly identity changed or downgraded → delete & recreate, losing step/image GUIDs (`PluginService.cs:453`); remove unrecognized form event handler(s) (`FormEventExecutor.cs:124`); config overwrite |
| R7 | `sync` | `dirty`, `config`, `all` | Overwrite uncommitted local package changes (`SyncCommand.cs:68`); config overwrite |
| R8 | `deploy` | `drift`, `config`, `all` | Skip drift validation for local-only or plugin-size-mismatch changes (`DeployCommand.cs:332`); config overwrite |
| R9 | `clone`, `generate`, `provision`, `drift` | `config`, `all` | Config overwrite only |
| R10 | `status` | — | No force-gated hazard; command is unaffected |

**Error messaging**
- R11. Every force-required error names the specific specifier(s) needed for that hazard (e.g. `"...Use --force recreate-assembly to allow"`), not a generic `--force` mention.
- R12. Force-related errors follow existing tone-of-voice conventions (`docs/tone-of-voice.md`): red, direct, tell the user what to do next, no passive voice.

### Acceptance Examples

- AE1. **Covers R1, R6.** Given `push` invoked non-interactively, when `--force` is passed with no value, then the command fails with a parse error listing `push`'s valid values: `delete-orphans`, `recreate-assembly`, `form-handlers`, `config`, `all`.
- AE2. **Covers R4, R7.** Given `sync` hits both the `dirty` and `config` hazards in one run, when invoked with `--force all`, then both hazards are approved without a further prompt.
- AE3. **Covers R5, R8.** Given `deploy` invoked with `--force dirty`, when `dirty` is not one of `deploy`'s valid values, then the command fails with a validation error naming `deploy`'s actual valid values: `drift`, `config`, `all`.

### Scope Boundaries

- The `config` hazard's own interactive confirm prompt (`ConsoleHelper.Confirm`'s TTY behavior) is unchanged — only what's required to skip it non-interactively changes.
- No change to how `PluginService` or `FormEventExecutor` compute or classify hazards — only how the CLI surface gates them.
- `DeployCommand.cs:334`'s remediation-text bug (told users to run `sync` instead of `push`) was found during this brainstorm and already fixed separately, outside this plan's scope.

### Dependencies / Assumptions

- Assumes Spectre.Console.Cli's `CommandOption` supports a repeatable, per-command-typed enum array in the same shape as the existing `--scope` option (`PushCommand.cs:34-36`) — low risk, since that pattern is already working code in this repo.

### Sources / Research

- `FlowlineSettings.cs:12-14` — current `-f|--force` boolean definition, shared base for every command.
- `ConsoleHelper.cs:43-59` — `Confirm()`, the shared gate that reads `settings.Force` for non-interactive auto-accept.
- `PluginService.cs:277,363,453` — orphan assembly/step deletion and assembly recreate hazards.
- `FormEventExecutor.cs:124-147` — unrecognized form event handler removal, gated once per push (fires during both the cleanup and registration passes).
- `SyncCommand.cs:68-78` — uncommitted local change overwrite.
- `DeployCommand.cs:312-334` — drift validation skip.
- `ProjectConfig.cs:48,83,118,153,224` and `FlowlineCommand.cs:144-148,195` — cross-cutting config-overwrite hazard.
- `docs/tone-of-voice.md` — error message conventions.
- `CONCEPTS.md` (Orphan handler / Orphan priority) and `STRATEGY.md` (Drift detection track, `--no-delete` milestone) — established Flowline vocabulary that motivated disambiguating `delete-orphans` from the product's separate multi-action orphan-cleanup concept.
