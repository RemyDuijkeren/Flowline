using Flowline.Core.OrphanCleanup;
using Flowline.Core.Services;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Configure;

/// <summary>The five component classes a settings file can declare.</summary>
public enum ConfigurableComponentKind
{
    /// <summary>Environment variable value, addressed by the definition's schema name.</summary>
    EnvironmentVariable,

    /// <summary>Connection reference, addressed by its logical name.</summary>
    ConnectionReference,

    /// <summary>Power Automate cloud flow, addressed by <c>workflow.uniquename</c>.</summary>
    CloudFlow,

    /// <summary>Classic Dataverse workflow, addressed by <c>workflow.uniquename</c>.</summary>
    Workflow,

    /// <summary>Plugin step, addressed by its step name.</summary>
    PluginStep,
}

/// <summary>One component found in the target, with the state the file would be reconciling against.</summary>
/// <param name="Kind">Which class it belongs to.</param>
/// <param name="Name">The addressing key for its class (KTD9) — never an environment-local id.</param>
/// <param name="Id">The record id, used only within this run.</param>
/// <param name="Enabled">Current state, or <c>null</c> for a class that carries a value rather than a state.</param>
/// <param name="CurrentValue">
/// The live value for a class that carries one, read only when a pull needs it. A connection reference
/// carries its bound connection id straight from the inventory read; an environment variable's value lives
/// in a separate row and is filled in later, so it is <c>null</c> here.
/// </param>
/// <param name="Type">
/// <c>environmentvariabledefinition.type</c>, carried so a pull can tell a Secret-type variable from an
/// ordinary one. <c>null</c> for every other class.
/// </param>
/// <param name="SecretStore">
/// <c>environmentvariabledefinition.secretstore</c>: 0 Azure Key Vault, 1 Microsoft Dataverse. Only a Key
/// Vault-backed Secret stores a reference rather than the secret itself, which is the whole reason R13 can
/// pull one verbatim and must not pull any other.
/// </param>
/// <param name="Suspended">
/// Whether a cloud flow or classic workflow was Suspended rather than Draft. <see cref="Enabled"/> flattens both to not-active, so this
/// is the only way an apply can tell a flow that stopped itself from one that was never started (KTD8).
/// Always <c>false</c> for a plugin step, whose table has no third state.
/// </param>
/// <param name="ConnectorId">
/// <c>connectionreference.connectorid</c>: which connector the reference is for, as
/// <c>/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps</c>. Only a connection of the
/// same connector can be bound, so this is what narrows a list of the environment's connections to the
/// ones that would work. <c>null</c> for every other class.
/// </param>
public sealed record InventoryComponent(
    ConfigurableComponentKind Kind,
    string Name,
    Guid Id,
    bool? Enabled,
    string? CurrentValue = null,
    int? Type = null,
    int? SecretStore = null,
    bool Suspended = false,
    string? ConnectorId = null);

/// <summary>Everything the target holds for one solution, in the classes a settings file can declare.</summary>
public sealed record SolutionInventory(IReadOnlyList<InventoryComponent> Components)
{
    /// <summary>Components of one class.</summary>
    public IEnumerable<InventoryComponent> OfKind(ConfigurableComponentKind kind) =>
        Components.Where(c => c.Kind == kind);

    /// <summary>
    /// Finds the single component a declared name addresses.
    /// </summary>
    /// <remarks>
    /// Dataverse enforces uniqueness on none of these name columns, so two matches is reachable in normal
    /// use. Neither an arbitrary pick nor a silent first-match is acceptable there — a wrong flow switched on
    /// in production is worse than a run that stops and names both candidates (KTD9).
    /// </remarks>
    public InventoryMatch Match(ConfigurableComponentKind kind, string name)
    {
        var matches = OfKind(kind)
            .Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new InventoryMatch(null, []),
            1 => new InventoryMatch(matches[0], []),
            _ => new InventoryMatch(null, matches),
        };
    }
}

/// <summary>The outcome of addressing one declared name.</summary>
/// <param name="Component">The single match, or <c>null</c> when absent or ambiguous.</param>
/// <param name="Ambiguous">Every candidate when more than one matched; empty otherwise.</param>
public sealed record InventoryMatch(InventoryComponent? Component, IReadOnlyList<InventoryComponent> Ambiguous)
{
    /// <summary>True when the name matched nothing in the target — an R8 skip.</summary>
    public bool NotFound => Component is null && Ambiguous.Count == 0;
}

/// <summary>Reads a solution's configurable components from a target environment (R5b, R9).</summary>
/// <remarks>
/// Addressing is by name throughout (KTD9). Component ids differ per environment, so a file pulled from PROD
/// and applied to TEST can only line up on names.
///
/// <b>Two queries, not one.</b> A connection reference's <c>componenttype</c> is environment-specific
/// (<c>src/Flowline.Core/OrphanCleanup/Handlers/ConnectionReferenceHandler.cs</c>), so it cannot be selected
/// from <c>solutioncomponent</c> by a constant. The stable classes come from <c>solutioncomponent</c>; the
/// connection references come from their own table and are intersected on id.
/// </remarks>
public static class SolutionComponentInventory
{
    /// <summary><c>solutioncomponent.componenttype</c> for anything in the Process family.</summary>
    /// <remarks>
    /// One component type covers seven unrelated things, so it is never enough on its own — see
    /// <see cref="ManagedWorkflowCategories"/>.
    /// </remarks>
    public const int WorkflowComponentType = 29;

    /// <summary><c>workflow.category</c> for a classic Dataverse workflow.</summary>
    public const int WorkflowCategoryClassic = 0;

    /// <summary><c>workflow.category</c> for a Power Automate cloud flow.</summary>
    public const int WorkflowCategoryModernFlow = 5;

    /// <summary>The only two Process categories a settings file manages.</summary>
    /// <remarks>
    /// <c>workflow.category</c>: 0 Workflow, 1 Dialog, 2 Business Rule, 3 Action, 4 Business Process Flow,
    /// 5 Modern Flow, 6 Desktop Flow, 7 AI Flow (Dynamics 365 adds 9000, Web Client API Flow).
    /// <see href="https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/workflow"/>
    ///
    /// All eight are <c>workflow</c> rows carrying a <c>statecode</c>, so an unfiltered read hands the state
    /// writer a business rule or a business process flow and a pull writes it into the file as an ordinary
    /// component. Applying that file then deactivates it. Narrowing here rather than at the writer keeps the
    /// rows out of the inventory entirely, so <c>configure</c> can neither report nor touch them.
    ///
    /// Business process flows are deliberately outside this set. They are legitimately per-environment
    /// state, but they are not flows in the Power Automate sense and belong in a section of their own if
    /// anyone asks for one.
    /// </remarks>
    public static readonly int[] ManagedWorkflowCategories = [WorkflowCategoryClassic, WorkflowCategoryModernFlow];

    /// <summary><c>solutioncomponent.componenttype</c> for a plugin step.</summary>
    public const int SdkMessageProcessingStepComponentType = 92;

    /// <summary><c>solutioncomponent.componenttype</c> for an environment variable definition.</summary>
    public const int EnvironmentVariableDefinitionComponentType = 380;

    /// <summary>Reads every configurable component the named solution holds in the target.</summary>
    public static async Task<SolutionInventory> ReadAsync(
        IOrganizationServiceAsync2 service,
        string solutionUniqueName,
        CancellationToken ct)
    {
        var componentIds = await QuerySolutionComponentIdsAsync(service, solutionUniqueName, ct).ConfigureAwait(false);

        var components = new List<InventoryComponent>();
        components.AddRange(await ReadWorkflowsAsync(service, Ids(componentIds, WorkflowComponentType), ct).ConfigureAwait(false));
        components.AddRange(await ReadPluginStepsAsync(service, Ids(componentIds, SdkMessageProcessingStepComponentType), ct).ConfigureAwait(false));
        components.AddRange(await ReadEnvironmentVariablesAsync(service, Ids(componentIds, EnvironmentVariableDefinitionComponentType), ct).ConfigureAwait(false));
        components.AddRange(await ReadConnectionReferencesAsync(service, componentIds.Select(c => c.ObjectId).ToList(), ct).ConfigureAwait(false));

        return new SolutionInventory(components);
    }

    static List<Guid> Ids(IReadOnlyList<(Guid ObjectId, int ComponentType)> all, int componentType) =>
        all.Where(c => c.ComponentType == componentType).Select(c => c.ObjectId).ToList();

    static async Task<List<(Guid ObjectId, int ComponentType)>> QuerySolutionComponentIdsAsync(
        IOrganizationServiceAsync2 service, string solutionUniqueName, CancellationToken ct)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid", "componenttype")
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        var result = new List<(Guid, int)>(entities.Count);
        foreach (var entity in entities)
        {
            var objectId = entity.GetAttributeValue<Guid>("objectid");
            if (objectId == Guid.Empty) continue;
            var componentType = entity.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
            if (componentType is null) continue;
            result.Add((objectId, componentType.Value));
        }

        return result;
    }

    static async Task<List<InventoryComponent>> ReadWorkflowsAsync(
        IOrganizationServiceAsync2 service, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        EntityNameLookup.EnsureInLimit(ids.Count, "workflow IDs", "Split the solution or narrow what the settings file declares.");

        var query = new QueryExpression("workflow")
        {
            ColumnSet = new ColumnSet("workflowid", "uniquename", "name", "statecode", "category"),
            NoLock = true,
        };
        query.Criteria.AddCondition("workflowid", ConditionOperator.In, ids.Cast<object>().ToArray());
        // Filtered in the query rather than in LINQ afterwards, so the rows never come back at all.
        query.Criteria.AddCondition("category", ConditionOperator.In, ManagedWorkflowCategories.Cast<object>().ToArray());

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        return entities
            // Checked again on the way out, not because the query is unreliable, but because KindOf has no
            // way to reject a row: a category it does not recognise would silently become a classic
            // workflow. Dropping an unmanaged row here is what keeps a business process flow out of the
            // settings file if the condition above is ever loosened.
            .Where(e => ManagedWorkflowCategories.Contains(
                e.GetAttributeValue<OptionSetValue>("category")?.Value ?? -1))
            .Select(e => new InventoryComponent(
                KindOf(e),
                // uniquename is stable across renames and is what a settings file keys on; name is the
                // renameable display label and is only a fallback for a row that has no unique name.
                e.GetAttributeValue<string>("uniquename") ?? e.GetAttributeValue<string>("name") ?? string.Empty,
                e.Id,
                IsWorkflowActive(e),
                Suspended: IsWorkflowSuspended(e)))
            .Where(c => c.Name.Length > 0)
            .ToList();
    }

    /// <summary>Which settings-file section a Process row belongs to.</summary>
    /// <remarks>
    /// The query already narrows to the two managed categories, so anything that is not a modern flow here
    /// is a classic workflow.
    /// </remarks>
    static ConfigurableComponentKind KindOf(Entity workflow) =>
        workflow.GetAttributeValue<OptionSetValue>("category")?.Value == WorkflowCategoryModernFlow
            ? ConfigurableComponentKind.CloudFlow
            : ConfigurableComponentKind.Workflow;

    /// <summary>
    /// <c>workflow.statecode</c>: 0 Draft, 1 Activated, 2 Suspended.
    /// </summary>
    /// <remarks>
    /// Suspended is neither on nor off. It reads as not-active here so a file declaring the flow on will act
    /// on it, and the caller reports the prior Suspended state so a re-suspension stays visible (KTD8).
    /// Note this is the opposite polarity to a plugin step, where 0 means enabled.
    /// </remarks>
    internal static bool IsWorkflowActive(Entity workflow) =>
        workflow.GetAttributeValue<OptionSetValue>("statecode")?.Value == 1;

    /// <summary>
    /// Whether a workflow stopped itself rather than never having been started.
    /// </summary>
    /// <remarks>
    /// Suspended and Draft both read as not-active, so an apply that only saw <see cref="IsWorkflowActive"/>
    /// would leave a suspended flow suspended when the file declares it off. Carrying the distinction is what
    /// lets a declared-off suspended flow reach Draft, and what lets the run say the flow had been suspended.
    /// </remarks>
    internal static bool IsWorkflowSuspended(Entity workflow) =>
        workflow.GetAttributeValue<OptionSetValue>("statecode")?.Value == ComponentStateWriter.WorkflowStateSuspended;

    static async Task<List<InventoryComponent>> ReadPluginStepsAsync(
        IOrganizationServiceAsync2 service, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        EntityNameLookup.EnsureInLimit(ids.Count, "plugin step IDs", "Split the solution or narrow what the settings file declares.");

        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "name", "statecode"),
            NoLock = true,
            Criteria =
            {
                Conditions =
                {
                    // The same two exclusions `push` applies, so configure and push agree on what a step is
                    // (src/Flowline.Core/Plugins/PluginReader.cs).
                    new ConditionExpression("category", ConditionOperator.NotEqual, "CustomAPI"),
                    new ConditionExpression("stage", ConditionOperator.NotEqual, 30),
                    new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.In, ids.Cast<object>().ToArray()),
                }
            }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        return entities
            .Select(e => new InventoryComponent(
                ConfigurableComponentKind.PluginStep,
                e.GetAttributeValue<string>("name") ?? string.Empty,
                e.Id,
                IsStepEnabled(e)))
            .Where(c => c.Name.Length > 0)
            .ToList();
    }

    /// <summary>
    /// <c>sdkmessageprocessingstep.statecode</c>: 0 Enabled, 1 Disabled.
    /// </summary>
    /// <remarks>
    /// Inverted relative to <c>workflow</c>, where 0 is the *off* state. Reading either table's statecode as
    /// "1 means on" is the trap this pair of helpers exists to keep out of the writers.
    /// </remarks>
    internal static bool IsStepEnabled(Entity step) =>
        step.GetAttributeValue<OptionSetValue>("statecode")?.Value == 0;

    static async Task<List<InventoryComponent>> ReadEnvironmentVariablesAsync(
        IOrganizationServiceAsync2 service, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        EntityNameLookup.EnsureInLimit(ids.Count, "environment variable IDs", "Split the solution or narrow what the settings file declares.");

        var query = new QueryExpression("environmentvariabledefinition")
        {
            ColumnSet = new ColumnSet("environmentvariabledefinitionid", "schemaname", "type", "secretstore"),
            NoLock = true,
        };
        query.Criteria.AddCondition("environmentvariabledefinitionid", ConditionOperator.In, ids.Cast<object>().ToArray());

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        return entities
            .Select(e => new InventoryComponent(
                ConfigurableComponentKind.EnvironmentVariable,
                e.GetAttributeValue<string>("schemaname") ?? string.Empty,
                e.Id,
                // A value, not a state — nothing to switch on or off.
                Enabled: null,
                // The value itself lives in environmentvariablevalue, one row per definition, and is read
                // only when a pull asks for it. Reading every value here would spend a query per definition
                // on every apply, which needs none of them.
                CurrentValue: null,
                Type: e.GetAttributeValue<OptionSetValue>("type")?.Value,
                SecretStore: e.GetAttributeValue<OptionSetValue>("secretstore")?.Value))
            .Where(c => c.Name.Length > 0)
            .ToList();
    }

    static async Task<List<InventoryComponent>> ReadConnectionReferencesAsync(
        IOrganizationServiceAsync2 service, List<Guid> allComponentIds, CancellationToken ct)
    {
        if (allComponentIds.Count == 0) return [];

        // No componenttype filter is possible: the value is environment-specific, so the whole component id
        // set is offered to the connectionreference table and the intersection is whatever comes back.
        EntityNameLookup.EnsureInLimit(allComponentIds.Count, "component IDs", "Split the solution or narrow what the settings file declares.");

        var query = new QueryExpression("connectionreference")
        {
            ColumnSet = new ColumnSet(
                "connectionreferenceid", "connectionreferencelogicalname", "connectionid", "connectorid"),
            NoLock = true,
        };
        query.Criteria.AddCondition("connectionreferenceid", ConditionOperator.In, allComponentIds.Cast<object>().ToArray());

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        return entities
            .Select(e => new InventoryComponent(
                ConfigurableComponentKind.ConnectionReference,
                e.GetAttributeValue<string>("connectionreferencelogicalname") ?? string.Empty,
                e.Id,
                Enabled: null,
                CurrentValue: e.GetAttributeValue<string>("connectionid"),
                ConnectorId: e.GetAttributeValue<string>("connectorid")))
            .Where(c => c.Name.Length > 0)
            .ToList();
    }
}
