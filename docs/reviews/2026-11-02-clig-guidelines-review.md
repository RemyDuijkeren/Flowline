# CLI review against clig.dev

Date: 2026-11-02
Reviewer stance: the [Command Line Interface Guidelines](https://clig.dev/), read with Flowline's
agent-first contract (`.claude/skills/cli-for-agents/SKILL.md`) in mind.
Scope: help, output streams, errors, flags, interactivity, signals, environment variables. Not
code quality, and not anything the 2026-09-08 review already decided.
Sources: clig.dev (fetched 2026-11-01), `src/Flowline/Program.cs`, the `Settings` classes under
`src/Flowline/Settings/` and `src/Flowline/Commands/`, and the `--help` output and error probes
of a Release build (v0.20.1-alpha.0.114).

How to use this file: each finding ends with a `Decision:` line. Fill it with `accept`,
`modify`, `reject` or `defer`, add a note, and the finding becomes a work item or closes.
Implemented on 2026-11-04: N3, N5, N6, N7, N9, N10. N1 and N4 deferred, N2 and N8 rejected.

Severity: **High** = users or agents get a wrong result, or a pipe carries the wrong data.
**Medium** = friction, surprise, or a hang an agent can't recover from. **Low** = polish.

Note: the push subcommands plan (`docs/plans/2026-11-01-1807-feat-push-subcommands-plan.md`) is
in flight. Nothing below depends on push's flag shape; N3 applies to whatever settings class the
push subcommands end up on.

## Summary

| ID | Finding | Severity | Decision |
|---|---|---|---|
| N1 | Errors and the log pointer go to stdout | High | defer |
| N2 | `-v` means `--version` at root and `--verbose` on subcommands | Medium | reject |
| N3 | `-f` is advertised on commands that reject it | Medium | accept |
| N4 | No `--no-input`; a pty on an unrecognised CI or agent harness hangs on a prompt | Medium | defer |
| N5 | A second Ctrl+C can't force an exit | Medium | accept |
| N6 | A missing required argument gets a bare error, no usage | Medium | modify |
| N7 | No suggestion on a mistyped command | Low | accept |
| N8 | Root help repeats every full description | Low | reject |
| N9 | Environment variables are undocumented in help | Low | modify |
| N10 | Help text inconsistencies | Low | accept |

## Already aligned

No action. Listed so the review reads as a full pass, not a partial one.

| clig.dev section | Flowline |
|---|---|
| Help | `-h`/`--help` everywhere, bare `flowline` prints help, examples on every command, docs link in the footer |
| Arguments and flags | Strict parsing: an undeclared option fails with `Unknown option` and exit 15 (`Program.cs:412`) |
| Arguments and flags | Every input has a flag; prompts sit behind the interactivity check (`FlowlineCommand.cs:256`) |
| Arguments and flags | `-f/--force` bypasses confirmations, scoped by specifier; `--dry-run` on the environment writers |
| The Basics | Exit codes mapped to failure modes and declared stable (`src/Flowline.Core/ExitCode.cs`) |
| Output | `NO_COLOR` honoured through Spectre.Console |
| Configuration | Project config in the version-controlled `.flowline`; cache and logs under `XDG_CACHE_HOME` |
| Analytics | Opt-out with `FLOWLINE_TELEMETRY_OPTOUT`, one-time disclosure on stderr (`TelemetryDisclosure.cs:30`) |

## Settled on 2026-09-08 where clig.dev says otherwise

Not reopened. Recorded so the divergence is visible as a choice.

| clig.dev | Decision |
|---|---|
| Offer `--json` / `--plain` for scripts | G6: defer until a real consumer exists |
| `-n` is the conventional dry-run short flag | C11: reject; `-n` is `--solution-name` |
| Do not read secrets from flags | C10: reject; `--client-secret` stays, PAC takes it in argv too |
| Prefer opt-in telemetry | Opt-out with disclosure and an env var, which clig.dev accepts as the fallback |
| `-f` as boolean force | Skill §3: scoped, repeatable specifier, no `--yes` |

---

## Findings

### N1. Errors and the log pointer go to stdout

clig.dev, The Basics: "Log messages, errors, and so on should all be sent to `stderr`."

**Now.** Every probe (`pusj`, `deploy` with no target, `push --bogus`, `diff --force nope`)
left stderr empty. The exception handler writes `Error:`, context lines and `Log:` to stdout
(`Program.cs:192-194`, `:211-213`, `:230-237`, `:242-244`), as does the late log pointer
(`Program.cs:321-325`). Other paths already use stderr: `PacUtils.cs:95-101`, `:610`, `:640`
and `TelemetryDisclosure.cs:30`.

**Problem.** A pipe or an agent reading stdout gets the failure mixed into the result.
`flowline diff > report.txt`, or a CI step capturing `drift` output, carries the error text
as if it were the report.

**Proposal.** Step 1: the exception handler and the log pointer write to a stderr console. One
place, no command changes. Step 2, optional: route `Warning`/`Error`/`Verbose` lines and
progress spinners to stderr too, keeping stdout for report data (status grid, diff and drift
output). Step 2 touches the console extensions in Core and the render hooks; size it separately.

**Prior analysis.** No earlier decision on where Flowline's own errors go was found in `docs/`
or the commit history. Related records: the 2026-07-03 Spectre pipeline dashboard ideation
floated moving ANSI rendering to stderr (downside noted: console setup change, nothing on
Windows); the 2026-08-05 terminal tab status plan sends escape sequences to stderr and flagged
virtual-terminal processing on the Windows stderr handle as the one fact to check; the
2026-09-20 telemetry plan put the disclosure on stderr to keep piped stdout clean on CI;
commit `6b2f7f5` removed a PAC failure message that printed on stdout as well as stderr.

**Windows and CI hosts.** The risk on Windows sits in the scripting host, not the OS. Verified
against Microsoft Learn on 2026-11-02:

| Host | stderr behaviour | Source |
|---|---|---|
| Windows PowerShell 5.1 | Native stderr becomes `ErrorRecord` objects, and `$ErrorActionPreference` applies to them, so `Stop` plus redirection (`2>&1`) can end the script on a stderr line | [Differences between Windows PowerShell 5.1 and PowerShell 7.x](https://learn.microsoft.com/powershell/scripting/whats-new/differences-from-windows-powershell#engine-changes) |
| PowerShell 7.2+ | Redirected native stderr is not written to `$Error`, and `$ErrorActionPreference` no longer affects it | [about_Preference_Variables](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_preference_variables#erroractionpreference) |
| Azure Pipelines `powershell:` step / `PowerShell@2` | Runs Windows PowerShell on Windows agents unless `pwsh: true`; prepends `$ErrorActionPreference = 'stop'` by default | [steps.powershell](https://learn.microsoft.com/azure/devops/pipelines/yaml-schema/steps-powershell#remarks), [PowerShell@2](https://learn.microsoft.com/azure/devops/pipelines/tasks/reference/powershell-v2) |
| `PowerShell@2`, `AzurePowerShell@5`, `AzureCLI@2` | `failOnStderr` / `FailOnStandardError` fails the task on any stderr data; default `false` | same pages, [AzureCLI@2](https://learn.microsoft.com/azure/devops/pipelines/tasks/reference/azure-cli-v2) |
| `PowerShell@1` (legacy) | `failOnStandardError` default `true` | [PowerShell@1](https://learn.microsoft.com/azure/devops/pipelines/tasks/reference/powershell-v1) |

Microsoft's own precedent goes the clig.dev way: since .NET 10 the `dotnet` CLI writes
non-primary output to stderr to keep stdout clean for scripts, and tells PowerShell users to run
7.2 or later ([dotnet CLI logs non-command-relevant data to stderr](https://learn.microsoft.com/dotnet/core/compatibility/sdk/10.0/dotnet-cli-stderr-output)).

What this means per step:

- **Step 1 is safe.** It writes to stderr only on runs that already exit non-zero, so a host
  that fails on stderr fails a step that was failing anyway.
- **Step 2 carries the Windows risk.** Warnings and spinners on stderr appear on successful runs.
  Under Windows PowerShell 5.1 with `Stop` and `2>&1`, or any task with `failOnStderr: true` or
  legacy `PowerShell@1`, a green deploy turns red. Keep step 2 out, or behind an opt-in.
- **An existing exposure.** The telemetry disclosure already writes to stderr on the first
  sending run per machine, which on hosted agents means once per job (`TelemetryDisclosure.cs:30`,
  telemetry plan D8). A pipeline that fails on stderr can already fail that run of Flowline
  today, on success. Worth one line in the
  CI wiki page whichever way N1 goes.

Decision: defer. Step 1 and step 2 both parked. Error output stays on stdout for now; N5 and N7 output follows it, except the Ctrl+C message (stderr).

### N2. `-v` means `--version` at root and `--verbose` on subcommands

clig.dev, Help: avoid `-v`, "since it can mean verbose or version."

**Now.** `flowline -v` prints the version (Spectre's built-in, enabled by
`SetApplicationVersion` at `Program.cs:165`). `flowline push -v` is verbose
(`Settings/FlowlineSettings.cs:9`).

**Problem.** Same letter, two meanings one position apart. `flowline -v push` reads as
"verbose push" and prints the version instead.

**Proposal.** Drop the `-v` short for version; keep `--version`. `-v` then means verbose
everywhere. Breaking only for `flowline -v`, which nothing scripts against for a decision.

Decision: reject. Keep current behaviour, same as git (`-v` is version at root, verbose on subcommands). Checked against dotnet, pac, pacx, az, git and npm on 2026-11-04.

### N3. `-f` is advertised on commands that reject it

clig.dev, Help and Subcommands: help describes what the command accepts; flags stay consistent
across subcommands.

**Now.** `-f|--force` lives on the base settings class (`Settings/FlowlineSettings.cs:13`), so
`status`, `diff`, `scaffold` and `sln add` list it in `--help`. Passing it fails:
`diff --force nope` exits 15 with "'diff' has no force-gated behavior. Remove --force." C7 moved
`--no-cache` and `-a` off these commands and left `-f`.

**Problem.** Help promises a flag the command always refuses. An agent reading help to build a
call has no way to tell.

**Proposal.** Move `-f` from the base class to the settings of commands that declare force
specifiers. The "has no force-gated behavior" check then becomes unreachable and can go.

Decision: accept. Move `-f` from the base settings to the Dataverse settings layer, the parent of every command that has force specifiers. Remove the now-unreachable "has no force-gated behavior" checks.

### N4. No `--no-input`; a pty-run agent hangs on a prompt

clig.dev, Interactivity: "If `--no-input` is passed, don't prompt or do anything interactive."

**Now.** Whether to prompt depends only on Spectre's interactivity detection
(`FlowlineCommand.cs:256`). That detection is false when stdin, stdout or stderr is redirected,
and Spectre's CI enrichers force it false on GitHub Actions, Azure Pipelines, Jenkins, GitLab and
others. Commit `a181518` (2026-08-03) moved to this and recorded the trade it accepted: a
pseudo-terminal on a CI Spectre has no enricher for (Buildkite, Drone, Woodpecker, Netlify,
Vercel set only `CI`) now blocks on a prompt instead of failing with `ForceRequired`.

**Problem.** The gap is narrow and known: a pty-allocating agent harness or an unrecognised CI.
There the run hangs instead of giving the "pass --x" error skill §1 promises. Severity is
Medium only because a hang is unrecoverable for an agent; frequency is low.

**Proposal.** Global `--no-input` plus `FLOWLINE_NO_INPUT=1`. Either one forces the
non-interactive path, so the existing flag-naming errors and `ForceRequired` fire. One check
where interactivity is decided. An explicit opt-out, not a return to the environment-variable
sniffing `a181518` removed. Reasonable to `defer` until a user hits the gap.

Decision: defer. When picked up: `FLOWLINE_NO_INPUT=1` switching the shared console to non-interactive at startup covers all prompt sites in one place.

### N5. A second Ctrl+C can't force an exit

clig.dev, Signals and control characters: exit fast, time-limit cleanup, and let a second
Ctrl+C skip it.

**Now.** The handler always cancels the termination and only signals the token
(`Program.cs:57-62`). The message is "Cancelled. Panic button acknowledged."

**Problem.** If a `pac` subprocess or a Dataverse call ignores the token, every further Ctrl+C
is swallowed too; the user has to kill the process from outside. The message also reads as a
joke, against `docs/tone-of-voice.md`.

**Proposal.** First press: cancel the token and print "Cancelling. Press Ctrl+C again to force
quit." Second press: let the process terminate. Exit code stays 130.

Decision: accept. First press cancels and prints "Cancelling. Press Ctrl+C again to force quit." on stderr; second press finishes the tab indicator, flushes telemetry and exits 130.

### N6. A missing required argument gets a bare error, no usage

clig.dev, Help: when required arguments are missing, show concise help: usage, an example,
a pointer to `--help`.

**Now.** `flowline deploy` prints "Command 'deploy' is missing required argument 'target'."
and the log pointer. No usage line, no valid values.

**Proposal.** After a missing-argument error, print the command's usage line and its first
registered example, e.g. `Usage: flowline deploy <target>` and `Example: flowline deploy prod`.
The hint chain at `Program.cs:232-235` is where it fits.

Decision: modify. Print a pointer under the error: "Run 'flowline <command> --help' for usage and examples." No usage line or example lookup.

### N7. No suggestion on a mistyped command

clig.dev, Help: suggest the correction; ask before running it, don't force it.

**Now.** `flowline pusj` prints "Unknown command 'pusj'." and exits 15.

**Proposal.** Append "Did you mean 'push'?" when one command is a close match. No auto-run.
First check whether Spectre's parse exception already carries a suggestion: the handler prints
only the message (`Program.cs:235`), so it may be discarding one.

Decision: accept. Print Spectre's formatted parse error (caret, and "Did you mean" for commands) when no Flowline hint matches. Applies to all parse errors.

### N8. Root help repeats every full description

clig.dev, Help: concise, most common first; detail behind `<cmd> --help`.

**Now.** The root command table runs about 60 lines: three to six per command, eight for
`settings`. Spectre prints the same description string in the root table and in
`<cmd> --help`, and skill §7 accepted that as a budget constraint.

**Proposal.** A first-sentence summary in the root table, full what/when/what-changes text in
`<cmd> --help`. `FlowlineHelpProvider` already customises the help output and is the place for
it. Skill §7's budget note gets a one-line update if accepted. Judgment call: skip if root help
is rarely read.

Decision: reject. No custom help work; skill §7's trade-off stands.

### N9. Environment variables are undocumented in help

clig.dev, Environment variables and Analytics: say where variables fit and make disabling
telemetry easy.

**Now.** `FLOWLINE_TELEMETRY_OPTOUT` and `PP_TOOLS_TELEMETRY_OPTOUT`
(`Diagnostics/TelemetryConsent.cs:8-9`) and `AZURE_CLIENT_SECRET`
(`Flowline.Core/Dataverse/SecretResolver.cs:24`) appear in no `--help`.

**Proposal.** A short ENVIRONMENT block in root help next to the docs footer. Minimum:
name `AZURE_CLIENT_SECRET` in the `--client-secret` description.

Decision: modify. `generate --client-secret` names `AZURE_CLIENT_SECRET`; root help footer names the telemetry opt-out. No ENVIRONMENT block.

### N10. Help text inconsistencies

One finding, five items:

- `settings set` description says "Requires a non-empty value" (`Program.cs:546`) while
  `--value` says "Omit to read the current one" (`Commands/SettingsComponentCommands.cs:995`).
  Contradiction.
- C12 half landed: the templates are `[true|false]` (`Commands/CloneCommand.cs:31`,
  `Commands/PullCommand.cs:26`) but Spectre uppercases them, so help shows `[TRUE|FALSE]`.
- `--managed` is described differently on `clone` ("--managed false means false") and `pull`
  ("--managed false resets to default", default column `True`).
- `--bump` and `--generator` have no value placeholder (`Commands/PullCommand.cs:31`,
  `Commands/GenerateCommand.cs:46`); every other valued option has one.
- `--suffix` description has a double space before "(default" (`Commands/ProvisionCommand.cs`).

Decision: accept items 1, 3, 4, 5; skip 2 (uppercase comes from Spectre, no custom help). Item 3: align the two `--managed` descriptions; the default column stays. The `--bump` placeholder does not fix the `__default_command` leak: that is Spectre's own message for any valued option missing its value at the end of the line (`--generator` too). Fixed separately with a Flowline hint that lists the option's values and points at `--help`.

---

## Considered and not raised

- `-q`/`--quiet`: `--verbose` already gates detail, and agents decide on the exit code.
- `--no-color`: `NO_COLOR` covers it.
- Man pages or a `help` subcommand: the wiki and `--help` cover it.
- `-` for stdin/stdout on file options: no command takes piped input today.
- Help examples wrapping in redirected output: terminal width, already closed by C12.
