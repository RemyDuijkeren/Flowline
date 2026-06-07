---
date: 2026-06-07
topic: dtap-gate-deploy
status: ready-for-planning
---

# Requirements: DTAP Gate in DeployCommand

## Summary

Before packing, `DeployCommand` checks that the solution has been promoted through the nearest configured predecessor environment in the DTAP chain. Two checks run in order: existence (solution must be installed in the predecessor) and version (predecessor must have ≥ the version being deployed). Both checks are bypassable with `--skip-dtap-check`. A visible skip notice prints when the flag is used and a check would have blocked.

---

## Problem Frame

`DeployCommand` currently deploys to any target environment unconditionally. Nothing prevents a developer from deploying directly to Prod without the solution ever reaching UAT or Test first. When that happens, Prod gets code the team hasn't validated in a staging context — and the process violation is invisible until something breaks in production.

The config already declares the DTAP topology via `ProdUrl`, `UatUrl`, `TestUrl`, and `DevUrl`. The data needed to enforce DTAP order is available. Without automated enforcement, the gate is a convention that breaks silently under deadline pressure or accident.

---

## Requirements

**Tier resolution**

- R1. Before running gate checks, resolve the target tier by matching the target URL against all configured URLs (`ProdUrl`, `UatUrl`, `TestUrl`, `DevUrl`) — this applies to both named aliases (`prod`, `uat`, `test`) and raw URLs.
- R2. If the resolved tier is Dev, block immediately with a non-bypassable error. `DeployCommand` is not for development environments — the sync workflow is the correct path for Dev.
- R3. If the target URL does not match any configured URL, skip all gate checks and deploy normally.
- R4. Find the predecessor env to check: the highest configured tier that is strictly below the target tier. DTAP order: Dev(0) < Test(1) < UAT(2) < Prod(3). If multiple tiers below the target are configured, check the highest one. If none are configured below the target, skip all gate checks.

**Existence check**

- R5. If the solution does not exist in the predecessor env (null result from `GetSolutionInfoAsync`), block with a clear error. Error is bypassable with `--skip-dtap-check`.

**Version check**

- R6. If the solution exists in the predecessor env, compare the predecessor's version to the local solution version. If `predecessorVersion < localVersion`, block with a clear error. Error is bypassable with `--skip-dtap-check`.
- R7. If the local solution version cannot be read (XML parsing failure or version not set), block with a non-bypassable error. This is a source integrity failure, not a DTAP skip.
- R8. If the local solution version can be read and `predecessorVersion >= localVersion`, the version check passes — proceed.

**Bypass**

- R9. `--skip-dtap-check` is a `DeployCommand`-specific flag. When set, all gate checks (R5, R6) are skipped.
- R10. When `--skip-dtap-check` is set and at least one check would have blocked, print a dim skip notice identifying which predecessor env was skipped and which check would have fired.
- R11. `--skip-dtap-check` does not bypass R2 (Dev block) or R7 (local version unreadable). Both are source/topology errors, not DTAP gates.

**Placement**

- R12. Gate checks run pre-pack, after the managed/unmanaged type guard (already implemented), before the drift check and pack step.

---

## Acceptance Examples

- AE1. **Covers R1, R4.** Given `ProdUrl` and `UatUrl` are configured, when `flowline deploy prod`, tier = Prod, predecessor = UAT → gate checks run against UAT.
- AE2. **Covers R1, R4.** Given `ProdUrl` and `TestUrl` are configured (no `UatUrl`), when `flowline deploy prod`, predecessor = Test (nearest configured below Prod) → gate checks run against Test.
- AE3. **Covers R1, R4.** Given `ProdUrl` and `DevUrl` are configured (no `UatUrl`, no `TestUrl`), when `flowline deploy prod`, predecessor = Dev → gate checks run against Dev.
- AE4. **Covers R2.** Given `DevUrl` is configured, when `flowline deploy dev`, blocks immediately with non-bypassable error: `"Dev is a development environment — use 'sync' to push changes there, not 'deploy'."` Even `--skip-dtap-check` does not bypass this.
- AE5. **Covers R3.** Given target is a raw URL that does not match any configured URL, gate checks are skipped and deploy proceeds.
- AE6. **Covers R1, R4.** Given `ProdUrl = https://org.crm.dynamics.com/`, when `flowline deploy https://org.crm.dynamics.com/`, tier = Prod → gate checks run (raw URL matched to ProdUrl).
- AE7. **Covers R5.** Given `ProdUrl` and `UatUrl` set, solution does not exist in UAT, when `flowline deploy prod`, blocks with: `"'MySolution' hasn't been deployed to UAT yet — promote there first, or use --skip-dtap-check."`
- AE8. **Covers R6.** Given `ProdUrl` and `UatUrl` set, solution in UAT is v1.1, local version is v1.2, when `flowline deploy prod`, blocks with: `"'MySolution' in UAT is v1.1 — promote v1.2 there first, or use --skip-dtap-check."`
- AE9. **Covers R8.** Given predecessor has v1.2 and local version is v1.2 (same), version check passes.
- AE10. **Covers R8.** Given predecessor has v1.3 and local version is v1.2 (predecessor is ahead), version check passes.
- AE11. **Covers R9, R10.** Given gate would have blocked (AE7 scenario), when `flowline deploy prod --skip-dtap-check`, blocks are skipped and a dim notice prints: `"Skipping DTAP gate — 'MySolution' not found in UAT."`.
- AE12. **Covers R7.** Given local solution version cannot be read, when `flowline deploy prod`, blocks with a non-bypassable error regardless of `--skip-dtap-check`.

---

## Success Criteria

- Deploying directly to Prod when UAT has never seen the solution is blocked with a clear, actionable error.
- Deploying to Prod when UAT is on an older version is blocked with the specific version numbers shown.
- A developer with a legitimate hotfix can deploy via `--skip-dtap-check` with a visible skip notice confirming what was bypassed.
- Deploying to Dev is always blocked — `sync` is the correct workflow for Dev environments.
- No gate runs when the target URL is outside the configured DTAP topology (unrecognised raw URL).
- Planning does not need to invent gate semantics, tier resolution rules, bypass behavior, or version comparison direction.

---

## Scope Boundaries

- Dev deployments are blocked entirely — non-bypassable, not gated.
- No non-bypassable DTAP block — all DTAP checks except R6 (source integrity) are bypassable.
- No required reason string for `--skip-dtap-check` — the flag alone is sufficient.
- No audit trail or deployment ledger — skip notices appear in the terminal session only, not persisted.
- No changes to `ProvisionCommand`, `SyncCommand`, or any other command.
- The managed/unmanaged type guard (idea #1, already implemented) is not changed by this feature.

---

## Key Decisions

- **Nearest configured predecessor, not immediate predecessor**: Avoids breaking teams that skip tiers in their config. If UAT isn't configured, Prod checks Test. This is more useful than requiring a complete DTAP chain.
- **Both checks bypassable with one flag**: A single `--skip-dtap-check` bypasses both existence and version checks. Two separate flags (`--skip-existence-check`, `--skip-version-check`) add surface with little added value for a solo developer workflow.
- **No reason string required**: The ideation doc proposed a required reason string; dropped in favour of simplicity. The skip notice in the terminal provides auditable context without requiring user input.
- **Version unreadable = hard error**: If the local solution version can't be parsed, the problem is in the source, not in the DTAP flow. This is not bypassable via `--skip-dtap-check`.
- **`predecessorVersion >= localVersion` passes**: Same-version redeploys and cases where predecessor is ahead both pass cleanly. Only strict downgrade (predecessor behind) blocks.
- **Dev is blocked, not gated**: Dev environments are for direct Dataverse work via `sync`. Deploying a packed solution to Dev is a process error — non-bypassable, same pattern as blocking a Production environment overwrite in `ProvisionCommand`.

---

## Dependencies / Assumptions

- `FlowlineValidator.GetSolutionInfoAsync` is used to fetch the predecessor env's `SolutionInfo` (including `VersionNumber`). The 4-hour cache covers repeated invocations within the same session.
- The local solution version must be parseable from the solution source. The exact source file and parsing mechanism are deferred to planning.
- `SolutionInfo.VersionNumber` from PAC CLI is a string (e.g., `"1.2.0.0"`). Version comparison requires parsing into a structured version type; deferred to planning.

---

## Outstanding Questions

### Deferred to Planning

- [Affects R5, R6][Technical] Which file in the solution source contains the version number (e.g., `solutions/<Name>/src/Other/Solution.xml`)? What XML element holds it?
- [Affects R5][Technical] How should version strings (`"1.2.0.0"`) be compared — `System.Version`, lexicographic, or semver? Verify that Dataverse version strings are always 4-part.
- [Affects R9][Technical] What exactly constitutes a "dim skip notice" when both existence and version checks would have blocked — one combined notice or two separate lines?

---

## Key References

- `src/Flowline/Commands/DeployCommand.cs` — insertion point after managed/unmanaged type guard, before drift check
- `src/Flowline/Config/ProjectConfig.cs` — ProdUrl, UatUrl, TestUrl, DevUrl (all nullable)
- `src/Flowline/Validation/FlowlineValidator.cs:107` — `GetSolutionInfoAsync` for predecessor version and existence
- `docs/ideation/2026-06-07-deploy-command-ideation.md` — idea #5
