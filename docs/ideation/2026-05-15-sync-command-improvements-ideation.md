---
date: 2026-05-15
topic: sync-command-improvements
focus: SyncCommand is mostly a wrapper around pac solution sync. What can we add or improve so that it is more than the default pac solution sync command?
mode: repo-grounded
---

# Ideation: SyncCommand Improvements

## Grounding Context

**Project shape:** C# / .NET CLI, Spectre.Console, CliWrap, NuGet-distributed. `FlowlineCommand<TSettings>` base class with DI, `PacUtils` abstraction, fluent subprocess execution.

**SyncCommand current flow:** validate dev env + solution → ensure `.cdsproj` → resolve `MappingPac.xml` → `pac solution sync --async` → validate exit code → debug build → print success + manual `git commit` nudge.

**Flags:** `[solution]` (auto-selected from `.flowline` if single solution), `--dev <URL>`, `--managed`, `--no-map`.

**Known gaps:** dead `GitCommitChanges` stub at `SyncCommand.cs:97–128`, no diff output, no pre-sync guards, no post-sync metadata. `pac solution sync` silently omits Plugin Packages and requires `-a` for canvas apps. No connection reference / env var settings file generated post-sync.

**Strategy:** PROD as canonical baseline; Git as delivery source; short-lived DEV/TEST provisioned from PROD. Key tracks: Core delivery (v1.0, deadline 2026-06-01), Code asset push, Drift detection + cleanup (post-v1).

**External:** spkl (`increment_on_import` version bump), PACx (ergonomic wrapper), `pac solution create-settings` (generates deployment settings file from solution folder), Terraform plan pattern. Missing dependencies = ~45% of import failures.

## Topic Axes

1. Pre-sync safety — guards/checks before touching local files
2. Sync fidelity — completeness of what gets synced
3. Change visibility — surfacing what changed / diff preview
4. Post-sync automation — build validation, auto-commit, settings scaffolding
5. Workflow integration — CI/CD fit, multi-solution, rollback

## Ranked Ideas

### 1. Pre-sync dirty-tree guard + git stash

**Description:** Before calling `pac solution sync`, check `git status --porcelain` scoped to `solutions/<Name>/`. If uncommitted changes exist, abort and print which files would be clobbered. Offer to stash automatically, or require `--force` to bypass. `GitUtils.IsRepoCleanAsync` is already in the codebase. DeployCommand already calls `GitUtils.AssertRepoCleanAsync` as a hard gate — sync should match.

**Axis:** Pre-sync safety

**Basis:** `direct:` `SyncCommand.cs:97–128` — `GitCommitChanges` stub contains `git status --porcelain` logic but it runs post-sync, never pre. Pre-sync guard is absent. `direct:` `DeployCommand.cs:38` — `AssertRepoCleanAsync` already used as a gate elsewhere; `GitUtils.IsRepoCleanAsync` is reusable.

**Rationale:** `pac solution sync` overwrites local XML files in-place with no warning. A developer who has half-edited a form, then runs sync to pick up a colleague's change, loses that work silently. Git can recover it — but only if they think to look, and only before the next commit. The guard is one git call using an existing method.

**Downsides:** Adds a gate that interrupts fast iteration. Mitigated by `--force` bypass or opt-in stash prompt.

**Confidence:** 95%
**Complexity:** Low
**Status:** Unexplored

---

### 2. Silent PAC omission warning

**Description:** After sync completes, check whether the unpacked `src/` contains `PluginPackages/` entries or canvas app components. If present, print a yellow warning: `Plugin Packages are not downloaded by pac solution sync — sync these separately`. Same pattern for canvas apps. Post-hoc filesystem scan — no extra API calls.

**Axis:** Sync fidelity

**Basis:** `direct:` grounding — `pac solution sync` silently skips Plugin Packages (open bug) and canvas apps (requires `-a` flag never passed). `direct:` `SyncCommand.cs:84` — single green success line, no compensating warnings. The green success is misleading when the sync was partial.

**Rationale:** pac returns exit code 0 even when it silently omitted components. Flowline prints success. Developer commits, deploys, discovers the gap in Test. A warning converts a hidden trap into an actionable message at the right moment. Cost: filesystem check, ~10 lines.

**Downsides:** Warning fatigue if the solution always contains plugin packages. Mitigated by only warning when the condition is actually present.

**Confidence:** 95%
**Complexity:** Low
**Status:** Unexplored

---

### 3. Post-sync change summary

**Description:** After `pac solution sync` completes but before the build step, run `git diff --stat -- solutions/<Name>/src/` and print a compact summary: `12 files changed, +47 −23`. For a no-change sync, print `No changes pulled from DEV` and suppress the "Run git commit" nudge (which is misleading on a no-op). 5-frame convergence — strongest signal in the candidate set.

**Axis:** Change visibility

**Basis:** `direct:` `SyncCommand.cs:84` — "Solution synced from Dataverse in 4.2s" is the entire output. A sync that reformatted 400 XML files is indistinguishable from one that changed 3 form fields. `external:` Terraform plan pattern — surfacing change scope at execution time is standard tooling practice.

**Rationale:** Without a count, developers have no signal that sync actually pulled anything new. They must leave the terminal and run `git diff` manually. The summary also doubles as no-op detection: if `+0 −0`, the commit nudge doesn't apply and the build step could be skipped. Zero extra API calls; pure git.

**Downsides:** None significant. `git diff --stat` is fast.

**Confidence:** 90%
**Complexity:** Low
**Status:** Unexplored

---

### 4. Post-sync metadata stamp (`.flowline-sync`)

**Description:** After a successful sync, write a small JSON file `.flowline-sync` into `solutions/<Name>/` recording: timestamp, dev environment URL, solution version (from `src/Other/Solution.xml`), pac CLI version, and changed-file count. Gets committed alongside the sync. Future commands (deploy, drift detection, sync-before-push gate) can read it without hitting Dataverse.

**Axis:** Post-sync automation

**Basis:** `direct:` `SyncCommand.cs:84–91` — success declared, zero metadata persisted. `direct:` `STRATEGY.md` — "Drift detection + component cleanup" is the explicit post-v1 track. Writing this file now seeds that track at negligible cost.

**Rationale:** Sync is the only moment the tool knows the exact Dataverse state that produced the local files. That knowledge evaporates unless captured. The stamp enables: stale-build warnings in deploy, drift detection without a second Dataverse call, and the sync-before-push gate (idea #8). The file is small, deterministic, and meaningful to version control.

**Downsides:** Adds one committed file per solution. The URL field may be sensitive in public repos — could mask it or make the URL opt-out.

**Confidence:** 85%
**Complexity:** Low
**Status:** Unexplored

---

### 5. Auto-generate settings stub via `pac solution create-settings`

**Description:** After sync, call `pac solution create-settings --solution-folder solutions/<Name>/ --settings-file solutions/<Name>/settings.template.json` to generate a deployment settings file with connection reference and environment variable placeholders. Skip if the file already exists and is complete. No XML parsing needed — pac does the work.

**Axis:** Post-sync automation

**Basis:** `direct:` grounding — "No connection reference/env var settings file generated post-sync" named explicitly as a known gap. `external:` `pac solution create-settings` exists in the pac CLI and generates the file in the `pac solution import --settings-file` schema. `external:` missing dependencies account for ~45% of deployment failures; connection references and env vars are the primary culprit.

**Rationale:** Every developer who syncs a solution with connection references hits the same invisible setup step before they can deploy. The answer is in the solution Flowline just unpacked — but nothing surfaces it. `pac solution create-settings` already knows the schema; Flowline just needs to call it. This directly enables the "full workflow usage" metric from STRATEGY.md.

**Downsides:** Adds a pac CLI call post-sync (small latency). `settings.template.json` must not be confused with real secrets — naming and gitignore strategy to decide.

**Confidence:** 85%
**Complexity:** Low (pac does the work)
**Status:** Unexplored

---

### 6. `--dry-run` flag

**Description:** Add `--dry-run` to SyncCommand. Instead of calling `pac solution sync`, export the solution to a temp directory using `pac solution export` + unpack, diff the temp against `solutions/<Name>/src/`, print a component-level diff summary, then discard the temp. Local files are never touched. Pairs with idea #1: dry-run first to see what's coming, then sync safely.

**Axis:** Change visibility

**Basis:** `reasoned:` If sync were instant, developers would run it speculatively before every commit to check for drift. The cost preventing that today is "I don't want to overwrite local state just to look." Dry-run makes the peek free. Same structural pattern as `terraform plan`, `git fetch` vs `git pull`, `apt-get --simulate`.

**Rationale:** Useful pre-commit signal: confirm DEV hasn't drifted before pushing. Useful in CI to detect drift without triggering a write. Useful for onboarding — see what's in DEV before committing to sync. Enables the "safe exploration" workflow.

**Downsides:** `pac solution export` + unpack has its own failure modes. Adds a code path that needs separate maintenance. Medium complexity.

**Confidence:** 75%
**Complexity:** Medium
**Status:** Unexplored

---

### 7. `--bump-version` flag (opt-in version increment on sync)

**Description:** Add an opt-in `--bump-version` flag. When set, after sync completes, read the solution version from `src/Other/Solution.xml`, increment the patch segment, and write it back before the build step. Prevents "solution version already installed" import rejections on deploy. spkl-validated pattern (`increment_on_import`).

**Axis:** Post-sync automation

**Basis:** `external:` spkl's `increment_on_import` — version bumping before import is a documented pain point addressed by spkl and DAXIF. `reasoned:` `pac solution sync` does NOT bump the version — it downloads state as-is from Dataverse. Without a bump, repeated syncs + deploys of the same component changes can hit version collision errors at import time.

**Rationale:** Every sync produces a local snapshot intended for deployment. A bump ensures each snapshot has a unique, ordered version number. Opt-in (`--bump-version`) avoids changing default behavior and keeps the synced `Solution.xml` version aligned with Dataverse by default — the bump is a deliberate "mark as ready for deploy" signal.

**Downsides:** Creates a version mismatch between Dataverse and local (Dataverse has 1.0.3, local has 1.0.4 after bump). This is intentional but can confuse version comparisons. Bump-on-deploy (in PushCommand) may be a cleaner home long-term — flag here for now.

**Confidence:** 70%
**Complexity:** Low
**Status:** Unexplored

---

### 8. Sync-before-push gate (conditional on idea #4)

**Description:** In PushCommand, before pushing local state to Dataverse, read the `.flowline-sync` metadata file (idea #4). If any files in `solutions/<Name>/src/` were committed after the last sync timestamp, warn: `Local changes committed since last sync — DEV may have newer state you'd overwrite. Run 'flowline sync' first or pass --force.` Requires idea #4 to ship first; detection is trivial once the stamp exists.

**Axis:** Workflow integration

**Basis:** `reasoned:` STRATEGY.md — "PROD as canonical baseline; Git as delivery source." The sync-before-push invariant protects this: pushing without a recent sync risks overwriting DEV changes that were never captured in git. Detection via `.flowline-sync` timestamp vs. `git log --after` on the solution folder: if commits exist after the last sync, the developer may have diverged from DEV.

**Rationale:** The most common multi-person data loss scenario: developer A syncs, developer B makes a change in DEV, developer A pushes without re-syncing — B's change is gone. The gate doesn't prevent this by force but makes the risk visible at the moment it can still be fixed. Depends on idea #4 for the timestamp; without it, detection requires a Dataverse API call.

**Downsides:** Cross-command dependency on idea #4. PushCommand touches different scope than SyncCommand — may be out of scope for a SyncCommand ideation cycle. `--force` bypass required for deliberate overwrite scenarios.

**Confidence:** 70%
**Complexity:** Low (if #4 ships), Medium (if detecting via Dataverse API instead)
**Status:** Unexplored

---

### 9. `--check` flag: post-sync Solution Checker (`pac solution check`)

**Description:** Add an opt-in `--check` flag. After sync completes, export the solution to a temp zip and invoke `pac solution check --path <temp.zip>`. Parse and display violations grouped by severity (Critical / High / Medium / Low / Informational) with a summary line: `3 critical, 7 high, 12 medium`. On clean: `Solution Checker: no violations`. Discard the temp zip. Default off — the cloud call can take 1–5 minutes for complex solutions.

**Axis:** Post-sync automation

**Basis:** `direct:` grounding — sync is the moment the solution is freshest from Dataverse; checker results are most meaningful here before the developer commits and raises a PR. `external:` `pac solution check --path <solution.zip>` exists in the pac CLI and calls Microsoft's cloud analysis service — no custom parsing of Solution.xml required.

**Rationale:** Solution Checker catches deprecated API usage, performance anti-patterns, and supportability violations that the build step cannot detect. These issues are expensive to find in production. Surfacing them immediately post-sync, while the developer is still in the sync workflow, converts a future "why did AppSource reject this?" or "why is this slow?" into an actionable message with file-level detail. Opt-in via `--check` avoids imposing cloud latency on every sync.

**Downsides:** Requires an export step (pac solution export → temp zip) before the check. Cloud call latency (1–5 min) makes it unsuitable as default behavior. Results may be noisy for solutions inherited from third parties. Requires an active pac auth context with permissions to call the checker service.

**Confidence:** 70%
**Complexity:** Medium (cloud call + export step)
**Status:** Unexplored

---

## Rejection Summary

| # | Idea | Reason Rejected |
|---|------|-----------------|
| 1 | cwd-inferred solution argument | Already solved — `.flowline` auto-selects the solution when only one exists |
| 2 | MappingPac.xml convention derivation | Complex; "convention-compliant" definition needs a dedicated brainstorm |
| 3 | Version alignment warning (DEV vs local) | Narrow; solution version shown in change summary + metadata stamp covers most cases |
| 4 | --no-map divergence warning | Too niche; below ideation ambition floor |
| 5 | Pre-sync API manifest compare | Expensive (Dataverse call before every sync); simpler guards cover the failure class |
| 6 | pac exit code fidelity gap (API verify) | Too expensive; duplicate failure class covered by PAC omission warning |
| 7 | Partial sync / component-count regression | Overlaps with post-sync change summary; not additive |
| 8 | Post-sync XML schema validation | Covered by existing debug build; malformed pac output is very rare |
| 9 | Build failure better diagnosis | Medium complexity, low v1.0 priority; change summary already narrows the diagnostic gap |
| 10 | --async feedback / timeout warning | Constrained by pac's async model — pac doesn't stream job status |
| 11 | No-op detection (skip build when nothing changed) | Trivial wire-up already in `GitCommitChanges:113`; below ideation ambition floor |
| 12 | Sync direction reframe (DEV as ephemeral) | Strategic architectural insight — belongs in ce-brainstorm or STRATEGY.md |
| 13 | Solution file as wrong unit | pac already unpacks to component-level files; not actionable as SyncCommand change |
| 14 | Flowline as native Git enhancement layer | Strategic positioning — better handled in STRATEGY.md |
| 15 | Event-triggered sync | Scope overrun; architectural; post-v1 |
