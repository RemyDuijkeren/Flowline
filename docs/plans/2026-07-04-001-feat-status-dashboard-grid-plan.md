---
title: Status Dashboard Grid - Plan
type: feat
date: 2026-07-04
topic: status-dashboard-grid
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
---

## Goal Capsule

- **Objective:** Pivot `flowline status`'s environment/solution rendering from nested per-env blocks to a single grid, with a new Local column showing the version currently checked out on disk.
- **Product authority:** This document.
- **Open blockers:** None.

## Product Contract

**Product Contract preservation:** unchanged from the brainstorm — Requirements, Key Decisions, and Scope Boundaries carry forward verbatim. Outstanding Questions' zero-solutions item is resolved below (KTD6); the terminal-wrapping item remains deferred to implementation.

### Summary

`flowline status` renders solution versions across environments as a grid instead of nested per-environment blocks: one row per solution, one column per environment, ordered Local → Dev → Test → UAT → Prod. The version data the command already computes is unchanged; this is a rendering pivot plus one new offline read of the locally checked-out solution version.

### Key Decisions

- **Rendering pivot on the existing command, not a new command.** `status` already mixes machine-setup checks (SDK/PAC/Git versions) with the environment/solution matrix in one command (`StatusCommand.cs:30-127`) — the grid replaces the nested render at `StatusCommand.cs:105-127` in place, no new command name.
- **Column order changes to DTAP flow.** Today's order is Production → UAT → Test → Development (`StatusCommand.cs:54-60`). The grid switches to Local → Dev → Test → UAT → Prod, matching promotion direction, left to right. This is a visible behavior change from today's output, not just an added column.
- **Local column is always shown.** `clone` always seeds a local Dev checkout, so a local version is the normal case, not the exception. The column is never conditionally hidden; a dash appears per-row only for the rare solution that hasn't been cloned yet (`ReadLocalSolutionVersion` throws today when `Solution.xml` is absent — `DeployCommand.cs:332-348`).
- **Unconfigured environments drop the column.** An environment with no URL in `.flowline` (e.g. no `UatUrl`) omits that column entirely from the grid — matches today's skip behavior (`StatusCommand.cs:107-111`), just applied at the column level instead of per env-block.
- **Authentication failure gets a distinct cell marker.** Today, an unauthenticated environment prints "✗ Not authenticated" and silently produces zero solution rows for that env (`StatusCommand.cs:115-118`, `versions` dict never populated). In the grid, every solution gets a row regardless of column, so an authenticated-but-failed environment must render a marker distinct from "not deployed" — otherwise a real auth problem looks identical to a solution simply absent from that environment.

### Requirements

- R1. `flowline status` renders one `Solution × Environment` grid: rows are solutions, columns are Local, Dev, Test, UAT, Prod in that order.
- R2. The Local column shows the version parsed from the solution's own local `Package/src/Other/Solution.xml`.
- R3. A version cell shows the version string when known.
- R4. A cell shows a dash when the solution has no local checkout yet (Local column) or is not deployed to that environment (env columns).
- R5. A cell shows a distinct marker (not the same as R4's dash) when the environment is configured but authentication to it fails.
- R6. An environment with no URL configured in `.flowline` is omitted from the grid as a column, not rendered as an empty column.
- R7. The tool-version and authentication check output that precedes the grid today (`.NET SDK`, `Power Platform CLI`, `Git` versions) is unchanged by this work.
- R8. When zero solutions are configured, the command prints a plain note instead of an empty grid.

### Scope Boundaries

Deferred — not covered by this document, each is a distinct piece of future work with its own scoping needs:

- `--json` structured output for the status/dashboard data.
- Import health badges sourced from `msdyn_solutionhistories` (requires a new per-environment Dataverse SDK auth path `status` doesn't have today — deliberately kept out of this pass so it gets its own scoping and risk assessment).
- Version-skew anomaly detection across the DTAP chain.
- Exception-first collapse (hiding nominal solutions by default).
- Interactive promote-from-dashboard action.

### Outstanding Questions

**Deferred to Implementation:**

- How Spectre.Console's `Table` wraps or truncates long solution names in narrow terminals — left to the rendering implementation; no product decision needed.

### Sources / Research

- `src/Flowline/Commands/StatusCommand.cs:54-127` — current nested render, per-env `Dictionary<string, string?> Versions`, auth/config skip behavior.
- `src/Flowline/Commands/DeployCommand.cs:332-348` — `ReadLocalSolutionVersion()`, existing offline parse of `Package/src/Other/Solution.xml`.
- `src/Flowline/Commands/DeployCommand.cs:61` — per-solution folder path (`solutions/<SolutionName>`), confirming Local version reads are per-solution, not per-project.
- `src/Flowline/Commands/FlowlineCommand.cs:28` — `PackageFolder(slnFolder)`, public static, shared across commands.
- `src/Flowline/Config/ProjectConfig.cs:13-17` — `ProdUrl` / `UatUrl` / `TestUrl` / `DevUrl` and `Solutions` collection.
- `src/Flowline/Utils/PacUtils.cs:442-467` — `GetEnvWhoAsync`, returns `null` both when unauthenticated and (by caller convention) when no URL was configured — the two cases are already distinguished by whether `Url` was empty before calling it.
- `src/Flowline.Core/LoggingRenderHook.cs:17` — the structured-logging render hook already handles `Table` renderables, not just `Markup`.
- `src/Flowline.Core/FlowlineConsoleExtensions.cs:7-12` — existing prefix/color conventions (`✓`, `↷`, `Warning:`, `Error:`) to match for the auth-failure marker.
- `tests/Flowline.Tests/DeployCommandDtapGateTests.cs` — precedent for testing an internal static command method directly with `FluentAssertions`, without spinning up the full command.
- `docs/ideation/2026-07-03-flowline-dashboard-ideation.html` — ideas 1 (Solutions × Environments Grid) and 2 (Local-as-First-Column), including rejected/deferred alternatives on the same axes.

---

## Planning Contract

### Key Technical Decisions

- **KTD1 — Extract pure, testable cell-classification logic.** Follow the `ResolveDtapGate` / `ReadLocalSolutionVersion` precedent (`DeployCommand.cs:284-348`, tested directly in `DeployCommandDtapGateTests.cs`): add internal static methods on `StatusCommand` for building grid rows and rendering the table, rather than inlining `Table` construction into `ExecuteAsync`. Keeps cell-state logic unit-testable without a live PAC CLI.
- **KTD2 — No new data plumbing for auth-failure detection.** `StatusCommand`'s existing `results` tuple already distinguishes "no URL configured" (never calls `GetEnvWhoAsync`) from "URL configured, `Who` is null" (auth failed) — see `StatusCommand.cs:73` and the render branches at `:107` and `:115`. The grid only needs to branch on this existing distinction; no new environment probing.
- **KTD3 — Auth-failure marker reuses existing convention.** Render the same `✗` glyph in yellow that today's "✗ Not authenticated" line uses (`StatusCommand.cs:118`, `FlowlineConsoleExtensions.WarningPrefix`-style coloring), instead of introducing new iconography. Keeps the CLI's visual vocabulary consistent.
- **KTD4 — Reuse `ReadLocalSolutionVersion` for the Local column.** Call `ReadLocalSolutionVersion(FlowlineCommand<Settings>.PackageFolder(Path.Combine(rootFolder, "solutions", solutionName)))` per solution; catch `FlowlineException` and treat it as "not cloned yet" (dash). No new Solution.xml parsing.
- **KTD5 — Grid output is already captured by structured logging.** `LoggingRenderHook` already matches `Table` renderables (`LoggingRenderHook.cs:17`), so the grid is logged at `Debug` level automatically (no line matches the `Information`/`Warning`/`Error` prefixes). No new logging wiring is needed.
- **KTD6 — Zero-solutions message.** When `config.Solutions` is empty, print a plain line (e.g. via the existing `Info`/`Warning` console extension conventions) instead of rendering an empty `Table`. Resolves R8 and the origin document's deferred zero-solutions question.

### High-Level Technical Design

Cell content is fully determined by column type and, for env columns, three data states already present in `results`. No new states are introduced — the design work is exhaustively enumerating the existing ones per cell:

| Column | Condition | Cell content |
|---|---|---|
| Local | Solution has a local `Solution.xml` | version string |
| Local | Solution not cloned yet (`ReadLocalSolutionVersion` throws `FlowlineException`) | dash |
| Env (Dev/Test/UAT/Prod) | `Url` empty | column omitted entirely |
| Env (Dev/Test/UAT/Prod) | `Url` set, `Who` non-null, solution present in `Versions` | version string |
| Env (Dev/Test/UAT/Prod) | `Url` set, `Who` non-null, solution absent from `Versions` | dash |
| Env (Dev/Test/UAT/Prod) | `Url` set, `Who` null (auth failed) | `✗` marker (yellow), every row in that column |

### Assumptions

- Long solution names in narrow terminals rely on Spectre.Console's default `Table` wrapping/truncation behavior; no custom column-width handling is added (see Outstanding Questions).

---

## Implementation Units

### U1. Grid cell classification (pure, testable)

- **Goal:** Build per-solution, per-column cell values (version / dash / auth-failure) from data `StatusCommand` already computes, plus a per-solution Local version lookup.
- **Requirements:** R2, R3, R4, R5, R6, R8
- **Dependencies:** none
- **Files:** `src/Flowline/Commands/StatusCommand.cs`, `tests/Flowline.Tests/StatusCommandGridTests.cs` (new)
- **Approach:** Add an internal cell type (e.g. a small record distinguishing Version / Dash / AuthFailed) and an internal static method that takes the solution list, the existing per-env `results` tuple array, and a local-version-reader delegate (injected for testability, wrapping `ReadLocalSolutionVersion(FlowlineCommand<Settings>.PackageFolder(...))`), and returns per-solution rows with columns pre-filtered to configured environments, Local first, Dev→Test→UAT→Prod after.
- **Patterns to follow:** `DeployCommand.ResolveDtapGate` / `ReadLocalSolutionVersion` — internal static, tested directly (see `DeployCommandDtapGateTests.cs`).
- **Test scenarios:**
  - Happy path: solution with a version in every env and a local checkout → all five cells hold version strings.
  - Edge: local-version delegate throws `FlowlineException` → Local cell is a dash.
  - Edge: env configured (`Url` set) but solution absent from that env's `Versions` dict → dash.
  - Error path: env configured, `Who` null (auth failed) → every solution's cell in that column is the auth-failure marker.
  - Edge: env not configured (`Url` empty) → that env is absent from the returned column set entirely.
  - Edge: zero solutions passed in → empty row set (feeds U3's message decision).
- **Verification:** unit tests pass for every scenario above.

### U2. Grid table renderer

- **Goal:** Render U1's rows into a `Spectre.Console.Table` with headers Local, Dev, Test, UAT, Prod (only for columns U1 kept), version cells plain, dash cells dim, auth-failure cells styled like today's `✗ Not authenticated` line.
- **Requirements:** R1, R6, R7
- **Dependencies:** U1
- **Files:** `src/Flowline/Commands/StatusCommand.cs`, `tests/Flowline.Tests/StatusCommandGridTests.cs`
- **Approach:** internal static method taking `IAnsiConsole` and U1's rows, building the `Table` via `AddColumn`/`AddRow`; cell markup mirrors `FlowlineConsoleExtensions` styling conventions.
- **Patterns to follow:** `FlowlineConsoleExtensions.cs` prefix/color conventions; `Spectre.Console.Testing.TestConsole` for asserting rendered output (see `LoggingRenderHookTests.cs`).
- **Test scenarios:**
  - Happy path: given a mix of version/dash/auth-failure cells, the rendered table has headers in Local→Dev→Test→UAT→Prod order and correct cell text.
  - Edge: a column U1 excluded does not appear in the rendered table.
- **Verification:** `TestConsole`-based assertions on rendered header order and cell text.

### U3. Wire into `StatusCommand.ExecuteAsync`

- **Goal:** Replace the nested render block (`StatusCommand.cs:105-127`) with the reordered `envs` array and Local-version lookups, then call U1 and U2; print the zero-solutions message when `config.Solutions` is empty.
- **Requirements:** R1, R2, R6, R7, R8
- **Dependencies:** U1, U2
- **Files:** `src/Flowline/Commands/StatusCommand.cs`
- **Approach:** reorder the `envs` array (`StatusCommand.cs:54-60`) to Dev→Test→UAT→Prod; after the existing `results` computation, resolve each solution's Local version via the U1 delegate; branch on `config.Solutions.Count == 0` to print the plain message instead of calling U1/U2.
- **Test expectation:** none -- covered by U1/U2 unit tests; the full `ExecuteAsync` flow needs a live, authenticated PAC CLI session, matching `StatusCommand`'s current lack of direct integration tests.
- **Verification:** `dotnet build` succeeds; a manual `flowline status` run inside a cloned Flowline project shows the grid with correct column order, dash, and auth-failure states.

---

## Verification Contract

| Command | Applies to | Done signal |
|---|---|---|
| `dotnet build` | All units | Solution builds with no errors or new warnings |
| `dotnet test tests/Flowline.Tests/StatusCommandGridTests.cs` | U1, U2 | All cell-classification and rendering scenarios pass |
| Manual: `flowline status` in a cloned project | U3 | Grid renders Local→Dev→Test→UAT→Prod, dash and auth-failure states match the Planning Contract's cell table |

## Definition of Done

- U1, U2, U3 complete; `dotnet build` and `dotnet test tests/Flowline.Tests/StatusCommandGridTests.cs` pass.
- The nested per-env render block that `StatusCommand.cs:105-127` used before this work is fully replaced, not left dead alongside the grid.
- Manual verification confirms: column order Local→Dev→Test→UAT→Prod; unconfigured envs omitted; auth failures marked distinctly from "not deployed"; zero solutions shows a message instead of an empty table.
