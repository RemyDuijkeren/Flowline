---
date: 2026-06-07
topic: deploy-command
focus: Make DeployCommand production-quality — managed/unmanaged safety, DTAP flow enforcement, versioning, drift detection, process state preservation
mode: repo-grounded
---

# Ideation: DeployCommand

## Grounding Context

**Codebase:** C# .NET 10 CLI, Spectre.Console.Cli. Config (`.flowline`): ProdUrl, UatUrl, TestUrl, DevUrl (optional; undefined = alias won't resolve). DTAP: Dev → Test → UAT → Prod.

**Available but unused in DeployCommand:**
- `SolutionInfo.IsManaged`, `SolutionInfo.VersionNumber` — fetchable via `FlowlineValidator.GetSolutionInfoAsync`
- `EnvironmentInfo.Type` ("Production" / "Sandbox") — fetched as `targetEnv` but never read

**Current prototype gaps:**
1. No DTAP order enforcement — can deploy directly to prod
2. No pre-flight check: target env managed vs unmanaged state
3. No version check — `--skip-lower-version` defaults false in pac (silent no-op or platform block)
4. No prod confirmation
5. No version bump step before deploy
6. `--force` undefined scope (`DeployCommand.cs:87` uses it for drift, help text says "skip confirmation prompts")
7. No upgrade vs update distinction (pac defaults to merge-only; removed components accumulate)
8. No connection reference / env variable drift detection
9. No deployment history / audit trail
10. No rollback mechanism
11. `pac solution import --async` used without `--wait` — `Console.Done("Deployed!")` fires immediately after job submission, not completion

**Past learnings applied:**
- Provision safety guard pattern: non-bypassable even with `--force` — reused in idea #1
- Sync-first ALM: pack from `src/` — already correct in DeployCommand
- Version+sync ordering: write version first, sync second

**External context:**
- Dataverse blocks version downgrades at the API level — so the footgun is pac `--skip-lower-version true` silently no-op-ing (exit 0, nothing imported)
- Update = merge-only (no component deletion). Upgrade = holding solution + "Apply Upgrade" deletes removed components
- Managed import over unmanaged → "already installed as unmanaged" error, environment left undefined
- Configuration drift (conn refs, env vars) = #1 post-deploy breakage cause per community postmortems
- Managed solution import resets process enabled/disabled state — common cause of disabled cloud flows post-deploy

## Topic Axes

1. Safety guards — preventing irreversible or incorrect operations
2. DTAP flow enforcement — correct sequencing, preventing environment skips
3. Version management — validation, mode derivation, silent-skip detection
4. Deployment mode — managed/unmanaged semantics, upgrade vs update, orphan cleanup
5. Configuration drift — connection refs, env vars, process/workflow state

## Ranked Ideas

### 1. Pre-flight managed/unmanaged guard (non-bypassable)
**Axis:** Safety guards
**Basis:** `direct:` `SolutionInfo.IsManaged` returned by `FlowlineValidator.GetSolutionInfoAsync` (`FlowlineValidator.cs:107`). `ProvisionCommand.FindProblematicSolutions` (`ProvisionCommand.cs:178`) implements the same pattern. Past learning: guard is non-bypassable even with `--force` — data loss is permanent.
**Description:** Before any import, fetch `SolutionInfo` from the target env. If the solution is installed as unmanaged and the deploy is managed (or target is a Production type that shouldn't receive unmanaged): hard block with a clear message. No `--force` bypass — overwriting unmanaged with managed is irreversible. If the solution doesn't exist yet in the target, allow.
**Rationale:** "Already installed as unmanaged" from pac leaves the environment in an undefined state with no recovery short of portal intervention. The data to prevent it is already in the model and cached.
**Downsides:** Extra API call per deploy (mitigated by 4-hour `FlowlineValidator` cache). Must handle "solution not yet in target" as a valid allow case.
**Confidence:** 95% | **Complexity:** Low | **Status:** Explored

---

### 3. Replace `--force` with named scoped bypass flags
**Axis:** Safety guards
**Basis:** `direct:` `FlowlineSettings.Force` description is "Skip confirmation prompts" but `DeployCommand.cs:87` uses it to skip the drift check — not the same thing. Past learning: managed/unmanaged guard must be non-bypassable "even with --force" — this already contradicts a monolithic `--force` design.
**Description:** Remove global `--force`. Replace with explicit named flags: `--skip-drift-check`, `--allow-downgrade`, `--skip-dtap-check`. CI pipelines get `--non-interactive` to skip confirmation prompts while keeping all safety guards active. The managed/unmanaged guard (#1) and prod type enforcement remain non-bypassable regardless of any flag.
**Rationale:** Named flags make each bypass conscious, auditable, and documentable. A single `--force` in CI bypasses undefined checks. The non-bypassable guards become structurally impossible to violate, not conventionally enforced.
**Downsides:** Breaking change — scripts using `--force` must migrate. Adds CLI surface. Needs a deprecation path.
**Confidence:** 90% | **Complexity:** Low | **Status:** Unexplored

---

### 4. Pre-flight version check: fail-fast + derive upgrade/update mode from delta
**Axis:** Version management
**Basis:** `direct:` `SolutionInfo.VersionNumber` in `PacUtils.cs:455`, fetchable, unused by `DeployCommand`. `external:` Dataverse blocks downgrades at the API level, but pac `--skip-lower-version true` silently returns exit 0 with nothing imported — looks like a successful deploy.
**Description:** Before packing, fetch the target env's current solution version. Three cases: (a) incoming < target → fail fast with clear message before wasting pack time; (b) incoming = target → warn "nothing will change — bump version first"; (c) incoming > target → proceed using upgrade mode (`--import-as-holding` + apply upgrade) so removed components are deleted, not silently accumulated. This collapses version validation and import mode selection into one decision.
**Rationale:** Silent no-ops (case b/a) make deploys look successful when nothing changed or when the platform blocked the import. Upgrade mode for case (c) ensures the environment stays clean — removes zombie components that accumulate with merge-only imports.
**Downsides:** Upgrade mode (holding solution) allows only one pending upgrade per solution at a time. `Apply Upgrade` is a second async step. Must handle "solution doesn't exist in target yet" as a valid first-deploy case.
**Confidence:** 90% | **Complexity:** Medium | **Status:** Needs Verification — test how pac/Dataverse handles same-version reimport and older-version import (does Dataverse block at API level, silent no-op, or error?) before finalizing scope of this idea

---

### 5. DTAP gate: verify predecessor env has ≥ version before promoting
**Axis:** DTAP flow enforcement
**Basis:** `direct:` `EnvironmentInfo.Type` identifies Production vs Sandbox. `GetSolutionInfoAsync` queryable on any URL. `Config.UatUrl`, `Config.TestUrl` encode the DTAP topology. Gap #1 in grounding: no DTAP order enforcement.
**Description:** When `targetEnv.Type == "Production"`: fetch solution version from `UatUrl` and verify it's ≥ the incoming version. When deploying to UAT: check `TestUrl` if configured. When a predecessor URL is not configured: skip that tier's check (absence = team doesn't use that stage). Block with a named escape hatch: `--skip-dtap-check` with a required reason string.
**Rationale:** DTAP is meaningless if the gate is optional. The config already declares the topology — enforcing order requires reading what's already there and two extra cacheable API calls.
**Downsides:** Hotfix-to-prod workflows are blocked without `--skip-dtap-check`. "Version must match" is strict — patch/hotfix versioning may violate the check legitimately.
**Confidence:** 85% | **Complexity:** Medium | **Status:** Unexplored

---

### 6. `--dry-run`: export target → unpack to temp → diff vs local `src/`
**Axis:** Safety guards / DTAP flow enforcement
**Basis:** `reasoned:` No tool gives developers a "what will change" view before importing — this is the most-requested DevOps primitive (terraform plan, db migrate --dry-run, git diff). `direct:` DriftChecker already does SHA-256 comparison on web resources and plugin sizes — the diff infrastructure exists. New step: `pac solution export` + `pac solution unpack` on the target env to a temp folder, then compare with local `src/`.
**Description:** `flowline deploy <target> --dry-run` exports the solution currently in the target environment, unpacks it to a temp folder, and runs a component-level diff against the local `src/`. Shows added/modified/removed components before any import runs. No writes to the target. Exit 0 with a diff report.
**Rationale:** Critical for prod deploys where the developer wants to verify exactly what will change before committing. Also useful as a DTAP check: see what differs between UAT and the package about to go to prod.
**Downsides:** Export + unpack is 2 extra pac CLI calls (network, time). Comparison quality depends on unpack output format being stable. Temp folder cleanup needed. Medium-High complexity.
**Confidence:** 85% | **Complexity:** Medium-High | **Status:** Unexplored

---

### 7. `--prune`: report orphaned components in unmanaged solutions, remove safe ones
**Axis:** Deployment mode
**Basis:** `reasoned:` pac solution import is additive-only for unmanaged — removed components (plugin steps, views, flows, columns) accumulate silently. Managed solutions auto-clean via upgrade mode; unmanaged teams have no equivalent. Component-type-aware safety classification: plugin steps, SDK message processing steps, processes/flows → safe to remove. Columns, relationships, tables with data → report only (data loss risk).
**Description:** After resolving what's in the target vs. what's in the source, identify orphaned components not present in the local solution. With `--prune`: auto-remove components classified as safe (plugin steps, flows, custom views). Report-only (never auto-remove) for data-bearing components. Always shows the full orphan list before acting, requires confirmation unless `--non-interactive`.
**Rationale:** Unmanaged solution maintenance currently requires manual portal cleanup. Accumulated orphans inflate solution size, confuse debugging, and cause deployment conflicts over time.
**Downsides:** High complexity — component type safety classification must be correct or causes harm. Requires querying component details from the target env. The safety boundary for each component type must be explicitly defined and tested.
**Confidence:** 80% | **Complexity:** High | **Status:** Unexplored

---

### 8. Process/workflow state snapshot: save pre-deploy, restore post-deploy
**Axis:** Configuration drift
**Basis:** `external:` Well-documented Power Platform community problem — managed solution import sets processes to their imported state (often disabled), overwriting the runtime enabled/disabled customization applied in that environment. No tool currently handles automatic state restoration.
**Description:** Before import: query all processes/cloud flows in the solution and record their `statecode`/`statuscode`. After import completes: re-query, compare, and restore any that changed state (enabling what was enabled before, disabling what was disabled before). Output a diff: "3 flows re-enabled after deploy." Optionally skip with `--no-restore-state`.
**Rationale:** A deploy that silently disables active production flows is invisible until users report broken automations. The fix is manual portal work. Automatic state restoration makes deploys idempotent with respect to process state.
**Downsides:** Requires Dataverse API calls (not pac CLI) to query process state. Post-deploy state changes have a race window — flows may have just started running. Must decide what "same state" means if a flow was transitioning during deploy.
**Confidence:** 80% | **Complexity:** Medium | **Status:** Unexplored

---

### 9. Configuration drift pre-flight: connection refs + env vars
**Axis:** Configuration drift
**Basis:** `external:` Configuration drift (connection references, environment variables) is the #1 post-deploy breakage cause per Power Platform community postmortems. `direct:` `DriftChecker.cs` implements the diff/warning pattern — the approach is reusable for config items.
**Description:** Before import, diff the solution's declared connection references and environment variables against what's bound in the target environment. Unbound refs = warning (sandbox) or block (Production). Extend the DriftChecker pattern to cover config items in addition to files.
**Rationale:** Solutions that work perfectly in UAT break silently in prod when connection refs point to UAT endpoints. Surfacing it before import gives the developer a choice, not a post-deploy surprise.
**Downsides:** High complexity — requires parsing connection refs from the solution zip or querying the Dataverse API, matching against target env state. Consider shipping as a separate `flowline check <env>` command first before integrating into the deploy flow.
**Confidence:** 75% | **Complexity:** High | **Status:** Unexplored

## Rejection Summary

| # | Idea | Reason Rejected |
|---|------|-----------------|
| - | Fix async import wait + structured failure | Wrong assumption — `pac solution import --async` already polls and waits internally; exit code reflects actual completion |
| - | Auto-derive package type from `EnvironmentInfo.Type` | Wrong model — managed/unmanaged is a team-level ALM strategy, not derivable from env type; sandbox environments can be managed (test, UAT, demo) depending on team choice |
| - | No-URL hard abort at parse time | Below floor — obvious code fix, not a team-discussion idea |
| - | Prod confirmation prompt (standalone) | Subsumed by #3 (named flags handles CI distinction) and #4 (env type enforcement catches misconfigured aliases) |
| - | `--non-interactive` as standalone idea | Subsumed by #3 — CI gets `--non-interactive` in the named-flags design |
| - | Immutable artifact promotion (build-once) | Scope overrun — restructures CI pipeline assumptions and artifact storage |
| - | Deployment ledger (audit trail + DTAP) | High leverage but infrastructure-level — warrants its own dedicated brainstorm |
| - | Auto-version bump as deploy prerequisite | Nice to have, lower priority than safety/correctness gaps |
| - | Component delta manifest (solution weight) | Informational; partially covered by `--dry-run` (#7) |
| - | Default-to-upgrade (as a separate flag) | Subsumed by #5 — version delta drives the mode decision more cleanly than a flag |
| - | Detect unmanaged layers via `pac list-layers` | Complex external tool call; partially covered by #1 |
| - | Escrow (pack-then-hold-then-import) | Internal refactoring, low user-visible safety value vs other ideas |
| - | Version freshness decay / UAT proof expiry | Niche, adds timer state, limited practical adoption |
| - | Environment lock (LOTO) for concurrent deploys | Niche, complex, low priority for solo/small teams |
| - | Promote-gate prompt between every DTAP tier | Subsumed by #6 (gate does the check); UX is part of #3's design space |
| - | Configurable environment risk tiers | Good direction but overlaps #3+#6; better addressed in a dedicated brainstorm |
