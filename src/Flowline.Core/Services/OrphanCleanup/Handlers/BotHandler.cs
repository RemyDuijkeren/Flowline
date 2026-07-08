using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.Services.OrphanCleanup.Handlers;

// U6: migrates Bot's entity-detected orphan detection out of OrphanCleanupService.
// Bot's componenttype is env-specific (see OrphanCleanupService.CustomApiIdAttributes' comment on the
// same shape) — a candidate can only be identified as a Bot by querying the "bot" table directly, so
// match and detect are the same batched async call (see the Planning Contract's HTD note). KTD4: this
// handler owns its own query and try/catch against "bot" only — a ConnectionReferenceHandler failure
// (or vice versa) can never affect this handler's detection, since each is now a fully separate handler
// instance rather than a shared Task.WhenAll batch.
public sealed class BotHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    public async Task<IReadOnlyList<HandlerFinding>> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return [];

        var idList = candidates.Select(c => c.ObjectId).Distinct().ToList();
        if (idList.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many orphan candidates for Bot detection.");

        var idArray = idList.Select(id => (object)id).ToArray();

        List<Entity> rows;
        try
        {
            var query = new QueryExpression("bot")
            {
                // schemaname is Bot's identity attribute (KTD3 note in OrphanCleanupService.TypeName/
                // ResolvedTypeNameAttributes) — "name" is a separate, unrelated display string.
                // publishedon ("Date and time when the Copilot was last published", nullable — Microsoft
                // Dataverse "Copilot (bot)" table reference) distinguishes Published/live (non-null) from
                // never-published/draft (null) for KTD8's Prio rule. Not componentstate: that attribute
                // tracks solution-layer publish state (customization published to the unmanaged layer),
                // which is ~always Published for any component that made it into a solution at all — it
                // doesn't vary with the Copilot's own authoring lifecycle the way publishedon does.
                ColumnSet = new ColumnSet("schemaname", "publishedon"),
                Criteria  = { Conditions = { new ConditionExpression("botid", ConditionOperator.In, idArray) } }
            };
            rows = await context.Service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        }
        // KTD6: a business fault (the bot table genuinely has no matching rows, e.g. Copilot Studio not
        // provisioned in this org) is not evidence any candidate was deleted — resolves quietly to "no
        // candidates claimed", same as an infrastructure fault (network/auth/throttle), which additionally
        // warns since it's a real failure the operator should see.
        catch (FaultException<OrganizationServiceFault>)
        {
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"Bot orphan detection failed ({Markup.Escape(ex.Message)}) — its candidates are skipped this run.");
            return [];
        }

        // Bot has no GUID anywhere in local source — schemaname (bots/<schemaname>/bot.xml) is the only
        // local identity (see ComponentClassifier.ScanBotSchemaNames).
        var localSchemaNames = ComponentClassifier.ScanBotSchemaNames(context.PackageSrcRoot);

        var componentTypeById = new Dictionary<Guid, int>();
        foreach (var candidate in candidates)
            componentTypeById[candidate.ObjectId] = candidate.ComponentType;

        var findings = new List<HandlerFinding>();
        foreach (var row in rows)
        {
            // KTD5: no resolved schemaname means local-source verification never actually ran for this
            // candidate — not evidence of removal. Skip rather than default to "orphaned".
            var schemaName = row.GetAttributeValue<string>("schemaname");
            if (string.IsNullOrEmpty(schemaName)) continue;
            if (localSchemaNames.Contains(schemaName)) continue; // still declared locally — not orphaned

            // KTD8: Published/live (publishedon set) -> Prio2; never-published/draft (publishedon null)
            // -> Prio3.
            var publishedOn = row.GetAttributeValue<DateTime?>("publishedon");
            var priority = publishedOn.HasValue ? OrphanPriority.Prio2 : OrphanPriority.Prio3;

            findings.Add(new HandlerFinding(
                ObjectId: row.Id,
                ComponentType: componentTypeById.GetValueOrDefault(row.Id),
                DisplayName: $"Bot '{schemaName}' ({row.Id})",
                // Manual per KTD8 — Bot can't be deleted automatically, same as today.
                Action: OrphanAction.Manual,
                Priority: priority,
                SequenceHint: 0,
                Timing: OrphanTiming.PreImportEligible,
                EntityName: "bot"));
        }

        return findings;
    }
}
