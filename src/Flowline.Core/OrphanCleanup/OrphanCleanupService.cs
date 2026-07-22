using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.OrphanCleanup.Handlers;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.OrphanCleanup;

public enum OrphanAction { Delete, RemoveFromSolution, Manual }

// EntityName, Priority, SequenceHint, and Timing default so pre-existing 4-arg call sites keep
// compiling. They carry a handler's classification and ordering decision (from HandlerFinding) into the
// entry the orchestrator executes and prints.
public sealed record OrphanEntry(
    Guid ObjectId,
    int ComponentType,
    string DisplayName,
    OrphanAction Action,
    string? EntityName = null,
    OrphanPriority Priority = OrphanPriority.None,
    int SequenceHint = 0,
    OrphanTiming Timing = OrphanTiming.PreImportEligible);

// Skipped distinguishes "ran and found nothing" (false) from "an empty-input guard short-circuited
// before comparing" (true) — a read-only caller like DriftCommand must not conflate the two.
public sealed record CompareResult(IReadOnlyList<OrphanEntry> Entries, bool Skipped = false);

public class OrphanCleanupService(IAnsiConsole console, IEnumerable<IOrphanHandler> handlers) : IPostDeployService
{
    // Explicit, centrally-declared cross-family order, independent of Program.cs's DI-registration order
    // — adding a handler means appending it here.
    static readonly Type[] FamilyOrder =
    [
        typeof(PluginAssemblyFamilyHandler),
        typeof(WebResourceHandler),
        typeof(WorkflowHandler),
        typeof(CustomApiFamilyHandler),
        typeof(BotHandler),
        typeof(ConnectionReferenceHandler),
        typeof(RoleHandler),
        typeof(EntityFamilyHandler),
    ];

    readonly IReadOnlyList<IOrphanHandler> _orderedHandlers = handlers
        .OrderBy(h => FamilyIndex(h))
        .ToList();

    static int FamilyIndex(IOrphanHandler handler)
    {
        var idx = Array.IndexOf(FamilyOrder, handler.GetType());
        return idx >= 0 ? idx : FamilyOrder.Length; // unlisted handler (future addition not yet in FamilyOrder) sorts last
    }

    // Handlers that can only identify a candidate by querying their own backing table.
    // DispatchToHandlersAsync gives all three the identical still-unclaimed batch — not progressively
    // narrowed relative to each other — so one handler's failure can't suppress another's attempt.
    static readonly HashSet<Type> EntityDetectedHandlerTypes =
    [
        typeof(CustomApiFamilyHandler),
        typeof(BotHandler),
        typeof(ConnectionReferenceHandler),
    ];

    // Threads dependency-deferred entries from RunPreImportAsync to RunPostImportAsync on the same instance.
    IReadOnlyList<OrphanEntry> _deferred = [];

    // Threads declared-PostImportOnly entries to RunPostImportAsync — entries never attempted pre-import,
    // unlike _deferred (attempted, then deferred on a fault). Merged with _deferred before the single
    // ExecuteInOrderAsync call.
    IReadOnlyList<OrphanEntry> _postImportOnly = [];

    // Fallback table-name lookup for componenttype-gated Auto handlers, whose findings leave EntityName
    // null. Entity-detected handlers (CustomApi family, Bot, ConnectionReference) set EntityName
    // explicitly since their componenttype is env-specific.
    static readonly Dictionary<int, string> EntityNames = new()
    {
        [91] = "pluginassembly",
        [90] = "plugintype",
        [92] = "sdkmessageprocessingstep",
        [93] = "sdkmessageprocessingstepimage",
        [61] = "webresource",
        [29] = "workflow",
    };

    // Manual-orphan display labels for solutioncomponent.componenttype (learn.microsoft.com/power-apps/
    // developer/data-platform/reference/entities/solutioncomponent). Not exhaustive — unmapped types
    // (e.g. env-specific 10000+ codes) are reported as unrecognized rather than guessed at. Used by
    // LogUnsupportedOrphansAsync for any candidate no handler claims.
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
        _postImportOnly = [];

        var result = await CompareAsync(context, ct, BuildNoDeleteHint(context.Solution, context.Mode)).ConfigureAwait(false);

        if (context.Mode.IsReportOnly())
            return;

        // PostImportOnly entries skip the pre-import execution pass entirely and are threaded to
        // RunPostImportAsync via _postImportOnly instead.
        var preImportEntries = result.Entries.Where(e => e.Timing == OrphanTiming.PreImportEligible).ToList();
        _postImportOnly = result.Entries.Where(e => e.Timing == OrphanTiming.PostImportOnly).ToList();

        _deferred = await ExecuteInOrderAsync(context.Service, context.Solution.Name, preImportEntries, isPostImport: false, ct).ConfigureAwait(false);
    }

    // Derives the report reason from DeploySolutionInfo rather than a caller-supplied string —
    // presentation belongs to the service that owns the report. U5/KTD4: mode defaults to NoDelete so
    // every pre-existing call site (and test) keeps its current behavior unmodified; DryRun short-circuits
    // before the managed/unmanaged/exists-in-target branching, since --dry-run's "preview" framing
    // dominates regardless of the solution's managed status.
    internal static string BuildNoDeleteHint(DeploySolutionInfo solution, RunMode mode = RunMode.NoDelete) =>
        mode == RunMode.DryRun ? "(--dry-run preview)"
        : !solution.IncludeManaged ? "(--no-delete active)"
        : solution.ExistsInTarget ? "(managed — previewing what the upgrade import will remove)"
        : "(managed — first install, cleanup runs on a later upgrade deploy)";

    // Thin wrapper for DeployCommand, which already has a PostDeployContext for the IPostDeployService
    // fan-out. Delegates to the primitives overload below so the engine isn't coupled to deploy-only
    // fields like PackagePath.
    public Task<CompareResult> CompareAsync(PostDeployContext context, CancellationToken ct, string? noDeleteHint = "(--no-delete active)") =>
        CompareAsync(context.DataverseSolutionSrcRoot, context.Service, context.Solution.Name, context.Solution.EnvironmentUrl, context.Mode, ct, noDeleteHint);

    // Convenience overload for read-only callers with no context of their own (e.g. DriftCommand) —
    // takes dataverseSolutionFolder (parent of src) and always runs RunMode.NoDelete.
    public Task<CompareResult> CompareAsync(
        string dataverseSolutionFolder,
        IOrganizationServiceAsync2 service,
        string solutionName,
        string environmentUrl,
        CancellationToken ct,
        string? noDeleteHint = null) =>
        CompareAsync(Path.Combine(dataverseSolutionFolder, "src"), service, solutionName, environmentUrl, RunMode.NoDelete, ct, noDeleteHint);

    // Comparison-only half of the pre-import step: parses committed source, resolves sNewIds
    // (schemaName/entity/OptionSet special-casing), dispatches candidates to the handler set, and prints
    // the report — stopping before ExecuteInOrderAsync so it's safely callable read-only (used by
    // DriftCommand). `noDeleteHint` lets a caller without its own `--no-delete` flag replace the
    // deploy-specific report phrasing.
    //
    // Takes primitives rather than a PostDeployContext — this is the real comparison engine both
    // overloads above delegate to, and shouldn't be coupled to deploy-only fields like PackagePath.
    //
    // Returns CompareResult rather than a bare list so a caller can tell "compared, found nothing"
    // (Skipped: false) apart from "didn't run at all" (Skipped: true, the two empty-input guards below)
    // — DriftCommand needs that distinction to avoid reporting a false "no drift".
    public async Task<CompareResult> CompareAsync(
        string dataverseSolutionSrcRoot,
        IOrganizationServiceAsync2 service,
        string solutionName,
        string environmentUrl,
        RunMode mode,
        CancellationToken ct,
        string? noDeleteHint = "(--no-delete active)")
    {
        var (sNew, entityLogicalNames, namedComponents) = ComponentClassifier.ParseLocalSource(dataverseSolutionSrcRoot);

        var sOld = await console.Status().FlowlineSpinner()
            .StartAsync($"Querying orphan components in [bold]{solutionName}[/]...",
                _ => QuerySolutionComponentsAsync(service, solutionName, ct))
            .ConfigureAwait(false);

        if (sOld.Count == 0)
        {
            console.Skip("No solution components in Dataverse — skipping orphan check.");
            return new CompareResult([], Skipped: true);
        }

        var sNewIds = sNew.Select(c => c.ObjectId).ToHashSet();

        if (sNew.Count == 0)
        {
            console.Warning("No components in Solution.xml — orphan check skipped to prevent mass deletion.");
            return new CompareResult([], Skipped: true);
        }

        // Entity roots in Solution.xml are recorded by schemaName, not MetadataId — resolve them live
        // so entity components aren't misdiagnosed as orphans. See ComponentClassifier.ParseSolutionXmlComponents.
        if (entityLogicalNames.Count > 0)
        {
            var resolvedEntityIds = await ResolveEntityMetadataIdsAsync(service, entityLogicalNames, ct).ConfigureAwait(false);
            sNewIds.UnionWith(resolvedEntityIds);
        }

        // Other types recorded by schemaName instead of id (e.g. WebResource — its id is not portable
        // across environments, so pac always records it by name) — resolve live for the same reason.
        if (namedComponents.Count > 0)
        {
            var resolvedNamedIds = await ResolveNamedComponentIdsAsync(service, namedComponents, ct).ConfigureAwait(false);
            sNewIds.UnionWith(resolvedNamedIds);
        }

        // OptionSet roots are also schemaName-declared, but OptionSet is metadata, not a data-table row,
        // so ResolveNamedComponentIdsAsync can't resolve it. Resolve via RetrieveOptionSetRequest
        // instead and fold into sNewIds before the orphan diff runs.
        var optionSetSchemaNames = namedComponents
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
            return new CompareResult([]);
        }

        var detectionContext = new DetectionContext(dataverseSolutionSrcRoot, service, solutionName, environmentUrl, mode, entityLogicalNames);
        var entries = await DispatchToHandlersAsync(detectionContext, namedComponents, orphans, ct).ConfigureAwait(false);

        PrintReport(entries, mode, solutionName, environmentUrl, noDeleteHint);

        return new CompareResult(entries);
    }

    // Dispatches to each handler once, in FamilyOrder, against candidates still unclaimed by an earlier
    // handler — covering both dispatch shapes uniformly: componenttype-gated handlers just ignore
    // non-matching candidates regardless of batch size, while entity-detected handlers (CustomApi
    // family, Bot, ConnectionReference) get the same still-unclaimed batch as their one query.
    //
    // A candidate absent from Findings is either recognized-but-clean (in ClaimedIds) and silently
    // dropped, or unclaimed by every handler and routed to the generic-fallback preview — computed from
    // ClaimedIds, not Findings, so a recognized-but-clean candidate never leaks into the fallback.
    async Task<List<OrphanEntry>> DispatchToHandlersAsync(
        DetectionContext detectionContext,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents,
        List<(Guid ObjectId, int ComponentType)> orphans,
        CancellationToken ct)
    {
        var service            = detectionContext.Service;
        var dataverseSolutionSrcRoot = detectionContext.DataverseSolutionSrcRoot;
        var solutionName       = detectionContext.SolutionName;
        var entityLogicalNames = detectionContext.EntityLogicalNames;

        var claimedIds      = new HashSet<Guid>();
        var findings         = new List<HandlerFinding>();
        var familyIndexById  = new Dictionary<Guid, int>();

        // Split into detect and merge so Pass 2 can fan the three entity-detected handlers out
        // concurrently via Task.WhenAll, then merge into claimedIds/findings/familyIndexById
        // single-threaded — those collections aren't thread-safe.
        async Task<HandlerDetectionResult> DetectHandlerAsync(int index, IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates) =>
            await _orderedHandlers[index].DetectAsync(detectionContext, candidates, ct).ConfigureAwait(false);

        void MergeResult(int index, HandlerDetectionResult result)
        {
            var handler = _orderedHandlers[index];
            claimedIds.UnionWith(result.ClaimedIds);

            if (handler.Status == HandlerStatus.Active)
            {
                foreach (var finding in result.Findings)
                {
                    findings.Add(finding);
                    familyIndexById[finding.ObjectId] = index;
                }
            }
            else
            {
                // No handler ships Preview today — this branch exists so a future Preview handler is
                // field-tested with zero action risk, without a code change here.
                foreach (var finding in result.Findings)
                    console.Verbose($"[Preview: {handler.GetType().Name}] {finding.DisplayName}");
            }
        }

        async Task RunHandlerAsync(int index, IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates)
        {
            var result = await DetectHandlerAsync(index, candidates).ConfigureAwait(false);
            MergeResult(index, result);
        }

        // Pass 1: componenttype-gated handlers match by componenttype alone, so their gates never
        // overlap — shrinking the batch as handlers claim from it is just an optimization, not required
        // for correctness.
        for (var i = 0; i < _orderedHandlers.Count; i++)
        {
            if (EntityDetectedHandlerTypes.Contains(_orderedHandlers[i].GetType())) continue;
            var remaining = orphans.Where(c => !claimedIds.Contains(c.ObjectId)).ToList();
            await RunHandlerAsync(i, remaining).ConfigureAwait(false);
        }

        // Pass 2: entity-detected handlers each independently query their own table against the SAME
        // still-unclaimed batch — not narrowed relative to each other, so one handler's failure or claim
        // can never suppress another's independent attempt.
        //
        // Their queries have no data dependency on each other, so they dispatch concurrently via
        // Task.WhenAll and merge single-threaded afterward in declared order — Task.WhenAll preserves
        // input order, so zipping indices with results keeps that order for familyIndexById/findings.
        var remainderForEntityDetected = orphans.Where(c => !claimedIds.Contains(c.ObjectId)).ToList();
        var entityDetectedIndices = Enumerable.Range(0, _orderedHandlers.Count)
            .Where(i => EntityDetectedHandlerTypes.Contains(_orderedHandlers[i].GetType()))
            .ToList();

        var entityDetectedResults = await Task.WhenAll(
            entityDetectedIndices.Select(i => DetectHandlerAsync(i, remainderForEntityDetected))).ConfigureAwait(false);

        foreach (var (index, result) in entityDetectedIndices.Zip(entityDetectedResults))
            MergeResult(index, result);

        var unclaimed = orphans.Where(c => !claimedIds.Contains(c.ObjectId)).ToList();
        if (unclaimed.Count > 0)
        {
            var localIdentifiers = BuildLocalIdentifierHarvest(dataverseSolutionSrcRoot, entityLogicalNames, namedComponents);
            await LogUnsupportedOrphansAsync(service, unclaimed, localIdentifiers, ct).ConfigureAwait(false);
        }

        // Delete-vs-RemoveFromSolution override spans handlers (a handler only ever proposes Delete;
        // cross-solution membership is an orchestrator concern), applied here on top of every Auto
        // handler's findings. Manual findings never reach it.
        var deleteCandidateIds = findings.Where(f => f.Action == OrphanAction.Delete).Select(f => f.ObjectId).ToList();
        var crossSolution = deleteCandidateIds.Count > 0
            ? await GetCrossSolutionMembershipAsync(service, deleteCandidateIds, ct).ConfigureAwait(false)
            : [];

        // Sorted once here — cross-family via FamilyOrder/familyIndexById, then per-family via
        // SequenceHint — so downstream consumers just use this order.
        return findings
            .OrderBy(f => familyIndexById[f.ObjectId])
            .ThenBy(f => f.SequenceHint)
            .Select(f =>
            {
                var action = f.Action;
                if (action == OrphanAction.Delete)
                {
                    var otherSolutions = OtherRelevantSolutions(crossSolution, f.ObjectId, solutionName);
                    if (otherSolutions.Count > 0)
                        action = OrphanAction.RemoveFromSolution;
                }

                return new OrphanEntry(f.ObjectId, f.ComponentType, f.DisplayName, action, f.EntityName, f.Priority, f.SequenceHint, f.Timing);
            })
            .ToList();
    }

    public async Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var service      = context.Service;
        var solutionName = context.Solution.Name;
        var mode         = context.Mode;

        // Merges _deferred (attempted pre-import, faulted on a dependency) with _postImportOnly (never
        // attempted) into one list — both need the same still-present/cross-solution re-validation below
        // since live state may have moved on, and both converge on the single ExecuteInOrderAsync call.
        // Concatenated, not re-sorted: each set already preserves its own DispatchToHandlersAsync-derived
        // order.
        var candidates = _deferred.Concat(_postImportOnly).ToList();

        // Re-parses committed source (cheap) rather than threading the CompareAsync-time parse across
        // the pre/post-import boundary — same tradeoff as querying live state twice.
        var (sNew, _, _) = ComponentClassifier.ParseLocalSource(context.DataverseSolutionSrcRoot);

        if (candidates.Count == 0 || mode.IsReportOnly())
            return 0;

        var sNewIds      = sNew.Select(c => c.ObjectId).ToHashSet();
        var candidateIds = candidates.Select(e => e.ObjectId).ToList();

        var stillPresent  = await GetStillPresentAsync(service, solutionName, candidateIds, ct).ConfigureAwait(false);
        var presentIds    = stillPresent.ToList();
        var crossSolution = presentIds.Count > 0
            ? await GetCrossSolutionMembershipAsync(service, presentIds, ct).ConfigureAwait(false)
            : [];

        var reEntries = new List<OrphanEntry>();
        foreach (var entry in candidates)
        {
            if (!stillPresent.Contains(entry.ObjectId)) continue;
            if (sNewIds.Contains(entry.ObjectId)) continue;

            var otherSolutions = OtherRelevantSolutions(crossSolution, entry.ObjectId, solutionName);

            var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
            reEntries.Add(entry with { Action = action });
        }

        if (reEntries.Count == 0)
            return 0;

        console.Skip("Post-import: running orphan cleanup...");
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

    // OptionSet's own metadata-resolution path — separate from ResolveNamedComponentIdsAsync since
    // OptionSet has no backing table. Unlike ResolveEntityMetadataIdsAsync, failures are caught per-name
    // (RetrieveOptionSetRequest throws for a genuinely-deleted global choice) so one bad name doesn't
    // block the rest.
    async Task<HashSet<Guid>> ResolveOptionSetMetadataIdsAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<string> schemaNames,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentMetadataRequests, MaxConcurrentMetadataRequests);

        // A genuinely-deleted global choice faults at the org-service level — treated as expected.
        // Anything else is a real failure the operator should see.
        var tasks = schemaNames.Distinct().Select(async name =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await DataverseFaultTolerance.TryQueryAsync(async () =>
                {
                    var request = new RetrieveOptionSetRequest { Name = name };
                    var response = (RetrieveOptionSetResponse)await service.ExecuteAsync(request, ct).ConfigureAwait(false);
                    return response.OptionSetMetadata?.MetadataId;
                }, null, console, msg => $"OptionSet metadata lookup for '{name}' failed ({msg}) — treating as unresolved this run.");
            }
            finally
            {
                semaphore.Release();
            }
        });

        var metadataIds = await Task.WhenAll(tasks).ConfigureAwait(false);
        return metadataIds.Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
    }

    // Resolves non-entity schemaName-recorded RootComponents (e.g. WebResource) to their live id via each
    // type's NameResolvableTypes-mapped table. A type absent from NameResolvableTypes is skipped, not
    // guessed at. Pre-diff step — stays in the orchestrator regardless of which handler would eventually
    // claim the component.
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

    // componenttype → backing table. Still needed by two concerns outside the handler dispatch:
    // ResolveNamedComponentIdsAsync's schemaName pre-diff resolution, and ResolveGroupNamesAsync's
    // fallback name resolution for a candidate no handler claims (e.g. Form/View/ConnectionRole, which
    // have no handler). Six entries also have their own copy in their owning handler — not redundant,
    // this table serves the two concerns above only.
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

    // Resolves componenttype → display name via solutioncomponentdefinition. Verbose-fallback preview
    // only — it identifies a type, not verifies one, so it must never feed the actionable report.
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
    // LogicalName (confirmed: connectionreference/bot resolve to those exact strings), so the resolved
    // label doubles as the entity to query for the record's own name. Verbose-preview only, same caveat
    // as ResolveComponentTypeNamesAsync above.
    static readonly Dictionary<string, (string IdAttribute, string NameAttribute)> ResolvedTypeNameAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connectionreference"] = ("connectionreferenceid", "connectionreferencelogicalname"),
        ["bot"]                 = ("botid", "name"),
    };

    // Case-insensitive identifier set from local shapes already scanned for known types — never an
    // unscoped repo search. Used only to enrich LogUnsupportedOrphansAsync's verbose preview; membership
    // here never promotes a type into the actionable report.
    static HashSet<string> BuildLocalIdentifierHarvest(
        string dataverseSolutionSrcRoot,
        IReadOnlyList<string> entityLogicalNames,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents)
    {
        var harvest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, schemaName) in namedComponents)
            harvest.Add(schemaName);

        harvest.UnionWith(entityLogicalNames);

        var customApiNames = ComponentClassifier.ScanCustomApiNames(dataverseSolutionSrcRoot);
        harvest.UnionWith(customApiNames.ApiUniqueNames);
        harvest.UnionWith(customApiNames.RequestParameterNames);
        harvest.UnionWith(customApiNames.ResponsePropertyNames);

        harvest.UnionWith(ComponentClassifier.ScanBotSchemaNames(dataverseSolutionSrcRoot));
        harvest.UnionWith(ComponentClassifier.ScanConnectionReferenceLogicalNames(dataverseSolutionSrcRoot));

        return harvest;
    }

    // Verbose-only preview of orphan candidates no handler claimed. Resolves the type's label and the
    // record's name where possible, purely informational — a local-identifier match note never changes
    // control flow.
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
                names = await EntityNameLookup.GetEntityNamesAsync(service, typeLabel, resolvedLookup.IdAttribute, resolvedLookup.NameAttribute, group.Select(o => o.ObjectId), ct).ConfigureAwait(false);

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

    // Shared by every componentType-group name-resolution loop remaining after handler dispatch — a type
    // with no NameResolvableTypes entry resolves to an empty map.
    static Task<Dictionary<Guid, string>> ResolveGroupNamesAsync(
        IOrganizationServiceAsync2 service,
        int componentType,
        IEnumerable<Guid> ids,
        CancellationToken ct) =>
        NameResolvableTypes.TryGetValue(componentType, out var lookup)
            ? EntityNameLookup.GetEntityNamesAsync(service, lookup.EntityLogicalName, lookup.IdAttribute, lookup.NameAttribute, ids, ct)
            : Task.FromResult(new Dictionary<Guid, string>());

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

    // Dataverse dual-writes every component into "Default" too, so Default membership doesn't count as a
    // reason to keep an orphan. Mirrors PluginPlanner.AddCrossSolutionWarnings.
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

    // Executes in the order entries already carry (assigned by DispatchToHandlersAsync).
    // RunPostImportAsync's reEntries preserve that same order, so no re-sort is needed here. The
    // reactive dependency-deferral only changes attempt order, never fault-handling behavior.
    async Task<IReadOnlyList<OrphanEntry>> ExecuteInOrderAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<OrphanEntry> entries,
        bool isPostImport,
        CancellationToken ct)
    {
        var deferred = new List<OrphanEntry>();

        foreach (var entry in entries.Where(e => e.Action != OrphanAction.Manual))
            await TryExecuteEntryAsync(service, solutionName, entry, isPostImport, deferred, ct);

        return deferred.AsReadOnly();
    }

    // Dependency-fault deferral and the Workflow deactivate-before-delete step are both orthogonal to
    // handler dispatch — WorkflowHandler only classifies (statecode -> Prio); this method owns
    // deactivation.
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

    // Automated entries are additionally grouped by Prio — Prio1 first, since these block deployment —
    // on top of Action grouping. Every automated entry is guaranteed a real Prio1/2/3 by construction, so
    // the trailing None slot only guards against that invariant breaking.
    static readonly OrphanPriority[] PriorityOrder =
        [OrphanPriority.Prio1, OrphanPriority.Prio2, OrphanPriority.Prio3, OrphanPriority.None];

    void PrintReport(IReadOnlyList<OrphanEntry> entries, RunMode mode, string solutionName, string environmentUrl, string? noDeleteHint = "(--no-delete active)")
    {
        var automated = entries.Where(e => e.Action != OrphanAction.Manual).ToList();
        var manual = entries.Where(e => e.Action == OrphanAction.Manual).ToList();

        console.MarkupLine($"[bold]Orphan components ({entries.Count}):[/]");

        foreach (var priority in PriorityOrder)
        {
            var group = automated.Where(e => e.Priority == priority).ToList();
            if (group.Count == 0) continue;

            console.MarkupLine($"  [bold {PriorityColor(priority)}]{PriorityLabel(priority)}:[/]");
            foreach (var entry in group)
            {
                var label = mode.IsReportOnly() ? NoDeleteLabel(entry.Action) : ActionLabel(entry.Action);
                console.MarkupLine($"    [{ActionColor(entry.Action)}]{Markup.Escape(entry.DisplayName)} — {label}[/]");
            }
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

        if (mode.IsReportOnly())
        {
            var hint = string.IsNullOrEmpty(noDeleteHint) ? "" : $" {noDeleteHint}";
            console.Skip($"{deleteCount} would be deleted, {removeCount} would be removed from solution, {manual.Count} manual.{hint}");
        }
        else
            console.Skip($"{deleteCount} to delete, {removeCount} to remove from solution, {manual.Count} manual");
    }

    static string SolutionsListUrl(string environmentUrl) =>
        $"{environmentUrl.TrimEnd('/')}/tools/Solution/home_solution.aspx?etn=solution";

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

    // No default arm — an enum addition to OrphanPriority without a matching case here is a compile
    // error (CS8509), not a silently-dropped report group.
    static string PriorityLabel(OrphanPriority priority) => priority switch
    {
        OrphanPriority.Prio1 => "Prio1 — blocks deployment",
        OrphanPriority.Prio2 => "Prio2 — still running deleted logic",
        OrphanPriority.Prio3 => "Prio3 — safe to clean up",
        OrphanPriority.None  => "Unclassified",
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
    };

    static string PriorityColor(OrphanPriority priority) => priority switch
    {
        OrphanPriority.Prio1 => "red",
        OrphanPriority.Prio2 => "yellow",
        OrphanPriority.Prio3 => "dim",
        OrphanPriority.None  => "dim",
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
    };

    static bool IsDependencyError(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
        (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
}
