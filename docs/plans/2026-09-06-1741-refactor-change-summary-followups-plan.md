---
title: "Change Summary Follow-Ups - Plan"
type: refactor
date: 2026-09-06
topic: change-summary-followups
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-plan-bootstrap
execution: code
---

# Change Summary Follow-Ups - Plan

## Goal Capsule

- **Objective:** Someone extending the change summary can find the part they need and change it without reading the whole thing, and a solution with hundreds of changed components reports as quickly as a small one.
- **Means:** Split the summary along the seam that already exists between computing a change set and rendering one, then remove the per-component subprocess fan-out (KTD1, KTD2).
- **Product authority:** The code review of `feat/diff-command` (ten reviewers, 2026-09-06). Every item here was raised there, triaged as non-blocking, and deferred with the user's agreement.
- **Open blockers:** None. Nothing here blocks `feat/diff-command` from merging.

---

## Product Contract

### Summary

Six deferred items from the `flowline diff` review, none of which are defects in shipped behavior. The largest is a three-way split of `SolutionChangeSummary`, which `AGENTS.md` already flags as misfiled. The rest are one performance change with a measurement gate, two small simplifications, and a set of named test gaps.

### Problem Frame

`SolutionChangeSummary` reached 987 lines carrying eight responsibilities: git plumbing, two status-vocabulary parsers, component-path parsing, XML name resolution, five sub-change differs, and three renderers. `AGENTS.md` lists it among the files misfiled into `Flowline/` that belong in `Flowline.Core`, and says to move them opportunistically. The move has never happened because the file as a whole cannot move: its compute half depends on `SubprocessCapture` and `XmlHelpers`, both of which live in `Flowline`.

The half that could move never has, because nobody separated it. The renderers are pure functions of the public model and touch nothing but Spectre and the filesystem, and `Flowline.Core` already references Spectre.

Separately, reading a component's XML costs a `git show` subprocess per side, so the cost of a report grows with the number of changed components rather than with their size.

### Key Decisions

- KD1. Nothing here blocks the `diff` feature. Every item was reviewed, triaged and deferred deliberately; this plan exists so that decision leaves a record instead of a gap.
- KD2. The split is three-way, not an extraction (session-settled: user-directed — chosen over extracting a renderer inside `Flowline`, which shrinks the file but leaves the misfile exactly as `AGENTS.md` found it). Governs R1, R2.

### Requirements

- R1. The change-summary model and its renderers live in `Flowline.Core`.
- R2. Computing a change set stays in `Flowline`, and produces the Core model.
- R3. Reading component XML does not cost one subprocess per component per side.
- R4. A component's XML is parsed once per side, not once per consumer.
- R5. Whether an empty report is written is decided by the caller without a parameter on the writer.
- R6. Selecting the working-tree listing path does not depend on matching the literal string `HEAD`.
- R7. The test gaps named in the review are covered.

### Scope Boundaries

**Not in scope.** Any change to what the report says. This is a structural and performance plan: `sync`, `diff` and `generate` must produce identical output before and after, and that is the acceptance test for every unit here.

---

## Planning Contract

### Key Technical Decisions

- KTD1. **The split follows the dependency, not the line count.** The model (`ChangeStatus`, `SubChange`, `ChangeItem`, `ChangeGroup`, `VersionTransition`, the summary's own properties) and the three renderers depend on nothing in `Flowline`. The compute half depends on `SubprocessCapture` and `XmlHelpers`, which is why the file has never moved. Move the first group to `Flowline.Core` and leave the second where it is. Governs R1, R2.
- KTD2. **Batching is gated on a measurement, not adopted on principle.** `git cat-file --batch` replaces N subprocesses with one per ref, but the review found no measured problem: a real solution reported in under three seconds. Take a baseline against a repository with a few hundred changed components first, and drop this unit if the number does not justify it. Governs R3.
- KTD3. **`--verbose` must not carry file content.** A previous attempt to route the per-component read through the shared subprocess helper put whole solution manifests through the console renderer and the log sink, adding minutes to the main path. Whatever replaces the read keeps file content out of `SubprocessCapture`. The comment at the read site records this; do not undo it.

### Sequencing

U1 first: it moves types every other unit touches. U2 and U3 are independent of each other once U1 lands. U4, U5 and U6 are independent throughout.

---

## Implementation Units

### U1. Split the summary across the project boundary

- **Goal:** the model and renderers live in `Flowline.Core`; computing stays in `Flowline`.
- **Requirements:** R1, R2. Implements KTD1.
- **Dependencies:** None.
- **Files:** `src/Flowline.Core/` (new files for the model and the renderer), `src/Flowline/Utils/SolutionChangeSummary.cs`, `src/Flowline/Commands/SyncCommand.cs`, `src/Flowline/Commands/DiffCommand.cs`, `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`, and a new test file in `tests/Flowline.Core.Tests/` for whatever moves.
- **Approach:**
  1. Move the model types and the summary's own properties to `Flowline.Core`.
  2. Move `WriteTree`, `WriteFlat`, `WriteChangesFileAsync` and their private helpers (`AddGroupItems`, `AddFileTreeNodes`, `StatusIcon`, `WriteFileAsync`) to a renderer type in `Flowline.Core` that takes the model.
  3. Leave `ComputeAsync` and everything it calls in `Flowline`, returning the Core model.
  4. Update the three production call sites and roughly twenty test call sites.
  5. Move the renderer's tests to `Flowline.Core.Tests`; compute tests stay put.
- **Patterns to follow:** `AGENTS.md`'s project boundary rule is the authority on what goes where. `src/Flowline.Core/Console/` shows Spectre use inside Core.
- **Execution note:** Output must not change. Run the full suite between the model move and the renderer move rather than only at the end, so a break is attributable to one of them.
- **Test scenarios:**
  - Every existing summary and renderer test passes unchanged in behavior after relocation.
  - `sync`'s report is byte-identical before and after: same file location, same provenance line, same no-changes line, same overflow hint.
  - `diff`'s tree and written report are unchanged.
  - `generate`'s dirty-tree guard reports what it reported before.
  - `Flowline.Core` does not reference `Flowline` (compiler-enforced; assert the direction is intact).
- **Verification:** The full suite passes and `flowline diff` against a real solution repository produces output identical to the pre-split build.

### U2. Read component XML without a subprocess per component

- **Goal:** the cost of a report tracks the size of the change, not the number of components in it.
- **Requirements:** R3. Implements KTD2, KTD3.
- **Dependencies:** U1.
- **Files:** `src/Flowline/Utils/SolutionChangeSummary.cs` (or its post-split compute file), plus its tests.
- **Approach:** Measure first against a repository with a few hundred changed components and record the baseline in the commit message. If it justifies the change, replace the per-component `git show` with one `git cat-file --batch` process per ref, feeding `<ref>:<path>` on stdin and reading length-prefixed blobs back. Bounded parallelism over the existing calls is the smaller alternative if batching proves awkward.
- **Execution note:** If the measurement does not justify it, close the unit with the number recorded and change nothing. That is a successful outcome, not a skipped one.
- **Test scenarios:**
  - A component present on one side and absent on the other still resolves correctly through the batch reader.
  - A read failure is still distinguishable from a genuine absence, and still suppresses that component's sub-change block rather than rendering everything as added.
  - Paths with spaces and non-ASCII characters round-trip through the batch protocol.
  - Output is identical to the pre-change build for the same repository state.
- **Verification:** The recorded before and after timings on the same fixture, plus identical output.

### U3. Parse each component's XML once per side

- **Goal:** one parse per side per component, not one per consumer.
- **Requirements:** R4.
- **Dependencies:** U1.
- **Files:** the post-split compute file and its tests.
- **Approach:** `DiffFormXml` parses each side up to three times and `DiffSavedQuery` twice, on top of name resolution. Have the differs take an already-parsed document instead of a string.
- **Test scenarios:** sub-change output is unchanged for entities, views, forms and option sets; a malformed document on one side still yields no sub-changes and no throw.
- **Verification:** Existing sub-change tests pass unchanged.

### U4. Let the caller decide whether an empty report is written

- **Goal:** the writer stops carrying a caller's policy as a parameter.
- **Requirements:** R5.
- **Dependencies:** U1.
- **Files:** the renderer, `src/Flowline/Commands/SyncCommand.cs`, `src/Flowline/Commands/DiffCommand.cs`, and their tests.
- **Approach:** Drop `writeWhenEmpty` and have each caller guard its own call. `sync` skips an empty report; `diff` writes one for a named target and skips it for the bare default.
- **Test scenarios:** `sync` writes no file for an empty summary; `diff --write <named>` writes one; bare `diff --write` on a clean tree leaves `CHANGES.md` alone.
- **Verification:** The existing tests for all three behaviors pass without being weakened.

### U5. Select the listing path without matching a literal ref name

- **Goal:** the working-tree listing path is chosen by what the caller meant, not by the spelling of a ref.
- **Requirements:** R6.
- **Dependencies:** None.
- **Files:** the compute file, `src/Flowline/Commands/DiffCommand.cs`, and their tests.
- **Approach:** The branch exists because a repository with no commits has no HEAD to resolve, so that case must use the status listing. It is currently selected by comparing the left ref to `"HEAD"`, so an explicit `--from head` takes the other path. Carry the intent explicitly instead. Do not make the comparison case-insensitive: that would accept a spelling git rejects where the filesystem is case-sensitive.
- **Test scenarios:** a bare run and `--from HEAD` produce identical reports in a repository with commits; a repository with no commits still lists its files; `--from head` behaves consistently on both path styles.
- **Verification:** The no-commits test still fails if the status listing path is bypassed.

### U6. Close the named test gaps

- **Goal:** the scenarios the review named as uncovered are covered.
- **Requirements:** R7.
- **Dependencies:** None.
- **Files:** `tests/Flowline.Tests/DiffCommandTests.cs`, the summary tests, `tests/Flowline.Core.Tests/TerminalTabStatusTests.cs`.
- **Approach:** Add the scenarios below. Each was named by a reviewer with a reason; none is speculative coverage.
- **Test scenarios:**
  - `diff` performs no Dataverse call, asserted rather than argued structurally.
  - A staged deletion and an unstaged deletion through `--from <ref>` working-tree mode, which exercises the porcelain deletion vocabulary that ref-to-ref mode does not.
  - The untracked-versus-listed dedup in `--from <ref>` mode where the same path appears in both listings.
  - A component whose name comes from XML, added in ref-to-ref mode.
  - A tracked file modified and deleted on disk against a non-HEAD ref.
  - The terminal tab marker for the remaining non-failure exit codes.
- **Verification:** Each new test fails when the behavior it names is broken.

---

## Verification Contract

- Build: `dotnet build Flowline.slnx`
- Full suite: `dotnet test Flowline.slnx`, required for U1 and U2 since both are cross-cutting.
- Check user-facing output from a Release build (`-c Release`); a Debug build prints a stack trace instead of the handled error line.
- For U1 and U2, run `flowline diff` against a real solution repository and compare output to the pre-change build. The review found two problems that no test caught and only a real repository surfaced.
- Confirm every changed file's UTF-8 BOM state matches its state before the change. Byte-order-mark churn is invisible in a normal diff and was introduced twice during the review round it came from.

## Definition of Done

- `Flowline.Core` holds the model and renderers; `Flowline` holds compute; the dependency direction is intact.
- `sync`, `diff` and `generate` produce identical output to the pre-change build.
- U2 either lands with recorded before and after timings, or is closed with the measurement that made it unnecessary.
- Every unit's test scenarios exist as tests and pass.
- `AGENTS.md`'s list of known-misfiled files is updated to reflect what moved.
- No abandoned approach is left in the diff.

---

## Sources

- Code review of `feat/diff-command`, 2026-09-06: ten reviewers (correctness, project-standards, testing, maintainability, agent-native, api-contract, reliability, performance, learnings, adversarial). Findings triaged live with the user; everything here was deferred deliberately.
- `docs/plans/2026-09-06-1229-feat-diff-command-plan.md`, the feature this follows.
- `AGENTS.md`, project boundary rule and the known-misfiled list.
