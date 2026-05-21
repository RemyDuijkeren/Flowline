---
title: "Version bump must precede pac solution sync to avoid stale XML"
date: 2026-05-21
category: docs/solutions/logic-errors/
module: flowline-cli
problem_type: logic_error
component: tooling
severity: high
symptoms:
  - "solutions/<Name>/src/Other/Solution.xml contains old version after sync"
  - "Version mismatch: Dataverse holds new version but local XML reflects old one"
  - "Git tag version does not match version in synced XML"
root_cause: logic_error
resolution_type: code_fix
related_components:
  - SyncCommand
  - PacUtils
tags:
  - version-management
  - pac-cli
  - operation-ordering
  - dataverse-sync
  - SyncCommand
---

# Version bump must precede pac solution sync to avoid stale XML

## Problem

When adding automatic version bumping to `SyncCommand`, the version bump was initially placed after `pac solution sync`. Since `pac solution sync` downloads the solution XML from Dataverse at the moment it runs — including the version number stored there — bumping after sync causes the local XML to always reflect the pre-bump version. Local and remote are immediately inconsistent after every sync cycle.

## Symptoms

- `solutions/<Name>/src/Other/Solution.xml` shows old version after sync (e.g., `1.0.0.1` even though Dataverse now holds `1.0.1.0`)
- Git tag (e.g., `1.0.1`) is created at a commit where solution XML still shows the previous version
- A second sync run is required to see the correct version in local XML

## What Didn't Work

No alternative approaches were tried — caught in code review before runtime. The incorrect ordering produced no visible error: `pac solution sync` succeeded and the XML was valid. The version staleness was a silent data inconsistency with no warning.

## Solution

Move the version read, bump, and write to Dataverse **before** the `pac solution sync` call. Success messages and git tag creation stay at the end, after all validation steps.

```csharp
// Correct: Bump version BEFORE sync so the downloaded XML reflects the new version
var currentVersion = await PacUtils.GetSolutionVersionAsync(
    slnInfo.SolutionUniqueName!, devEnv.EnvironmentUrl!, settings.Verbose, cancellationToken);
var newVersion = BumpVersion(currentVersion, settings.Bump);
var tagVersion = ToTagVersion(newVersion);
await PacUtils.SetSolutionVersionAsync(
    slnInfo.SolutionUniqueName!, newVersion, devEnv.EnvironmentUrl!, settings.Verbose, cancellationToken);

// pac solution sync now downloads XML with the bumped version
CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
    $"Syncing solution [bold]{projectSln.Name}[/]...", ctx => ...);
```

**Wrong (original ordering):**

```csharp
// Sync first — downloads XML at old version
CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(...);

// Too late — local XML already written with old version number
await PacUtils.SetSolutionVersionAsync(..., newVersion, ...);
```

## Why This Works

`pac solution sync` downloads the solution XML from Dataverse as it exists at the moment of the call. By writing the new version to Dataverse first, the XML downloaded by the sync already contains the bumped version — local and remote are immediately consistent. Success messages and git tag creation follow after pack + build + drift check validation, so they only run when the full sync cycle succeeded.

## Prevention

- When combining a remote write with a PAC CLI sync, always order: **write → download**, never **download → write-what-you-downloaded**.
- This applies to any sequence involving `pac solution online-version`, `pac solution set-version`, or similar mutations combined with `pac solution sync` or `pac solution pull`.
- Review operation ordering any time a new step is added to `SyncCommand`'s `ExecuteFlowlineAsync` — the existing flow is: preflight → version bump → sync → pack → build → drift check → tag.

## Related Issues

- `docs/solutions/logic-errors/sync-overwrites-uncommitted-src-without-warning-2026-05-15.md` — complementary guard: prevents `SyncCommand` from overwriting uncommitted local changes before sync runs
- `docs/solutions/architecture-patterns/sync-first-remove-mapping-replace-dotnet-build-2026-05-17.md` — broader sync-first ALM pattern context
