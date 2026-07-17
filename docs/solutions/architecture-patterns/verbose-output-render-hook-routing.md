---
title: "Route Verbose Output Through the Render-Hook Pipeline, Never a Hand-Rolled Guard"
date: 2026-07-09
last_updated: 2026-07-17
category: docs/solutions/architecture-patterns/
module: "Flowline.Core / Flowline - Console Output and Verbose Logging"
problem_type: architecture_pattern
component: tooling
severity: medium
applies_when:
  - "Adding a new call site that conditionally prints a Spectre.Console renderable (Tree, Panel, Table, Markup) based on --verbose or similar terminal-only gating"
  - "A method has an early `if (!opt.IsVerbose) return;` guard before building and printing a renderable — check whether skipping the guard also skips logging the content to the audit trail"
  - "Hand-rolling verbose/non-verbose branching in application code (`if (isVerbose) { console.MarkupLine(...) } else { logger.LogDebug(...) }`) instead of routing through the existing IRenderHook pipeline"
  - "A render call needs to always be logged to file regardless of terminal visibility, but should only ever be shown on the terminal when verbose"
  - "Writing or reviewing unit tests that construct a bare `new TestConsole()` to assert on verbose-gated output or logger calls"
  - "A constructor parameter of type FlowlineRuntimeOptions/bool isVerbose looks unused after routing verbose logic through render hooks — check for CS9113 (parameter is unread) to confirm it is now dead code"
tags: [verbose-logging, render-hook, spectre-console, audit-trail, test-hygiene, dead-code, ilogger]
related_components: [PluginService, WebResourceService, SubprocessCapture]
---

# Route Verbose Output Through the Render-Hook Pipeline, Never a Hand-Rolled Guard

## Context

Flowline already has a render-hook pipeline (documented in [`spectre-console-ilogger-render-hook.md`](spectre-console-ilogger-render-hook.md)) built from two `IRenderHook`s attached to the console's `Pipeline`:

- **`VerboseFilterHook`** (`src/Flowline.Core/Console/VerboseFilterHook.cs`) — drops renderables marked as verbose-only from the terminal unless `--verbose` is passed.
- **`LoggingRenderHook`** (`src/Flowline.Core/Console/LoggingRenderHook.cs`) — logs every renderable's rendered text to the log file unconditionally, because it `yield return`s the renderable *before* attempting to log it, so terminal suppression can never block logging.

These two hooks already cleanly separate two concerns that look like one: *terminal visibility* and *audit-trail completeness*. Several call sites conflated them anyway, bypassing the pipeline entirely:

1. `PluginService.WriteSnapshotVerbose` and the equivalent methods in `WebResourceService` built a Spectre `Tree` and called `console.Write(tree)` directly, guarded by `if (!opt.IsVerbose) return;` **before the tree was even built**. When not verbose, nothing was logged and nothing was shown — the guard didn't just hide terminal noise (correct), it deleted the audit trail (not correct).
2. `SubprocessCapture.Apply` (`src/Flowline/Diagnostics/SubprocessCapture.cs`) hand-rolled the exact same if/else the hook pipeline already provides generically, duplicating logic that every new call site would otherwise have to remember to reimplement correctly.

Both are the same underlying mistake: verbosity gating implemented in application code instead of delegated to the hook pipeline that already exists for this purpose.

## Guidance

**1. Generalize the existing wrapper type — don't add a parallel one.**

The marker type `VerboseMarkup` originally wrapped only a `Markup` string, so `VerboseFilterHook` could identify "this is verbose-only" by type. It couldn't wrap a `Tree`. The instinct to add a *new* type (e.g. one specifically for `Tree`/`Panel`) would require updating both hooks' type checks to also recognize it. Instead, the type itself was extended with a second constructor accepting any `IRenderable`, and renamed `VerboseRenderable` (the old name was misleading once it could wrap non-Markup content). Neither hook needed to change, because both already check by type name, not by inspecting wrapped content:

```csharp
// Console/VerboseRenderable.cs — extended, not replaced
public sealed class VerboseRenderable : IRenderable
{
    private readonly IRenderable _inner;
    private readonly bool _appendLineBreak;

    public VerboseRenderable(string message)
    {
        _inner = new Markup($"[dim]{Markup.Escape(message)}[/]");
        _appendLineBreak = true;
    }

    public VerboseRenderable(IRenderable renderable)
    {
        _inner = renderable;
        _appendLineBreak = false;
    }
    // Measure/Render delegate to _inner
}
```

```csharp
// VerboseFilterHook.Process — unchanged
if (renderable is VerboseRenderable && !options.IsVerbose)
    continue;

// LoggingRenderHook.Process — unchanged
if (renderable is Markup or VerboseRenderable or Tree or Panel or Table)
{
    var text = string.Concat(renderable.Render(options, int.MaxValue).Select(s => s.Text)).Trim();
    // ... detect level, log unconditionally ...
}
```

`FlowlineConsoleExtensions` got a sibling overload so any `IRenderable` gets the same treatment as a plain string:

```csharp
public static void Verbose(this IAnsiConsole console, string message) => console.Write(new VerboseRenderable(message));
public static void Verbose(this IAnsiConsole console, IRenderable renderable) => console.Write(new VerboseRenderable(renderable));
```

**2. Replace the early-return guard with always-build, hook-gated-display.**

```csharp
// BEFORE — deletes the audit trail when not verbose
void WriteSnapshotVerbose(RegistrationSnapshot snapshot)
{
    if (!opt.IsVerbose) return;
    var tree = new Tree("[dim]Dataverse snapshot[/]") { Style = Style.Parse("dim") };
    // ...build tree...
    console.Write(tree);
}

// AFTER (PluginService.cs)
void WriteSnapshotVerbose(RegistrationSnapshot snapshot)
{
    var tree = new Tree("[dim]Dataverse snapshot[/]") { Style = Style.Parse("dim") };
    // ...build tree...
    console.Verbose(tree);
}
```

The tree is always built and passed through the hook pipeline: `LoggingRenderHook` logs it unconditionally (complete audit trail regardless of `--verbose`); `VerboseFilterHook` alone decides terminal visibility.

**3. Watch for call sites that aren't a pure verbose gate.**

`PluginService.WritePlanTree` always shows its plan tree during `RunMode.DryRun`, regardless of `--verbose` — a dry-run preview must always be visible, that's the point of dry-run — and only requires verbose in Normal mode. `VerboseFilterHook` has no concept of `RunMode`, so blindly swapping every `console.Write(tree)` to `console.Verbose(tree)` would wrongly suppress the dry-run preview when not verbose. The fix branches explicitly on the mode instead of trying to make the hook mode-aware:

```csharp
if (runMode == RunMode.DryRun)
{
    console.Write(tree); // always visible during dry-run, verbose or not
    console.Ok($"Dry run: {plan.TotalDeletes + cascadeDeleteCount} delete(s), {creates} create(s), {updates} update(s). Run without --dry-run to apply.");
}
else
{
    console.Verbose(tree); // Normal mode: hook-gated, always logged
}
```

**4. Collapse hand-rolled duplication into the one-line idiom.**

```csharp
// BEFORE — SubprocessCapture.Apply
if (_options.IsVerbose)
{
    var display = lineTransform != null ? lineTransform(line) : line;
    _console.MarkupLine($"[dim]{Markup.Escape(prefix)}: {Markup.Escape(display)}[/]");
}
else
{
    _logger.LogDebug("{Prefix}: {Line}", prefix, line);
}

// AFTER
var display = lineTransform != null ? lineTransform(line) : line;
_console.Verbose($"[dim]{Markup.Escape(prefix)}: {Markup.Escape(display)}[/]");
```

Its now-dead `ILogger<SubprocessCapture>` and `FlowlineRuntimeOptions` constructor parameters were removed entirely, since neither was read anywhere else in the class — the hook pipeline does the logging now.

**5. Gotcha: a bare `new TestConsole()` in tests silently loses verbose-gating behavior.**

Because suppression now happens purely at the render-hook layer, not in application code, a test that constructs `new TestConsole()` without attaching `VerboseFilterHook` (and `LoggingRenderHook`, if logging matters to the assertion) to its `.Pipeline` will not reproduce production behavior — `console.Verbose(...)` calls just print unfiltered. Two tests in `SubprocessCaptureTests` broke this way because they asserted against a `CaptureLogger` mock passed directly to the old constructor, a mock the simplified class no longer calls at all. Fix: attach the same hook `Program.cs` attaches in production, in the test constructor, and assert against terminal-visible output instead of a disconnected logger mock:

```csharp
public class SubprocessCaptureTests
{
    readonly TestConsole _console = new();
    readonly FlowlineRuntimeOptions _options = new();

    public SubprocessCaptureTests()
    {
        // Matches Program.cs wiring — verbose gating is VerboseFilterHook's job, not SubprocessCapture's.
        _console.Pipeline.Attach(new VerboseFilterHook(_options));
    }
    // ...
    // _console.Output.Should().BeEmpty();      // IsVerbose = false
    // _console.Output.Should().Contain("...");  // IsVerbose = true
}
```

This mirrors a pattern already established in [`stale-bool-capture-hook-construction.md`](../logic-errors/stale-bool-capture-hook-construction.md), but had not previously been applied to `SubprocessCaptureTests` — and applies equally to `PluginServiceTests`/`WebResourceServiceTests` once their `WriteSnapshotVerbose`/`WritePlanReport` methods route through `console.Verbose(...)`.

**6. Follow-on signal: a dead `opt`/`isVerbose` constructor parameter marks a class that fully adopted the pattern.**

Once verbosity gating moves entirely to the render-hook layer, a class that only used `FlowlineRuntimeOptions.IsVerbose` (or a bare `bool isVerbose`) to gate its own console writes no longer needs that dependency at all. The C# compiler's `CS9113` ("parameter is unread") warning is a live, mechanical signal for this — confirmed via a full `--no-incremental` rebuild showing zero remaining occurrences of these now-dead params across `PluginService`, `WebResourceService`, `DataverseContextGenerator`, plus (apparently already dead from an earlier incomplete pass of the same cleanup) `PluginPlanner`, `PluginExecutor`, `WebResourcePlanner`, `DataverseConnector`, `OrphanCleanupService`, `PluginAssemblyReader`, `ProfileResolutionService`, `XrmContextGenerator`, `PacGenerator`, `XrmContext3Generator`, `XrmContextRunner`.

## Why This Matters

- **Audit-trail completeness.** With the hook pipeline as the single gate, `--verbose` only ever controls terminal noise. The log file always has the full detail record, regardless of how the CLI was invoked — the early-return pattern silently broke that guarantee for exactly the diagnostic content (snapshots, plan trees) an audit trail exists to capture.
- **One idiom instead of N hand-rolled variants.** Every call site that needs "show detail only with --verbose, but always log it" writes the same one-liner (`console.Verbose(...)`) instead of reimplementing the if/else and risking a subtly wrong version (e.g. forgetting to log, or gating the log by the same flag as the display).
- **A live compiler signal for over-parameterization.** Once this pattern is adopted consistently, `CS9113` on an `opt`/`isVerbose` parameter is a mechanical, zero-cost way to find classes that no longer need runtime-options awareness at all — no separate audit tooling required.

## When to Apply

- A method gates an entire block of work (not just a single print) behind `if (!opt.IsVerbose) return;` — check whether that also silently drops the content from the log file.
- Application code manually branches `if (verbose) { print } else { log }` instead of calling `console.Verbose(...)` once.
- Adding a new `IRenderable` (custom widget, `Panel`, `Table`) that should participate in verbose gating — extend/reuse `VerboseRenderable`, don't invent a parallel wrapper.
- Writing or reviewing a unit test for any class that calls `console.Verbose(...)` — the test's `TestConsole` needs `VerboseFilterHook` (and `LoggingRenderHook`, if the assertion cares about logging) attached to `.Pipeline`, since production wiring only happens in `Program.cs`.
- Do **not** apply this pattern by rote to a call site whose visibility rule depends on something other than `--verbose` (e.g. `RunMode.DryRun`) — branch explicitly instead of forcing it through the hook.

## Examples

**Non-pure gate — do NOT swap blindly:**
A method that must show output unconditionally in one mode (e.g. `RunMode.DryRun`) and only-if-verbose in another cannot be a single `console.Verbose(...)` call — it needs an explicit branch on the mode, keeping `console.Write(tree)` for the always-visible path and `console.Verbose(tree)` for the gated path (see Guidance item 3, `PluginService.WritePlanTree`).

**Test wiring for any class using `console.Verbose(...)`:**
Attach `VerboseFilterHook` (and `LoggingRenderHook` if the test needs to assert on logged output) to a `TestConsole().Pipeline` in the constructor, matching `Program.cs`'s production wiring — otherwise verbose-gating tests pass or fail for the wrong reason, or don't reproduce production behavior at all.

*(session history)* A prior session (2026-07-04, unrelated `flowline status` feature work) had already flagged `spectre-console-ilogger-render-hook.md`'s embedded `LoggingRenderHook` code sample as out of date against the real source — an early independent signal of the same drift confirmed and addressed alongside this learning.

## Related

- [`spectre-console-ilogger-render-hook.md`](spectre-console-ilogger-render-hook.md) — the parent architecture this learning extends (documents `LoggingRenderHook`'s original design). Its file-path citations drifted after the `src/Flowline.Core/Console/` folder move and were refreshed on 2026-07-17; the embedded code sample and prefix-detection content still match current source.
- [`../logic-errors/stale-bool-capture-hook-construction.md`](../logic-errors/stale-bool-capture-hook-construction.md) — sibling incident about `VerboseFilterHook`'s constructor; already reflects the `VerboseRenderable` rename and established the `TestConsole.Pipeline.Attach(VerboseFilterHook)` test pattern this learning reused.
- `src/Flowline.Core/Console/VerboseRenderable.cs`, `src/Flowline.Core/Console/VerboseFilterHook.cs`, `src/Flowline.Core/Console/LoggingRenderHook.cs`, `src/Flowline.Core/Console/FlowlineConsoleExtensions.cs` — implementation
- `src/Flowline.Core/Plugins/PluginService.cs`, `src/Flowline.Core/WebResources/WebResourceService.cs`, `src/Flowline/Diagnostics/SubprocessCapture.cs` — call sites fixed
- `tests/Flowline.Tests/SubprocessCaptureTests.cs`, `tests/Flowline.Core.Tests/PluginServiceTests.cs`, `tests/Flowline.Core.Tests/WebResourceServiceTests.cs` — test wiring fixed
- Commit `0a3d34c` — `refactor(verbose-routing): route verbose output through render-hook pipeline, drop dead IsVerbose params` (40 files changed, 1051 tests passing)
