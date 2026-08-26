# Push `--no-publish` — Requirements

**Date:** 2026-06-29
**Status:** Ready for planning
**Scope:** Lightweight

## Problem

`flowline push` always runs `PublishXml` after syncing web resources — wired via
`WebResourceExecutor.cs:104-111`, called with `publishAfterSync = true` hardcoded
at `PushCommand.cs:141`. In CI pipelines that batch multiple pushes before a
single publish, or that hand publish to another tool, every `flowline push` pays
an unnecessary Dataverse roundtrip.

The `publishAfterSync` parameter already exists on
`WebResourceService.SyncSolutionAsync` (`WebResourceService.cs:19`) — it is just
never exposed as a CLI flag.

## Goal

Add a `--no-publish` flag to `push` that skips the `PublishXml` step after
syncing web resources, leaving publish to a separate step or tool.

## Requirements

- `--no-publish` boolean option on `push`. Default false (publish happens, same
  as today).
- Applies to web resources only. Plugin push has no publish step; `--no-publish`
  is silently ignored when `--scope plugins` is set.
- When set, pass `publishAfterSync: false` through `PushCommand` →
  `WebResourceService.SyncSolutionAsync` → `WebResourceExecutor`.
- `--no-publish` + `--dry-run` compose trivially: dry-run already suppresses all
  Dataverse writes, so the combination needs no special handling.
- Default behavior unchanged — users who do not set the flag get the same
  publish-on-push they get today.

## Decisions

- **`--no-publish` over `--skip-publish`.** Flowline uses two negative-prefix
  patterns with distinct semantics: `--no-X` disables a pipeline step (`--no-build`,
  `--no-delete`); `--skip-X` bypasses a guard or safety check (`--skip-dtap-check`).
  Publish is a pipeline step, not a guard — `--no-publish` is the right fit.
  Daxif uses `--skip-publish`, but Flowline's internal consistency takes precedence.
- **Web resources only, not a global "publish" concept.** Plugins are live on
  deploy; Dataverse publish is a web-resource/customization concept. No ambiguity
  introduced.

## Success Criteria

- `flowline push --no-publish` syncs web resources without triggering any
  `PublishXml` request.
- `flowline push` (no flag) continues to publish after sync, as today.
- `flowline push --scope plugins --no-publish` produces no error and behaves
  identically to `flowline push --scope plugins`.

## Documentation

- **README** — add `--no-publish` to the `push` command usage/examples section.
- **Wiki `Command-Reference.md`** — add flag entry under `push`, describe the
  CI use case (batch push + single publish step).
- **Flowline examples** — add or update any push examples that show CI pipeline
  usage to demonstrate `--no-publish`.

## Out of Scope

- A standalone `flowline publish` command — separate concern, tracked separately
  if needed.
- `--no-publish` on `sync` or `clone` — not requested; add later if the same
  friction materializes there.
