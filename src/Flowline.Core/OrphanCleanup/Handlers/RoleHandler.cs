using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Flowline.Core.Console;
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

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var roleCandidates = candidates.Where(c => c.ComponentType == RoleComponentType).ToList();
        if (roleCandidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every componenttype-20 candidate is claimed — this handler always emits a finding for each
        // one, so ClaimedIds equals the full roleCandidates set.
        var claimedIds = roleCandidates.Select(c => c.ObjectId).ToHashSet();

        Dictionary<Guid, string> names;
        try
        {
            names = await EntityNameLookup.GetEntityNamesAsync(context.Service, "role", "roleid", "name", roleCandidates.Select(c => c.ObjectId), ct).ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            names = [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"Role name resolution failed ({Markup.Escape(ex.Message)}) — display falls back to bare id this run.");
            names = [];
        }

        var findings = new List<HandlerFinding>();
        foreach (var candidate in roleCandidates)
        {
            var hasName = names.TryGetValue(candidate.ObjectId, out var name);
            var displayName = hasName ? $"Role '{name}' ({candidate.ObjectId})" : $"Role {candidate.ObjectId}";

            findings.Add(new HandlerFinding(
                candidate.ObjectId,
                RoleComponentType,
                displayName,
                OrphanAction.Manual,
                OrphanPriority.Prio3,
                SequenceHint: 0, // Role is the only type in this family — no ordering to express
                OrphanTiming.PreImportEligible));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }
}
