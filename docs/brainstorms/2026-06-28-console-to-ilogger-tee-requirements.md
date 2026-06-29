# Console → ILogger Tee — Requirements

**Date:** 2026-06-28
**Status:** Ready for planning

---

## Problem

Two separate pains:

1. **Empty logs on crash.** When a command fails, the log file has the exception but none of the
   terminal output that preceded it. Hard to debug without that context.

2. **Duplicate-write fatigue.** Every message that should land in both the terminal and the log
   requires two calls — `output.Ok(...)` + `logger.LogInformation(...)`. Wave 1 wired ILogger
   everywhere; this is now friction on every new message.

---

## Goals

- Log file contains what the user saw on the terminal, at the right level.
- Extension methods remain the single call site — no dual writes in service/command code.
- Observe-all, filter-out: everything written to the terminal is a candidate for logging. The hook
  decides what to keep and what to skip — no opt-in required per method.

---

## Non-goals

- Opt-in logging (tagging each write explicitly with a log level).
- Logging verbose messages suppressed by `--verbose` off — if it didn't reach the terminal, it
  isn't observed. Crash context for suppressed verbose is covered by Wave 1.5.
- Capturing Spectre widget output (spinners, progress bars, tables) in the log.
- Routing ILogger → Spectre (reverse direction).
- Changing the Serilog configuration from what Wave 1 established.

---

## Scope

Two deliverables, shipped separately:

### Wave 1.5 — Minimal fix (exception context) ✓ Done

Implemented in `src/Flowline/Program.cs` via `FlushBufferedVerboseOutput` helper. On exception:
- Logs `ex.Data` entries at `Debug` level
- Logs all `VerboseOutput.Lines` at `Debug` level
- Prints `VerboseOutput.Lines` to terminal when not in verbose mode
- Prints `ex.HelpLink` if present

### Wave 2 — Full tee via `LoggingRenderHook`

Eliminates pain #2 by observing all terminal writes automatically.

**Core mechanism:**

A single `LoggingRenderHook : IRenderHook` is attached to `AnsiConsole.Console.Pipeline` after DI
builds. `IRenderHook.Process(RenderOptions, IEnumerable<IRenderable>)` intercepts every renderable:

- Always yields the renderable through unchanged — terminal output is never affected.
- If `Markup`: renders to a no-color `StringWriter` to get plain text, detects log level from the
  output prefix, logs at that level.
- If anything else (spinner, progress, table, panel): skips — non-`Markup` types are not status
  messages and would produce noise.

No `IAnsiConsole` wrapper. No new types on call sites. `IAnsiConsole` DI registration unchanged.
`TestConsole`-based tests unaffected (hook attaches to `AnsiConsole.Console`, not test instances).

**Level detection by plain-text prefix:**

After stripping markup tags via no-color rendering:

| Plain text starts with | Detected level |
|------------------------|---------------|
| `✓ ` | `Information` |
| `· ` | `Information` |
| `↷ ` | `Information` |
| `🚀` | `Information` |
| `Warning:` | `Warning` |
| `Error:` | `Error` |
| whitespace / empty | skip |
| anything else | `Debug` |

Known fragility: coupled to the prefix strings in the extension methods. If prefixes change, level
detection breaks silently. Accepted for a single-maintainer CLI.

---

## Visual changes to extension methods

`Info` and `Skip` get new prefixes to make them visually distinct from `Verbose` (currently both
render `[dim]` with no prefix) and recognisable to the hook:

| Method | New terminal output | Detected log level |
|--------|-------------------|--------------------|
| `Ok` | `[green]✓[/] {message}` | `Information` |
| `Done` | `[bold green]:rocket: {message}[/]` | `Information` |
| `Info` | `· {message}` | `Information` |
| `Skip` | `[dim]↷ {message}[/]` | `Information` |
| `Verbose` | `[dim]{message}[/]` | `Debug` |
| `Warning` | `[yellow]Warning:[/] {message}` | `Warning` |
| `Error(string)` | `[red]Error:[/] {message}` | `Error` |
| `Error(Exception)` | `WriteException(ex)` | skip — not `Markup`, already logged in exception handler |

`Info` dot: plain `·` (no `[dim]`) — normal weight to distinguish from `Skip`/`Verbose`.

---

## New type (Wave 2)

- `LoggingRenderHook` — `IRenderHook` (`Spectre.Console.Rendering`); one method; renders `Markup`
  to plain text, detects level by prefix, logs. Lives in `src/Flowline.Core/`.

---

## Migration scope (Wave 2)

| File | Change |
|------|--------|
| `src/Flowline.Core/FlowlineConsoleExtensions.cs` | Visual changes to `Info` and `Skip` only |
| `src/Flowline/Program.cs` | Attach `LoggingRenderHook` to `AnsiConsole.Console.Pipeline` after DI builds |
| New: `src/Flowline.Core/LoggingRenderHook.cs` | New type |

No changes to commands, services, or test files.

---

## Decisions

- **Approach:** `IRenderHook` observer with plain-text string matching. No `LoggedMarkup`. Observe
  all terminal output and filter — not opt-in per method.
- **Suppressed verbose:** not logged. Only what reaches the terminal is observed.
- **Non-`Markup` filter:** spinners, progress bars, tables are naturally excluded by type check.
- **Wave 1 ILogger duplicates:** deferred cleanup — not a concern for this decision.
