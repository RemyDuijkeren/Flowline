---
title: "feat: Add --no-build flag to push"
type: feat
date: 2026-06-14
depth: Lightweight
origin: docs/brainstorms/2026-06-14-push-no-build-requirements.md
---

# feat: Add `--no-build` flag to `push`

## Summary

Add a `--no-build` flag to `flowline push` that skips the `dotnet build` step for
in-scope assets and pushes the Release artifacts already on disk. Mirrors
`dotnet --no-build`: strict, Release-only, clear error when artifacts are absent.

## Problem Frame

`push` always runs `dotnet build` (Release) before pushing — plugins at
`src/Flowline/Commands/PushCommand.cs:190`, webresources at
`src/Flowline/Commands/PushCommand.cs:225`. Developers routinely build the
projects themselves during verification and testing, so push pays the cost a
second time for no benefit. The build is slow (C# compile for plugins; npm
install + rollup for webresources).

## Requirements

- **R1** — `--no-build` boolean option on `push`, project mode, default false.
- **R2** — Honors `--scope`: skips build only for the assets being pushed.
- **R3** — Strict Release-only. With `--no-build`, expect the same Release output
  push already reads (`bin/Release/net462/publish/Plugins.dll`, `dist/`). No
  Debug fallback, no config auto-detect.
- **R4** — Missing plugins artifact under `--no-build` → clear actionable error,
  nothing pushed, non-zero exit. Refine the existing message to mention
  `--no-build`.
- **R5** — `--no-build` + `--dry-run` compose: skip build, preview against
  on-disk artifacts, touch nothing.
- **R6** — Standalone mode (`--pluginFile`/`--webresources`) already skips build;
  `--no-build` is silently ignored there (no code — build branch is already
  bypassed). (see origin: docs/brainstorms/2026-06-14-push-no-build-requirements.md)
- **R7 (safety, unconditional)** — Before pushing web resources, guard against a
  missing or empty `dist/` **regardless of `--no-build`**. An empty local set in
  Normal mode makes push compute deletes for every remote web resource — a
  mass-delete. Error out, push nothing, non-zero exit. `--no-build` is just one
  way to reach an empty `dist/` (skipped build); a misconfigured build that
  emits nothing is another.

## Key Technical Decisions

- **Strict Release, error if missing** — chosen over Debug fallback (risks Debug
  plugins / unminified webresources reaching Dataverse) and over an explicit
  `--configuration` flag (extra concept). Matches `dotnet --no-build`.
- **`dist/` guard is unconditional, not gated on `--no-build`** — it is a
  mass-delete safety net (R7). `--no-build` raised its visibility, but an empty
  `dist/` is dangerous however it arises. Runs in project mode before push,
  whether or not a build ran.
- **Extract the `dist/` guard as a static helper** — push integration isn't unit
  tested (needs Dataverse/dotnet), but static helpers are
  (`tests/Flowline.Tests/PushCommandTests.cs`). A static
  `EnsureBuiltWebResources` is testable in isolation like the existing
  `ResolveStandaloneWebResourcesPath`. The build-skip and plugins-message paths
  stay inline and are verified by hand.
- **Standalone: silently ignore** — standalone Prepare methods early-return
  before the build call, so `--no-build` has no effect with zero added code.

## Implementation Units

### U1. Add `--no-build` option and skip build

**Goal:** Add the flag and skip `BuildSolutionAsync` for in-scope assets in
project mode.

**Requirements:** R1, R2, R3, R5.

**Dependencies:** none.

**Files:**
- `src/Flowline/Commands/PushCommand.cs` (modify)

**Approach:**
- Add `NoBuild` to `PushCommand.Settings` mirroring the existing `--no-delete`
  option (`[CommandOption("--no-build")]`, `[Description]`, `[DefaultValue(false)]`).
- Thread the flag into `PreparePluginsForPushAsync` and
  `PrepareWebResourcesForPushAsync` (pass `settings.NoBuild` or `settings`).
- Guard each `DotNetUtils.BuildSolutionAsync` call so it is skipped when
  `NoBuild` is set. Artifact path resolution (`bin/Release/.../Plugins.dll`,
  `dist/`) is unchanged — Release-only by construction.
- Standalone branches already early-return before the build call — no change
  needed for R6.

**Patterns to follow:** the `--no-delete` option declaration
(`src/Flowline/Commands/PushCommand.cs:47`) and how `NoDelete` flows into
`ResolveRunMode`.

**Test scenarios:** Test expectation: none — flag plumbing and build-skip run
through the Dataverse/dotnet push path, which is integration-only. Verified by
hand (see Verification).

**Verification:** `flowline push --no-build` with current Release artifacts
present completes without a "Building …" spinner / `dotnet build` invocation, and
pushes. `--scope plugins --no-build` skips only the plugins build.

### U2. Refine plugins error + unconditional webresources `dist/` safety guard

**Goal:** Make the missing-plugins failure clear under `--no-build`, and prevent
push from ever computing a mass-delete from an empty `dist/`.

**Requirements:** R4, R7.

**Dependencies:** U1.

**Files:**
- `src/Flowline/Commands/PushCommand.cs` (modify)
- `tests/Flowline.Tests/PushCommandTests.cs` (modify)

**Approach:**
- Plugins: refine the existing not-found message
  (`src/Flowline/Commands/PushCommand.cs:196-199`) to mention `--no-build`
  (e.g. "build Release first, or drop `--no-build`"). Apply tone-of-voice rules.
- Webresources: add a static `EnsureBuiltWebResources(distPath)` that throws
  `FlowlineException(ExitCode.NotFound, …)` when `dist/` is missing or contains
  no files. Call it in `PrepareWebResourcesForPushAsync` **on the normal path
  too**, not only the no-build branch — after the build (when one runs) and
  before returning the dist path. Today only the project folder is checked
  (`src/Flowline/Commands/PushCommand.cs:219`), so an empty `dist/` flows through
  to a mass-delete. Message explains the refusal and points to building or
  checking source (apply tone-of-voice rules).

**Patterns to follow:** static guard helpers `ResolveStandaloneWebResourcesPath`
/ `ResolveStandalonePluginFilePath` (`src/Flowline/Commands/PushCommand.cs:298,315`)
and their tests (`tests/Flowline.Tests/PushCommandTests.cs:128-170`).

**Test scenarios (for `EnsureBuiltWebResources`):**
- Happy path: `dist/` exists with one or more files → returns normally / no throw.
- Edge: `dist/` exists but empty → throws `FlowlineException`.
- Error: `dist/` does not exist → throws `FlowlineException`.

**Verification:** `flowline push --no-build` with no/stale Release plugin DLL
fails with the refined message, non-zero exit, nothing pushed. Push (with or
without `--no-build`) against an absent/empty `dist/` refuses with the safety
message and does not delete remote web resources.

### U3. Document the flag

**Goal:** Surface `--no-build` in user-facing docs.

**Requirements:** R1–R6 (user-facing surface).

**Dependencies:** U1, U2.

**Files:**
- `README.md` (modify, if it lists push flags)
- Wiki (sibling repo `Flowline.wiki`): `Command-Reference.md` (modify) — add
  `--no-build` with the strict-Release behavior and missing-artifact errors.

**Approach:** Document the flag, its strict Release-only semantics, the
missing-artifact failure, and that it honors `--scope`. Note it is a no-op in
standalone mode. Per repo convention (`CLAUDE.md`), update the wiki alongside any
README change.

**Test scenarios:** Test expectation: none — docs only.

**Verification:** Command-Reference lists `--no-build` with accurate behavior.

## Scope Boundaries

### Deferred to Follow-Up Work

- A parallel `--no-build` for `sync` and `clone`, which also build
  (`src/Flowline/Commands/SyncCommand.cs:117`,
  `src/Flowline/Commands/CloneCommand.cs:76`). Add if the same friction appears.

### Out of Scope

- A `--configuration` flag to push a non-Release build — rejected in the
  brainstorm in favor of strict Release.
- Debug-artifact fallback or built-config auto-detection.

## Open Questions

- **Intentional full removal.** The unconditional `dist/` guard (R7) blocks the
  rare case where a dev empties the web resources to delete them all remotely.
  Decision: **hard guard, no bypass** — `--force` does NOT skip it (`--force`
  means "skip confirmation prompts", not "override safety guards", so coupling
  would silently disarm the mass-delete protection for habitual `--force` users).
  If the intentional-wipe need ever materializes, add a dedicated explicit path
  (e.g. `--allow-empty` or a `remove` operation) then. Not blocking.
- **Dry-run.** Guard fires under `--dry-run` too (errors instead of previewing a
  mass-delete). Consistent and safe; revisit only if previewing the delete is
  ever wanted. Not blocking.
