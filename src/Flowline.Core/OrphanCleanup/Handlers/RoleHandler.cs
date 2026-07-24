using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Role (20)'s id is declared directly in Solution.xml and resolved by the orchestrator's raw-candidate
// diff before reaching this handler. The orchestrator also resolves it live by name (via
// ComponentClassifier.ScanRoleNames + ResolveNamedComponentIdsAsync), additively alongside the raw id, to
// cover Dataverse reconciling a role to a different live id on import when a same-named role already
// exists in the target — so this handler only wraps the name lookup for display. Auto/Manual is static
// Manual (human review before removal); Prio is a constant Prio3 (roles don't execute logic).
//
// Name resolution is caught — a failed lookup degrades to the bare-id display fallback below.
public sealed class RoleHandler(IAnsiConsole console) : IOrphanHandler
{
    const int RoleComponentType = 20;

    public HandlerStatus Status => HandlerStatus.Active;

    public Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct) =>
        NameLookupDetectionHelper.DetectByComponentTypeAsync(
            context, candidates, console, ct,
            componentType: RoleComponentType,
            entityLogicalName: "role",
            idAttribute: "roleid",
            nameAttribute: "name",
            label: "Role",
            action: OrphanAction.Manual);
}
