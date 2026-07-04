---
date: 2026-05-20
topic: solution-versioning
---

# Solution Versioning

## Summary

Automatic patch version bumping on every `flowline sync`, backed by an immutable git tag, so Dataverse solution version and plugin assembly version stay aligned across the full build→push→sync cycle without manual version management.

---

## Problem Frame

When a Dataverse solution is exported manually through the UI, Dataverse automatically increments the patch version. `pac solution sync` — which Flowline uses — does not trigger that increment. As a result, the solution version in Dataverse never advances during normal Flowline-based development, leaving no audit trail of which solution state was captured when.

Today there is no versioning of Dataverse solutions from Flowline. Developers have no audit trail of which solution version was exported when, no way to correlate a git commit to a specific Dataverse state, and the plugin assembly version is unrelated to the solution version. This makes debugging, hotfixing, and release traceability harder than it needs to be.

The typical inner loop is: build plugin → `flowline push` → `flowline sync` → commit. Multiple of these cycles happen between each deployment to test or prod. Any versioning scheme that only fires on deploy will drift out of alignment.

---

## Key Flows

- F1. **Build → push → sync cycle**
  - **Trigger:** Developer has made plugin or solution changes in the dev Dataverse environment
  - **Steps:**
    1. Build Plugins.csproj — MinVer derives AssemblyVersion from the last sync tag
    2. `flowline push` — DLL pushed to Dataverse dev; assembly version registered
    3. `flowline sync` — Dataverse solution version bumped (patch), git tag created, solution XML downloaded
    4. Developer commits the synced changes
  - **Outcome:** Dataverse solution version and plugin assembly version align after the push in the next cycle
  - **Covered by:** R1, R2, R3, R4, R5

- F2. **Deploy to test or prod**
  - **Trigger:** Developer is ready to promote current state to test or prod
  - **Steps:**
    1. `flowline deploy [test|prod]` — deploys the solution to the target environment
  - **Outcome:** Target environment updated; no version tagging involved (version was already established at last sync)
  - **Covered by:** R8

---

## Requirements

**SyncCommand — version bump**

- R1. After a successful sync, SyncCommand reads the current solution version from Dataverse live via PAC CLI.
- R2. SyncCommand increments the patch component by default (`major.minor.patch` → `major.minor.patch+1`) and writes the new version back to Dataverse via PAC CLI.
- R3. A `--bump [major|minor|patch]` flag controls which component is incremented. Default is `patch`. Bumping major or minor resets lower components to zero (e.g. `--bump minor` on `1.2.5` → `1.3.0`, `--bump major` on `1.2.5` → `2.0.0`).

**SyncCommand — git tagging**

- R4. SyncCommand creates an immutable git tag at the current HEAD after bumping the version. A `--no-tag` flag suppresses tag creation for that run.
- R5. Tag format is always `{major}.{minor}.{patch}` (bare version, no prefix). All solutions in a repo share the same tag namespace. Flowline recommends one solution per repo as a best practice; multi-solution repos are allowed when solutions are tightly related and share a release cadence.

**Plugin project — MinVer**

- R6. Flowline scaffolds MinVer into `Plugins.csproj` when the plugin project is created. MinVer expects bare version tags (`1.0.0`) by default with no prefix configuration needed.
- R7. Plugin AssemblyVersion is derived from git tags via MinVer — no hardcoded version in `.csproj`.

**DeployCommand**

- R8. DeployCommand does not create or modify git tags. Version tracking is handled entirely by SyncCommand.

---

## Acceptance Examples

- AE1. **Covers R1, R2, R3.** Given Dataverse solution version is `1.0.5`, when `flowline sync` completes successfully, the solution version in Dataverse is updated to `1.0.6`.

- AE2. **Covers R4, R5.** Given the last sync bumped the solution to `1.0.6`, when SyncCommand runs, a git tag `1.0.6` is created at HEAD.

- AE2b. **Covers R3.** Given Dataverse solution version is `1.2.5`, when `flowline sync --bump minor` runs, the version becomes `1.3.0`. When `flowline sync --bump major` runs on `1.2.5`, it becomes `2.0.0`.

- AE3. **Covers R6, R7.** Given a repo with Plugins.csproj scaffolded by Flowline, the last git tag is `1.0.5`, and HEAD is at least one commit ahead of that tag, when the plugin is built, AssemblyVersion is `1.0.6`. If HEAD is exactly on the `1.0.5` tag (height=0), AssemblyVersion is `1.0.5`.

- AE4. **Covers R2, R7.** Given the last git tag is `1.0.5` at commit A, the developer commits new code (→ commit B, height=1), builds (AssemblyVersion=`1.0.6`), pushes DLL, then syncs: Dataverse solution becomes `1.0.6` and git tag `1.0.6` is created at B. The next build after another commit (height=1 from `1.0.6`) produces AssemblyVersion `1.0.7` — ready for the next push cycle.

---

## Success Criteria

- After a push cycle where the build happened at height > 0 (at least one commit since the last sync tag), Dataverse solution version and plugin assembly version are the same number. At height=0 (build at the exact tagged commit), the plugin trails by one patch — acceptable and transient.
- Every `flowline sync` leaves a traceable, immutable git tag. Checking out any tag gives the solution state that was in Dataverse at that version.
- No manual version management required for normal development cycles.

---

## Scope Boundaries

- No moving environment tags (`prod-current`, `test-current`) — out of scope permanently; complexity outweighs value given immutable sync tags already provide a hotfix base.
- No CI/CD pipeline wiring (GitHub Actions, Azure Pipelines) — Flowline CLI only.
- No changelog or release notes generation from tags.
- Major and minor bumps are always manual — Flowline never touches them.
- No environment tracking in `.flowline` config — version in each environment is not recorded by Flowline.

---

## Key Decisions

- **Tag on sync, not on deploy:** Multiple sync cycles happen between deploys. Tagging on deploy would misalign plugin and solution versions across those intermediate syncs. Tag is placed at HEAD at sync time (before the sync files are committed), which is semantically a pre-sync pointer but is correct for MinVer's purposes.
- **Patch bump, not build bump:** Keeps the version 3-part SemVer-compatible, which MinVer consumes directly. The fourth component (build number) is not used.
- **No tag prefix — bare version tags (`1.0.0`):** Flowline supports multiple solutions under `solutions/` but recommends one solution per repo as a best practice. Multi-solution repos share the same tag namespace (solutions are versioned together), which is coherent when solutions are tightly related. Bare version tags are MinVer's default — no configuration needed.
- **`pac solution online-version` for both read and write:** This single PAC CLI command handles reading the current solution version from Dataverse and writing the new version back. `pac solution version` (local XML) is not used — `pac solution sync` downloads the updated version naturally.
- **No env tracking tags/branches:** Immutable sync tags already provide the data needed for hotfix base lookup. Adding moving tags adds git mechanics (force-push, non-idiomatic update) for marginal value.

---

## Dependencies / Assumptions

- `pac solution online-version` reads and writes the solution version in Dataverse. It is the single PAC CLI command for both operations.
- MinVer NuGet package is available and compatible with Plugins.csproj target framework. MinVer expects bare version tags (`1.0.0`) by default — no `MinVerTagPrefix` configuration needed. MinVer gives AssemblyVersion = tag version when HEAD is exactly on a tag (height=0), and AssemblyVersion = next patch when HEAD is one or more commits ahead (height > 0). Alignment with Dataverse solution version relies on height > 0, which is the normal case when the developer has committed .NET changes since the last sync.
- Git is available in the developer environment (Flowline already requires it).

---

## Outstanding Questions

### Resolved

- [Affects R4] Tag at HEAD (pre-commit) is acceptable. `--no-tag` flag suppresses tagging for runs where this is not wanted.
- [Affects R1, R2] `pac solution online-version` is the PAC CLI command for both reading and writing the Dataverse solution version. `pac solution version` (local XML) is not used.
- [Affects R5] No tag prefix — bare version tags (`1.0.0`). Flowline supports multiple solutions but recommends one per repo; multi-solution repos share the tag namespace.

### Deferred to Planning

- [Affects R6][Technical] Determine whether MinVer scaffolding step in CloneCommand needs any changes, or if `dotnet add package MinVer` already added there is sufficient (no prefix config needed for `v*` tags).
