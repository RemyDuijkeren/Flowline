---
name: cli-for-agents
description: How to shape Flowline's own command surface so AI agents can drive it headlessly — flag-first inputs, interactivity gates, scoped --force, --dry-run, exit-code selection, graceful stops, and help text. Use whenever adding or changing a Flowline command, a `[CommandOption]`, a prompt, a confirmation, a `FlowlineException`, an `ExitCode`, or a `.WithDescription(...)` in `src/Flowline/Commands/` or `src/Flowline/Services/`.
---

# Flowline CLI for agents

Most Flowline runs are unattended: an agent, a CI job, a script. Human-oriented patterns —
menus, timed prompts, decorative-only output — hang or lie in that context. Flowline already
has a mechanism for each rule below. **Reuse it; don't invent a parallel one.**

Scope: authoring decisions (flags, gates, exit codes, output shape).
Message *wording* is `/tone` + [docs/tone-of-voice.md](../../../docs/tone-of-voice.md). Don't restate its checklist here.

## 1. Every input expressible as a flag

Interactive is the fallback, never the requirement. Prompt only after the flag is absent
*and* the run is interactive.

```csharp
bool IsInteractive() => Console.Profile.Capabilities.Interactive;   // CloneCommand.cs:259
```

No TTY and no flag → fail immediately, naming the flag. Model:
[InitCommand.cs:102-104](../../../src/Flowline/Commands/InitCommand.cs#L102-L104).

```
Publisher prefix is required — pass --publisher-prefix <prefix>, or run this interactively to pick one.
```

Never let a `SelectionPrompt`/`TextPrompt` be reachable without that guard — an agent gets a
hung process, not an error. Same rule in services:
[CreateEnvironmentResolver.cs:57](../../../src/Flowline/Services/CreateEnvironmentResolver.cs#L57),
[ProfileResolutionService.cs:52](../../../src/Flowline/Services/ProfileResolutionService.cs#L52),
[SecretResolver.cs:30](../../../src/Flowline/Services/SecretResolver.cs#L30).

## 2. Confirmations go through `ConfirmGated`

Never hand-roll `console.Confirm(...)` for a hazard.
[FlowlineConsoleExtensions.cs:39](../../../src/Flowline.Core/Console/FlowlineConsoleExtensions.cs#L39)
(`ConfirmGatedAsync` at :56 when a `CancellationToken` is in scope) already encodes the whole
contract: `--force` skips it, non-interactive throws `ExitCode.ForceRequired` with the message
you supply.

```csharp
console.ConfirmGated(
    "Remove orphaned form handlers?",
    defaultValue: false,
    force: settings.HasForce("delete-form-handlers"),
    nonInteractiveMessage: "Orphaned form handlers found — rerun with --force delete-form-handlers to remove them.");
```

`nonInteractiveMessage` is what the agent sees. It must name the exact flag value.

## 3. `--force` is scoped and repeatable — not a boolean, and there is no `--yes`

Generic CLI advice says "add `--force` to skip confirmation". Flowline's is narrower on
purpose: `-f|--force <SPECIFIER>`, repeatable, `all` for everything the command gates
([FlowlineSettings.cs:13-38](../../../src/Flowline/FlowlineSettings.cs#L13-L38)).

- Read it with `settings.HasForce("<specifier>")` — never `settings.Force.Any()`.
- New hazard → new specifier, added to that command's `ValidForceSpecifiers` override
  ([PushCommand.cs:102](../../../src/Flowline/Commands/PushCommand.cs#L102),
  [DeployCommand.cs:63](../../../src/Flowline/Commands/DeployCommand.cs#L63),
  [SyncCommand.cs:42](../../../src/Flowline/Commands/SyncCommand.cs#L42); config-only commands
  reuse `FlowlineSettings.ConfigOnlyValidSpecifiers`). The base command validates it for you
  ([FlowlineCommand.cs:56-98](../../../src/Flowline/Commands/FlowlineCommand.cs#L56-L98)) and an
  invalid value **lists the valid ones** — that error is how an agent discovers the vocabulary.
  A specifier read by `HasForce` but missing from the list is unreachable: passing it errors out.
- Do not add `--yes`, `--assume-yes`, or a `bool Force`.

## 4. `--dry-run` on anything that writes

Preview must run the whole pre-flight and stop before the write:
[PushCommand.cs:70](../../../src/Flowline/Commands/PushCommand.cs#L70),
[DeployCommand.cs:55](../../../src/Flowline/Commands/DeployCommand.cs#L55).
Precedence is settled — dry-run wins over every other mode flag
([DeployCommand.cs:686](../../../src/Flowline/Commands/DeployCommand.cs#L686)). Follow it.

A dry run exits 0 and says so explicitly, so an agent can tell "nothing to do" from "would have
worked". Never emit a `Done(...)` that reads as if the write happened.

## 5. Exit code is the machine contract

[ExitCode.cs](../../../src/Flowline.Core/ExitCode.cs) is declared stable public API — agents
pattern-match on the numbers. Rules when throwing `FlowlineException`:

- Pick the **specific** code. `GeneralError` (1) only when nothing else fits — never as a shortcut.
- Never renumber or repurpose an existing code. New failure class → new number, with a doc comment.
- The message carries the fix, because the code alone can't. Codes whose XML doc already promises
  a corrective action must repeat it in the message: 4 → `pac auth create --environment <url>`,
  12 → commit or stash, 14 → `--force`, 17 → the specifier to pass.
- No message that is only "failed" / "error". Name the resource and the next action.

## 6. Graceful stop: `CannotContinue`, exit 0

Work that legitimately belongs elsewhere ends with
`console.CannotContinue(message, nextStep)` and `return 0`
([FlowlineConsoleExtensions.cs:13](../../../src/Flowline.Core/Console/FlowlineConsoleExtensions.cs#L13)).

Because the exit code is 0, `Next:` is the *only* signal the agent gets. It must be a runnable
command or a named place — not "try again" or "check your setup". Nothing prints after it.

## 7. Help is layered, and every command earns its description

`flowline --help` lists commands; `flowline <cmd> --help` owns the detail. Don't widen top-level
help — unused command docs stay out of the agent's context.

`.WithDescription(...)` follows **what + when to run + what changes**
([Program.cs:156-219](../../../src/Flowline/Program.cs#L156-L219) are the reference set). "Push
plugins" fails. Same for `[Description]` on every `[CommandOption]` — an undocumented flag is
invisible to an agent reading `--help`.

Every command also registers `.WithExample(...)` — examples pattern-match better than prose, so a
new command without at least one is incomplete. One argument per string:

```csharp
config.AddCommand<PushCommand>("push")
      .WithDescription("...")
      .WithExample("push")
      .WithExample("push", "ContosoCustomizations", "--scope", "webresources");
```

(A few existing calls pack several args into one string —
[Program.cs:164](../../../src/Flowline/Program.cs#L164),
[:171](../../../src/Flowline/Program.cs#L171). Don't copy that shape.)

## 8. Output: text lines + exit code, no `--json`

There is no structured-output mode and adding one isn't in scope — don't invent `--json`.
The contract an agent relies on is: exit code for the decision, `Ok`/`Skip`/`Warning`/`Error`/
`Done` lines for the detail, `Verbose` for anything only a human debugging wants.

So: never encode a result *only* in colour, a spinner, a table border, or an emoji. If the run
produced an id, url, path, or version an agent might need next, it belongs in a plain line.

## 9. Idempotency

Agents retry. Reconciling commands (`push`, `clone`, `provision`) must converge: a second run
makes Dataverse match source again and reports the unchanged parts with `console.Skip(...)` —
never a duplicate step registration, duplicate web resource, or second assembly.

Deliberate exceptions exist and stay that way: `sync` bumps the build version every run
([Program.cs:176](../../../src/Flowline/Program.cs#L176)) — that's the point of the command, not
a defect. For a state-advancing command, make the advance visible in output so a retrying agent
can tell it moved, and don't gate it behind an "already done" check.

When a step genuinely can't be repeated, the error names the recovery command.

## Review checklist

When changing a command, walk this:

- [ ] Every prompt reachable only behind `IsInteractive()`, with a flag that bypasses it
- [ ] Non-interactive + missing input → immediate error naming the flag, never a hang
- [ ] Hazard confirmations via `ConfirmGated`/`ConfirmGatedAsync`, with an actionable `nonInteractiveMessage`
- [ ] New `--force` specifier registered in the command's valid list
- [ ] Writes have `--dry-run`, and dry-run precedence is respected
- [ ] Specific `ExitCode`, message carries the fix
- [ ] `CannotContinue` `Next:` is a runnable command
- [ ] `.WithDescription` = what + when + state change; at least one `.WithExample`; every option has `[Description]`
- [ ] Result data in plain lines, not only styling
- [ ] Re-running converges (or the state advance is deliberate and visible)
