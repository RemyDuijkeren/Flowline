---
title: "SyncCommand overwrote uncommitted src/ files without warning"
date: 2026-05-15
category: docs/solutions/logic-errors/
module: flowline-cli
problem_type: logic_error
component: tooling
symptoms:
  - "pac solution sync silently overwrote unpacked XML in solutions/<Name>/src/ with no warning"
  - "Developers lost uncommitted work with no opportunity to abort"
  - "On Windows: git status returned empty for untracked files in a new src/ directory when workingDirectory was omitted"
  - "Filenames in the dirty list had a trailing \\r when splitting git output on \\n only"
root_cause: missing_workflow_step
resolution_type: code_fix
severity: high
related_components:
  - git-utils
  - sync-command
tags:
  - git
  - dirty-tree
  - data-loss
  - windows
  - crlf
  - cli-guard
  - cliwrap
  - sync
---

# SyncCommand overwrote uncommitted src/ files without warning

## Problem

`pac solution sync` overwrites unpacked XML in `solutions/<Name>/src/` with no confirmation prompt. `SyncCommand` had no dirty-tree check before invoking pac, so developers with half-edited form components who ran sync to pick up a colleague's change lost work silently — no warning, only `git diff` as a recovery path after the fact.

`DeployCommand` had an existing repo-wide guard (`GitUtils.AssertRepoCleanAsync`); `SyncCommand` had none.

## Symptoms

- `flowline sync` completed with exit code 0 even when `solutions/<Name>/src/` had modified tracked files
- Uncommitted changes to solution XML (forms, entities, plugin registrations) disappeared after sync
- On Windows: with a brand-new `src/` directory (first sync after `pac solution clone`), the guard silently passed even when untracked files were present

## What Didn't Work

**Passing the absolute `srcPath` without `workingDirectory`** — the initial implementation called `GetUncommittedChangesInPathAsync(srcPath, cancellationToken: cancellationToken)` leaving `workingDirectory` as null. The relative-path conversion inside the method only fires when `workingDirectory != null`. With null, the raw absolute Windows path (e.g. `E:\Code\Project\solutions\CRM\src`) is passed directly to `git status --porcelain`. On Windows, git may return no output for untracked files when given an absolute pathspec for a directory that has no prior commits. The guard returned 0 dirty files and sync proceeded — defeating the feature at the highest-risk moment (first sync after clone).

**Splitting stdout on `'\n'` only** — `StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)` left a trailing `\r` on every filename on Windows where git emits CRLF. Returned paths were `src/form.xml\r` instead of `src/form.xml`. Tests using `.Contains("form.xml")` assertions didn't catch the corruption.

## Solution

### 1. New method `GitUtils.GetUncommittedChangesInPathAsync`

```csharp
public static async Task<IReadOnlyList<string>> GetUncommittedChangesInPathAsync(
    string path, string? workingDirectory = null, bool verbose = false,
    CancellationToken cancellationToken = default)
{
    var cmd = Cli.Wrap("git");
    if (workingDirectory != null)
        cmd = cmd.WithWorkingDirectory(workingDirectory);

    // Relative paths required for untracked file reporting on some platforms;
    // absolute paths can silently omit untracked entries in new directories
    var pathArg = workingDirectory != null && Path.IsPathRooted(path)
        ? Path.GetRelativePath(workingDirectory, path)
        : path;

    var result = await cmd
        .WithArguments(args => args.Add("status").Add("--porcelain").Add("--").Add(pathArg))
        .WithToolExecutionLog(verbose)
        .ExecuteBufferedAsync(cancellationToken);

    return result.StandardOutput
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line[3..])
        .ToList();
}
```

### 2. Guard in `SyncCommand.ExecuteFlowlineAsync`

Insert after the `.cdsproj` existence check (so `slnFolder` is known) and before `EnsureMapFilePathAsync` (the first write operation):

```csharp
if (!settings.Force)
{
    var srcPath = Path.Combine(slnFolder, "src");
    var dirty = await GitUtils.GetUncommittedChangesInPathAsync(
        srcPath, workingDirectory: RootFolder, verbose: settings.Verbose,
        cancellationToken: cancellationToken);
    if (dirty.Count > 0)
    {
        Console.Error($"Uncommitted changes in '{projectSln.Name}/src/' — stash or commit first, or re-run with --force.");
        foreach (var file in dirty)
            Console.Skip($"  {Markup.Escape(file)}");
        return 1;
    }
}
```

`-f|--force` is inherited from `FlowlineSettings`. No new property needed on `SyncCommand.Settings`.

## Why This Works

`git status --porcelain -- <pathspec>` reports only files under the given path, so dirty files in `Plugins/` or another solution's `src/` never trigger the guard. The `--` separator prevents git from misinterpreting the path as a revision.

**The `workingDirectory`/relative-path conversion is load-bearing.** On Windows, `git status` with an absolute pathspec for a directory created by `pac solution clone` (no prior commit in that dir) may return no output even when untracked files exist. Passing `workingDirectory: RootFolder` converts the absolute `src/` path to a relative one via `Path.GetRelativePath` before git sees it. `RootFolder` (the git repo root) is always available from `FlowlineCommand`.

**`Split(new[] { '\r', '\n' }, ...)` not `Split('\n', ...)`** — CliWrap's buffered stdout can include `\r\n` on Windows. Splitting on both chars matches the pattern already used in `GetRemoteUrlAsync` in the same file. `line[3..]` then extracts the filename: porcelain v1 format is `XY filename` (two status chars + space).

**No try/catch.** CliWrap throws on non-zero exit codes by default. Git utility methods have no exception handling — infrastructure failures (git not on PATH, wrong CWD, corrupt repo) propagate as exceptions so users report them and they get fixed, not silently swallowed as pass-through clean results.

## Prevention

**When wiring a dirty-tree guard to a new command that overwrites solution files:**

Use `GetUncommittedChangesInPathAsync` rather than the repo-wide `AssertRepoCleanAsync` when the destructive scope is a single solution's `src/`. This gives a tighter, more actionable error and avoids false positives from unrelated solutions.

Checklist:
- Always pass `workingDirectory: RootFolder` — never omit it; null triggers the absolute-path code path that silently misses untracked files on Windows
- Insert the guard after existence checks but before the first write operation
- Reuse `settings.Force` from `FlowlineSettings` — do not add a duplicate property to the command's `Settings` class
- Pass `verbose: settings.Verbose` to honour the `--verbose` flag
- Use `Console.Skip($"  {Markup.Escape(file)}")` for the per-file list (dim, pre-escaped)

**In tests for this method:**
- Always pass `workingDirectory: _root` — this exercises the relative-path conversion (the production call path)
- Assert exact relative paths (`.Should().Be("src/form.xml")`), not `.Contains("form.xml")` — `.Contains` passes even with a trailing `\r`

## Related Issues

- Requirements: `docs/brainstorms/2026-05-15-sync-pre-sync-guard-requirements.md`
- Implementation plan: `docs/plans/2026-05-15-001-feat-sync-pre-sync-dirty-guard-plan.md`
