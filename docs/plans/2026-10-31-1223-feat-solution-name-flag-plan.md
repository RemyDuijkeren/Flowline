---
title: One Spelling for the Solution Name - Plan
type: feat
date: 2026-10-31
artifact_contract: ce-unified-plan/v1
product_contract_source: ce-plan-bootstrap
execution: code
---

# One Spelling for the Solution Name - Plan

## Goal Capsule

- **Objective:** Anyone driving Flowline, human or agent, names the solution the same way on every command where a project can supply it: `-n|--solution-name`. A word typed right after `push` or `generate` is never silently read as a solution, and someone with the old spelling in a script is told exactly what to type instead.
- **Means:** Replace the optional `[solution]` positional on `push` and `generate` with the option `settings` already has, add `-n` to it, and declare its name and description once (KTD1, KTD3). Clone and init keep their positional, named `[solution-name]` (KTD2, KTD7).
- **Authority:** Idea 2 in `docs/ideation/2026-10-31-cli-surface-consistency-and-boundary-ideation.html` owns the rationale and the C1 rule. KD5 forbids a transition alias. `STRATEGY.md:29-32` (progressive adoption) is why stand-alone mode must keep working under the new spelling. `.claude/skills/cli-for-agents/SKILL.md` and `docs/tone-of-voice.md` govern flags and message wording.
- **Stop conditions:** Stop and ask if Spectre cannot bind `-n` on any of the six commands or rejects `[solution-name]` as an argument template, if removing the positional changes how `--force <value>` binds on push beyond what KTD5 describes, or if stand-alone `push`/`generate` would need a flag other than `--solution-name` to identify the solution.
- **Execution profile:** One breaking release. Lands as one change set in this repo plus one in the wiki repo (`../Flowline.wiki`). Ask about the branch before the first commit (AGENTS.md, Branching).
- **Finishes the work:** `ce-work` or a human, in unit order.

---

## Product Contract

### Summary

`push` and `generate` stop taking the solution as a bare word and take `-n|--solution-name`, the same option the four `settings` operations use. `settings` gains the `-n` short form. `clone` and `init` both show `[solution-name]` as their positional placeholder, so the positional and the flag share one name. A leftover positional fails with a message that names the flag, or names `--env` when the word was an environment.

### Problem Frame

The solution is spelled two ways today. Outside a project, `push` and `generate` take it as an optional positional (`src/Flowline/Commands/PushCommand.cs:40`, `src/Flowline/Commands/GenerateCommand.cs:30`), while `settings` takes `--solution-name` (`src/Flowline/Commands/SettingsSupport.cs:23`). The first word after the verb means the environment on `deploy prod`, `drift test` and `settings push test`, so `flowline push test` reads as "push to test" but means "a solution called test". The positional only matters stand-alone; inside a project `.flowline` supplies the name. By C1 that makes it optional input, which is a flag. Step 1 (commit 46db17d) already aligned the in-project validation, so only the spelling differs now.

### Requirements

**Command surface**

- R1. `push` and `generate` accept `-n|--solution-name <name>` with the semantics the positional had: required stand-alone, optional inside a project, and rejected when it differs from the project's solution.
- R2. `push` and `generate` declare no positional argument.
- R3. The four `settings` operations accept `-n` as the short form of their existing `--solution-name`.
- R4. `clone` and `init` keep their positional, with the placeholder `[solution-name]` on both (today `[solution]` and `[name]`). Neither command gets `-n` or `--solution-name`.

**Errors**

- R5. `flowline push <word>` or `flowline generate <word>` fails with a message that names `--solution-name <word>`. When `<word>` is an environment role (dev, test, uat, prod) or a URL, the message names `--env <word>` instead.
- R6. Every message that tells the user to pass the solution as an argument names `--solution-name` instead. Every message that cites `flowline init <name>` or `flowline clone <solution>` cites `<solution-name>` instead.

**Documentation**

- R7. Help examples, README, wiki and CHANGELOG show only the new spelling, and CHANGELOG records the removal under Breaking.

### Key Decisions

- **No transition alias for the positional.** KD5. Governs R2, R5.
- **Positional only where the command creates or clones the solution.** C1 applied to the solution, from idea 2. Governs R2, R4.

### Scope Boundaries

- Non-goal, considered and not built: spotting a stray positional anywhere other than straight after the verb (for example `push --env dev Contoso`). Spectre's own error, "Unknown command 'Contoso'.", still names the token, though it misdescribes it. The old documented shape always put the solution straight after the verb, so that is where scripts will break. Revisit if users report the later-position case.
- Non-goal, considered and not built: `-n` on `clone` and `init`. It would give one value two spellings on those commands (KTD2).

#### Deferred to Follow-Up Work

- Make `push` and `generate` treat a `.flowline` without a `Solution` the same way. Today `generate` saves the passed name (`src/Flowline/Commands/GenerateCommand.cs:270-276`), while `push` keeps it in memory unless it also saves an environment URL (`src/Flowline/Commands/PushCommand.cs:124-127`). This change keeps both behaviours (KTD4).
- Idea 4's vocabulary table and the surface test that walks the command tree.

---

## Planning Contract

### Key Technical Decisions

- KTD1. **`-n|--solution-name <name>` is the spelling.** PAC uses `--solution-name` on `solution delete`, `online-version` and `upgrade`, and `-n` is PAC's short form for the same value on `solution clone` and `export`. Spectre 0.57.2 rejects multi-character short names ("Short option names can not be longer than one character."), so PAC's `-sn` is not available. `-n` is unused on every command involved. (session-settled: user-directed; chosen over `--solution` with `-n`: aligning with PAC beats five fewer characters once a short form exists)
- KTD2. **No `-n` on `clone` and `init`.** They keep the positional, and a short flag there would add a second spelling. (session-settled: user-directed; chosen over adding `-n` to all six commands: one spelling per command)
- KTD3. **Declare the option's template and description once as shared constants, used by two properties.** `settings` derives from `DataverseSettings` (no `--env`, it takes the environment positionally), while `push` and `generate` derive from `EnvironmentSettings` (`src/Flowline/Settings/EnvironmentSettings.cs:12`). C# allows a single base class, so a shared base would either push `--env` onto `settings` or make `push`/`generate` redeclare `--env`. One property stays on the settings base and one goes on a push/generate-only layer. Both use the same constants, and a test pins them together. The existing settings description ("required outside a Flowline project. Inside one, leave it out or pass the project's own") is true for `push` and `generate` as well, so it becomes the shared text. (session-settled: user-approved; chosen over a single shared base class: the inheritance tree cannot hold one without moving `--env`)
- KTD4. **The flag changes nothing in a `.flowline` that has no solution.** `ProjectConfig.GetOrUpdateSolution` adopts a passed name when `Solution` is null (`src/Flowline.Core/Config/ProjectConfig.cs:162-164`). Clone and init hit that branch on every first run. Push and generate hit it only from a hand-written `.flowline`, because every command that writes `.flowline` sets the solution first (clone sets it at `src/Flowline/Commands/CloneCommand.cs:109` or `:248` and saves at `:73`, init at `src/Flowline/Commands/InitCommand.cs:165-169`, and provision requires an existing project). The flag feeds the same property the positional did, so Core's solution logic is untouched (two Core messages change wording under R6, nothing else). (session-settled: user-approved; chosen over making the flag refuse or always persist there: a hand-edited edge case is no reason to change Core in a spelling change)
- KTD5. **The leftover-positional message is a third hint in the existing parse-error chain.** `config.Settings.StrictParsing = true` (`src/Flowline/Program.cs:410`) already makes `push Contoso` a `CommandRuntimeException`, and the handler at `src/Flowline/Program.cs:223-235` tries `BuildArgumentOrderHint` and then `BuildTypeValueHint` before falling back to Spectre's message. The new hint matches when the first token is `push` or `generate` and the second does not start with `-`, a shape the parser can no longer accept. It answers `--env` for a role keyword (`DriftCommand.TryResolveRole`) or an absolute URL, because `push dev` is the confusion idea 2 names, and `--solution-name` otherwise. It is pure over the raw args, like its siblings, so it can be unit-tested. `push --force Contoso` still binds `Contoso` as the `--force` value and fails in force validation, the same as today (`tests/Flowline.Tests/PushCommandTests.cs:988-1006`).
- KTD6. **`clone` and `init` change their placeholder and messages, not their properties.** The help placeholder and the messages are user-visible. The settings property names are not, and renaming them would only add churn.
- KTD7. **The positional placeholder on `clone` and `init` is `[solution-name]`.** The positional and the flag then name the same value the same way, and help reads `clone [solution-name]` beside `push --solution-name`. Whether Spectre accepts a hyphen in an argument template is not yet verified; U2 checks it in help output, and the stop condition covers a rejection. (session-settled: user-directed; chosen over `[solution]` on both: one name for the value whether it is passed positionally or as a flag)

### Assumptions

- Agents and scripts drive stand-alone `push`/`generate` with the long form. The short form serves interactive use. This is why the long name follows PAC rather than being shortened.

---

## Implementation Units

### U1. Shared solution-name option on push, generate and settings

- **Goal:** One declaration of `-n|--solution-name` serves `settings`, `push` and `generate`, and the positional is gone.
- **Requirements:** R1, R2, R3, R6 (push/generate messages). KTD1, KTD3, KTD4.
- **Dependencies:** none.
- **Files:**
  - `src/Flowline/Settings/` (new small file holding the shared template and description constants, and later U3's hint)
  - `src/Flowline/Commands/SettingsSupport.cs`
  - `src/Flowline/Commands/PushCommand.cs`
  - `src/Flowline/Commands/GenerateCommand.cs`
  - `src/Flowline/Commands/FlowlineCommand.cs` (comment at the solution-match check that says "positional [solution] argument")
  - `src/Flowline/Program.cs` (push and generate help examples)
  - `tests/Flowline.Tests/FlowlineSettingsTests.cs`
  - `tests/Flowline.Tests/PushCommandTests.cs`
  - `tests/Flowline.Tests/GenerateCommandTests.cs`
- **Approach:**
  1. Add the constants. Point the settings base's existing option at them, with `-n` added.
  2. Give `push` and `generate` a solution-name property declared with the same constants. Put it on a layer between `EnvironmentSettings` and their own settings classes, so it is declared once for both.
  3. Remove the `[solution]` argument from both. Rename their reads of the old property to the new one at every site: push stand-alone resolution, push project-mode validation and lookup, generate stand-alone and project branches.
  4. Reword the three messages that say "first argument" or "as an argument" (`PushCommand.cs:632`, `GenerateCommand.cs:104`, `GenerateCommand.cs:126`) to name `--solution-name`, following `docs/tone-of-voice.md`.
  5. Rewrite the help examples at `Program.cs:436-437` and `Program.cs:465` with `--solution-name`. Keep one stand-alone push example showing the full long form, since agents copy examples.
  6. Update the five code comments tagged `R8` (the code's own tag from an earlier plan, not an R-ID here) that describe the positional: `PushCommand.cs:93`, `GenerateCommand.cs:119`, `FlowlineCommand.cs:450`, `PushCommandTests.cs:145`, `GenerateCommandTests.cs:489`.
- **Patterns to follow:** the settings base option (`SettingsSupport.cs:21-31`). `EnvironmentSettings` as the model for a small shared settings layer.
- **Test scenarios:**
  - `push --solution-name Contoso` and `push -n Contoso` both bind `Contoso` to the solution property (parse probe, like `BuildPushParseProbe`).
  - `generate -n Contoso` binds the same way.
  - `push Contoso` makes the parse probe fail (non-zero exit, no binding).
  - Reflection: the solution-name property on `SettingsSettings`, `PushCommand.Settings` and `GenerateCommand.Settings` carries the identical option template and description.
  - Reflection: `PushCommand.Settings` and `GenerateCommand.Settings` declare no `CommandArgument`.
  - Stand-alone push with no solution name throws `ValidationFailed`, and the message contains `--solution-name`.
  - The existing `--force MySolution` test still shows `MySolution` bound as the force value and the solution property null. Update its comment, which describes the removed positional.
  - Existing `ValidateSolutionMatchesConfig` tests stay green unchanged. The check sits behind the property, not the spelling.
- **Verification:** a Debug build runs `config.ValidateExamples()` (`Program.cs:170`) without complaint about the rewritten examples. `push --help` and `generate --help` list `-n, --solution-name` and no `[solution]`.

### U2. Clone and init placeholder and messages

- **Goal:** `clone` and `init` both show `[solution-name]`, and no message cites `init <name>` or `clone <solution>`.
- **Requirements:** R4, R6 (clone and init messages). KTD2, KTD6, KTD7.
- **Dependencies:** none.
- **Files:**
  - `src/Flowline/Commands/InitCommand.cs`
  - `src/Flowline/Commands/CloneCommand.cs` (placeholder, and the `flowline init <name>` hint at the empty-picker path)
  - `src/Flowline/Commands/FlowlineCommand.cs` (the `flowline clone <solution>` message when no solution is configured)
  - `src/Flowline.Core/Config/ProjectConfig.cs` (two `flowline clone <solution>` messages in schema validation)
  - `docs/end-to-end-test-goal.md` (cites the `Next:` line as `flowline init <name>`)
  - `tests/Flowline.Tests/InitCommandTests.cs` (only if a test asserts either message text)
- **Approach:**
  1. Change both placeholders to `[solution-name]` (`InitCommand.cs:40`, `CloneCommand.cs:27`).
  2. Change `flowline init <name>` to `flowline init <solution-name>` at `InitCommand.cs:233` and `CloneCommand.cs:230`.
  3. Change `flowline clone <solution>` to `flowline clone <solution-name>` at `FlowlineCommand.cs:360` and `ProjectConfig.cs:261`, `:270`.
  4. Leave the property names alone (KTD6).
- **Test scenarios:**
  - Reflection: `InitCommand.Settings` and `CloneCommand.Settings` each have a `CommandArgument` at position 0 whose template is `[solution-name]`, and no `--solution-name` option.
  - Any existing test asserting the `clone <solution>` or `init <name>` message text is updated to the new wording.
- **Verification:** `clone --help` and `init --help` show `[solution-name]`. Searching `src/` for `init <name>` and `clone <solution>` finds nothing.

### U3. Pointed error for the old positional

- **Goal:** `flowline push Contoso` tells the user to type `--solution-name Contoso`, and `flowline push dev` tells them `--env dev`.
- **Requirements:** R5. KTD5.
- **Dependencies:** U1 (the positional must be gone for the parse error to fire).
- **Files:**
  - the shared-option file from U1 in `src/Flowline/Settings/`
  - `src/Flowline/Program.cs` (add the hint to the chain at the `CommandRuntimeException` arm)
  - `tests/Flowline.Tests/` (new test class for the hint, next to the settings hint tests in `SettingsCommandTests.cs`)
- **Approach:** a pure function over the raw args returns the better message or null, matching the shape of `SettingsSupport.BuildArgumentOrderHint`. Chain it after the two settings hints. Write the wording to `docs/tone-of-voice.md`: say what moved and give the exact replacement, quoting the user's word.
- **Patterns to follow:** `SettingsSupport.BuildArgumentOrderHint` and `BuildTypeValueHint` (`src/Flowline/Commands/SettingsSupport.cs`), and their tests in `tests/Flowline.Tests/SettingsCommandTests.cs:450-480`.
- **Test scenarios:**
  - `["push", "Contoso"]` returns a message containing `--solution-name Contoso`.
  - `["generate", "Contoso", "-o", "./Models"]` returns a message containing `--solution-name Contoso`.
  - `["push", "dev"]` and `["push", "PROD"]` return a message containing `--env`, not `--solution-name`.
  - `["push", "https://contoso-dev.crm4.dynamics.com"]` returns a message containing `--env`.
  - `["push", "--env", "dev", "Contoso"]` returns null (out of scope per Scope Boundaries).
  - `["push"]`, `["deploy", "prod"]` and `["settings", "push", "test"]` return null.
  - Case: `["PUSH", "Contoso"]` matches, the same way the settings hints ignore case.
- **Verification:** from a Release build (AGENTS.md, Build and verification), `flowline push Contoso` prints `Error:` plus the hint and exits with the `ValidationFailed` code. `flowline push dev` prints the `--env` variant.

### U4. Documentation and changelog

- **Goal:** No user-facing doc shows the positional on `push` or `generate`, or any placeholder other than `[solution-name]` on `clone` and `init`.
- **Requirements:** R7.
- **Dependencies:** U1, U2, U3 (docs describe shipped behaviour and the error text).
- **Files:**

| File | Lines today | Change |
|---|---|---|
| `CHANGELOG.md` | Unreleased, Breaking | Entry: positional removed from `push`/`generate`, use `-n\|--solution-name`, no alias, old form fails naming the flag. Changed: `settings` gains `-n`; `clone` and `init` show `[solution-name]` |
| `README.md` | 73, 110, 124, 148, 149, 150, 154 | `init <solution-name>`; stand-alone push examples with `--solution-name`; command table `clone [solution-name]`, `init [solution-name]`, `push`, `generate` |
| `../Flowline.wiki/04-Command-Reference.md` | 38, 46, 73, 79, 95, 118-120, 261, 273-274, 416; settings option rows | clone and init argument rows and prose `[solution-name]`; push and generate argument rows become `-n, --solution-name` option rows; examples; settings rows show `-n` |
| `../Flowline.wiki/02-Project-Configuration.md` | 158-159 | examples |
| `../Flowline.wiki/09-Settings.md` | 3 mentions of `--solution-name` | mention `-n` once where the option is introduced |
| `../Flowline.wiki/10-WebResources-Project.md` | 23 | `push --solution-name <solution> --webresources ...` |
| `../Flowline.wiki/11-Generate-Early-Bound-Types.md` | 16, 75, 76 | examples and the inline comment |
| `../Flowline.wiki/16-Migration-from-spkl.md` | 50, 53, 56, 59 | examples |
| `../Flowline.wiki/17-Migration-from-Daxif.md` | 50, 53, 56, 59 | examples |
| `../Flowline.wiki/18-Migration-from-ALM-Accelerator.md` | 79, 82, 85 | examples |
| `../Flowline.wiki/19-Migration-from-PACX.md` | 37, 40, 43, 46, 130 | examples |
| `docs/ideation/2026-10-31-cli-surface-consistency-and-boundary-ideation.html` | idea 2 card and its index row | mark step 2 implemented, matching how step 1 was recorded |

- **Approach:** read `../Flowline.wiki/AGENTS.md` before editing wiki pages. Use the long form in docs and examples, and mention `-n` once per command reference entry. Before finishing, re-run the sweep `rg -n "(push|generate)\s+(<solution>|\[solution\]|[A-Z][A-Za-z]+)\b"` (case-sensitive, so lowercase prose does not match) plus `init \[name\]|init <name>|clone \[solution\]|clone <solution>` over README, `docs/` (excluding `docs/ideation` and `docs/plans`) and the wiki. Every remaining hit must be `settings push` or capitalised prose, not a command example.
- **Test expectation:** none. Docs only. The sweep above is the check.
- **Verification:** the sweep is clean, and every wiki example runs as written against the Release build's `--help` output (option names match).

---

## Verification Contract

| Gate | Command or check | Proves |
|---|---|---|
| Build | `dotnet build Flowline.slnx` | compiles; Debug example validation passes |
| Targeted tests | `dotnet test tests/Flowline.Tests` | U1-U3 scenarios |
| Full suite | `dotnet test Flowline.slnx` | settings, clone and init untouched elsewhere |
| CLI output | Release build: `push Contoso`, `push dev`, `push --help`, `generate --help`, `init --help`, `settings pull --help` | R1-R5 as a user sees them |
| Tone | `/tone` on changed `.cs` files, or a manual pass against `docs/tone-of-voice.md` | new and reworded messages |
| Doc sweep | the U4 `rg` sweep | R7 |

## Definition of Done

- R1-R7 hold, each with the test or check named in its unit.
- Build and full test suite pass. The final response lists what ran and anything not verified.
- CHANGELOG has the Breaking entry. README and wiki are updated, and the wiki commit is reported separately because it is its own repo.
- No dead code from the removed positional remains: no unused property, no stale "first argument" text, no comment describing `[solution]` on push or generate.
