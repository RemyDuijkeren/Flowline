---
title: Stale Bool Capture in VerboseFilterHook Constructor
date: "2026-07-01"
module: VerboseFilterHook
problem_type: logic_error
component: tooling
severity: critical
symptoms:
  - VerboseRenderable not appearing in terminal output despite --verbose flag
  - Verbose render hook ignores runtime IsVerbose option
  - Subprocess verbose output permanently suppressed due to stale constructor capture
root_cause: logic_error
resolution_type: code_fix
tags: [hook-pipeline, Spectre.Console, stale-capture, initialization-order, render-hook, value-semantics]
---

## Problem

`VerboseFilterHook` captured `bool isVerbose` by value at construction time, but the hook was constructed before `app.RunAsync(args)` parsed CLI arguments. The stored bool was always `false`, so `--verbose` was silently ignored and `VerboseRenderable` was always suppressed from the terminal.

## Symptoms

- `--verbose` flag has no effect on terminal output — verbose lines never appear.
- Log file is unaffected (complete); only terminal filtering is broken.
- No exception, no warning. Silent failure.

## What Didn't Work

Passing `bool isVerbose` directly to the constructor:

```csharp
// Program.cs — BEFORE app.RunAsync(args)
AnsiConsole.Console.Pipeline.Attach(new VerboseFilterHook(runtimeOptions.IsVerbose));
// ...
var exitCode = await app.RunAsync(args, cancellationTokenSource.Token); // args parsed HERE
```

At the point of construction, `runtimeOptions.IsVerbose` is the default value (`false`). The bool is a value type — it is copied once and never updated. By the time Spectre.Console parses `--verbose` and sets `runtimeOptions.IsVerbose = true`, the hook holds a stale copy.

`SubprocessCapture` was unaffected because it held a `FlowlineRuntimeOptions` reference and called `_options.IsVerbose` per-line at runtime, not at startup.

## Solution

Change the constructor from `bool isVerbose` to `FlowlineRuntimeOptions options`. Read `options.IsVerbose` lazily inside `Process()` on each render call.

```csharp
// BEFORE — stale value copy:
public sealed class VerboseFilterHook(bool isVerbose) : IRenderHook
{
    public IEnumerable<IRenderable> Process(RenderOptions renderOptions, IEnumerable<IRenderable> renderables)
    {
        foreach (var renderable in renderables)
        {
            if (renderable is VerboseRenderable && !isVerbose)
                continue;
            yield return renderable;
        }
    }
}

// AFTER — lazy reference read:
public sealed class VerboseFilterHook(FlowlineRuntimeOptions options) : IRenderHook
{
    public IEnumerable<IRenderable> Process(RenderOptions renderOptions, IEnumerable<IRenderable> renderables)
    {
        foreach (var renderable in renderables)
        {
            if (renderable is VerboseRenderable && !options.IsVerbose)
                continue;
            yield return renderable;
        }
    }
}
```

`Program.cs` updated to pass the shared mutable options object:

```csharp
AnsiConsole.Console.Pipeline.Attach(new VerboseFilterHook(runtimeOptions));
```

### Secondary fix: `IndexOutOfRangeException` in `SetStatusWithExecutionTime`

PAC CLI sometimes emits `"Processing asynchronous operation..."` without an execution-time suffix on the first poll tick. The guard `s.StartsWith("Processing asynchronous operation...")` passes, but `s.Split("... ")` returns a 1-element array.

```csharp
// SubprocessCapture.cs — BEFORE:
var execution = s.Split("... ")[1]; // IndexOutOfRangeException when no suffix

// AFTER:
var parts = s.Split("... ", 2);
if (parts.Length < 2) return;
var execution = parts[1];
```

## Why This Works

`FlowlineRuntimeOptions` is a reference type. Passing the object to `VerboseFilterHook` gives the hook a reference to the same instance that Spectre.Console's argument binder mutates when it processes `--verbose`. Each call to `Process()` reads the current value of `options.IsVerbose` — it sees the post-parse state, not the pre-parse default.

### IRenderHook pipeline ordering (LIFO)

Hooks are invoked in reverse attachment order (last-attached = innermost):

1. `VerboseFilterHook` attached first → outermost: runs last, decides terminal pass-through.
2. `LoggingRenderHook` attached second → innermost: runs first via yield-before-log.

`LoggingRenderHook` yields each renderable before logging. Execution suspends at `yield return` until `VerboseFilterHook`'s enumerator calls `MoveNext()`. When VFH drops a `VerboseRenderable` (no `yield return`), it never calls `MoveNext()` on LRH's side — the log call in LRH after the yield is never reached for suppressed items.

This means: with the stale bool bug, VFH always let everything through → log file still complete (correct), terminal showed everything including VerboseRenderable even without `--verbose` (incorrect). With the fix: terminal is filtered correctly and log file remains complete.

## Prevention

**1. Pass reference types, not value snapshots, to long-lived hooks**

Any hook, background service, or pipeline component that outlives a configuration parse step must hold a reference to the options object, not a copy of a field. This applies to all `IRenderHook` implementations and any constructor taking a configuration value that may change after construction.

**2. Integration test pattern that would have caught this**

Unit tests using `new VerboseFilterHook(isVerbose: true)` / `new VerboseFilterHook(isVerbose: false)` directly never exercise the stale-capture scenario. The missing test:

```csharp
[Fact]
public void IsVerbose_ReadLazily_EnabledAfterConstruction()
{
    // Construct with IsVerbose = false (simulates pre-parse state)
    var options = new FlowlineRuntimeOptions { IsVerbose = false };
    var console = new TestConsole();
    console.Pipeline.Attach(new VerboseFilterHook(options));

    console.Write(new VerboseRenderable("suppressed"));
    console.Output.Should().BeEmpty();

    // Simulate args parse: --verbose sets IsVerbose = true
    options.IsVerbose = true;

    console.Write(new VerboseRenderable("visible"));
    console.Output.Should().Contain("visible");
}
```

This test is now in `VerboseFilterHookTests.cs` alongside an integration test confirming VerboseRenderable is still logged by `LoggingRenderHook` even when suppressed from terminal.

**3. Defensive Split for external process output**

When splitting output from external tools (PAC CLI, any subprocess), never index directly into the result array. Use `Split(separator, count)` with `count = 2` and guard on `parts.Length < 2`. External tools are free to omit expected suffixes.

## Related Issues

- `SubprocessCapture._options.IsVerbose` reference approach — already correct; serves as the model for the VFH fix.
- IRenderHook pipeline architecture (VFH outer + LRH inner, yield-before-log, hook registration order): [`docs/solutions/architecture-patterns/spectre-console-ilogger-render-hook.md`](../architecture-patterns/spectre-console-ilogger-render-hook.md)
