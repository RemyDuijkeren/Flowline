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

### 2. WebResource-dependencies — integration test
`// flowline:depends` and RESX auto-link (shipped in v0.7.0) have no integration test yet against a real WebResources project/push. Needs verification before v1.0.

---

### 3. `push` — NuGet package support (Dependent Assemblies) instead of plugin `.dll`
**Status (2026-07-05):** New v1 scope, added on request. Not yet planned or implemented — `PushCommand.ResolveStandalonePluginFilePath` currently rejects `.nupkg` outright (`PushCommand.cs:353`, "NuGet packages not yet supported — use a .dll file.").

Uses Dataverse's Dependent Assemblies feature (`pluginpackage` table) instead of raw `pluginassembly` `.dll` upload — removes the ILMerge/ILRepack step for plugins with external NuGet dependencies.

Brainstorm already exists with the full mechanism (detection, `pluginpackage` upload/update, hash-skip, assembly reflection, `Mapping.xml` implications, constraints): `docs/brainstorms/2026-06-12-plugin-nuget-packages-requirements.md`. It has **5 open questions** still unresolved (solution-pack support for `pluginpackage`, exact content attribute name, migration path from existing `pluginassembly` + steps, feature-flag requirements, signing validation) and no implementation plan yet.

**Before implementing:** run `/ce-plan` (or `/ce-brainstorm` first if the open questions need narrowing) against the brainstorm doc — this is real scope, not a quick add.

---

## Should-haves (good to add, not v1.0 blockers)

### 4. Observability Wave 4 — telemetry (I7)
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
- [ ] `push` NuGet package (`pluginpackage`/Dependent Assemblies) support planned and implemented — not started
- [x] Observability Wave 1 implemented (structured invocation logging, stderr capture, ILogger file sink)
- [x] Observability Wave 2 implemented (invocation context, W3C `Activity.TraceId` correlation)
- [x] `deploy` pre-backup implemented (`BackupService` wired as pre-import safety net)
- [x] `componenttype` constants confirmed via `PicklistAttributeMetadata` — low-numbered types are stable platform constants, one org sufficient
- [x] Greenfield getting-started path documented in wiki (`01-Getting-Started`, `14-Planned-Features`)
- [ ] All tests pass (`dotnet test`)
- [ ] CHANGELOG entry written for v1.0
- [ ] Release tag triggers pipeline → NuGet publish confirmed
