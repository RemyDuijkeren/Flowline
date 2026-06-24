# Flowline v1.0 Release Strategy

**Target date:** July 1, 2026
**Current version:** v0.7.0 (released 2026-06-21)
**Working window:** 7 days (2026-06-23 → 2026-06-30)

---

## Must-haves (v1.0 blockers)

### 1. `deploy` — full integration test
Never been run end-to-end against a real org. Test the full flow, not just orphan cleanup.

**Core flow:**
- Pack solution from local source (`pac solution pack`)
- Import into target environment (`pac solution import`)
- DTAP gate enforcement (prod target blocked without `--skip-dtap-check`)
- Managed/unmanaged type guard
- Drift check

**Orphan cleanup (AE1–AE8):**
- AE1: Solo orphan component → deleted
- AE2: Shared orphan (in another solution) → removed from solution only
- AE3: Data-bearing component (table, column) → MANUAL report, not deleted
- AE4: Dependency-blocked pre-import → deferred to post-import
- AE5: `--no-delete` active → report prints, no deletes
- AE6: Workflow deactivated before delete
- AE7: Workflow deactivation fails → downgrade to MANUAL
- AE8: Plugin class removed from DLL → orphaned `plugintype` cleaned pre-import → pack+import succeed

**Plan:** `docs/plans/2026-06-07-004-feat-deploy-orphan-cleanup-plan.md`

---

## Could-haves (nice before v1.0, not blockers)

### 3. `deploy` pre-backup + `--skip-backup`
Auto-backup the target environment before any deploy. Opt-out via `--skip-backup`.

**Why could-have:** PAC backup is async and takes minutes on large orgs (must poll for completion). `--dry-run` is the existing safety net. Dataverse admin center has scheduled backups. Adds real scope — PAC CLI has `pac env backup` but no native restore-and-wait primitive; need polling loop + timeout handling.

**If time allows:** Implement as opt-out (`--skip-backup` to bypass), spinner while backup runs, fail-fast on backup error before proceeding to orphan cleanup + pack + import.

---

### 4. Resolve PluginPlanner mutability question
`PluginPlanner.cs:458,522` — design question: are certain plugin attributes updatable (`IsValidForUpdate=true`) or must steps be recreated? Current logic is correct but the design intent is undocumented.

**Status:** Code comment + decision note; minimal implementation work

---

## Skip (post-v1.0)

- `--restore-state` — reactivate workflows after deploy (already scoped post-v1 in wiki)
- `provision` import-as-alternative strategy (copy vs import)
- Full managed-solution lifecycle (layering, hold, upgrade vs update)

---

## Definition of done

- [x] Pending commit pushed
- [x] `generate` safe deletion implemented (`IsGeneratorOwned` + copy-before-swap)
- [ ] `generate` safe deletion tested (partial class, `.csproj`, stale entity all verified)
- [x] `provision` region guard implemented
- [ ] `deploy` full flow tested against real org (pack, import, DTAP gate, type guard, drift check, orphan cleanup AE1–AE8)
- [x] `componenttype` constants confirmed via `PicklistAttributeMetadata` — low-numbered types are stable platform constants, one org sufficient
- [ ] All tests pass (`dotnet test`)
- [ ] Tone reviewed on any new CLI output (`/tone`)
- [ ] CHANGELOG entry written for v1.0
- [ ] Release tag triggers pipeline → NuGet publish confirmed
