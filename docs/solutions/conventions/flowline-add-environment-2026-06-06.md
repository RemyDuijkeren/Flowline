---
title: "Adding a new named environment to Flowline CLI"
date: 2026-06-06
category: docs/solutions/conventions
module: environment-management
problem_type: convention
component: tooling
severity: low
applies_when:
  - "Adding a new named environment beyond Dev, Test, UAT, Prod (e.g., Staging, Demo, Training)"
  - "Deciding which Dataverse environment type a new environment should require"
tags:
  - environment
  - flowline-cli
  - project-config
  - provision
  - deploy
  - clone
  - status
---

# Adding a new named environment to Flowline CLI

## Context

Flowline CLI manages named environments (Dev, Test, UAT, Prod) that map to Dataverse URLs. Adding UAT as a fourth environment between Test and Production revealed a clear pattern: six distinct files each need a small, consistent change. Missing any one of them leaves that command silently broken for the new environment (e.g., `deploy uat` errors out, `status` doesn't show it, `clone` skips it).

This document captures the complete checklist and the key decisions that must be made before each new environment is added.

## Guidance

### Step 1: Make four key decisions upfront

Before touching any code, answer these for the new environment:

| Decision | Options | Example (UAT) |
|---|---|---|
| Dataverse type | `Production` or `Sandbox` | `Sandbox` |
| Deploy confirmation | Silent (like Test) or prompted (like Prod) | Silent |
| Provision supported? | Yes (`FullCopy` or `MinimalCopy`) or No | Yes — `FullCopy` |
| Clone search order position | Where in Prod → ... → Dev? | After Prod, before Test |

The Dataverse type drives the validation guard in `GetAndCheckEnvironmentInfoAsync` — **Production** environments require `env.Type == "Production"`; everything else rejects that type. A new Sandbox environment needs no type-guard changes at all.

### Step 2: Six files to update

#### 1. `src/Flowline/Config/ProjectConfig.cs` — URL storage

Add a property and its `GetOrUpdateXxxUrl()` method. The method has a fixed three-block structure matching all existing env methods:

```csharp
public string? UatUrl { get; set; }

public string? GetOrUpdateUatUrl(string? inputUatUrl, FlowlineSettings? settings = null)
{
    inputUatUrl = inputUatUrl?.Trim();

    if (string.IsNullOrWhiteSpace(UatUrl))
    {
        UatUrl = inputUatUrl;
        return string.IsNullOrWhiteSpace(inputUatUrl) ? null : inputUatUrl;
    }

    if (string.IsNullOrWhiteSpace(inputUatUrl))
    {
        if (settings is { Verbose: true })
            AnsiConsole.MarkupLine($"[dim]UAT: [bold]{UatUrl}[/][/]");
        return UatUrl;
    }

    if (UatUrl != inputUatUrl)
    {
        AnsiConsole.MarkupLine($"[yellow]UAT is already set: [bold]{UatUrl}[/][/]");
        if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings))
        {
            AnsiConsole.MarkupLine($"[dim]Keeping UAT as-is: [link]{UatUrl}[/][/]");
            return UatUrl;
        }
        AnsiConsole.MarkupLine("[green]UAT updated[/]");
    }

    UatUrl = inputUatUrl;
    return UatUrl;
}
```

Property order in the class: `ProdUrl`, `UatUrl`, `TestUrl`, `DevUrl` — matches promotion order top-to-bottom.

#### 2. `src/Flowline/Commands/FlowlineCommand.cs` — Base enum + switches

Add the new value to `EnvironmentRole` and extend the three switch expressions in `GetAndCheckEnvironmentInfoAsync`:

```csharp
public enum EnvironmentRole { Prod, Uat, Test, Dev }

// label switch:   EnvironmentRole.Uat => "UAT"
// flag switch:    EnvironmentRole.Uat => "--uat"
// URL switch:     EnvironmentRole.Uat => Config!.GetOrUpdateUatUrl(inputUrl, settings)
```

No type-guard changes needed for Sandbox environments — the existing guard (`role != Prod && type == Production → reject`) already rejects Production-type URLs for any non-Prod role.

#### 3. `src/Flowline/Commands/DeployCommand.cs` — Keyword resolution

Add the keyword to the `targetUrl` switch and update the `[Description]` attribute:

```csharp
// Before:
var targetUrl = settings.Target.ToLowerInvariant() switch
{
    "prod" => Config!.ProdUrl,
    "test" => Config!.TestUrl,
    _ => settings.Target
};

// After:
var targetUrl = settings.Target.ToLowerInvariant() switch
{
    "prod" => Config!.ProdUrl,
    "uat"  => Config!.UatUrl,
    "test" => Config!.TestUrl,
    _ => settings.Target
};
```

For a **Production-type** new environment (not UAT's case), no extra code is needed — the existing `if (targetEnv.Type == "Production")` confirmation check fires automatically for any URL that resolves to a Production-type environment, regardless of the keyword used.

#### 4. `src/Flowline/Commands/ProvisionCommand.cs` — Role enum + copy type + suffix

Three changes:

```csharp
// 1. Extend Role enum
public enum Role { Dev, Test, Uat }

// 2. Update suffix determination
var suffix = string.IsNullOrWhiteSpace(settings.Suffix)
    ? settings.Role switch { Role.Dev => "Dev", Role.Uat => "UAT", _ => "Test" }
    : settings.Suffix;

// 3. Add URL dispatch
string? url = settings.Role switch
{
    Role.Dev  => Config!.GetOrUpdateDevUrl(targetUrl, settings),
    Role.Test => Config!.GetOrUpdateTestUrl(targetUrl, settings),
    Role.Uat  => Config!.GetOrUpdateUatUrl(targetUrl, settings),
    _ => null
};

// 4. Update copyType (FullCopy for test-like environments)
string copyType = (settings.Role is Role.Test or Role.Uat || settings.CopyType == CopyType.Full)
    ? "FullCopy"
    : "MinimalCopy";
```

Also update `[Description]` on the `[role]` argument to list the new value.

#### 5. `src/Flowline/Commands/CloneCommand.cs` — Search option + order

Three changes:

```csharp
// 1. Add option to Settings
[CommandOption("--uat <URL>")]
[Description("UAT environment URL to clone solution from")]
public string? UatUrl { get; set; }

// 2. Save URL in ExecuteFlowlineAsync (alongside existing saves)
Config!.GetOrUpdateUatUrl(settings.UatUrl, settings);

// 3. Extend FindUnmanagedSourceAsync
foreach (var role in new[] { EnvironmentRole.Prod, EnvironmentRole.Uat, EnvironmentRole.Test, EnvironmentRole.Dev })
{
    var configUrl = role switch
    {
        EnvironmentRole.Prod => Config!.ProdUrl,
        EnvironmentRole.Uat  => Config!.UatUrl,
        EnvironmentRole.Test => Config!.TestUrl,
        EnvironmentRole.Dev  => Config!.DevUrl,
        _ => null
    };
    // ...
    var label = role switch { EnvironmentRole.Prod => "Prod", EnvironmentRole.Uat => "UAT", EnvironmentRole.Test => "Test", _ => "Dev" };
}
```

Update the `FlowlineException` error message at the end of `FindUnmanagedSourceAsync` to include `--uat`.

#### 6. `src/Flowline/Commands/StatusCommand.cs` — Display row

Insert the new entry in promotion order:

```csharp
var envs = new (string Label, string? Url)[]
{
    ("Production",  config.ProdUrl),
    ("UAT",         config.UatUrl),   // new
    ("Test",        config.TestUrl),
    ("Development", config.DevUrl),
};
```

### Step 3: Add tests for `GetOrUpdateXxxUrl`

Add five tests to `ProjectConfigTests.cs` covering: null input when empty (returns null), URL input when empty (sets and returns), null input when set (returns stored), same URL input when set (no conflict), and JSON round-trip.

## Why This Matters

Each command independently resolves the environment URL from the config. There is no central dispatch that fans out automatically — every command has its own switch or option set. Missing a command leaves it silently broken: `deploy uat` prints "Can't resolve 'uat'", `clone` skips the environment without an error, `status` just omits the row.

Following the checklist ensures the new environment is first-class everywhere.

## When to Apply

- Adding any named Sandbox environment between existing ones (Staging, Demo, Training, etc.)
- The pattern for Production-type environments is the same except: no type-guard changes needed, and the deploy confirmation fires automatically based on Dataverse environment type (not the keyword).

## Examples

The UAT addition that established this pattern touched exactly these 6 files and introduced no behavioral regressions. 184 existing tests continued to pass after the change.

Commit reference: `feat: add UAT environment` on branch `feat/add-uat-environment` (2026-06-06).
