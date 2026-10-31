---
title: "Adding a new named environment role to Flowline CLI"
date: 2026-06-06
last_updated: 2026-11-01
category: docs/solutions/conventions
module: environment-management
problem_type: convention
component: tooling
severity: low
applies_when:
  - "Adding a new named environment role beyond dev, test, uat, prod (e.g. staging, demo, training)"
  - "Deciding which Dataverse environment type a new role should require"
tags:
  - environment
  - flowline-cli
  - environment-role
  - project-config
  - provision
  - deploy
  - clone
  - status
---

# Adding a new named environment role to Flowline CLI

## Context

A role (`dev`, `test`, `uat`, `prod`) is one enum plus helper switches in Core, and a handful of hard-coded role lists in commands. Most commands now read through the shared helpers, so they need no edit. The lists that remain are plain arrays and `||` chains; the compiler does not flag a missing entry in them. A forgotten list leaves that command quietly ignoring the new role.

## Guidance

### Step 1: Decide four things first

| Decision | Options | Example (UAT) |
|---|---|---|
| Dataverse type | `Production` or `Sandbox` | `Sandbox` |
| Deploy confirmation | Silent (like Test) or prompted (like Prod) | Silent |
| Provision supported? | Yes (full or minimal copy) or No | Yes, full copy |
| Position in the promotion order | Where between Dev and Prod? | After Test, before Prod |

The Dataverse type feeds `EnvironmentTarget.CheckType` (step 2). The position feeds `ResolveDtapGate` (step 6) and the status column order (step 8).

### Step 2: Core role definition and rules (`src/Flowline.Core/Environments/`)

1. `EnvironmentRole.cs`: add the enum member, add it to `All` (dev first, promotion order), and add an arm to each helper switch: `ConfigKey`, `Keyword`, `Label`, `UpperLabel`, `TryParse`. `ConfigKey`, `Keyword`, `Label` and `UpperLabel` throw on an unhandled member; `TryParse` returns null, so the role would be unreachable by keyword.
2. `EnvironmentTarget.cs`: `CheckType` needs a decision. Today prod must be a Production type and every other role must not be; a null type passes. A new Sandbox role needs no change. A new Production-typed role does. The placeholder and "isn't a role" message are built from `All`, so they follow.
3. `EnvironmentRoleInference.cs`: add keywords for the role to the keyword map. Keep `CONCEPTS.md` "Role inference" in step, since it lists every keyword.
4. `EnvironmentTargetResolver.cs`: the interactive picker for a new URL has two literal role lists (one with prod, one without). Add the role to both if it is a sandbox role the user may choose.

### Step 3: Config (`src/Flowline.Core/Config/ProjectConfig.cs`)

Add a URL property, an arm in `GetUrl` and an arm in `GetOrUpdateUrl(role, ...)` (it delegates to the shared `GetOrUpdateValue`). A named `GetOrUpdateXxxUrl` wrapper is optional: it is a one-line delegate to `GetOrUpdateUrl`, and the commands call `GetOrUpdateUrl` directly. Tests: `tests/Flowline.Core.Tests/ProjectConfigTests.cs` (null input when empty, URL when empty, null when set, same URL when set, JSON round-trip).

### Step 4: Provision (`src/Flowline/Commands/ProvisionCommand.cs`)

Provision has its own `Role` enum, separate from `EnvironmentRole`. Add the member, update the `[Description]` on the positional, and decide the copy type in `ResolveCopyType` (test and uat default to a full copy, dev to minimal). Mapping goes through `EnvironmentRoles.TryParse` and the save through `GetOrUpdateUrl`, so there is no URL switch. Provision branches from prod with `pac admin copy`, so the new role is a Sandbox here.

### Step 5: Clone (`src/Flowline/Commands/CloneCommand.cs`)

There is no per-role option. The source comes from `--env`, resolved by the shared resolver. Two hard-coded lists remain: `AnyRoleUrlConfigured` (a `||` chain over the four URLs) and the role picker in the role-resolution method (`Dev, Test, Uat`; prod is chosen by environment type, not by the picker). Add the role to both.

### Step 6: Deploy (`src/Flowline/Commands/DeployCommand.cs`)

Keyword resolution is shared, so `deploy <role>` works after step 2. Update `ResolveDtapGate`: an `is<Role>` flag, predecessor lists for the roles above the new one, and its own predecessor list. Also update `HasAnyEnvironmentUrl`, the `hasTestOrUat` line near the build-once messaging if the new role counts as a pipeline stage, and the `[Description]` on the target setting.

### Step 7: Other command surfaces

- `src/Flowline/Settings/EnvironmentSettings.cs`, `src/Flowline/Commands/DriftCommand.cs`, `SettingsApplyCommand.cs`, `SettingsCaptureCommand.cs` and `SettingsComponentCommands.cs` in the same folder: `[Description]` strings list the role keywords by hand.
- `src/Flowline/Program.cs`: command descriptions and examples (deploy, provision, drift).

### Step 8: Status and logging lists

- `src/Flowline/Commands/StatusCommand.cs`: the `envs` array, in Dev to Prod order.
- `src/Flowline/Commands/FlowlineCommand.cs` (known-host scrubber loop over the four URLs) and `src/Flowline/Diagnostics/InvocationLogger.cs` (tier list). A missed entry here leaks the new host into logs or drops it from telemetry tiers.

### Step 9: Docs

README command table, `CONCEPTS.md` keyword table, wiki `02-Project-Configuration.md` (config keys and keyword tables), `04-Command-Reference.md`, `08-Deploy.md`, and a CHANGELOG entry. Follow `docs/solutions/conventions/cli-surface-change-doc-sweep.md`.

### Step 10: Find the rest

Run `rg -n "Uat|UAT|uat" src` (substitute the nearest existing role) and walk every hit that is a list, array, `||` chain or description string. Also grep the shipped help text under `src/Flowline/Templates/` and `plugin/skills/`, which the command above does not cover well. Then run the Core and CLI test projects.

## Why This Matters

Core reads through the helpers, so a new role mostly works once step 2 is done. What fails is silent: a missing literal in the clone picker, status grid, scrubber loop or tier list. No test fails for those unless one is written.

## When to Apply

- Adding a named Sandbox role between existing ones.
- A Production-typed role also changes `CheckType` and `Infer` (which maps a Production type to prod); not verified beyond reading those two methods.

## Related

- `docs/solutions/architecture-patterns/dtap-gate-enforcement-in-deploy-command-2026-06-07.md`
- `docs/solutions/conventions/cli-surface-change-doc-sweep.md`
