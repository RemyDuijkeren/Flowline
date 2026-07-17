---
title: Spectre.Console Status spinner throws when the work inside it prompts interactively
date: 2026-07-17
category: runtime-errors
module: Flowline CLI / Dataverse connectivity
problem_type: runtime_error
component: service_object
symptoms:
  - "System.InvalidOperationException: Trying to run one or more interactive functions concurrently. Operations with dynamic displays (e.g. a prompt and a progress display) cannot be running at the same time."
  - "PAC profile disambiguation prompt never appears; the command aborts instead"
  - "Only reproduces when the target environment URL matches more than one local PAC auth profile — a single-match or no-match resolution never hits the prompting branch"
root_cause: logic_error
resolution_type: code_fix
severity: high
tags:
  - spectre-console
  - interactive-prompt
  - status-spinner
  - concurrency
  - pac-profile-resolution
related_components:
  - authentication
---

# Spectre.Console Status spinner throws when the work inside it prompts interactively

## Problem

`FlowlineCommand.GetAndCheckEnvironmentInfoAsync` and `ConnectToDataverseAsync` (`src/Flowline/Commands/FlowlineCommand.cs`) resolved a `PacProfile` via `ProfileResolutionService.ResolveAsync(url, cancellationToken)` *inside* the lambda passed to `Console.Status().FlowlineSpinner().StartAsync(...)`.

`ProfileResolutionService.ResolveAsync` is not always silent: on an ambiguous match — when the target URL matches more than one local PAC auth profile — it calls `HandleAmbiguousAsync`, which drives an interactive `console.Prompt(new SelectionPrompt<PacProfile>())` to ask the user which profile to use.

Spectre.Console does not allow an interactive prompt to run while a `Status`/`Progress`/`Live` display is already active — both are "dynamic displays" that own the console's render loop, and the library enforces mutual exclusion between them. Doing the ambiguous-profile prompt from inside the spinner's `StartAsync` lambda re-enters the console while the spinner's own display session is still open, which Spectre.Console detects and rejects.

Every command routed through the shared `FlowlineCommand` helper was affected: `clone`, `sync`, `push` (non-standalone), `generate` (non-standalone), `drift` (role-keyword branch), and `provision` (source-Prod check). Any of these, run against a URL with more than one matching PAC profile, crashed instead of asking the user to pick one.

## Symptoms

- Command aborts with an unhandled exception instead of showing the expected "select a profile" picker:
  ```
  System.InvalidOperationException: Trying to run one or more interactive functions concurrently. Operations with dynamic displays (e.g. a prompt and a progress display) cannot be running at the same time.
  ```
- Only reproducible when the target environment URL matches multiple local PAC auth profiles — a single-match or no-match resolution never enters the prompting branch, so the bug is silent in the common case.
- Affects any command path that reaches `FlowlineCommand.GetAndCheckEnvironmentInfoAsync` or `ConnectToDataverseAsync`: clone, sync, push (non-standalone), generate (non-standalone), drift (role-keyword branch), provision (source-Prod check).

## What Didn't Work

The first attempt to reproduce and confirm the bug used `Spectre.Console.Testing`'s `TestConsole` — the console test double already standard throughout this codebase's test suite (see the `TestCommand` helper in `FlowlineCommandTests.cs`):

```csharp
var console = new TestConsole();
console.Interactive();
console.Input.PushTextWithEnter("x");
await console.Status().StartAsync("Checking...", async _ =>
{
    var result = console.Prompt(new TextPrompt<string>("Pick:"));
});
// -> no exception thrown
```

This did **not** throw. `TestConsole` does not enforce the same live-display exclusivity lock that `AnsiConsole.Console` (the real console, and the instance `Program.cs` registers via `services.AddSingleton<IAnsiConsole>(AnsiConsole.Console)` in production) does. A unit test written against `TestConsole` — the only console double this codebase's tests use — cannot catch this bug class, regardless of how the test is structured. This was a false negative, not evidence the bug was wrong.

Switching the probe to the real `AnsiConsole.Console` reproduced the exception immediately:

```csharp
var console = AnsiConsole.Console;
await console.Status().StartAsync("Checking...", async _ =>
{
    var result = console.Prompt(new TextPrompt<string>("Pick:"));
});
// -> System.InvalidOperationException: Trying to run one or more interactive functions concurrently...
```

This confirmed the bug empirically, against the real console implementation, before touching production code.

## Solution

Resolve the profile *before* opening the spinner, so any prompt happens on the plain console with no active display, then open the spinner around only the non-interactive work. `src/Flowline/Commands/FlowlineCommand.cs`:

```csharp
// Resolved before opening the status spinner — ProfileResolutionService.ResolveAsync can prompt
// interactively on an ambiguous match, and Spectre.Console throws if a prompt runs while a
// Status/Live display is active ("Trying to run one or more interactive functions concurrently").
var profile = resolvedProfile ?? await ProfileResolutionService.ResolveAsync(url, cancellationToken);
EnvironmentInfo? env = await Console.Status().FlowlineSpinner().StartAsync(
    $"Checking {label.ToLower()} [bold]{url}[/]...",
    ctx => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(url, profile, settings, cancellationToken));
```

The same fix applies to `ConnectToDataverseAsync`:

```csharp
// Resolved before opening the status spinner — see the comment in GetAndCheckEnvironmentInfoAsync.
var profile = resolvedProfile ?? await ProfileResolutionService.ResolveAsync(environmentUrl, cancellationToken);
```

This matches the pattern already used correctly elsewhere in the same codebase — `DeployCommand.ValidateTargetAsync` resolves the profile before opening its own status spinner, and `DriftCommand`'s raw-URL branch and `PushCommand`/`GenerateCommand`'s standalone paths do the same.

## Why This Works

`ProfileResolutionService.ResolveAsync` now runs against the console with no `Status`/`Progress`/`Live` display open, so `HandleAmbiguousAsync`'s `SelectionPrompt` can render without contending for the console's dynamic-display lock. The spinner opens only after a concrete `PacProfile` is already in hand, wrapping purely non-interactive work (`GetEnvironmentInfoByUrlAsync`, `ConnectViaPacAsync`) — exactly the kind of work `Status` is meant for. Sequencing, not any Spectre.Console API change, is the fix: interactive resolution and dynamic-display rendering are mutually exclusive operations, so they must not be nested.

## Prevention

- **General rule:** never call anything that can drive an interactive `console.Prompt(...)` (directly, or transitively through a service like `ProfileResolutionService.ResolveAsync`) from inside a `Console.Status()`, `Console.Progress()`, or `Console.Live()` lambda. Resolve/prompt for everything interactive first; open the dynamic display afterward, scoped to only the non-interactive work inside it. When adding a new command or helper that both resolves something ambiguous and shows a spinner/progress display, check this ordering explicitly — it is not caught by the compiler or by `TestConsole`-based tests.
- **Review technique that caught this:** look for the inconsistent sibling pattern. Two independent code-review passes (an adversarial failure-scenario review and a correctness execution-trace review) both spotted this by noticing that every other command in the codebase resolved the profile *before* the spinner, and only the shared `FlowlineCommand` helper resolved it *inside*. When one code path in a codebase does something structurally different from every sibling that does the same job, that asymmetry is worth checking even if nothing is failing yet — neither reviewer ran the code; both found it by reading and comparing.
- **Known testing gap, named explicitly:** `Spectre.Console.Testing.TestConsole` — the double used throughout this codebase's tests — does not enforce the live-display mutual-exclusion lock that the real `AnsiConsole.Console` does. A test written against `TestConsole` to verify "prompting inside a Status display throws" will pass with no exception thrown, which reads as confirmation the code is fine when it is actually a blind spot in the test double. No regression test was added for this fix: the alternative (using the real static `AnsiConsole.Console` in a committed test) would introduce shared mutable global console state into a test suite that otherwise consistently uses `TestConsole`, which is worse than the gap it closes. This class of Spectre.Console concurrency bug is currently only verifiable by manual/live reproduction against the real console, not by this codebase's unit test suite — treat that as a standing limitation, not an oversight, and revisit if `Spectre.Console.Testing` ever adds live-display exclusivity support.

### Secondary fixes from the same session (brief)

1. **Stale faulted-task memoization** — `DataverseConnector.GetOrCreateMsalCacheHelperAsync` used `s_cacheHelperTask ??= CreateMsalCacheHelperAsync()`. If creation ever threw (e.g. a transient file lock), the faulted `Task` was cached permanently, so every later call in the process re-threw the same stale failure with no retry. Fixed to only reuse a task that hasn't faulted or been canceled (`if (existing is { IsFaulted: false, IsCanceled: false }) return existing;`) before creating and caching a fresh one.
2. **Duplicate model type** — `BapEnvironmentInfo` was an exact duplicate of the pre-existing `EnvironmentInfo` type, hand-mapped between the two. Resolved by moving `EnvironmentInfo` into the shared `Flowline.Core.Models` namespace and deleting the duplicate, since the project dependency direction only ran one way (`Flowline.csproj` already references `Flowline.Core.csproj`).

## Related Issues

- `docs/plans/2026-07-17-002-refactor-environment-check-drop-active-profile-plan.md` — the plan whose implementation surfaced this bug during code review
- `docs/solutions/architecture-patterns/spectre-console-ilogger-render-hook.md` — another Spectre.Console integration pattern in this codebase (output routing, not concurrency); its embedded code sample is flagged elsewhere as stale and due a refresh
- `docs/solutions/architecture-patterns/verbose-output-render-hook-routing.md` — mentions `ProfileResolutionService` in passing; confirmed unaffected by this fix (the reorder only changes *when* `ResolveAsync` runs relative to the spinner, not its verbose-gating behavior, which this service no longer carries)
