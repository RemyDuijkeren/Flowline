---
title: Settings Verbs Named for What They Do - Plan
type: feat
date: 2026-10-31
artifact_contract: ce-unified-plan/v1
product_contract_source: ce-plan-bootstrap
execution: code
---

# Settings Verbs Named for What They Do - Plan

## Goal Capsule

- **Objective:** Someone driving `flowline settings` from a script can trust the outcome. Each verb says what it does, and a write that names no component fails loudly instead of exiting 0 having changed nothing. A swapped environment and component name gets a correction, not advice to authenticate against a component name.
- **Means:** Rename `settings push`/`pull` to `apply`/`capture` with no aliases (R1, R2). Add two input checks that run before any auth or network call: one for the target on all four verbs (KTD3), one for the name on component writes (KTD2).
- **Authority:** Idea 5 in `docs/ideation/2026-10-31-cli-surface-consistency-and-boundary-ideation.html` owns the decision and rationale; do not reopen it. KD5 forbids transition aliases. `.claude/skills/cli-for-agents/SKILL.md` governs exit codes, help text and examples. `docs/tone-of-voice.md` governs every message. `../Flowline.wiki/AGENTS.md` governs the wiki edits.
- **Stop conditions:** Stop and ask if Spectre's command context does not expose the raw invocation arguments (KTD4 depends on it). Also stop if the name check cannot run before the Dataverse connection without moving project or solution resolution, or if a test proves an existing interactive flow (picker then write, read then prompt) changes behaviour.
- **Execution profile:** One breaking release, shipped with the `state`/`value` rename (to `toggle`/`set`) already under CHANGELOG Unreleased > Breaking. One branch with a worktree in this repo (AGENTS.md, Branching: multi-commit work), one commit per unit. The wiki changes land as a separate commit in `../Flowline.wiki`.
- **Finishes the work:** `ce-work` or a human, in unit order.

---

## Product Contract

### Summary

`settings push` becomes `settings apply` and `settings pull` becomes `settings capture`, with arguments and flags unchanged and no aliases. `settings toggle` and `settings set` keep the optional `[name]` positional, but a non-interactive write with no name now exits 15 and prints the caller's own command with `<name>` filled in. A target that is neither a role nor an http(s) URL exits 15 before any auth call. When the two positionals are swapped, the message gives the corrected line.

### Problem Frame

`push` and `pull` mean different things at the root and under `settings`. Root `pull` is DEV-only, needs a clean tree and bumps the version. `settings pull` has none of those constraints, yet it shares the word. Help text already describes the commands as "Apply a settings file" and "Environment to capture" (`src/Flowline/Program.cs:518`, `src/Flowline/Commands/SettingsPullCommand.cs:31`).

The bigger defect is silent. `settings toggle prod --off` in a script reaches the no-name branch, lists the components and returns success (`src/Flowline/Commands/SettingsComponentCommands.cs:142-149`). The script believes a flow is off and nothing changed.

The ideation card flagged a swapped order (`settings toggle ApprovalFlow prod`) as "should fail, unverified". Tracing the code shows it fails, but not clearly. The settings role check only rejects a role used outside a project (`src/Flowline/Commands/SettingsCommandBase.cs:66-74`). Any other token is treated as a URL and handed to PAC profile resolution. With no universal profile active, that exits 4 printing `No PAC auth profile found for ApprovalFlow` and `Run: pac auth create --environment ApprovalFlow --name "..."` (`src/Flowline.Core/Environments/ProfileResolutionService.cs:254-260`). With a universal profile active, the lookup matches it (`src/Flowline.Core/Dataverse/DataverseConnector.cs:671-673`). The run then goes on to an environment lookup against `ApprovalFlow`. Either way the user is pointed at authentication.

### Requirements

**Verb names**

- R1. `settings apply <target>` and `settings capture [target]` replace `settings push` and `settings pull`, with every argument, flag and behaviour unchanged.
- R2. `settings push` and `settings pull` are no longer commands. They fail with Spectre's own `Unknown command` error and exit 15. No alias and no redirect hint.
- R3. Every user-facing string that names a settings verb names the new one: help, examples, error and warning text, finish lines, docs.

**Component name on writes**

- R4. `settings toggle` and `settings set` keep the component name as the optional second positional `[name]`.
- R5. In a non-interactive run, `--on`, `--off` or `--value` with no name exits 15 before any Dataverse connection. The message names the positional and shows the caller's command with `<name>` inserted after the target, other flags kept, and any `--value` shown as `<value>`. A `--dry-run` write follows the same rule.
- R6. At a terminal, a write with no name still runs the picker and then writes the picked component.
- R7. A read with no name is unchanged: it lists in a non-interactive run and picks at a terminal.

**Target validation**

- R8. On all four settings verbs, a target that is neither a role (`dev`, `test`, `uat`, `prod`) nor an absolute http(s) URL exits 15 before any PAC profile lookup or network call. A bare host without a scheme is rejected the same way `--env` rejects it.
- R9. On `toggle` and `set`, when the target fails R8 and the name is a role or an http(s) URL, the message says the environment comes first and gives the swapped line to run. Otherwise it gives the generic R8 message.

### Key Decisions

- **`apply`/`capture`, positional name, writes require it.** (session-settled: user-directed, chosen over top-level commands, keeping push/pull, and a `--component` flag: recorded in Idea 5.) Governs R1, R4, R5.
- **No old-verb hint.** (session-settled: user-directed, chosen over mapping `push`/`pull` (and `state`/`value`) to their new names in the existing settings argument hint: "no need for it".) Governs R2.
- **A dry-run write with no name fails like a real write.** (session-settled: user-approved, chosen over listing like a read: the preview must fail where the real run fails.) Governs R5.
- **Echo the caller's flags, never a value.** (session-settled: user-approved, chosen over a minimal suggestion that drops `--type`/`--dry-run`, and over echoing the real `--value`, which could reprint a secret into CI logs.) Governs R5, R9.
- **Detect the swap.** (session-settled: user-approved, chosen over one generic message for every bad target.) Governs R9.
- **Reject a bare host.** (session-settled: user-approved, chosen over prefixing `https://`: keeps one definition of a URL, the one `--env` uses.) Governs R8.

### Scope Boundaries

- Root `push`, `pull`, `deploy` and `drift` are untouched. `deploy` and `drift` take a positional target through their own path and may share the bare-word problem. Considered and not built here: no report of it, and it is a different command family.
- Dated records stay as written: `docs/reviews/`, `docs/solutions/`, `docs/brainstorms/`, older plans, and released CHANGELOG sections.
- No transition alias, no deprecation period, no redirect hint for the old verbs (Key Decisions).
- The solution-name plan (`docs/plans/2026-10-31-1223-feat-solution-name-flag-plan.md`) is independent. Neither waits on the other.

---

## Planning Contract

### Key Technical Decisions

- KTD1. **Rename the classes and files with the verbs.** `src/Flowline/Commands/SettingsPushCommand.cs` becomes `SettingsApplyCommand.cs` and `SettingsPullCommand.cs` becomes `SettingsCaptureCommand.cs`, class names included, so code and CLI use one vocabulary. Compiler-checked. (session-settled: user-approved, chosen over keeping the internal names.)
- KTD2. **The name check sits in the shared component command flow, after the target check and before environment resolution.** The shared flow already rejects contradictory flags before anything else runs (`src/Flowline/Commands/SettingsComponentCommands.cs:95-99`). The name check goes after project mode, the stand-alone solution check and the target check (KTD3), and before environment and profile resolution. Everything before that point is local, so the script fails without a Dataverse round trip. The write intent already comes from each command's own on/off/value test, so the check needs no per-command code. Interactivity uses the same capability test as every other prompt guard.
- KTD3. **The target check lives in the one settings role check all four verbs already call.** Apply, capture (per target, including each target of a sweep) and both component verbs route through it (`src/Flowline/Commands/SettingsCommandBase.cs:66-74`; callers at `SettingsPushCommand.cs:38`, `SettingsPullCommand.cs:223`, `SettingsComponentCommands.cs:108`). Fixing it there fixes every caller. The URL rule copies the shared environment resolver: absolute, scheme http or https (`src/Flowline.Core/Environments/EnvironmentTargetResolver.cs:57-62`). Exit 15 matches that resolver's answer to the same input. The target check runs before the name check, because `settings toggle ApprovalFlow --off` is a bad target first. Asking for a name there would suggest `settings toggle ApprovalFlow <name> --off`.
- KTD4. **Suggestions are rebuilt from the raw invocation, not from parsed settings.** Rebuilding from the caller's own tokens keeps every flag they passed, including global ones, in their order. The token quoting is reused from the existing settings argument hint (`src/Flowline/Commands/SettingsSupport.cs:241`), but its position logic is not: that hint reads fixed indexes on a parse failure, while here a value-taking option can precede the positionals (`settings toggle --type flow prod --off`). The builders find the target token, and for the swap the name token, by matching the parsed target and name values among the tokens after the verb, then insert `<name>` or swap the two. Any `--value` argument becomes `<value>` in both the `--value x` and `--value=x` forms. The message builders are pure functions in `SettingsSupport.cs`, so the wording is testable without a console or a connection.
- KTD5. **Exit 15 (ValidationFailed) for both new failures.** It is what `settings pull` already returns when a script names no environment (`src/Flowline/Commands/SettingsPullCommand.cs:145-148`). The shared resolver uses the same code for a target that is neither role nor URL. The ExitCode doc comment and the wiki exit-code tables gain the missing-input case, so the documented contract matches.
- KTD6. **Wording follows the tone guide's error shape: direct, then what to run.** Directional drafts; the `/tone` pass (or a direct review against `docs/tone-of-voice.md`) settles the final text:
  - Missing name: `Name the flow to turn off: 'flowline settings toggle prod <name> --type flow --off'.` The noun comes from the kinds in play (`component` across several, the singular kind name under `--type`). The verb phrase is `turn on`, `turn off` or `set`.
  - Swapped: `'ApprovalFlow' isn't an environment. The environment comes first: 'flowline settings toggle prod ApprovalFlow --off'.`
  - Generic bad target: `'ApprovalFlow' isn't a role (dev, test, uat, prod) or a URL. Pass one of those as the environment, with the full https:// address for a URL.`

### Assumptions

- Spectre 0.57.2's command context exposes the raw arguments the app was run with. Tests already build a context from an argument list (`tests/Flowline.Tests/UpdateNoticeWiringTests.cs:58`). If the full invocation is not there, stop (Goal Capsule).

### System-Wide Impact

- **Telemetry:** the recorded command name comes from the invoked name (`src/Flowline/Commands/FlowlineCommand.cs:118`). Usage series for `push`/`pull` under settings end and `apply`/`capture` start, with no code change.
- **Agents and scripts:** exit codes are stable public API. Three changes reach them: two verbs disappear, a scripted write with no name moves from 0 to 15, and a bad target moves from a PAC profile failure (usually exit 4) to 15. CHANGELOG classifies these (U4).
- **Plugin skills:** `plugin/skills/flowline/SKILL.md` is what installed agents read. Older plugin installs keep the old verbs until updated.

### Risks & Dependencies

- **Overlap with the solution-name plan.** It adds `-n` to the same settings commands and edits the same help strings, README row and wiki tables. Whichever lands second rebases those lines. No functional dependency.
- **The cwd-based project check in tests.** Component verbs detect project versus stand-alone from the working directory (`src/Flowline/Commands/SettingsSupport.cs:65-66`). End-to-end tests of the check ordering must control that, or cover ordering with pure tests plus the Release smoke run in the Verification Contract.

---

## Implementation Units

### U1. Rename the verbs

**Goal:** `settings apply` and `settings capture` exist, `settings push` and `settings pull` do not, and every string in the code names the new verbs.

**Requirements:** R1, R2, R3; KTD1.

**Dependencies:** none.

**Files:**
- `src/Flowline/Program.cs` (settings branch registration, examples, nearby comments)
- `src/Flowline/Commands/SettingsPushCommand.cs` renamed to `src/Flowline/Commands/SettingsApplyCommand.cs`
- `src/Flowline/Commands/SettingsPullCommand.cs` renamed to `src/Flowline/Commands/SettingsCaptureCommand.cs`
- `src/Flowline/Commands/SettingsSupport.cs` (operation list used by the argument hint, messages at :390-392)
- `src/Flowline/Commands/SettingsComponentCommands.cs` (messages at :202, :338, :369, :376, :434)
- `src/Flowline.Core/Configure/SettingsFileReader.cs` (messages at :33, :140)
- `src/Flowline.Core/Configure/ComponentValueWriter.cs` (messages at :49, :116: "still holds the pull placeholder")
- `src/Flowline.Core/Configure/ConfigureApplyService.cs` (doc comment at :186)
- `src/Flowline.Core/ExitCode.cs` (PartialSuccess doc comment at :54)
- `tests/Flowline.Tests/ProgramParsingTests.cs`
- `tests/Flowline.Tests/FlowlineSettingsTests.cs` (type references at :113-114)
- `tests/Flowline.Tests/SettingsCommandTests.cs` (:275, :487, :517)
- `tests/Flowline.Tests/SettingsCommandSurfaceTests.cs` (:594)

**Approach:**
1. Rename the two files and classes (KTD1). Update registration to `apply` and `capture`, keeping descriptions and the order apply, capture, toggle, set.
2. Change the examples to `apply`/`capture`, one argument per string.
3. Change the operation list behind the settings argument hint to `apply`, `capture`, `toggle`, `set`. That list feeds the "Operations are ..." line, which then names the new verbs.
4. Replace every `'flowline settings push'`/`'flowline settings pull'` in messages. The toggle/set finish line "still decides this on the next push" becomes "on the next apply", and "still holds the pull placeholder" becomes "the capture placeholder". Update doc comments that name the commands. Core class names and comments that use "pull" as prose for the capture operation stay; they are not user-facing.

**Patterns to follow:** `Sync_IsNotACommand` in `tests/Flowline.Tests/ProgramParsingTests.cs` for proving a removed command fails through the real configuration.

**Test scenarios:**
- `settings push test` through the real configuration throws a parse error matching `Unknown command 'push'`.
- `settings pull test` throws a parse error matching `Unknown command 'pull'`.
- `settings --help` lists `apply` and `capture`, and lists neither `push` nor `pull`.
- `settings apply --help` and `settings capture --help` render their examples with the new verb.
- The argument hint for `settings dev` names `apply`, `capture`, `toggle`, `set`.
- The argument hint returns nothing for `settings apply prod` (replaces the existing `push` row).
- Existing message assertions expect `flowline settings capture` and `settings capture <target>`.

**Verification:** No `settings push`/`settings pull` string remains under `src/` or `tests/` except the two parse-failure tests, and no user-facing message string under `src/` uses bare "push" or "pull" for a settings operation. The full `tests/Flowline.Tests` project passes.

### U2. Reject a target that is neither a role nor a URL

**Goal:** A bad target on any settings verb exits 15 before any auth or network call. A swapped order on `toggle`/`set` gets the corrected line.

**Requirements:** R8, R9; KTD3, KTD4, KTD5, KTD6.

**Dependencies:** U1 (file names).

**Files:**
- `src/Flowline/Commands/SettingsCommandBase.cs` (shared role check)
- `src/Flowline/Commands/SettingsComponentCommands.cs` (passes the name and the raw invocation so the swap can be detected)
- `src/Flowline/Commands/SettingsSupport.cs` (pure message builders)
- `tests/Flowline.Tests/SettingsCommandTests.cs`

**Approach:**
1. In the shared settings role check, after the existing stand-alone role rule, reject a token that is neither a role nor an absolute http/https URL (KTD3).
2. Component verbs supply the name and raw arguments. When the name passes the role-or-URL test, the message carries the swapped line (KTD4). Apply and capture get the generic message.
3. The swap suggestion obeys the same `--value` placeholder rule as U3.

**Patterns to follow:** the neither-role-nor-URL check in `src/Flowline.Core/Environments/EnvironmentTargetResolver.cs:57-62`; the token rebuild and quoting in the settings argument hint.

**Test scenarios:**
- Target `ApprovalFlow` with name `prod` on toggle: message says the environment comes first and contains `flowline settings toggle prod ApprovalFlow --off`.
- Target `ApprovalFlow` with name `https://contoso.crm4.dynamics.com/` on set: the swapped line puts the URL first.
- Swapped set with `--value secret123`: the suggestion shows `--value <value>` and does not contain `secret123`.
- Swapped set with `--value=secret123`: same placeholder, value absent.
- Target `ApprovalFlow` with name `Nightly reconciliation`: generic message, no swap suggestion.
- A component name with spaces in the swapped line stays quoted.
- `toggle --type flow ApprovalFlow prod --off` (option before the positionals): swapped line is `flowline settings toggle --type flow prod ApprovalFlow --off`.
- Target `contoso-test.crm4.dynamics.com` (no scheme): generic message, exit 15.
- Target `ftp://contoso`: rejected.
- Targets `prod`, `PROD` and `https://contoso.crm4.dynamics.com` pass the check.
- Role target in stand-alone mode still gets the existing stand-alone role error (exit 11), not the new one.
- Apply with target `ApprovalFlow` gets the generic message with exit 15.

**Verification:** The Release smoke run in the Verification Contract prints the swapped line for `settings toggle ApprovalFlow <url> --off` and exits 15 with no PAC profile message.

### U3. Require the name for a scripted write

**Goal:** A non-interactive `toggle`/`set` write with no name exits 15 with a runnable suggestion. Interactive and read behaviour stay as they are.

**Requirements:** R4, R5, R6, R7; KTD2, KTD4, KTD5, KTD6.

**Dependencies:** U2 (target check runs first, KTD3).

**Files:**
- `src/Flowline/Commands/SettingsComponentCommands.cs` (shared flow; `[name]` description)
- `src/Flowline/Commands/SettingsSupport.cs` (pure message builder)
- `src/Flowline/Program.cs` (settings branch description)
- `src/Flowline.Core/ExitCode.cs` (ValidationFailed doc comment gains missing required input)
- `tests/Flowline.Tests/SettingsCommandTests.cs`
- `tests/Flowline.Tests/SettingsCommandSurfaceTests.cs`

**Approach:**
1. Add the check at the point KTD2 names: write intent, empty name, not interactive, then fail with exit 15.
2. Build the message from the raw arguments (KTD4) with the noun and verb phrase from KTD6.
3. Change the `[name]` description from "Omit to pick one" to say that omitting lists or picks, and that a scripted write needs it.
4. Change the branch description's "optionally the component name; omit the name to list or pick one" clause. It should say the name is needed to change a component from a script, and that leaving it out reads. It must stay within the cli-for-agents §7 budget.
5. Leave the toggle/set descriptions and examples alone. The existing examples already show the name on every write.

**Patterns to follow:** the `settings capture` no-target guard (`src/Flowline/Commands/SettingsCaptureCommand.cs`, formerly `SettingsPullCommand.cs:145-148`); the probe classes and interactive/non-interactive console setup in `tests/Flowline.Tests/SettingsCommandSurfaceTests.cs:39-79`.

**Test scenarios:**
- `toggle prod --off`, non-interactive, no name: exit 15, message contains `flowline settings toggle prod <name> --off`.
- `toggle prod --on --type flow --dry-run`: suggestion keeps `--type flow` and `--dry-run`, and the noun is `flow`.
- `set prod --value https://api.contoso.com`: suggestion is `flowline settings set prod <name> --value <value>`, the real value absent.
- `set prod --value=abc --type envvar`: placeholder applied, noun `environment variable`.
- `toggle --type flow prod --off` (option before the target): suggestion is `flowline settings toggle --type flow prod <name> --off`.
- `toggle prod` (read), non-interactive, no name: no failure. The existing listing path runs and exits 0 (`NoNameInANonInteractiveRun_PrintsEveryCandidateName` still passes).
- Write intent with a name given: the check does not fire.
- Write intent at a terminal with no name: the check does not fire, and the picker path is reached.
- `toggle ApprovalFlow --off` (bad target, no name): the target error from U2 wins.
- The check fires before any environment or profile resolution, proved by a probe whose environment-resolution path would throw if reached. If cwd-based project detection makes that unreliable, the Release smoke run covers ordering (Risks).

**Verification:** `flowline settings toggle --help` and `flowline settings --help` read correctly in a Release build. The full `tests/Flowline.Tests` project passes.

### U4. Repo docs, skills and CHANGELOG

**Goal:** Every live doc in this repo uses the new verbs and states the new failure rules.

**Requirements:** R1 to R9 as documented behaviour; R3.

**Dependencies:** U1, U2, U3 (document what shipped).

**Files:**
- `README.md` (command table row at :157)
- `CONCEPTS.md` (:134)
- `docs/folder-structure.md` (:47)
- `STRATEGY.md` (:158)
- `.claude/skills/cli-for-agents/SKILL.md` (§1 environment paragraph)
- `plugin/skills/flowline/SKILL.md` (step 6 at :74-92, exit-code rows at :227, :228, :232)
- `plugin/skills/flowline-migration/SKILL.md` (:50)
- `CHANGELOG.md` (Unreleased)
- `docs/ideation/2026-10-31-cli-surface-consistency-and-boundary-ideation.html` (idea 5 card and index entry)

**Approach:**
1. Rename the verbs in each file above.
2. In `CONCEPTS.md:134`, the same sentence still names the retired `settings flow|workflow|plugin|envvar|connref` operations. Change it to `settings toggle|set` while rewriting the sentence.
3. In `plugin/skills/flowline/SKILL.md` step 6, the paragraph also still says `settings state`/`settings value`. It describes the no-name behaviour this work changes, so rewrite it for `toggle`/`set`, the write-requires-name rule and the target rule.
4. CHANGELOG, Breaking: fold `push`→`apply` and `pull`→`capture` into the existing `state`/`value` bullet, so one line covers the settings rename. Add a Breaking bullet for the scripted write with no name (0 to 15). (session-settled: user-approved.)
5. CHANGELOG, Changed: add a bullet for a bad target, now exit 15 up front instead of failing at the PAC profile lookup (usually exit 4), with the swap correction. (session-settled: user-approved.)
6. Mark idea 5 implemented in the ideation file, following the pattern of commits `94f5ea7` and `589f727`.

**Test expectation:** none, documentation only. Each behaviour claim is checked against the code from U1 to U3 before writing (AGENTS.md, Documentation).

**Verification:** Searching the live docs above for `settings push` and `settings pull` finds nothing outside CHANGELOG's rename bullet, and the two rewrites in steps 2 and 3 are done. `/verify-doc-claims` passes on the changed sections.

### U5. Wiki

**Goal:** The user-facing wiki shows the new verbs and the new failure rules.

**Requirements:** R1 to R9 as documented behaviour.

**Dependencies:** U1, U2, U3.

**Files** (repo `../Flowline.wiki`, rules in its `AGENTS.md`):
- `09-Settings.md` (examples at :8-9, :51-52, :65-66, :144-145; prose at :61, :108, :134; no-name paragraph ending "In a non-interactive run it only prints")
- `04-Command-Reference.md` (settings examples :346-347, operation table :354-355, option table "On" column :365-366, exit table row 15)
- `12-AI-Agents.md` (exit rows 15, 18, 19; command rows :77-80)
- `17-Migration-from-Daxif.md` (:403, :424)
- `18-Migration-from-ALM-Accelerator.md` (:49)

**Approach:**
1. Leave headings alone. "Applying the file" and "Writing the file" in 09 are link anchors (`[[09-Settings#...]]`), and their wording still fits.
2. In `09-Settings.md`, the no-name sentence must say that a read prints in a non-interactive run and a write fails asking for the name.
3. In `12-AI-Agents.md`, the toggle and set rows drop "exits 0" for writes. Row 15 gains a write with no name and a target that is neither a role nor a URL.
4. No rationale, internals or reassurance (wiki AGENTS.md, Audience rule).

**Test expectation:** none, documentation only.

**Verification:** A search of `../Flowline.wiki` for `settings push`/`settings pull` finds nothing. Every example pastes and runs against a Release build.

---

## Verification Contract

| Gate | Command or check | Proves |
|---|---|---|
| Build | `dotnet build Flowline.slnx` | Renames compile, no orphaned references |
| CLI tests | `dotnet test tests/Flowline.Tests` | U1 to U3 scenarios |
| Full suite | `dotnet test Flowline.slnx` | Nothing else depended on the old names |
| Release smoke, old verb | `settings push test` from a Release build | `Error: Unknown command 'push'`, exit 15 |
| Release smoke, missing name | stand-alone: `settings toggle https://contoso.crm4.dynamics.com --solution-name X --off` with stdin and stdout not a TTY | Exit 15, the suggestion line, no auth or network message |
| Release smoke, swap | stand-alone: `settings toggle ApprovalFlow https://contoso.crm4.dynamics.com --solution-name X --off`, non-TTY | Exit 15, swapped line, no PAC profile message |
| Release smoke, help | `settings --help`, `settings toggle --help` | New verbs, new branch and `[name]` descriptions |
| Tone | `/tone` on changed `.cs` files, or a review against `docs/tone-of-voice.md` | Every new or changed message |
| Docs | `/verify-doc-claims` on README, CONCEPTS, wiki 09/04/12 sections touched | Doc claims match code |

Smoke runs use `-c Release`. A Debug build propagates exceptions and prints a stack trace instead of the typed exit (AGENTS.md, Build and verification).

---

## Definition of Done

- All Verification Contract gates pass, with output recorded in the final report.
- No `settings push`/`settings pull` reference remains in `src/`, `tests/` (besides the parse-failure tests), live docs (besides the Unreleased CHANGELOG rename bullet), skills, or the wiki.
- CHANGELOG Unreleased carries the folded Breaking bullet, the new Breaking bullet and the Changed bullet.
- Idea 5 is marked implemented in the ideation file.
- No abandoned-attempt code, commented-out blocks or stray debug output in the diff.
- The final report lists what was verified and anything not verified, such as the universal-profile path of the old swapped-order behaviour, which the new check makes unreachable.
