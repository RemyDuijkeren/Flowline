---
title: Environment Config Command (`flowline config` / `flowline set`)
date: 2026-06-12
status: planned
planned_in: docs/plans/2026-09-05-1332-feat-environment-configure-command-plan.md
origin: docs/Features/FR-environment-config.md
---

# Environment Config Command

> **Planned.** The command design in this document is superseded by
> [`docs/plans/2026-09-05-1332-feat-environment-configure-command-plan.md`](../plans/2026-09-05-1332-feat-environment-configure-command-plan.md),
> which settles the surface as a single `flowline configure` command. The SDK notes below remain accurate.

## Problem

No Flowline command to apply per-environment runtime settings after deploy:

- **SecureConfig** — per-environment secret on plugin steps; intentionally excluded from solutions
- **Step enabled/disabled** — disable a noisy step in staging; keep steps off until post-cutover
- **Workflow enabled/disabled** — same need for classic workflows

Neither Daxif nor spkl solve this.

---

## Command Design

Two viable surface options. Both are valid Spectre.Console.Cli implementations.

### Option A — Verbs as subcommands

```
flowline config enable <env> --step "OnCreate_Account"
flowline config disable <env> --step "OnCreate_Account"
flowline config enable <env> --workflow "Send Welcome Email"
flowline config secure-config <env> --step "OnCreate_Account" --value "abc123"
```

Each verb (`enable`, `disable`, `secure-config`) is a registered subcommand. Env is `[CommandArgument(0)]`.
Pros: per-action `--help`, reads naturally. Cons: env arg comes after verb (differs from `flowline deploy <env>`).

### Option B — Flat single command

```
flowline config <env> --step "OnCreate_Account" --disabled
flowline config <env> --workflow "Send Welcome Email" --enabled
flowline config <env> --step "OnCreate_Account" --secure-config "abc123"
```

Single leaf command. `--enabled`/`--disabled` are bool switches (mutual exclusion validation required).
Pros: consistent with other Flowline commands (env always first positional), one help page.
Cons: no per-action `--help`.

### Command name: `config` vs `set`

`set` is more consistent with Flowline's verb-first identity (`push`, `sync`, `deploy`, `provision`).
`config` is more familiar to Power Platform developers who use PAC daily (`pac telemetry enable/disable`).
Either works — `set` is slightly stronger for Flowline's own identity.

---

## Behaviour

### SecureConfig

Operates on `sdkmessageprocessingstep` → `sdkmessageprocessingstepsecureconfig`:

- Step has no SecureConfig record → create and associate
- Step has SecureConfig record → update value

CI usage: `flowline config prod --step "OnCreate_Account" --secure-config "${{ secrets.APIKEY }}"`

### Step enabled/disabled

`SetStateRequest` on `sdkmessageprocessingstep`:
- Enabled: `statecode=0, statuscode=1`
- Disabled: `statecode=1, statuscode=2`

### Workflow enabled/disabled

`SetStateRequest` on `workflow`. Deactivate first if currently Active before state transition.

### Name matching

Match by name, case-insensitive. Non-unique name across solution → fail with list of duplicates.

---

## `IsDisabled` on `[Step]` Attribute

Declare a step as disabled by default in source:

```csharp
[Step(Message.Create, Stage.PostOperation, IsDisabled = true)]
public class OnCreate_Account : IPlugin { ... }
```

`flowline push` respects this — calls `SetStateRequest` if `IsDisabled = true`. Attribute is
authoritative and reapplied on every push. `flowline config` always wins at runtime until next push.

---

## Out of Scope

- UnsecureConfig (version-controlled in source via `[Step]` attribute)
- Bulk config via file
- Key Vault integration (inject secrets as env vars in CI before calling Flowline)
