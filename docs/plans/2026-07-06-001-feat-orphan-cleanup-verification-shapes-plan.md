---
title: "Orphan Cleanup — Verification Shape Expansion - Plan"
type: feat
date: 2026-07-06
topic: orphan-cleanup-verification-shapes
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
---

# Orphan Cleanup — Verification Shape Expansion - Plan

## Goal Capsule

- **Objective:** Expand orphan-cleanup's trusted removal-recommendation surface to Role (via `SupportedManualTypes`), ConnectionReference and Bot (via entity-side detection, mirroring the CustomApi precedent — their componenttype codes are env-specific like CustomApi's), fix the OptionSet false positive, and add an informational-only "possible match found locally" signal for unsupported types — by generalizing local-source verification into reusable checks per identity shape instead of one bespoke scanner per component type, without loosening the evidence-gated trust bar.
- **Product authority:** This session's real incidents against a live org — WebResource/CustomApi false-positive deletions, then connectionreference/bot false-positive removal recommendations — plus a survey of a second, mature Dataverse solution (SpotlerAutomate.Dataverse) that confirmed the same identity-shape taxonomy recurs across component types Flowline hasn't seen yet.
- **Open blockers:** None. Scope and the two dialogue call-outs (type list; shape-scoped vs. blanket local-match signal) are resolved below.

---

**Product Contract preservation:** unchanged. `ce-plan` enriched this file in place from its own `ce-brainstorm` output; no R/AE-ID content changed.

## Product Contract

### Summary

Generalize orphan-cleanup's local-source verification into one reusable check per identity shape — embedded id, schemaname-keyed folder, inline Customizations.xml section — instead of one bespoke scanner per component type. Use that to promote Role, ConnectionReference, and Bot into the trusted `SupportedManualTypes` set, fix OptionSet's schemaName-declared-but-unresolvable false positive, and give unsupported types a soft "possible match found locally" signal in the verbose-only preview, without ever letting that signal promote a type into the actionable report.

### Problem Frame

Orphan-cleanup's `SupportedManualTypes` allowlist only recommends removing a component type once Flowline has a verified local-source cross-check for it — a bar set after two real incidents this session where a resolved display name got mistaken for verification and Flowline recommended deleting still-needed connection references and a bot. That bar is correct, but today it's satisfied by writing one bespoke scanner per type from scratch each time a new false positive surfaces in a real solution. This session alone found three more types (Role, ConnectionReference, Bot) whose local identity already collapses into one of three recurring shapes, and a survey of a second, larger Dataverse solution confirmed the same three shapes cover several more types Flowline hasn't hit yet (EnvironmentVariableDefinition, AppModule, AppModuleSiteMap), plus a fourth shape — a GUID-named local file or folder absent from Solution.xml entirely, the same category as Forms/Views (Dashboard, and the table-search components) — that this plan does not operationalize. Continuing to hand-write a scanner per type means re-deriving this taxonomy from scratch on every future incident, with a real-world lag between "a new type false-positives" and "it gets fixed" — during which a plausible-looking bad recommendation can sit in front of a user who (by their own account) mostly skims the manual bucket and acts only when something looks obviously wrong.

### Key Decisions

- **Shape-based reuse over per-type bespoke code.** Three identity shapes recur across the component types this session and the SpotlerAutomate survey touched: (a) a GUID embedded directly in the component's own local file, mirrored by `id` in Solution.xml — Role; (b) a schemaname/uniquename-keyed folder with no GUID anywhere locally — CustomApi (already handled), Bot, and (per the survey) EnvironmentVariableDefinition, AppModule, AppModuleSiteMap; (c) declared inline within the solution's own Customizations.xml section, also with no GUID — ConnectionReference. A future type matching an already-known shape should cost a small addition, not a new scanner.
- **Evidence-gated promotion stays the trust boundary, not the shape taxonomy.** Knowing a type's shape is necessary but not sufficient for trusting it. A type only joins `SupportedManualTypes` once a real Flowline-managed solution has actually needed it, and both suppression directions (still-declared → suppressed, genuinely-removed → reported) have test coverage. Types whose shape is known but that haven't caused a real problem yet stay deferred (see Scope Boundaries).
- **The "possible match found locally" signal is informational-only and shape-scoped.** It checks an unsupported orphan's resolved name against identifiers already harvested while scanning the known shapes above — never an unscoped, whole-repo string search across every kind of local XML. It can only change the verbose-only preview's wording; it can never promote a type into the actionable report or the manual count. This preserves the exact trust bar the connectionreference/bot incidents established.
- **OptionSet needs metadata resolution, not a data-table query.** Its Solution.xml declaration shares WebResource's schemaName-only shape, but OptionSet (global choice) is Dataverse metadata, not a data-table row — resolving schemaName → id requires a metadata-level call (the same category as the existing entity-logicalName resolution), not the `QueryExpression`-based mechanism used for data-table types like WebResource.
- **Bot and ConnectionReference are detected by entity-side query, never added to `SupportedManualTypes`.** `SupportedManualTypes` is a componenttype-int allowlist, but Bot's and ConnectionReference's componenttype codes are env-specific — the same reason CustomApi is never keyed by componenttype either. Flowline already solved this exact problem for CustomApi (`IdentifyCustomApiEntityTypesAsync`: query the backing entity table directly for the orphan candidate's objectid, independent of its numeric componenttype). Bot and ConnectionReference follow that precedent instead of the componenttype-int gate — a hardcoded componenttype constant for either would only be valid for the org it was captured in and silently stop matching in any other tenant.

### Requirements

**Trusted-type expansion**

- R1. Role is added to `SupportedManualTypes`. Its identity is already fully resolvable via the existing id-in-Solution.xml mechanism — no new scanning code — so promotion only requires test coverage for both suppression directions.
- R2. ConnectionReference orphan candidates are detected via entity-side query against the `connectionreference` table (mirroring the CustomApi precedent, not the `SupportedManualTypes` componenttype-int gate) and verified via the `connectionreferencelogicalname` declared inline in the solution's own Customizations.xml `<connectionreferences>` section — not the separately-generated deploymentSettings.json, which is optional and can go stale.
- R3. Bot orphan candidates are detected via entity-side query against the `bot` table (mirroring the CustomApi precedent, not the `SupportedManualTypes` componenttype-int gate) and verified via its schemaname-keyed local folder — matched against the live `bot.schemaname` attribute, not `bot.name` (a separate display-name field).
- R4. Local-source verification for schemaname/uniquename-keyed-folder types is generalized into one reusable scanner, reused by Bot and the existing CustomApi check rather than duplicated.

**OptionSet false positive**

- R5. An OptionSet orphan candidate recorded by `schemaName` in Solution.xml is resolved against live Dataverse metadata, not a `QueryExpression` against a data table, before being treated as orphaned.

**Informational signal for unsupported types**

- R6. For a component type not yet in `SupportedManualTypes`, if the live orphan's resolved name matches an identifier already harvested from a known local-source shape, the verbose-only preview notes a possible local match — this can never promote the type into the actionable report or the manual count.
- R7. The local-match check in R6 only draws from the known identity shapes already scanned for supported or shape-known types — it is not an unscoped, whole-repo string search.

### Acceptance Examples

- AE1. Role still declared with a matching `id` in Solution.xml → not reported as orphaned, even after promotion. **Covers R1.**
- AE2. ConnectionReference's `connectionreferencelogicalname` no longer present in Customizations.xml → reported as an actionable Manual recommendation. **Covers R2.**
- AE3. Bot's live `schemaname` matches a `bots/<schemaname>/bot.xml` folder still present locally → suppressed, not reported. **Covers R3.**
- AE4. OptionSet declared by `schemaName` in Solution.xml and still present in the target org's metadata → not reported as orphaned. **Covers R5.**
- AE5. An EnvironmentVariableDefinition orphan (not yet promoted) whose schemaname matches an identifier harvested from a known shape elsewhere in local source → verbose preview notes a possible local match, but it still does not appear in the actionable report or manual count. **Covers R6, R7.**

### Scope Boundaries

**Deferred for later**

- EnvironmentVariableDefinition, AppModule, AppModuleSiteMap — schemaname-keyed-folder shape confirmed via the SpotlerAutomate survey, but not promoted to `SupportedManualTypes` until a real Flowline-managed solution needs them.
- Dashboard, dvtablesearchentity, dvtablesearchs — GUID-keyed file/folder shape, absent from Solution.xml entirely (same category as Forms/Views), same evidence-gate applies.
- Ownership stamps (`[flowline:solution=...]` description-field tagging) — a separate, already-parked idea (`docs/brainstorms/2026-06-12-orphan-cleanup-ownership-stamps-requirements.md`); different mechanism, not part of this work.

**Outside this work's scope**

- The AutoDelete tier and its existing verification mechanisms — unchanged.
- Managed/patch solution support — long-standing out of scope for orphan-cleanup generally.

### Future Considerations

Two related but distinct ideas surfaced while researching a comparable tool (Daxif's `ExtendedSolution`) and are deliberately deferred rather than folded into this work — neither changes this plan's scope, and the scanners built here become the fallback if the first is ever built.

- **Sync-time manifest.** Capture live DEV component identity (name + id, per type) at `sync` time — when Flowline already holds a DEV connection — and commit it into `Package/src` alongside the rest of the unpacked source. This would give one clean, git-diffable identity source for every component type, potentially superseding the per-shape scanners built here. Unlike Daxif's version — which embeds the snapshot in the export zip, decoupling capture-time from consume-time, and hard-fails when that step was skipped or a different export tool was used — committing it into git-tracked `Package/src` keeps it as version-controlled source, read by the same deploy step that already reads `Package/src` today.
- **Live-query two environments directly.** A separate future capability for comparing two live environments (e.g. DEV vs PROD) without exporting or unpacking either — useful for drift detection or previewing what the next `sync` would capture. This is not a substitute for the manifest idea above: live-querying DEV at deploy time would reflect DEV's current, possibly un-synced state, which can diverge from what `Package/src` actually ships. Using live DEV for the deploy-time orphan check would be a correctness regression relative to what Flowline does today, not an improvement.

### Sources & Research

- `docs/plans/2026-06-07-004-feat-deploy-orphan-cleanup-plan.md` and `docs/brainstorms/2026-06-07-deploy-orphan-cleanup-requirements.md` — original orphan-cleanup design; actors, flows, and acceptance examples from that plan (pre-import/post-import phases) still apply unchanged here.
- `docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md` — running institutional-learnings log; parts 1–7 document each false-positive incident found and fixed this session, including the `SupportedManualTypes` opt-in bar this work extends.
- `STRATEGY.md` — "Drift detection + component cleanup" track; the 2026-07-04 milestone "Orphan cleanup (AE1–AE8) real-org testing — open, unit tests only so far" is what this work directly advances.
- Live verification (this session): `bot.schemaname` queried directly against the AutomateValue org returned `"msdyn_salesCopilot"`, matching `deploymentSettings.json`'s `CopilotAgents[].Name` and confirming it differs from `bot.name` (the display name).
- Cross-repo survey (this session): a second, mature Dataverse solution (`SpotlerAutomate.Dataverse`) confirmed the three-shape taxonomy recurs beyond Flowline's own test solution — Role (id embedded, matches Solution.xml), EnvironmentVariableDefinition/AppModule/AppModuleSiteMap (schemaname-keyed folder), Dashboard/dvtablesearchentity/dvtablesearchs (GUID-keyed file/folder, absent from Solution.xml).
- Comparable-tool research (this session): Daxif (a Dataverse ALM tool)'s `ExtendedSolution` feature (`Modules/Solution/Domain.fs`, `Extend.fs`) independently arrived at name-based (not GUID-based) identity and the same pre/post-import dependency ordering, validating this plan's direction; it also motivated the Future Considerations above.

---

## Planning Contract

### High-Level Technical Design

Every component type's local-source verification reduces to one of three identity shapes, each with its own resolution mechanism. This plan adds three rows to the existing table and one new resolution mechanism (OptionSet):

| Type | Local-source shape | Resolution mechanism | Verification key | Unit |
|---|---|---|---|---|
| PluginAssembly / Workflow / SdkMessageProcessingStep(Image) | id in Solution.xml | Existing plain id-match | `id` | *(already shipped)* |
| Role | id in Solution.xml, mirrored in `Roles/<name>.xml` | Existing plain id-match, `SupportedManualTypes` | `id` | U1 |
| CustomApi | schemaname-keyed folder + child collections | Entity-side detection (`IdentifyCustomApiEntityTypesAsync`), folder scan for suppression | `uniquename` | *(already shipped)* |
| Bot | schemaname-keyed folder, no children | Entity-side detection (KTD2, generalized from the CustomApi precedent), folder scan (U2's generalized helper) | `bot.schemaname` (not `bot.name` — KTD3) | U2, U3 |
| ConnectionReference | inline Customizations.xml section | Entity-side detection (KTD2, reusing U3's generalization), dedicated XML read for suppression | `connectionreferencelogicalname` | U4 |
| WebResource | schemaName in Solution.xml | `NameResolvableTypes`-style query | `webresource.name` | *(already shipped)* |
| OptionSet | schemaName in Solution.xml | New: metadata-level request (KTD1) | schema name | U5 |

Unsupported types (any row not yet promoted) still surface in the verbose-only preview; U6 adds a "possible local match" note there, drawing only from the shapes above — never a blanket search.

### Key Technical Decisions

- **KTD1. OptionSet resolution folds into the early `sNewIds` stage, not the manual-bucket gate.** `ComponentClassifier.Classify` already routes OptionSet (componenttype 9) to `ComponentAction.Manual`, and its schemaName-declared Solution.xml roots already land in `context.NamedComponents` via the existing generic schemaName fallback. R5's fix belongs alongside `OrphanCleanupService.ResolveEntityMetadataIdsAsync`/`ResolveNamedComponentIdsAsync` in `RunPreImportAsync` — resolving still-declared OptionSets into `sNewIds` *before* the orphan diff runs — not inside `BuildManualEntriesAsync`/`SupportedManualTypes`. This mirrors exactly how the Entity schemaName fix and the WebResource `NamedComponents` fix already work, and means a still-declared OptionSet never becomes an orphan candidate at all, matching the AutoDelete-tier types' behavior rather than the Manual-bucket's.
- **KTD2. Bot and ConnectionReference are detected by entity-side query, never gated through `SupportedManualTypes`.** `SupportedManualTypes` is `OrphanCleanupService.cs`'s `static readonly HashSet<int>` of componenttype codes (`RunPreImportAsync` checks `SupportedManualTypes.Contains(o.ComponentType)`) — a compile-time allowlist that only works for componenttype values fixed across every Dataverse tenant. Bot's and ConnectionReference's componenttype codes are env-specific, exactly like CustomApi's, so a hardcoded int constant for either would only be valid for the org it was captured in and silently stop matching (falling through to the unsupported/verbose path) in any other tenant. Flowline already solved this for CustomApi: `unknownOrphans` are checked against `IdentifyCustomApiEntityTypesAsync` (a direct entity-table membership query, independent of componenttype) *before* `BuildManualEntriesAsync` runs, and matched orphans are resolved, cross-checked against local source, and added to `entries` directly — bypassing `SupportedManualTypes` entirely. Bot and ConnectionReference follow the same pattern: generalize `IdentifyCustomApiEntityTypesAsync` to accept additional (entity name, id attribute) pairs (`bot`/`botid`, `connectionreference`/`connectionreferenceid`) alongside the three CustomApi entities, and give each its own resolve-and-suppress block mirroring the existing `customApiOrphans` block (`RunPreImportAsync`, lines ~154-181). `SupportedManualTypes` itself only ever gains Role (R1) — Role's componenttype (20) is already fixed and already resolvable via `NameResolvableTypes`.
- **KTD3. Bot's identity check uses `bot.schemaname`, not `bot.name`.** The existing `ResolvedTypeNameAttributes["bot"]` entry (`OrphanCleanupService.cs`) maps to `("botid", "name")` for the verbose-preview's *display* purpose, which is fine cosmetically. R3's *verification* purpose needs `bot.schemaname` instead — confirmed live against a real org to equal `"msdyn_salesCopilot"`, matching the local `bots/<schemaname>/bot.xml` folder name, whereas `bot.name` is a separate, unrelated display string ("Sales Copilot Power Virtual Agents Bot"). These are two different attributes serving two different concerns; the new entity-side resolution in KTD2 must query `schemaname`, not reuse `ResolvedTypeNameAttributes`'s `name` mapping.
- **KTD4. Generalize before duplicating.** `ComponentClassifier.ScanCustomApiNames`'s folder-walk (`customapis/<uniquename>/customapi.xml` plus two named child collections) is structurally the same shape Bot needs (`bots/<schemaname>/bot.xml`, no child collections). Extract the shared walk into one parameterized helper (folder name, root element name, key attribute, optional named child-collection sub-folders) before adding Bot's scanner, so a future type of the same shape (EnvironmentVariableDefinition, AppModule, AppModuleSiteMap — deferred per Scope Boundaries) is a config addition, not a new method.
- **KTD5. The local-match harvest set is names, not ids, and is built once per run.** R6/R7's "possible match found locally" check needs a flat, case-insensitive set of every identifier already surfaced by existing and new scanners in a single `RunPreImportAsync` call — `context.NamedComponents`' schemaNames, `context.EntityLogicalNames`, `ScanCustomApiNames`'s three sets flattened, and the new ConnectionReference/Bot scanners' outputs. Build this once and pass it into `LogUnsupportedOrphansAsync`, rather than re-scanning per unsupported orphan.

### Assumptions

- SpotlerAutomate's confirmed shapes (Role, EnvironmentVariableDefinition/AppModule/AppModuleSiteMap, Dashboard/table-search) are assumed representative of Dataverse's own unpack conventions generally, not specific to that solution's pac version — consistent with Cr07982's independently-observed WebResource/CustomApi shapes, but not verified against a third solution.

---

## Implementation Units

### U1. Promote Role to `SupportedManualTypes`

**Goal:** Add Role (componenttype 20) to the trusted removal-recommendation set with no new scanning code, since its identity is already captured correctly today.

**Requirements:** R1

**Dependencies:** None

**Files:**
- `src/Flowline.Core/Services/OrphanCleanupService.cs` (add `RoleComponentType` constant and include it in `SupportedManualTypes`)
- `tests/Flowline.Core.Tests/OrphanCleanupServiceTests.cs` (new tests)

**Approach:** Role's `id` is already declared directly in Solution.xml's `<RootComponent type="20" id="...">` and mirrored inside the unpacked `Roles/<name>.xml` file's own `id` attribute (confirmed against a real solution) — the existing plain id-match path in `RunPreImportAsync` already handles both directions correctly. This unit only widens `SupportedManualTypes` and adds the missing test coverage; no scanner or resolution code changes.

**Patterns to follow:** Mirror the existing `EntityComponentType`/`AttributeComponentType` promotion shape already in `SupportedManualTypes`.

**Test scenarios:**
- Happy path: Role still declared with a matching `id` in Solution.xml → not reported as orphaned. Covers AE1.
- Happy path: Role's `id` genuinely absent from Solution.xml → reported as an actionable Manual recommendation with its resolved name (via the existing `NameResolvableTypes[20]` entry).
- Regression: Role orphan candidates are excluded from `LogUnsupportedOrphansAsync`'s verbose-only path now that they're promoted.

**Verification:** `OrphanCleanupServiceTests` shows both directions passing; no other test's behavior changes.

---

### U2. Generalize the schemaname-keyed-folder scanner

**Goal:** Extract `ComponentClassifier.ScanCustomApiNames`'s folder-walk into one reusable, shape-generic helper, with `ScanCustomApiNames` refactored to call it.

**Requirements:** R4

**Dependencies:** None

**Files:**
- `src/Flowline.Core/Services/ComponentClassifier.cs`
- `tests/Flowline.Tests/ComponentClassifierTests.cs`

**Approach:** The shared shape: a top-level folder, one subfolder per component keyed by a schemaname/uniquename value, a fixed-name XML file inside each subfolder whose root element carries that key as an attribute, and zero or more named child-collection subfolders following the same pattern one level deeper (CustomApi's `customapirequestparameters/`, `customapiresponseproperties/`). Parameterize on: folder name, root element's key attribute name, and an optional list of (child-folder-name, child-key-attribute-name) pairs. Return the same shape `ScanCustomApiNames` already returns (one set per collection) so callers with a single collection (Bot) just get a one-set result.

**Technical design:** Directional only —
```
ScanShapeFolder(packageSrcRoot, folderName, keyAttribute, childCollections[]) -> { top: HashSet<string>, children: Map<folderName, HashSet<string>> }
```

**Patterns to follow:** `ComponentClassifier.ScanCustomApiNames`'s existing directory-enumeration logic is the reference implementation to refactor from.

**Test scenarios:**
- Happy path: folder with N top-level entries and no child collections returns the correct flat set.
- Happy path: folder with child collections returns correct per-child sets, matching `ScanCustomApiNames`'s current three-set output exactly (regression check).
- Edge case: folder absent entirely → empty result, no exception (matches existing `ScanCustomApiNames` behavior on a missing `customapis/` folder).
- Edge case: a component subfolder present but its expected XML file missing → skipped, not an error.

**Verification:** All existing `ScanCustomApiNames`-related tests pass unchanged after the refactor (behavior-preserving); new tests cover the generalized helper directly.

---

### U3. Detect and verify Bot orphans via entity-side query

**Goal:** Detect Bot orphan candidates via entity-side query against the `bot` table (per KTD2, mirroring the CustomApi precedent — never via `SupportedManualTypes`), verify against the local `bots/<schemaname>/bot.xml` folder (via U2's generalized scanner), and report genuinely-orphaned Bots as actionable Manual recommendations.

**Requirements:** R3

**Dependencies:** U2

**Files:**
- `src/Flowline.Core/Services/ComponentClassifier.cs` (new `ScanBotSchemaNames` using U2's helper)
- `src/Flowline.Core/Services/OrphanCleanupService.cs` (generalize `IdentifyCustomApiEntityTypesAsync` to also query `bot`/`botid`; add a Bot resolve-and-suppress block in `RunPreImportAsync` mirroring the existing `customApiOrphans` block)
- `tests/Flowline.Tests/ComponentClassifierTests.cs`, `tests/Flowline.Core.Tests/OrphanCleanupServiceTests.cs`

**Approach:** Per KTD2, Bot's componenttype is env-specific like CustomApi's, so it is never checked against `SupportedManualTypes` (a componenttype-int allowlist). Instead, generalize `IdentifyCustomApiEntityTypesAsync`'s entity-table membership query to also check `bot`/`botid` alongside the three CustomApi entities, before `unknownOrphans` reaches `BuildManualEntriesAsync`. Bot orphan candidates identified this way get their own resolve-and-suppress block (mirroring the existing `customApiOrphans` block in `RunPreImportAsync`): resolve each candidate's live `bot.schemaname` (per KTD3 — not `bot.name`), cross-check against `ScanBotSchemaNames`'s local scan, drop matches, and add genuinely-orphaned Bots directly to `entries` as `OrphanAction.Manual` with `EntityName: "bot"`.

**Patterns to follow:** `OrphanCleanupService.IdentifyCustomApiEntityTypesAsync` and the `customApiOrphans` resolve-and-suppress block immediately following it in `RunPreImportAsync` are the direct structural precedent — same query shape, same resolve-then-filter-by-local-scan flow, same `entries.Add` shape with `EntityName` set.

**Test scenarios:**
- Happy path: Bot's live `schemaname` matches a `bots/<schemaname>/bot.xml` folder still present locally → suppressed, not reported. Covers AE3.
- Happy path: Bot's live `schemaname` has no matching local folder → reported as an actionable Manual recommendation with its resolved name.
- Edge case: `bots/` folder entirely absent from `Package/src` → no false suppression (empty scan result, all Bot orphans still reported).
- Regression: Bot orphan candidates no longer appear in `LogUnsupportedOrphansAsync`'s verbose-only output once detected this way, and `SupportedManualTypes` itself is unchanged by this unit.

**Verification:** Both directions covered in `OrphanCleanupServiceTests`; `ScanBotSchemaNames` unit-tested in `ComponentClassifierTests`.

---

### U4. Detect and verify ConnectionReference orphans via entity-side query

**Goal:** Detect ConnectionReference orphan candidates via entity-side query against the `connectionreference` table (per KTD2, reusing U3's generalized detection helper — never via `SupportedManualTypes`), verify against the solution's own `Other/Customizations.xml` `<connectionreferences>` section, and report genuinely-orphaned ConnectionReferences as actionable Manual recommendations.

**Requirements:** R2

**Dependencies:** U3

**Files:**
- `src/Flowline.Core/Services/ComponentClassifier.cs` (new `ScanConnectionReferenceLogicalNames`)
- `src/Flowline.Core/Services/OrphanCleanupService.cs` (extend the entity-detection query from U3 to also check `connectionreference`/`connectionreferenceid`; add a ConnectionReference resolve-and-suppress block in `RunPreImportAsync`)
- `tests/Flowline.Tests/ComponentClassifierTests.cs`, `tests/Flowline.Core.Tests/OrphanCleanupServiceTests.cs`

**Approach:** Per KTD2, ConnectionReference's componenttype is env-specific like CustomApi's and Bot's, so it is never checked against `SupportedManualTypes`. Extend U3's generalized entity-detection query to also check `connectionreference`/`connectionreferenceid`. ConnectionReference has no dedicated top-level folder — unlike Bot's shape, it's declared inline as `<connectionreferences><connectionreference connectionreferencelogicalname="...">` entries inside the same `Other/Customizations.xml` file `ComponentClassifier.ParseSolutionXmlComponents` already parses for Solution.xml (a sibling file, same `Other/` folder). Add a small, dedicated XML read for this one section rather than forcing it through U2's folder-shape helper — the shape genuinely differs (inline section vs. folder-per-item) per the Key Decisions' three-shape taxonomy. ConnectionReference orphan candidates get their own resolve-and-suppress block mirroring U3's Bot block: resolve each candidate's live `connectionreferencelogicalname`, cross-check against `ScanConnectionReferenceLogicalNames`'s local scan, drop matches, and add genuinely-orphaned ConnectionReferences directly to `entries` as `OrphanAction.Manual` with `EntityName: "connectionreference"`.

**Patterns to follow:** `ComponentClassifier.ParseSolutionXmlComponents`'s XML-reading style (for parsing `Other/Customizations.xml` instead of `Other/Solution.xml`); U3's Bot resolve-and-suppress block for the entity-detection-to-`entries` flow.

**Test scenarios:**
- Happy path: ConnectionReference's `connectionreferencelogicalname` still present in `Other/Customizations.xml` → suppressed, not reported.
- Happy path: ConnectionReference's `connectionreferencelogicalname` no longer present in Customizations.xml → reported as an actionable Manual recommendation. Covers AE2.
- Edge case: `<connectionreferences>` section empty or absent (e.g. `<connectionreferences />`, matching a solution with none) → no false suppression, all candidates still reported.
- Edge case: `Other/Customizations.xml` itself missing → scanner returns empty, no exception.

**Verification:** Both directions covered in `OrphanCleanupServiceTests`; the Customizations.xml scan unit-tested in `ComponentClassifierTests`.

---

### U5. Fix the OptionSet false positive

**Goal:** Resolve schemaName-declared OptionSet root components against live Dataverse metadata before the orphan diff runs, per KTD1.

**Requirements:** R5

**Dependencies:** None

**Files:**
- `src/Flowline.Core/Services/OrphanCleanupService.cs` (new metadata-based resolution branch, wired into `RunPreImportAsync`'s early `sNewIds` resolution alongside `ResolveEntityMetadataIdsAsync`)
- `tests/Flowline.Core.Tests/OrphanCleanupServiceTests.cs`

**Approach:** `context.NamedComponents` already carries `(9, schemaName)` entries for OptionSet roots (via the existing generic schemaName fallback in `ComponentClassifier.ParseSolutionXmlComponents`) — today `ResolveNamedComponentIdsAsync` silently skips componenttype 9 because `NameResolvableTypes` has no entry for it (OptionSet is metadata, not a data-table row, so a `QueryExpression` can't resolve it). Add a parallel resolution path for componenttype 9 specifically, using a metadata-level request keyed by the global choice's schema name (the SDK's option-set-metadata retrieval message, mirroring `ResolveEntityMetadataIdsAsync`'s shape), and fold resolved ids into the same `sNewIds` set before the orphan diff — not into `SupportedManualTypes`/the Manual bucket, per KTD1.

**Patterns to follow:** `OrphanCleanupService.ResolveEntityMetadataIdsAsync`'s semaphore-bounded parallel resolution pattern is the direct structural precedent (metadata request per name, bounded concurrency).

**Test scenarios:**
- Happy path: OptionSet declared by `schemaName` in Solution.xml and still present in the target org's metadata → not reported as orphaned at all (never becomes an orphan candidate). Covers AE4.
- Happy path: OptionSet's schemaName genuinely no longer exists in the org's metadata → still reported (falls through to the existing Manual-bucket path, gated by `SupportedManualTypes` as today — OptionSet is not being promoted to the trusted set in this unit).
- Edge case: metadata request fails for one schemaName (e.g. a deleted global choice) → that one resolution fails gracefully without blocking resolution of the others.

**Verification:** `OrphanCleanupServiceTests` confirms an OptionSet no longer surfaces as an orphan when still declared; existing WebResource/Entity schemaName-resolution tests remain unaffected (regression check).

---

### U6. Wire the "possible match found locally" signal

**Goal:** Build the flat local-identifier harvest set once per `RunPreImportAsync` call and use it to enrich `LogUnsupportedOrphansAsync`'s verbose message when a match is found, per R6/R7.

**Requirements:** R6, R7

**Dependencies:** U3, U4 (harvests from the scanners those units add)

**Files:**
- `src/Flowline.Core/Services/OrphanCleanupService.cs`
- `tests/Flowline.Core.Tests/OrphanCleanupServiceTests.cs`

**Approach:** Per KTD4, build one case-insensitive `HashSet<string>` per run from: `context.NamedComponents`' schemaNames, `context.EntityLogicalNames`, `ComponentClassifier.ScanCustomApiNames`'s three sets flattened, and the ConnectionReference/Bot scanners' outputs (U3/U4). Pass it into `LogUnsupportedOrphansAsync`; when an unsupported orphan's resolved name is found in the set, append a short note to the existing verbose line (e.g. alongside the current "would have proposed: remove manually via maker portal" text) — the message changes, the control flow does not: the entry still never reaches `entries`/the report/the manual count.

**Test scenarios:**
- Happy path: an unsupported orphan (e.g. EnvironmentVariableDefinition, not promoted) whose resolved name matches a harvested identifier → verbose preview notes a possible local match. Covers AE5.
- Happy path: an unsupported orphan whose resolved name matches nothing harvested → verbose message unchanged from today's wording.
- Regression: in both cases, the orphan still does not appear in `entries`, the printed report, or the manual count (the load-bearing invariant from the connectionreference/bot incident).
- Edge case: an unsupported orphan with no resolvable name at all → no match attempted, no exception.

**Verification:** `OrphanCleanupServiceTests` covers both matched and unmatched cases; assert on `_console.Output` for the wording and on the absence of the type from the manual bucket/count in both cases.

---

### U7. Update institutional docs

**Goal:** Record this work in the running institutional-learnings log and confirm `CONCEPTS.md` reflects the shape taxonomy.

**Requirements:** Supports R1–R7 (traceability, not new behavior)

**Dependencies:** U1–U6

**Files:**
- `docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md` (new "part 8" update entry)
- `CONCEPTS.md` (verify the "Local-source identity shape" entry added during brainstorming still matches the shipped shapes; update only if implementation diverged)

**Approach:** Follow the existing "part N" update convention already used in this doc (parts 1–7) — one dated entry summarizing what changed and why, in the same voice as the existing entries. No new document structure needed.

**Test expectation:** none — documentation only, no behavior to test.

**Verification:** The doc's "part 8" entry accurately reflects the shipped code (cross-check against the actual diff, not the plan's Approach fields, which may have left implementation-time details open per Phase 3.6).

---

## Verification Contract

| Command | Applies to |
|---|---|
| `dotnet build src/Flowline.Core/Flowline.Core.csproj` | All units — must build with 0 errors |
| `dotnet build src/Flowline/Flowline.csproj` | All units — must build with 0 errors |
| `dotnet test tests/Flowline.Core.Tests/Flowline.Core.Tests.csproj` | U1–U6 — all tests pass, including new ones |
| `dotnet test tests/Flowline.Tests/Flowline.Tests.csproj` | U2, U3, U4 — no regression from the `ComponentClassifier.cs` changes |

No live-org verification is required to land this work (all new behavior is covered by the existing NSubstitute-based mock patterns already used throughout `OrphanCleanupServiceTests`), but a real `flowline deploy --verbose` run against a solution with Role/ConnectionReference/Bot/OptionSet components is the natural follow-up confirmation once shipped — consistent with how every prior incident this session was caught.

---

## Definition of Done

- All seven units implemented; both test projects pass with no reduction in existing test count.
- Role appears in `SupportedManualTypes`. Bot and ConnectionReference are detected via entity-side query (per KTD2) and never gated through `SupportedManualTypes`. OptionSet no longer reaches the orphan candidate list when still declared.
- The verbose-only preview for unsupported types names a possible local match when one exists, and the manual bucket / actionable report composition is unchanged for every case that isn't Role/ConnectionReference/Bot/OptionSet.
- `docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md` has a "part 8" entry; `CONCEPTS.md`'s "Local-source identity shape" entry matches the shipped shapes.
- No dead-end or experimental code remains from approaches considered and abandoned during implementation — e.g. U4's decision not to reuse U2's folder-shape helper for ConnectionReference (per U4's Approach) should leave no half-wired attempt at forcing ConnectionReference through that helper.
