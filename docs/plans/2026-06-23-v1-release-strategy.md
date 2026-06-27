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

### 2. Observability + bug reproduction

Every invocation leaves enough context for a bug report without re-running. Implemented in three waves.

**Wave 1 — Foundation (I1 + I2 + I3):**
- I1: Always-on JSONL run log (`%LOCALAPPDATA%/Flowline/runs/<date>.jsonl`). Timestamp, command, redacted args, exit code, duration, tool versions. 30-day rotation. On failure, print path.
- I2: Subprocess stderr capture — 50-line rolling buffer from PAC CLI / dotnet / git, regardless of `--verbose`. Attached to `FlowlineException.WithDetail` on failure.
- I3: `ILogger<T>` debug file sink (`%LOCALAPPDATA%/Flowline/debug/<date>.log`). Warning by default; Debug when `--verbose` active. Wired into PluginService, WebResourceService, SolutionDiffService.

**Wave 2 — Rich context (I4 + I6):**
- I4: DiagnosticContext stage chain — `List<string>` scoped to command. Commands populate at each named stage. On failure: "Completed: A, B, C. Failed at: D." Single registration in `FlowlineCommand.ExecuteAsync`.
- I6: Per-invocation correlation ID — reads `FLOWLINE_TRACE_ID` env var (CI); auto-generates 8-char hex if absent. Stamps every JSONL record and verbose line.

**Wave 3 — Crash bundle (I5):**
- I5: On unhandled exception, writes zip to `%LOCALAPPDATA%/Flowline/bug-reports/<timestamp>.zip` (last N JSONL records, redacted `.flowline` config, tool versions, exception). Prints: "Bug report saved: <path> — attach to issue at github.com/..."

**Wave 4 — Telemetry (I7):** Separate product decision. Not a v1.0 blocker.

**References:** `docs/ideation/2026-06-25-cli-observability-ideation.html`

---

## Could-haves (nice before v1.0, not blockers)

### 3. `deploy` pre-backup + `--skip-backup`
Auto-backup the target environment before any deploy. Opt-out via `--skip-backup`.

**Why could-have:** PAC backup is async and takes minutes on large orgs (must poll for completion). `--dry-run` is the existing safety net. Dataverse admin center has scheduled backups. Adds real scope — PAC CLI has `pac env backup` but no native restore-and-wait primitive; need polling loop + timeout handling.

**If time allows:** Implement as opt-out (`--skip-backup` to bypass), spinner while backup runs, fail-fast on backup error before proceeding to orphan cleanup + pack + import.

---

## Skip (post-v1.0)

- `--restore-state` — reactivate workflows after deploy (already scoped post-v1 in wiki)
- `provision` import-as-alternative strategy (copy vs import)
- Full managed-solution lifecycle (layering, hold, upgrade vs update)

---

## Definition of done

- [x] Pending commit pushed
- [x] `generate` safe deletion implemented (`IsGeneratorOwned` + copy-before-swap)
- [x] `generate` safe deletion tested (partial class, `.csproj`, stale entity all verified)
- [x] `provision` region guard implemented
- [ ] `deploy` full flow tested against real org (pack, import, DTAP gate, type guard, drift check, orphan cleanup AE1–AE8)
- [x] Observability Wave 1 implemented (I1 JSONL run log, I2 stderr capture, I3 ILogger file sink)
- [ ] Observability Wave 2 implemented (I4 stage chain, I6 correlation ID)
- [ ] Observability Wave 3 implemented (I5 crash bundle)
- [x] `componenttype` constants confirmed via `PicklistAttributeMetadata` — low-numbered types are stable platform constants, one org sufficient
- [x] Greenfield getting-started path documented in wiki (`01-Getting-Started`, `14-Planned-Features`)
- [ ] All tests pass (`dotnet test`)
- [ ] Tone reviewed on any new CLI output (`/tone`)
- [ ] CHANGELOG entry written for v1.0
- [ ] Release tag triggers pipeline → NuGet publish confirmed
