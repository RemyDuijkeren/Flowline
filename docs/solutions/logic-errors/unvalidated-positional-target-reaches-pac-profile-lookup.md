---
title: A positional target that is neither a role nor a URL reaches the PAC profile lookup and comes back as an auth error
date: 2026-10-31
category: logic-errors
module: flowline-cli
problem_type: logic_error
component: tooling
symptoms:
  - "`flowline settings toggle ApprovalFlow prod --off` exits 4 with \"No PAC auth profile found for ApprovalFlow\""
  - "The error tells the user to run `pac auth create --environment ApprovalFlow`, against a component name"
  - "With a universal PAC profile active, the run goes on to an environment lookup against the bare word instead of failing"
  - "A typo'd role keyword (`staging`) gets the same authentication advice"
root_cause: missing_validation
resolution_type: code_fix
severity: medium
tags:
  - positional-target
  - environment-resolution
  - pac-profile
  - exit-codes
  - settings
  - drift
---

# A positional target that is neither a role nor a URL reaches the PAC profile lookup and comes back as an auth error

## Problem

Commands that take the environment as a positional `<target>` resolve a role keyword (`dev`, `test`, `uat`, `prod`) and treat every other token as a URL. A token that is neither, such as a component name typed in the environment's place or a mistyped role, went straight to PAC profile resolution. That lookup answered with an authentication error that pointed the user at the wrong fix.

## Symptoms

- `flowline settings toggle ApprovalFlow prod --off` (component and environment swapped) exits 4, `NotAuthenticated`, printing `No PAC auth profile found for ApprovalFlow` and `Run: pac auth create --environment ApprovalFlow --name "..."`.
- With a universal PAC profile active, the profile lookup falls back to it and the run continues to an environment lookup against `ApprovalFlow` rather than stopping.
- A typo such as `staging` gets the same advice. An agent reading exit 4 follows the documented recovery for that code, `pac auth create`, which cannot succeed.

## What Didn't Work

- The ideation that proposed keeping the component name as a second positional expected a swapped order to "fail because the component name is neither a role nor a URL", and marked that as unverified (`docs/ideation/2026-10-31-cli-surface-consistency-and-boundary-ideation.html`, idea 5, Downsides). Tracing the code showed it did fail, but in the profile lookup and with exit 4. Failing is not enough; the failure has to name the real mistake.
- The settings role check only rejected a role keyword used outside a project. It had no branch for "not a role and not a URL", so the token fell through as a URL.

## Solution

Reject the token before any profile lookup, with exit 15 (`ValidationFailed`), and say what a target is. All four settings verbs route through one role check, so the guard lives there (`src/Flowline/Commands/SettingsCommandBase.cs:70-83`):

```csharp
var role = DriftCommand.TryResolveRole(target);

if (standalone && role is not null)
    throw new FlowlineException(ExitCode.ConfigInvalid, SettingsSupport.BuildStandaloneRoleError(target));

if (role is null && !SettingsSupport.IsEnvironmentTarget(target))
    throw new FlowlineException(ExitCode.ValidationFailed,
        SettingsSupport.BuildNotAnEnvironmentError(target, name, args ?? []));
```

The predicate is a role or an absolute `http`/`https` URI (`src/Flowline/Commands/SettingsSupport.cs:234-237`). A bare host with no scheme is rejected, the same rule `--env` applies.

When the second positional is itself a role or URL, the two were swapped, so the message prints the caller's own invocation with the two exchanged (`src/Flowline/Commands/SettingsSupport.cs:249-266`). The tokens are found by value, not position, because an option may precede the positionals, and any `--value` is shown as `<value>` so a secret never lands in a log:

```text
Error: 'ApprovalFlow' isn't an environment, and 'prod' is. The environment comes first:
'flowline settings toggle prod ApprovalFlow --off'.
```

Otherwise:

```text
Error: 'ApprovalFlow' isn't an environment. Pass a role (dev, test, uat, prod) or the full https:// URL.
```

Tests: `tests/Flowline.Tests/SettingsCommandTests.cs:582` (swapped line) and `tests/Flowline.Tests/SettingsCommandSurfaceTests.cs:678` (exit 15 through the role check).

## Why This Works

The profile lookup never validates its input as a URL. `ProfileResolutionService.ResolveAsync` (`src/Flowline.Core/Environments/ProfileResolutionService.cs:55-68`) matches the token against each profile's resource URL, falls back to the active universal profile when nothing matches (`src/Flowline.Core/Dataverse/DataverseConnector.cs:671-676`), and otherwise throws `NotAuthenticated` with a `pac auth create --environment <token>` suggestion (`ProfileResolutionService.cs:254-260`). From its side, "no profile for this URL" is the only reading available. The caller is the only place that knows the token was meant to be an environment, so the caller has to check the shape before handing it over.

## Prevention

The guard exists in three places, each written separately, and one command still lacks it:

| Path | Guard |
|---|---|
| `--env` on push, pull, generate, clone, init, provision | `src/Flowline.Core/Environments/EnvironmentTargetResolver.cs:58-63` |
| `deploy <target>` | `src/Flowline/Commands/DeployCommand.cs:409-414` |
| `settings apply/capture/toggle/set <target>` | `src/Flowline/Commands/SettingsCommandBase.cs:78-80` |
| `drift <target>` | **None.** `src/Flowline/Commands/DriftCommand.cs:180-184` passes a non-role token straight to the profile lookup |

- Any new command that takes an environment positional checks role-or-http(s)-URL before calling the profile lookup, and fails with exit 15, never 4.
- `drift staging` still reproduces the original symptom. Fixing it is a one-line call to the same predicate; consolidating the three copies into one shared check is the larger cleanup that would stop the next command from missing it.
- Adding a role keyword means updating the role parser that all of these read through (`DriftCommand.TryResolveRole` delegates to `EnvironmentRoles.TryParse`); see `docs/solutions/conventions/flowline-add-environment-2026-06-06.md`.
- Exit 4 promises that `pac auth create` is the fix (`docs/solutions/architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md`). Reserve it for inputs that really are environment URLs.

Related: `docs/solutions/integration-issues/linux-pac-profile-casing-and-token-cache-mismatch.md` (another case where the profile lookup's error points at the wrong fix).
