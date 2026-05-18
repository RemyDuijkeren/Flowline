---
date: 2026-05-18
topic: provision-unmanaged-solution-guard
---

# ProvisionCommand — Unmanaged Solution Guard

## Summary

Add a hard safety check to `ProvisionCommand` that blocks the environment copy whenever the target contains unmanaged solutions that would be permanently lost. The block fires before the copy executes and cannot be bypassed.

---

## Problem Frame

`pac admin copy` overwrites the target environment completely. When a developer has unmanaged solutions in a dev or test environment — actively being built — and someone runs `provision --allow-overwrite`, those solutions are destroyed with no recovery path.

The dangerous scenario: prod contains a managed version of solution X (deployed as a package). Dev or test contains an unmanaged version of X — or a solution Y that prod doesn't have at all. Copying prod over dev/test silently wipes the unmanaged work. The `--allow-overwrite` flag was designed to bypass the "environment already exists" warning, not to signal awareness of data loss. The two concerns are conflated in the current implementation.

---

## Requirements

**Detection**

- R1. Before executing the environment copy, query both the prod and target environments for their solution lists.
- R2. For each unmanaged solution in the target environment, check whether the same solution (matched by unique name, case-insensitive) exists as unmanaged in prod.

**Block condition**

- R3. If any unmanaged target solution is managed in prod — block the copy, exit non-zero, and list the affected solutions with their state in each environment.
- R4. If any unmanaged target solution is absent from prod entirely — block the copy with the same error format.
- R5. `--allow-overwrite` does not bypass the block. It retains its existing role: skipping the "environment already exists" warning.

**Safe path**

- R6. If every unmanaged target solution also exists as unmanaged in prod — proceed normally. No warning needed.
- R7. If the target environment is new (does not yet exist), skip the check entirely.

---

## Acceptance Examples

- AE1. **Covers R3.** Given target has unmanaged solution `MySolution` and prod has `MySolution` as managed — when `provision test --allow-overwrite` runs — the command aborts before copying with an error naming `MySolution` and its managed state in prod.

- AE2. **Covers R4.** Given target has unmanaged solution `WorkInProgress` and prod has no solution named `WorkInProgress` — when `provision dev --allow-overwrite` runs — the command aborts before copying, naming `WorkInProgress` as absent from prod.

- AE3. **Covers R6.** Given target has unmanaged solution `SharedLib` and prod also has `SharedLib` as unmanaged — when `provision test --allow-overwrite` runs — the command proceeds to copy without any warning.

- AE4. **Covers R5.** Given target has unmanaged solution `MySolution` and prod has it as managed — when `provision test --allow-overwrite` runs — the `--allow-overwrite` flag does not suppress the block; the command still aborts.

- AE5. **Covers R7.** Given the target environment does not yet exist — when `provision dev` runs — no solution check is performed; the environment is created and copied normally.

---

## Success Criteria

- Running `provision --allow-overwrite` against a dev/test environment with unmanaged solutions (managed or absent in prod) always aborts before any copy is attempted.
- The error message names the specific solutions at risk and their state in each environment, giving the developer enough information to decide what to do next.
- No false positives: environments where all unmanaged target solutions also exist as unmanaged in prod copy cleanly without extra friction.

---

## Scope Boundaries

- No escape hatch or override flag — hard block only.
- No check on the source (prod) side beyond what's needed for the comparison.
- No changes to `sync`, `deploy`, `clone`, or any other command.
- No check when the target environment is newly created by the same `provision` run.

---

## Key Decisions

- **Hard block, no `--force`**: The risk (permanent data loss of in-progress work) outweighs the convenience of an escape hatch. Users who genuinely want to wipe an abandoned environment should delete the unmanaged solutions first.
- **Cross-environment comparison, not target-only**: Checking only the target would block even when prod has the same solution as unmanaged (safe case). The comparison determines whether the copy is destructive, not just whether unmanaged solutions exist.
- **`--allow-overwrite` scope unchanged**: The flag bypasses "env already exists" friction, not data-loss protection. Keeping them separate avoids conflating two different risk levels under one flag.

---

## Dependencies / Assumptions

- `PacUtils.GetSolutionsAsync` can be called against both prod and target URLs and returns `IsManaged` and `SolutionUniqueName` per solution. This is verified — the method is already used by `CloneCommand` for the same kind of check.
- No system solution filtering needed: Microsoft solutions are always managed (excluded by `!IsManaged` automatically); the Default Solution is always unmanaged in both environments (passes the cross-env comparison cleanly).
