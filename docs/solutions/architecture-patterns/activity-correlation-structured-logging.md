---
title: "W3C TraceId Correlation and Structured Invocation Logging"
date: 2026-06-29
category: docs/solutions/architecture-patterns/
module: "Flowline.Diagnostics, Flowline.Logging, Flowline.Commands"
problem_type: architecture_pattern
component: tooling
applies_when:
  - "CLI tool produces multiple log lines per command and needs them correlatable"
  - "Adding OpenTelemetry export later without changing the Activity API surface"
  - "Logging environment-specific identifiers (URLs, tenant IDs) that must not appear verbatim in CI"
  - "Command observability logic needs to be testable without constructing the full command DI graph"
  - "CI platform needs to be distinguishable in log filters, not just detected as a boolean"
tags:
  - activity-tracing
  - structured-logging
  - trace-correlation
  - serilog-enricher
  - invocation-logging
  - cli-observability
  - system-diagnostics
  - activitysource
---

## Context

Flowline CLI commands previously ran with no per-invocation correlation identifier. If a command
produced multiple log lines — setup checks, git probes, API calls, warnings — there was no way to
group them as a single invocation in a log tail or log aggregator. Debugging required eyeballing
timestamps and hoping no concurrent commands overlapped.

Wave 2 adds structured per-invocation observability. The goal: every log line produced during a
single `flowline` run carries the same `TraceId`, so a developer (or CI pipeline) can filter
`TraceId == "abc123"` and see exactly what one command did, in order, without noise from other runs.
Constraint: no OpenTelemetry export pipeline in Wave 2. Use only `System.Diagnostics` types already
in the .NET runtime — zero added dependencies.

A secondary goal was to log a structured invocation header early in each command — tool versions,
git branch, environment URL hash, CI platform — to make CI logs self-describing without requiring
external metadata injection.

The session also surfaced three latent bugs discovered during the observability wiring: a dead
`ToolCheckResult.Branch` field that caused a redundant git fork-exec on every command, an
`ActivityListener` that was created but never disposed, and the activity start happening *after*
the first log line (defeating the correlation for the most visible log entry).

---

## Guidance

### 1. Force W3C format globally at startup

In `Program.cs` top-level statements, before any logging or command execution:

```csharp
Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;
```

W3C format produces a 32-hex-char `TraceId` (`4bf92f3577b34da6a3ce929d0e0e4736`). Without
`ForceDefaultIdFormat = true`, the runtime can fall back to hierarchical format on some code paths,
producing inconsistent IDs.

### 2. Register a minimal ActivityListener — and dispose it

`ActivitySource.StartActivity()` returns `null` unless at least one `ActivityListener` with a
matching `ShouldListenTo` predicate is registered. Register one in Program.cs, held alive for the
full process lifetime via `using var`:

```csharp
using var activityListener = new ActivityListener
{
    ShouldListenTo = s => s.Name == "Flowline.CLI",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
};
ActivitySource.AddActivityListener(activityListener);
```

`using var` is the correct scope: the listener must outlive every command that runs in the process.
A bare `new ActivityListener { ... }` (no `using`) means the listener is eligible for GC during a
long command, after which `Activity.Current` becomes null mid-run.

### 3. Declare the ActivitySource in a static class

```csharp
// src/Flowline/Diagnostics/FlowlineActivitySource.cs
public static class FlowlineActivitySource
{
    static readonly string s_version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? "0.0.0";

    public static readonly ActivitySource Source = new("Flowline.CLI", s_version);
}
```

One source, one name. All commands share the same source, so the single listener registration in
Program.cs covers them all.

### 4. Start the activity BEFORE the first log call in ExecuteAsync

The `Command: <name> <args>` log line is the anchor entry — it's what you grep for in CI. If the
activity is started after this line, that line carries no `TraceId`, breaking the correlation for
the most visible entry.

```csharp
public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
{
    var sw = Stopwatch.StartNew();

    // Activity FIRST — the next log line must already carry the TraceId
    using var activity = FlowlineActivitySource.Source.StartActivity(context.Name);

    Logger.LogInformation("Command: {Command} {Args}", context.Name, FormatArgs(settings));
    // ... rest of command
}
```

### 5. Enrich every log event with the current TraceId via Serilog enricher

```csharp
// src/Flowline/Logging/ActivityTraceEnricher.cs
public sealed class ActivityTraceEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (traceId is not null)
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("TraceId", traceId));
    }
}
```

Wire it into the Serilog pipeline in Program.cs:

```csharp
.Enrich.With(new ActivityTraceEnricher())
```

`AddOrUpdateProperty` overwrites any existing `TraceId` property with the ambient Activity's value.
This is intentional: the enricher is the authoritative source of the correlation ID — it should not
be suppressable by a log caller accidentally stamping a different value.

### 6. Extract InvocationLogger for testability

Logging 16 activity tags and the invocation header from inside `FlowlineCommand.ExecuteAsync`
couples the behavior to the command dependency graph. Extract it to a static internal class:

```csharp
// src/Flowline/Commands/InvocationLogger.cs
internal static class InvocationLogger
{
    internal static string HashUrl(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();

    internal static void Log(
        ILogger logger,
        FlowlineRuntimeOptions runtimeOptions,
        ProjectConfig? config,
        string rootFolder,
        Activity? activity)
    {
        var tv = runtimeOptions.ToolVersions;
        if (tv is null) return;

        // ... build envTiers, envHashes, solutionNames ...

        logger.LogInformation(
            "Invocation: {FlowlineVersion} dotnet={DotNetVersion} pac={PacVersion}({PacInstallType}) " +
            "git={GitVersion}@{GitBranch} os={Os} arch={OsArch} ci={Ci} ci.platform={CiPlatform} ...",
            tv.FlowlineVersion, tv.DotNetVersion, tv.PacVersion, tv.PacInstallType,
            tv.GitVersion, tv.GitBranch, ...);

        if (activity is null) return;
        activity.SetTag("flowline.version", tv.FlowlineVersion);
        activity.SetTag("git.branch", tv.GitBranch);
        // ... 14 more tags ...
    }
}
```

The static class has no constructor dependencies, so tests can call `InvocationLogger.Log(...)` or
`InvocationLogger.HashUrl(...)` directly with a mock `ILogger` and a manually started `Activity` —
no command object graph needed. The `[assembly: InternalsVisibleTo("Flowline.Tests")]` attribute
(already present in `ProfileResolutionService.cs`) grants the test project access.

Call site in `FlowlineCommand.ExecuteAsync`:

```csharp
InvocationLogger.Log(Logger, RuntimeOptions, Config, RootFolder, activity);
```

### 7. Hash environment URLs, do not log them verbatim

Environment URLs contain tenant identifiers (e.g. `https://contoso.crm.dynamics.com`). Logging them
verbatim leaks PII into CI logs and log aggregators. Hash them instead:

```csharp
internal static string HashUrl(string url) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();
```

SHA-256 truncated to 8 hex chars (`a3ce929d`) is stable and collision-resistant enough for log
correlation within a single project. Two runs against the same environment always produce the same
hash, so a developer can track "all commands run against env `a3ce929d`" over time without storing
the URL.

### 8. Detect CI platform as a discriminated string, not a boolean

```csharp
internal static string? DetectCIPlatform()
{
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null) return "github";
    if (Environment.GetEnvironmentVariable("TF_BUILD") != null) return "azuredevops";
    if (Environment.GetEnvironmentVariable("JENKINS_URL") != null) return "jenkins";
    if (Environment.GetEnvironmentVariable("CI") != null) return "unknown";
    return null;
}
```

Returns `null` (not-CI) or a platform string. `"unknown"` covers generic CI systems that set only
the `CI` environment variable. A `bool` would collapse all CI environments into one, making it
impossible to write CI-specific log filters or alert rules in future waves. The `null` sentinel
makes the `ci={true|false}` log property derivable without a separate IsCI flag:

```csharp
var ciPlatform = ConsoleHelper.DetectCIPlatform();
var ci = ciPlatform is not null;
```

### 9. Fetch git branch fresh — do not cache in a long-TTL store

Git branch changes frequently (every checkout). Caching it in a 7-day `ToolCheckResult` TTL means
the invocation header reflects the branch from the last tool-version probe, not the current run.

```csharp
// Inside CheckSetupAsync, inside the Spectre.Console spinner:
// Fetched fresh (not cached) — branch changes too frequently for 7-day TTL
gitBranch = await GitUtils.GetCurrentBranchAsync(settings.Verbose, cancellationToken);
```

`ToolCheckResult` is appropriate for stable facts (dotnet version, pac version) that change only
when the tool is upgraded. It is not appropriate for operational state (current branch, working dir)
that changes per-commit.

A prior version also stored `Branch` in `ToolCheckResult` and fetched it inside the `EnsureGitAsync`
factory — creating a double git fork-exec on every cache miss, since `CheckSetupAsync` already
fetched branch fresh and never read `git.Branch`. Remove volatile fields from long-TTL caches.

---

## Why This Matters

**Correlation without a collector.** W3C `TraceId` is a de-facto standard. When Wave 3 adds an
OpenTelemetry exporter, the same `TraceId` values already in the structured logs will join up
automatically with spans in Jaeger, Tempo, or Application Insights — no log re-ingestion or schema
migration needed.

**Zero added dependencies.** `System.Diagnostics.Activity`, `ActivitySource`, and `ActivityListener`
are in the .NET runtime. `SHA256.HashData` is in `System.Security.Cryptography`. Wave 2 adds
observability structure at zero package cost.

**Testability by design.** `ActivityTraceEnricher` and `InvocationLogger` are small, focused classes
with no framework coupling. Tests exercise the exact production code path — not a simulation — by
creating a real `Activity` in the test and asserting on the enriched log events or activity tags.

**Dead code removal pays observability dividends.** The `ToolCheckResult.Branch` field existed as a
dead write for months: `EnsureGitAsync` fetched and cached the branch, but `CheckSetupAsync` never
read `git!.Branch` — it fetched it again inside the spinner. Removing the field eliminated the
double git fork-exec and made the git probe in the spinner the single source of truth for branch,
correctly placed where the Spectre.Console progress display can show it.

---

## When to Apply

- Any .NET CLI command producing multiple log lines per invocation that need to be correlatable in a
  log file or aggregator.
- When you want to adopt OpenTelemetry export later without changing the Activity API surface —
  start the `ActivitySource`/`ActivityListener` pattern now; add the exporter later.
- When logging environment-specific identifiers (URLs, connection strings, tenant IDs) that must not
  appear verbatim in CI logs.
- When a command's observability logic grows beyond 2–3 lines and begins coupling to the command's
  constructor dependencies — extract to a static helper for testability.
- When CI platform matters for downstream log filtering or alerting — return a discriminated string,
  not a bool.

Do **not** touch `Activity.DefaultIdFormat`, `ForceDefaultIdFormat`, or
`ActivitySource.AddActivityListener` in library projects. Those are host-process concerns. Library
code should start `Activity` spans via its own `ActivitySource` but leave format and listener
registration to the host.

---

## Examples

### Before: no correlation, verbatim URL, bool CI flag

```csharp
// ExecuteAsync — log lines carry no shared identifier
Logger.LogInformation("Command: {Command}", context.Name);
// ... 200ms of work ...
Logger.LogInformation("Pushed solution. Env: {Url}", runtimeOptions.EnvironmentUrl);
Logger.LogInformation("Done in {Ms}ms", sw.ElapsedMilliseconds);
```

Log output (two concurrent runs interleaved):

```
[14:01:00] Command: push
[14:01:00] Command: pull
[14:01:02] Pushed solution. Env: https://contoso.crm.dynamics.com   ← which run?
[14:01:02] Pulled solution. Env: https://contoso.crm.dynamics.com
[14:01:02] Done in 1847ms
[14:01:03] Done in 2103ms
```

### After: W3C TraceId on every line, hashed URL

```csharp
using var activity = FlowlineActivitySource.Source.StartActivity(context.Name);
Logger.LogInformation("Command: {Command}", context.Name);
// ...
InvocationLogger.Log(Logger, RuntimeOptions, Config, RootFolder, activity);
Logger.LogInformation("Done in {Ms}ms", sw.ElapsedMilliseconds);
```

Log output (same two concurrent runs):

```
[14:01:00] TraceId=4bf92f3577b34da6a3ce929d0e0e4736 Command: push
[14:01:00] TraceId=8e9ac0571c2741b5b9f3d8e1a4c62f90 Command: pull
[14:01:02] TraceId=4bf92f3577b34da6a3ce929d0e0e4736 Invocation: 1.0.0 dotnet=8.0.100 ... branch=feat/wave2 env.hashes=prod=a3ce929d ci=False ...
[14:01:02] TraceId=8e9ac0571c2741b5b9f3d8e1a4c62f90 Invocation: 1.0.0 dotnet=8.0.100 ... branch=main env.hashes=prod=a3ce929d ci=True ci.platform=github ...
[14:01:02] TraceId=4bf92f3577b34da6a3ce929d0e0e4736 Done in 1847ms
[14:01:03] TraceId=8e9ac0571c2741b5b9f3d8e1a4c62f90 Done in 2103ms
```

Filter `TraceId=4bf92f3577b34da6a3ce929d0e0e4736` → exactly the push run, nothing else.

### Test setup: ActivityListener in test static constructor

Registering in a static constructor (shared across all tests in the class) ensures
`Activity.Current` is non-null during assertions, and the registration only runs once per test
assembly:

```csharp
static InvocationLoggerTests()
{
    Activity.DefaultIdFormat = ActivityIdFormat.W3C;
    Activity.ForceDefaultIdFormat = true;
    var listener = new ActivityListener
    {
        ShouldListenTo = s => s.Name == "Flowline.CLI",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
    };
    ActivitySource.AddActivityListener(listener);
}
```

### Test: ActivityTraceEnricher stamps TraceId only when an Activity is active

```csharp
[Fact]
public void Enrich_WhenActivityIsActive_AddsTraceId()
{
    using var activity = FlowlineActivitySource.Source.StartActivity("test-op");
    activity.Should().NotBeNull();

    var enricher = new ActivityTraceEnricher();
    // ... create LogEvent, call enricher.Enrich, assert "TraceId" key present
    logEvent.Properties.Should().ContainKey("TraceId");
    logEvent.Properties["TraceId"].ToString().Trim('"').Should().Be(activity!.TraceId.ToString());
}

[Fact]
public void Enrich_WhenNoActivity_DoesNotAddTraceId()
{
    Activity.Current.Should().BeNull(); // guard: no ambient activity in this test
    var enricher = new ActivityTraceEnricher();
    // ... create LogEvent, call enricher.Enrich
    logEvent.Properties.Should().NotContainKey("TraceId");
}
```

### Test: InvocationLogger.HashUrl

```csharp
[Theory]
[InlineData("https://contoso.crm4.dynamics.com/")]
[InlineData("https://example.com")]
public void HashUrl_ReturnsDeterministicEightCharLowercaseHex(string url)
{
    var hash = InvocationLogger.HashUrl(url);
    hash.Should().HaveLength(8);
    hash.Should().MatchRegex("^[0-9a-f]+$");
    InvocationLogger.HashUrl(url).Should().Be(hash); // deterministic
}

[Fact]
public void HashUrl_DifferentUrls_ProduceDifferentHashes()
{
    InvocationLogger.HashUrl("https://contoso.crm.dynamics.com")
        .Should().NotBe(InvocationLogger.HashUrl("https://fabrikam.crm.dynamics.com"));
}
```

### Test: CI platform detection with environment isolation

```csharp
[Theory]
[InlineData("GITHUB_ACTIONS", "1", "github")]
[InlineData("TF_BUILD", "True", "azuredevops")]
[InlineData("JENKINS_URL", "http://jenkins:8080", "jenkins")]
[InlineData("CI", "true", "unknown")]
public void DetectCIPlatform_ReturnsExpectedString(string envVar, string value, string expected)
{
    using var scope = SaveAndClearCiVars(); // clears all CI env vars, restores on Dispose
    Environment.SetEnvironmentVariable(envVar, value);
    ConsoleHelper.DetectCIPlatform().Should().Be(expected);
}

[Fact]
public void DetectCIPlatform_WhenNoCiVarsSet_ReturnsNull()
{
    using var scope = SaveAndClearCiVars();
    ConsoleHelper.DetectCIPlatform().Should().BeNull();
}
```

---

## Related

- [`spectre-console-ilogger-render-hook.md`](spectre-console-ilogger-render-hook.md) — the
  complementary logging infrastructure pattern: bridging Spectre.Console terminal output to ILogger.
  Both patterns are wired in `Program.cs`; they coexist without interference. The ActivityTraceEnricher
  runs in the Serilog pipeline alongside the file sink that the render hook writes to.
