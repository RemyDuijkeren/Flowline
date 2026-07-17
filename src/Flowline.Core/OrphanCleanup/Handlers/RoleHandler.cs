using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Migrates Role (20) detection (U7) out of OrphanCleanupService's NameResolvableTypes[20]-driven lookup.
// Role needs no new local-source scanner: its id is declared directly in Solution.xml's RootComponent
// and mirrored in the unpacked Roles/<name>.xml file, so the existing plain id-in-LocalComponents match
// (which happens centrally in the orchestrator's raw-candidate diff, before a candidate ever reaches
// this handler) already resolves it correctly — this handler only wraps the name lookup for display.
// Auto/Manual is static Manual (R2) — a Role needs human review before removal via the maker portal,
// same as today. Prio is a constant Prio3 (KTD8) — roles don't execute logic, so they can never be
// Prio1/Prio2.
//
// Code-review fault-isolation fix: name resolution is now caught (KTD6) — a failed lookup degrades to
// the same bare-id display the unresolved-name path below already produces.
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
