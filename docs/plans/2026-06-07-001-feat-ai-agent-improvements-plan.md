---
title: "feat: Flowline AI-agent improvements"
type: feat
status: completed
date: 2026-06-07
origin: docs/brainstorms/2026-06-07-ai-agent-improvements-requirements.md
---

# feat: Flowline AI-agent improvements

## Summary

Three changes that make Flowline usable by AI agents without ambiguity: a typed `ExitCode` enum replaces always-return-1; `flowline clone` scaffolds an `AGENTS.md` at the repo root; all seven command descriptions follow the "what + trigger + state change" pattern.

---

## Problem Frame

AI agents (Claude Code, GitHub Copilot) interacting with Flowline currently receive exit code `1` for every failure, making corrective action impossible without parsing free-form error output. No `AGENTS.md` contract exists in solution repos, leaving agents to infer the workflow and command sequencing. Command help text is terse and omits trigger and state-change context that agents rely on for command selection.

(see origin: `docs/brainstorms/2026-06-07-ai-agent-improvements-requirements.md`)

---

## Requirements

- **R1.** `ExitCode` enum defined with de facto convention slots (3=NotFound, 4=NotAuthenticated, 130=Cancelled) and Flowline-specific codes 10–17
- **R2.** `FlowlineException` carries a typed `ExitCode`; `Program.cs` returns `(int)fe.ExitCode` instead of `1`
- **R3.** All FlowlineException throw sites and direct `return 1` paths in commands assign the correct typed code
- **R4.** Error messages for codes 4, 12, 14, and 17 include the explicit corrective action
- **R5.** `flowline clone` scaffolds `AGENTS.md` at the repo root with the solution name substituted
- **R6.** All seven command descriptions updated to the "what + trigger + state change" pattern
- **R7.** Exit codes are stable across versions — treat as public API; no renumbering without a breaking change notice

---

## Scope Boundaries

**In scope:** ExitCode enum, FlowlineException wiring, throw-site and direct-return-path updates, AGENTS.md scaffolding, command help text.

**Out of scope:** `--json` flag (already removed), PAC CLI wrapping behavior, AGENTS.md for multi-solution repos.

### Deferred to Follow-Up Work

- Exit code documentation in GitHub Wiki
- `flowline status` output format stabilization (related public API surface; track separately)
- AGENTS.md updates when new commands are added

---

## Key Technical Decisions

| Decision | Choice | Rationale |
|---|---|---|
| ExitCode file | `src/Flowline/ExitCode.cs` (standalone) | Clean public API surface; easy to reference from docs and AGENTS.md |
| FlowlineException ExitCode | New constructor overload `(ExitCode, string, Exception?)` | Cleaner than init-only property at 40+ throw sites; chains naturally with existing `.WithDetail()` / `.WithHelpLink()` fluent methods |
| Direct `return 1` paths | Convert to `return (int)ExitCode.X` | Message already printed above the return; conversion is mechanical; no user-facing change |
| AGENTS.md delivery | Inline C# raw string literal in CloneCommand | Needs `SolutionName` substitution; single use case makes embedded resource infrastructure disproportionate |
| Existing `OperationCanceledException` → 130 | No change | Already correct; `Cancelled = 130` in the enum documents the convention |

---

## Implementation Units

### U1. ExitCode enum

**Goal:** Define the stable public `ExitCode` enum that all commands and the global handler will reference.

**Requirements:** R1, R7

**Dependencies:** none

**Files:**
- `src/Flowline/ExitCode.cs` (new)

**Approach:** Public enum in the `Flowline` namespace. Include XML doc comment on the type stating it is a public API surface. Add inline comments on each value explaining when it fires. Leave 2 and 5 unused with brief comments. Values:

```
Success = 0, GeneralError = 1,
// 2 unused — Spectre handles arg validation
NotFound = 3, NotAuthenticated = 4,
// 5 unused — no permissions/forbidden concept
ConnectionFailed = 10, ConfigInvalid = 11,
DirtyWorkingDirectory = 12, BuildFailed = 13,
VersionConflict = 14, ValidationFailed = 15,
Timeout = 16, ForceRequired = 17,
Cancelled = 130
```

*This sketch is directional guidance for review, not implementation specification.*

**Test expectation:** none — pure enum; verified indirectly by U2 and U3.

**Verification:** `dotnet build src/Flowline/Flowline.csproj` succeeds; enum values match the table above.

---

### U2. FlowlineException — ExitCode and Program.cs handler

**Goal:** Thread the typed exit code from exception through to the process exit.

**Requirements:** R2

**Dependencies:** U1

**Files:**
- `src/Flowline/FlowlineException.cs` (modify)
- `src/Flowline/Program.cs` (modify — exception handler only)
- `tests/Flowline.Tests/FlowlineExceptionTests.cs` (new)

**Approach:**
- Add `public ExitCode ExitCode { get; init; } = ExitCode.GeneralError;` property
- Add new constructor `FlowlineException(ExitCode exitCode, string message, Exception? inner = null)` that sets the property and calls the existing base constructor — existing no-code constructor remains intact for backward compatibility
- In `Program.cs` `FlowlineException` case: change `return 1;` to `return (int)fe.ExitCode;`
- `OperationCanceledException` case already returns `130` — no change

**Patterns to follow:** `src/Flowline/FlowlineException.cs` — fluent builder methods return `this`; new constructor must not break chaining (it won't, since `.WithDetail()` and `.WithHelpLink()` operate on the returned instance, not on construction).

**Test scenarios:**
- `new FlowlineException("msg")` → `ExitCode == ExitCode.GeneralError`
- `new FlowlineException(ExitCode.NotAuthenticated, "msg")` → `ExitCode == ExitCode.NotAuthenticated`
- `new FlowlineException(ExitCode.BuildFailed, "msg").WithDetail(d => { })` → `ExitCode == ExitCode.BuildFailed` (fluent chain preserves code)
- `new FlowlineException(ExitCode.ConfigInvalid, "msg").WithHelpLink("url")` → `ExitCode == ExitCode.ConfigInvalid`

**Verification:** Unit tests green; manually confirm that a command throwing a typed exception returns the expected exit code (`$LASTEXITCODE` in PowerShell).

---

### U3. Wire exit codes throughout commands and utilities

**Goal:** Every FlowlineException throw and every direct `return 1` in commands carries the correct typed exit code. Error messages for actionable codes include corrective text (R4).

**Requirements:** R3, R4

**Dependencies:** U1, U2

**Files:**
- `src/Flowline/FlowlineCommand.cs` (modify)
- `src/Flowline/Commands/CloneCommand.cs` (modify)
- `src/Flowline/Commands/SyncCommand.cs` (modify)
- `src/Flowline/Commands/PushCommand.cs` (modify)
- `src/Flowline/Commands/DeployCommand.cs` (modify)
- `src/Flowline/Commands/ProvisionCommand.cs` (modify)
- `src/Flowline/Commands/GenerateCommand.cs` (modify)
- `src/Flowline/Utils/GitUtils.cs` (modify)
- `src/Flowline/Utils/PacUtils.cs` (modify)
- `src/Flowline/Utils/ConsoleHelper.cs` (modify)
- `src/Flowline/Config/ProjectConfig.cs` (modify)

**Approach — two patterns:**

*FlowlineException throw sites*: Swap `throw new FlowlineException("msg")` for `throw new FlowlineException(ExitCode.X, "msg")` using the mapping table below. Touch the message text only when R4 requires it.

*Direct `return 1` paths*: Replace with `return (int)ExitCode.X;`. The error message is already printed above the return — no message text changes needed for these paths. For pack-failed and build-failed checks (`if (await PacUtils.PackSolutionAsync(...) != 0) return 1;`), use `BuildFailed`. For environment-not-found returns, use `NotFound` or `ConnectionFailed` per context.

**Exit code mapping (from origin doc):**

| Condition | Code |
|---|---|
| No PAC profile found | `NotAuthenticated` (4) |
| Environment URL unreachable | `ConnectionFailed` (10) |
| `.flowline` missing or malformed | `ConfigInvalid` (11) |
| Uncommitted changes block sync (`Package/src/`) | `DirtyWorkingDirectory` (12) |
| Clean repo check fails before deploy | `DirtyWorkingDirectory` (12) |
| `dotnet build` or PAC pack non-zero | `BuildFailed` (13) |
| Solution not found in Dataverse or repo | `NotFound` (3) |
| Target environment has newer version | `VersionConflict` (14) |
| Drift detected (local changes not in Dataverse) | `ValidationFailed` (15) |
| Missing dependencies / schema mismatch | `ValidationFailed` (15) |
| PAC CLI timeout (60-min limit) | `Timeout` (16) |
| Non-interactive config overwrite (ProjectConfig, ConsoleHelper) | `ForceRequired` (17) |

**Message updates required for R4:**
- `NotAuthenticated` (4) — update `FlowlineCommand.cs` message from "No PAC profile found — run 'pac auth create' first." to include the environment URL hint: "Not authenticated — run: pac auth create --environment \<url\>"
- `DirtyWorkingDirectory` (12) — verify `SyncCommand.cs` message includes "git commit first"; update if it doesn't
- `VersionConflict` (14) — locate throw site; ensure message includes "Add --force to overwrite"
- `ForceRequired` (17) — update `ConsoleHelper.cs` message to include the specific operation name

**Test scenarios:**
- `flowline sync` with dirty `Package/src/` → exits `12`
- `flowline deploy` on a repo with uncommitted changes → exits `12`
- Any command with no PAC profile → exits `4`
- `flowline deploy test` when drift exists and `--force` not set → exits `15`
- `flowline clone` when PAC pack fails → exits `13`
- `flowline clone` with no unmanaged solution at any provided URL → exits `3`
- `flowline clone` in non-interactive mode on config overwrite without `--force` → exits `17`
- Ctrl+C during any command → exits `130` (unchanged)
- Successful command → exits `0`

**Verification:** Run each scenario and confirm `$LASTEXITCODE` matches expected value. All tests in `tests/Flowline.Tests/` remain green.

---

### U4. AGENTS.md scaffolding in flowline clone

**Goal:** `flowline clone` writes `AGENTS.md` at the repo root with `<SolutionName>` substituted.

**Requirements:** R5

**Dependencies:** none (independent of exit code units; can be done in any order)

**Files:**
- `src/Flowline/Commands/CloneCommand.cs` (modify — add AGENTS.md write step near end of `ExecuteAsync`)

**Approach:** After the solution project structure is scaffolded, write `AGENTS.md` to `Path.Combine(RootFolder, "AGENTS.md")`. If the file already exists, log an info message and skip — do not overwrite (the developer may have customised it). Use a C# raw string literal for the template body; substitute `projectSln.Name` for the solution name placeholder throughout.

Template content is specified verbatim in the origin requirements doc under `## AGENTS.md Template`. The substitution points are:
- Every occurrence of `<SolutionName>` in the project structure section and rules
- The solutions folder path in the project structure tree

**Patterns to follow:** Console output via `Console.Info(...)` / `Console.Ok(...)` matching tone-of-voice rules (`docs/tone-of-voice.md`); file existence check + skip pattern used elsewhere in CloneCommand for idempotent writes.

**Test scenarios:**
- After `flowline clone`, `AGENTS.md` exists at repo root
- `AGENTS.md` contains the actual solution name — no `<SolutionName>` placeholder remains
- `AGENTS.md` contains the exit codes table with all expected codes
- `AGENTS.md` daily dev loop contains `flowline push --dry-run`
- Running `flowline clone` a second time does not overwrite an existing `AGENTS.md` (skip with info message)

**Verification:** Run `flowline clone` against a test environment; inspect `AGENTS.md` at repo root. Re-run; confirm file unchanged.

---

### U5. Help text updates

**Goal:** All seven command descriptions follow the "what + trigger + state change" pattern for accurate agent command selection.

**Requirements:** R6

**Dependencies:** none

**Files:**
- `src/Flowline/Program.cs` (modify — `.WithDescription(...)` strings only)

**Approach:** Replace each `.WithDescription("...")` value with the text below. Touch only the description strings — no changes to examples, command registration, or structure.

| Command | Updated description |
|---|---|
| `clone` | `Initialize a Flowline project from an existing Dataverse solution. Creates folder structure, unpacks solution XML, scaffolds Plugins and WebResources projects, and generates AGENTS.md. One-time setup per solution.` |
| `push` | `Build and register plugin assembly and web resources directly to DEV — skips pack/import. Reads [Step] attributes to create or update plugin registrations. Run after plugin or web resource changes.` |
| `sync` | `Export solution from DEV, bump build version, and unpack to source-controlled XML. Run after testing changes in DEV. Requires no uncommitted changes in Package/src/.` |
| `deploy` | `Pack solution from repo and import into target environment (test, uat, prod, or URL). Requires clean git working directory.` |
| `provision` | `Create a DEV, TEST, or UAT environment by copying from production. Saves environment URL to .flowline. One-time setup for new environments.` |
| `status` | `Show configured environments, connection status, solution version, PAC CLI auth status, and git state. Use to verify setup before running commands.` |
| `generate` | `Generate early-bound C# types from solution entities and custom APIs. Overwrites Plugins/Models/ with generated .cs files. Run after adding or modifying entities or custom APIs.` |

**Test expectation:** none — text-only change.

**Verification:** `flowline --help` and each `flowline <command> --help` show the updated descriptions. Run `/tone` review on changed output to verify tone-of-voice consistency.

---

## Deferred Implementation Notes

- Whether `VersionConflict` (14) has an existing throw site or is only forward-planned — verify during U3; add a throw if the scenario exists but currently falls through to `return 1`
- Constant placement for repeated `ExitCode` values (inline vs. local variables) — implementer's judgment; prefer named locals if the same code appears 3+ times in one method
- AGENTS.md token substitution scope — if future templates also need substitution, consider extending `TemplateWriter` then; not warranted now

---

## System-Wide Impact

- **All commands:** exit code contract changes from binary (0/1/130) to typed. CI pipelines checking `exit code != 0` continue to work. Pipelines checking `exit code == 1` for specific failure types will see new values — this is intentional; document in release notes.
- **`flowline clone`:** produces one additional file (`AGENTS.md`) at repo root. Existing repos with an `AGENTS.md` are unaffected (skip logic in U4).
- **AGENTS.md:** loaded automatically by Claude Code and GitHub Copilot on project open. Content must stay accurate as Flowline commands and behavior evolve.
