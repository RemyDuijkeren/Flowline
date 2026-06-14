# Push `--no-build` — Requirements

**Date:** 2026-06-14
**Status:** Ready for planning
**Scope:** Lightweight

## Problem

`flowline push` always runs `dotnet build` (Release) for in-scope assets before
pushing — plugins at `PushCommand.cs:190`, webresources at `PushCommand.cs:225`.
Developers frequently build the projects themselves during verification and
testing, so push pays the build cost a second time for no benefit. The build can
be slow (C# compile for plugins; npm install + rollup for webresources).

## Goal

Add a `--no-build` flag to `push` that skips the build step and pushes the
artifacts already on disk, mirroring `dotnet --no-build`.

## Requirements

- `--no-build` boolean option on `push`, project mode. Default false.
- Honors `--scope`: skips the build only for the assets actually being pushed
  (plugins, webresources, or both).
- Skips both build calls in `PushCommand` — `PreparePluginsForPushAsync`
  (`:190`) and `PrepareWebResourcesForPushAsync` (`:225`).
- **Strict, Release-only.** Push reads Release output
  (`bin/Release/net462/publish/Plugins.dll` for plugins, `dist/` for
  webresources). With `--no-build`, expect those same Release artifacts. No
  fallback to Debug, no auto-detect of the built configuration.
- **Missing artifacts → clear error, nothing pushed, non-zero exit.**
  - Plugins: the existing `File.Exists` check (`PushCommand.cs:196`) already
    errors when the DLL is absent — refine the message to mention `--no-build`
    (e.g. "build Release first, or drop `--no-build`").
- **Unconditional `dist/` safety guard (independent of `--no-build`).** Before
  pushing web resources, guard against a missing or empty `dist/` **whether or
  not `--no-build` is set**. An empty local set in Normal mode makes push compute
  deletes for every remote web resource — a mass-delete. Today only the project
  *folder* existence is checked (`PushCommand.cs:219`), not `dist/`. `--no-build`
  (skipped build) is one way to reach an empty `dist/`; a build that emits
  nothing is another. Error out, push nothing.

## Decisions

- **Config handling: require Release, error if missing.** Chosen over
  "prefer Release, fall back to Debug" (risks Debug plugins / unminified
  webresources landing in Dataverse) and over an explicit `--configuration` flag
  (extra concept and typing). Strict matches `dotnet --no-build` semantics.
- **`--no-build` + `--dry-run` compose.** Skip build, preview the plan against
  on-disk artifacts, touch nothing in Dataverse. No special handling needed.
- **`dist/` guard is unconditional, not gated on `--no-build`.** It is a
  mass-delete safety net — an empty `dist/` is dangerous however it arises.
  Standalone mode: `--no-build` silently ignored (build branch already bypassed).

## Success Criteria

- `flowline push --no-build`, with current Release artifacts present, pushes
  without invoking `dotnet build`.
- `flowline push --no-build` with no/stale Release artifacts fails with an
  actionable message and a non-zero exit code, pushing nothing.
- `--no-build` respects `--scope` (e.g. `--scope plugins --no-build` skips only
  the plugins build).
- Push against an absent/empty `dist/` (with or without `--no-build`) refuses
  and deletes no remote web resources.

## Out of Scope / Deferred

- **`sync` and `clone`** also build (`SyncCommand.cs:117`, `CloneCommand.cs:76`).
  Not covered here — add a parallel flag later if the same friction appears.
- **Standalone mode** (`--pluginFile` / `--webresources`) already skips the build
  and uses prebuilt artifacts, so `--no-build` is a no-op there. Treat as
  ignored/harmless rather than an error.
- **`--configuration` flag** to push a non-Release build — rejected this round.

## Open Questions

- Resolved during planning: `--no-build` in standalone mode is silently ignored.
- The unconditional `dist/` guard blocks the rare case of intentionally emptying
  web resources to delete them all remotely. Decision: hard guard, no bypass —
  `--force` does not skip it (`--force` = skip confirmation prompts, not override
  safety guards). A dedicated explicit path (`--allow-empty` or a `remove`
  operation) can be added later if the need materializes.
