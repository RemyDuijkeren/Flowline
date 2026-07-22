using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// ConnectionReference's componenttype is env-specific, same shape as CustomApi/Bot — a candidate can
// only be identified as a ConnectionReference by querying the "connectionreference" table directly, so
// match and detect are the same batched async call. This handler owns its own query against
// "connectionreference" only — a BotHandler failure (or vice versa) can never affect this handler's
// detection.
public sealed class ConnectionReferenceHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    public Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        // ConnectionReference has no dedicated folder like Bot's bots/<schemaname>/bot.xml — it's
        // declared inline in Other/Customizations.xml's <connectionreferences> section (see
        // ComponentClassifier.ScanConnectionReferenceLogicalNames).
        var localLogicalNames = ComponentClassifier.ScanConnectionReferenceLogicalNames(context.DataverseSolutionSrcRoot);

        return EntityDetectionHelper.DetectByTableAsync(
            context, candidates, console, ct,
            entityLogicalName: "connectionreference",
            idAttribute: "connectionreferenceid",
            keyAttribute: "connectionreferencelogicalname",
            columnSet: new ColumnSet("connectionreferencelogicalname"),
            localKeys: localLogicalNames,
            label: "ConnectionReference",
            // Prio2 always — a live connection reference remains usable by anything still holding its
            // logical name.
            priority: _ => OrphanPriority.Prio2);
    }
}
