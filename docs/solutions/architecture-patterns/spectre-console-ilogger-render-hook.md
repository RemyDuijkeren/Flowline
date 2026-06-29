---
title: "Bridging Spectre.Console Terminal Output to ILogger via IRenderHook"
date: 2026-06-28
category: docs/solutions/architecture-patterns/
module: "Flowline.Core - Logging and Console Output"
problem_type: architecture_pattern
component: tooling
severity: medium
applies_when:
  - "Using Spectre.Console for terminal UI output"
  - "Structured logging required via ILogger"
  - "Need to capture all terminal output in logs without duplicate writes"
  - "CLI application with multiple commands and services"
tags:
  - logging
  - spectre-console
  - ilogger
  - render-hook
  - cli
  - integration
---

# Bridging Spectre.Console Terminal Output to ILogger via IRenderHook

## Context

Flowline uses two output channels: `AnsiConsole` (Spectre.Console) for rich terminal output, and Serilog via `ILogger` for structured file logging. Before this solution, getting terminal output into the log file required duplicate writes — one `console.MarkupLine(...)` call and one `logger.Log(...)` call in every command or service method that produced output.

This is tedious and fragile: developers forget the logger call, the log file drifts from what the user actually saw, and every new message becomes a two-line obligation.

Two approaches were considered:

- **Path 1 (observe-all):** Attach a hook to Spectre's render pipeline; intercept every `Markup` write and log it automatically. No per-method opt-in required.
- **Path 2 (opt-in `LoggedMarkup`):** Extend console helper methods (`Info()`, `Ok()`, etc.) to call both `AnsiConsole` and `ILogger` explicitly.

Path 2 was rejected because it still requires every call site to use the "right" method variant, and plain `MarkupLine` calls outside extensions would still be invisible to the logger. Path 1 was chosen: log everything the terminal shows, then filter out noise via level detection. The log file becomes a faithful record of the terminal session.

---

## Guidance

The implementation has three parts: the hook itself, visual prefix conventions on console extension methods, and wiring in `Program.cs`.

### 1. LoggingRenderHook

`LoggingRenderHook` implements `IRenderHook` and is attached to `AnsiConsole.Console.Pipeline`. Spectre calls `Process(...)` for every renderable before it reaches the terminal.

```csharp
// LoggingRenderHook.cs
public sealed class LoggingRenderHook(ILogger<LoggingRenderHook> logger) : IRenderHook
{
    public IEnumerable<IRenderable> Process(RenderOptions options, IEnumerable<IRenderable> renderables)
    {
        foreach (var renderable in renderables)
        {
            yield return renderable;  // always pass through first

            try
            {
                if (renderable is Markup markup)
                {
                    var text = string.Concat(((IRenderable)markup).Render(options, int.MaxValue)
                        .Select(s => s.Text)).Trim();

                    var level = DetectLevel(text);
                    if (level.HasValue)
                        logger.Log(level.Value, "{Message}", text);
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Debug, ex, "LoggingRenderHook: render extraction failed");
            }
        }
    }

    private static LogLevel? DetectLevel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith("✓ ") || text.StartsWith("· ") || text.StartsWith("↷ ") || text.StartsWith("🚀"))
            return LogLevel.Information;
        if (text.StartsWith("Warning:")) return LogLevel.Warning;
        if (text.StartsWith("Error:"))   return LogLevel.Error;
        return LogLevel.Debug;
    }
}
```

**Three design constraints in this implementation:**

**`yield return` before the try/catch body.** The `yield return renderable` must come before the logging attempt. If an exception is thrown inside an iterator *after* a yield, it propagates through Spectre's render pipeline and can abort subsequent console writes. The try/catch wraps only the post-yield logging extraction, leaving the render pipeline unaffected. This is a deliberate exception to Flowline's general "no try/catch around service calls" rule — it is valid here because (a) it guards the render pipeline (a critical path, not a service boundary) and (b) it has explicit recovery logic: log the failure at Debug level and continue iteration rather than swallowing it silently.

**`int.MaxValue` for the extraction width.** `IRenderable.Render(options, maxWidth)` must be called with `int.MaxValue` rather than `options.ConsoleSize.Width`. Using the actual console width causes word-wrap in narrow terminals (CI is typically 80 columns), embedding `\n` into the logged string and splitting one message across multiple log entries. It also crashes at width=0, which occurs when output is piped. The `int.MaxValue` width is for extraction only — the renderable was already yielded to the terminal at its natural width before this call executes.

**`ILoggerFactory` lifecycle.** `CommandApp` does not expose its `IServiceProvider` before `RunAsync`, so the hook's logger cannot be resolved from the DI container. A separate `ILoggerFactory` is created outside the container, stored, and disposed after `Log.CloseAndFlush()` to ensure proper cleanup.

### 2. Visual Prefixes on Console Extensions

Level detection relies on plain-text prefixes surviving Spectre markup stripping. `FlowlineConsoleExtensions` applies consistent prefixes:

```csharp
public static void Info(this IAnsiConsole console, string message) =>
    console.MarkupLine($"· {message}");

public static void Skip(this IAnsiConsole console, string message) =>
    console.MarkupLine($"[dim]↷ {message}[/]");

// Ok() already emits "✓ "; Done() already emits ":rocket:" → "🚀"
```

These prefixes survive `Segment.Text` extraction because `IRenderable.Render(...)` strips Spectre markup tags and returns raw text.

### 3. Wiring in Program.cs

```csharp
// Program.cs
var hookLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(serilogLogger));
AnsiConsole.Console.Pipeline.Attach(new LoggingRenderHook(
    hookLoggerFactory.CreateLogger<LoggingRenderHook>()
));

var exitCode = await app.RunAsync(args, cancellationTokenSource.Token);
Log.CloseAndFlush();
hookLoggerFactory.Dispose();
return exitCode;
```

The hook is attached before `RunAsync` and the factory is disposed after the logger is flushed. Order matters: `CloseAndFlush` first, then `Dispose`.

---

## Why This Matters

Without this pattern, every method that writes to the terminal must also call `logger.Log(...)` with equivalent content. In practice:
- Developers forget the logger call on new output paths.
- The log file shows structured events but misses plain terminal messages.
- After a failed run, the log file does not reproduce what the user saw, making diagnosis harder.

With the hook in place:
- Every `AnsiConsole.MarkupLine(...)` call is captured automatically — including calls from third-party code and Spectre's own status/progress widgets.
- The log file mirrors what the terminal showed, minus empty lines and noise (which fall through as `null` and are skipped).
- Adding a new terminal message requires no logging companion call.

The level detection gives the log file useful structure: informational progress (`✓`, `·`, `↷`, `🚀`) goes to `Information`, warnings and errors bubble up at their correct severity, and unrecognized output (debug/internal Spectre text) lands at `Debug` where it can be filtered out by the log sink's minimum level.

---

## When to Apply

Apply this pattern when all three conditions hold:

- Spectre.Console (`AnsiConsole`) and `ILogger`/Serilog coexist in the same process.
- You want the log file to automatically capture what the terminal shows, without modifying call sites.
- Opt-in per console extension method is not acceptable (too many sites, or third-party code writes to the console).

Do not apply it if:
- You only need structured log events (not terminal echo) — standard `ILogger` usage is sufficient.
- Your console output does not use `Markup` (e.g., raw `Console.WriteLine`) — `IRenderHook` only intercepts `IRenderable` writes routed through `AnsiConsole`.

---

## Examples

### Level Detection Table

| Plain text after markup stripping | Detected level |
|-----------------------------------|----------------|
| `✓ Plugin registered`             | Information    |
| `· Scanning assemblies…`          | Information    |
| `↷ Skipping unchanged resource`   | Information    |
| `🚀 Deploy complete`              | Information    |
| `Warning: no steps found`         | Warning        |
| `Error: connection failed`        | Error          |
| `[Progress indicator text]`       | Debug          |
| *(empty or whitespace)*           | *(skipped)*    |

### Testing the Hook with TestConsole

`RenderOptions` has an internal constructor and cannot be instantiated directly. Use `Spectre.Console.Testing.TestConsole`, which provides a real `Pipeline` and valid `RenderOptions`:

```csharp
[Fact]
public void Info_LogsAtInformationLevel()
{
    var entries = new List<(LogLevel Level, string Message)>();
    var logger = new CaptureLogger(entries);
    var console = new TestConsole();
    console.Pipeline.Attach(new LoggingRenderHook(logger));

    console.MarkupLine("· Scanning assemblies");

    Assert.Single(entries);
    Assert.Equal(LogLevel.Information, entries[0].Level);
    Assert.Contains("Scanning assemblies", entries[0].Message);
}
```

A minimal `CaptureLogger` is simpler than mocking `ILogger<T>` via NSubstitute because the generic `ILogger<T>.Log<TState>` overload is awkward to verify with argument matchers:

```csharp
private sealed class CaptureLogger(List<(LogLevel, string)> entries)
    : ILogger<LoggingRenderHook>
{
    public void Log<TState>(LogLevel level, EventId _, TState state,
        Exception? ex, Func<TState, Exception?, string> formatter)
        => entries.Add((level, formatter(state, ex)));

    public bool IsEnabled(LogLevel level) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
```

---

## Related

- `src/Flowline.Core/LoggingRenderHook.cs` — implementation
- `src/Flowline.Core/FlowlineConsoleExtensions.cs` — visual prefix conventions
- `src/Flowline/Program.cs` — hook wiring and `ILoggerFactory` lifecycle
- `tests/Flowline.Core.Tests/LoggingRenderHookTests.cs` — test suite
- Flowline exception-handling convention — the try/catch here is a deliberate exception to the "no try/catch around service calls" rule (see `memory/feedback_exception_handling.md`)
- [`activity-correlation-structured-logging.md`](activity-correlation-structured-logging.md) — complementary pattern: W3C TraceId correlation via ActivitySource/ActivityListener + Serilog enricher. Both patterns are wired in `Program.cs` and coexist without interference.
