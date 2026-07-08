using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.Services.OrphanCleanup.Handlers;

// U8: migrates Entity (1) and Attribute (2) detection into one handler (see
// docs/plans/2026-07-08-001-refactor-orphan-cleanup-handler-architecture-plan.md, KTD2/KTD8).
//
// ResolveEntityMetadataIdsAsync stays exactly where it is today — a centralized, pre-diff step in the
// orchestrator (OrphanCleanupService.CompareAsync) that folds declared entity roots into sNewIds
// *before* the raw orphan diff runs. This handler never re-verifies entity declaration itself
// (duplicating that check per-candidate would create a parallel-code-path parity-bug — see
// docs/solutions/logic-errors/secondary-match-predicate-missing-mode.md). By the time a componenttype-1
// candidate reaches DetectAsync, it's already survived that pre-diff exclusion, so the Entity path here
// is nearly trivial: resolve a display name (Entity has no NameResolvableTypes entry today, so this
// stays a bare-GUID label, matching today's exact output) and emit Prio3 Manual.
//
// The Attribute path carries the real work: Attribute is never recorded in Solution.xml's
// RootComponents at all (see ComponentClassifier.ParseSolutionXmlComponents), so it has no pre-diff
// equivalent — an attribute still declared in Entity.xml is only caught here, via this handler's own
// ResolveAttributeInfoAsync + ComponentClassifier.ScanEntityAttributeLogicalNames, migrated unchanged
// from OrphanCleanupService's old attribute-handling block (removed during U9's orchestrator rewrite).
public sealed class EntityFamilyHandler(IAnsiConsole console) : IOrphanHandler
{
    const int EntityComponentType = 1;
    const int AttributeComponentType = 2;

    public HandlerStatus Status => HandlerStatus.Active;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var entityOrphans    = candidates.Where(c => c.ComponentType == EntityComponentType).ToList();
        var attributeOrphans = candidates.Where(c => c.ComponentType == AttributeComponentType).ToList();

        if (entityOrphans.Count == 0 && attributeOrphans.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every componenttype-1/2 candidate is claimed, regardless of whether the Attribute path below
        // suppresses it out of Findings (still declared in Entity.xml) — both Entity and Attribute
        // candidates are always recognized as this handler's own.
        var claimedIds = entityOrphans.Select(c => c.ObjectId)
            .Concat(attributeOrphans.Select(c => c.ObjectId))
            .ToHashSet();

        var findings = new List<HandlerFinding>();

        // Entity: already survived the orchestrator's pre-diff ResolveEntityMetadataIdsAsync exclusion
        // (see class doc comment above) — a genuine orphan needs no further verification, only a label.
        foreach (var (id, componentType) in entityOrphans)
            findings.Add(EntityFinding(id, componentType, $"Entity {id}"));

        if (attributeOrphans.Count == 0) return new HandlerDetectionResult(findings, claimedIds);

        if (context.EntityLogicalNames.Count == 0)
        {
            // No entity context to cross-check against — report bare, matching the fallback path
            // OrphanCleanupService's old attribute-handling block used for this same empty-entityLogicalNames case.
            foreach (var (id, componentType) in attributeOrphans)
                findings.Add(EntityFinding(id, componentType, $"Attribute {id}"));
            return new HandlerDetectionResult(findings, claimedIds);
        }

        // Code-review fault-isolation fix: a failed metadata query is now caught (KTD6) — attributeInfo
        // degrades to empty, which the loop below already treats identically to "unresolved" (bare-id
        // "Attribute {id}" fallback) — no new fallback shape.
        Dictionary<Guid, (string EntityLogicalName, string AttributeLogicalName)> attributeInfo;
        try
        {
            attributeInfo = await ResolveAttributeInfoAsync(
                context.Service, context.EntityLogicalNames, attributeOrphans.Select(o => o.ObjectId), ct).ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            attributeInfo = [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"Attribute metadata resolution failed ({Markup.Escape(ex.Message)}) — falling back to bare id display this run.");
            attributeInfo = [];
        }
        var localAttributesByEntity = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, componentType) in attributeOrphans)
        {
            if (!attributeInfo.TryGetValue(id, out var info))
            {
                findings.Add(EntityFinding(id, componentType, $"Attribute {id}"));
                continue;
            }

            if (!localAttributesByEntity.TryGetValue(info.EntityLogicalName, out var localAttributes))
                localAttributesByEntity[info.EntityLogicalName] = localAttributes =
                    ComponentClassifier.ScanEntityAttributeLogicalNames(context.PackageSrcRoot, info.EntityLogicalName);

            if (localAttributes.Contains(info.AttributeLogicalName))
                continue; // still declared in Entity.xml — false positive, not an orphan (still claimed above)

            var detail = $"{info.EntityLogicalName}.{info.AttributeLogicalName}";
            findings.Add(EntityFinding(id, componentType, $"Attribute '{detail}' ({id})"));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }

    // Prio3 always (KTD8) — hygiene, human review before removal given data-loss risk, but not blocking
    // or risk-executing. SequenceHint is 0 for every entry: these are always Manual (OrphanAction.Manual),
    // so ExecuteInOrderAsync's automatic-execution pass never touches them (it excludes Action == Manual
    // entries) — the ordering hint has no operational effect for this handler's findings today.
    static HandlerFinding EntityFinding(Guid id, int componentType, string displayName) =>
        new(id, componentType, displayName, OrphanAction.Manual, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PreImportEligible);

    // Moved unchanged from OrphanCleanupService's old ResolveAttributeInfoAsync (removed during U9's
    // orchestrator rewrite). Cross-entity attribute lookup, scoped to the solution's own entities
    // (context.EntityLogicalNames) rather than an unfiltered scan —
    // an EntityQueryExpression with no Criteria is the RetrieveAllEntities-equivalent full metadata walk,
    // which doesn't scale. Attributes on entities outside this solution's root list won't resolve — those
    // fall back to a bare GUID rather than a guessed name.
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
}
