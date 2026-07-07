using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.Services;

public enum OrphanAction { Delete, RemoveFromSolution, Manual }

public sealed record OrphanEntry(Guid ObjectId, int ComponentType, string DisplayName, OrphanAction Action, string? EntityName = null);

public class OrphanCleanupService(IAnsiConsole console, FlowlineRuntimeOptions opt) : IPostDeployService
{
    // R8: step images → steps → types → assemblies; web resources; workflows
    // CustomApi family has env-specific componenttype — handled separately via entity-side detection.
    static readonly int[] ExecutionOrder = [93, 92, 90, 91, 61, 29];

    // Threads dependency-deferred entries from RunPreImportAsync to RunPostImportAsync on the same instance.
    IReadOnlyList<OrphanEntry> _deferred = [];

    // CustomApi child entities deleted before parent to avoid dependency errors.
    static readonly string[] CustomApiEntityOrder = ["customapirequestparameter", "customapiresponseproperty", "customapi"];

    static readonly Dictionary<int, string> EntityNames = new()
    {
        [91] = "pluginassembly",
        [90] = "plugintype",
        [92] = "sdkmessageprocessingstep",
        [93] = "sdkmessageprocessingstepimage",
        [61] = "webresource",
        [29] = "workflow",
    };

    // Manual-orphan display labels for solutioncomponent.componenttype, verified against the
    // "Solution Component" table reference (learn.microsoft.com/power-apps/developer/data-platform/
    // reference/entities/solutioncomponent). Not exhaustive — covers types plausible in a manual-cleanup
    // report. Types outside this map (e.g. env-specific 10000+ codes not in the public componenttype
    // choice set) are reported as unrecognized rather than guessed at.
    static readonly Dictionary<int, string> ManualTypeLabels = new()
    {
        [1]   = "Entity",
        [2]   = "Attribute",
        [3]   = "Relationship",
        [9]   = "OptionSet",
        [14]  = "EntityKey",
        [20]  = "Role",
        [24]  = "Form",
        [26]  = "View",
        [36]  = "EmailTemplate",
        [44]  = "DuplicateRule",
        [46]  = "EntityMap",
        [60]  = "Form",
        [62]  = "SiteMap",
        [63]  = "ConnectionRole",
        [66]  = "CustomControl",
        [70]  = "FieldSecurityProfile",
        [71]  = "FieldPermission",
        [95]  = "ServiceEndpoint",
        [150] = "RoutingRule",
        [152] = "SLA",
        [161] = "MobileOfflineProfile",
        [165] = "SimilarityRule",
        [166] = "DataSourceMapping",
        [208] = "ImportMap",
        [300] = "CanvasApp",
        [371] = "Connector",
        [372] = "Connector",
        [380] = "EnvironmentVariableDefinition",
        [381] = "EnvironmentVariableValue",
    };

    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        _deferred = [];

        var entries = await CompareAsync(context, ct).ConfigureAwait(false);

        if (context.Mode == RunMode.NoDelete)
            return;

        _deferred = await ExecuteInOrderAsync(context.Service, context.SolutionName, entries, isPostImport: false, ct).ConfigureAwait(false);
    }

    // Comparison-only half of the pre-import step (KTD5): queries live solutioncomponents, resolves
    // sNewIds via all existing special-casing (schemaName, entity, OptionSet, CustomApi, Bot,
    // ConnectionReference), classifies orphans, and prints the report — stopping before
    // ExecuteInOrderAsync (the mutating delete/remove step), so this is callable from a future
    // read-only context (e.g. a `drift` command) without mutating anything.
    public async Task<IReadOnlyList<OrphanEntry>> CompareAsync(PostDeployContext context, CancellationToken ct)
    {
        var service      = context.Service;
        var solutionName = context.SolutionName;
        var sNew         = context.LocalComponents;
        var mode         = context.Mode;

        var sOld = await console.Status().FlowlineSpinner()
            .StartAsync($"Querying orphan components in [bold]{solutionName}[/]...",
                _ => QuerySolutionComponentsAsync(service, solutionName, ct))
            .ConfigureAwait(false);

        if (sOld.Count == 0)
        {
            console.Skip("No solution components in Dataverse — skipping orphan check.");
            return [];
        }

        var sNewIds = sNew.Select(c => c.ObjectId).ToHashSet();

        if (sNew.Count == 0)
        {
            console.Warning("No components in Solution.xml — orphan check skipped to prevent mass deletion.");
            return [];
        }

        // Entity roots in Solution.xml are recorded by schemaName, not MetadataId — resolve them live
        // so entity components aren't misdiagnosed as orphans. See ComponentClassifier.ParseSolutionXmlComponents.
        if (context.EntityLogicalNames.Count > 0)
        {
            var resolvedEntityIds = await ResolveEntityMetadataIdsAsync(service, context.EntityLogicalNames, ct).ConfigureAwait(false);
            sNewIds.UnionWith(resolvedEntityIds);
        }

        // Other types recorded by schemaName instead of id (e.g. WebResource — its id is not portable
        // across environments, so pac always records it by name) — resolve live for the same reason.
        if (context.NamedComponents.Count > 0)
        {
            var resolvedNamedIds = await ResolveNamedComponentIdsAsync(service, context.NamedComponents, ct).ConfigureAwait(false);
            sNewIds.UnionWith(resolvedNamedIds);
        }

        // OptionSet (9) roots are also schemaName-declared in Solution.xml, but OptionSet is metadata,
        // not a data-table row — NameResolvableTypes' QueryExpression pattern can't resolve it, so
        // ResolveNamedComponentIdsAsync silently skips it. Resolve it via a metadata request instead
        // (RetrieveOptionSetRequest) and fold it into sNewIds the same way, before the orphan diff runs
        // (KTD1) — a still-declared OptionSet must never become an orphan candidate at all.
        var optionSetSchemaNames = context.NamedComponents
            .Where(c => c.ComponentType == OptionSetComponentType)
            .Select(c => c.SchemaName)
            .ToList();
        if (optionSetSchemaNames.Count > 0)
        {
            var resolvedOptionSetIds = await ResolveOptionSetMetadataIdsAsync(service, optionSetSchemaNames, ct).ConfigureAwait(false);
            sNewIds.UnionWith(resolvedOptionSetIds);
        }

        var orphans = sOld
            .Where(c => !sNewIds.Contains(c.ObjectId))
            .Where(c => !ComponentClassifier.IsWellKnownSystemComponent(c.ObjectId))
            .ToList();

        if (orphans.Count == 0)
        {
            console.Ok("No orphan components.");
            return [];
        }

        var autoOrphans    = orphans.Where(c => ComponentClassifier.Classify(c.ComponentType) == ComponentAction.AutoDelete).ToList();
        var unknownOrphans = orphans.Where(c => ComponentClassifier.Classify(c.ComponentType) == ComponentAction.Manual).ToList();

        // Exempt webresource orphans referenced in // flowline:depends annotations.
        autoOrphans = await ExemptAnnotationReferencedWebResourcesAsync(service, autoOrphans, context.PackageSrcRoot, ct).ConfigureAwait(false);

        // CustomApi and Bot both have env-specific componenttypes — detect via entity-side queries
        // instead (see IdentifyEntityDetectedTypesAsync).
        Dictionary<Guid, string> entityDetectedTypes = [];
        if (unknownOrphans.Count > 0)
        {
            try
            {
                entityDetectedTypes = await IdentifyEntityDetectedTypesAsync(service, unknownOrphans.Select(c => c.ObjectId), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                console.Warning($"Entity-side orphan detection failed ({Markup.Escape(ex.Message)}) — unknown orphan components will be marked Manual.");
            }
        }

        var customApiOrphans           = unknownOrphans.Where(c => entityDetectedTypes.TryGetValue(c.ObjectId, out var e) && CustomApiIdAttributes.ContainsKey(e)).ToList();
        var botOrphans                 = unknownOrphans.Where(c => entityDetectedTypes.TryGetValue(c.ObjectId, out var e) && e == "bot").ToList();
        var connectionReferenceOrphans = unknownOrphans.Where(c => entityDetectedTypes.TryGetValue(c.ObjectId, out var e) && e == "connectionreference").ToList();
        var manualOrphans              = unknownOrphans.Where(c => !entityDetectedTypes.ContainsKey(c.ObjectId)).ToList();

        // CustomApi source has no GUID at all — uniquename is the only local identity (see
        // ComponentClassifier.ScanCustomApiNames) — so a recreated CustomApi (same uniquename, new
        // customapiid) looks orphaned by id alone. Resolve names once here, drop the ones still in
        // source, and reuse the same lookup below instead of querying names twice.
        Dictionary<Guid, string> customApiNames = [];
        if (customApiOrphans.Count > 0)
        {
            foreach (var group in customApiOrphans.GroupBy(o => entityDetectedTypes[o.ObjectId]))
            {
                var names = await GetEntityNamesAsync(service, group.Key, CustomApiIdAttributes[group.Key], "name", group.Select(o => o.ObjectId), ct).ConfigureAwait(false);
                foreach (var (id, name) in names)
                    customApiNames[id] = name;
            }

            var localCustomApiNames = ComponentClassifier.ScanCustomApiNames(context.PackageSrcRoot);
            customApiOrphans = customApiOrphans.Where(o =>
            {
                if (!customApiNames.TryGetValue(o.ObjectId, out var name)) return true;
                var localNames = entityDetectedTypes[o.ObjectId] switch
                {
                    "customapi"                 => localCustomApiNames.ApiUniqueNames,
                    "customapirequestparameter" => localCustomApiNames.RequestParameterNames,
                    "customapiresponseproperty" => localCustomApiNames.ResponsePropertyNames,
                    _                           => (IReadOnlySet<string>)new HashSet<string>()
                };
                return !localNames.Contains(name);
            }).ToList();
        }

        // Bot and ConnectionReference share the same entity-detected, no-componenttype-gate shape —
        // resolve each candidate's live identity attribute and drop the ones still declared locally
        // (KTD2). Bot's identity is schemaname, not name (KTD3) — ResolvedTypeNameAttributes' bot entry
        // maps to name for the unsupported-preview's display purpose only, never for verification.
        // ConnectionReference has no dedicated folder like Bot's bots/<schemaname>/bot.xml — it's
        // declared inline in Other/Customizations.xml's <connectionreferences> section (R2), not the
        // separately-generated deploymentSettings.json, which is optional and can go stale.
        var botEntries = await ResolveEntityDetectedManualEntriesAsync(
            service, botOrphans, "bot", "botid", "schemaname",
            ComponentClassifier.ScanBotSchemaNames, context.PackageSrcRoot, ct).ConfigureAwait(false);
        var connectionReferenceEntries = await ResolveEntityDetectedManualEntriesAsync(
            service, connectionReferenceOrphans, "connectionreference", "connectionreferenceid", "connectionreferencelogicalname",
            ComponentClassifier.ScanConnectionReferenceLogicalNames, context.PackageSrcRoot, ct).ConfigureAwait(false);

        var crossSolutionIds = autoOrphans.Select(c => c.ObjectId).Concat(customApiOrphans.Select(c => c.ObjectId));
        var crossSolution = crossSolutionIds.Any()
            ? await GetCrossSolutionMembershipAsync(service, crossSolutionIds, ct).ConfigureAwait(false)
            : [];

        var entries = new List<OrphanEntry>();

        foreach (var group in autoOrphans.GroupBy(o => o.ComponentType))
        {
            var names = await ResolveGroupNamesAsync(service, group.Key, group.Select(o => o.ObjectId), ct).ConfigureAwait(false);

            foreach (var orphan in group)
            {
                var otherSolutions = OtherRelevantSolutions(crossSolution, orphan.ObjectId, solutionName);
                var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
                var detail = names.TryGetValue(orphan.ObjectId, out var name) ? name : null;
                entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId, detail: detail), action));
            }
        }

        foreach (var group in customApiOrphans.GroupBy(o => entityDetectedTypes[o.ObjectId]))
        {
            var entityName = group.Key;

            foreach (var orphan in group)
            {
                var otherSolutions = OtherRelevantSolutions(crossSolution, orphan.ObjectId, solutionName);
                var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
                var detail = customApiNames.TryGetValue(orphan.ObjectId, out var name) ? name : null;
                entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId, entityName, detail), action, EntityName: entityName));
            }
        }

        // Bot and ConnectionReference can't be deleted automatically like CustomApi — genuinely-orphaned
        // ones are reported Manual, same as any other unsupported/opt-in-gated type, but bypass
        // SupportedManualTypes entirely per KTD2 (entity-side detection, not a componenttype gate).
        entries.AddRange(botEntries);
        entries.AddRange(connectionReferenceEntries);

        entries.AddRange(await BuildManualEntriesAsync(service, context, manualOrphans, ct).ConfigureAwait(false));

        PrintReport(entries, mode, solutionName, context.EnvironmentUrl);

        return entries;
    }

    public async Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var service      = context.Service;
        var solutionName = context.SolutionName;
        var sNew         = context.LocalComponents;
        var mode         = context.Mode;
        var deferred     = _deferred;

        if (deferred.Count == 0 || mode == RunMode.NoDelete)
            return 0;

        var sNewIds     = sNew.Select(c => c.ObjectId).ToHashSet();
        var deferredIds = deferred.Select(e => e.ObjectId).ToList();

        var stillPresent  = await GetStillPresentAsync(service, solutionName, deferredIds, ct).ConfigureAwait(false);
        var presentIds    = stillPresent.ToList();
        var crossSolution = presentIds.Count > 0
            ? await GetCrossSolutionMembershipAsync(service, presentIds, ct).ConfigureAwait(false)
            : [];

        var reEntries = new List<OrphanEntry>();
        foreach (var entry in deferred)
        {
            if (!stillPresent.Contains(entry.ObjectId)) continue;
            if (sNewIds.Contains(entry.ObjectId)) continue;

            var otherSolutions = OtherRelevantSolutions(crossSolution, entry.ObjectId, solutionName);

            var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
            reEntries.Add(entry with { Action = action });
        }

        if (reEntries.Count == 0)
            return 0;

        console.Skip("Post-import: retrying deferred orphan cleanup...");
        var failed = await ExecuteInOrderAsync(service, solutionName, reEntries, isPostImport: true, ct).ConfigureAwait(false);
        return failed.Count;
    }

    const int MaxConcurrentMetadataRequests = 20;

    static async Task<HashSet<Guid>> ResolveEntityMetadataIdsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<string> logicalNames,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentMetadataRequests, MaxConcurrentMetadataRequests);

        var tasks = logicalNames.Select(async name =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var request = new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = false };
                var response = (RetrieveEntityResponse)await service.ExecuteAsync(request, ct).ConfigureAwait(false);
                return response.EntityMetadata?.MetadataId;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var metadataIds = await Task.WhenAll(tasks).ConfigureAwait(false);
        return metadataIds.Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
    }

    const int OptionSetComponentType = 9;

    // OptionSet's own metadata-request resolution path (see the call site in RunPreImportAsync) — kept
    // separate from ResolveNamedComponentIdsAsync's NameResolvableTypes/QueryExpression pattern since
    // OptionSet has no backing data table to query. Unlike ResolveEntityMetadataIdsAsync, a failed
    // request here (e.g. a genuinely-deleted global choice) is caught per-name so it doesn't block
    // resolution of the others — RetrieveOptionSetRequest throws for a name that no longer exists,
    // whereas RetrieveEntityRequest's precondition (context.EntityLogicalNames) never includes deleted
    // entities in the first place.
    async Task<HashSet<Guid>> ResolveOptionSetMetadataIdsAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<string> schemaNames,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentMetadataRequests, MaxConcurrentMetadataRequests);

        var tasks = schemaNames.Distinct().Select(async name =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var request = new RetrieveOptionSetRequest { Name = name };
                var response = (RetrieveOptionSetResponse)await service.ExecuteAsync(request, ct).ConfigureAwait(false);
                return response.OptionSetMetadata?.MetadataId;
            }
            // A genuinely-deleted global choice faults at the organization-service level — the same
            // "well-formed Dataverse business-logic response" shape TryExecuteEntryAsync already treats
            // as expected elsewhere in this file. Anything else (network, auth, throttling) is a real
            // failure the operator should see, not silently equivalent to "not found".
            catch (FaultException<OrganizationServiceFault>)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                console.Warning($"OptionSet metadata lookup for '{name}' failed ({Markup.Escape(ex.Message)}) — treating as unresolved this run.");
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var metadataIds = await Task.WhenAll(tasks).ConfigureAwait(false);
        return metadataIds.Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
    }

    // Resolves non-entity schemaName-recorded RootComponents (e.g. WebResource) to their live id, by
    // querying each type's NameResolvableTypes-mapped table for name-attribute IN (schemaNames). A type
    // not present in NameResolvableTypes is skipped — same as before this method existed, it's just not
    // folded into sNewIds — rather than guessed at.
    static async Task<HashSet<Guid>> ResolveNamedComponentIdsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents,
        CancellationToken ct)
    {
        var result = new HashSet<Guid>();

        foreach (var group in namedComponents.GroupBy(c => c.ComponentType))
        {
            if (!NameResolvableTypes.TryGetValue(group.Key, out var lookup)) continue;

            var names = group.Select(c => (object)c.SchemaName).Distinct().ToArray();
            if (names.Length == 0) continue;
            if (names.Length > 2000)
                throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {names.Length} names (max 2000). Solution has too many {lookup.EntityLogicalName} schemaName roots for live resolution.");

            var query = new QueryExpression(lookup.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                Criteria  = { Conditions = { new ConditionExpression(lookup.NameAttribute, ConditionOperator.In, names) } }
            };

            var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
            foreach (var entity in entities)
                result.Add(entity.Id);
        }

        return result;
    }

    const int EntityComponentType = 1;
    const int AttributeComponentType = 2;
    const int RoleComponentType = 20;

    // Manual-bucket types Flowline recommends removal for — each has a local-source cross-check
    // (Entity: EntityLogicalNames/RetrieveEntityRequest; Attribute: Entity.xml scan) verified against a
    // real org. Recommending removal for a type without that verification is a guess, not a finding —
    // two incidents confirm it (2026-07-05): connectionreference/bot resolved a real display name via
    // solutioncomponentdefinition but have no local representation at all, so a still-needed connection
    // reference got flagged; and Form (60) — despite ScanEntitySubcomponents — false-positives for any
    // form whose entity isn't unpacked under Entities/<name>/ locally (e.g. a standard Microsoft form
    // like "Sales Insights" living on an entity this solution's Entities/ folder doesn't include at
    // all — behavior="1" entities still get FormXml unpacked for editing, but a form on an entity
    // outside that set has nothing for the scan to find). View (26) shares the same untested gap.
    // Role (20) needs no new scanner: its id is declared directly in Solution.xml's RootComponent and
    // mirrored in the unpacked Roles/<name>.xml file, so the existing plain id-in-LocalComponents match
    // already resolves it correctly in both directions (2026-07-06).
    // Widen this set only after adding the equivalent local-source verification and tests for a new
    // type — everything else is logged at verbose only, never recommended for action.
    static readonly HashSet<int> SupportedManualTypes = [EntityComponentType, AttributeComponentType, RoleComponentType];

    // componenttype → backing table, keyed by the componenttype value, resolvable via a single bulk
    // QueryExpression per type (same pattern as the original webresource-only name lookup). Covers both
    // AutoDelete types (so deploy shows what it's actually deleting, not just a GUID) and the common
    // Manual types. Column names verified against existing queries in PluginReader.cs.
    static readonly Dictionary<int, (string EntityLogicalName, string IdAttribute, string NameAttribute)> NameResolvableTypes = new()
    {
        [91] = ("pluginassembly", "pluginassemblyid", "name"),
        [90] = ("plugintype", "plugintypeid", "typename"),
        [92] = ("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name"),
        [93] = ("sdkmessageprocessingstepimage", "sdkmessageprocessingstepimageid", "name"),
        [61] = ("webresource", "webresourceid", "name"),
        [29] = ("workflow", "workflowid", "name"),
        [60] = ("systemform", "formid", "name"),
        [26] = ("savedquery", "savedqueryid", "name"),
        [20] = ("role", "roleid", "name"),
        [63] = ("connectionrole", "connectionroleid", "name"),
    };

    // CustomApi family entities are detected by entity-side query rather than componenttype (see
    // IdentifyEntityDetectedTypesAsync) — keyed by entity LogicalName instead. Bot uses the same
    // detection query but resolves its display name via schemaname, not this map — see the botOrphans
    // block in RunPreImportAsync.
    static readonly Dictionary<string, string> CustomApiIdAttributes = new()
    {
        ["customapi"]                 = "customapiid",
        ["customapirequestparameter"] = "customapirequestparameterid",
        ["customapiresponseproperty"] = "customapiresponsepropertyid",
    };

    // Resolves componenttype → display name via solutioncomponentdefinition, Dataverse's own lookup
    // table for component types (documented for the Web API as GET solutioncomponentdefinitions?
    // $select=name,solutioncomponenttype — see Power Pages CLI solution docs). Used ONLY for the
    // unsupportedOrphans verbose preview below — it identifies a type, it does not verify one, so it
    // must never feed into SupportedManualTypes or the printed report.
    static async Task<Dictionary<int, string>> ResolveComponentTypeNamesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<int> componentTypes,
        CancellationToken ct)
    {
        var types = componentTypes.Distinct().Select(t => (object)t).ToArray();
        if (types.Length == 0) return [];

        var query = new QueryExpression("solutioncomponentdefinition")
        {
            ColumnSet = new ColumnSet("name", "solutioncomponenttype"),
            Criteria  = { Conditions = { new ConditionExpression("solutioncomponenttype", ConditionOperator.In, types) } }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        var result = new Dictionary<int, string>();
        foreach (var entity in entities)
        {
            var name = entity.GetAttributeValue<string>("name");
            if (string.IsNullOrEmpty(name)) continue;

            var type = entity["solutioncomponenttype"] switch
            {
                OptionSetValue osv => osv.Value,
                int i => i,
                _ => (int?)null
            };
            if (type.HasValue)
                result[type.Value] = name;
        }

        return result;
    }

    // solutioncomponentdefinition.name for env-specific types is literally the backing table's
    // LogicalName (confirmed against a real org: connectionreference/bot resolve to those exact
    // strings) — so the resolved label doubles as the entity to query for the record's own name.
    // Verbose-preview only, same caveat as ResolveComponentTypeNamesAsync above.
    static readonly Dictionary<string, (string IdAttribute, string NameAttribute)> ResolvedTypeNameAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connectionreference"] = ("connectionreferenceid", "connectionreferencelogicalname"),
        ["bot"]                 = ("botid", "name"),
    };

    // Builds display entries for orphans that fall outside Classify's AutoDelete set. Attribute (2)
    // orphans get special handling: Solution.xml's RootComponents never lists them (see
    // ComponentClassifier.ParseSolutionXmlComponents), so — same as the entity/form/view gaps fixed
    // earlier — a customized-but-still-present attribute on a solution entity looks orphaned. Resolving
    // to (entity, attribute) LogicalName lets us check Entity.xml and suppress those false positives;
    // genuine leftovers get a real name instead of a bare GUID.
    async Task<List<OrphanEntry>> BuildManualEntriesAsync(
        IOrganizationServiceAsync2 service,
        PostDeployContext context,
        List<(Guid ObjectId, int ComponentType)> manualOrphans,
        CancellationToken ct)
    {
        var attributeOrphans = manualOrphans.Where(c => c.ComponentType == AttributeComponentType).ToList();
        var otherOrphans     = manualOrphans.Where(c => c.ComponentType != AttributeComponentType).ToList();

        var entries = new List<OrphanEntry>();

        if (attributeOrphans.Count > 0 && context.EntityLogicalNames.Count > 0)
        {
            var attributeInfo = await ResolveAttributeInfoAsync(service, context.EntityLogicalNames, attributeOrphans.Select(o => o.ObjectId), ct).ConfigureAwait(false);
            var localAttributesByEntity = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var orphan in attributeOrphans)
            {
                if (!attributeInfo.TryGetValue(orphan.ObjectId, out var info))
                {
                    entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId), OrphanAction.Manual));
                    continue;
                }

                if (!localAttributesByEntity.TryGetValue(info.EntityLogicalName, out var localAttributes))
                    localAttributesByEntity[info.EntityLogicalName] = localAttributes =
                        ComponentClassifier.ScanEntityAttributeLogicalNames(context.PackageSrcRoot, info.EntityLogicalName);

                if (localAttributes.Contains(info.AttributeLogicalName))
                    continue; // still declared in Entity.xml — false positive, not an orphan

                var detail = $"{info.EntityLogicalName}.{info.AttributeLogicalName}";
                entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId, detail: detail), OrphanAction.Manual));
            }
        }
        else
        {
            foreach (var orphan in attributeOrphans)
                entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId), OrphanAction.Manual));
        }

        // Only recommend removal for types Flowline has verified via local source (see
        // SupportedManualTypes) — everything else is surfaced at verbose only, not in the report.
        var supportedOrphans   = otherOrphans.Where(o => SupportedManualTypes.Contains(o.ComponentType)).ToList();
        var unsupportedOrphans = otherOrphans.Where(o => !SupportedManualTypes.Contains(o.ComponentType)).ToList();

        if (unsupportedOrphans.Count > 0)
        {
            var localIdentifiers = BuildLocalIdentifierHarvest(context);
            await LogUnsupportedOrphansAsync(service, unsupportedOrphans, localIdentifiers, ct).ConfigureAwait(false);
        }

        foreach (var group in supportedOrphans.GroupBy(o => o.ComponentType))
        {
            var names = await ResolveGroupNamesAsync(service, group.Key, group.Select(o => o.ObjectId), ct).ConfigureAwait(false);

            foreach (var orphan in group)
            {
                var detail = names.TryGetValue(orphan.ObjectId, out var name) ? name : null;
                entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId, detail: detail), OrphanAction.Manual));
            }
        }

        return entries;
    }

    // KTD5: flat, case-insensitive identifier set drawn only from local shapes already scanned for
    // supported or shape-known types (R7 — never an unscoped, whole-repo string search). Built once per
    // BuildManualEntriesAsync call (itself called exactly once per RunPreImportAsync run) and used only
    // to enrich LogUnsupportedOrphansAsync's verbose preview (R6) — membership here is informational
    // only and never promotes a type into the actionable report or manual count.
    static HashSet<string> BuildLocalIdentifierHarvest(PostDeployContext context)
    {
        var harvest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, schemaName) in context.NamedComponents)
            harvest.Add(schemaName);

        harvest.UnionWith(context.EntityLogicalNames);

        var customApiNames = ComponentClassifier.ScanCustomApiNames(context.PackageSrcRoot);
        harvest.UnionWith(customApiNames.ApiUniqueNames);
        harvest.UnionWith(customApiNames.RequestParameterNames);
        harvest.UnionWith(customApiNames.ResponsePropertyNames);

        harvest.UnionWith(ComponentClassifier.ScanBotSchemaNames(context.PackageSrcRoot));
        harvest.UnionWith(ComponentClassifier.ScanConnectionReferenceLogicalNames(context.PackageSrcRoot));

        return harvest;
    }

    // Verbose-only preview of orphan candidates Flowline can't yet act on (see SupportedManualTypes).
    // Resolves the type's own label (ManualTypeLabels, falling back to solutioncomponentdefinition for
    // env-specific codes like connectionreference/bot) and the individual record's name where a lookup
    // exists, plus what the pre-opt-in logic would have proposed — purely informational, so a type can
    // be evaluated with real data before deciding to add it to SupportedManualTypes. R6: when the
    // resolved name matches localIdentifiers (KTD5), the line also notes a possible local match — this
    // never changes control flow, the orphan still doesn't reach entries/the report/the manual count.
    async Task LogUnsupportedOrphansAsync(
        IOrganizationServiceAsync2 service,
        List<(Guid ObjectId, int ComponentType)> unsupportedOrphans,
        IReadOnlySet<string> localIdentifiers,
        CancellationToken ct)
    {
        var unlabeledTypes = unsupportedOrphans.Select(o => o.ComponentType).Where(t => !ManualTypeLabels.ContainsKey(t)).Distinct().ToList();
        var resolvedTypeLabels = unlabeledTypes.Count > 0
            ? await ResolveComponentTypeNamesAsync(service, unlabeledTypes, ct).ConfigureAwait(false)
            : [];

        foreach (var group in unsupportedOrphans.GroupBy(o => o.ComponentType))
        {
            var typeLabel = ManualTypeLabels.TryGetValue(group.Key, out var known) ? known
                : resolvedTypeLabels.TryGetValue(group.Key, out var resolved) ? resolved
                : null;

            var names = await ResolveGroupNamesAsync(service, group.Key, group.Select(o => o.ObjectId), ct).ConfigureAwait(false);
            if (names.Count == 0 && typeLabel != null && ResolvedTypeNameAttributes.TryGetValue(typeLabel, out var resolvedLookup))
                names = await GetEntityNamesAsync(service, typeLabel, resolvedLookup.IdAttribute, resolvedLookup.NameAttribute, group.Select(o => o.ObjectId), ct).ConfigureAwait(false);

            foreach (var orphan in group)
            {
                var typeText  = typeLabel != null ? $"{orphan.ComponentType} ({typeLabel})" : orphan.ComponentType.ToString();
                var hasName   = names.TryGetValue(orphan.ObjectId, out var name);
                var nameText  = hasName ? $" '{name}'" : "";
                var matchNote = name != null && localIdentifiers.Contains(name) ? " Possible match found locally." : "";
                console.Verbose($"Solution component type {typeText}{nameText} ({orphan.ObjectId}) — not tracked yet, no action taken. Out-of-the-box logic would have proposed: remove manually via maker portal.{matchNote}");
            }
        }
    }

    // Cross-entity attribute lookup, scoped to the solution's own entities (context.EntityLogicalNames)
    // rather than an unfiltered scan — an EntityQueryExpression with no Criteria is the RetrieveAllEntities-
    // equivalent full metadata walk, which doesn't scale. Attributes on entities outside this solution's
    // root list won't resolve — those fall back to a bare GUID rather than a guessed name.
    static async Task<Dictionary<Guid, (string EntityLogicalName, string AttributeLogicalName)>> ResolveAttributeInfoAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<string> entityLogicalNames,
        IEnumerable<Guid> attributeIds,
        CancellationToken ct)
    {
        // MetadataConditionExpression is strictly typed — MetadataId is Guid?, so the In-array must be
        // Guid[], not object[]. An object[] (even one boxing only Guids) throws OrganizationServiceFault
        // 0x80044183 "cannot be compared with a condition value of type System.Object" server-side.
        var idArray = attributeIds.Distinct().Where(id => id != Guid.Empty).ToArray();
        if (idArray.Length == 0) return [];

        var query = new EntityQueryExpression
        {
            Properties = new MetadataPropertiesExpression("LogicalName", "Attributes"),
            Criteria = new MetadataFilterExpression(LogicalOperator.Or)
            {
                Conditions = { new MetadataConditionExpression("LogicalName", MetadataConditionOperator.In, entityLogicalNames.ToArray()) }
            },
            AttributeQuery = new AttributeQueryExpression
            {
                Properties = new MetadataPropertiesExpression("LogicalName"),
                Criteria = new MetadataFilterExpression
                {
                    Conditions = { new MetadataConditionExpression("MetadataId", MetadataConditionOperator.In, idArray) }
                }
            }
        };

        var response = (RetrieveMetadataChangesResponse)await service.ExecuteAsync(new RetrieveMetadataChangesRequest { Query = query }, ct).ConfigureAwait(false);

        var result = new Dictionary<Guid, (string, string)>();
        foreach (var entity in response.EntityMetadata)
        foreach (var attribute in entity.Attributes)
            if (attribute.MetadataId.HasValue)
                result[attribute.MetadataId.Value] = (entity.LogicalName, attribute.LogicalName);

        return result;
    }

    // Scans Package/src/WebResources — the content this deploy is actually packing and importing —
    // never WebResources/dist. Deploy promotes whatever's committed in Package/src; reading a separate
    // local build artifact here would check content that may not match what's shipping (or, on a CI
    // agent that never runs the WebResources build, may not exist at all).
    async Task<List<(Guid ObjectId, int ComponentType)>> ExemptAnnotationReferencedWebResourcesAsync(
        IOrganizationServiceAsync2 service,
        List<(Guid ObjectId, int ComponentType)> autoOrphans,
        string packageSrcRoot,
        CancellationToken ct)
    {
        var webResourceOrphans = autoOrphans.Where(c => c.ComponentType == 61).ToList();
        if (webResourceOrphans.Count == 0) return autoOrphans;

        var annotationRefs = WebResourceAnnotationParser.CollectAllReferences(Path.Combine(packageSrcRoot, "WebResources"));
        if (annotationRefs.Count == 0) return autoOrphans;

        var orphanNames = await GetWebResourceNamesAsync(service, webResourceOrphans.Select(c => c.ObjectId), ct).ConfigureAwait(false);

        var exemptIds = webResourceOrphans
            .Where(o => orphanNames.TryGetValue(o.ObjectId, out var name) && annotationRefs.Contains(name))
            .Select(o => o.ObjectId)
            .ToHashSet();

        if (exemptIds.Count == 0) return autoOrphans;

        foreach (var (id, name) in orphanNames)
            if (exemptIds.Contains(id))
                console.Skip($"'{name}' preserved — referenced in // flowline:depends annotation.");

        return autoOrphans.Where(o => !exemptIds.Contains(o.ObjectId)).ToList();
    }

    static Task<Dictionary<Guid, string>> GetWebResourceNamesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> ids,
        CancellationToken ct) =>
        GetEntityNamesAsync(service, "webresource", "webresourceid", "name", ids, ct);

    // Shared by every "group orphans by componentType, resolve display names via NameResolvableTypes"
    // loop (AutoDelete entries, supported Manual entries, and the unsupported-type verbose preview) —
    // a type with no NameResolvableTypes entry resolves to an empty map rather than a query.
    static Task<Dictionary<Guid, string>> ResolveGroupNamesAsync(
        IOrganizationServiceAsync2 service,
        int componentType,
        IEnumerable<Guid> ids,
        CancellationToken ct) =>
        NameResolvableTypes.TryGetValue(componentType, out var lookup)
            ? GetEntityNamesAsync(service, lookup.EntityLogicalName, lookup.IdAttribute, lookup.NameAttribute, ids, ct)
            : Task.FromResult(new Dictionary<Guid, string>());

    static async Task<Dictionary<Guid, string>> GetEntityNamesAsync(
        IOrganizationServiceAsync2 service,
        string entityLogicalName,
        string idAttribute,
        string nameAttribute,
        IEnumerable<Guid> ids,
        CancellationToken ct)
    {
        var idList = ids.Distinct().Where(id => id != Guid.Empty).ToList();
        if (idList.Count == 0) return [];
        if (idList.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many {entityLogicalName} orphans for name resolution.");

        var query = new QueryExpression(entityLogicalName)
        {
            ColumnSet = new ColumnSet(nameAttribute),
            Criteria  = { Conditions = { new ConditionExpression(idAttribute, ConditionOperator.In, idList.Select(id => (object)id).ToArray()) } }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue<string>(nameAttribute)))
            .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>(nameAttribute)!);
    }

    async Task<List<(Guid ObjectId, int ComponentType)>> QuerySolutionComponentsAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        CancellationToken ct)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid", "componenttype")
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        var result = new List<(Guid, int)>(entities.Count);
        foreach (var entity in entities)
        {
            var objectId = entity.GetAttributeValue<Guid>("objectid");
            if (objectId == Guid.Empty) continue;
            var componentType = entity.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
            if (componentType == null) continue;
            result.Add((objectId, componentType.Value));
        }
        return result;
    }

    // Identifies orphan candidates that fall outside the componenttype-int gate (SupportedManualTypes)
    // by checking membership in a fixed set of env-specific-componenttype tables — CustomApi family
    // (customapi/customapirequestparameter/customapiresponseproperty), Bot, and ConnectionReference —
    // via a single bulk query per table (same "IN (ids)" pattern as GetEntityNamesAsync). See KTD2: a
    // hardcoded componenttype constant would only be valid for the org it was captured in.
    async Task<Dictionary<Guid, string>> IdentifyEntityDetectedTypesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> objectIds,
        CancellationToken ct)
    {
        var ids = objectIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (ids.Count == 0)
            return [];
        if (ids.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {ids.Count} IDs (max 2000). Solution has too many entity-detected components for orphan detection.");

        var idArray = ids.Select(id => (object)id).ToArray();

        static QueryExpression EntityQuery(string entityName, string idAttr, object[] ids) =>
            new(entityName)
            {
                ColumnSet = new ColumnSet(false),
                Criteria  = { Conditions = { new ConditionExpression(idAttr, ConditionOperator.In, ids) } }
            };

        // Each table is queried and caught independently — one table failing (e.g. "bot" unavailable in
        // an org without Copilot Studio provisioned) must not blank out detection for the other four.
        // Before this, a single shared try/catch around the whole Task.WhenAll meant one failing table
        // silently lost CustomApi's own detection too.
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
        return result;
    }

    // Shared by every entity-detected type that has no data-table componenttype to gate on (Bot,
    // ConnectionReference — see IdentifyEntityDetectedTypesAsync/KTD2): resolve each candidate's live
    // identity attribute, drop the ones still declared in local source, and build the resulting Manual
    // entries. CustomApi keeps its own version above since it groups three sub-entities, not one.
    static async Task<List<OrphanEntry>> ResolveEntityDetectedManualEntriesAsync(
        IOrganizationServiceAsync2 service,
        List<(Guid ObjectId, int ComponentType)> orphans,
        string entityLogicalName,
        string idAttribute,
        string identityAttribute,
        Func<string, HashSet<string>> scanLocal,
        string packageSrcRoot,
        CancellationToken ct)
    {
        var entries = new List<OrphanEntry>();
        if (orphans.Count == 0) return entries;

        var names = await GetEntityNamesAsync(service, entityLogicalName, idAttribute, identityAttribute, orphans.Select(o => o.ObjectId), ct).ConfigureAwait(false);
        var localIdentities = scanLocal(packageSrcRoot);

        foreach (var orphan in orphans)
        {
            // No resolved identity attribute means local-source verification never actually ran for
            // this candidate — unlike a resolved-but-unmatched name, this isn't evidence of removal.
            // Skip rather than default to "orphaned": the exact false-positive shape this trust bar
            // exists to prevent (KTD2) is reporting a component nobody actually verified as gone.
            if (!names.TryGetValue(orphan.ObjectId, out var name)) continue;
            if (localIdentities.Contains(name)) continue; // still declared locally — not orphaned

            entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId, entityLogicalName, name), OrphanAction.Manual, EntityName: entityLogicalName));
        }

        return entries;
    }

    // Dataverse dual-writes every component added to a custom unmanaged solution into "Default" as
    // well, so "Default" membership is not a reason to keep an otherwise-orphaned component around.
    // Excluding it here mirrors PluginPlanner.AddCrossSolutionWarnings.
    static List<string> OtherRelevantSolutions(Dictionary<Guid, List<string>> crossSolution, Guid objectId, string solutionName) =>
        crossSolution.TryGetValue(objectId, out var sols)
            ? sols.Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    async Task<Dictionary<Guid, List<string>>> GetCrossSolutionMembershipAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> objectIds,
        CancellationToken ct)
    {
        var ids = objectIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (ids.Count == 0)
            return [];
        if (ids.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {ids.Count} IDs (max 2000). Solution has too many orphan components for cross-solution membership check.");

        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria  = { Conditions = { new ConditionExpression("objectid", ConditionOperator.In, ids.Select(id => (object)id).ToArray()) } },
            LinkEntities =
            {
                new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner)
                {
                    Columns     = new ColumnSet("uniquename"),
                    EntityAlias = "sol"
                }
            }
        };

        var entities   = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        var membership = new Dictionary<Guid, List<string>>();

        foreach (var entity in entities)
        {
            var objectId = entity.GetAttributeValue<Guid>("objectid");
            if (objectId == Guid.Empty) continue;

            var sln = entity.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string;
            if (string.IsNullOrEmpty(sln)) continue;

            if (!membership.TryGetValue(objectId, out var sols))
                membership[objectId] = sols = [];
            sols.Add(sln);
        }

        return membership;
    }

    async Task<HashSet<Guid>> GetStillPresentAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<Guid> objectIds,
        CancellationToken ct)
    {
        if (objectIds.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {objectIds.Count} IDs (max 2000). Solution has too many deferred orphan components.");

        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria  = { Conditions = { new ConditionExpression("objectid", ConditionOperator.In, objectIds.Select(id => (object)id).ToArray()) } }
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities.Select(e => e.GetAttributeValue<Guid>("objectid")).Where(id => id != Guid.Empty).ToHashSet();
    }

    async Task<IReadOnlyList<OrphanEntry>> ExecuteInOrderAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<OrphanEntry> entries,
        bool isPostImport,
        CancellationToken ct)
    {
        var deferred = new List<OrphanEntry>();

        // Stable componenttype entries in R8 order.
        var stableByType = entries.Where(e => e.Action != OrphanAction.Manual && e.EntityName == null)
                                  .GroupBy(e => e.ComponentType)
                                  .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var componentType in ExecutionOrder)
        {
            if (!stableByType.TryGetValue(componentType, out var group)) continue;

            foreach (var entry in group)
                await TryExecuteEntryAsync(service, solutionName, entry, isPostImport, deferred, ct);
        }

        // Entity-detected CustomApi family: children before parent.
        var customApiByEntity = entries.Where(e => e.Action != OrphanAction.Manual && e.EntityName != null)
                                       .GroupBy(e => e.EntityName!)
                                       .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entityName in CustomApiEntityOrder)
        {
            if (!customApiByEntity.TryGetValue(entityName, out var group)) continue;

            foreach (var entry in group)
                await TryExecuteEntryAsync(service, solutionName, entry, isPostImport, deferred, ct);
        }

        return deferred.AsReadOnly();
    }

    async Task TryExecuteEntryAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        OrphanEntry entry,
        bool isPostImport,
        List<OrphanEntry> deferred,
        CancellationToken ct)
    {
        try
        {
            if (entry.ComponentType == 29 && entry.Action == OrphanAction.Delete)
            {
                var deactivated = await TryDeactivateWorkflowAsync(service, entry.ObjectId, ct).ConfigureAwait(false);
                if (!deactivated)
                {
                    console.Warning($"'{entry.DisplayName}' — workflow deactivation failed, remove manually via maker portal.");
                    return;
                }
            }

            await PerformActionAsync(service, solutionName, entry, ct).ConfigureAwait(false);
            console.Verbose($"{(isPostImport ? "Post-import: " : "")}{entry.DisplayName} {(entry.Action == OrphanAction.Delete ? "deleted" : "removed from solution")}");
        }
        catch (FaultException<OrganizationServiceFault> ex) when (!isPostImport && IsDependencyError(ex))
        {
            console.MarkupLine($"[dim]Deferred: {Markup.Escape(entry.DisplayName)} — dependency, will retry post-import[/]");
            deferred.Add(entry);
        }
        catch (FaultException<OrganizationServiceFault> ex) when (isPostImport)
        {
            console.Warning($"'{entry.DisplayName}' — post-import cleanup failed, remove manually: {Markup.Escape(ex.Message)}");
            deferred.Add(entry);
        }
    }

    static async Task PerformActionAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        OrphanEntry entry,
        CancellationToken ct)
    {
        if (entry.Action == OrphanAction.RemoveFromSolution)
        {
            await service.ExecuteAsync(new OrganizationRequest("RemoveSolutionComponent")
            {
                ["ComponentId"]        = entry.ObjectId,
                ["ComponentType"]      = entry.ComponentType,
                ["SolutionUniqueName"] = solutionName
            }, ct).ConfigureAwait(false);
            return;
        }

        var entityName = entry.EntityName ?? (EntityNames.TryGetValue(entry.ComponentType, out var n) ? n : null);
        if (entityName == null) return;
        await service.DeleteAsync(entityName, entry.ObjectId, ct).ConfigureAwait(false);
    }

    static async Task<bool> TryDeactivateWorkflowAsync(IOrganizationServiceAsync2 service, Guid workflowId, CancellationToken ct)
    {
        try
        {
            await service.UpdateAsync(new Entity("workflow", workflowId)
            {
                ["statecode"]  = new OptionSetValue(0),
                ["statuscode"] = new OptionSetValue(1)
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            return false;
        }
    }

    void PrintReport(IReadOnlyList<OrphanEntry> entries, RunMode mode, string solutionName, string environmentUrl)
    {
        var automated = entries.Where(e => e.Action != OrphanAction.Manual)
            .OrderBy(e => ExecutionOrderIndex(e.ComponentType, e.EntityName))
            .ToList();
        var manual = entries.Where(e => e.Action == OrphanAction.Manual)
            .OrderBy(e => ExecutionOrderIndex(e.ComponentType, e.EntityName))
            .ToList();

        console.MarkupLine($"[bold]Orphan components ({entries.Count}):[/]");

        foreach (var entry in automated)
        {
            var label = mode == RunMode.NoDelete ? NoDeleteLabel(entry.Action) : ActionLabel(entry.Action);
            console.MarkupLine($"  [{ActionColor(entry.Action)}]{Markup.Escape(entry.DisplayName)} — {label}[/]");
        }

        if (manual.Count > 0)
        {
            console.Warning($"{manual.Count} component{(manual.Count == 1 ? "" : "s")} can't be removed automatically:");
            foreach (var entry in manual)
                console.MarkupLine($"  [yellow]{Markup.Escape(entry.DisplayName)}[/] — remove manually via maker portal");
            console.MarkupLine($"  Open {SolutionsListUrl(environmentUrl)}, find '{solutionName}', and remove these from there.");
        }

        var deleteCount = entries.Count(e => e.Action == OrphanAction.Delete);
        var removeCount = entries.Count(e => e.Action == OrphanAction.RemoveFromSolution);

        if (mode == RunMode.NoDelete)
            console.Skip($"{deleteCount} would be deleted, {removeCount} would be removed from solution, {manual.Count} manual. (--no-delete active)");
        else
            console.Skip($"{deleteCount} to delete, {removeCount} to remove from solution, {manual.Count} manual");
    }

    static string SolutionsListUrl(string environmentUrl) =>
        $"{environmentUrl.TrimEnd('/')}/tools/Solution/home_solution.aspx?etn=solution";

    static int ExecutionOrderIndex(int componentType, string? entityName)
    {
        if (entityName != null)
        {
            var customApiIdx = Array.IndexOf(CustomApiEntityOrder, entityName);
            return ExecutionOrder.Length + (customApiIdx < 0 ? CustomApiEntityOrder.Length : customApiIdx);
        }
        var idx = Array.IndexOf(ExecutionOrder, componentType);
        return idx < 0 ? ExecutionOrder.Length - 1 : idx;
    }

    static string TypeName(int componentType, Guid objectId, string? entityName = null, string? detail = null)
    {
        var name = entityName switch
        {
            "customapi"                 => "CustomApi",
            "customapirequestparameter" => "CustomApiRequestParameter",
            "customapiresponseproperty" => "CustomApiResponseProperty",
            "bot"                       => "Bot",
            "connectionreference"       => "ConnectionReference",
            _ => componentType switch
            {
                91 => "PluginAssembly",
                90 => "PluginType",
                92 => "SdkMessageProcessingStep",
                93 => "SdkMessageProcessingStepImage",
                61 => "WebResource",
                29 => "Workflow",
                _ when ManualTypeLabels.TryGetValue(componentType, out var label) => label,
                _  => $"Unrecognized component (type {componentType})"
            }
        };
        return detail != null ? $"{name} '{detail}' ({objectId})" : $"{name} {objectId}";
    }

    static string ActionLabel(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "delete",
        OrphanAction.RemoveFromSolution => "remove from solution",
        _                               => action.ToString()
    };

    static string NoDeleteLabel(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "would delete",
        OrphanAction.RemoveFromSolution => "would remove from solution",
        _                               => action.ToString()
    };

    static string ActionColor(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "red",
        OrphanAction.RemoveFromSolution => "yellow",
        _                               => "white"
    };

    static bool IsDependencyError(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
        (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
}
