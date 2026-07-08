using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;

namespace Flowline.Core.Services;

public enum OrphanAction { Delete, RemoveFromSolution, Manual }

// EntityName, Priority, SequenceHint, and Timing all default so every pre-existing 4-arg call site
// (OrphanEntry(ObjectId, ComponentType, DisplayName, Action)) keeps compiling unchanged. Priority/
// SequenceHint/Timing are the handler-architecture bridge (see HandlerFinding, U9): a handler's
// per-instance Prio (R3), its family-scoped ordering hint (R11, KTD1), and its declared pre/post-import
// timing (R12) all survive from HandlerFinding into the entry the orchestrator executes and prints.
public sealed record OrphanEntry(
    Guid ObjectId,
    int ComponentType,
    string DisplayName,
    OrphanAction Action,
    string? EntityName = null,
    OrphanPriority Priority = OrphanPriority.None,
    int SequenceHint = 0,
    OrphanTiming Timing = OrphanTiming.PreImportEligible);

// Skipped distinguishes "the comparison ran and found nothing" (false) from "an empty-input guard
// short-circuited before any comparison happened" (true) — callers that need a trustworthy read-only
// signal (e.g. DriftCommand) must not treat those two cases as equivalent.
public sealed record CompareResult(IReadOnlyList<OrphanEntry> Entries, bool Skipped = false);

public class OrphanCleanupService(IAnsiConsole console, FlowlineRuntimeOptions opt, IEnumerable<IOrphanHandler> handlers) : IPostDeployService
{
    // KTD1: explicit, centrally-declared cross-family order — NOT DI-registration order (a future
    // unrelated edit to Program.cs's AddSingleton<IOrphanHandler, _> call sequence must not silently
    // reorder execution/report sequencing). Mirrors the role the old flat ExecutionOrder/
    // CustomApiEntityOrder arrays played as sole source of truth for cross-family sequencing. Adding a
    // future handler means appending to this one visible list, matching KTD2's roster order exactly.
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

    // The three handlers that can only tell whether a candidate is theirs by querying their own backing
    // table (KTD4/HTD note) — DispatchToHandlersAsync gives all three the identical still-unclaimed batch
    // left after the componenttype-gated handlers run, rather than progressively narrowing it relative to
    // each other, so one handler's claim or query failure never silently skips another's independent
    // attempt against the same candidate.
    static readonly HashSet<Type> EntityDetectedHandlerTypes =
    [
        typeof(CustomApiFamilyHandler),
        typeof(BotHandler),
        typeof(ConnectionReferenceHandler),
    ];

    // Threads dependency-deferred entries from RunPreImportAsync to RunPostImportAsync on the same instance.
    IReadOnlyList<OrphanEntry> _deferred = [];

    // U11 (R12): threads declared-PostImportOnly entries from RunPreImportAsync to RunPostImportAsync on
    // the same instance — mirrors _deferred's role but for entries never attempted pre-import at all
    // (distinct from _deferred's entries, which were attempted and reactively deferred only after a
    // dependency fault, per R13/KTD10). Merged with _deferred in RunPostImportAsync before the single
    // ExecuteInOrderAsync call.
    IReadOnlyList<OrphanEntry> _postImportOnly = [];

    // Execution-time fallback for componenttype-gated Auto handlers: their findings leave EntityName
    // null (see e.g. PluginAssemblyFamilyHandler.BuildFinding), so PerformActionAsync resolves the
    // delete target's table name from ComponentType here instead. Entity-detected handlers (CustomApi
    // family, Bot, ConnectionReference) set EntityName explicitly since their componenttype is
    // env-specific and can't be mapped through a fixed table like this one.
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
    // choice set) are reported as unrecognized rather than guessed at. Still used by the generic-fallback
    // path (LogUnsupportedOrphansAsync) for any candidate no handler claims.
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

        var result = await CompareAsync(context, ct).ConfigureAwait(false);

        if (context.Mode == RunMode.NoDelete)
            return;

        // R12: entries declared PostImportOnly by their handler are excluded from the pre-import
        // execution pass entirely — never attempted, never subject to TryExecuteEntryAsync's reactive
        // dependency-deferral (R13, untouched) — and threaded to RunPostImportAsync via _postImportOnly
        // instead, mirroring _deferred's existing instance-field pattern.
        var preImportEntries = result.Entries.Where(e => e.Timing == OrphanTiming.PreImportEligible).ToList();
        _postImportOnly = result.Entries.Where(e => e.Timing == OrphanTiming.PostImportOnly).ToList();

        _deferred = await ExecuteInOrderAsync(context.Service, context.SolutionName, preImportEntries, isPostImport: false, ct).ConfigureAwait(false);
    }

    // Thin wrapper for RunPreImportAsync's caller (DeployCommand), which already has a PostDeployContext
    // built for the whole IPostDeployService fan-out (it also carries RunMode from --no-delete and a real
    // PackagePath from its own packing step). Unpacks it and delegates to the primitives-based overload
    // below, which is where the actual comparison logic lives (KTD12) — PostDeployContext is a deploy-
    // pipeline type (it still carries PackagePath, which this comparison never reads), so the engine
    // itself shouldn't be coupled to its shape.
    public Task<CompareResult> CompareAsync(PostDeployContext context, CancellationToken ct, string? noDeleteHint = "(--no-delete active)") =>
        CompareAsync(context.PackageSrcRoot, context.Service, context.SolutionName, context.EnvironmentUrl, context.Mode, ct, noDeleteHint);

    // Convenience overload for callers with no packed/mutating context of their own (e.g. DriftCommand) —
    // takes a packageFolder (parent of src) rather than packageSrcRoot, matching ComponentClassifier.
    // ParseLocalSource's own parameter, and always runs in RunMode.NoDelete since these callers never mutate.
    public Task<CompareResult> CompareAsync(
        string packageFolder,
        IOrganizationServiceAsync2 service,
        string solutionName,
        string environmentUrl,
        CancellationToken ct,
        string? noDeleteHint = null) =>
        CompareAsync(Path.Combine(packageFolder, "src"), service, solutionName, environmentUrl, RunMode.NoDelete, ct, noDeleteHint);

    // Comparison-only half of the pre-import step (KTD5): parses committed source, queries live
    // solutioncomponents, resolves sNewIds via all existing special-casing (schemaName, entity,
    // OptionSet), dispatches raw orphan candidates to the U1-U8 handler set (U9), classifies orphans, and
    // prints the report — stopping before ExecuteInOrderAsync (the mutating delete/remove step), so this
    // is callable from a read-only context (used today by DriftCommand) without mutating anything.
    // `noDeleteHint` lets a caller that has no `--no-delete` flag of its own (DriftCommand is always
    // read-only) suppress or replace the deploy-specific "(--no-delete active)" phrasing in the printed
    // report.
    //
    // Takes packageSrcRoot/service/solutionName/environmentUrl/mode directly rather than a PostDeployContext
    // — this is the real comparison engine both public overloads above delegate to, and its dependencies
    // should be exactly what it needs, not a deploy-pipeline type carrying fields (like PackagePath) it
    // never reads (KTD12). Owns parsing packageSrcRoot itself (via ComponentClassifier.ParseLocalSource)
    // rather than reading pre-parsed LocalComponents/EntityLogicalNames/NamedComponents.
    //
    // Returns a CompareResult rather than a bare entry list so a caller can tell "compared and found
    // nothing" (Skipped: false, Entries: []) apart from "the comparison itself didn't run" (Skipped:
    // true) — the two empty-input guards below are a skip, not a verified-clean result. RunPreImportAsync
    // doesn't need this distinction (it only ever acts on Entries), but DriftCommand does: a solution
    // with no local components or no live components at all should not report the same "no drift" as
    // one that was actually compared and matched.
    public async Task<CompareResult> CompareAsync(
        string packageSrcRoot,
        IOrganizationServiceAsync2 service,
        string solutionName,
        string environmentUrl,
        RunMode mode,
        CancellationToken ct,
        string? noDeleteHint = "(--no-delete active)")
    {
        var (sNew, entityLogicalNames, namedComponents) = ComponentClassifier.ParseLocalSource(packageSrcRoot);

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

        // OptionSet (9) roots are also schemaName-declared in Solution.xml, but OptionSet is metadata,
        // not a data-table row — NameResolvableTypes' QueryExpression pattern can't resolve it, so
        // ResolveNamedComponentIdsAsync silently skips it. Resolve it via a metadata request instead
        // (RetrieveOptionSetRequest) and fold it into sNewIds the same way, before the orphan diff runs
        // (KTD1) — a still-declared OptionSet must never become an orphan candidate at all.
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

        var detectionContext = new DetectionContext(packageSrcRoot, service, solutionName, environmentUrl, mode, entityLogicalNames);
        var entries = await DispatchToHandlersAsync(detectionContext, namedComponents, orphans, ct).ConfigureAwait(false);

        PrintReport(entries, mode, solutionName, environmentUrl, noDeleteHint);

        return new CompareResult(entries);
    }

    // U9: replaces the old inline per-type branching (autoOrphans/unknownOrphans/entityDetectedTypes
    // split) with dispatch to the U1-U8 handler set. Handlers run once each, in FamilyOrder (KTD1),
    // always against the candidates still unclaimed by an earlier handler in that order — this covers
    // both dispatch shapes the Planning Contract describes uniformly: a componenttype-gated handler
    // (PluginAssembly family, WebResource, Workflow, Role, Entity family) simply ignores candidates
    // outside its own componenttype gate no matter how large the batch it's handed, while an
    // entity-detected handler (CustomApi family, Bot, ConnectionReference) receives the same
    // "still-unclaimed-so-far" batch as its one batched query — since a real candidate can only ever be a
    // row in one of these tables, this is behaviorally identical to handing every entity-detected handler
    // an identical fixed "remainder after componenttype-gated dispatch" batch, without the orchestrator
    // needing to special-case which shape a given handler is.
    //
    // A candidate absent from a handler's Findings is either recognized-but-clean (in ClaimedIds — e.g.
    // still locally declared, or a WebResource exempted via annotation) and silently dropped, or
    // unrecognized by every handler (never in any ClaimedIds) and routed to the generic-fallback verbose
    // preview (R8) — computed as the candidate set minus the union of every handler's ClaimedIds, never
    // minus the union of Findings (that earlier shape would leak a spurious "not tracked yet" line for a
    // recognized-but-clean candidate that doesn't exist in today's behavior).
    async Task<List<OrphanEntry>> DispatchToHandlersAsync(
        DetectionContext detectionContext,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents,
        List<(Guid ObjectId, int ComponentType)> orphans,
        CancellationToken ct)
    {
        // Caller (CompareAsync) already has every piece DetectionContext needs and builds it once — this
        // method's own dependencies are the context plus the two locally-computed collections (KTD1's
        // FamilyOrder dispatch needs orphans; the generic-fallback preview needs namedComponents) that
        // aren't part of DetectionContext's shape.
        var service            = detectionContext.Service;
        var packageSrcRoot     = detectionContext.PackageSrcRoot;
        var solutionName       = detectionContext.SolutionName;
        var entityLogicalNames = detectionContext.EntityLogicalNames;

        var claimedIds      = new HashSet<Guid>();
        var findings         = new List<HandlerFinding>();
        var familyIndexById  = new Dictionary<Guid, int>();

        // Split into a detect-only step and a merge-into-shared-state step so Pass 2 below can fan the
        // three entity-detected handlers' DetectAsync calls out concurrently via Task.WhenAll while still
        // merging their results into claimedIds/findings/familyIndexById single-threaded afterward — those
        // are plain HashSet/List/Dictionary, not thread-safe, so no merge may happen inside a concurrent
        // handler's own async continuation.
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
                // KTD3: no handler ships Preview this round (KTD2) — this branch exists so a future
                // Preview handler is field-tested with zero action risk, per R7, without a code change here.
                foreach (var finding in result.Findings)
                    console.Verbose($"[Preview: {handler.GetType().Name}] {finding.DisplayName}");
            }
        }

        async Task RunHandlerAsync(int index, IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates)
        {
            var result = await DetectHandlerAsync(index, candidates).ConfigureAwait(false);
            MergeResult(index, result);
        }

        // Pass 1: componenttype-gated handlers (PluginAssembly family, WebResource, Workflow, Role,
        // Entity family — KTD2) each match by componenttype alone, so their gates never overlap; each
        // runs against whatever the prior one left unclaimed purely as an optimization (skips a handler
        // a needless empty-candidate call once nothing is left), not because correctness depends on the
        // shrinking.
        for (var i = 0; i < _orderedHandlers.Count; i++)
        {
            if (EntityDetectedHandlerTypes.Contains(_orderedHandlers[i].GetType())) continue;
            var remaining = orphans.Where(c => !claimedIds.Contains(c.ObjectId)).ToList();
            await RunHandlerAsync(i, remaining).ConfigureAwait(false);
        }

        // Pass 2: entity-detected handlers (CustomApi family, Bot, ConnectionReference — KTD4) each
        // independently query their own backing table against the SAME still-unclaimed batch left after
        // pass 1 — not a batch progressively narrowed relative to EACH OTHER. A real candidate can only
        // ever be a row in one of these three tables, but KTD4's isolation guarantee (one handler's own
        // query failure or claim must never suppress another's independent attempt) only holds if every
        // entity-detected handler actually gets to run its own query against the identical batch, exactly
        // like the old shared Task.WhenAll batch did — narrowing the batch as each handler claims from it
        // would silently skip a later handler's query whenever an earlier one claimed the same candidate
        // first, which is observable (e.g. a live diagnostic warning) even though the claim itself was
        // correct.
        //
        // The three handlers' own queries have no data dependency on each other, so they dispatch
        // concurrently via Task.WhenAll (matching pre-refactor latency — this was one shared Task.WhenAll
        // batch before U9's orchestrator rewrite serialized it into three sequential waves). Results are
        // merged single-threaded afterward, in _orderedHandlers' declared order (KTD1) rather than
        // task-completion order — Task.WhenAll(IEnumerable<Task<T>>) already returns its result array in
        // input order, so zipping entityDetectedIndices with entityDetectedResults below preserves that
        // order for familyIndexById/findings.
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
            var localIdentifiers = BuildLocalIdentifierHarvest(packageSrcRoot, entityLogicalNames, namedComponents);
            await LogUnsupportedOrphansAsync(service, unclaimed, localIdentifiers, ct).ConfigureAwait(false);
        }

        // Execution-time cross-solution-membership Delete-vs-RemoveFromSolution override — spans
        // handlers (a handler only ever proposes Delete; whether another solution still needs the
        // component is an orchestrator-level concern), so it's re-applied here on top of every Auto
        // handler's findings, same as today. Only Delete-action findings are ever candidates for the
        // override — Manual findings (Role, EntityFamily, Bot, ConnectionReference) never reach it,
        // matching today's exact scope.
        var deleteCandidateIds = findings.Where(f => f.Action == OrphanAction.Delete).Select(f => f.ObjectId).ToList();
        var crossSolution = deleteCandidateIds.Count > 0
            ? await GetCrossSolutionMembershipAsync(service, deleteCandidateIds, ct).ConfigureAwait(false)
            : [];

        // Sorted once here (KTD1: cross-family via FamilyOrder/familyIndexById, then per-family via each
        // finding's own SequenceHint) — ExecuteInOrderAsync and PrintReport both just consume this list's
        // order downstream rather than re-deriving it themselves.
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
        var solutionName = context.SolutionName;
        var mode         = context.Mode;

        // U11 (R12): merges reactively-deferred entries (_deferred — attempted pre-import, faulted on a
        // dependency, retried here per R13/KTD10) with declared-PostImportOnly entries (_postImportOnly —
        // never attempted pre-import at all) into one candidate list up front. Both sets were classified
        // from the same CompareAsync-time live-state snapshot, and live state can equally have moved on
        // for either kind by the time this runs (e.g. cross-solution membership changed as a side effect
        // of the import) — so both need the same "still present / not re-declared locally / cross-solution
        // override" re-validation below before executing, and both converge on the single
        // ExecuteInOrderAsync call at the end (KTD10 — one code path decides execution order). The two
        // sets are concatenated, not re-sorted by family/SequenceHint: _deferred already preserves its own
        // DispatchToHandlersAsync-derived order (see ExecuteInOrderAsync's comment), and _postImportOnly
        // is itself a filtered, order-preserving subset of that same sorted list — cross-cutting order
        // between the two sets was never established by any SequenceHint (family-scoped only, per KTD1),
        // and any real ordering surprise here still falls back to the existing warn-and-report-failed path
        // TryExecuteEntryAsync already uses for any post-import fault.
        var candidates = _deferred.Concat(_postImportOnly).ToList();

        // Re-parses committed source (cheap — one small XML file plus a folder scan) rather than
        // threading the CompareAsync-time parse across the pre/post-import boundary — this is the same
        // tradeoff RunPreImportAsync/RunPostImportAsync already make for querying live state twice.
        var (sNew, _, _) = ComponentClassifier.ParseLocalSource(context.PackageSrcRoot);

        if (candidates.Count == 0 || mode == RunMode.NoDelete)
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

    // OptionSet's own metadata-request resolution path (see the call site in CompareAsync) — kept
    // separate from ResolveNamedComponentIdsAsync's NameResolvableTypes/QueryExpression pattern since
    // OptionSet has no backing data table to query. Unlike ResolveEntityMetadataIdsAsync, a failed
    // request here (e.g. a genuinely-deleted global choice) is caught per-name so it doesn't block
    // resolution of the others — RetrieveOptionSetRequest throws for a name that no longer exists,
    // whereas RetrieveEntityRequest's precondition (the parsed entityLogicalNames) never includes deleted
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
    // folded into sNewIds — rather than guessed at. This is a pre-diff step (KTD1 — a still-declared
    // component must never become an orphan candidate at all) and stays in the orchestrator regardless of
    // which handler would eventually claim the component as an orphan; it has nothing to do with the
    // handler dispatch below.
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

    // componenttype → backing table, keyed by the componenttype value, resolvable via a single bulk
    // QueryExpression per type. Still needed by two pre-diff/fallback concerns unaffected by the handler
    // dispatch: ResolveNamedComponentIdsAsync's schemaName pre-diff resolution (any of these ten types
    // could in principle be schemaName-declared in Solution.xml) and ResolveGroupNamesAsync's generic-
    // fallback name resolution for a candidate no handler claims (e.g. Form/View/ConnectionRole, which
    // have no handler in the R14 roster). The six entries also migrated into their owning handler
    // (PluginAssemblyFamilyHandler, WebResourceHandler, WorkflowHandler, RoleHandler) keep their own copy
    // there — this table is not superseded, it now serves the two concerns above only.
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

    // Resolves componenttype → display name via solutioncomponentdefinition, Dataverse's own lookup
    // table for component types (documented for the Web API as GET solutioncomponentdefinitions?
    // $select=name,solutioncomponenttype — see Power Pages CLI solution docs). Used ONLY for the
    // generic-fallback verbose preview below — it identifies a type, it does not verify one, so it must
    // never feed into the actionable report.
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
    // Verbose-preview only, same caveat as ResolveComponentTypeNamesAsync above. Bot/ConnectionReference
    // now have their own handlers, but a real record can still land here if a handler's own live query
    // failed with an infrastructure fault (KTD6) and its candidates fell through unclaimed.
    static readonly Dictionary<string, (string IdAttribute, string NameAttribute)> ResolvedTypeNameAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connectionreference"] = ("connectionreferenceid", "connectionreferencelogicalname"),
        ["bot"]                 = ("botid", "name"),
    };

    // KTD5: flat, case-insensitive identifier set drawn only from local shapes already scanned for
    // known-shape types (R7 — never an unscoped, whole-repo string search). Built once per
    // DispatchToHandlersAsync call (itself called exactly once per CompareAsync run) and used only to
    // enrich LogUnsupportedOrphansAsync's verbose preview (R6) — membership here is informational only
    // and never promotes a type into the actionable report.
    static HashSet<string> BuildLocalIdentifierHarvest(
        string packageSrcRoot,
        IReadOnlyList<string> entityLogicalNames,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents)
    {
        var harvest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, schemaName) in namedComponents)
            harvest.Add(schemaName);

        harvest.UnionWith(entityLogicalNames);

        var customApiNames = ComponentClassifier.ScanCustomApiNames(packageSrcRoot);
        harvest.UnionWith(customApiNames.ApiUniqueNames);
        harvest.UnionWith(customApiNames.RequestParameterNames);
        harvest.UnionWith(customApiNames.ResponsePropertyNames);

        harvest.UnionWith(ComponentClassifier.ScanBotSchemaNames(packageSrcRoot));
        harvest.UnionWith(ComponentClassifier.ScanConnectionReferenceLogicalNames(packageSrcRoot));

        return harvest;
    }

    // Verbose-only preview of orphan candidates no handler claimed (R8) — the generic-fallback path.
    // Resolves the type's own label (ManualTypeLabels, falling back to solutioncomponentdefinition for
    // env-specific codes) and the individual record's name where a lookup exists, plus what the
    // no-handler-exists logic would have proposed — purely informational. R6: when the resolved name
    // matches localIdentifiers (KTD5), the line also notes a possible local match — this never changes
    // control flow, the orphan still doesn't reach entries/the report.
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

    // Shared by every "group orphans by componentType, resolve display names via NameResolvableTypes"
    // loop still remaining after the handler dispatch (the unsupported-type verbose preview) — a type
    // with no NameResolvableTypes entry resolves to an empty map rather than a query.
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

    // KTD10: generalized to execute in the order the entries list already carries (assigned once by
    // DispatchToHandlersAsync per KTD1 — cross-family via FamilyOrder, then per-family via SequenceHint)
    // rather than re-deriving order from the old static ExecutionOrder/CustomApiEntityOrder arrays.
    // RunPostImportAsync's reEntries preserve that same relative order (they're built by filtering
    // _deferred concatenated with _postImportOnly — U11, R12 — each of which is itself an
    // order-preserving subset of this method's own attempt loop / DispatchToHandlersAsync's sorted list,
    // in that order), so no re-sort is needed here either way. The IsDependencyError-triggered reactive
    // deferral in TryExecuteEntryAsync is untouched (R13) — this generalization only changes what decides
    // the *attempt* order, never the fault-handling/deferral behavior.
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

    // Untouched by U9 (R13/KTD10) — the IsDependencyError-triggered reactive deferral (attempt, catch
    // dependency fault, defer, retry post-import) and the Workflow deactivate-before-delete step are both
    // orthogonal to the handler-dispatch refactor. WorkflowHandler only classifies (statecode -> Prio);
    // this method still owns actually deactivating a Workflow before deleting it, exactly as before.
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

    // U10 (R1/R6): automated entries within their existing DispatchToHandlersAsync order (KTD1) are
    // additionally grouped by Prio — Prio1 first, since these block deployment — layered on top of
    // today's Action grouping rather than replacing it. Manual entries and the summary line are
    // unaffected: R7 excludes Preview findings from this report entirely (they never reach `entries`,
    // see DispatchToHandlersAsync's console.Verbose branch), and generic-fallback candidates never
    // carry a Prio at all (R8), so every automated entry here is guaranteed a real Prio1/2/3 by
    // construction — PriorityOrder's trailing None slot only guards against that invariant breaking.
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
                var label = mode == RunMode.NoDelete ? NoDeleteLabel(entry.Action) : ActionLabel(entry.Action);
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

        if (mode == RunMode.NoDelete)
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

    // KTD9: no default arm — an enum addition to OrphanPriority without a matching case here is a
    // compile error (CS8509), not a silently-dropped report group.
    static string PriorityLabel(OrphanPriority priority) => priority switch
    {
        OrphanPriority.Prio1 => "Prio1 — blocks deployment",
        OrphanPriority.Prio2 => "Prio2 — still running deleted logic",
        OrphanPriority.Prio3 => "Prio3 — safe to clean up",
        OrphanPriority.None  => "Unclassified",
    };

    static string PriorityColor(OrphanPriority priority) => priority switch
    {
        OrphanPriority.Prio1 => "red",
        OrphanPriority.Prio2 => "yellow",
        OrphanPriority.Prio3 => "dim",
        OrphanPriority.None  => "dim",
    };

    static bool IsDependencyError(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
        (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
}
