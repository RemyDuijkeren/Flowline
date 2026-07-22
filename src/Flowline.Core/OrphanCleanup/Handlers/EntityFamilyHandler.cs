using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Handles Entity (1) and Attribute (2) detection.
//
// ResolveEntityMetadataIdsAsync stays in the orchestrator as a pre-diff step that folds declared entity
// roots into sNewIds before the orphan diff runs — this handler never re-verifies entity declaration
// itself (duplicating that check per-candidate risks a parallel-code-path parity bug, see
// docs/solutions/logic-errors/secondary-match-predicate-missing-mode.md), so the Entity path here is
// nearly trivial: resolve a display name and emit Prio3 Manual.
//
// Attribute carries the real work: it's never recorded in Solution.xml's RootComponents at all, so an
// attribute still declared in Entity.xml is only caught here, via ResolveAttributeInfoAsync +
// ComponentClassifier.ScanEntityAttributeLogicalNames.
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

        // Every componenttype-1/2 candidate is claimed, even if the Attribute path below suppresses it
        // out of Findings (still declared in Entity.xml).
        var claimedIds = entityOrphans.Select(c => c.ObjectId)
            .Concat(attributeOrphans.Select(c => c.ObjectId))
            .ToHashSet();

        var findings = new List<HandlerFinding>();

        // Entity already survived the orchestrator's pre-diff exclusion — a genuine orphan needs only a
        // label.
        foreach (var (id, componentType) in entityOrphans)
            findings.Add(EntityFinding(id, componentType, $"Entity {id}"));

        if (attributeOrphans.Count == 0) return new HandlerDetectionResult(findings, claimedIds);

        if (context.EntityLogicalNames.Count == 0)
        {
            // No entity context to cross-check against — report bare.
            foreach (var (id, componentType) in attributeOrphans)
                findings.Add(EntityFinding(id, componentType, $"Attribute {id}"));
            return new HandlerDetectionResult(findings, claimedIds);
        }

        var attributeInfo = await DataverseFaultTolerance.TryQueryAsync(
            () => ResolveAttributeInfoAsync(context.Service, context.EntityLogicalNames, attributeOrphans.Select(o => o.ObjectId), ct),
            [], console, msg => $"Attribute metadata resolution failed ({msg}) — falling back to bare id display this run.");
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
                    ComponentClassifier.ScanEntityAttributeLogicalNames(context.DataverseSolutionSrcRoot, info.EntityLogicalName);

            if (localAttributes.Contains(info.AttributeLogicalName))
                continue; // still declared in Entity.xml — false positive, not an orphan (still claimed above)

            var detail = $"{info.EntityLogicalName}.{info.AttributeLogicalName}";
            findings.Add(EntityFinding(id, componentType, $"Attribute '{detail}' ({id})"));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }

    // Prio3 always — hygiene, human review before removal, not blocking. SequenceHint is 0: these are
    // always Manual, so the ordering hint has no operational effect for this handler's findings.
    static HandlerFinding EntityFinding(Guid id, int componentType, string displayName) =>
        new(id, componentType, displayName, OrphanAction.Manual, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PreImportEligible);

    // Cross-entity attribute lookup, scoped to the solution's own entities — an unfiltered
    // EntityQueryExpression is a full metadata walk that doesn't scale. Attributes outside this
    // solution's root list fall back to a bare GUID rather than a guessed name.
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
