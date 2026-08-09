---
title: "A Debug build prints stack traces instead of the CLI's real error output"
date: 2026-08-09
category: docs/solutions/developer-experience/
module: flowline-cli
problem_type: developer_experience
component: development_workflow
severity: medium
applies_when: "Manually running the CLI to check user-facing messages, error wording, or exit codes"
related_components:
  - Program
  - FlowlineException
tags:
  - build-configuration
  - error-handling
  - manual-testing
  - spectre-console
---

# A Debug build prints stack traces instead of the CLI's real error output

## Context

While testing a new pre-import gate end to end, a `FlowlineException` surfaced like this:

```
Unhandled exception. Flowline.Core.FlowlineException: No solution.xml entry found in artifact ...
   at Flowline.Commands.DeployCommand.ReadArtifactSolutionManifest(String zipPath)
   at Flowline.Commands.DeployCommand.ExecuteFlowlineAsync(...)
   ...
```

That reads unmistakably like broken error handling — a typed, carefully worded exception escaping to
the runtime with a stack trace and a meaningless exit code (`0xE0434352`). The obvious conclusions
are that the exception handler is misconfigured or that a recent change broke it.

Neither is true. It was a Debug build behaving exactly as configured.

## Guidance

**Run the CLI from a Release build whenever you are checking user-facing output.**

`Program.cs` calls `config.PropagateExceptions()` inside an `#if DEBUG` block, while
`config.SetExceptionHandler(...)` — the handler that renders `Error: <message>` and returns the
typed `ExitCode` — sits outside it. Propagation wins, so under Debug every `FlowlineException`
escapes as an unhandled .NET exception. The handler is never reached.

```bash
dotnet build src/Flowline/Flowline.csproj -c Release
dotnet src/Flowline/bin/Release/net10.0/Flowline.dll deploy test --dry-run
```

The same commit, built Release, produces what a user actually sees:

```
Error: No solution.xml entry found in artifact '...' — is this a valid packed solution zip?
Log: C:\Users\...\AppData\Local\Flowline\logs\2026-08-09T031856Z-deploy.log
```

Reach for Debug when you *want* the stack trace to find a throw site. That is the whole point of the
`#if DEBUG` block — it is a feature for debugging, not a defect.

## Why This Matters

The failure mode is a false alarm that looks like a serious regression, and it costs time in the
worst place: mid-way through verifying something else. In the session that produced this note, the
stack trace was initially read as evidence that the new gate's error path was broken, which is
precisely backwards — the gate's messages were correct and only the build configuration was hiding
them.

It also cuts the other way. Verifying error wording, exit codes, or tone-of-voice compliance from a
Debug build proves nothing about what ships, because the rendering layer under test never runs.

## When to Apply

- Manually exercising any command to check messages, error wording, or exit codes.
- Verifying that a new `FlowlineException` renders per `docs/tone-of-voice.md`.
- Checking that a command returns the exit code its contract promises — Debug returns the CLR's
  unhandled-exception code regardless of the `ExitCode` the throw specified.

Not applicable to the automated test suite, which asserts against thrown exceptions and exit-code
values directly rather than through the console rendering layer.

## Examples

The configuration that produces the asymmetry, in `src/Flowline/Program.cs`:

```csharp
#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
    config.SetExceptionHandler((ex, _) =>
    {
        switch (ex)
        {
            case FlowlineException fe:
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(fe.Message)}");
                return (int)fe.ExitCode;
            // ...
        }
    });
```

`PropagateExceptions()` tells Spectre.Console.Cli to let exceptions escape `RunAsync` rather than
routing them to the registered handler. Under Debug the handler below it is unreachable for any
exception the command throws.

## Related

- `docs/solutions/logic-errors/packed-vs-unpacked-solution-zip-layout.md` — the bug being tested
  when this trap surfaced
