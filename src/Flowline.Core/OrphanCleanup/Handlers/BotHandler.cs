using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Bot's componenttype is env-specific (same shape CustomApiFamilyHandler documents for its own family)
// — a candidate can only be identified as a Bot by querying the "bot" table directly, so match and
// detect are the same batched async call. This handler owns its own query against "bot" only — a
// ConnectionReferenceHandler failure (or vice versa) can never affect this handler's detection.
public sealed class BotHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    public Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        // Bot has no GUID anywhere in local source — schemaname (bots/<schemaname>/bot.xml) is the only
        // local identity (see ComponentClassifier.ScanBotSchemaNames).
        var localSchemaNames = ComponentClassifier.ScanBotSchemaNames(context.DataverseSolutionSrcRoot);

        return EntityDetectionHelper.DetectByTableAsync(
            context, candidates, console, ct,
            entityLogicalName: "bot",
            idAttribute: "botid",
            // schemaname is Bot's identity attribute — "name" is a separate, unrelated display string.
            // publishedon distinguishes Published/live (non-null) from never-published/draft (null) for
            // the priority rule below — unlike componentstate, which tracks solution-layer publish state
            // and doesn't vary with the Copilot's own authoring lifecycle.
            keyAttribute: "schemaname",
            columnSet: new ColumnSet("schemaname", "publishedon"),
            localKeys: localSchemaNames,
            label: "Bot",
            // Published/live (publishedon set) -> Prio2; never-published/draft (publishedon null) ->
            // Prio3.
            priority: row => row.GetAttributeValue<DateTime?>("publishedon").HasValue ? OrphanPriority.Prio2 : OrphanPriority.Prio3);
    }
}
