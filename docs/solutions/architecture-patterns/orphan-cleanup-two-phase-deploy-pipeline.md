---
title: Orphan component cleanup — two-phase deploy pipeline
date: 2026-06-10
last_updated: 2026-07-08 (part 10)
category: docs/solutions/architecture-patterns
module: DeployCommand
problem_type: architecture_pattern
component: tooling
severity: high
resolution_type: workflow_improvement
applies_when:
  - Deploy command imports a Dataverse solution and must remove components no longer in the solution
  - Component set in Solution.xml (S_new) differs from component set currently in Dataverse (S_old)
  - Some orphaned components may be shared with other solutions (RemoveFromSolution vs Delete)
  - Dependency errors block deletion before import but resolve after import completes
  - A --no-delete flag is needed to report what would be deleted without acting
  - An entity-detection query spans multiple backing tables (e.g. CustomApi family plus Bot/ConnectionReference) sharing one failure boundary
  - A live metadata or data lookup can return a null or unresolved identity attribute for a candidate orphan
related_components:
  - ComponentClassifier
  - OrphanCleanupService
  - IPostDeployService
  - PostDeployContext
  - ExitCode
  - DriftCommand
tags:
  - dataverse
  - orphan-cleanup
  - deploy
  - two-phase
  - solution-components
  - dependency-order
  - cross-solution
  - exit-codes
  - failure-isolation
  - false-positive-prevention
  - exception-handling
---

# Orphan component cleanup — two-phase deploy pipeline

## Context

Unmanaged solution imports in Dataverse are additive. When a plugin class is removed from the DLL or a web resource is deleted from source, the corresponding Dataverse record is never removed by the import. This creates orphan components: records in Dataverse that exist in neither the current solution source nor any successor deployment.

The hard-failure case for plugin development: when a plugin class is removed from the DLL and the developer redeploys, Dataverse blocks the DLL upload because the orphaned `plugintype` record still references the old class. The developer must manually delete the record through the maker portal before the deploy succeeds.

Without automated cleanup, long-lived unmanaged solutions accumulate orphaned plugin types, step configurations, web resources, and workflows that slow execution, pollute the environment, and periodically surface as manual intervention requirements.

The pattern documented here — pre/post-import orphan diffing with two-phase execution — addresses all of these cases systematically during the normal deploy workflow.

## Guidance

### Core principle: diff-then-execute with dependency deferral

The implementation resolves orphans in two phases because Dataverse enforces referential integrity: you cannot delete a `pluginassembly` while `plugintype` records depend on it, and you cannot delete a `plugintype` while `sdkmessageprocessingstep` records depend on it. Rather than computing a topological sort in advance (which requires full knowledge of all dependencies), the pattern:

1. **Pre-import:** classifies and deletes orphans in dependency order; catches dependency errors specifically and defers those entries.
2. **Post-import:** re-queries deferred entries; retries them. At this point the import has resolved most cross-solution dependencies.

### Component classification

`ComponentClassifier` distinguishes components that can be auto-deleted from those requiring manual removal.

```csharp
public enum ComponentAction { AutoDelete, Manual }

public static class ComponentClassifier
{
    private static readonly HashSet<int> AutoTypes =
    [
        91,  // PluginAssembly
        90,  // PluginType
        92,  // SdkMessageProcessingStep
        93,  // SdkMessageProcessingStepImage
        61,  // WebResource
        29,  // Workflow
    ];

    public static ComponentAction Classify(int componentType) =>
        AutoTypes.Contains(componentType) ? ComponentAction.AutoDelete : ComponentAction.Manual;
}
```

**Why only stable componenttype values:** Dataverse `solutioncomponent.componenttype` values below 100 are stable platform constants across all orgs. The CustomApi family uses env-specific component type numbers that differ per org — they cannot be classified by integer. They are detected separately via parallel entity queries (see below).

### S_new: Solution.xml RootComponents, plus entity subcomponent files — not customizations.xml

The diff source must come from the unpacked solution source, not from `customizations.xml` (`customizations.xml` contains entity metadata — attribute definitions, form XML — not component object references). But `Solution.xml` `<RootComponents>` alone under-reports S_new (see 2026-07-05 update below): entity roots can be recorded by `schemaName` instead of `id`, and subcomponents like Forms/Views live under `Entities/<name>/**` with no `<RootComponent>` entry at all.

```csharp
var rootComponents = doc.Root
    ?.Element("SolutionManifest")
    ?.Element("RootComponents")
    ?.Elements("RootComponent")
    ?? [];

var components = new List<(Guid, int)>();
var entityLogicalNames = new List<string>();

foreach (var component in rootComponents)
{
    if (!int.TryParse(component.Attribute("type")?.Value, out var type)) continue;

    if (Guid.TryParse(component.Attribute("id")?.Value, out var id))
    {
        components.Add((id, type));
        continue;
    }

    // Entity roots use schemaName instead of id — caller must resolve to MetadataId live.
    if (type != EntityComponentType) continue;
    var schemaName = component.Attribute("schemaName")?.Value;
    if (!string.IsNullOrEmpty(schemaName))
        entityLogicalNames.Add(schemaName);
}
```

`ScanEntitySubcomponents` separately walks `Entities/<name>/{FormXml,SavedQueries}/**` for GUID-named files, since these are never enumerated as `<RootComponent>` nodes regardless of the entity's `behavior` setting.

### Always use RetrieveAllAsync for solutioncomponent queries

`RetrieveMultipleAsync` silently truncates at 5000 records. A solution with more than 5000 components silently drops entries, causing false positives in the orphan diff (records appear to be orphans because they weren't seen in the first page). All `solutioncomponent` queries must paginate:

```csharp
// Bad: silently truncates at 5000
var result = await service.RetrieveMultipleAsync(query, ct);

// Good: pages through all results
var entities = await service.RetrieveAllAsync(query, ct);
```

See `src/Flowline.Core/Services/OrganizationServiceExtensions.cs` for the paging extension. Applied in all four query methods in `OrphanCleanupService`.

**Update (2026-07-03):** `OrphanCleanupService` now implements `IPostDeployService` and threads its deferred-entry state across the two phases via a private mutable instance field (`_deferred`) rather than an explicit parameter/return value passed by the caller. This is safe only because the service is registered `AddSingleton` and Flowline runs one command per process — see [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) for the interface shape and the tradeoffs of that design.

**Update (2026-07-05): `<RootComponents>` alone under-reports S_new — three false-positive sources found and fixed.** A real deploy flagged 53 "manual" orphans; verification against the actual unpacked solution source showed most were still legitimately part of the solution:

1. **Entity roots recorded by `schemaName`, not `id`.** `<RootComponent type="1" schemaName="account" behavior="1" />` has no `id` attribute — the old parser's `Guid.TryParse(id)` silently dropped these, so every entity added as a root (Account, Contact, Lead, ...) looked orphaned. `ParseSolutionXmlComponents` now returns a `SolutionXmlComponents(Components, EntityLogicalNames)` record; callers with a live connection resolve `EntityLogicalNames` to MetadataIds via `RetrieveEntityRequest { LogicalName = name }` (see `OrphanCleanupService.ResolveEntityMetadataIdsAsync`) and fold the result into the S_new id set before diffing.
2. **Subcomponents unpacked under `Entities/<name>/**` are never listed as `<RootComponent>` nodes at all** — Forms (`FormXml/**/{guid}.xml`, componenttype 60) and Views (`SavedQueries/{guid}.xml`, componenttype 26) exist as tracked, git-diffable files but have no corresponding manifest entry. `ComponentClassifier.ScanEntitySubcomponents(srcRoot)` recovers their GUIDs by filename scan; `DeployCommand.ParseSolutionXml` merges this into S_new alongside the manifest components.
3. **Microsoft system components use a fixed GUID prefix** (`00000000-0000-0000-00aa-...` — confirmed on out-of-box system views) and are never user-deletable regardless of manifest state. `ComponentClassifier.IsWellKnownSystemComponent` excludes them from the orphan set entirely, independent of S_new.

**Update (2026-07-05, part 2): Attribute-type orphans (componenttype 2), manual-report naming, and `PackageSrcRoot`.** Unlike Forms/Views, attributes have no GUID-named file to scan — `Entity.xml` declares them by `LogicalName`/`PhysicalName`, not by their solutioncomponent MetadataId. Closing this gap needed a live metadata round-trip regardless of local source:

- `OrphanCleanupService.ResolveAttributeInfoAsync` issues one `RetrieveMetadataChangesRequest` with `EntityQueryExpression.Criteria` scoped to `context.EntityLogicalNames` (the solution's own entities) and `AttributeQuery.Criteria` filtered to the orphan attribute MetadataIds. Leaving `Criteria` unset would make this the `RetrieveAllEntities`-equivalent full-org metadata walk the SDK docs warn against — scoping to known entities keeps it a single, cheap, bounded call that only fires when Attribute-type orphans exist.
- For each resolved `(entityLogicalName, attributeLogicalName)`, `ComponentClassifier.ScanEntityAttributeLogicalNames(packageSrcRoot, entityLogicalName)` checks whether that LogicalName is still declared in `Entities/<name>/Entity.xml`. If yes, it's a false positive (a customized-but-still-present field on a `behavior="1"` entity like Account) and is suppressed rather than listed. If no, it's a genuine leftover and gets a real name instead of a bare GUID.
- `PostDeployContext` gained `PackageSrcRoot` (the `Package/src` folder) — a raw folder reference alongside the parsed `LocalComponents`, for services that need to read unpacked source directly. Note this can't solve the naming problem alone: attributes are keyed by LogicalName in Entity.xml, not the MetadataId GUID a solutioncomponent row carries, so correlating the two still requires the live metadata call above.
- Manual-orphan output also got a label pass: a verified `componenttype → display label` table (`ManualTypeLabels`, sourced from the Solution Component table reference on learn.microsoft.com) replaced the bare `Component(N)` fallback; types outside that table (e.g. env-specific 10000+ codes, same category as the CustomApi family) are shown as "Unrecognized component" rather than guessed at; and the manual bucket is now grouped separately from what Flowline handles automatically, with a pointer to the org's classic solutions list (`/tools/Solution/home_solution.aspx?etn=solution`, a documented Dataverse URL) since "remove via maker portal" alone was a dead end.
- Data-backed manual types (Form/View/Role/ConnectionRole) get names via `OrphanCleanupService.GetEntityNamesAsync`, a generalization of the existing webresource-name lookup (`QueryExpression` on the backing table's `name` column) — no metadata call needed for these, they're plain data rows.

**Update (2026-07-05, part 3): names everywhere, and a real fix for "unrecognized" types via `solutioncomponentdefinition`.** Part 2 only named the Manual bucket — the AutoDelete and CustomApi entries (WebResource, PluginAssembly, PluginType, SdkMessageProcessingStep(Image), Workflow, CustomApi family) still showed bare GUIDs, which defeats the point when deciding whether a delete is safe.

- `NameResolvableTypes` now covers the AutoDelete componenttypes too (column names verified against the existing queries in `PluginReader.cs` — `plugintype.typename`, `sdkmessageprocessingstep.name`, etc.), and the `autoOrphans`/`customApiOrphans` loops in `RunPreImportAsync` resolve names the same way the Manual loop already did — grouped by componenttype (or by entity LogicalName for the CustomApi family), one bulk query per group.
- **`solutioncomponentdefinition` resolves "unrecognized" componenttype codes to a real name.** This is Dataverse's own component-type lookup table — documented for the Web API as `GET solutioncomponentdefinitions?$select=name,solutioncomponenttype` (Power Pages CLI solution docs) — queryable via the SDK exactly like any other entity. `ResolveComponentTypeNamesAsync` queries it for any componenttype not already in `ManualTypeLabels`, turning e.g. `Unrecognized component (type 10064)` into the type's actual name (e.g. `Connection Reference`) wherever Dataverse itself knows what it is. `OrphanEntry.TypeNameResolved` tracks whether this resolved, so `ManualLabel` can drop the "unrecognized" wording once the type is identified — genuinely unresolvable codes (nothing found in `solutioncomponentdefinition` either — e.g. a private/deleted definition) keep the more cautious wording.
- Test-mock note: `OrphanCleanupServiceTests`' `IOrganizationServiceAsync2` mock needed a catch-all default (`Arg.Any<QueryExpression>()` → empty `EntityCollection`) once name resolution started firing unconditionally for every orphan group — real Dataverse never returns a null `EntityCollection`, but NSubstitute does for unconfigured calls, so bulk name queries not explicitly mocked in older tests threw `NullReferenceException` inside `RetrieveAllAsync` until the default was added.

**Update (2026-07-05, part 4): non-entity schemaName roots and CustomApi's GUID-less source — the same under-reporting bug, two more places.** A real deploy flagged every WebResource in the solution (7/7) plus the entire CustomApi family for deletion, even though all of them were still declared in local source. Root cause was the identical class of bug fixed in part 1 for Entity roots, just unhandled for two more shapes:

- **WebResource (and any other `NameResolvableTypes` type) recorded by `schemaName`, not `id`.** `<RootComponent type="61" schemaName="av_pkg/example1.js" behavior="0" />` — in the real `Solution.xml` that surfaced this, every WebResource root used `schemaName` with no `id`, while every PluginAssembly/Workflow/SdkMessageProcessingStep root carried an `id` (n=1 solution — not verified across every org/pac version, but consistent with pac recording by name specifically when the id isn't portable). The old parser's `if (type != EntityComponentType) continue;` silently dropped every non-Entity schemaName root, so WebResource identity was never captured in S_new at all — it looked orphaned regardless of whether the live id matched, on every deploy, not just cross-environment ones. `SolutionXmlComponents` gained `NamedComponents: IReadOnlyList<(int ComponentType, string SchemaName)>` for these; `OrphanCleanupService.ResolveNamedComponentIdsAsync` resolves each group live via `NameResolvableTypes`' entity/name-attribute mapping (same table already used for display-name lookups, now reused in reverse — name→id instead of id→name) and folds the result into the S_new id set, mirroring `ResolveEntityMetadataIdsAsync`.
- **CustomApi source has no GUID anywhere** — `Package/src/customapis/<uniquename>/customapi.xml` (and its `customapirequestparameters/<name>/`, `customapiresponseproperties/<name>/` children) is keyed purely by `uniquename`/folder name, and isn't listed in `Solution.xml` at all (its componenttype is env-specific — see CustomApi detection below). `ComponentClassifier.ScanCustomApiNames(packageSrcRoot)` scans this folder structure; `RunPreImportAsync` resolves each CustomApi-family orphan candidate's live name (already needed for display) and drops it from `customApiOrphans` if that name is still present locally, before cross-solution membership and delete execution ever see it.

Both fixes only ever narrow the orphan set (never widen it) — a type not covered by `NameResolvableTypes`, or a `customapis/` folder that doesn't exist, falls back to the pre-fix behavior exactly, not a worse one.

**`connectionreference`/`bot` manual-bucket entries also got instance names, not just the type label.** These have env-specific componenttype codes like the CustomApi family, so they can't be keyed in `NameResolvableTypes` by componenttype. But `solutioncomponentdefinition.name` for them resolves to literally the backing table's LogicalName (confirmed against a real org — a type resolves to the string `"connectionreference"`/`"bot"`, not a display label like "Connection Reference"). `ResolvedTypeNameAttributes` maps that resolved label to `(IdAttribute, NameAttribute)` — `connectionreferenceid`/`connectionreferencelogicalname` and `botid`/`name` respectively — so `BuildManualEntriesAsync` can query the actual record's name once the type itself resolves, rather than showing a bare GUID next to the type name.

**Manual-bucket entries are informational only — Flowline never auto-deletes or auto-removes them.** Worth restating: everything in the "can't be removed automatically" bucket is a candidate for the human to verify via the maker portal link, not something Flowline acts on. A manual-bucket component that's intentionally still wanted (e.g. a standard Microsoft form the solution legitimately depends on) requires no action — it's flagged because Flowline can't confirm its presence from local source, not because it's scheduled for deletion.

**Update (2026-07-05, part 5): explicit opt-in per componenttype for the manual bucket — the connectionreference/bot fix above was still wrong.** A real deploy against the same solution showed the part-4 fix working for WebResource/CustomApi, but the manual bucket recommended removing two `connectionreference` rows that resolved to `av_sharedcalendlyv2_bffc3` and `cr993_sharedcommondataserviceforapps_d75a1` — names the user confirmed were still actively used by flows in the solution. The bug: resolving a display name via `solutioncomponentdefinition` is cosmetic (it tells you *what* the component is) and had been conflated with verification (*whether* Flowline can confirm it's actually orphaned). `ManualLabel` treated "name resolved" as "confident to recommend," when connectionreference/bot have no local source representation at all — Flowline can't check them against anything.

The fix, per the user's own proposal: an explicit opt-in allowlist, `SupportedManualTypes`, gates which componenttypes are recommended for manual removal at all. A type only joins once it has a real local-source cross-check *and* test coverage for both directions (still-present → suppressed, genuinely-removed → reported):

```csharp
static readonly HashSet<int> SupportedManualTypes = [EntityComponentType, AttributeComponentType]; // 1, 2
```

Everything else — Role, ConnectionRole, connectionreference, bot, and any unrecognized type — is logged via `console.Verbose` only (componenttype + objectid, no recommendation) and excluded entirely from the printed report and the manual count. This is deliberately narrower than the pre-incident behavior; **Form (60) and View (26) were pulled from the allowlist during this same pass** — despite `ScanEntitySubcomponents` existing, it only finds a form/view's file under `Entities/<name>/FormXml|SavedQueries/` for entities this solution actually unpacks. A form on an entity outside that set (the `Form 'Sales Insights'` from the same log — a standard Microsoft form, confirmed by the user, most likely living on an entity like Opportunity that this solution doesn't include as a root component at all) has nothing for the scan to find, so it would false-positive exactly like connectionreference/bot did. Widening `SupportedManualTypes` to include Form/View requires handling that gap first.

The `solutioncomponentdefinition`-based "resolve an unrecognized type's display name" machinery added in part 3 (`ResolveComponentTypeNamesAsync`, `ResolvedTypeNameAttributes`, `OrphanEntry.TypeNameResolved`) was removed rather than kept dormant: once gated by `SupportedManualTypes`, every type that reaches the manual-entry-building code is already a key in `ManualTypeLabels`, so the resolution path was unreachable dead code, not future-proofing. Re-add the equivalent mechanism only when actually opting in a new env-specific type with test coverage to back it.

**Update (2026-07-05, part 6): verbose preview for unsupported types, so opt-in decisions have real data behind them.** Dropping unsupported types to a bare `console.Verbose($"Solution component type {N} ({id})...")` solved the false-positive problem but lost the information needed to actually widen `SupportedManualTypes` later — the user asked to see the resolved type name (`connectionreference`, `bot`, `Form`, ...), the individual record's own name (`av_sharedcalendlyv2_bffc3`), and what the pre-opt-in logic would have recommended, entirely for evaluation purposes. `LogUnsupportedOrphansAsync` re-introduces the `ResolveComponentTypeNamesAsync`/`ResolvedTypeNameAttributes` machinery removed in part 5 — this time deliberately scoped to the verbose-only preview path so it can never influence `entries`/the printed report/the manual count. A resolved instance name is informational context for a human decision, not a finding Flowline is confident enough to act on; the two are structurally separated by which method reads the resolution result (`BuildManualEntriesAsync` for the former, `LogUnsupportedOrphansAsync` for the latter), not just by a comment.

The three near-identical "group orphans by componentType, resolve names via `NameResolvableTypes`" loops this added (AutoDelete entries, supported Manual entries, this verbose preview) were then deduplicated into one `ResolveGroupNamesAsync` helper — pure extraction, no behavior change.

**Update (2026-07-06, part 7): the WebResource annotation exemption reads `Package/src`, never `WebResources/dist` — `PostDeployContext.WebResourceRoot` removed.** `ExemptAnnotationReferencedWebResourcesAsync` originally scanned the top-level `WebResources/` project (`src/`, `dist/`, `public/`) for `// flowline:depends` comments. That's wrong for what `deploy` actually does: `PackSolutionAsync` always packs from `Package/src` (`pac solution pack --folder Package/src`) — whatever's in `Package/src/WebResources/**` at the moment `RunPreImportAsync` runs *is* the content this invocation ships, regardless of target environment. Reading `WebResources/dist` instead checks a different artifact than the one being packed and imported:

- An annotation present in `dist` but not yet promoted into the committed `Package/src` would exempt a component based on content that isn't part of this deploy.
- An annotation removed from `dist` during a later refactor, while `Package/src` still holds the older committed version *with* that annotation (not yet re-synced), would be missed if only `dist` were checked — but the old version, annotation and all, is what's actually shipping, so the exemption should still apply.
- On a CI agent that promotes `Package/src` through TEST/UAT/PROD without ever running the WebResources build step, `WebResources/dist` may not exist at all — `WebResourceAnnotationParser.CollectAllReferences` degrades silently on a missing directory, so the exemption check would silently no-op with no signal that it wasn't checking anything.

`deploy`'s job is promotion from the committed `Package/src` — the WebResources build/drift concerns belong to an earlier pipeline stage (`sync`, or the `PluginWebResourceDriftChecker` gate that already runs before packing), not something orphan-cleanup should re-derive from a separate local artifact. `ExemptAnnotationReferencedWebResourcesAsync` now takes `packageSrcRoot` and scans `Path.Combine(packageSrcRoot, "WebResources")`; `PostDeployContext.WebResourceRoot` is gone (it had exactly one consumer). `push`'s own `webresourceRoot` parameter — used by `WebResourceReader`/`WebResourceService` — is unrelated and unaffected: push legitimately operates on `WebResources/dist` directly, since it bypasses `Package/src` entirely for fast DEV iteration.

**Generalized rule for the next deploy-time local-source check:** a promotion pipeline needs exactly one artifact as ground truth per stage. For `deploy`, that's `Package/src` — not because it's necessarily the freshest content, but because it's the only artifact guaranteed to match what's actually being imported. Before wiring up a new check, ask: does this command pack from `Package/src` (→ read `Package/src`), or does it intentionally skip packing, like `push` (→ reading the build-tool output directly is correct)?

**Update (2026-07-06, part 8): three more identity shapes promoted, an entity-detection gap fixed, OptionSet's metadata-vs-data-table mistake corrected, and a soft signal added for everything still unsupported.** Two real incidents this session (connectionreference and bot false-positive removal recommendations) surfaced a recurring taxonomy: most component types' local identity collapses into one of three shapes — a GUID embedded in the component's own file and mirrored by `id` in Solution.xml (Role); a schemaname/uniquename-keyed folder with no GUID anywhere locally (CustomApi, Bot); or a declaration inline within Customizations.xml's own section, also with no GUID (ConnectionReference). This work:

- **Generalized the folder-shape scanner.** `ComponentClassifier.ScanCustomApiNames`'s folder-walk was extracted into `ScanShapeFolder(packageSrcRoot, folderName, childCollectionFolders[])` — a top-level folder, one subfolder per component keyed by its local identity, and zero or more named child-collection subfolders one level deeper. `ScanCustomApiNames` now calls it (three collections); `ScanBotSchemaNames` calls it too (zero collections). Identity comes purely from folder names — no file inside a subfolder is ever opened, so a subfolder present without its usual XML file still counts (confirmed by an existing test fixture predating this work).
- **Promoted Role** into `SupportedManualTypes` — no new scanning code needed, since Role's id is already declared in Solution.xml and mirrored in `Roles/<name>.xml`.
- **A design correction mid-implementation, surfaced by doc review, not caught in the original plan draft:** the plan initially proposed adding Bot and ConnectionReference to `SupportedManualTypes` too, alongside Role. `SupportedManualTypes` is a compile-time `HashSet<int>` of componenttype codes — but Bot's and ConnectionReference's componenttype codes are env-specific, exactly like CustomApi's (already handled via entity-side detection, never componenttype). A hardcoded int for either would only be valid for the org it was captured in and silently stop matching in any other tenant — precisely the class of bug this whole taxonomy work exists to prevent, and it would have shipped inside the very PR meant to fix that class of bug. `IdentifyCustomApiEntityTypesAsync` was generalized to `IdentifyEntityDetectedTypesAsync`, extended to also query `bot`/`botid` and `connectionreference`/`connectionreferenceid` alongside the three CustomApi entities (still a single bulk `RetrieveAllAsync` per table, still one `Task.WhenAll`). `SupportedManualTypes` itself only ever gained Role.
- **Bot verification uses `bot.schemaname`, not `bot.name`.** `ResolvedTypeNameAttributes["bot"]` (added in part 6) maps to `name` for the verbose-preview's *display* purpose only — that's a separate, unrelated string ("Sales Copilot Power Virtual Agents Bot") from the stable identity attribute (`"msdyn_salesCopilot"`, confirmed live against a real org) that matches the local `bots/<schemaname>/bot.xml` folder name. The new entity-side resolution queries `schemaname` independently rather than reusing the display mapping.
- **ConnectionReference has no dedicated folder** — it's declared inline in `Other/Customizations.xml`'s `<connectionreferences>` section, the same file `ParseSolutionXmlComponents` already reads Solution.xml alongside. `ScanConnectionReferenceLogicalNames` reads it directly via `XDocument.Descendants(...)` (not `Root.Element(...)`) — the exact nesting depth wasn't confirmed against a real unpacked-solution fixture in this repo, so the defensive, nesting-agnostic style already used by `DataverseContextGenerator.ReadConnectionReferences` (which reads the same file) was matched rather than assumed.
- **OptionSet's false positive was a different bug class entirely.** Its Solution.xml declaration shares WebResource's schemaName-only shape, but OptionSet (global choice) is Dataverse metadata, not a data-table row — `NameResolvableTypes`' `QueryExpression` pattern can't resolve it, so `ResolveNamedComponentIdsAsync` silently skipped componenttype 9 with no signal. Rather than another manual-bucket verification, this fix (`ResolveOptionSetMetadataIdsAsync`, mirroring `ResolveEntityMetadataIdsAsync`'s semaphore-bounded shape but using `RetrieveOptionSetRequest`) resolves still-declared OptionSets into `sNewIds` *before* the orphan diff runs — a still-declared OptionSet never becomes an orphan candidate at all, matching how Entity and WebResource schemaName roots already behave, not how the Manual bucket behaves.
- **A soft "possible match found locally" signal for everything still unsupported.** `BuildLocalIdentifierHarvest` unions every identifier already surfaced by the shapes above (`NamedComponents` schemaNames, `EntityLogicalNames`, the CustomApi/Bot/ConnectionReference scanners) into one flat, case-insensitive set, built once per run and passed into `LogUnsupportedOrphansAsync`. When an unsupported orphan's resolved name matches, the verbose line gains one sentence — control flow is untouched, the entry still never reaches `entries`/the report/the manual count. Deliberately scoped to identifiers already harvested for known shapes, never an unscoped repo-wide search. **Caveat found during implementation:** the signal is inert for any unsupported type whose instance name never resolves at all — `EnvironmentVariableDefinition` (componenttype 380) has a `ManualTypeLabels` entry for its type label, but no `NameResolvableTypes`/`ResolvedTypeNameAttributes` entry for its own name, so it can never produce a name to check against the harvest. The mechanism was proven instead against View (componenttype 26, already name-resolvable). Adding a name-resolution entry for 380 is a natural, low-risk follow-up if `EnvironmentVariableDefinition` needs this signal specifically — not done here since it wasn't part of this work's stated scope.

**Update (2026-07-07, part 9): three bugs in part 8's own work, all found by a code-review pass run immediately after implementation — a generalization effort re-derives its assumptions each time, or it silently carries the previous ones forward.** Part 8 generalized entity-side detection from CustomApi-only to also cover Bot and ConnectionReference. That generalization itself introduced three separate defects, none caught by the unit tests written alongside it:

- **Shared failure domain widened silently.** `IdentifyEntityDetectedTypesAsync` grew from three CustomApi-family queries under one `Task.WhenAll` to five (adding `bot`/`connectionreference`), but the single shared `try/catch` around the whole batch didn't grow with it — a failure on either of the two *new* tables (Bot/ConnectionReference tables aren't guaranteed present in every org edition, e.g. no Copilot Studio provisioning) silently blanked out detection for the three *original*, already-reliable CustomApi-family tables too. Fixed by isolating each table's query in its own `try/catch` (a local `TryQuery` helper), so one table's failure only removes that table's candidates from detection — the general rule: a shared batch's failure-isolation boundary has to be re-examined every time a new consumer joins it, especially one with different reliability characteristics than the existing consumers.
- **Unresolvable identity attribute defaulted to "orphaned."** `ResolveEntityDetectedManualEntriesAsync` (the new shared Bot/ConnectionReference resolve-and-suppress helper) was modeled on the pre-existing CustomApi code, which itself has always defaulted to reporting a component as orphaned when its live identity attribute fails to resolve (`if (!names.TryGetValue(...)) return true;`) — copied uncritically into the new helper. A record whose `schemaname`/`connectionreferencelogicalname` comes back null or empty was never actually checked against local source, but got reported as an actionable removal recommendation anyway — the exact false-positive shape `SupportedManualTypes`'s entire evidence-gated trust bar exists to prevent, reintroduced one level down inside a type that was supposed to be safely verified. Fixed to skip reporting when the identity attribute is unresolvable (`if (!names.TryGetValue(orphan.ObjectId, out var name)) continue;`) rather than treating "couldn't verify" as "verified as gone." The pre-existing CustomApi code has the same latent defect and was not touched by this fix — mirroring an existing pattern carries its bugs forward along with its logic.
- **OptionSet's exception handling conflated "not found" with "couldn't ask."** `ResolveOptionSetMetadataIdsAsync`'s per-item catch treated every exception identically as "this OptionSet was genuinely deleted," including network failures, auth failures, and throttling — a systemic failure degraded silently with no operator-visible signal. Fixed by catching `FaultException<OrganizationServiceFault>` specifically (the well-formed Dataverse business-logic response a genuine "not found" produces, matching the pattern `TryExecuteEntryAsync`'s dependency-error handling already uses elsewhere in this file) and warning on anything else.

None of these three would have been caught by generalizing correctly the first time — they're each a different way "reuse the existing pattern for a new case" goes wrong even when the reuse instinct itself is right: matching a pattern's *surface* (a working scanner) isn't the same as matching its *founding assumption* (a componenttype stable across environments, already corrected once in part 8 itself for the SupportedManualTypes decision); a batch's failure boundary that was fine for N consumers isn't automatically fine for N+2; and copying an existing check forward copies its bugs unless the copy is re-verified against what "no evidence either way" should actually do.

### CustomApi, Bot, and ConnectionReference detection via entity queries

Because these component types' componenttype numbers are env-specific, detection uses one parallel entity query per backing table, independent of componenttype entirely — each wrapped in its **own** `try/catch` (part 9), not one shared catch around the whole batch:

```csharp
async Task<IEnumerable<Entity>> TryQuery(string entityName, string idAttr)
{
    try
    {
        return await service.RetrieveAllAsync(EntityQuery(entityName, idAttr, idArray), ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        console.Warning($"Entity-side orphan detection failed for '{entityName}' ({Markup.Escape(ex.Message)}) — its orphan candidates will be marked Manual.");
        return [];
    }
}

var caTask    = TryQuery("customapi",                 "customapiid");
var paramTask = TryQuery("customapirequestparameter", "customapirequestparameterid");
var propTask  = TryQuery("customapiresponseproperty", "customapiresponsepropertyid");
var botTask   = TryQuery("bot",                       "botid");
var crTask    = TryQuery("connectionreference",       "connectionreferenceid");
await Task.WhenAll(caTask, paramTask, propTask, botTask, crTask).ConfigureAwait(false);

var result = new Dictionary<Guid, string>();
foreach (var e in caTask.Result)    result[e.Id] = "customapi";
foreach (var e in paramTask.Result) result[e.Id] = "customapirequestparameter";
foreach (var e in propTask.Result)  result[e.Id] = "customapiresponseproperty";
foreach (var e in botTask.Result)   result[e.Id] = "bot";
foreach (var e in crTask.Result)    result[e.Id] = "connectionreference";
```

`TryQuery`'s catch is the same deliberate exception to the project-wide "no try/catch around service calls" rule as before — failure has explicit recovery logic (drop that table's candidates, warn, continue) — but it's now scoped per table instead of around the whole `Task.WhenAll`, so one table's failure can't take the others down with it (part 9).

### Dependency execution order

The deletion order matches the Dataverse dependency chain:

```csharp
// step images → steps → plugin types → assemblies; web resources; workflows
static readonly int[] ExecutionOrder = [93, 92, 90, 91, 61, 29];

// CustomApi: children deleted before parent
static readonly string[] CustomApiEntityOrder = ["customapirequestparameter", "customapiresponseproperty", "customapi"];
```

Violating this order causes `FaultException<OrganizationServiceFault>` with error code `0x80047002`. The pre-import phase catches dependency errors specifically and defers rather than failing:

```csharp
catch (FaultException<OrganizationServiceFault> ex) when (!isPostImport && IsDependencyError(ex))
{
    deferred.Add(entry);
}

static bool IsDependencyError(FaultException<OrganizationServiceFault> ex) =>
    ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
    (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
```

Post-import failures are non-blocking — they return a count, and the deploy command exits with `ExitCode.PartialSuccess = 18`.

### Workflow deactivation before delete

Active workflows cannot be deleted. Deactivate first, then delete. Deactivation failure is recoverable — downgrade to Manual:

```csharp
if (entry.ComponentType == 29 && entry.Action == OrphanAction.Delete)
{
    var deactivated = await TryDeactivateWorkflowAsync(service, entry.ObjectId, ct);
    if (!deactivated)
    {
        output.Warning($"'{entry.DisplayName}' — workflow deactivation failed, remove manually via maker portal.");
        return;
    }
}
```

`TryDeactivateWorkflowAsync` sets `statecode=0` (Draft), `statuscode=1`, and swallows `FaultException<OrganizationServiceFault>` — the second intentional exception to the no-catch rule, with explicit recovery: return false → caller downgrades to Manual.

### Cross-solution membership: RemoveSolutionComponent vs Delete

An orphaned component may still belong to another solution. Query all memberships and use `RemoveSolutionComponent` instead of `Delete` when shared:

```csharp
var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;

// For RemoveFromSolution:
await service.ExecuteAsync(new OrganizationRequest("RemoveSolutionComponent")
{
    ["ComponentId"]        = entry.ObjectId,
    ["ComponentType"]      = entry.ComponentType,
    ["SolutionUniqueName"] = solutionName
}, ct);
```

### Guard: empty S_new prevents mass deletion

If `Solution.xml` has no `<RootComponents>`, the diff produces every existing component as an orphan. Guard and short-circuit:

```csharp
if (sNew.Count == 0)
{
    output.Warning("No components in Solution.xml — orphan check skipped to prevent mass deletion.");
    return [];
}
```

### ConditionOperator.In hard limit

Dataverse `ConditionOperator.In` is limited to 2000 values. Throw before reaching Dataverse — do not silently chunk (chunking changes query semantics and can mask issues):

```csharp
if (ids.Count > 2000)
    throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {ids.Count} IDs (max 2000).");
```

Applied in `IdentifyEntityDetectedTypesAsync` (renamed from `IdentifyCustomApiEntityTypesAsync` in part 8), `GetCrossSolutionMembershipAsync`, and `GetStillPresentAsync`.

### --no-delete flag: report without acting

`--no-delete` maps to `RunMode.NoDelete`. Pre-import prints the full report but returns an empty deferred list. Post-import is skipped entirely. Mirrors the `--no-delete` flag on `push` — consistent "report without acting" pattern across Flowline commands.

**Update (2026-07-07, part 10): the comparison itself is now a reusable, standalone `drift <target>` command, not just a deploy-time side effect.** `RunPreImportAsync` (query live components, resolve `sNewIds` via every identity shape above, classify, print) is split into a new `CompareAsync(PostDeployContext, ct)` that returns a `CompareResult(Entries, Skipped)` — `RunPreImportAsync` is now a thin wrapper: call `CompareAsync`, then (unless `RunMode.NoDelete`) call `ExecuteInOrderAsync` on `result.Entries`, exactly as before the split. Every guard documented above — `SupportedManualTypes`, the empty-`S_new`/empty-live short-circuits, `RetrieveAllAsync`, the `ConditionOperator.In` cap, per-table failure isolation — moved verbatim; nothing about deploy's own orphan-cleanup behavior changed. The new `flowline drift <target> [solution]` command calls `CompareAsync` directly against any named environment (dev/test/uat/prod), always in `RunMode.NoDelete`, and never calls `ExecuteInOrderAsync` — a read-only report, usable as a standalone drift check against prod/test or a pre-sync/pre-deploy preview against dev. `OrphanCleanupService` had to be registered in DI as itself, **replacing** its previous `IPostDeployService`-only registration (`services.AddSingleton<OrphanCleanupService>()` plus a forwarding `IPostDeployService` registration resolving to that same singleton), so `DriftCommand` could depend on it directly. Adding the new registrations *alongside* the old one instead of replacing it would have left two independent `IPostDeployService` entries resolving to two different instances — the exact "deploy's orphan-cleanup fan-out runs twice" bug this change exists to avoid.

**Known limitation, not a bug:** `CompareAsync`'s comparison only computes one direction — "present live, not declared in committed source" (which is all deploy's mutating cleanup ever needed; a component missing from live doesn't need cleanup, since `deploy`'s own import recreates it). `drift` inherits that same one-directional scope: it detects components created directly in the target or added in DEV since the last sync, but not components deleted directly in a live environment while committed source still declares them. Building that reverse direction is new comparison logic, not a byproduct of this extraction — an unresolved live identity in that direction would need to *suppress*, not report as "missing," mirroring exactly the part 9 false-positive class this document exists to prevent. Deferred rather than built ad hoc; see `docs/plans/2026-07-07-002-feat-drift-preview-diff-engine-plan.md`.

**Update (2026-07-08, code review): two false-clean-result gaps found and fixed before merge.** First, `CompareAsync`'s empty-`S_new`/empty-live short-circuits (both "skip" cases, above) returned the same empty entry list as a genuinely-verified zero-drift comparison — `drift` couldn't tell "nothing found" from "the check didn't run." Fixed by adding `Skipped` to the new `CompareResult` record; `DriftCommand` maps a skipped comparison to a new `ExitCode.Inconclusive` (`19`) instead of `Success`. Second, `DriftCommand`'s solution lookup (`GetAndCheckSolutionAsync` → `FlowlineValidator.GetSolutionInfoAsync`) used the same 4-hour solution cache every command does — harmless for `deploy`/`sync`, where a stale "solution still exists" entry gets caught by the import/export step that follows, but `drift` has no such downstream check, so a solution deleted or renamed in the target could read as "no drift" until the cache expired. Fixed by adding an opt-in `bypassCache` parameter to `GetAndCheckSolutionAsync` (default `false`, so every other caller is unaffected) and having `DriftCommand` pass `bypassCache: true`.

### Deploy command execution order

```
ValidateTarget → ValidateDtap → DriftCheck → ParseSolutionXml →
ConnectDataverse → RunPreImport → Pack → Import → RunPostImport
```

Pre-import runs before Pack: if Dataverse connection or the orphan query fails, the user gets a fast error before the slow pack step.

**Update (2026-07-03):** `RunPreImport`/`RunPostImport` are no longer direct calls to `OrphanCleanupService`. `DeployCommand` builds one `PostDeployContext` per deploy and fans out over every registered `IEnumerable<IPostDeployService>` implementer at each point (`foreach (var postDeployService in postDeployServices) await postDeployService.RunPreImportAsync(...)`, and symmetrically for post-import) — `OrphanCleanupService` is currently the only registered implementer, but the pipeline now runs all of them, not just it. See [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) for why this changed and what it enables.

## Why This Matters

**Without this pattern:**
- Orphaned `plugintype` records block DLL upload — hard failure requiring manual portal intervention.
- Orphaned step configurations keep firing on messages even after the plugin class is gone — silent runtime errors.
- Orphaned workflows may keep running, consuming async job capacity.

**With this pattern:**
- Deploy is idempotent with respect to deletions: running deploy twice leaves the environment in the same state.
- Cross-solution safety: components shared with other solutions are unlinked, not deleted.
- Dependency errors are handled via deferral rather than requiring retry logic from the caller.
- `ExitCode.PartialSuccess = 18` gives CI pipelines a distinct signal: deploy succeeded, cleanup partially failed — human review needed, not a full redeploy.

**The two catches in `OrphanCleanupService` are intentional deviations from the project-wide no-catch rule.** Both have explicit recovery logic: the dependency catch defers the entry; the CustomApi detection catch downgrades unknowns to Manual. Neither swallows silently; both produce visible user-facing output.

## When to Apply

Apply this pattern when all of the following are true:

1. The target is an additive import platform — Dataverse unmanaged solution import never removes components.
2. Components have a stable local manifest — `Solution.xml` `<RootComponents>` is the source of truth.
3. Components have referential integrity — deletion must respect a dependency graph.
4. Components may be shared across deployments — requiring membership checks before delete.

Do not apply the deferral pattern if the platform cascades deletes automatically, or if the full dependency graph is cheaply computable upfront (topological sort is cleaner than catch-and-retry in that case).

## Examples

**Plugin class removed from DLL (hard-failure case resolved):**
```
$ flowline deploy prod
Querying orphan components in MySolution...
Orphan components (2):
  PluginType 3a9f... — delete
  SdkMessageProcessingStep b12c... — delete
2 to delete, 0 to remove from solution, 0 manual
Packing MySolution...
Deploying MySolution to Production...
Deployed! Your solution is live.
```

**Component shared with another solution → RemoveFromSolution:**
```
Orphan components (1):
  WebResource 7d44... — remove from solution
0 to delete, 1 to remove from solution, 0 manual
```

**`--no-delete` for pre-deploy audit:**
```
$ flowline deploy prod --no-delete
Orphan components (3):
  SdkMessageProcessingStep f8a1... — would delete
  PluginType 92cc... — would delete
  PluginAssembly 1d73... — would delete
3 would be deleted, 0 would be removed from solution, 0 manual. (--no-delete active)
```

**Dependency deferral in action:**
```
Deferred: PluginAssembly 1d73... — dependency, will retry post-import
[import completes]
Post-import: retrying deferred orphan cleanup...
PluginAssembly 1d73... deleted
```

**Partial failure exit code for CI (`ExitCode.PartialSuccess = 18`):**
```
WARNING: 1 orphan component couldn't be cleaned up — see above, remove manually via maker portal.
$ echo $?
18
```

Deploy succeeded; manual cleanup required. CI can alert without treating this as a full deploy failure.

## Related

- [post-deploy-service-di-fanout-protocol.md](post-deploy-service-di-fanout-protocol.md) — `OrphanCleanupService` is now the first implementer of `IPostDeployService`; the interface/DI shape wrapping this algorithm, and the deferred-state instance-field mechanism it relies on
- [retrieve-multiple-async-silent-truncation-2026-05-29.md](../logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md) — why `RetrieveAllAsync` is mandatory for solutioncomponent queries
- [dtap-gate-enforcement-in-deploy-command-2026-06-07.md](dtap-gate-enforcement-in-deploy-command-2026-06-07.md) — other deploy pipeline guards; execution order now includes pre/post-import orphan cleanup phases
- [managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md](managed-unmanaged-type-guard-in-deploy-command-2026-06-07.md) — managed/unmanaged type guard in the same pipeline
- [ai-agent-consumable-cli-contract-2026-06-07.md](ai-agent-consumable-cli-contract-2026-06-07.md) — `ExitCode` enum as stable public API; `PartialSuccess = 18` added in this work
