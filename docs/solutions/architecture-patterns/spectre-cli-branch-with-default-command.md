---
title: "Turning a Spectre.Console.Cli command into a branch with a default command"
date: 2026-11-01
category: docs/solutions/architecture-patterns/
module: flowline-cli
problem_type: architecture_pattern
component: tooling
severity: medium
applies_when:
  - "Splitting a Flowline command into subcommands while keeping the bare command working"
  - "Declaring options that every subcommand of a branch shares"
  - "Reading CommandContext.Name in a command that sits under a branch"
  - "Relying on a Spectre parse-error suggestion reaching the user"
related_components:
  - FlowlineCommand
  - Program
  - PushSettings
  - SolutionNameSettings
retire_when: "A Spectre.Console.Cli upgrade past 0.57.2: re-run the push parse and help tests in ProgramParsingTests and PushCommandTests; if leaf help starts listing branch options, drop the pointer sentence from the subcommand descriptions"
tags:
  - spectre-console
  - cli
  - branch
  - subcommands
  - default-command
  - strict-parsing
  - help-text
---

# Turning a Spectre.Console.Cli command into a branch with a default command

## Context

`flowline push` became a branch: bare `push` is the branch's default command and pushes every target, `push plugins` and `push webresources` (alias `wr`) push one. Spectre.Console.Cli 0.57.2 supports this shape, but several of its behaviours are not obvious from the API, and one contradicts a note already in this repo. They were measured with a throwaway console app on 0.57.2 before the real change; the ones the repo's tests now pin are cited below, the rest rest on that measurement.

The settings branch plan recorded that a branch-declared option binds only before the subcommand name and is silently ignored after it (`docs/plans/2026-09-06-1132-feat-configure-inline-component-plan.md:168`). That was measured on 0.55. On 0.57.2 with strict parsing it no longer holds.

## Guidance

**Declare shared options on a typed branch.** Register with `AddBranch<TSettings>`, where `TSettings` holds the options every form takes, and derive each subcommand's settings (and the default command's) from it (`src/Flowline/Program.cs:438`, `src/Flowline/Settings/PushSettings.cs:26`). On 0.57.2 those options bind on either side of the subcommand name: `push --dry-run plugins` and `push plugins --dry-run` both work. Target-only options go on the subcommand's settings, so the parser rejects them on the wrong command with no hand-written check.

**Keep strict parsing on.** Without `StrictParsing = true` (`src/Flowline/Program.cs:412`) a flag on the wrong command, or in the wrong position, is silently ignored and the run exits 0. Every claim above holds only under strict parsing; test parse probes with it on.

**Help shows branch options in one place only.** `push -h` lists the branch's options, its subcommands and the branch examples. `push plugins -h` lists only the options declared on the subcommand's own settings, never the inherited ones. Options declared on the default command's settings never appear in `push -h` at all. Spectre has no setting for this. Flowline's answer, chosen over a custom help provider, is the docker/kubectl pattern: shared options on the branch, and each subcommand description ends with "Use 'flowline push -h' for the options every push command takes."

**Don't trust `context.Name` for messages or logs.** Under a branch it is the leaf alone (`plugins`), an alias resolves to the canonical name (`wr` gives `webresources`), and the branch's default command reports the placeholder `__default_command`. Spectre's `CommandContext` exposes no parent. `FlowlineCommand` builds the name a user typed from a branch name the command declares plus a default-command flag (`src/Flowline/Commands/FlowlineCommand.cs:86`, `:101`). The invocation log strips command words by token, not by prefix, because the typed line can hold an alias or options before the subcommand (`src/Flowline/Commands/FlowlineCommand.cs:112`).

**Spectre's "Did you mean" never reaches a Flowline user.** Spectre puts the suggestion in its rendered error, but Flowline's handler prints only the exception message (`src/Flowline/Program.cs:224`, `:235`). So `push plugin` shows a plain "Unknown command". When a slip on a subcommand name matters, the hint chain in that handler has to supply it, as `SolutionNameSettings.BuildPositionalHint` does for push targets (`src/Flowline/Settings/SolutionNameSettings.cs:60`).

**A leaf and a branch with the same name do not coexist.** Registering `AddCommand<X>("push")` and `AddBranch("push", ...)` side by side gives no error: the leaf wins and the branch's subcommands are unreachable. A default command on the branch is the only way to keep the bare name working.

Short option names are one character in 0.57.2; Spectre rejects longer ones at registration (session history).

## Why This Matters

Each of these fails quietly. The 0.55 note would have pushed the design toward redeclaring shared options on every leaf, losing order-independence for nothing. Without strict parsing a misplaced `--dry-run` would push for real. Prefix-matching `context.Name` against the logged arguments stopped stripping anything once aliases and leading options existed. A parse-error test that only checks the exit code would never notice the missing suggestion.

## When to Apply

- Adding a subcommand to an existing command, or a new branch (the next push target, PCF, is one).
- Writing an error message, a `--force` validation message or a log line that names the command.
- Checking a parse error's wording: verify it from a Release build, where the handler renders it (see `debug-build-hides-the-real-cli-error-output.md`).

## Examples

Parse probes that pin the binding, using the real branch shape with capturing commands in place of the real ones (`tests/Flowline.Tests/PushCommandTests.cs`, the parse-seam section):

```csharp
[Theory]
[InlineData("push", "plugins", "--dry-run")]
[InlineData("push", "--dry-run", "plugins")]
public void CommandApp_SharedOption_BindsOnEitherSideOfTheSubcommand(params string[] args)
```

Help assertions against the production configuration (`tests/Flowline.Tests/ProgramParsingTests.cs`): `push --help` contains the shared options and both subcommands; `push plugins --help` contains `--assembly-only`, `--path` and the `'flowline push -h'` pointer.

## Related

- `docs/solutions/developer-experience/debug-build-hides-the-real-cli-error-output.md`: the same Program.cs handler, and why parse errors are checked in Release.
- `docs/solutions/conventions/cli-surface-change-doc-sweep.md`: the doc sweep a surface change like this one needs.
- `docs/solutions/architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md`: help, exit codes and `--force` rules for agent-driven commands.
- `docs/plans/2026-11-01-1807-feat-push-subcommands-plan.md`: the plan this came from.
