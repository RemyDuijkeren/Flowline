# Flowline CLI end-to-end test goal

## Goal

Exercise the Flowline CLI against live Dataverse and real project folders, not the unit suite. Cover
the `clone -> init -> push -> sync -> sln add -> deploy` matrix including real project-structure
flexibility (moved, renamed, multi-project), and judge every command as both a human and an AI agent
would consume it.

**Done when** the slice chosen for the run is exercised live, every case has a recorded exit code, each
issue is either fixed inline with a regression test or written up in `docs/test-findings/`, and the
round is appended to `docs/end-to-end-test-log.md` with an explicit "Not covered" list.

A run that reports only its successes is indistinguishable from a run that covered everything. That
failure mode is what this document exists to prevent.

## Scoping a run

The full matrix does not fit one session. A single real TEST deploy alone runs past 10 minutes.
Before starting:

1. Read `docs/end-to-end-test-log.md`, newest round first. The **Not covered** lists are the backlog.
2. Pick the slice: the area named in the invocation, or the highest-value uncovered item from the log.
3. State the slice up front and carry it into the round heading.
4. Everything outside the slice counts as not covered. Say so in the writeup.

## Logging

Results live in [`docs/end-to-end-test-log.md`](end-to-end-test-log.md), never here. This document
holds only what changes how the **next** run is conducted: fixtures, constraints, matrix, known
traps. Update it when a run teaches something procedural, not to record what happened.

Append each round to the top of the log, newest first. Never edit a prior round. Round shape, taken
from the rounds that proved most useful:

```markdown
## Round <yyyy-MM-dd> — <slice>

<one paragraph: what this round exercises and why>

- **Date:** <yyyy-MM-dd>
- **Build:** `src/Flowline/Flowline.csproj`, Release, at `<commit>` (`<version>`)
- **Target:** which environments were contacted, which were not
- **Writes:** every real write made, or "none"
- **Fixtures:** what was set up and how

### Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|

### <Case X> is the one worth trusting

Name the negative control: the case that would look identical if the feature were echoing its input
rather than doing the work. State the number that moved (2 candidates in, 1 orphan out) and why that
number is the proof. Every round needs one.

### What these runs confirmed

### Not covered

### State left behind
```

## Environment

- DEV: `https://automatevalue-dev.crm4.dynamics.com`
- TEST: `https://automatevalue-test.crm4.dynamics.com`, the **real-deploy target**
- PROD: `https://automatevalue.crm4.dynamics.com`
- Auth: one `OperatingSystem`/UNIVERSAL PAC profile, tenant-wide. `flowline status` connects to all
  three first try. No `pac auth create` needed.

Solutions:

- `Cr07982`: unmanaged, but **is the environment's default solution** (friendlyname "AV Default
  Solution", system id `00000001-0000-0000-0001-00000000009b` on DEV *and* TEST). `pac solution
  delete` refuses it. Never use it for anything needing a deletable solution.
- `FlowlineDeployTest`: unmanaged, normal, deletable. Use this for first-import and delete scenarios.

Workspaces live at `C:\Code\FlowlineTests\solutions\<Name>\`, one self-contained Flowline project per
solution, each with its own `.flowline` and `.git` (co-located separate repos, per
`docs/folder-structure.md` §4). Run commands from inside the specific solution's folder; Flowline
resolves by walking up to the nearest `.flowline`.

- `Cr07982\`: the project-structure-flexibility fixture. **Keep it, don't rebuild it, and don't
  assume the scaffolded default layout when reading output:**
  - `Backend/Cr07982.Backend.csproj`: moved and renamed nupkg-mode plugin project (Dataverse package
    `av_Cr07982.Backend`). No "Plugins" anywhere in the name.
  - `LegacyPlugins/Cr07982.LegacyPlugins.csproj`: a second, **classic/unpackaged** plugin project
    (signed, plain `.dll`). Signed with a *copy* of Backend's `.snk`, so a real push hits the
    identity-changed gate and needs `--force recreate-assembly`. Fixture history, not a bug.
  - `ClientAssets/Cr07982.ClientAssets.csproj`: moved and renamed WebResources project.
  - Both plugin projects reference **published** `Flowline.Attributes 0.12.0`, so attribute features
    newer than that aren't testable here without bumping it.
- `FlowlineDeployTest\`: clean scaffold, one `[Step("account")]` plugin plus one web resource.

DEV and TEST components (assemblies, steps, Custom APIs, web resources) are disposable: add, modify
and delete freely. No need to preserve state between runs. **DEV note:** publisher `flx` already
exists, so a new-publisher test must pick a *fresh* prefix or it silently exercises the reuse path.

## Safety constraints

- **PROD: never a real write.** Always `--dry-run`. `flowline drift prod` is the even lighter preview.
- **TEST: real `deploy test` is allowed and expected.** That is what the environment is for.
- **Never force a default-solution delete.** `pac solution delete` refuses `Cr07982` because it carries
  the system default-solution identity. That is the fixture's nature, not a TEST quirk and not a
  Flowline bug. Do not look for a way around the refusal. Use `FlowlineDeployTest` when you need a
  deletable solution.
- Never force-push, never touch remote git state.
- Never commit in the Flowline source repo without being explicitly asked.

## Running the tests

- **A real TEST deploy takes over 10 minutes** (the `pac solution import` async op alone runs about 8).
  Always run it backgrounded and watch the printed log for the terminal `🚀`/error line. Do
  **not** poll for "log idle": the import and publish waits are silent for minutes and an idle-detector
  fires a false "done". A foreground timeout that kills the CLI mid-publish leaves the import committed
  but post-import cleanup unrun.
- Long `clone` and `sync` exports also take minutes. Background them.
- **Verify out-of-band with `pac env fetch`**, raw FetchXML against a target, bypassing Flowline
  entirely: `pac env fetch --environment <url> --xml "<fetch><entity name='customapi'><attribute
  name='uniquename'/></entity></fetch>"`. Use it to confirm a component really exists or is really
  gone, rather than trusting Flowline's own read of it.
  - Watch the `like` escape: `!` is **not** a FetchXML escape character. `value='av!_%'` matches
    nothing and reads as "none exist". Use `value='av_%'` or `[_]`. Be suspicious of any
    "No results returned." that confirms what you expected.
- Commit between test phases so `sync`'s dirty-check behaves predictably.

## Bug-fix policy

- **Capture the unit-suite baseline before the first fix**, and compare against it after every fix.
  Last known good: 2125 passed / 0 failed / 4 skipped (2026-08-20). Treat that number as dated, not
  authoritative. The 4 skips are the live-MSAL `ConnectViaPacAsync_*` tests, which carry an explicit
  `Skip` reason; anything red beyond those is a real regression.
- Fix inline when the root cause is clear and small (parsing slip, null ref, wrong exit code,
  misleading message). Re-run the exact failing scenario to confirm.
- **Verify the fix against the actual test or spec first.** One past "fix" (flipping `[DefaultValue]`
  on `--managed`) broke correct, already-tested behavior. Run `dotnet test Flowline.slnx`, the *full*
  suite and not just the affected file, after every fix, and revert immediately on any regression.
- Don't fix anything needing architectural judgment or deeper investigation. Log it as a finding.
- After a fix, rebuild and reinstall before re-testing live:
  `dotnet pack src/Flowline/Flowline.csproj -c Release`, then `dotnet tool uninstall -g flowline` and
  `dotnet tool install -g flowline --add-source <nupkg-dir> --version <exact-version>`. **Pin the
  exact version**, otherwise install can silently resolve the published package from nuget.org.
  Purge the NuGet cache if the version string didn't change.
- **Run the CLI from a Release build.** A Debug build propagates exceptions past the handler, so
  correct error handling prints a raw stack trace and reads as broken.

## Test matrix

Cover both **fresh state** (wipe the workspace, start clean) and **reused state** (idempotent re-run
against an already-cloned, pushed or synced folder) where relevant.

### `clone`

- Fresh empty folder, with and without each env URL (`--dev`, `--prod`, `--uat`, `--test`), with and
  without `--managed`.
- Idempotent re-clone into an already-cloned folder: skip messages, no errors, no duplicate
  `Plugins/` or `WebResources/` projects even when the real ones were moved or renamed, solution file
  untouched.
- Managed-solution rejection; C#-keyword solution-name rejection (hard to trigger live without a
  matching real solution, so check the code path if there's no practical repro).
- Interactive pick (no solution named, no env configured, TTY): tenant-wide environment picker, then
  that environment's **unmanaged** solutions (managed hidden with a count, the environment's
  `Default` solution never listed). **No "create new" choice, clone only adopts.** Picking confirms
  which `.flowline` role to save the environment under.
- Nothing to clone: an environment with no unmanaged solution stops with `⏸ Nothing to clone in
  '<env>'` plus a `Next:` line naming `flowline init <name>`, exit 0.
- Review the generated `AGENTS.md` as the artifact an agent will be steered by: accurate against
  current CLI behavior, right command order, hazards named.

### `init`

- **Greenfield create** against DEV: new publisher plus empty **unmanaged** solution land in Dataverse,
  then the repo scaffolds and builds identically to a `clone`. Verify independently (`pac solution
  list`, `Solution/src/Other/Solution.xml`), not off the CLI's own success line.
- Reusing an existing `--publisher-prefix` instead of creating one.
- **Validation rejections, before any Dataverse write**: `--publisher-prefix mscrmx`, 1-char and
  9-char prefixes, non-alphanumeric or non-letter-start prefix; a `<name>` that's a C# keyword, has
  invalid characters, or starts with a digit; a `<name>` over 65 chars; a `--display-name` over 256.
- **DEV-only refusal**: targeting a Production-type environment must refuse before any create call;
  Sandbox and Developer proceed.
- **No-TTY errors**: missing `--dev` and missing `--publisher-prefix` each error naming the flag,
  never derive one, never hang.
- **Duplicate-name refusal** before any write.
- **`✓ DEV set to <env>`** written to `.flowline` only after create, scaffold and build all succeed, and
  the created solution recorded too, so a later push or sync can resolve it.
- **Post-create failure reporting**: if scaffold or build fails after the records exist in Dataverse,
  the created publisher and solution identifiers are reported for manual cleanup, not silently dropped.
- Interactive pickers (environment, publisher, name).

### `push`, test **both modes**, they have different validation surfaces

**Project mode** (inside a Flowline project folder):

- Full push (default scope), dry-run and real. Idempotent re-run gives "Nothing to push — already up
  to date."
- Each `--scope` individually: `all`, `webresources`, `plugins`, `assemblyonly`.
- `--scope assemblyonly --scope plugins` together: rejected (mutually exclusive).
- `--no-delete`, `--no-build`, `--no-publish`, each prints its own "skipping (--flag active)" line.
- Non-interactive gates: an unrecognized form-event handler requires `--force delete-form-handlers`;
  an orphaned plugin assembly requires `--force delete-orphans`. Both must name the flag, not hang.
- Real create, update and delete paths. Drive them by actually adding a `[Step]` class, editing a web
  resource, and deleting one.

**Standalone mode** (`--pluginFile` / `--webresources`, from *outside* a project folder):

- Rejected when run inside a Flowline project folder.
- Solution name required as the first positional.
- `--scope plugins` and `assemblyonly` require `--pluginFile`; `--scope webresources` requires
  `--webresources`. The error must name the missing flag.
- **Standalone's pushed set is the single artifact**, so every *other* assembly in the solution is an
  orphan by definition, and `--force delete-orphans` would delete a live sibling. Deliberate, not a
  bug, but it makes standalone the cheapest way to manufacture an orphan on demand for testing.

### `sync`

- Clean tree: full sync, diff and drift summary correct.
- Dirty tree: rejects naming `Solution/src/...`, message **plain text, not raw Spectre markup**.
- `--bump patch|minor|major|none`, verify the version actually changes.
- `--no-build`.
- Non-interactive `--managed` reconfirmation gate when the flag conflicts with the configured value:
  rejects cleanly *before* the long export, no hang.
- A moved or renamed WebResources project must still be found, not reported as phantom drift.

### `sln add`

- Valid `.cdsproj` add; idempotent re-add gives "already in ... — skipping", not an error.
- Wrong extension (`.csproj`) points at `dotnet sln add`.
- Nonexistent path.
- **No solution file in the exact folder: error naming that exact folder, and must not search parent
  folders.** Regression test for the walk-up incident. Run it in an isolated throwaway subfolder and
  confirm the parent's solution file is untouched.

### `deploy`

- Invalid target name: clean validation error, not an opaque MSAL/AADSTS stack trace.
- `dev` as a target: rejected ("use sync, not deploy"), **regardless of `--dry-run`**. Use
  `flowline drift dev` as the DEV-target preview instead.
- Dirty git tree: rejects before contacting any environment. Scope of the dirty check is
  `Solution/src/` only, so dirtying `Plugins/` or `Backend/` does not trigger it.
- **Full `--dry-run` against PROD**: every pre-flight runs (DTAP gate, git-clean, local drift, pack,
  solution checker, orphan report) and a labeled backup is taken before stopping short of import.
  Backup label must be `flowline-dryrun-<solution>-<ts>`, distinct from a real deploy's
  `flowline-deploy-<solution>-<ts>`.
- Confirm `--dry-run` never calls `pac solution import`, never runs post-import cleanup, and emits no
  CI artifact signal (`##vso[artifact.upload...]`, `$GITHUB_OUTPUT`). Grep the output for all three.
- `--dry-run` plus `--no-delete` is identical to `--dry-run` alone (dry-run takes precedence, so the
  report reads `(--dry-run preview)`, not `(--no-delete active)`).
- Orphan cleanup is automatic rather than flag-driven, so a plain `deploy test` is what proves
  "cleanup makes the deploy succeed". Run-mode detail is under behaviors below.
- **Real `deploy test`**: update over an existing version, and a fresh first-import on
  `FlowlineDeployTest` with `--force first-import`.
- Orphan detect and delete on the deploy path, plus a follow-up `drift test` showing 0 orphans.
  **Assert a foreign component survives.**
- Post-import package-assembly check: an assembly carried in package content with no registration in
  the target is named, exits non-zero, and the finding repeats until the record exists.

### Project-structure flexibility (`SolutionFileLayout` and multi-project)

- **Move and rename the Plugins project** (folder, `.csproj`, `.snk`, `PackageId`, no "Plugins" left in
  the name): `push` must still discover it via solution-file membership plus `IPlugin` and
  `CodeActivity` reflection, not folder convention.
- **Move and rename the WebResources project**: resolved via elimination plus weighted signals
  (NoTargets SDK, `dist/`, bundler config, `package.json` build script, web assets), never a false
  negative.
- **Two plugin projects, mixed shapes**, one nupkg and one classic/unpackaged: both discovered, built
  and registered in **one** push. `PluginPackageMode.Auto` resolves per project.
- **Two WebResources candidates**: an exact top-score tie throws `ConfigInvalid` naming both. A
  *weaker* second candidate is correctly not flagged, because the resolver picks the clear winner.
  Design, not a bug.
- **Zero plugin projects**: default scope skips silently; explicit `--scope plugins` errors.
- **Zero WebResources projects**: loud warning, plugins still pushed, no throw.
- **Orphan and drift across renames**: the old assembly or package name becomes a genuine orphan, so
  push must name it and gate deletion behind `--force delete-orphans`.

## Output modes, run every phase both without and with `--verbose`

- **Without**: what a normal user sees. Judge as a UX reviewer: clean, consistent, no leaked internal
  detail, nothing that reads as unfinished.
- **With**: the real step-by-step work. Confirm the extra detail is accurate and reflects the actual
  Dataverse calls, not a restatement of the summary.
- **Every run writes a full log**, capturing the verbose detail regardless of the console filter. For a
  subset of runs, skip `--verbose` and read the log instead; that validates the "log has everything"
  guarantee independently. The path is `<storage-root>/Flowline/logs/<yyyy-MM-ddTHHmmss>Z-<command>.log`
  and is **only printed when the command fails** (non-zero exit or an unhandled exception), so a
  successful run leaves an agent to construct the path itself.

## Agent UX, judge every command as an AI agent would consume it

A large share of invocations come from an agent, not a human. Different consumer, different failure
modes. Judge from your own position: you *are* the consumer.

- **Never blocks.** No command may wait on a prompt without a TTY. Every gate needs a flag equivalent,
  an invalid `--force` value must list the valid specifiers, and a gated hazard must fail *naming the
  flag*. A hang burns the whole session, not one command.
- **Exit codes are the primary signal**: distinct, stable, same meaning every time. Record the exit
  code for every matrix case. Flag any two different failure classes collapsing onto one code.
- **Output survives capture.** Anything an agent must extract (log paths, URLs, versions) must survive
  as one unbroken token.
- **Machine-readable mode**: note where its absence forces brittle prose parsing.
- **Errors must be actionable verbatim**: the literal next command (`git init`, `pac auth create
  --url <url>`, `dotnet sln add`, `--force delete-orphans`), not a description of one.
- **Repeated runs must be honest**: "already up to date / skipping", never a silent re-do.

## Behaviors that look like bugs but aren't

Don't re-report these.

- `drift` exits **15 when drift is found**. Success, not an error, and an intentional exit-code
  distinction.
- `drift` and `deploy` compare Dataverse against the **committed `Solution/src/`**, not build output,
  so anything pushed but not yet synced shows as an orphan. Correct.
- `deploy dev` is rejected outright, dry-run or not. There is no valid dev deploy.
- **Deploy's orphan run mode is resolved, not flag-driven.** `ResolveRunMode`: dry-run wins over
  everything; `--no-delete` or a managed solution forces report-only; an unmanaged real deploy gets
  `RunMode.Normal`, which **deletes** (pre-import where possible, deferred ones retried post-import).
  `deploy`'s valid force specifiers are `drift`, `first-import`, `delete-orphans` and `all`, but
  `delete-orphans` does **not** mean what it means on `push`: here it gates exactly one unattributable
  case, a Custom API with no `plugintypeid` at all. Everything else deletes or reports based on run
  mode alone.
- An `init`-created solution builds in **Debug**. A Release build needs both the caller asking for one
  *and* `IncludeManaged`, and an init solution is unmanaged, so Debug is correct.
- `--managed` bare sets `true`; `--managed false` resets; omitting it leaves it unset, treated as
  `false`. The help's DEFAULT column shows what *bare* `--managed` resolves to. Don't "fix" without
  re-running `ManagedFlagBindingTests`.
- `clone` requires an existing git repo first.
- `push` scope and flag-mismatch validation runs *after* env-URL and auth resolution, so reaching it
  live needs a resolvable `--dev` first.
- `deploy` resolves the project *before* validating the target.
- The git repo and remote pre-flight is TTL-cached, so "No remote configured" shows on the first run
  and not the next. An agent diffing two runs will notice.
- Under `--dry-run` *with* `--force delete-orphans`, the warning still says "Use --force
  delete-orphans to delete". Cosmetic; the message keys off `willDelete`, which dry-run forces false.
- Plugin **package** content syncs by content, not version. The `.nupkg` version can stay fixed while
  code changes apply normally, and a package's name and version can't be changed after create. The
  classic-assembly version rules don't carry over.
- Commands act on the current directory with no `--project` or `--path`, so a harness that resets cwd
  must wrap every call.

## Known gaps, not coverable on this machine

State these explicitly in the round writeup rather than quietly skipping them.

- **Interactive pickers** (clone solution pick, init publisher/name/environment): no TTY in an agent
  harness, so `IsInteractive()` is false and prompts never render. Unit-tested only.
- **Privilege fault** (a user lacking create rights should give a clean error, not a raw SDK
  exception): no locked-down test user.
- **`--dev` with no matching PAC profile leading to `pac auth create`**: not triggerable with a single
  tenant-wide universal profile. A foreign URL resolves the profile and fails later as `Dev
  environment not found` instead.
- **`--force delete-form-handlers`**: needs a Dataverse form whose handler points at a function no
  longer in source, and there's no cheap way to construct it on this fixture.

## Lessons worth carrying into the next run

- **A passing test that bypasses a layer proves nothing about that layer.** Interactive `clone`
  shipped dead because a required positional made Spectre reject the bare command before the code
  ever ran, and every test called the methods directly. Any positional-arg contract needs at least
  one test through a real `CommandApp`.
- **Make the probe throw.** "The update succeeded" does not prove a plugin ran, because a silently
  skipped step looks identical. Only a marker exception discriminates.
- **Assert that a foreign component survives**, on every path that deletes anything. A prefix-wide
  Custom API delete once removed every API sharing the publisher prefix, silently, because the cascade
  preview only listed plugin types and steps.
- **Every round needs a negative control**: a case whose result would look the same if the feature
  were echoing its input. Report the number that moved, not just the pass.
- **The two orphan passes overlap by design**, so a change to one has to be checked against the other
  or a refused orphan gets deleted a pass later in the same run.
- **Check the vendor docs before writing "the platform doesn't support X"** into a finding. One search
  would have caught a wrong claim that got committed.

## Findings workflow

Every issue not fixed inline gets its own file in `docs/test-findings/`, named by slug, covering:
status (fixed or not), severity, exact repro, root cause as far as understood, and suggested fix
direction with why it wasn't done inline. This document only ever *references* a finding by path and
a one-line summary, never duplicates the writeup.

Before a run, skim `docs/test-findings/` for issues that may now be fixed (re-verify, then update or
delete the file) and decide whether any still-open finding should be promoted to a fix.

**Carry into every run:** `agent-ux-output-hard-wrapped-at-80-columns.md`. Non-TTY output hard-wraps
at 80 columns with no escape hatch, splitting log paths and `--help` URLs mid-token, and `COLUMNS` has
no effect. There is also no machine-readable mode (`--json` or porcelain) anywhere, so parsing
reflowed prose is an agent's only option. Same finding. It shapes how every run reads output, so
expect broken tokens rather than re-reporting them.

## Deliverable

1. The round appended to [`docs/end-to-end-test-log.md`](end-to-end-test-log.md) in the shape above.
2. A `docs/test-findings/<slug>.md` for each finding needing human judgment.
3. Any inline fix accompanied by its regression test and a full-suite run.
4. An explicit **Not covered** list: every matrix item in the slice that was skipped, and why.
