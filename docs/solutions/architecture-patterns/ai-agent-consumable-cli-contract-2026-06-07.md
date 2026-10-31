---
title: "AI-agent-consumable CLI contract: typed exit codes, actionable errors, AGENTS.md"
date: 2026-06-07
category: docs/solutions/architecture-patterns/
module: flowline-cli
problem_type: architecture_pattern
component: tooling
severity: high
applies_when:
  - adding a new CLI command or new exit scenario
  - designing command error handling or output messages
  - integrating Flowline with AI agents or CI/CD pipelines
tags:
  - exit-codes
  - cli-api
  - agent-friendly
  - error-handling
  - agents-md
  - flowline-exception
  - help-text
related_components:
  - development_workflow
  - documentation
---

# AI-agent-consumable CLI contract: typed exit codes, actionable errors, AGENTS.md

## Context

Flowline always returned exit code `1` for every failure. AI agents (Claude Code, GitHub Copilot
Workspace) had no programmatic way to distinguish authentication failure from a dirty working
directory from a build error — so they could not implement corrective logic and had to fall back
to parsing unstructured stderr text.

Additionally, solution repos had no machine-readable contract documenting Flowline's commands and
their preconditions, and command help text omitted the trigger/state-change context agents use for
command selection.

## Guidance

Three-part contract that makes Flowline agent-consumable without free-form output parsing.

### 1. Typed `ExitCode` enum — a stable public API

```csharp
/// Treat as a stable public API — agents and scripts pattern-match on these values.
public enum ExitCode
{
    Success = 0,
    GeneralError = 1,
    // 2 intentionally unused — Spectre handles argument validation internally
    NotFound = 3,           // de facto convention (curl, git)
    NotAuthenticated = 4,   // de facto convention
    // 5 intentionally unused — no forbidden/permissions concept
    ConnectionFailed = 10,
    ConfigInvalid = 11,
    DirtyWorkingDirectory = 12,
    BuildFailed = 13,
    VersionConflict = 14,
    ValidationFailed = 15,
    Timeout = 16,
    ForceRequired = 17,
    PartialSuccess = 18,        // deploy succeeded; orphan cleanup partially failed — manual action needed
    Inconclusive = 19,          // a check couldn't run to completion — not a pass/fail signal
    WriteTargetOccupied = 20,   // a write target is already occupied by something valid
    AssemblyNotRegistered = 21, // deploy succeeded; a plug-in package assembly has no registration
    ChangesFound = 22,          // diff/drift found changes; not a failure, only with --exit-code
    Cancelled = 130,            // de facto convention: 128 + SIGINT(2)
}
```

Conventional slots (3, 4, 130) follow curl/git/rsync to avoid collisions. Flowline-specific codes
use the 10–22 range (added to as new failure classes earned their own number — see `ExitCode.cs`
for the current, authoritative list). Codes 2 and 5 are intentionally unused — document that fact
with comments so future maintainers don't fill them with wrong semantics.

**Renumbering is a breaking change.** Treat the enum as a public API surface.

### 2. `FlowlineException` carries the typed code

```csharp
public class FlowlineException : Exception
{
    public ExitCode ExitCode { get; init; } = ExitCode.GeneralError;

    // New overload — preferred for all new throw sites
    public FlowlineException(ExitCode exitCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
    }
}
```

`Program.cs` returns the typed code from the global exception handler:

```csharp
case FlowlineException fe:
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(fe.Message)}");
    WriteExceptionContext(fe, serilogLogger);
    return (int)fe.ExitCode;
```

For direct early returns (where the error is already printed above), use `return (int)ExitCode.X`
rather than `throw` — keeps the message/return co-located and avoids exception overhead for
expected conditions.

### 3. Actionable error messages for the four most important codes

Codes 4, 12, 14, and 17 have clear corrective actions. Embed the fix verbatim in the message so
agents can extract it without a lookup table:

| Code | Required message content |
|------|--------------------------|
| 4 (NotAuthenticated) | `run: pac auth create --environment <url>` |
| 12 (DirtyWorkingDirectory) | `Commit or stash changes first` |
| 14 (VersionConflict) | Reserved, currently never thrown (`ExitCode.cs`). `Add --force <specifier> to overwrite` — `--force` requires a value naming the exact hazard (e.g. `--force dirty`); a bare or unrecognized value fails listing the valid ones for that command. See `docs/plans/2026-07-11-001-refactor-force-specifier-plan.md`. |
| 17 (ForceRequired) | `Use --force <specifier> to proceed` — same per-command specifier requirement as above |

All other error messages must at minimum state what failed and what to check. No message should
say only "failed" or "error" — agents receiving the message must know which resource failed and
what action to take.

Use `Console.Error(message)` (the FlowlineConsoleExtensions helper), never raw
`Console.MarkupLine("[red]...[/]")` for error output.

### 4. `AGENTS.md` scaffolded by `flowline clone`

`ProjectScaffolder` (invoked by `clone`) writes `AGENTS.md` at the repo root after scaffolding the solution structure.
Template is a C# raw string literal with `solutionName` interpolated — not an embedded resource
(single use, needs substitution, no infrastructure justified):

```csharp
internal async Task ScaffoldAgentsFileAsync(string slnFolder, string solutionName, string slnFileName, CancellationToken ct)
{
    var agentsFilePath = Path.Combine(RootFolder, "AGENTS.md");
    if (File.Exists(agentsFilePath))
    {
        console.Skip("AGENTS.md already exists — skipping.");
        return;
    }
    var content = $"""
        # Flowline — Agent Instructions
        ...daily loop, project structure with {solutionName} substituted, exit code table...
        """;
    await File.WriteAllTextAsync(agentsFilePath, content, ct);
    console.Ok("AGENTS.md created.");
}
```

Skip-if-exists is non-negotiable: the developer may have customised the file. Do not overwrite.

### 5. Help text: what + trigger + state-change

Every `.WithDescription()` must answer three questions agents use for command selection:

```
"<What it does> — <details>. <When to run it>. <Preconditions/invariants>."
```

Examples:
```csharp
// push
"Build and register plugin assembly and web resources directly to DEV — skips pack/import.
 Reads [Step] attributes to create or update plugin registrations.
 Run after plugin or web resource changes."

// pull
"Export the solution from DEV, bump the patch version, unpack it to source-controlled XML, and write CHANGES.md and the context docs.
 Run after testing in DEV, with a clean working tree."

// deploy
"Pack solution from repo and import into target environment (test, uat, prod, or URL).
 Requires clean git working directory."
```

One-line descriptions ("Deploy the solution", "Push plugins") fail — agents pick commands based
on this text and need trigger context and preconditions to choose correctly.

## Why This Matters

**Exit code 1 for everything:** agents cannot implement retry logic, precondition detection, or
corrective flows. An agent seeing exit 1 does not know if it should retry (transient), fix
environment (precondition), or abort (user error).

**Vague error messages:** agents that receive "Error: failed" must maintain an out-of-band lookup
table mapping error text to corrective actions — this breaks the moment messages change.

**No AGENTS.md:** agents lack the repository's operational model. They don't know that `push` is
idempotent, that `pull` requires a clean working directory, or the correct command order. So they
default to defensive inaction or guesswork.

With this contract, agents can classify failures by exit code, extract corrective actions directly
from error messages, and understand the full workflow without external documentation.

## When to Apply

- Every new `FlowlineException` throw site — always pass the typed `ExitCode` overload.
- Every `return 1` in commands — convert to `return (int)ExitCode.X` with the correct code.
- Every new CLI command — write a three-part help text description before shipping.
- Exit code enum additions — treat as a breaking change; document in release notes.

Do not add new exit codes in the 0–9 range (reserved by convention). Extend above the highest
allocated code (see `ExitCode.cs`) for new Flowline-specific conditions.

## Examples

**Before — every failure returns 1:**
```csharp
if (!await GitUtils.IsRepoCleanAsync(verbose, ct))
{
    AnsiConsole.MarkupLine("[red]Git has uncommitted changes.[/]");
    return 1;  // agent: code 1 — transient? precondition? abort?
}
```

**After — typed code, actionable message, Console.Error helper:**
```csharp
// In PullCommand:
throw new FlowlineException(
    ExitCode.DirtyWorkingDirectory,
    "Uncommitted changes in 'Solution/src' — Commit or stash changes first, or re-run with --force dirty.");
```

**Before — vague error message:**
```csharp
throw new FlowlineException("No PAC profile found — run 'pac auth create' first.");
```

**After — corrective command in the message:**
```csharp
throw new FlowlineException(
    ExitCode.NotAuthenticated,
    "Not authenticated — run: pac auth create --environment <url>");
```

**Before — one-line help text:**
```csharp
.WithDescription("Pull the solution")
```

**After — what + trigger + state-change:**
```csharp
.WithDescription("Export the solution from DEV, bump the patch version, unpack it to source-controlled XML, and write CHANGES.md and the context docs. Run after testing in DEV, with a clean working tree.")
```

## Related

- [`src/Flowline.Core/ExitCode.cs`](../../../src/Flowline.Core/ExitCode.cs) — the enum
- [`src/Flowline.Core/FlowlineException.cs`](../../../src/Flowline.Core/FlowlineException.cs) — typed constructor
- [`src/Flowline/Program.cs`](../../../src/Flowline/Program.cs) — global exception handler + `.WithDescription()` strings
- [`src/Flowline/Services/ProjectScaffolder.cs`](../../../src/Flowline/Services/ProjectScaffolder.cs) — `ScaffoldAgentsFileAsync`
- [`src/Flowline.Core/Console/FlowlineConsoleExtensions.cs`](../../../src/Flowline.Core/Console/FlowlineConsoleExtensions.cs) — `Console.Error()` helper
- [`docs/solutions/conventions/flowline-add-environment-2026-06-06.md`](../conventions/flowline-add-environment-2026-06-06.md) — environment resolution per command (related FlowlineException context)
- [`docs/solutions/tooling-decisions/webresources-project-scaffolding-2026-06-04.md`](../tooling-decisions/webresources-project-scaffolding-2026-06-04.md) — clone scaffolding patterns
- [`docs/solutions/logic-errors/sync-overwrites-uncommitted-src-without-warning-2026-05-15.md`](../logic-errors/sync-overwrites-uncommitted-src-without-warning-2026-05-15.md) — DirtyWorkingDirectory guard origin
