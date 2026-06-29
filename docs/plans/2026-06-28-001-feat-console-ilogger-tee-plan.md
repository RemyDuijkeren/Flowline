---
title: "feat: Tee console output to ILogger via LoggingRenderHook"
date: 2026-06-28
origin: docs/brainstorms/2026-06-28-console-to-ilogger-tee-requirements.md
status: ready
---

# feat: Tee console output to ILogger via LoggingRenderHook

## Summary

Attach a single `IRenderHook` to `AnsiConsole.Console.Pipeline` that observes every terminal
write, renders `Markup` renderables to plain text, detects log level from the output prefix, and
logs. Everything the user sees on screen lands in the log file at the right level. No opt-in per
method. No changes to call sites. Wave 1.5 (exception context) already shipped.

---

## Problem Frame

After Wave 1, every user-facing message requires two calls — `console.Ok(...)` for the terminal
and `logger.LogInformation(...)` for the log. This plan eliminates the duplicate by observing
terminal output rather than explicitly tagging it.

---

## Requirements

- Terminal output reaches the log file at the correct level automatically.
- Extension methods remain the single call site — no dual writes in service/command code.
- Spectre widgets (spinners, progress bars, tables) do not produce log noise.
- `TestConsole`-based tests are unaffected.

---

## Key Technical Decisions

**Observe-all, filter-out over opt-in tagging.**
The hook sees everything written to the terminal. It filters what to skip (non-`Markup` renderables)
and detects level from plain-text prefixes. This avoids requiring every extension method to
explicitly declare its log level.

**`Markup`-only filter eliminates widget noise.**
Spinners, progress bars, and tables are Spectre-specific renderable types — not `Markup`. Checking
`renderable is Markup` naturally excludes them with no additional logic.

**Level detection by plain-text prefix.**
Each extension method produces a distinct visual prefix. After rendering to a no-color
`StringWriter`, the prefix is recognisable:

| Plain text starts with | Log level |
|------------------------|-----------|
| `✓ ` | `Information` |
| `· ` | `Information` |
| `↷ ` | `Information` |
| `🚀` | `Information` |
| `Warning:` | `Warning` |
| `Error:` | `Error` |
| whitespace / empty | skip |
| anything else | `Debug` |

Known fragility: detection couples to the prefix strings in the extension methods. If prefixes
change, level detection breaks silently. Acceptable for a single-maintainer CLI.

**Suppress verbose when `--verbose` is off.**
When `--verbose` is off, `Verbose()` writes nothing to the console — the hook never sees it, so
nothing is logged. Crash context for suppressed verbose is already covered by Wave 1.5.

**No-color console for text extraction.**
Each `Markup` renderable is rendered to a fresh `StringWriter` via a no-color `AnsiConsole`
instance to strip markup tags cleanly. One instance is created per `LoggingRenderHook`; the
`StringWriter` buffer is cleared per call. Thread safety via lock.

---

## Implementation Units

### U1. LoggingRenderHook type

**Goal:** Single observer that intercepts all `AnsiConsole` writes, extracts plain text, and logs
at the detected level.

**Dependencies:** none

**Files:**
- `src/Flowline.Core/LoggingRenderHook.cs` (new)
- `tests/Flowline.Core.Tests/LoggingRenderHookTests.cs` (new)

**Approach:**
Sealed class implementing `Spectre.Console.Rendering.IRenderHook`. Constructor:
`(ILogger<LoggingRenderHook> logger)`. Initialises a no-color `IAnsiConsole` backed by a
`StringWriter` for plain-text extraction.

`Process(RenderOptions options, IEnumerable<IRenderable> renderables)`:
1. For each renderable: always `yield return` it unchanged (terminal output unaffected).
2. If `renderable is Markup`: render to the no-color console, read plain text, clear buffer, call
   `DetectLevel(text)`, log if a level is returned.
3. If not `Markup`: skip (no log).

`DetectLevel(string text)` — private method returning `LogLevel?`:
- Empty/whitespace → `null`
- Starts with `✓ `, `· `, `↷ `, or `🚀` → `Information`
- Starts with `Warning:` → `Warning`
- Starts with `Error:` → `Error`
- Anything else → `Debug`

**Patterns to follow:** `IRenderHook` from `Spectre.Console.Rendering` namespace (confirmed in
Spectre.Console 0.57.0). `RenderPipeline.Attach(IRenderHook)` is the attachment API.

**Test scenarios:**
- `Markup("[green]✓[/] msg")` → logged at `Information` with plain text `✓ msg`; renderable
  yielded through.
- `Markup("· msg")` → logged at `Information`.
- `Markup("[dim]↷ msg[/]")` → logged at `Information`.
- `Markup("[yellow]Warning:[/] msg")` → logged at `Warning`.
- `Markup("[red]Error:[/] msg")` → logged at `Error`.
- `Markup("[dim]verbose detail[/]")` → logged at `Debug` (no recognised prefix).
- Empty `Markup("")` → not logged.
- Non-`Markup` renderable (e.g., `Text`, `Rule`) → yielded through, logger not called.
- Multiple renderables in one `Process` call — each handled independently.
- Thread safety: concurrent calls do not mix buffer contents.

**Verification:** Tests pass with a mocked `ILogger`. Non-`Markup` types produce zero logger calls.

---

### U2. Visual changes to FlowlineConsoleExtensions

**Goal:** Give `Info` and `Skip` distinct prefixes so the hook can identify them and so they are
visually distinguishable from `Verbose` on the terminal.

**Dependencies:** none (independent of U1, but must ship together for level detection to work)

**Files:**
- `src/Flowline.Core/FlowlineConsoleExtensions.cs` (modify)

**Approach:**
Two one-line changes only:

- `Info`: `console.MarkupLine(message)` → `console.MarkupLine($"· {message}")`
- `Skip`: `console.MarkupLine($"[dim]{message}[/]")` → `console.MarkupLine($"[dim]↷ {message}[/]")`

No other methods change. No logging calls added anywhere.

**Test scenarios:**
- `Info("msg")` on a `TestConsole`: output contains `· msg`.
- `Skip("msg")` on a `TestConsole`: output contains `↷ msg`.
- All other extension methods: output unchanged from current.

**Verification:** `TestConsole.Output` contains the new prefixes for `Info` and `Skip`.

---

### U3. Attach hook in Program.cs

**Goal:** Wire `LoggingRenderHook` to `AnsiConsole.Console.Pipeline` so all subsequent writes are
observed.

**Dependencies:** U1

**Files:**
- `src/Flowline/Program.cs` (modify)

**Approach:**
After `app.Configure(...)` and before `app.RunAsync(...)`, resolve a logger and attach:

```
// directional
var hookLogger = app.Services.GetRequiredService<ILogger<LoggingRenderHook>>();
AnsiConsole.Console.Pipeline.Attach(new LoggingRenderHook(hookLogger));
```

`AnsiConsole.Console` is the same static instance registered in DI as `IAnsiConsole`. All
service and command writes flow through it and therefore through the hook.

Hook is attached only to the static console — `TestConsole` instances created in tests are not
affected.

**Test scenarios:**
- Manual smoke test: run `flowline status`, confirm log file contains `Ok`/`Warning` output at
  `Information`/`Warning` level.

**Verification:** Log file contains console output entries after any command run.

---

## Scope Boundaries

### In scope
- `LoggingRenderHook` in `src/Flowline.Core/`
- Visual-only changes to `Info` and `Skip` in `FlowlineConsoleExtensions`
- Hook wired in `Program.cs`

### Deferred to follow-up work
- Reviewing and removing Wave 1 ILogger calls that now duplicate terminal output
- Logging suppressed verbose messages (requires opt-in approach — out of scope by design)

### Out of scope
- Logging Spectre widget output (progress bars, spinners, tables)
- Routing ILogger → Spectre
- Changing Serilog configuration

---

## Risks & Dependencies

- **Newline handling** — `MarkupLine` appends a newline; the rendered plain text will include a
  trailing newline. Use `Trim()` before prefix matching and logging.
- **`🚀` emoji rendering** — Spectre resolves `:rocket:` to `🚀` during render. The `DetectLevel`
  match should use the resolved emoji, not the shortcode. Verify with a test.
- **Pipeline ordering** — the logging hook should be attached before any live-renderable hooks
  (Status, Progress) so it sees `Markup` writes made inside those contexts. Since live hooks
  attach and detach per-invocation and our hook is persistent, ordering is naturally correct.
