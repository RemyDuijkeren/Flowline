# Flowline AI-Friendliness Improvements

**Date:** 2026-06-07
**Status:** Ready for planning

## Overview

Three improvements to make Flowline usable by AI agents (Claude Code, GitHub Copilot) without ambiguity:

1. **Structured exit codes** — replace always-return-1 with an enum aligned to de facto CLI conventions
2. **AGENTS.md scaffolding** — `flowline clone` generates an AGENTS.md at repo root giving agents the complete workflow contract
3. **Expanded help text** — all 7 command descriptions follow the "what + trigger + state change" pattern

## Current State

- All `FlowlineException` paths return exit code `1`; `OperationCanceledException` returns `130`
- No `AGENTS.md` exists in solution repos
- Help text is minimal (e.g. "Push plugins and web resources to Dataverse")
- No `ExitCode` enum; `.ExitCode` references in code are from CliWrap, not Flowline's own

## Exit Codes

### Enum definition

```csharp
public enum ExitCode
{
    Success = 0,
    GeneralError = 1,
    // 2 intentionally unused — Spectre handles argument validation
    NotFound = 3,           // de facto convention: resource not found
    NotAuthenticated = 4,   // de facto convention: unauthorized
    // 5 intentionally unused — no forbidden/permissions concept in Flowline
    ConnectionFailed = 10,
    ConfigInvalid = 11,
    DirtyWorkingDirectory = 12,
    BuildFailed = 13,
    VersionConflict = 14,
    ValidationFailed = 15,
    Timeout = 16,
    ForceRequired = 17,
    Cancelled = 130,        // de facto convention: SIGINT / Ctrl+C
}
```

Codes 3 and 4 align with de facto CLI conventions (curl, git, etc.) so agents with prior CLI knowledge interpret them without reading the table.

### Wiring

- Add `ExitCode ExitCode { get; init; } = ExitCode.GeneralError;` to `FlowlineException`
- Update `Program.cs` exception handler: return `(int)fe.ExitCode` instead of `1`
- Update every `throw new FlowlineException(...)` site with the appropriate code (see mapping below)
- `OperationCanceledException` stays `130` (already correct)

### Throw-site mapping

| Condition | Code |
|-----------|------|
| No PAC profile found | `NotAuthenticated` (4) |
| Environment URL unreachable | `ConnectionFailed` (10) |
| `.flowline` missing or malformed | `ConfigInvalid` (11) |
| Uncommitted changes in `Package/src/` block sync | `DirtyWorkingDirectory` (12) |
| Clean repo check fails before deploy | `DirtyWorkingDirectory` (12) |
| `dotnet build` non-zero exit | `BuildFailed` (13) |
| Solution not found in Dataverse or repo | `NotFound` (3) |
| Target environment has newer version | `VersionConflict` (14) |
| Drift detected (local not in Dataverse) | `ValidationFailed` (15) |
| Missing dependencies, schema mismatch | `ValidationFailed` (15) |
| PAC CLI timeout (60-min limit) | `Timeout` (16) |
| Config overwrite confirmation in non-interactive mode | `ForceRequired` (17) |

### Error message requirements for actionable codes

Error messages for these codes must include the corrective action:

| Code | Required message pattern |
|------|--------------------------|
| 4 | "Not authenticated. Run: pac auth create --environment \<url\>" |
| 12 | "Uncommitted changes in \<path\>. Commit or stash before running \<command\>." |
| 14 | "Version conflict. Add --force to overwrite." |
| 17 | "Non-interactive mode requires --force for \<operation\>. Add --force to proceed." |

## AGENTS.md Template

`flowline clone` scaffolds this file at the repo root. `<SolutionName>` is substituted with the actual solution name at scaffold time.

```markdown
# Flowline — Agent Instructions

Flowline is the ALM CLI for this Power Platform solution repo.
Use Flowline commands instead of PAC CLI directly.

## Daily dev loop

```
dotnet build                    # build plugin assembly
flowline push --dry-run         # preview what would be registered (optional safety check)
flowline push                   # register DLL + web resources in DEV
flowline sync                   # pull solution state from DEV, bump version, unpack to XML
git add . && git commit -m "…"  # commit the unpacked XML diff
flowline deploy test            # promote to TEST
flowline deploy prod            # promote to PROD
```

## Generate early-bound types (run after entities or custom APIs change)

```
flowline generate               # regenerate Plugins/Models/ from solution entities
```

## Rules

- Never run `pac solution` commands directly — Flowline wraps them correctly.
- Always run `flowline push` before `flowline sync` when plugin code changed.
- `flowline sync` requires no uncommitted changes in `Package/src/` (exit code 12 if dirty).
- `flowline deploy` requires a clean git working directory (exit code 12 if dirty).
- DEV is the source of truth. Sync captures its state; never hand-edit unpacked XML.
- `clone`, `push`, and `sync` require an unmanaged solution in DEV — they fail on managed environments.
- To deploy a managed package, use `--managed` on `deploy`. Requires the solution to have been cloned or synced with `--managed` first.
- In repos with multiple solutions, pass the solution name as the first argument: `flowline push <SolutionName>`, `flowline sync <SolutionName>`, etc.

## Project structure

```
.flowline                            ← environment URLs + solution config
solutions/<SolutionName>/
  Package/Package.cdsproj            ← solution package project (PAC-managed, do not edit)
  Package/src/                       ← unpacked solution XML (git-diffable)
  Plugins/Plugins.csproj             ← plugin source, decorated with [Step] attributes
  Plugins/Models/                    ← early-bound C# types (from flowline generate)
  WebResources/WebResources.csproj   ← web resource assets
  WebResources/dist/                 ← build output synced to Dataverse
```

## Exit codes

| Code | Meaning | Fix |
|------|---------|-----|
| 0 | Success | |
| 1 | General error | Check error output |
| 3 | Not found | Verify solution name matches .flowline config |
| 4 | Not authenticated | Run: `pac auth create --environment <url>` |
| 10 | Connection failed | Check environment URL in .flowline |
| 11 | Config invalid | Check .flowline exists and is valid |
| 12 | Dirty working directory | Commit or stash changes first |
| 13 | Build failed | Fix `dotnet build` errors in Plugins/ |
| 14 | Version conflict | Add --force to overwrite older version |
| 15 | Validation failed | Check error output for drift or missing dependencies |
| 16 | Timeout | PAC CLI 60-min limit hit — retry or check environment health |
| 17 | Force required | Add --force flag |
| 130 | Cancelled | Ctrl+C pressed |

## Environments

Defined in `.flowline`. Use `flowline status` to verify connectivity before running commands.
```

## Help Text Changes

All changes are in `src/Flowline/Program.cs`.

### `clone`
**Before:** `Clone an existing unmanaged solution into this repo.`
**After:** `Initialize a Flowline project from an existing Dataverse solution. Creates folder structure, unpacks solution XML, scaffolds Plugins and WebResources projects, and generates AGENTS.md. One-time setup per solution.`

### `push`
**Before:** `Push plugins and web resources to Dataverse`
**After:** `Build and register plugin assembly and web resources directly to DEV — skips pack/import. Reads [Step] attributes to create or update plugin registrations. Run after plugin or web resource changes.`

### `sync`
**Before:** `Pull dev changes back into the repo`
**After:** `Export solution from DEV, bump build version, and unpack to source-controlled XML. Run after testing changes in DEV. Requires no uncommitted changes in Package/src/.`

### `deploy`
**Before:** `Deploy solution to test or prod environment`
**After:** `Pack solution from repo and import into target environment (test, uat, prod, or URL). Requires clean git working directory.`

### `provision`
**Before:** `Copy prod into dev or test environment`
**After:** `Create a DEV, TEST, or UAT environment by copying from production. Saves environment URL to .flowline. One-time setup for new environments.`

### `status`
**Before:** `Show Flowline, PAC CLI, and project status`
**After:** `Show configured environments, connection status, solution version, PAC CLI auth status, and git state. Use to verify setup before running commands.`

### `generate`
**Before:** `Generate early-bound C# types for the solution's entities and custom APIs`
**After:** `Generate early-bound C# types from solution entities and custom APIs. Overwrites Plugins/Models/ with generated .cs files. Run after adding or modifying entities or custom APIs.`

## Acceptance Criteria

1. `ExitCode` enum defined in `src/Flowline/ExitCode.cs`
2. `FlowlineException` has `ExitCode ExitCode { get; init; }` property, defaults to `GeneralError`
3. `Program.cs` handler returns `(int)fe.ExitCode`
4. All throw sites in `FlowlineException` updated with appropriate `ExitCode`
5. Error messages for codes 4, 12, 14, 17 include explicit corrective action text
6. `flowline clone` scaffolds `AGENTS.md` at repo root with `<SolutionName>` substituted
7. All 7 command descriptions updated in `Program.cs`
8. Exit codes treated as stable public API — no renumbering without a breaking change notice

## Out of Scope

- `--json` flag (already removed)
- PAC CLI wrapping behavior changes
- AGENTS.md content for multi-solution repos
- Exit code documentation in GitHub Wiki (separate task)
