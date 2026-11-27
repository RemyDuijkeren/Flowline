# Live test log

Manual end-to-end exercises against a real Dataverse environment, rather than only through the unit
suite. Newest round first. The matrix, fixtures and constraints these rounds run against live in
[`docs/end-to-end-test-goal.md`](end-to-end-test-goal.md); results only ever land here.

---

## Round 2026-11-27: changes since 0.18.0, and the NU5017 root cause

Sweep of what 0.19.0, 0.20.0, 0.21.0 and Unreleased added or changed, run after #26 landed on
`master`. It starts with the NU5017 build failure from the previous round, which blocked every case
that builds.

- **Date:** 2026-11-27
- **Build:** `src/Flowline/Flowline.csproj`, Release, at `4a90c24` plus the uncommitted NU5017
  scaffold fix (`0.21.1-alpha.0.32`), run by absolute path from the main checkout.
- **SDK:** every Flowline run picked 11.0.100 (workspaces have no `global.json`).
- **Targets:** DEV (writes), TEST (one real deploy, dry-runs), PROD (dry-runs and reads only).
- **Writes:**
  - DEV, solution `FlowlineDeployTest`: package update, step upsert, Custom API `av_KeepMe` deleted,
    3 web resources created or updated, versions 1.0.5, 1.0.6 and 1.1.0.
  - DEV, solution `FlowlineConnRefE2E`: flow `E2E A1` off then on; `settings apply` changed nothing.
  - TEST: the failed `deploy test` deleted step `FlowlineDeployTest.Probe.ProbePlugin: Create of
    contact` and Custom API `av_KeepMe` before the import failed. Backups
    `flowline-deploy-FlowlineDeployTest-20261127T015323Z` and four `flowline-dryrun-…` labels.
  - PROD: no import. One backup (`flowline-dryrun-FlowlineDeployTest-20261127T020746Z`) from the
    one dry-run that got past the DTAP gate.
- **Workspaces:** `~/Projects/AutomateValue-Dev/solutions/FlowlineDeployTest/` (fresh clone, plus an
  `AccountPostCreatePlugin` with `[Step("account")]` and the template form annotations removed) and
  `~/Projects/AutomateValue-Dev/solutions/Nu5017Check/` (fresh clone of `FlowlineConnRefE2E`).
- **Parse-level checks:** 35 commands run by a sub-agent in a copy of the workspace whose roles all
  pointed at `https://flowline-sealed.invalid`, so nothing could reach Dataverse.

### NU5017

Root cause found and fixed in the scaffolder (`8bf4ee4`; the finding file was deleted once fixed).
SDK 11 only: a clean Plugins build fails 6 of 6 on 11.0.100 and 0 of 6 on 10.0.403, and
`Microsoft.PowerApps.MSBuild.Plugin` 2.16.1 fails the same way. Clean or rebuild does not help; the
earlier passes were a skipped pack reusing a package from an SDK 10 hand build. With the fix, two
fresh clones built first time on SDK 11.

### Results

| Area (release) | Case | Observed | Exit |
|---|---|---|---|
| clone (fix) | Fresh clone, SDK 11 | Built first time, csproj has `<TargetFrameworks>net462</TargetFrameworks>` | 0 |
| pull (fix) | `--bump none`, `bin/` and `obj/` wiped | Repacked, package holds the dll | 0 |
| parse (0.19, 0.21) | Unknown option, mistyped command, missing argument, option without value | Caret line, `Did you mean 'push'?`, help pointer, value list | 15 |
| parse (0.21) | `sync`, `settings state`, `settings value`, `-f` on `status`/`diff`/`scaffold`, `--scope`, target-only flags on the wrong command, `push Contoso` | All rejected; `push Contoso` names `--solution-name` | 15 |
| parse (0.21) | Non-role target; role whose `.flowline` value is not a URL | Rejected up front naming the key | 15 |
| push (0.21) | Bare `push` or `push plugins` outside a project | Names the stand-alone form | 11 |
| diff (0.19) | `diff A B`, `diff A..B` offline | Component tree, identical for both forms | 0 |
| status (0.20) | Linux, three environments | All three checked over their own connections in 10 s | 0 |
| drift (0.19) | dev, test, prod; `--exit-code` | 0 with orphans; 22 with `--exit-code`; prod reports solution not found | 0 / 22 / 3 |
| push (0.21) | `--dry-run`, real, re-run | Tree plan; package updated, step upserted, Custom API deleted; then `Nothing to push` | 0 |
| push wr (Unreleased) | LF to CRLF + BOM, then a real edit | `no changes`, then `1 update(s)` | 0 |
| pull (0.21) | Second pull with nothing new | Says only the version moved and there is nothing to deploy, names `--bump none` | 0 |
| pack (Unreleased) | Clean; dirty `Solution/src`; untracked root file | Versioned zip; exit 12 naming the file; root file ignored (scope is the pack inputs) | 0 / 12 / 0 |
| pack (Unreleased) | `--binaries-from-build`: untracked root file; clean; `--no-build` alone | Exit 12; zip carries the fresh build's package (md5 differs from the committed one); exit 15 | 12 / 0 / 15 |
| deploy (0.21) | `prod` pointing at a Sandbox, `test` pointing at Production | Refused before any work, naming the key | 15 |
| deploy | `dev` as target | Rejected | 15 |
| deploy | `prod --dry-run` with 1.1.0 not in TEST | DTAP gate refuses | 15 |
| deploy | **real `deploy test`, 1.1.0 over 1.0.6** | **Import failed**: `Unable to delete plug-in assembly as it is part of plugin package`. Finding below | 13 |
| deploy | `test --dry-run`, `--no-delete`, `--path <zip>`, `--binaries-from-build` | `flowline-dryrun-…` backup, no import, no CI signals, `(--dry-run preview)` | 0 |
| deploy | `prod --dry-run --skip-dtap-check` (first import) | Names the first-import confirmation the real run will ask for | 0 |
| settings (0.21) | `toggle dev` list, read one, `--on` without a name | List, state, `Name the component to turn on: …` | 0 / 0 / 15 |
| settings (0.21) | `toggle --off --dry-run`, `--off`, `--off` again, `--on` | Verified with `pac env fetch`: unchanged, Draft, Draft, Activated | 0 |
| settings (0.21) | `capture dev`, `set dev <ref>` read, `apply dev --dry-run`, `apply dev` | File written; binding read; `1 unchanged` twice | 0 |
| Ctrl+C (0.21) | One press, two presses during the export | `Cancelling. Press Ctrl+C again to force quit.`; tree clean | 130 |
| --env (0.19) | Unconfigured URL, unattended; TEST URL on `push` | Used without saving; push refuses a non-DEV URL | 3 / 15 |

### New finding

Fixed in `33ccca1` (the finding file was deleted once fixed):
deploy's orphan cleanup marks the plugin package the solution still carries for deletion when the
target holds a stale sibling assembly in it. Dataverse refused the delete on a dependency, then the
import failed on the same leftovers. Reproduced offline. Predates 0.18.0. Every `deploy test`
dry-run since still plans the delete and still says "would deploy cleanly".

### Observations, not written up (pre-existing, outside the slice)

- The change summary in `pull` and `CHANGES.md` has no handling for `customapis/` or
  `pluginpackages/`: a deleted Custom API and a changed package produce no entry.
- The template web resources carry `// flowline:onload account "Account"`. In a solution without the
  account form, the first `push` fails with exit 15.
- `generate` on a solution with no tables passes an empty `-enf`, which pac reads as no filter: it
  generated all 2052 DEV entities and was killed by the 500 s timeout.
- A failed import exits 13 with "check the environment and your PAC login".
- `settings toggle … --off` on a flow already off prints the same `Done` as a change.
- `sync` suggests `sln`, not `pull`.
- `capture` lists only components that are off, by design (R12), so a solution with every flow on
  captures no flow section.

### Not covered

- A successful real `deploy test`, and with it: the printed `git tag` commands (0.21), post-import
  orphan cleanup, the post-import package-assembly check. Blocked by the finding above.
- The staged-upgrade early stop (needs a `_Upgrade` holding solution) and managed deploys.
- `deploy --force first-import` for real, and the declined first-import exit 17.
- `init`, `provision` (including `--force overwrite`), `scaffold plugins`, `sln add`.
- `generate` on a solution with tables, and the `xrmcontext` generators.
- The interactive role offer for an unconfigured `--env` URL: needs a second DEV-typed environment.
- Interactive prompts generally, other than Ctrl+C.
- Forms and views in settings files, business rules and BPFs, environment variables (the fixtures
  have none).
- `push plugins` with a service-endpoint step (#21), and Custom API paging past 5000.
- Telemetry content.
- Windows.

### State left behind

- TEST: `FlowlineDeployTest` still 1.0.6.0; package `av_FlowlineDeployTest.Plugins` still holds
  `Probe` and `Probe2` with their plugin types (the finding's repro, kept on purpose); the Probe step
  and `av_KeepMe` are gone.
- DEV: `FlowlineDeployTest` at 1.1.0.0 with the template-based plugin; fixture `FlowlineConnRefE2E`
  unchanged by this round's net effect.
- Workspaces `FlowlineDeployTest/` and `Nu5017Check/` under `~/Projects/AutomateValue-Dev/solutions/`.
  `Nu5017Check/` has an uncommitted `Solution/deploymentSettings.dev.json` from `capture`.

---

## Round 2026-11-26: pull connection reference cleanup (`--tidy-connrefs`)

This round exercises the connection reference review that branch `feat/pull-tidy-connrefs` (#26) adds
to `pull`. It covers advice without the flag, the unattended merge and removal with the flag, and
the interactive questions at a terminal, all against live DEV. It is the first round on the Linux
machine, and the first to drive Flowline's interactive prompts, through a pty.

- **Date:** 2026-11-26
- **Build:** `src/Flowline/Flowline.csproj`, Release, at `b11aac0` plus the two inline fixes below
  (`0.21.1-alpha.0.30`). The worktree binary was run by absolute path; the global tool was not
  reinstalled.
- **Target:** DEV (`automatevalue-dev`) only. TEST and PROD were not contacted.
- **Writes:**
  - Fixture seed, plus one out-of-band save of flow `E2E E` (case I1), by a scratch SDK harness.
  - Through Flowline:
    - 4 flow definition rewrites (`E2E B`, `D`, `E`, `G`).
    - 5 reference deletes (`flcr_unused`, `dvB`, `dvD`, `dvE`, the generated-name one).
    - 1 reference removed from the solution (`flcr_foreign`).
    - 2 display-name renames.
    - A version write of 1.0.1 (case A's default bump).
- **Fixtures:** solution `FlowlineConnRefE2E`, publisher prefix `flcr`, seeded by a scratch harness.
  The shape is in the goal doc under `pull`. Every reference was bound to the one DEV Dataverse
  connection ("My Test Connection"). All 7 flows were on, and `E2E Foreign` was outside the solution
  and used `flcr_foreign`. Workspace:
  `~/Projects/AutomateValue-Dev/solutions/FlowlineConnRefE2E/`, its own git repo, committed between
  cases. The role files (`deploymentSettings.{test,uat,prod}.json`) were added before case D, with
  invented connection ids.

### Results

| # | Case | Expected (from the plan) | Observed | Exit |
|---|---|---|---|---|
| 0 | `clone FlowlineConnRefE2E --env <dev>` on a fresh repo | Clone completes | Export and scaffold OK, then build fails with NU5017; no AGENTS.md or settings file. Re-run: completes. Fixed in `8bf4ee4` | **13**, then 0 |
| A | Plain `pull` (default bump, build on) | Advice block, no DEV write | `! Connection references in DEV: 1 duplicate group on one connection, 2 unused.` plus the `--tidy-connrefs` line. All 14 DEV row versions identical before and after. Build then fails with NU5017 | **13** |
| A2 | `pull --force all --no-build --bump none` | `--force all` does not turn the cleanup on | Same advice, 14 row versions identical | 0 |
| B | Dirty `Solution/src/` plus `--tidy-connrefs` | Dirty gate stops before any cleanup | `Uncommitted changes in 'Solution/src/'`; 14 row versions identical | 12 |
| C | `--tidy-connrefs`, unattended, no role files | Unused removed; group with missing evidence reported, not merged (AE8) | `flcr_unused` deleted; `flcr_foreign` removed from the solution only, record and outside flow untouched. **The 5-member group was skipped with no line at all.** Fixed inline, see below | 0 |
| C2 | Same, after the fix | AE8 line; nothing to change | `Not merging flcr_dvB, flcr_dvD, flcr_dvE, flcr_shared…_a1b2c into flcr_dvA: no settings file for Prod, Uat, Test…` then `↷ nothing to change`; DEV identical to C | 0 |
| D | Role files added, `--tidy-connrefs --verbose`, unattended | `dvA`/`dvB` merge; the different-account group and default name are only reported | Flow `E2E B` re-pointed and still on; `flcr_dvB` deleted; entries removed from 4 settings files; `Left for a person: 1 group on different accounts, 1 default name.` | 0 |
| E | Re-run D | Nothing to do, said honestly | `Left for a person…` then `↷ Connection references: nothing to change`; DEV unchanged | 0 |
| I1 | Interactive (pty): `Merge into flcr_dvA`, while flow `E2E E` is saved out of band at the prompt | Row-version refusal stops the cleanup, reports done and not done, no export | `✓ Re-pointed flow E2E D`, then `stopped at flow 'E2E E': The flow changed since the review… Done: flow 'E2E D'. Not done: flow 'E2E G', the settings files, reference…`; working tree clean | **18** |
| I2 | Interactive re-run: keep separate, remove unused, renames | Fresh review sees `flcr_dvD` as unused; renames write the display name only | `flcr_dvD` deleted, 4 settings files cleaned, 2 renames landed. The driver double-sent keys (see "Not covered"), so which rename went where was not the plan | 0 |
| I3 | Interactive: `Merge into flcr_dvA` | Different-account merge completes | Flows `E2E E` and `E2E G` re-pointed and still on; `flcr_dvE` and `flcr_shared…_a1b2c` deleted; prod file left with `flcr_dvA` only | 0 |
| G | Plain `pull --bump none` with the build on, after a clean hand build | Build passes | `✓ Build FlowlineConnRefE2E done in 5s`. NU5017 is intermittent: 2 of 4 Flowline builds failed | 0 |
| F | Plain `pull --verbose`, then `--tidy-connrefs`, on the tidy solution | Silent advice; tidy message | `Connection references: nothing to tidy` (verbose only); `✓ Connection references are tidy` | 0, 0 |

### Case A is the one worth trusting

Case A is the negative control for "advice never writes". A review that wrote while advising would
print the same two lines, so the lines prove nothing on their own. The proof is the 14 row versions
(7 flows, 7 references) read before and after, which were identical. Cases A2 and B repeat that
check. Against them, case D moved exactly 2 of the 13 numbers, flow `E2E B` and the deleted
`flcr_dvB`, and left the other 11 untouched.

### Fixed inline

- **Unattended run said nothing about a held-back group (AE8).** `--tidy-connrefs` without a
  terminal skipped a same-connection group with missing evidence silently. An agent reading the
  output would believe the duplicates were gone. It now prints one `Not merging … no settings file
  for …` line per such group. There is a regression test in `PullConnectionReferencesTests`. Full
  suite: 2128 passed / 4 skipped (Core) and 1573 passed / 7 skipped (CLI).
- **Output leaked type codes and `(s)` plurals:** `still used by 1 component(s) (types 29)` and
  `1 file(s) changed` now read `still used by 1 component outside this solution` and `1 file
  changed`. The existing test was tightened to the exact text.

### What these runs confirmed

- **Advice:** advice mode makes no DEV write, and `--force all` does not change that (A, A2).
- **Dirty gate:** the gate runs before the cleanup (B).
- **Outside use:** a reference used only from outside the solution is removed from the solution,
  not deleted. Its record and the outside flow both survive (C).
- **Flows stay on:** in-place rewrites of flows that are on, including the different-account merge,
  left every flow on, with the new reference in its definition (D, I1, I3).
- **Interrupted merge:**
  - The row-version check refuses a flow saved after the review.
  - The stop lists done and not-done items with exit 18.
  - The export is skipped.
  - A re-run picks up the leftovers as a fresh review (I1, I2).
- **Settings files:** entries are removed from the shared file and all three role files (D, I2, I3).
- **Interactive questions:** they render and accept selection, confirm and text input through a
  real pty (I1 to I3).
- **Log file:** the non-verbose run's log carries the cleanup lines (C2).

### Observation: `--verbose` adds nothing to the review

Case D ran with `--verbose` and made real DEV writes. The connection reference section printed exactly
the same lines as a run without it. There was no count of references and flows read, no timing, and
no line per write. The summary lines do name each write, so nothing is hidden. But the goal doc's
"with: the real step-by-step work" check has nothing to judge here. This is not filed as a finding.
Add verbose lines only if someone needs to debug a review.

### Not covered

- **A second DEV connection.** This environment has one Dataverse connection, so every
  different-account group differed by role file only. A merge across two DEV connections has never
  run live.
- **Suspended-flow and canvas-app hold-backs.** Unit-tested only; there was no cheap way to build
  either fixture.
- **A second identity.** All writes ran as one user.
- **Rename mapping as scripted.** I2's driver matched each prompt's redraw and double-sent keys. The
  renames that landed were real writes of the display name only, but not the planned mapping, and
  "Type a name" with a name already taken was never exercised.
- **The review failing with the flag on.** Unit-tested only (`Run_WithFlag_ReviewThatCannotRun_Stops`).
- **Settings save failure mid-cleanup (AE10).** Unit-tested only.
- **Everything outside the slice:** push, deploy, drift, init, `sln add`, project-structure
  flexibility, TEST and PROD. The orphan cleanup's switch to the shared removal call was not run
  live through `push` or `deploy`; the same call ran live through case C.

### State left behind

- **DEV:** the fixture is still there, pending approval to delete:
  - Solution `FlowlineConnRefE2E` 1.0.1 and publisher `flcr`.
  - References `flcr_dvA` (renamed "flcr_dvA (My Test Connection)") and `flcr_foreign`.
    `flcr_foreign` is outside the solution now.
  - Seven flows named `E2E *`, all on, on a monthly recurrence that lists one account row.
- **Workspace:** `~/Projects/AutomateValue-Dev/solutions/FlowlineConnRefE2E/`, kept, one commit
  per case.
- **Connection:** "My Test Connection" in DEV, kept, because the fixture needs it.
- **Flowline repo:** the inline fixes and these docs are uncommitted in the worktree.

---

## Round 2026-08-25 — deploy registers a missing package assembly before importing

Verifies the pre-import repair built from rounds 2026-08-24 and 2026-08-24b: `deploy` creates the
`pluginassembly` record the target is missing, before the import, so the import can bind the
assembly's plugin types.

- **Date:** 2026-08-25
- **Build:** `src/Flowline/Flowline.csproj`, **Release**, `0.17.1-alpha.0.33`, with
  `PluginPackageAssemblyRepairService` added. Installed by purging the NuGet cache first — the
  version string didn't change, so a plain reinstall would have resolved the previous build.
- **Target:** TEST only. DEV and PROD were not contacted at all this round.
- **Writes:** one `pluginassembly` record created by Flowline, one real solution import, and the
  pre-import backup (`flowline-deploy-FlowlineDeployTest-20260824T162437Z`).
- **Fixtures:** rounds 2026-08-24a/b consumed the original unregistered-Probe fixture, so a fresh one
  was built rather than reused. `FlowlineDeployTest.Probe2` — a real net462 assembly with one `IPlugin`
  type, strong-named with a new key — was added to the package content of the previous round's zip
  (`lib/net462/` inside `av_FlowlineDeployTest.Plugins.nupkg`), and the solution bumped 1.0.5.0 ->
  1.0.6.0. TEST had no record for it. The key is disposable and lives only in a scratch folder — a
  future `Probe3` should just generate a new one rather than hunting for this.

### Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | `deploy <test-url> --path probe2.zip` | Repair names the assembly and creates its record before the import | `✓ FlowlineDeployTest.Probe2 (1.0.0.0) in package av_FlowlineDeployTest.Plugins had no registration in the target — created it, so the import can bind its plugin types.` | **0** |
| B | Post-import check on the same run | Clean, now that the record exists | `✓ Plugin package assemblies are all registered.` | — |
| C | TEST after A | Record present, plugin types materialized by the import | `85e1e151-…` with `publickeytoken d5f3bf8ba511b1c3`, and plugin type `FlowlineDeployTest.Probe2.Probe2Plugin` | — |
| D | Orphan cleanup during A | Must not treat content-carried assemblies as orphans | 2 candidates checked, **0 orphans** | — |

### Case A's count is the one worth trusting

The package content carried **three** plugin-bearing assemblies — `FlowlineDeployTest.Plugins`,
`.Probe`, `.Probe2` — and exactly **one** create was issued. A service that registered
unconditionally, or that echoed its input, would have printed three lines and created two duplicate
records over assemblies already registered from the earlier rounds. One line, one record, for the one
assembly the target actually lacked.

Case C is the second control, and it is the claim the whole design rests on: Flowline wrote **only**
the record. It never uploaded package content. The plugin type under that record was created by the
import itself, exactly as measured in round 2026-08-24b, which is why the repair is one `Create` and
not a content write.

**What this round does not prove.** The repair ran first, so the counterfactual was never observed —
nothing here shows the import would have left `Probe2` unregistered. That evidence belongs to rounds
2026-08-17 and 2026-08-24, against assemblies a build produced into a package push had written;
`Probe2` was hand-injected into package content and signed with a different key, which is not the same
shape. What this round shows is narrower and still worth having: at the moment the repair looked, the
target had no record, and after it the assembly was registered with working plugin types. Proving the
counterfactual for this fixture shape needs a `Probe3` deployed once with the repair disabled first.

### What these runs confirmed

- The repair fires on a real deploy, against a real target, and names what it created (A).
- One create is enough end to end: record before the import, plugin types after it, no content
  upload from Flowline (A, C).
- The repair runs after the backup — the restore point existed before the first write (A).
- The post-import check and the repair agree on the same run rather than double-reporting (A, B).
- Orphan cleanup is unaffected by a record Flowline created seconds earlier (D).

### Not covered

- **The managed branch.** Report-without-writing on a managed target is unit-tested only. No managed
  import of a package in this state has ever been measured, which is exactly why that branch refuses
  to write.
- **A failed create.** The warn-and-import-anyway path is unit-tested only; no live case was built.
- **Report-only runs.** `--dry-run` and `--no-delete` write nothing and say what they would create —
  unit tests only.
- **A step bound to the repaired assembly.** `Probe2` carries a plugin type and no step, so this run
  does not re-exercise the exit-13 import failure from round 2026-08-24. It exercises the state that
  *precedes* it. Binding a step needs the plugin type's export key, which does not exist until the
  type does.
- **A target that has never held the package**, where the repair skips and the import creates
  everything itself.
- **The pre-import missing-component gate**, which still reports clean on the zip that failed in round
  2026-08-24. Unchanged, and still open as a finding.

### State left behind

TEST holds `FlowlineDeployTest` at 1.0.6.0 with three registered package assemblies — `Plugins`,
`Probe`, `Probe2` — and the `Create of contact` step from round 2026-08-24b. `Probe2` is now
registered, so this fixture is consumed for repair-positive testing the same way the original was;
the next round needs a `Probe3`. The fixture zips and the unpacked trees are under
`C:\Code\FlowlineDeployTest\_fixture\`. Three environment backups exist from these rounds.

---

## Round 2026-08-24b — does creating the assembly record alone unblock the import?

Direct continuation of round 2026-08-24, which left one state unmeasured: assembly record present in
the target, plugin types not yet written. That state decides how large a repair has to be. If the
import can materialize plugin types once the record exists, a repair is a single create. If not, it
also has to upload package content before importing.

- **Date:** 2026-08-24
- **Build:** `src/Flowline/Flowline.csproj`, **Release**, at `4a17ff3` (`0.17.1-alpha.0.33`)
- **Target:** TEST only. DEV read-only, PROD not contacted.
- **Writes:** one `pluginassembly` record created directly in TEST, then one real `deploy` that
  succeeded, plus its pre-import backup (`flowline-deploy-FlowlineDeployTest-20260824T130050Z`).
- **Fixtures:** the same `probe-step.zip` from the previous round, byte-identical, redeployed
  unchanged. The only variable introduced was the record.

### Setting up the middle row

`FlowlineDeployTest.Probe` (1.0.0.0, neutral, `a610a2cd1c4b4489`, isolationmode Sandbox) was created
under package `av_FlowlineDeployTest.Plugins` with `SolutionUniqueName=FlowlineDeployTest`, using the
same field set `PluginService.RegisterPackageAssemblyDirectlyAsync` uses. **Content was deliberately
not written** — writing it would have populated plugin types and measured the wrong row. Confirmed
before deploying: record present, **zero** plugin types.

### Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| E | Create the record only, no content write | Record exists, no plugin types | `63e3e99b-bb9f-f111-b8dc-6045bd8ca91c`, 0 plugin types | — |
| F | Re-run the identical failing deploy from round 2026-08-24 | Unknown; the question of the round | **Succeeded.** `✓ Plugin package assemblies are all registered.` | **0** |
| G | TEST state after F | Show what the import created | Solution 1.0.5.0, plugin type `FlowlineDeployTest.Probe.ProbePlugin` created, step `...: Create of contact` created | — |
| H | Orphan cleanup during F, with a hand-created record present | Must not delete the record or its package | 2 candidates checked, **0 orphans** | — |

### The answer

**Creating the `pluginassembly` record is enough.** The import's own package-content write populates
plugin types under the newly created record, the step's `PluginTypeExportKey` then resolves, and the
deploy completes. Flowline never has to upload package content to a target.

That sizes the repair: **one create, before the import.** Not a content upload, and nothing the
import was not already going to write.

### Case F against case C is the one worth trusting

Same zip, byte-identical. Same command. Same target. Same solution version. One record created by
hand between the two runs, and nothing else touched. The outcome flipped from **exit 13, import
failed on the export key, nothing written** to **exit 0, plugin type and step both created**. A round
that only ran F would be indistinguishable from a fixture that had quietly repaired itself; C is the
control that makes the record the cause.

Case H is the second control, and it closes a loop from round 2026-08-20. Hand-registering a record
in the target used to make the next deploy delete the entire package — that is the bug the
package-content exclusion fixed. This round created exactly such a record and then ran the deploy
that would have deleted it. Zero orphans, package intact.

### What these runs confirmed

- A missing package-assembly record is the whole blocker; the plugin type and step follow from the
  import once it exists (E, F, G).
- A repair can therefore be a single `Create`, and it must run **before** the import (F).
- The post-import package-assembly check reports clean once the record exists, on the same deploy that
  created the types (F).
- Orphan cleanup tolerates a hand-created package-owned assembly record (H).

### Not covered

- **Managed import.** Every run here was unmanaged.
- **A target that has never held the package.** The record was created under an existing package; a
  first import has no package to attach it to and may behave differently.
- **A Custom API bound to a missing plugin type**, which resolves by `plugintypeid` rather than by
  export key. Still unexercised.
- **Whether the created record's identity fields matter.** Version, culture and public key token were
  copied from DEV. A wrong-identity record was not tried, so it is unknown whether the import would
  reject it or silently attach types to it.
- **The pre-import missing-component gate** still reported `✓ No missing components` on the zip that
  failed in round 2026-08-24. Unchanged, and still written up as a finding.

### State left behind

**The unregistered-Probe fixture is consumed.** TEST now holds `FlowlineDeployTest.Probe` registered,
with one plugin type and one step (`Create of contact`), and the solution at 1.0.5.0. Recreating the
"content carries it, no record" state means rebuilding the package. DEV is unchanged and still holds
the same package with both assemblies registered, so a future round can re-derive a fixture from it.
Two environment backups exist from today's runs.

---

## Round 2026-08-24 — does a step bound to an unregistered package assembly fail the import?

Single-question round. The 2026-08-17 measurement and the 2026-08-20 round both showed a solution
importing cleanly, exit 0, while an assembly carried in plug-in package content never got a
`pluginassembly` record in the target. Both used `FlowlineDeployTest.Probe`, which has a plugin type
and **no steps** — so nothing in the imported solution ever referenced the missing registration. This
round adds exactly one variable: a step bound to that assembly's plugin type.

- **Date:** 2026-08-24
- **Build:** `src/Flowline/Flowline.csproj`, **Release**, at `4a17ff3` (`0.17.1-alpha.0.33`)
- **Target:** TEST only. DEV was read (export, FetchXML) but never written to. PROD not contacted.
- **Writes:** one real `deploy` into TEST, which failed and rolled back, plus the environment backup
  that deploy takes before importing (`flowline-deploy-FlowlineDeployTest-20260824T112012Z`). No
  component was created, changed or deleted.
- **Fixtures:** both workspaces (`C:\Code\FlowlineDeployTest`, `C:\Code\FlowlineTests`) were gone and
  were not rebuilt. The environment-side fixture survived intact and carried the round: DEV holds
  `FlowlineDeployTest.Probe` (1.0.0.0) registered under `av_FlowlineDeployTest.Plugins` with one
  plugin type and no steps; TEST holds the same package content with no Probe record. A fresh DEV
  export was unpacked, one hand-authored step file added under `SdkMessageProcessingSteps/`, its
  root component added to `Solution.xml`, the version bumped 1.0.4.0 -> 1.0.5.0, and the result
  repacked. Package content was never altered.

### Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | `drift <test-url> --path <zip>` before the deploy | Read-only preview, nothing to delete | 2 orphan candidates checked, **0 orphans**, 0 to delete | 0 |
| B | Pre-import missing-component gate on the same zip | Names the plugin type the step needs and blocks | `✓ No missing components.` — did not see it | — |
| C | `deploy <test-url> --path <zip>` with a step bound to the unregistered assembly's plugin type | Unknown; the question of the round | Import **failed** on the step, whole import rolled back | **13** |
| D | TEST state after C | Prove C rolled back rather than half-applied | Version still 1.0.4.0, one assembly, one step, no Probe record, no Probe step | — |

### The answer

```
pac.exe: The reason given was: SDK Message Processing Steps import: FAILURE: A
record for PluginType with hash value
0867E13987980D03336B16BAF525D616BA4F4B987C6CC2F228966631CE0665F9 for
PluginTypeExportKey is not found. Please try again with correct value
pac.exe: Error: The async operation completed with a statuscode of Failed.
Error: Deploy failed — check the environment and your PAC login.
```

A step resolves its plugin type by `PluginTypeExportKey`, a content hash, not a GUID. The target has
no plugin type for an assembly it has no record for, the hash resolves to nothing, and the import
fails. Nothing is partially applied.

**This narrows the silent hole considerably.** The unregistered assembly is silent *only while nothing
references it*. Give it a step and the deploy stops dead.

What this run does **not** establish is whether the documented remedy still works from here. That
remedy is "create the `pluginassembly` record, then deploy again so the content write populates its
plugin types" — but a step resolves by plugin *type* export key, and creating the record does not by
itself create the type. Three states exist and only one was measured:

| Target state | Step import resolves? |
|---|---|
| no assembly record, no plugin type | **fails** — measured, case C |
| assembly record created, plugin types not yet written | **unmeasured** |
| assembly record and plugin types both present | presumably fine; the Plugins assembly transports normally |

If the middle row also fails, the manual remedy is unusable for any assembly carrying a step, because
the "deploy again" half is the deploy that fails. That is the next run to make, and it is cheap: the
identity needed is `FlowlineDeployTest.Probe`, 1.0.0.0, neutral, `a610a2cd1c4b4489`, Sandbox, under
package `a4ee77fc-b0a4-4718-8db9-43298593644f`.

### Case C against the 2026-08-20 round is the one worth trusting

Same target, same package, same unregistered assembly, same `.nupkg` content, same command shape. The
outcome flipped from **import succeeds, exit 21, finding reported** to **import fails, exit 13,
nothing written**. A round that only ran case C would be indistinguishable from a broken fixture; the
prior round is the control that makes the step the cause.

Strictly, two things differ from that control, not one: the step, and the version bump 1.0.4.0 ->
1.0.5.0 that a re-import needs. The failure names the plugin-type export key and nothing else, so the
version is not a plausible cause — but it was not excluded by running the unedited zip at 1.0.5.0,
which is what would make this control airtight.

Case D is the second control. A failed import that had half-applied would leave the version bumped or
the package content rewritten. Neither moved.

### What these runs confirmed

- A step bound to a plugin type absent from the target fails the whole solution import (C).
- The failure is atomic — solution version, assemblies, plugin types and steps all unchanged (D).
- Flowline's pre-import missing-component gate does not cover this class of reference (B). Written up
  as `docs/test-findings/missing-component-gate-misses-plugin-type-step-reference.md`.
- Standalone `deploy --path` and `drift --path` both ran the full pre-flight from a bare folder with
  no project and no Git repo, and reported the standalone-specific skips (`no environment config`,
  `no project source here`) rather than failing (A, C).
- Deploy takes its backup before importing, so a failed import still leaves a labelled restore point (C).

### Not covered

- **A Custom API bound to a missing plugin type.** `av_KeepMe` binds by `plugintypeid` rather than by
  export key, so it is a different resolution path and may well behave differently. Not exercised.
- **Whether a real export would fail identically.** The step file here was hand-authored with a
  synthesised step id; a genuine export from an environment where the step exists carries the source's
  own id. The plugin-type reference — the part that failed — is byte-identical either way, but this
  was not confirmed against a real registered step. Nothing else in that file was validated either:
  the import stopped at the export key, so `Stage`, `Mode` and `EventHandlerTypeCode`, all copied from
  the Plugins step, were never exercised.

- **The middle row of the table above** — assembly record present, plugin types not yet written. This
  is the run that says whether the documented manual remedy still works for a step-carrying assembly,
  and whether a repair can sit after the import at all. It needs a `pluginassembly` record created in
  TEST, which consumes the unregistered-Probe fixture permanently: a package-owned assembly record
  cannot be deleted directly, so the state cannot be restored without rebuilding the package.
- **Managed import**, and a target that has never held the package.
- **The post-import package-assembly check (exit 21) on this fixture.** The import never completed, so
  the check never ran. The 2026-08-20 round covers it for the step-less case.
- Everything else in the matrix. This round asked one question.

### State left behind

TEST is byte-for-byte where the 2026-08-20 round left it: `FlowlineDeployTest` at 1.0.4.0, package
content carrying both DLLs, `FlowlineDeployTest.Probe` still unregistered. One new environment backup
exists. The fixture zips are under `C:\Code\FlowlineDeployTest\_fixture\` — `src.zip` is the clean DEV
export, `probe-step.zip` the edited one that reproduces case C in a single command.

---

## Round 2026-08-20 — deploy package assembly check

Branch `feat/deploy-package-assembly-check`. Exercises the post-import check that verifies plug-in
package assemblies registered in the target, and the orphan-cleanup fix that stops a whole package
being deleted over one of them.

- **Date:** 2026-08-20
- **Build:** `src/Flowline/Flowline.csproj`, **Release**, at `553c592`
- **Target:** TEST only. DEV and PROD were never written to in this round.
- **Writes:** three real solution imports into TEST, all through standalone `deploy --path` from a
  bare temp folder, so no project or repository was involved.
- **Fixtures:** exports taken from DEV during the 2026-08-17 measurement, with `solution.xml` edited
  to produce the manifest shapes under test. Package content was never altered.

### Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | Target's package content carries an assembly the target has no record for | Names it, non-zero exit | `FlowlineDeployTest.Probe` (1.0.0.0) named in `av_FlowlineDeployTest.Plugins`, with the remedy line and the recurrence line | **21** |
| B | Re-run A unchanged | The finding and its exit repeat | Identical finding | **21** |
| C | Manifest names **no** plugin assemblies while content still carries both | A registered assembly whose DLL is in the content is not an orphan; package survives | 3 orphan candidates checked, **0 orphans**, package, assembly and both plugin types intact afterwards | **21** |

### Case C is the one worth trusting

C is the destructive path. TEST holds `FlowlineDeployTest.Plugins` as a registered package assembly.
Stripping both `type="91"` root components from the manifest makes that assembly absent from the
solution while its DLL is still inside the imported `.nupkg` — exactly the state that used to
classify it as an orphan and redirect to deleting the entire `pluginpackage`, taking every assembly,
plugin type and step registration with it, automatically and with no `--force`.

**The orphan candidate count moved from 2 to 3.** That is the load-bearing observation: the case
genuinely arose and reached the handler rather than never being detected. The handler then excluded
it and reported zero orphans. Queried afterwards, the package, its assembly and both plugin types
(`KeepMeApi`, `AccountPostCreatePlugin`) were all still present.

The negative direction — a package genuinely dropped from source still deleting — was **not**
exercised live. It is covered in the unit suite by revert-and-confirm, including an over-broad probe
that fails 12 tests when the protection is widened to every package. Proving it live means actually
destroying the package in TEST, and the recovery costs more than the evidence is worth.

### Case A is the headline, and the message shape held

```
! 'FlowlineDeployTest.Probe' (1.0.0.0) in package 'av_FlowlineDeployTest.Plugins'
  has no registration in the target — it will not run.
Fix it: create the pluginassembly record under that package with isolationmode sandbox
  and the assembly's own version, culture and public key token, then deploy again so the
  content write populates its plugin types.
This finding and its non-zero exit repeat on every later deploy until that record exists.
! Deploy finished with 1 post-import finding — see above.
```

Before this branch, that same deploy printed nothing about the assembly and exited 0. The closing
line names no cause, because more than one post-import service can report now.

Case B exists because the message makes a claim — that the finding repeats until the record is
created — and a claim in user-facing output is worth checking rather than asserting.

### What these runs confirmed

- The check runs post-import, reports, and never writes to the target (A, B, C).
- `ExitCode.AssemblyNotRegistered` (21) reaches the process exit code from a real deploy (A, B, C).
- The finding message renders in the house warning / `Fix it:` / recurrence shape (A).
- The finding and its exit code recur unchanged until the record exists (B).
- An assembly the imported package content still carries is not treated as an orphan, and the
  package survives a deploy that would previously have deleted it (C).
- Standalone `deploy --path` reaches all of this with no project and no Git repository.

### Not covered

Everything here rests on unmanaged imports into one Sandbox environment in one tenant.

- **`ExitCode.Inconclusive` (19)** — the "could not verify" path. Unit tests only; no live case was
  built, because making a package uninspectable without also making the import fail needs a fixture
  this round did not construct.
- **The plugin-types finding** — a `pluginassembly` row that exists with zero plugin types. Unit
  tests only.
- **R12 live** — a package genuinely removed from source still deleting. Unit tests only, for the
  reason under case C.
- **A mixed package** — one content-carried assembly plus one genuinely orphaned sibling, where the
  sibling's steps and Custom APIs are cleared while the package survives. Unit tests only.
- **Direct deletion of a package-owned plugin type** — now load-bearing on the mixed-package path,
  and still unverified against a live environment.
- **The `push` promotion note** — not re-exercised here. It was observed firing during the
  2026-08-17 measurement, before the message was reshaped.
- **Managed import**, and importing into an environment that has never held the package.

### State left behind

TEST's `FlowlineDeployTest` solution was imported three times at 1.0.4.0 and its plug-in package
content rewritten each time. `FlowlineDeployTest.Probe` remains unregistered there, which is the
finding rather than a fault. No records were created or deleted. DEV and PROD were not contacted.

---

## Round 2026-08-15 — changes since 0.16.0

Manual end-to-end exercise of the work released after tag `0.16.0`, run against a real Dataverse
environment and real folders rather than only through the unit suite.

- **Date:** 2026-08-15
- **Build:** `src/Flowline/Flowline.csproj`, Release, at commit `4409fc4` (`0.16.1-alpha.0.68`)
- **Range under test:** `0.16.0..HEAD` — 70 commits, matching the CHANGELOG `[Unreleased]` section
- **Test project:** `FlowlineTests/solutions/FlowlineDeployTest`, outside this repo
- **Target:** the DEV environment. PROD and TEST were never written to.
- **Writes:** none. Every Dataverse call in this round was read-only (`drift`, `status`).

The earlier round of this document covered one post-0.16.0 feature — the deploy missing-component
gate — against TEST. Those results are kept below rather than discarded; the feature is in the same
range and the runs still stand.

### Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | `drift dev` on a project with a deliberately orphaned Custom API | Orphan flagged with a provenance verdict naming the removal commit | `CustomApi 'av_KeepMe'` reported at Prio2, with author, date, and the commit subject as the stated reason | 15 |
| B | Resolved-profile line | Names the environment, not the profile | `Resolved PAC auth profile #1 (unnamed, UNIVERSAL) — AutomateValue Dev (…)` | 0 |
| C | `scaffold webresources` in an empty folder | Writes the template alone, generic project name | 8 files, `WebResources/WebResources.csproj`, no solution file, no `.flowline` | 0 |
| D | Re-run over the same folder | Reports already-there, writes nothing | `↷ WebResources project already there — skipping` | 0 |
| E | Folder holding a stray `tsconfig.json`, no project file | Refuse by name, write nothing | Refused naming `tsconfig.json`; the file's contents unchanged; no other file created | 11 |
| F | `scaffold plugins` | Reject, naming accepted values | `'plugins' isn't something scaffold can write — pass one of: webresources.` | 15 |
| G | `scaffold webresources` inside a project | Name after the solution, register in the solution file | `Contoso.WebResources.csproj`, one entry added to `Contoso.slnx` | 0 |
| H | Same, run from `src/deep/nested` | Resolve upward, write at the project root | `✓ Flowline project: ../../../` — 9 entries at the root, **0** in the nested folder | 0 |
| I | `new webresources` (alias) | Identical to `scaffold` | Same output and same files | 0 |
| J | `push --scope formevents` (removed) | Rejected, naming valid values | `Failed to convert 'formevents' to PushScope[]. Valid values are 'None', 'AssemblyOnly', 'Plugins', 'WebResources', 'All'` | 15 |

### Case A is the one worth trusting

The orphan provenance verdict is the largest thing in this range, and it is the hardest to fake.
`FlowlineDeployTest` carries a commit that removed a Custom API from source specifically to leave a
survivor in the environment. `drift dev` found it and answered *why*:

```
Orphan components (1):
  Prio2 — still running deleted logic:
    CustomApi 'av_KeepMe' (27b8f322-941b-49b4-a681-74cd94f12a09) — would delete
      Removed by RemyDuijkeren on 2026-08-01 — "Remove av_KeepMe from source —
      probe: drift must flag it live in TEST (survivor proof)"
```

The commit subject is the entire point of the feature. *Removed on purpose* and *never yours* are
indistinguishable from the orphan alone, and the operator was closing that gap from memory. Here the
reason for the removal is read out of the repository's own history and printed next to the component
that would be deleted.

**The filtering held.** The run checked 2 orphan candidates and reported 1. A pass that reported both
would be indistinguishable from one that echoes its input; reporting one means the comparison
actually resolved against the environment and the checkout.

### Case H is the one that justifies the mode announcement

`scaffold` finds a Flowline project by walking *upward*, which means a run from a subdirectory can
land in project mode when the folder in front of you looks empty. Run from `src/deep/nested`, it
announced `Flowline project: ../../../`, wrote nine entries at the project root, and wrote nothing at
all in the nested folder.

That behaviour is correct and would be surprising without the announcement — which is why the mode is
stated before anything is written rather than inferred afterwards from where the files landed.

### Case E — the collision guard

The already-there check only sees the project file. A folder holding template-named files *without*
one sails past it, and the template writer replaces files rather than merging them. Case E put a
hand-written `tsconfig.json` in an otherwise empty `WebResources/`:

```
Error: WebResources\tsconfig.json is already here and scaffold won't write over it
       — move it aside, or scaffold somewhere else.
```

Exit 11, the file byte-identical afterwards, and no other template file created — the check runs
before the first write, so a refusal leaves nothing half-written. There is deliberately no `--force`
to overrule it.

### No-network claim, observed rather than asserted

`scaffold` claims it reaches nothing: no Dataverse, no authentication, no network, not even the
update check. That is observable in the output rather than only in the code.

Every command that runs the standard probe opens with it — the `drift` run in case A begins
`Checking your setup...` and then `✓ Prerequisites all good, let's go!`. **No `scaffold` run in cases
C through I printed either line.** The probe is where the PAC CLI check, the git-repo check, and the
NuGet update call live, so its absence is the claim demonstrated.

Case F is the sharper version: `scaffold plugins` returned exit 15 from a bare temp directory that
was not a git repo and had no `.flowline` — the argument was rejected without any of those
prerequisites being consulted.

### What these runs confirmed

- Orphan provenance resolves against real git history and renders author, date, and subject (A).
- The comparison filters rather than echoes — 2 candidates in, 1 orphan out (A).
- The resolved-profile line names the environment it resolved for (B).
- `scaffold` writes the template with no Dataverse call, no auth, and no prerequisite probe (C–I).
- Both `scaffold` modes work, and the upward resolution is announced before it surprises anyone (G, H).
- Nothing is written over: an existing project skips, a stray template file refuses (D, E).
- The `new` alias is a true alias, not a near-duplicate (I).
- `--scope formevents` is gone from both the help surface and the parser, and is rejected before any
  project or Dataverse work (J).
- Verbose mode carries tool versions after their check lines, per the verbose rules.

### Not covered

Everything below needs a **write** to a live environment. This round deliberately made none, so these
remain unexercised here.

- **`push` warns which components depend on a web resource** before deleting or removing it.
- **`push` refuses a web resource another solution owns** (breaking change).
- **`push --no-build` no longer pushes a stale plugin package**, and `push` no longer re-uploads an
  unchanged one — both need a real push to observe.
- **`push --no-publish` warns whenever nothing publishes.**
- **Verbose side-by-side snapshot trees** — needs a push to render.
- **`deploy` managed-unpack fix** and **the orphan report's managed-upgrade wording**.
- **Dataverse request timeout reporting** — not reproducible on demand; it needs a slow environment.
- **`[PreImage]` on `CreateMultiple` rejection** and the corrected "entity images not supported"
  message — both are attribute-validation paths covered by the unit suite, not reachable from the CLI
  without a plugin assembly built for the purpose.

**The update notice** was exercised but not observed firing: the local build (`0.16.1-alpha.0.68`) is
ahead of anything published, so a channel-matched comparison correctly prints nothing. The path ran —
`status --verbose --no-cache` forced a fresh check — but a positive result needs a published version
newer than the running one.

---

### Earlier round — deploy missing-component preflight gate

Retained from the 2026-08-09 run against TEST. Same release range; the results still stand.

- **Build:** Release, at commit `4097bdf`
- **Target:** TEST. PROD was never contacted.
- **Import:** never performed — every run used `--dry-run`, which stops before the import.

Runs used `--no-backup --skip-solution-check --skip-dtap-check` so the new gate was the only
pre-import work doing anything, and no environment backup was taken.

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | Target has everything the solution needs | Gate passes, deploy continues | `✓ No missing components.` — one verdict line, deploy proceeded to completion | 0 |
| B | Target missing components | Block before import, name them, write report | Blocked; named 2 components with type, owning solution, and what required each; wrote the report | 15 |
| C | `--skip-component-check` after a block | Gate does not run; its stale report is cleared | No gate line in the output; the target's report removed; a second target's report untouched | 0 |
| D | Malformed required-component list | Fail as "check couldn't run", distinct from a block | `Error: Missing-component check couldn't run against the target (…). Use --skip-component-check to deploy without it.` | 10 |

Case D was not planned — it happened when the platform rejected a first, badly-shaped injection.
It is the more valuable accident: it exercised the "no verdict" path against a live service and
confirmed it is reported and exit-coded separately from a real block.

#### The negative control

Case B is the result worth trusting. Three fabricated dependencies were injected into the
solution source; **two** were reported. The third, `msdyn_iotdevice`, was silently resolved because
TEST genuinely has it.

That is the whole claim of the feature demonstrated in one run: the gate is filtering the
solution's required-component list against the live target, not echoing the file back. A gate that
reported all three would have been indistinguishable from one that reads the file and prints it.

#### Observed output (case B)

```
Checking target for missing components...
Error: Target is missing 2 required components — deploy stopped before import.
  Flowline Probe Absent Entity (flowline_probe_absent_entity) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by Flowline Probe Absent Entity (flowline_probe_absent_entity)
  Flowline Probe Absent Two (flowline_probe_absent_two) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by Flowline Probe Absent Two (flowline_probe_absent_two)
Full list: …\artifacts\missing-components-automatevalue-test-crm4-dynamics-com.txt
Fix it: install the missing solution or application in the target, or remove the dependent component from the solution in DEV and run 'flowline sync'.
Last resort: --skip-component-check deploys without this check.
```

The filename carries the target, and the header names solution, target, and UTC time — both fixes
for the review finding that one report was being shared across every promotion stage.

#### Payload size

The open question was whether a large solution exceeds the inline message limit, forcing teams to
disable the gate permanently. Measured against TEST by padding one real solution with an
incompressible entry, so every zip carried an identical required-component list (12,786 entries)
and only size varied:

| Payload | Duration | Result |
|---|---|---|
| 0.5 MB | 5.7 s | 0 missing |
| 1.5 MB | 6.0 s | 0 missing |
| 8.5 MB | 26.5 s | 0 missing |
| 32.5 MB | 111.7 s | 0 missing |
| 64.5 MB | 216.9 s | 0 missing |

**Nothing was rejected at any size.** The concern was the wrong shape — there is no observed ceiling
up to 64.5 MB, so nothing forces a team onto the skip flag. The real cost is duration: a 64 MB
solution adds over three and a half minutes to every deploy.

It also corrects an assumption the plan carried. Cost was expected to track the number of required
components; it tracks payload size. Every zip above held the same dependency list, and going from
0.5 MB to 8.5 MB still moved the call from ~6s to ~27s.

Two changes followed:

- The failure message no longer claims a large payload "may exceed the inline message limit" — that
  limit was never observed. It names the size and points at duration, which makes a client timeout
  the plausible cause instead.
- Above 8 MB the spinner label carries the size and says the wait runs to minutes, so a slow check
  does not read as a hang.

Still untested at size: whether the check and `pac solution import` diverge somewhere beyond
64.5 MB, and how the check behaves when dependency count and payload size are large independently —
they were never varied separately above 0.5 MB.

#### Not covered by that round

- **False-positive rate.** Every component the gate reported was one that was genuinely absent. No
  case has been observed where it reports something the target actually has, and the gate blocks by
  design, so a false positive is a hard stop.
- **A real import.** All runs were `--dry-run`. The gate's interaction with an actual import — in
  particular whether a blocked import would have failed the way the gate predicts — is untested.
- **Ordering against the solution checker and backup specifically.** Both were skipped to isolate the
  gate. Their relative order is covered by the unit test that resolves the real DI container.

---

### Build configuration matters when testing the CLI

A `FlowlineException` from a pre-import gate first appeared as `Unhandled exception. …` with a full
stack trace, which read like broken error handling. It was not. `Program.cs` calls
`config.PropagateExceptions()` inside `#if DEBUG`, and propagation beats the `SetExceptionHandler`
that renders `Error: <message>` with a typed `ExitCode`. The same commit built `-c Release` produced
the correct output.

**Verify user-facing CLI output from a Release build.** A Debug build makes correct error handling
look broken. This is now recorded in `AGENTS.md` under Build and verification.

Every exit code in both result tables above was read from a Release build for this reason.

### Cleanup

`FlowlineDeployTest` was left exactly as found: clean working tree, no commits, nothing written to
any environment. Every folder used for the `scaffold` cases was a fresh temp directory.

One residue carried from the earlier round: the test project's cached artifact (`artifacts/*.zip` and
its manifest) records a commit that no longer exists. That is gitignored build output, and the next
deploy detects the mismatch and repacks — no action needed.
