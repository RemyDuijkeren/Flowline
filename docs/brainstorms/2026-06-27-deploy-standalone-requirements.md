---
date: 2026-06-27
topic: deploy-standalone
---

# Deploy Standalone Mode

## Summary

Add standalone mode to `flowline deploy`: deploy a pre-built solution zip to an environment without a Flowline project. Triggered implicitly by `--solutionFile <path>`, mirroring the pattern already established by `push` and `generate`.

---

## Key Decisions

**Implicit trigger, not a flag** — standalone mode activates when `--solutionFile` is set, not via an explicit `--standalone` flag. Mirrors how `push` treats `--pluginFile` and how `generate` treats the absence of `.flowline`.

**Auto-extract solution name** — the solution name is read from `Solution.xml` inside the zip, so the user only needs `--solutionFile` and `<target>`. `--solution <name>` remains available as an explicit override.

**DTAP auto-skipped** — DTAP promotion gate is silently bypassed in standalone; no flag is needed to opt out. Manual one-off deploys have no project config to gate against, and requiring `--skip-dtap-check` adds friction without value.

**Mutual exclusion enforced** — `--solutionFile` errors when `.flowline` is detected in the working directory, keeping project mode and standalone mode cleanly separate. Mirrors the guard in `push` (lines 293–296 of `src/Flowline/Commands/PushCommand.cs`).

---

## Requirements

**Trigger and detection**

R1. `flowline deploy <target> --solutionFile <path>` activates standalone mode.
R2. If `--solutionFile` is set and a `.flowline` config is detected, the command exits with an actionable error.

**Skipped checks**

R3. Git clean-state check does not run in standalone mode.
R4. DTAP promotion gate does not run in standalone mode.

**Solution name resolution**

R5. In standalone mode, the solution name is extracted from `Solution.xml` inside the zip.
R6. If `--solution <name>` is also set, it overrides the extracted name.
R7. If the zip contains no valid `Solution.xml` and no `--solution` override is provided, the command exits with an error that names the missing file and suggests `--solution <name>` as a workaround.

**Orphan cleanup**

R8. Pre- and post-import orphan cleanup runs in standalone mode, identical to normal mode behavior.

**Existing flags in standalone**

R9. `--managed` applies in standalone mode.
R10. `--no-delete` applies in standalone mode.

---

## Key Flows

- F1. Standalone deploy (happy path)
  - **Trigger:** `flowline deploy <env> --solutionFile <path.zip>` with no `.flowline` present
  - **Steps:**
    1. Detect `--solutionFile` + no `.flowline` → enter standalone mode
    2. Extract solution name from `Solution.xml` in zip (use `--solution` override if set)
    3. Run pre-import orphan cleanup against the target environment
    4. Import zip to target environment
    5. Run post-import orphan cleanup
  - **Skipped:** git check, DTAP check, pack step (`PackSolutionAsync`)
  - **Covers:** R1, R3, R4, R5, R8, R9, R10

---

## Acceptance Examples

- AE1. Happy path
  - **Covers:** R1, R3, R4, R5, R8
  - **Given:** No `.flowline` in the working directory
  - **When:** `flowline deploy prod --solutionFile ./MySolution_1_0_0_0_managed.zip`
  - **Then:** Imports the zip to `prod`; orphan cleanup runs; git and DTAP checks do not run

- AE2. Solution name override
  - **Covers:** R5, R6
  - **Given:** No `.flowline` in the working directory
  - **When:** `flowline deploy prod --solutionFile ./MySolution.zip --solution OverrideName`
  - **Then:** Uses `OverrideName` as the solution identifier for orphan cleanup

- AE3. Used inside a Flowline project
  - **Covers:** R2
  - **Given:** `.flowline` config exists in the working directory
  - **When:** `flowline deploy prod --solutionFile ./MySolution.zip`
  - **Then:** Error; message tells the user to use `flowline deploy <target>` for project-based deploys

- AE4. Zip without Solution.xml
  - **Covers:** R7
  - **Given:** No `.flowline` in the working directory; zip has no `Solution.xml`
  - **When:** `flowline deploy prod --solutionFile ./bad.zip`
  - **Then:** Error naming the missing `Solution.xml`; suggests passing `--solution <name>` to proceed without it

---

## Scope Boundaries

- CI/CD pipeline ergonomics (artifact store integration, env-var inputs) — not this iteration
- Separate `upload` or `standalone` subcommand — not warranted; `--solutionFile` on the existing command is sufficient
- PAC auth management — out of scope
- Solution unpack or diff as part of the deploy flow — out of scope

---

## Sources

- Existing standalone patterns: `src/Flowline/Commands/PushCommand.cs` (detection lines 66–72, mutual-exclusion guard lines 293–296)
- Standalone via config absence: `src/Flowline/Commands/GenerateCommand.cs` (lines 59–73, 346–347)
- Deploy execution flow: `src/Flowline/Commands/DeployCommand.cs` (lines 42–73)
