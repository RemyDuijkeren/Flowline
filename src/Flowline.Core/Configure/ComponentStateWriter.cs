using System.ServiceModel;
using Flowline.Core.Models;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Configure;

/// <summary>What happened to one component during an apply.</summary>
public enum ComponentOutcomeKind
{
    /// <summary>Already in the declared state. Nothing was written.</summary>
    Unchanged,

    /// <summary>The declared state was written.</summary>
    Applied,

    /// <summary>Named by the file but absent from the target — an R8 skip, exit-code neutral.</summary>
    Skipped,

    /// <summary>The write was attempted and Dataverse refused it.</summary>
    Failed,
}

/// <summary>The result of applying one declared component.</summary>
/// <param name="Kind">Which class the component belongs to.</param>
/// <param name="Name">The declared name, so a report can name it without re-deriving anything.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Why, for a skip or a failure. Never carries a component value (R10a).</param>
/// <param name="WasSuspended">
/// Set when a cloud flow or classic workflow was in the Suspended state before being activated, so KTD8's report can say so and a
/// re-suspension by the platform stays visible.
/// </param>
public sealed record ComponentOutcome(
    ConfigurableComponentKind Kind,
    string Name,
    ComponentOutcomeKind Outcome,
    string? Detail = null,
    bool WasSuspended = false);

/// <summary>Turns cloud flows, classic workflows and plugin steps on or off (R7, KTD7, KTD8).</summary>
/// <remarks>
/// State is written by updating <c>statecode</c> and <c>statuscode</c> directly, following
/// <c>OrphanCleanupService.TryDeactivateWorkflowAsync</c>. Nothing in this codebase uses
/// <c>SetStateRequest</c>, which is the deprecated route.
///
/// <b>The two tables have opposite polarity.</b> A workflow is on at statecode 1 and a plugin step is on at
/// statecode 0. Both pairs below are confirmed against a shipping implementation as well as Flowline's own
/// deactivation path, because getting one backwards silently inverts every declaration in a settings file.
/// </remarks>
public static class ComponentStateWriter
{
    // workflow: State { Draft = 0, Activated = 1, Suspended = 2 }, Status { Draft = 1, Activated = 2 }
    const int WorkflowStateDraft = 0;
    const int WorkflowStateActivated = 1;
    internal const int WorkflowStateSuspended = 2;
    const int WorkflowStatusDraft = 1;
    const int WorkflowStatusActivated = 2;

    // sdkmessageprocessingstep: statecode 0 Enabled / 1 Disabled, statuscode 1 Enabled / 2 Disabled
    const int StepStateEnabled = 0;
    const int StepStateDisabled = 1;
    const int StepStatusEnabled = 1;
    const int StepStatusDisabled = 2;

    /// <summary>Applies one declared state, or reports why it could not be applied.</summary>
    /// <param name="currentlySuspended">
    /// Whether the component was Suspended before this call, which only a caller reading the raw statecode
    /// can know — <see cref="InventoryComponent.Enabled"/> flattens Suspended to not-active. Required, not
    /// defaulted: a suspended flow reads as not-enabled, so a caller that silently omitted this would have a
    /// declared-off flow report Unchanged and stay Suspended (KTD7, KTD8). A plugin step has no third state,
    /// so its caller always passes <c>false</c>.
    /// </param>
    public static async Task<ComponentOutcome> ApplyAsync(
        IOrganizationServiceAsync2 service,
        InventoryComponent component,
        bool desiredEnabled,
        RunMode mode,
        CancellationToken ct,
        bool currentlySuspended)
    {
        if (component.Enabled == desiredEnabled && !currentlySuspended)
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Unchanged);

        var update = BuildStateUpdate(component, desiredEnabled);
        if (update is null)
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Skipped,
                $"{component.Kind} carries a value rather than an on/off state.");

        // Report-only stops here rather than before the comparison above, so a dry run reports exactly the
        // components a real run would change. Deciding it earlier reported every declared component as a
        // change, which made the preview useless for the one thing it is for.
        if (mode.IsReportOnly())
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied,
                "would change", WasSuspended: currentlySuspended);

        try
        {
            await service.UpdateAsync(update, ct).ConfigureAwait(false);
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied,
                WasSuspended: currentlySuspended);
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Failed,
                DescribeFault(component, ex));
        }
    }

    /// <summary>Builds the state update for a component, or <c>null</c> for a class with no state.</summary>
    internal static Entity? BuildStateUpdate(InventoryComponent component, bool enabled) => component.Kind switch
    {
        // Both sections write the same table: a classic workflow and a cloud flow are both `workflow` rows,
        // separated only by category, which the inventory has already read.
        ConfigurableComponentKind.CloudFlow or ConfigurableComponentKind.Workflow => new Entity("workflow", component.Id)
        {
            ["statecode"] = new OptionSetValue(enabled ? WorkflowStateActivated : WorkflowStateDraft),
            ["statuscode"] = new OptionSetValue(enabled ? WorkflowStatusActivated : WorkflowStatusDraft),
        },
        ConfigurableComponentKind.PluginStep => new Entity("sdkmessageprocessingstep", component.Id)
        {
            ["statecode"] = new OptionSetValue(enabled ? StepStateEnabled : StepStateDisabled),
            ["statuscode"] = new OptionSetValue(enabled ? StepStatusEnabled : StepStatusDisabled),
        },
        _ => null,
    };

    /// <summary>
    /// Turns a Dataverse fault into a message that names the component and, where known, the fix.
    /// </summary>
    /// <remarks>
    /// The fault's own text is not echoed: a Dataverse fault on a value write commonly quotes the rejected
    /// value, and R10a keeps values off every output surface. The classification matches
    /// <c>OrphanCleanupService</c>'s so the two commands describe the same fault the same way.
    /// </remarks>
    internal static string DescribeFault(InventoryComponent component, FaultException<OrganizationServiceFault> ex)
    {
        if (IsDependencyFault(ex))
            return $"'{component.Name}' is blocked by a dependency — another component still references it. " +
                   "Remove or update that reference, then re-run.";

        var code = ex.Detail?.ErrorCode ?? 0;
        return $"Dataverse refused the change to '{component.Name}' (error 0x{code:X8}).";
    }

    /// <summary>Matches the dependency-blocked fault the same way orphan cleanup does.</summary>
    internal static bool IsDependencyFault(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
        (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
}
