---
title: "feat: Add Wave 1 CLI observability (run log, subprocess buffer, ILogger)"
type: feat
date: 2026-06-26
origin: docs/brainstorms/2026-06-25-cli-observability-wave1-requirements.md
---

# feat: Add Wave 1 CLI observability (run log, subprocess buffer, ILogger)

## Summary

Adds three observability features that together ensure every failed Flowline invocation leaves a durable, inspectable trace — without requiring `--verbose` in advance:

- **I1 — Run log**: always-on JSONL record per invocation, written to `%LOCALAPPDATA%/Flowline/runs/<date>.jsonl` on both success and failure.
- **I2 — Verbose output buffer**: rolling 50-line buffer of all verbose output (subprocess and Flowline's own) via a new `FlowlineConsoleExtensions.Verbose` overload; surfaced in the terminal on non-verbose failures.
- **I3 — ILogger infrastructure**: MEL + Serilog file sink writing to `%LOCALAPPDATA%/Flowline/logs/<date>.log` at Debug level, with `LogInformation` milestones in `PluginService`, `WebResourceService`, and a new `SolutionDiffService` to prove end-to-end injection.

---

## Problem Frame

When a Flowline command fails on a CI server or a developer machine that wasn't running `--verbose`, there is currently no diagnostic path beyond re-running with `--verbose` and hoping the failure reproduces. The result is bug reports with no context. (see origin: docs/brainstorms/2026-06-25-cli-observability-wave1-requirements.md)

---

## Requirements

**Run Log (I1)**

- R1. Every `FlowlineCommand.ExecuteAsync` invocation appends one JSONL record to `<root>/runs/<yyyy-MM-dd>.jsonl` on success and failure alike. `--help` and `--version` are excluded.
- R2. Each record contains: UTC timestamp, command name, args (redacted), exit code, duration in ms, Flowline version, cached tool versions (dotnet, pac, git), path to today's ILogger log file (`log_file`), and — on failure — exception type, message, and subprocess output.
- R3. Storage root follows the same resolution chain as `ValidationCacheStore.GetDefaultCachePath()`: `%LOCALAPPDATA%` → `XDG_CACHE_HOME` → `~/.cache` → system temp.
- R4. Files older than 30 days are deleted at startup. Directory created on first use.
- R5. Args are redacted via the existing `RedactSensitiveArgs` before any write.
- R6. On command failure, the exception handler appends a dim `Run log: <path>` line after the error output.

**Subprocess Capture (I2)**

- R7. `WithToolExecutionLog` maintains a rolling 50-line buffer of subprocess output (stderr primary; stdout lines matching error patterns also captured).
- R8. On non-zero subprocess exit, the buffer is attached to the thrown `FlowlineException` and rendered in the terminal between the Flowline error message and the run-log path line, using dim verbose style.
- R9. When `--verbose` was active, the terminal rendering is omitted — output was already printed live. Buffer contents are still included in the JSONL record.
- R10. Buffer contents are written to the JSONL record under `subprocess_output`.

**ILogger Infrastructure (I3)**

- R11. `Microsoft.Extensions.Logging` is registered in DI at startup.
- R12. A Serilog file sink writes to `<root>/logs/<yyyy-MM-dd>.log` at Debug level, always-on. No ILogger output goes to the terminal.
- R13. `ILogger<T>` is constructor-injected into `PluginService`, `WebResourceService`, and `SolutionDiffService`.
- R14. Wave 1 adds `LogInformation` outcome lines at key decision points in those three services (~5–8 call sites): step registration counts, web resource discovery totals, diff summaries. Verifies end-to-end injection.

**Operational Resilience**

- R16. Log and debug file write failures are silent and fire-and-forget. Must not surface exceptions or affect command outcome.
- R17. Log directory creation failures are silent and do not prevent the command from running.

---

## Key Technical Decisions

- **Serilog as the file sink provider.** A hand-rolled `ILoggerProvider` would be ~70 lines, but logging failures are silent by design — a subtle bug produces no file and no indication. Serilog's production track record transfers that reliability for three lightweight packages (`Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`).

- **MEL namespace-level minimum levels.** At `Debug`, MEL and `Microsoft.PowerPlatform.Dataverse.Client` emit internal startup noise. Configure Serilog with `MinimumLevel.Override("Microsoft", Warning)` and `MinimumLevel.Override("System", Warning)`, defaulting to `Debug` for everything else (Flowline namespaces). Resolves the "MEL framework noise" open question from the origin doc.

- **`SolutionDiffService` created as a new DI service in `src/Flowline/Services/`.** The brainstorm names `SolutionDiffService` as the third injection target, but that class doesn't exist — `SolutionChangeSummary` in `src/Flowline/Utils/` is a static utility called directly in `SyncCommand`. Plan: create `SolutionDiffService` in `src/Flowline/Services/` (CLI project, not Core — placing it in Core would create a circular project reference since `SolutionChangeSummary` lives in the CLI project). `SyncCommand` switches to constructor-injected `SolutionDiffService`.

- **Verbose output buffered via existing `FlowlineConsoleExtensions.Verbose` overload.** `FlowlineConsoleExtensions` already has `Verbose(this IAnsiConsole, string, bool isVerbose)`. A new overload `Verbose(this IAnsiConsole, string, FlowlineRuntimeOptions)` buffers into `FlowlineRuntimeOptions.VerboseOutput` when `!IsVerbose`, or writes to the console immediately when `IsVerbose`. This captures both subprocess output and Flowline's own verbose lines in the same rolling 50-line buffer — no per-call buffer management. `WithToolExecutionLog` gains a `FlowlineRuntimeOptions` overload that uses this internally; commands pass `RuntimeOptions` instead of a `SubprocessBuffer`. `FlowlineException` needs no changes. The global exception handler reads `runtimeOptions.VerboseOutput.Lines` on failure.

- **JSONL write integration in `Program.cs`, wrapping `app.RunAsync`.** A closure captures `(exceptionType, exceptionMessage)` from the `SetExceptionHandler` callback; verbose output comes from `runtimeOptions.VerboseOutput.Lines`. A `Stopwatch` wraps `RunAsync`. After `RunAsync` returns, `RunLogService.AppendAsync` writes the record — this single write point handles both success and failure paths. `--help`/`-h`/`--version` are excluded by checking `args` before starting the timer.

- **30-day retention cleanup at startup.** Called fire-and-forget before `app.RunAsync`. Simple and reliable; on-write cleanup would add latency to every invocation.

- **Redaction scope unchanged.** `RedactSensitiveArgs` covers `--client-secret` and `/mfaClientSecret:`. No URL-embedded tokens or connection string fragments are in active use; scope extension deferred to the issue that introduces them.

- **`FlowlineStoragePaths` static helper for all log paths.** Mirrors `ValidationCacheStore.GetDefaultCachePath()` root resolution but returns subdirectory paths for `runs/` and `logs/`. Lives in `src/Flowline/Utils/`, shared by `RunLogService` (I1) and the Serilog registration (I3).

- **`Microsoft.Extensions.Logging.Abstractions` in `Flowline.Core.csproj`.** `PluginService` and `WebResourceService` live in `Flowline.Core`. They need `ILogger<T>` at compile time, which comes from the abstractions package. The full MEL stack (console, DI) stays in `Flowline` (the CLI entry project).

---

## High-Level Technical Design

Failure flow with all three features active:

```mermaid
flowchart TB
  A[Program.cs: start Stopwatch\ncreate RunLogService] --> B[app.RunAsync]
  B --> C[FlowlineCommand.ExecuteAsync]
  C --> D[WithToolExecutionLog\n+ SubprocessBuffer fills]
  D --> E{subprocess exit?}
  E -->|zero| F[command succeeds\nreturn 0]
  E -->|non-zero| G[throw FlowlineException\n.WithSubprocessBuffer\nbuffer, isVerbose]
  G --> H[SetExceptionHandler\ncapture ex info in closure\nprint error message\nif not verbose: render buffer\nprint dim 'Run log: path']
  H --> I[RunAsync returns exit code]
  F --> I
  I --> J[RunLogService.AppendAsync\nJSONL record\nexit_code, duration, exception,\nsubprocess_output, log_file]
  J --> K[ILogger file sink\nlogs/ written throughout]
```

---

## Scope Boundaries

**Deferred for later (Wave 2+):**
- `LogDebug` and `LogWarning` call sites — Wave 2
- Correlation ID via `FLOWLINE_TRACE_ID` — Wave 2
- `DiagnosticContext` stage chain — Wave 2
- Crash-initiated support bundle — Wave 3

**Outside Wave 1:**
- Remote telemetry to App Insights
- Log encryption or signing
- `flowline doctor` / `flowline bug-report` commands
- `--help` / `--version` JSONL entries
- Debug log namespace filtering beyond the Serilog override already planned

---

## Acceptance Examples

- AE1. **Non-verbose failure — buffer visible.** Given `flowline deploy` without `--verbose`; PAC CLI exits 1 with 3 stderr lines. Then terminal shows: Flowline error message → 3 PAC lines (dim) → dim `Run log: <path>` line. JSONL record written with `subprocess_output`.

- AE2. **Verbose failure — buffer suppressed.** Given `flowline deploy --verbose`; PAC CLI exits 1, stderr printed live. Then terminal shows: Flowline error message → dim `Run log: <path>` line. PAC output does not appear twice. JSONL record still has `subprocess_output`.

- AE3. **Successful run — no path line.** Given `flowline sync` succeeds. Then no path line is printed; JSONL record written silently. `exit_code: 0`, no exception fields.

- AE4. **Log write failure — command unaffected.** Given `<root>/runs/` is not writable. Then the command exits with its own code. No exception thrown from the log write; no error printed about the log.

---

## Implementation Units

### U1. Package setup and Serilog file-sink registration

**Goal:** Add Serilog packages, wire MEL + file sink in DI, add `FlowlineStoragePaths` helper for log directory resolution.

**Requirements:** R11, R12, R16, R17

**Dependencies:** none

**Files:**
- `Directory.Packages.props` — add `Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Microsoft.Extensions.Logging.Abstractions`
- `src/Flowline/Flowline.csproj` — add Serilog package references
- `src/Flowline.Core/Flowline.Core.csproj` — add `Microsoft.Extensions.Logging.Abstractions` reference
- `src/Flowline/Utils/FlowlineStoragePaths.cs` — new file
- `src/Flowline/Program.cs` — register `services.AddLogging(...)` with Serilog file sink

**Approach:**
- `FlowlineStoragePaths` mirrors `ValidationCacheStore.GetDefaultCachePath()` root resolution: `%LOCALAPPDATA%` → `XDG_CACHE_HOME` → `~/.cache` → `Path.GetTempPath()`. Exposes `GetStorageRoot()`, `GetRunsPath(DateOnly date)`, and `GetLogsPath(DateOnly date)`.
- Serilog configured with `WriteTo.File(path, rollingInterval: RollingInterval.Infinite)` pointing to `FlowlineStoragePaths.GetLogsPath(today)`. Using `RollingInterval.Infinite` because the path already embeds today's date — `RollingInterval.Day` would append a second date suffix, producing a double-dated filename that wouldn't match the `log_file` field. Minimum level: `Debug`; overrides: `Warning` for `Microsoft.*` and `System.*`.
- `services.AddLogging(b => b.ClearProviders().AddSerilog(...))` replaces any default console-to-logger wiring. No `AddConsole` — all terminal output stays through Spectre.Console.
- Serilog logger is created before `services.Build()` and disposed after `app.RunAsync` returns.

**Patterns to follow:** `ValidationCacheStore.GetDefaultCachePath()` in `src/Flowline/Validation/ValidationCacheStore.cs:53-70` for root resolution. DI registration in `src/Flowline/Program.cs:28-43`.

**Test scenarios:**
- `FlowlineStoragePaths.GetStorageRoot()` returns a path under `%LOCALAPPDATA%` on Windows when that env var is set.
- `FlowlineStoragePaths.GetStorageRoot()` falls back to `~/.cache` when `%LOCALAPPDATA%` is empty and `XDG_CACHE_HOME` is unset.
- `GetRunsPath(today)` returns a path ending with `runs/<yyyy-MM-dd>.jsonl`.
- `GetLogsPath(today)` returns a path ending with `logs/<yyyy-MM-dd>.log`.
- Test expectation for the Serilog wiring itself: integration — the debug log file is created and non-empty after a command runs (verified in U5 integration test).

**Verification:** `dotnet build` passes. `Directory.Packages.props` has the four new package entries. `FlowlineStoragePaths` compiles and has unit tests passing.

---

### U2. ILogger injection and SolutionDiffService

**Goal:** Add `ILogger<T>` to `PluginService`, `WebResourceService`, and a new `SolutionDiffService`. Add ~5–8 `LogInformation` call sites for domain milestones. Wire `SyncCommand` to use `SolutionDiffService`.

**Requirements:** R13, R14

**Dependencies:** U1 (MEL abstractions must be referenced in `Flowline.Core.csproj`)

**Files:**
- `src/Flowline.Core/Services/PluginService.cs` — add `ILogger<PluginService>` parameter + call sites
- `src/Flowline.Core/Services/WebResourceService.cs` — add `ILogger<WebResourceService>` parameter + call sites
- `src/Flowline/Services/SolutionDiffService.cs` — new file (in CLI project; see Risks)
- `src/Flowline/Commands/SyncCommand.cs` — add `SolutionDiffService` constructor parameter; replace static `SolutionChangeSummary.ComputeAsync` calls
- `src/Flowline/Program.cs` — register `SolutionDiffService` as singleton
- `src/Flowline/Infrastructure/FlowlineRuntimeOptions.cs` — add `string? CommandName` property
- `src/Flowline/Commands/FlowlineCommand.cs` — store `CommandContext.Name` in `RuntimeOptions.CommandName` at the top of `ExecuteAsync`

**Approach:**
- `FlowlineRuntimeOptions`: add `public string? CommandName { get; set; }` property. `FlowlineCommand.ExecuteAsync` sets `RuntimeOptions.CommandName = context.Name` at the top of each invocation. This gives the Program.cs JSONL write site a resolved command name without parsing `args[]` (which is fragile under aliases and flag ordering).
- `PluginService` constructor: add `ILogger<PluginService> logger` (after existing parameters). `LogInformation` sites: (a) step registration count at end of `SyncSolutionAsync` plan phase ("Registration plan ready: {PluginTypeCount} types, {StepCount} steps"), (b) assembly sync outcome ("Assembly '{Name}' synced").
- `WebResourceService` constructor: add `ILogger<WebResourceService> logger`. `LogInformation` sites: (a) snapshot totals after load ("Snapshot: {DataverseCount} Dataverse, {LocalCount} local resources"), (b) plan totals ("Plan: {Creates} creates, {Updates} updates, {Deletes} deletes").
- `SolutionDiffService` is a thin wrapper: constructor takes `ILogger<SolutionDiffService> logger`. Method `ComputeAsync(srcFolder, workingDirectory, verbose, ct)` delegates to `SolutionChangeSummary.ComputeAsync` and logs: "Diff computed: {TotalFiles} files, +{LinesAdded} -{LinesRemoved} lines". Returns `SolutionChangeSummary`.
- `SyncCommand` adds `SolutionDiffService solutionDiffService` to primary constructor; both `SolutionChangeSummary.ComputeAsync` call sites (lines 56 and 147) replaced with `solutionDiffService.ComputeAsync(...)`.

**Patterns to follow:** Existing constructor injection in `PluginService` and `WebResourceService`. `services.AddSingleton<PluginService>()` pattern in `Program.cs:40-41`.

**Test scenarios:**
- `SolutionDiffService.ComputeAsync` calls `ComputeAsync` on the underlying static utility and returns the result.
- `SolutionDiffService` logs one `LogInformation` line with file count and line totals after a successful compute.
- `SolutionDiffService` does not throw when `SolutionChangeSummary.ComputeAsync` returns an empty result (zero files).
- `PluginService` logs step registration count after plan phase completes.
- `WebResourceService` logs snapshot totals after load.
- Existing `PluginServiceTests` and `WebResourceServiceTests` still pass after adding the logger parameter (pass `NullLogger<T>.Instance` in test setup).

**Verification:** `dotnet build` passes. `SolutionDiffService` is registered in DI and received by `SyncCommand`. Existing service tests pass with null logger.

---

### U3. Verbose output buffer via FlowlineConsoleExtensions overload

**Goal:** Add a rolling 50-line verbose output buffer to `FlowlineRuntimeOptions`. Add a `Verbose` overload to the existing `FlowlineConsoleExtensions` that buffers when non-verbose. Update `WithToolExecutionLog` with a `FlowlineRuntimeOptions` overload so all subprocess output flows through the same buffer automatically. Remove old `SubprocessBuffer` artifacts.

**Requirements:** R7, R8, R9, R10

**Dependencies:** none (independent of U1/U2)

**Files:**
- `src/Flowline.Core/FlowlineRuntimeOptions.cs` — add `VerboseOutput` rolling 50-line buffer
- `src/Flowline.Core/FlowlineConsoleExtensions.cs` — add `Verbose(this IAnsiConsole, string, FlowlineRuntimeOptions)` overload
- `src/Flowline/Utils/CommandExtensions.cs` — add `WithToolExecutionLog` overload taking `FlowlineRuntimeOptions`; remove `SubprocessBuffer` parameter
- `src/Flowline.Core/FlowlineException.cs` — remove `SubprocessOutput` property and `WithSubprocessBuffer` method (if added in a prior pass)
- `src/Flowline/Utils/SubprocessBuffer.cs` — delete (if created in a prior pass)
- `tests/Flowline.Tests/Utils/SubprocessBufferTests.cs` — delete (if created in a prior pass)

**Approach:**
- `FlowlineRuntimeOptions.VerboseOutput`: a simple rolling 50-line queue as a nested value type or small internal class. `Append(string markup)` drops oldest when at cap. `Lines` returns `IReadOnlyList<string>`. `Clear()` allows flush-and-reset.
- New `Verbose` overload in `FlowlineConsoleExtensions`:
  - `if (options.IsVerbose)`: call `console.MarkupLine($"[dim]{Markup.Escape(message)}[/]")`.
  - `else`: call `options.VerboseOutput.Append(message)`.
  - Existing `Verbose(this IAnsiConsole, string, bool)` overload stays unchanged — backward compat.
- `WithToolExecutionLog` gains a new overload taking `FlowlineRuntimeOptions options` instead of `bool verbose` + `SubprocessBuffer? buffer`. Internally:
  - Verbose path (`options.IsVerbose`): prints "Executing: ..." line, pipes stdout/stderr to console AND `options.VerboseOutput.Append(...)`.
  - Non-verbose path: suppresses real-time output; pipes stderr and stdout error-lines to `options.VerboseOutput.Append(...)` only.
  - Remove the old `SubprocessBuffer? buffer` parameter from the existing overload.
- `FlowlineException` needs no changes — no `SubprocessOutput` property, no `WithSubprocessBuffer`. Exception handler reads `runtimeOptions.VerboseOutput.Lines` directly on failure.

**Patterns to follow:** Existing `Verbose(this IAnsiConsole, string, bool)` in `src/Flowline.Core/FlowlineConsoleExtensions.cs`. `WithToolExecutionLog` branching logic in `src/Flowline/Utils/CommandExtensions.cs`.

**Test scenarios:**
- `VerboseOutput.Append` holds at most 50 lines; adding a 51st drops the first.
- `VerboseOutput` with fewer than 50 lines returns all lines.
- `Verbose(console, msg, options)` with `IsVerbose = true` writes to console and does NOT append to buffer.
- `Verbose(console, msg, options)` with `IsVerbose = false` appends to buffer and does NOT write to console.
- `WithToolExecutionLog(RuntimeOptions, ctx)` non-verbose: subprocess stderr appended to `VerboseOutput`, not printed live.
- `WithToolExecutionLog(RuntimeOptions, ctx)` verbose: subprocess stderr printed live and appended to `VerboseOutput`.

**Verification:** `dotnet build` passes. Unit tests pass.

---

### U4. RunLogService and JSONL writer

**Goal:** Create `RunLogService` with fire-and-forget `AppendAsync`, `RunLogRecord`, and 30-day retention cleanup.

**Requirements:** R1, R2, R3, R4, R5, R16, R17

**Dependencies:** U1 (`FlowlineStoragePaths` must exist for path resolution)

**Files:**
- `src/Flowline/Services/RunLogRecord.cs` — new file
- `src/Flowline/Services/RunLogService.cs` — new file
- `tests/Flowline.Tests/Services/RunLogServiceTests.cs` — new test file

**Approach:**
- `RunLogRecord` is a record with all R2 fields: `DateTimeOffset Timestamp`, `string CommandName`, `string ArgsRedacted`, `int ExitCode`, `long DurationMs`, `string FlowlineVersion`, `Dictionary<string, string?> ToolVersions`, `string LogFilePath`, `string? ExceptionType`, `string? ExceptionMessage`, `string[]? SubprocessOutput`. `ArgsRedacted` is typed `string` (not `string[]`) because `RedactSensitiveArgs` matches two-token patterns (e.g. `--client-secret <value>`) against a joined string — splitting back after redaction would break multi-word quoted values.
- `RunLogService` is a plain non-static class registered as a DI singleton (`services.AddSingleton<RunLogService>()`). No constructor parameters — it calls `FlowlineStoragePaths` (a static helper) directly; no injection needed for a stateless utility.
  - `AppendAsync(RunLogRecord record)` wraps all I/O in `try { } catch { }` per R16/R17. Creates directory if needed; serializes record as single-line JSON; appends with a newline.
  - `CleanOldLogsAsync(DateOnly today)` deletes `.jsonl` files in `runs/` and `.log` files in `logs/` older than 30 days. Wrapped in `try { } catch { }`.
- Args redaction in the write path: accept `string[] args` and apply `RedactSensitiveArgs` from `CommandExtensions` before storing in the record. Since `RedactSensitiveArgs` is a static private method today, it needs to be made `internal static` so `RunLogService` can call it, OR `RunLogService` can call `string.Join(" ", args)` and apply the same regex. Plan: extract to `internal static` in `CommandExtensions`.
- Tool versions: read from `new ValidationCacheStore().Load().ToolChecks` — extract `Version` for keys "dotnet", "pac", "git". The parameterless `ValidationCacheStore()` constructor resolves the default cache path automatically.

**Patterns to follow:** `ValidationCacheStore.GetDefaultCachePath()` and `ValidationCacheStore.Save()` for pattern (directory creation + file write wrapped in try/catch). `FlowlineStoragePaths` from U1.

**Test scenarios:**
- `AppendAsync` creates the runs directory if it doesn't exist.
- `AppendAsync` writes a valid JSON line to the file; a second call appends a second line (file has 2 lines).
- `AppendAsync` does not throw when the directory is not writable (R16 — wrap with read-only dir).
- `RunLogRecord` serializes `null` fields as JSON null and `string[]` fields as JSON arrays.
- `CleanOldLogsAsync` deletes files in `runs/` and `logs/` with a date in their name older than 30 days and keeps recent ones.
- `CleanOldLogsAsync` does not throw when the directory doesn't exist.
- `ArgsRedacted` field for args containing `--client-secret <secret>` serializes as `"--client-secret ***"` (secret value replaced, not the flag name).

**Verification:** Unit tests pass. `dotnet build` passes. Manual run: `%LOCALAPPDATA%/Flowline/runs/<today>.jsonl` is created after any command run (verified in U5).

---

### U5. Wire run log and verbose buffer in Program.cs and command call sites

**Goal:** Integrate `RunLogService` and `VerboseOutput` buffer into the CLI lifecycle: timing, exception handler path line and buffer flush, JSONL write, and update command call sites to pass `RuntimeOptions` to `WithToolExecutionLog` instead of explicit `SubprocessBuffer`.

**Requirements:** R1, R2, R4, R5, R6, R8, R9, R10 (integration)

**Dependencies:** U3 (VerboseOutput buffer), U4 (RunLogService)

**Files:**
- `src/Flowline/Program.cs` — timing wrapper, closure capture, JSONL write, buffer flush in exception handler, path line
- `src/Flowline/Commands/SyncCommand.cs` — switch to `WithToolExecutionLog(RuntimeOptions, ctx)`, remove explicit `SubprocessBuffer` management
- `src/Flowline/Commands/DeployCommand.cs` — same as SyncCommand
- `src/Flowline/Commands/PushCommand.cs` — same if applicable
- `tests/Flowline.Tests/FlowlineCommandTests.cs` — extend with run-log integration scenarios

**Approach:**
- In `Program.cs`:
  1. `--help`/`-h`/`--version` check: skip run log entirely if present.
  2. Start `Stopwatch` before `app.RunAsync`.
  3. In `SetExceptionHandler` closure: capture `ex.GetType().FullName` and `ex.Message`. For `FlowlineException`: print error, render `fe.Detail` if set, print HelpLink if set, **flush `runtimeOptions.VerboseOutput.Lines` to console in `[dim]` if non-empty**, print the dim `Run log: <path>` line. Capture `runtimeOptions.VerboseOutput.Lines.ToArray()` into `capturedVerboseOutput`.
  4. After `await app.RunAsync(...)`: call `await runLogService.AppendAsync(...)` with `SubprocessOutput: capturedVerboseOutput`.
  5. `runLogService.CleanOldLogsAsync(...)` fire-and-forgotten before `app.RunAsync`.
- In affected commands (`SyncCommand`, `DeployCommand`, `PushCommand`):
  - Remove explicit `var buffer = new SubprocessBuffer()` creation.
  - Replace `WithToolExecutionLog(settings.Verbose, ctx, buffer: buffer)` with `WithToolExecutionLog(RuntimeOptions, ctx)`.
  - Remove `.WithSubprocessBuffer(buffer, RuntimeOptions.IsVerbose)` from thrown exceptions.

**Patterns to follow:** Existing `SetExceptionHandler` in `src/Flowline/Program.cs`. Subprocess call pattern in `SyncCommand`.

**Test scenarios:**
- Covers AE3: after a successful `RunAsync`, the JSONL file exists and contains a record with `exit_code: 0` and no exception fields.
- Covers AE4: `RunLogService.AppendAsync` does not throw when the log directory is not writable; the command's own exit code is unaffected.
- Covers AE1: on a non-verbose failure with buffered verbose output, the exception handler renders the buffer lines in dim style before the `Run log:` line.
- Covers AE2: on a verbose failure, the buffer lines are NOT rendered by the exception handler (buffer is empty — verbose mode outputs in real-time); the `Run log:` line still appears.
- `Run log:` line is NOT printed on successful runs.
- `--help` in `args` skips JSONL write entirely.
- JSONL record for a failure contains `exception_type`, `exception_message`, and `subprocess_output` (verbose buffer lines).
- `log_file` field in the JSONL record is a path ending with `logs/<today>.log`.

**Verification:** `dotnet run -- sync --help` exits without creating a JSONL file. `dotnet run -- <any command>` creates `<root>/runs/<today>.jsonl` with one record. On a forced subprocess failure, the JSONL record has `subprocess_output` populated.

---

## Risks and Dependencies

- **`RedactSensitiveArgs` visibility.** Currently `private static` in `CommandExtensions`. Must be made `internal static` for `RunLogService` to use it. Low risk — internal to the same project.
- **Serilog dispose order.** Serilog's `Log.CloseAndFlush()` must be called after `RunLogService.AppendAsync` — not before it. The JSONL write may itself emit ILogger calls; closing the sink first would silently drop them. Correct sequence: `RunAsync` → `AppendAsync` → `CloseAndFlush`.
- **DataverseClient MEL noise.** `Microsoft.PowerPlatform.Dataverse.Client` emits at Information/Debug via MEL. The `Warning` override for `Microsoft.*` suppresses this. If specific Dataverse client logs are needed in future, a tighter override (e.g. `Microsoft.PowerPlatform.*`) can be added in Wave 2.
- **`SolutionDiffService` project placement.** `SolutionChangeSummary` is in `src/Flowline/Utils/` (the CLI project). `SolutionDiffService` cannot live in `Flowline.Core` without creating a circular project reference. Resolved in U2: `SolutionDiffService` lives in `src/Flowline/Services/` alongside `RunLogService`. It still satisfies R13 — ILogger is injected into it, and it's registered in DI in `Program.cs`.

---

## Sources and Research

- `src/Flowline/Program.cs:55-71` — `SetExceptionHandler`; I1 path line (R6) and I2 buffer rendering attach here
- `src/Flowline/Utils/CommandExtensions.cs:18-54` — `WithToolExecutionLog`; I2 buffer fills here
- `src/Flowline.Core/FlowlineException.cs:1-28` — `WithDetail` API; `SubprocessOutput` and `WithSubprocessBuffer` extend here
- `src/Flowline/Commands/FlowlineCommand.cs:43-55` — `ExecuteAsync` entry point; run log timing wraps around `app.RunAsync` in Program.cs, not here
- `src/Flowline/Validation/ValidationCacheStore.cs:53-70` — path resolution pattern reused by `FlowlineStoragePaths`
- `src/Flowline/Validation/ValidationCache.cs` — `ToolCheckResult` with `Version` field; `ToolChecks` dict keyed by "dotnet"/"pac"/"git"
- `src/Flowline/Commands/SyncCommand.cs:86-103` — PAC sync subprocess pattern; I2 buffer attachment model
- `src/Flowline/Utils/SolutionChangeSummary.cs` — static utility; `SolutionDiffService` wraps `ComputeAsync`
- `Directory.Packages.props` — `Microsoft.Extensions.Logging.Console` v10.0.9 already present; Serilog packages not yet listed
