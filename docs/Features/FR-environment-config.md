# Feature Request: `flowline config`

## Problem

After deploying a solution to staging or production, there is no Flowline command to apply
per-environment runtime settings:

- **SecureConfig** — the per-environment secret on plugin steps, intentionally excluded from
  solutions and never exported.
- **Step enabled/disabled** — disable a noisy step in staging, or keep steps off until
  post-cutover.
- **Workflow enabled/disabled** — same need for classic workflows.

Neither Daxif nor Spkl solve this. Spkl parses `SecureConfiguration` from its attribute but
silently drops it — never written to Dataverse.

---

## Command Design Options

Two viable designs for the command surface. Both are fully implementable with
Spectre.Console.Cli. The difference is whether enable/disable are subcommands or flags.

Note: in Spectre.Console.Cli, command names are fixed registered strings — dynamic values
like the environment must be `[CommandArgument]` positionals, not path segments. This means
the environment argument always comes after the subcommand name in Option 1.

---

### Option 1 — Verbs as subcommands

```
flowline config enable <env> --step <stepName>
flowline config disable <env> --step <stepName>
flowline config enable <env> --workflow <workflowName>
flowline config disable <env> --workflow <workflowName>
flowline config secure-config <env> --step <stepName> --value <secret>
```

**Examples:**
```
flowline config enable staging --step "OnCreate_Account"
flowline config disable prod --step "OnCreate_Account"
flowline config enable prod --workflow "Send Welcome Email"
flowline config secure-config prod --step "OnCreate_Account" --value "abc123"
```

`enable`, `disable`, and `secure-config` are registered as subcommands under `config`.
Each has `[CommandArgument(0, "<environment>")]` for the env.

**Pros:**
- Each subcommand gets its own `--help` page.
- Reads naturally — the verb is explicit and upfront.
- `flowline config --help` lists all three actions clearly.

**Cons:**
- Environment arg comes after the verb, which differs slightly from other Flowline commands
  (`flowline deploy <env>`) where env is always the first positional.

---

### Option 4 — Flat single command

```
flowline config <env> --step <stepName> --enabled
flowline config <env> --step <stepName> --disabled
flowline config <env> --workflow <workflowName> --enabled
flowline config <env> --workflow <workflowName> --disabled
flowline config <env> --step <stepName> --secure-config <secret>
```

**Examples:**
```
flowline config staging --step "OnCreate_Account" --disabled
flowline config prod --step "OnCreate_Account" --enabled
flowline config prod --workflow "Send Welcome Email" --enabled
flowline config prod --step "OnCreate_Account" --secure-config "abc123"
```

`config` is a single leaf command. `--enabled`/`--disabled` are bool switch options.
Environment is `[CommandArgument(0, "<environment>")]`, consistent with `flowline deploy <env>`.

**Pros:**
- Consistent with other Flowline commands — environment is always the first positional.
- Less typing, one help page covers everything.
- Simpler to implement — one `CommandSettings` class.

**Cons:**
- No per-action `--help`. All options live on one command.
- `--enabled` and `--disabled` as separate switches require mutual exclusion validation
  (can't pass both).

---

## Command Name: `set` vs `config`

The command name is undecided. Two candidates:

| | `flowline set` | `flowline config` |
|---|---|---|
| **Reads naturally** | `flowline set prod --step "X" --disabled` — verb is unambiguous | `flowline config prod --step "X" --disabled` — noun-as-command, no explicit verb |
| **Flowline consistency** | ✅ All existing Flowline commands are verbs: `push`, `sync`, `deploy`, `provision` | ❌ `config` breaks the verb-first pattern |
| **PAC CLI alignment** | ❌ PAC is noun-first: `pac env`, `pac solution`, `pac plugin` | ✅ Familiar pattern for Power Platform developers |
| **PAC Option 1 fit** | — | ✅ `flowline config enable/disable` mirrors `pac telemetry enable/disable` exactly |
| **Semantic precision** | ✅ `set` is idempotent by convention — works whether the value exists or not (SecureConfig record may not exist yet) | ⚠️ `config` implies configuring the tool itself (cf. `git config`, `npm config`) rather than a target environment |
| **Discoverability** | ⚠️ Less obvious what it covers without `--help` | ✅ Developers recognise `config` as "where settings live" |
| **Future `get` counterpart** | ✅ `flowline get staging --step "X"` pairs naturally | ⚠️ `flowline config` doesn't pair as cleanly with a read command |
| **Rejected alternatives** | `set-config`, `change`, `change-setting` — all worse than `set` | `config-set` (compound, awkward), `set-config` (reads as subcommand not top-level command) |

**Summary:** `set` is more consistent within Flowline's own verb-first identity. `config` is more familiar to Power Platform developers who use PAC daily. Since Flowline's identity is deliberately different from PAC (it wraps and extends it), `set` is the slightly stronger choice — but either works.

---

## Behaviour (both options)

### SecureConfig

- Looks up the step by name in the target environment.
- If the step has no SecureConfig record yet: creates one and associates it.
- If the step already has a SecureConfig record: updates the value.

In CI, inject the value from a pipeline secret:
```
flowline config prod --step "OnCreate_Account" --secure-config "${{ secrets.APIKEY }}"
```

### Step enabled/disabled

Uses `SetStateRequest` on `sdkmessageprocessingstep`:
- Enabled: `statecode=0, statuscode=1`
- Disabled: `statecode=1, statuscode=2`

### Workflow enabled/disabled

Uses `SetStateRequest` on `workflow`. Deactivates first if currently Active before
transitioning to a different state.

---

## Name Matching

`--step` and `--workflow` match by name, case-insensitive. If the name is not unique across
the solution, the command fails with an error listing the duplicates — the user must qualify
with the full registered name.

---

## `IsDisabled` on the Step Attribute

It is useful to declare a step as disabled by default directly in source — for example, a
step that should never run in DEV, or one that is deployed inactive and enabled manually
after cutover.

Adding `IsDisabled` (default `false`) to the `[Step]` attribute covers this:

```csharp
[Step(Message.Create, Stage.PostOperation, IsDisabled = true)]
public class OnCreate_Account : IPlugin { ... }
```

`flowline push` respects this when registering or updating the step — it calls
`SetStateRequest` to disabled if `IsDisabled = true`. The attribute is the authoritative
source and is reapplied on every push.

`flowline config` always wins at runtime — it overrides the attribute value in the
environment until the next `push`.

---

## Implementation Notes

- Auth: same PAC CLI token cache as all other Flowline commands — no new auth needed.
- SecureConfig entity: `sdkmessageprocessingstepsecureconfig`, field `secureconfig`.
  Associate via `sdkmessageprocessingstepsecureconfigid` on the step.
- Step state: `SetStateRequest` on `sdkmessageprocessingstep`.
- Workflow state: `SetStateRequest` on `workflow`. Deactivate before state change if
  currently Active.
- No solution context required — operates directly on the environment.

---

## Out of Scope

- UnsecureConfig (version-controlled in source via `[Step]` attribute — not per-environment)
- Bulk config via file (can always script multiple `flowline config` calls in CI)
- Key Vault integration (inject secrets as env vars in CI before calling Flowline)
