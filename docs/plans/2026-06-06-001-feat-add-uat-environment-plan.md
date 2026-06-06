---
title: "feat: Add UAT environment"
type: feat
status: active
created: 2026-06-06
---

# feat: Add UAT environment

Add UAT (User Acceptance Testing) as a first-class environment, sitting between Test and Production in the promotion chain. UAT behaves as a Sandbox (not Production type). Deploy is silent (no confirmation). `provision uat` is supported. `clone` includes UAT in its search order.

## Problem Frame

Flowline currently knows three environments: Dev, Test, Prod. Users want a fourth — UAT — inserted between Test and Prod. Every place that accepts "prod" / "test" as a shorthand must also accept "uat". The config file gains a `UatUrl` field.

## Scope Boundaries

**In scope:**
- `EnvironmentRole.Uat` enum value
- `ProjectConfig.UatUrl` property + `GetOrUpdateUatUrl()` method
- `deploy uat` keyword resolution
- `provision uat` role
- `clone --uat <URL>` option + UAT in search order (Prod → UAT → Test → Dev)
- `status` display of UAT environment (between Production and Test)

**Out of scope:**
- Promotion gating / enforcing deploy order (no existing gating for other envs)
- Any new commands

---

## Key Technical Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Dataverse type for UAT | Sandbox | User confirmed; same guard as Test/Dev — rejects Production type |
| Deploy confirmation | None (silent) | User confirmed; UAT treated as promoted sandbox, not gated like Prod |
| Provision copy type | FullCopy | Mirrors Test behavior; UAT needs realistic data |
| Clone search order | Prod → UAT → Test → Dev | Reflects promotion ladder; UAT has higher fidelity than Test |

---

## Implementation Units

### U1. Add `UatUrl` to `ProjectConfig`

**Goal:** Store and retrieve the UAT URL in `.flowline` config.

**Dependencies:** none

**Files:**
- `src/Flowline/Config/ProjectConfig.cs`
- `tests/Flowline.Tests/ProjectConfigTests.cs`

**Approach:** Add `public string? UatUrl { get; set; }` alongside existing `ProdUrl`/`TestUrl`/`DevUrl`. Add `GetOrUpdateUatUrl()` method following the exact same pattern as `GetOrUpdateTestUrl()` — the three blocks (empty stored, empty input, mismatch) are identical in structure.

**Patterns to follow:** `GetOrUpdateTestUrl` in `src/Flowline/Config/ProjectConfig.cs:24-57`

**Test scenarios:**
- `GetOrUpdateUatUrl(null)` when `UatUrl` is null → returns null, `UatUrl` stays null
- `GetOrUpdateUatUrl("https://x.crm.dynamics.com/")` when `UatUrl` is null → sets and returns the URL
- `GetOrUpdateUatUrl(null)` when `UatUrl` is already set → returns stored URL unchanged
- `GetOrUpdateUatUrl("https://x.crm.dynamics.com/")` when same URL already stored → no conflict, returns URL
- Round-trip: `ProjectConfig` with `UatUrl` set serializes and deserializes via `JsonSerializer` preserving the value

**Verification:** Tests pass; `ProjectConfig` with `UatUrl` round-trips through JSON correctly.

---

### U2. Add `Uat` to `EnvironmentRole` and update base switch expressions

**Goal:** Teach `FlowlineCommand` to resolve label, flag, and URL for the UAT role.

**Dependencies:** U1

**Files:**
- `src/Flowline/Commands/FlowlineCommand.cs`

**Approach:** Add `Uat` to the `EnvironmentRole` enum at line 12. In `GetAndCheckEnvironmentInfoAsync`, extend all three switch expressions:
- label: `EnvironmentRole.Uat => "UAT"`
- flag: `EnvironmentRole.Uat => "--uat"`
- URL: `EnvironmentRole.Uat => Config!.GetOrUpdateUatUrl(inputUrl, settings)`

No type-guard changes needed — the existing check (`role != Prod && type == Production → reject`) already covers UAT correctly since UAT is a Sandbox.

**Patterns to follow:** Existing arms in `GetAndCheckEnvironmentInfoAsync` switches (`src/Flowline/Commands/FlowlineCommand.cs:73-94`)

**Test scenarios:**
- Test expectation: none — pure switch extension; integration-level behavior is covered by U3–U5 tests

**Verification:** Project compiles with no `ArgumentOutOfRangeException` on `EnvironmentRole.Uat`.

---

### U3. Add `uat` keyword to `DeployCommand`

**Goal:** `flowline deploy uat` resolves to `Config.UatUrl`.

**Dependencies:** U1, U2

**Files:**
- `src/Flowline/Commands/DeployCommand.cs`

**Approach:** Extend the `targetUrl` switch at line 34:
```
"uat"  => Config!.UatUrl,
"prod" => Config!.ProdUrl,
"test" => Config!.TestUrl,
_ => settings.Target
```
Update `[Description]` on `<target>` to `"Target environment: prod, uat, test, or a URL"`.

No confirmation prompt — UAT is silent like Test.

**Patterns to follow:** Existing `"prod"` and `"test"` cases in `DeployCommand.cs:34-39`

**Test scenarios:**
- Test expectation: none — no isolated unit-testable logic; this is a keyword-to-URL mapping in an integration command. The existing pattern for `"prod"` and `"test"` has no unit tests either.

**Verification:** Manual smoke test: `flowline deploy uat` with `UatUrl` set resolves correctly; `flowline deploy uat` with no `UatUrl` in config prints the "Can't resolve" error.

---

### U4. Add `Uat` to `ProvisionCommand`

**Goal:** `flowline provision uat` creates/copies a UAT sandbox from prod.

**Dependencies:** U1, U2

**Files:**
- `src/Flowline/Commands/ProvisionCommand.cs`

**Approach:**
1. Add `Uat` to the `Role` enum (line 11): `public enum Role { Dev, Test, Uat }`
2. Update `[Description]` on `[role]` to `"Target role: dev, test, or uat"`
3. Update `suffix` determination (line 48-50):
   - `Role.Dev → "Dev"`, `Role.Uat → "UAT"`, `Role.Test → "Test"` (use pattern matching switch)
4. Add `Role.Uat => Config!.GetOrUpdateUatUrl(targetUrl, settings)` to the `url` switch (line 59-64)
5. Update `copyType` logic (line 139) — UAT gets FullCopy like Test:
   - `(settings.Role is Role.Test or Role.Uat || settings.CopyType == CopyType.Full)`

**Patterns to follow:** `Role.Test` handling throughout `ProvisionCommand.cs`

**Test scenarios:**
- `FindProblematicSolutions` is unaffected by this change — no new tests needed there
- `suffix` for `Role.Uat` is `"UAT"` (not `"Test"`)
- `copyType` for `Role.Uat` is `"FullCopy"` (not `"MinimalCopy"`)

**Verification:** `flowline provision uat` builds target URL with `-uat` suffix, uses FullCopy.

---

### U5. Add `--uat` option to `CloneCommand` and include UAT in search order

**Goal:** `flowline clone <solution> --uat <URL>` accepted; clone searches Prod → UAT → Test → Dev.

**Dependencies:** U1, U2

**Files:**
- `src/Flowline/Commands/CloneCommand.cs`

**Approach:**
1. Add `[CommandOption("--uat <URL>")] public string? UatUrl { get; set; }` to `Settings`, with `[Description]` matching the pattern of `--test` and `--dev`.
2. In `ExecuteFlowlineAsync`, add `Config!.GetOrUpdateUatUrl(settings.UatUrl, settings);` after the existing three URL-save calls.
3. In `FindUnmanagedSourceAsync`, extend the `foreach` array to `{ EnvironmentRole.Prod, EnvironmentRole.Uat, EnvironmentRole.Test, EnvironmentRole.Dev }`.
4. Extend the `configUrl` switch to include `EnvironmentRole.Uat => Config!.UatUrl`.
5. Extend the `label` switch to include `EnvironmentRole.Uat => "UAT"`.
6. Update the `FlowlineException` message to include `--uat`.

**Patterns to follow:** `--prod`, `--test`, `--dev` options in `CloneCommand.Settings`; `FindUnmanagedSourceAsync` loop in `CloneCommand.cs:85-111`

**Test scenarios:**
- Test expectation: none — `FindUnmanagedSourceAsync` is private and relies on network calls; integration covered by the full command. No existing unit tests for this method.

**Verification:** `--uat` option appears in `flowline clone --help`; UAT is skipped cleanly when `UatUrl` is not configured.

---

### U6. Add UAT to `StatusCommand` display

**Goal:** `flowline status` shows UAT environment between Production and Test.

**Dependencies:** U1

**Files:**
- `src/Flowline/Commands/StatusCommand.cs`

**Approach:** Add `("UAT", config.UatUrl)` entry to the `envs` array between "Production" and "Test" (lines 49-54).

**Patterns to follow:** Existing entries in the `envs` array in `StatusCommand.cs:49-54`

**Test scenarios:**
- Test expectation: none — `StatusCommand` has no unit tests; display logic is integration-level.

**Verification:** `flowline status` output lists Production, UAT, Test, Development in order; UAT shows "Not configured" when `UatUrl` is absent.

---

## Deferred

Nothing deferred. All changes are self-contained.
