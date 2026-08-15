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
using Flowline.Core.WebResources;

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
    OrphanTiming Timing = OrphanTiming.PreImportEligible,
    // Set for findings from a Report handler (or a Guarded handler with no delete-orphans consent):
    // surfaced in the report but never executed. ExecuteInOrderAsync skips these, so a report-only
    // entry can never be deleted even if it carries Action.Delete.
    bool ReportOnly = false,
    // R10: WebResource (componenttype 61) entries only — resolved by WebResourceDependencyChecker
    // against the action the orchestrator already decided (delete vs remove-from-solution). Null means
    // either "not a WebResource entry" (never checked) or "checked, lookup faulted" (unchecked) — the
    // report distinguishes the two via ComponentType, same as WebResourceDependencyResult upstream.
    IReadOnlyList<WebResourceDependent>? Dependents = null)
{
    // R12/KTD4: carried through from the handler that matched this orphan — it already knows what it
    // matched, which is the only positive answer available for types whose component-type code is
    // environment-assigned. Init properties rather than positional parameters so no existing call site
    // has to change, and so Provenance can default to a value a positional default can't express.
    public LocalSourceIdentity Identity { get; init; } = LocalSourceIdentity.None;

    // R1/KTD3: every entry carries exactly one verdict, and it starts Undetermined. An entry that never
    // reaches a lookup — no lookup registered, lookup faulted, compare path that skipped resolution —
    // therefore reads Undetermined and can never read as NeverInSource. This default is what "no code
    // path can leave one unset" rests on.
    public ComponentProvenance Provenance { get; init; } = ComponentProvenance.Undetermined;
}

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

    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        _deferred = [];
        _postImportOnly = [];

        var result = await CompareAsync(context, ct, BuildReportOnlyHint(context.Solution, context.Mode)).ConfigureAwait(false);

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
    // every pre-existing call site (and test) keeps its current behavior unmodified.
    // A managed solution already installed in the target needs no hint at all: PrintReport's
    // managed-upgrade wording names who removes the components, and that stays true under --dry-run, so
    // it outranks the dry-run marker instead of being hidden by it.
    internal static string BuildReportOnlyHint(DeploySolutionInfo solution, RunMode mode = RunMode.NoDelete) =>
        solution.IncludeManaged
            ? solution.ExistsInTarget ? "" : "(managed — first install, cleanup runs on a later upgrade deploy)"
        : mode == RunMode.DryRun ? "(--dry-run preview)"
        : "(--no-delete active)";

    // Thin wrapper for DeployCommand, which already has a PostDeployContext for the IPostDeployService
    // fan-out. Delegates to the primitives overload below so the engine isn't coupled to deploy-only
    // fields like PackagePath.
    public Task<CompareResult> CompareAsync(PostDeployContext context, CancellationToken ct, string? noDeleteHint = "(--no-delete active)") =>
        CompareAsync(context.DataverseSolutionSrcRoot, context.Service, context.Solution.Name, context.Solution.EnvironmentUrl, context.Mode, ct, noDeleteHint, context.DeleteOrphansConsent,
            // Managed + already installed is exactly DeployCommand's --stage-and-upgrade condition, and a
            // Dataverse Upgrade removes every component the new version drops — so nothing in this report
            // is the operator's job on that path.
            context.Solution.IncludeManaged && context.Solution.ExistsInTarget);

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
        string? noDeleteHint = "(--no-delete active)",
        bool deleteOrphansConsent = false,
        // Reframes the report for a managed upgrade import, where Dataverse — not Flowline, not the
        // operator — removes everything the new version drops. Defaults false so every other caller
        // (DriftCommand, unmanaged deploys) keeps the action-oriented wording.
        bool managedUpgrade = false)
    {
        var (sNew, entityLogicalNames, namedComponents) = ComponentClassifier.ParseLocalSource(dataverseSolutionSrcRoot);

        // One spinner for the whole comparison, retitled per phase. The component query is the smaller
        // half of the wait — identity resolution and handler dispatch behind it are a dozen-plus further
        // round trips — so a spinner scoped to the query alone tore the live display down and left the
        // rest looking hung (measured at ~8s of silence against a real solution). Console writes from
        // inside here (skips, warnings, handler notes) render above the spinner; the report itself prints
        // after it closes.
        CompareResult? earlyResult = null;
        IReadOnlyList<OrphanEntry> entries = [];

        await console.Status().FlowlineSpinner().StartAsync(
            $"Querying orphan components in [bold]{solutionName}[/]...",
            async ctx =>
            {
                var sOld = await QuerySolutionComponentsAsync(service, solutionName, ct).ConfigureAwait(false);

                if (sOld.Count == 0)
                {
                    console.Skip("No solution components in Dataverse — skipping orphan check.");
                    earlyResult = new CompareResult([], Skipped: true);
                    return;
                }

                var sNewIds = sNew.Select(c => c.ObjectId).ToHashSet();

                if (sNew.Count == 0)
                {
                    console.Warning("No components in Solution.xml — orphan check skipped to prevent mass deletion.");
                    earlyResult = new CompareResult([], Skipped: true);
                    return;
                }

                ctx.Phase($"Resolving component ids in [bold]{solutionName}[/]...");

                // Entity roots in Solution.xml are recorded by schemaName, not MetadataId — resolve them live
                // so entity components aren't misdiagnosed as orphans. See ComponentClassifier.ParseSolutionXmlComponents.
                if (entityLogicalNames.Count > 0)
                {
                    var resolvedEntityIds = await ResolveEntityMetadataIdsAsync(service, entityLogicalNames, ct).ConfigureAwait(false);
                    sNewIds.UnionWith(resolvedEntityIds);
                }

                // Other types recorded by schemaName instead of id (e.g. WebResource — its id is not portable
                // across environments, so pac always records it by name) — resolve live for the same reason.
                //
                // Role isn't schemaName-declared in Solution.xml (it carries a raw id, see ComponentClassifier),
                // but its raw id isn't portable either — Dataverse reconciles security roles by name on import
                // when a role of that name already exists in the target. Its local name comes from the unpacked
                // Roles/<name>.xml file instead and is folded into the same by-name resolution, additively
                // alongside the raw id already captured in sNew.
                var roleNames = ComponentClassifier.ScanRoleNames(dataverseSolutionSrcRoot);
                var resolvableNamedComponents = roleNames.Count == 0
                    ? namedComponents
                    : namedComponents.Concat(roleNames.Select(name => (ComponentType: RoleComponentType, SchemaName: name))).ToList();

                if (resolvableNamedComponents.Count > 0)
                {
                    var resolvedNamedIds = await ResolveNamedComponentIdsAsync(service, resolvableNamedComponents, ct).ConfigureAwait(false);
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
                    earlyResult = new CompareResult([]);
                    return;
                }

                ctx.Phase($"Checking {orphans.Count} orphan candidate{(orphans.Count == 1 ? "" : "s")}...");

                var detectionContext = new DetectionContext(dataverseSolutionSrcRoot, service, solutionName, environmentUrl, mode, entityLogicalNames, deleteOrphansConsent);
                entries = await DispatchToHandlersAsync(detectionContext, namedComponents, orphans, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (earlyResult != null) return earlyResult;

        PrintReport(entries, mode, solutionName, environmentUrl, noDeleteHint, managedUpgrade);

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

            // Silent handlers never surface or act — verbose log only. Report/Guarded/Auto all surface in
            // the report; whether each surfaced finding actually executes is decided per-entry below
            // (OrphanEntry.ReportOnly), from the owning handler's status plus delete-orphans consent.
            if (handler.Status == HandlerStatus.Silent)
            {
                foreach (var finding in result.Findings)
                    console.Verbose($"[Silent: {handler.GetType().Name}] {finding.DisplayName}");
                return;
            }

            foreach (var finding in result.Findings)
            {
                findings.Add(finding);
                familyIndexById[finding.ObjectId] = index;
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

        // R10/KD6: resolved here, before the synchronous projection below decides Delete vs
        // RemoveFromSolution — every componenttype-61 finding gets checked regardless of ReportOnly, so
        // a report-only entry (the moment the operator is deciding) still carries its dependents.
        var webResourceFindingIds = findings.Where(f => f.ComponentType == WebResourceComponentType).Select(f => f.ObjectId).ToList();
        var webResourceDependentsById = await GetWebResourceDependentsAsync(service, webResourceFindingIds, ct).ConfigureAwait(false);

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

                // Report handlers, and Guarded handlers without delete-orphans consent, surface but never
                // execute — ExecuteInOrderAsync skips ReportOnly entries. Auto (and consented Guarded)
                // stay actionable. Silent findings never reach here (excluded in MergeResult).
                var reportOnly = IsReportOnly(_orderedHandlers[familyIndexById[f.ObjectId]].Status, detectionContext.DeleteOrphansConsent);

                var dependents = f.ComponentType == WebResourceComponentType ? webResourceDependentsById.GetValueOrDefault(f.ObjectId) : null;

                return new OrphanEntry(f.ObjectId, f.ComponentType, f.DisplayName, action, f.EntityName, f.Priority, f.SequenceHint, f.Timing, reportOnly, dependents)
                {
                    // R12: the handler's declared shape rides through unchanged — the orchestrator never
                    // re-derives it, and never substitutes one when the handler declared none.
                    Identity = f.Identity,
                };
            })
            .ToList();
    }

    // This is an informational check backing RunPreImportAsync for every component family — an
    // escaped exception here must not abort orphan detection for the whole deploy over it. Degrade to
    // an empty lookup on fault, so every web resource entry renders unchecked (GetValueOrDefault below
    // returns null) and the rest of the comparison still finishes. WebResourceDependencyChecker.CheckAsync
    // already isolates per-resource Dataverse faults; this guards against a fault in the caller's own code.
    static async Task<Dictionary<Guid, IReadOnlyList<WebResourceDependent>?>> GetWebResourceDependentsAsync(
        IOrganizationServiceAsync2 service, IReadOnlyList<Guid> webResourceFindingIds, CancellationToken ct)
    {
        if (webResourceFindingIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<WebResourceDependent>?>();

        try
        {
            return (await WebResourceDependencyChecker.CheckAsync(service, webResourceFindingIds, ct).ConfigureAwait(false))
                .ToDictionary(r => r.WebResourceId, r => r.Dependents);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Dictionary<Guid, IReadOnlyList<WebResourceDependent>?>();
        }
    }

    // A surfaced finding is report-only (surfaced, never executed) when its handler is Report, or Guarded
    // without explicit `--force delete-orphans` consent. Auto and consented-Guarded are actionable. Silent
    // findings never reach this — MergeResult drops them to verbose before an OrphanEntry is built.
    internal static bool IsReportOnly(HandlerStatus status, bool deleteOrphansConsent) => status switch
    {
        HandlerStatus.Report  => true,
        HandlerStatus.Guarded => !deleteOrphansConsent,
        _                     => false,
    };

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
    const int RoleComponentType = 20;
    const int WebResourceComponentType = 61;

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
            if (!ComponentTypeCatalog.NameResolvableTypes.TryGetValue(group.Key, out var lookup)) continue;

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
        var unlabeledTypes = unsupportedOrphans.Select(o => o.ComponentType).Where(t => !ComponentTypeCatalog.ManualTypeLabels.ContainsKey(t)).Distinct().ToList();
        var resolvedTypeLabels = unlabeledTypes.Count > 0
            ? await ResolveComponentTypeNamesAsync(service, unlabeledTypes, ct).ConfigureAwait(false)
            : [];

        foreach (var group in unsupportedOrphans.GroupBy(o => o.ComponentType))
        {
            var typeLabel = ComponentTypeCatalog.ManualTypeLabels.TryGetValue(group.Key, out var known) ? known
                : resolvedTypeLabels.TryGetValue(group.Key, out var resolved) ? resolved
                : null;

            var names = await ComponentTypeCatalog.ResolveGroupNamesAsync(service, group.Key, group.Select(o => o.ObjectId), ct).ConfigureAwait(false);
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

        // ReportOnly entries (Report handlers, and Guarded handlers without --force delete-orphans) are
        // surfaced in the report but never executed — this is the single chokepoint that guarantees it.
        foreach (var entry in entries.Where(e => e.Action != OrphanAction.Manual && !e.ReportOnly))
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

    // OrphanAction says what *Flowline* can do to a component through the SDK — it says nothing about
    // Dataverse's own Upgrade, which removes every dropped component regardless of type. So on the managed
    // upgrade path the Manual split is meaningless (nobody has to touch the maker portal) and those entries
    // join the Prio groups instead, keeping the Prio1/2/3 triage the operator still needs.
    const string ManagedUpgradeLabel = "removed by the managed upgrade";

    void PrintReport(IReadOnlyList<OrphanEntry> entries, RunMode mode, string solutionName, string environmentUrl, string? noDeleteHint = "(--no-delete active)", bool managedUpgrade = false)
    {
        var automated = managedUpgrade ? entries.ToList() : entries.Where(e => e.Action != OrphanAction.Manual).ToList();
        List<OrphanEntry> manual = managedUpgrade ? [] : entries.Where(e => e.Action == OrphanAction.Manual).ToList();

        console.MarkupLine($"[bold]Orphan components ({entries.Count}):[/]");

        foreach (var priority in PriorityOrder)
        {
            var group = automated.Where(e => e.Priority == priority).ToList();
            if (group.Count == 0) continue;

            console.MarkupLine($"  [bold {PriorityColor(priority)}]{PriorityLabel(priority)}:[/]");
            foreach (var entry in group)
            {
                // ReportOnly entries surface but are never executed (Report handler, or Guarded without
                // consent) — a dim "detected, not auto-removed" label, distinct from the real delete/would-
                // delete wording, so nobody reads a report-only line as an action that ran or will run.
                if (entry.ReportOnly)
                {
                    console.MarkupLine($"    [dim]{Markup.Escape(entry.DisplayName)} — {(managedUpgrade ? ManagedUpgradeLabel : "detected, not auto-removed")}[/]");
                    RenderWebResourceDependents(entry);
                    continue;
                }

                var label = managedUpgrade ? ManagedUpgradeLabel
                    : mode.IsReportOnly() ? ReportOnlyLabel(entry.Action)
                    : ActionLabel(entry.Action);
                console.MarkupLine($"    [{ActionColor(entry.Action)}]{Markup.Escape(entry.DisplayName)} — {label}[/]");
                RenderWebResourceDependents(entry);
            }
        }

        if (manual.Count > 0)
        {
            console.Warning($"{manual.Count} component{(manual.Count == 1 ? "" : "s")} can't be removed automatically:");
            foreach (var entry in manual)
                console.MarkupLine($"  [yellow]{Markup.Escape(entry.DisplayName)}[/] — remove manually via maker portal");
            console.MarkupLine($"  Open {SolutionsListUrl(environmentUrl)}, find '{solutionName}', and remove these from there.");
        }

        if (managedUpgrade)
        {
            console.Info("Components another solution owns only lose membership — they stay installed.");
            console.Skip($"{entries.Count} component{(entries.Count == 1 ? "" : "s")} — the upgrade import removes {(entries.Count == 1 ? "it" : "them")}. Nothing to remove by hand.");
            return;
        }

        // ReportOnly entries are excluded from the delete/remove counts — they are surfaced, not acted on.
        var deleteCount     = entries.Count(e => e.Action == OrphanAction.Delete && !e.ReportOnly);
        var removeCount     = entries.Count(e => e.Action == OrphanAction.RemoveFromSolution && !e.ReportOnly);
        var reportOnlyCount = entries.Count(e => e.ReportOnly);
        var reportOnlySuffix = reportOnlyCount > 0 ? $", {reportOnlyCount} report-only" : "";

        if (mode.IsReportOnly())
        {
            var hint = string.IsNullOrEmpty(noDeleteHint) ? "" : $" {noDeleteHint}";
            console.Skip($"{deleteCount} would be deleted, {removeCount} would be removed from solution, {manual.Count} manual{reportOnlySuffix}.{hint}");
        }
        else
            console.Skip($"{deleteCount} to delete, {removeCount} to remove from solution, {manual.Count} manual{reportOnlySuffix}");
    }

    // R10: mirrors WebResourceExecutor.RenderDependentLines' line shape so the push and orphan-cleanup
    // surfaces read the same. Renders for report-only entries too — a report-only line is exactly when
    // the operator is deciding. Null Dependents on a non-WebResource entry is simply "never checked" and
    // renders nothing; null on a WebResource entry means the lookup faulted and must read as unchecked,
    // never silently as clean.
    void RenderWebResourceDependents(OrphanEntry entry)
    {
        if (entry.ComponentType != WebResourceComponentType) return;

        if (entry.Dependents is { Count: > 0 } dependents)
        {
            foreach (var d in dependents)
                console.MarkupLine(d.Name is not null
                    ? $"      - {Markup.Escape(d.TypeLabel)} '{Markup.Escape(d.Name)}'"
                    : $"      - {Markup.Escape(d.TypeLabel)} {d.ObjectId}");
        }
        else if (entry.Dependents is null)
            console.MarkupLine("      [dim]Couldn't check for dependents.[/]");
    }

    static string SolutionsListUrl(string environmentUrl) =>
        $"{environmentUrl.TrimEnd('/')}/tools/Solution/home_solution.aspx?etn=solution";

    static string ActionLabel(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "delete",
        OrphanAction.RemoveFromSolution => "remove from solution",
        _                               => action.ToString()
    };

    static string ReportOnlyLabel(OrphanAction action) => action switch
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
