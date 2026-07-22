using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Role (20) needs no local-source scanner of its own — its id is declared directly in Solution.xml and
// resolved by the orchestrator's raw-candidate diff before reaching this handler, so this handler only
// wraps the name lookup for display. Auto/Manual is static Manual (human review before removal); Prio
// is a constant Prio3 (roles don't execute logic).
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
