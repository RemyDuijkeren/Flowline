# Flowline v1.0 Release Strategy

**Target date:** July 20, 2026 (moved from July 1 — orphan cleanup and WebResource-dependencies integration testing still open)
**Current version:** v0.7.0 (released 2026-06-21)
**Working window:** 27 days (2026-06-23 → 2026-07-20)

---

## Must-haves (v1.0 blockers)

### 1. `deploy` — full integration test
**Status (2026-07-04):** Core flow (pack, import, DTAP gate, type guard, drift check) tested against a real org — basic path works. Orphan cleanup (OrphanService) only has mocked unit tests (`OrphanCleanupServiceTests.cs`) — AE1–AE8 still need real-org verification.

**Core flow:**
- Pack solution from local source (`pac solution pack`)
- Import into target environment (`pac solution import`)
- DTAP gate enforcement (prod target blocked without `--skip-dtap-check`)
- Managed/unmanaged type guard
- Drift check

**Orphan cleanup (AE1–AE8) — remaining work:**
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

### 1b. WebResource-dependencies — integration test
`// flowline:depends` and RESX auto-link (shipped in v0.7.0) have no integration test yet against a real WebResources project/push. Needs verification before v1.0.

---

### 2. Observability + bug reproduction

Every invocation leaves enough context for a bug report without re-running.

**Status (2026-07-04):** Wave 1 and Wave 2 done. Shipped design diverged from the original I1–I6 sketch below but covers the same ground — see `docs/brainstorms/2026-06-29-wave-2-invocation-context-requirements.md` and `docs/plans/2026-06-29-001-feat-wave2-invocation-context-plan.md` for the as-built spec. Notably: I1's JSONL run log was implemented then replaced by structured `ILogger` invocation logging + `FlowlineActivitySource` (commit `8e7ba06`); I6's correlation ID is W3C `Activity.TraceId` (via `ActivityTraceEnricher`) rather than a custom `FLOWLINE_TRACE_ID` env var.

Wave 3 (crash bundle) and Wave 4 (telemetry) are **not v1.0 blockers** — moved to should-haves below.

**Wave 1 — Foundation (I1 + I2 + I3):** ✓ done
- I1: run log — superseded by structured `ILogger` invocation logging (see above)
- I2: Subprocess stderr capture — shipped as `SubprocessCapture` (DI-injected), wired into GitUtils/PacUtils/DotNetUtils/commands/generators
- I3: `ILogger<T>` debug file sink — shipped

**Wave 2 — Rich context (I4 + I6):** ✓ done
- I4: stage/invocation context — shipped as structured invocation logging + `Activity` tags per command (`FlowlineCommand.cs`)
- I6: correlation ID — shipped as W3C `Activity.TraceId`, enriched onto every log line via `ActivityTraceEnricher`

**References:** `docs/ideation/2026-06-25-cli-observability-ideation.html`

---

## Shipped since original plan

### 3. `deploy` pre-backup ✓ (2026-07-03)
`BackupService` wired as a pre-import safety net before orphan cleanup (`PacUtils.BackupEnvironmentAsync`, commit `1f57dbd`). Has unit tests (`PacUtilsBackupTests.cs`). Plan: `docs/plans/2026-07-03-002-feat-pre-deploy-backup-plan.md`.

---

## Should-haves (good to add, not v1.0 blockers)

### 4. Observability Wave 3 — crash bundle (I5)
On unhandled exception, writes zip to `%LOCALAPPDATA%/Flowline/bug-reports/<timestamp>.zip` (last N log records, redacted `.flowline` config, tool versions, exception). Prints: "Bug report saved: <path> — attach to issue at github.com/...". Not implemented — the shipped "wave 3" work (`docs/plans/2026-06-30-001-feat-verbose-log-routing-wave3-plan.md`) is a differently-scoped verbose-log-routing effort (VerboseMarkup, SubprocessCapture, LoggingRenderHook), not the crash bundle.

### 5. Observability Wave 4 — telemetry (I7)
Separate product decision (opt-in usage telemetry). Not scoped for v1.0.

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
- [x] `deploy` core flow tested against real org (pack, import, DTAP gate, type guard, drift check) — basic path works
- [ ] Orphan cleanup (AE1–AE8) tested against real org — currently mocked unit tests only
- [ ] WebResource-dependencies (`// flowline:depends`, RESX auto-link) integration tested against real push
- [x] Observability Wave 1 implemented (structured invocation logging, stderr capture, ILogger file sink)
- [x] Observability Wave 2 implemented (invocation context, W3C `Activity.TraceId` correlation)
- [x] `deploy` pre-backup implemented (`BackupService` wired as pre-import safety net)
- [x] `componenttype` constants confirmed via `PicklistAttributeMetadata` — low-numbered types are stable platform constants, one org sufficient
- [x] Greenfield getting-started path documented in wiki (`01-Getting-Started`, `14-Planned-Features`)
- [ ] All tests pass (`dotnet test`)
- [ ] Tone reviewed on any new CLI output (`/tone`)
- [ ] CHANGELOG entry written for v1.0
- [ ] Release tag triggers pipeline → NuGet publish confirmed
