---
title: stdout and stderr split - Plan
type: feat
date: 2026-11-08
topic: stdout-stderr-split
artifact_contract: ce-unified-plan/v1
product_contract_source: ce-brainstorm
execution: code
---

# stdout and stderr split - Plan

## Goal Capsule

- **Objective:** a person or agent who sends Flowline's output to a file or a pipe gets only what Flowline did or found, while everything about the run itself (errors, warnings, hints, progress, prompts) still reaches them on the terminal or in a separate stream.
- **Means:** split output between stdout and stderr along the clig.dev line, in one change.
- **Product authority:** this plan resolves finding N1 of `docs/reviews/2026-11-02-clig-guidelines-review.md`, which was deferred there; N1's step 1 and step 2 are both in scope here.
- **Open blockers:** none.

---

## Product Contract

### Summary

Flowline writes its result and the steps it took to stdout, and writes errors, warnings, hints, verbose detail, spinners and prompts to stderr. Interactive use at a terminal looks the same as today. The change ships once, as a breaking change.

### Problem Frame

Today nearly all Flowline output goes to stdout: failures, the log pointer, warnings, hints and spinner frames sit in the same stream as the status grid or the deploy steps. `flowline drift > report.txt` stores an error message as if it were the report, and a CI step or agent that captures stdout has to scrape around warnings to find the result. The only clean signal is the exit code. The telemetry disclosure is the one message already on stderr (`src/Flowline/Diagnostics/TelemetryDisclosure.cs:30`).

The review parked this over Windows PowerShell 5.1, which can stop a script on captured stderr. Flowline's users run it from bash, `pwsh`, GitHub Actions and Azure DevOps, not from Windows PowerShell 5.1 tasks, which removes that objection.

### Actors

- A1. A person at a terminal, sometimes redirecting stdout to a file.
- A2. An AI agent running Flowline headless, reading both streams and the exit code.
- A3. A CI pipeline (GitHub Actions, Azure DevOps) capturing the job log.

### Key Decisions

- **Follow the clig.dev split, not dotnet 10's.** stdout is the command's primary output; messages about the run go to stderr. (session-settled: user-directed — chosen over the dotnet 10 split, errors-only, and an opt-out switch: users don't run Windows PowerShell 5.1, so the git-style split's main risk doesn't apply.) Governs R1–R7.
- **An action command's steps are its primary output.** For push, pull, deploy, clone and provision, the step and outcome lines stay on stdout; only the animation moves. (session-settled: user-directed — chosen over git's empty stdout for action commands: "if you change state, tell the user", and a redirected log should record what happened.) Governs R2, R5.
- **Warnings, hints and verbose detail go to stderr.** (session-settled: user-approved — chosen over keeping them on stdout next to their step: stdout stays limited to what Flowline did or found.) Governs R3.
- **Redirecting stdout doesn't make a run unattended.** (session-settled: user-approved — chosen over keeping today's rule that redirected stdout counts as headless.) Governs R6.
- **One change, one release.** (session-settled: user-directed — chosen over shipping errors-first and the rest later: users adjust pipelines once.) Governs R9.
- **The line between stdout and stderr is decided by what a message says, not by its current style.** Today one message style carries both step lines and next-step hints (for example `src/Flowline/Commands/DeployCommand.cs:216`, `src/Flowline/Commands/ProvisionCommand.cs:133`), so each line is placed by R2 and R3, not by which helper prints it. Governs R2, R3.

### Requirements

**What goes to stdout**

- R1. Result data goes to stdout: the status grid, the drift and diff reports, `--help` and `--version`.
- R2. Step and outcome lines of action commands go to stdout: lines that say what Flowline did or found.

**What goes to stderr**

- R3. Errors, warnings, hints and verbose detail go to stderr: lines about the run that say what failed, what to watch out for, or what to do next.
- R4. The failure output (the `Error:` line, its context, the help pointer and the log pointer) goes to stderr, including Spectre's parse-error rendering.
- R5. Spinners and progress indicators render on stderr, and animate only when stderr is a terminal.
- R6. Prompts and confirmations render on stderr, and still appear when stdout is redirected while a person is at the terminal; agents and CI stay non-interactive.
- R7. A command that reports a partial failure and carries on writes that line to stderr; the run continues and its exit code is unchanged.

**Unchanged behaviour**

- R8. Colour on each stream is decided by that stream: redirecting one never strips colour from the other while it is still a terminal.
- R9. Exit codes, the log file's content and order, and the telemetry disclosure's stream are unchanged.

**Communicating the change**

- R10. The CHANGELOG carries a Breaking entry, and the wiki's CI guidance says that Azure DevOps PowerShell steps need `pwsh: true`, and that `failOnStderr: true` fails on warnings.

### Acceptance Examples

- AE1. **Covers R1, R3.** **Given** a repo with drift, **when** `flowline drift > report.txt` runs, **then** `report.txt` holds only the drift report, and any warning appears on the terminal.
- AE2. **Covers R2, R5.** **When** `flowline push > push.log` runs at a terminal, **then** the spinner animates on screen and `push.log` holds the step lines with no spinner frames.
- AE3. **Covers R4, R9.** **When** `flowline deploy` (target missing) runs with `2>/dev/null`, **then** stdout is empty and the exit code is the same as today.
- AE4. **Covers R6.** **When** `flowline deploy prod > deploy.log` runs at a terminal without `--yes`, **then** the confirmation prompt appears on screen and the answer is honoured; **when** an agent runs the same command with no terminal, **then** it behaves as today's non-interactive run.
- AE5. **Covers R7.** **Given** `settings capture` fails for one environment, **when** it continues with the others, **then** the failure line and the closing warning are on stderr, and the step lines for the other environments are on stdout.
- AE6. **Covers R8.** **When** stdout is redirected and stderr is a terminal, **then** warnings and errors keep their colour.

### Success Criteria

- Running any command with `2>/dev/null` shows only results and steps; running it with `>/dev/null` shows only errors, warnings, hints, progress and prompts.
- At a terminal with neither stream redirected, output looks the same as before the change.

### Scope Boundaries

- A machine-readable result mode (for example structured output from `drift` or `status`) is deferred. It's the bigger win for agents, and this split keeps its stdout clean when it lands.
- A quiet flag is out of scope.
- No escape hatch that puts everything back on stdout.
- Windows PowerShell 5.1 hosts are not supported targets for successful runs that emit warnings; the wiki note in R10 covers them.

### Dependencies / Assumptions

- Users run Flowline from bash, `pwsh`, GitHub Actions or Azure DevOps, not Windows PowerShell 5.1 tasks (user-stated).
- Mainstream agent harnesses read both streams. A harness that captures only stdout would miss warnings on successful runs; errors still show through the exit code.

### Outstanding Questions

**Deferred to Planning**

- How Spectre decides interactivity and terminal width for a console on stderr, and what R6 needs beyond it. Today every interactivity check reads one shared console profile.
- How the log file keeps recording both streams in order (R9); today it hooks the single shared console (`src/Flowline.Core/Console/LoggingRenderHook.cs:7`).
- Which existing lines of each command fall under R2 versus R3, reviewed command by command.

### Sources / Research

- `docs/reviews/2026-11-02-clig-guidelines-review.md` N1: current stdout paths, Windows and CI host behaviour with Microsoft Learn links.
- clig.dev, The Basics and Output: "The primary output for your command should go to `stdout`", "Log messages, errors, and so on should all be sent to `stderr`", "If `stdout` is not an interactive terminal, don't display any animations", "If you change state, tell the user."
- dotnet 10 breaking change, non-command output to stderr: https://learn.microsoft.com/dotnet/core/compatibility/sdk/10.0/dotnet-cli-stderr-output
- Probed locally: git writes progress to stderr on success, pac writes everything to stdout, dotnet 10 splits its own error line to stderr.
