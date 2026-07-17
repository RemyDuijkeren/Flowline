---
title: Standalone Push/Generate Active Profile Default - Plan
type: feat
date: 2026-07-17
topic: standalone-active-profile-default
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Standalone Push/Generate Active Profile Default - Plan

## Goal Capsule

- **Objective:** remove the mandatory `--dev <url>` flag in standalone `flowline push` and `flowline generate` by defaulting the target environment to PAC CLI's currently-active auth profile, while refusing to ever default onto a Production org.
- **Product authority:** resolved in this brainstorm dialogue, grounded against `pac solution import --environment`'s own documented default-to-active-profile behavior.
- **Open blockers:** none.

## Product Contract

### Summary

Standalone `flowline push` and `flowline generate` (no `.flowline` project present) currently require `--dev <url>` on every invocation. Both commands instead default to PAC CLI's currently-active auth profile when `--dev` is omitted, print the resolved environment before touching anything, and refuse outright if that environment is Production-type.

### Requirements

- R1. When `--dev` is omitted in standalone mode, `flowline push` and `flowline generate` resolve the target environment from PAC CLI's currently-active auth profile instead of erroring immediately.
- R2. Before performing any action against the resolved environment, the command prints which environment it resolved to.
- R3. If the resolved environment's type is Production, the command refuses with a clear error instead of proceeding — standalone mode has no `--prod` concept and must never default onto a production org.
- R4. If no PAC auth profile is currently active, the command errors clearly, directing the user to run `pac auth create` or pass `--dev <url>` explicitly.
- R5. An explicit `--dev <url>` flag always takes precedence over the active-profile default.

### Key Decisions

- **Print + proceed, no blocking confirmation.** Matches `pac solution import`'s own documented behavior ("When not specified, the active organization selected for the current auth profile will be used.") and Flowline's existing `ProfileResolutionService.EmitStatusLine` pattern. Avoids adding a `--yes`/confirm flag to a command whose purpose is removing flag friction.
- **Covers both `push` and `generate`.** Both share the identical standalone `--dev`-required pattern today (`PushCommand.cs:571`, `GenerateCommand.cs:114`); fixing only one would leave the other inconsistently stuck erroring.
- **Project-mode target resolution is untouched.** Commands run inside a Flowline project already resolve Dev/Prod from `.flowline` with zero required flags; this feature only addresses the flagless standalone gap.

### Scope Boundaries

- Project-mode (`.flowline` present) commands and their Prod/UAT/Test target resolution are out of scope.
- No new confirmation flag or interactive prompt is introduced.

### Acceptance Examples

- AE1. Standalone push, `--dev` omitted, active profile points to a non-Production environment.
  - **Given:** no `--dev` flag; PAC CLI has an active auth profile pointing to a Sandbox/Dev environment.
  - **When:** `flowline push --pluginFile <path>` runs.
  - **Then:** the resolved environment prints, then push proceeds against it.
  - **Covers:** R1, R2, R5.
- AE2. Standalone push, `--dev` omitted, active profile points to a Production org.
  - **Given:** no `--dev` flag; the active profile's environment type is Production.
  - **When:** the command runs.
  - **Then:** the command refuses with a clear error before touching anything.
  - **Covers:** R3.
- AE3. Standalone push, `--dev` omitted, no active PAC profile.
  - **Given:** `pac auth list` has no active entry.
  - **When:** the command runs.
  - **Then:** the command errors, directing the user to `pac auth create` or `--dev <url>`.
  - **Covers:** R4.
- AE4. Standalone push with `--dev <url>` explicitly given.
  - **Given:** `--dev <url>` is passed, regardless of active-profile state.
  - **When:** the command runs.
  - **Then:** the explicit URL is used; active-profile default logic never engages.
  - **Covers:** R5.
