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

    // Appended rather than inserted: test helpers and the inventory build components positionally, and
    // the enum's numeric values reach a settings file only through their names.

    /// <summary>Business rule, addressed by <c>workflow.uniquename</c>.</summary>
    BusinessRule,

    /// <summary>Dataverse custom process action, addressed by <c>workflow.uniquename</c>.</summary>
    Action,

    /// <summary>Business process flow, addressed by <c>workflow.uniquename</c>.</summary>
    BusinessProcessFlow,
}

/// <summary>Which component classes each half of the surface deals in (KTD25).</summary>
/// <remarks>
/// Two sets, because they stopped being the same one. The inline surface can read and switch anything in
/// the process family; a settings file declares only the three classes it has sections for.
///
/// Keeping them apart is what makes widening the read safe. Every process category is a
/// <c>workflow</c> row carrying a <c>statecode</c>, so an inventory the file paths trusted blindly would
/// have a capture write business rules into the file and the next apply deactivate them.
/// </remarks>
public static class ConfigurableComponentKinds
{
    /// <summary>Classes with an on and an off, which <c>settings state</c> addresses.</summary>
    public static readonly ConfigurableComponentKind[] WithState =
    [
        ConfigurableComponentKind.CloudFlow,
        ConfigurableComponentKind.Workflow,
        ConfigurableComponentKind.BusinessRule,
        ConfigurableComponentKind.BusinessProcessFlow,
        ConfigurableComponentKind.Action,
        ConfigurableComponentKind.PluginStep,
    ];

    /// <summary>Classes with a value, which <c>settings value</c> addresses.</summary>
    public static readonly ConfigurableComponentKind[] WithValue =
    [
        ConfigurableComponentKind.EnvironmentVariable,
        ConfigurableComponentKind.ConnectionReference,
    ];

    /// <summary>Classes a settings file has a section for, and therefore can declare.</summary>
    /// <remarks>
    /// A class outside this set is readable and switchable one component at a time and is never written
    /// to a file, never applied from one, and never reported as undeclared. A business rule switched off
    /// this way stays off, because nothing declares it and no push puts it back.
    /// </remarks>
    public static readonly ConfigurableComponentKind[] FileManaged =
    [
        ConfigurableComponentKind.EnvironmentVariable,
        ConfigurableComponentKind.ConnectionReference,
        ConfigurableComponentKind.CloudFlow,
        ConfigurableComponentKind.Workflow,
        ConfigurableComponentKind.BusinessRule,
        ConfigurableComponentKind.BusinessProcessFlow,
        ConfigurableComponentKind.Action,
        ConfigurableComponentKind.PluginStep,
    ];

    /// <summary>Whether a settings file can carry this class at all.</summary>
    public static bool IsFileManaged(ConfigurableComponentKind kind) => FileManaged.Contains(kind);
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

    /// <summary>Components of any of several classes, in inventory order.</summary>
    /// <remarks>
    /// One command now spans several classes, so a name can resolve across them and a listing can show
    /// them together. Order is left alone here; the caller decides how to sort what it got.
    /// </remarks>
    public IEnumerable<InventoryComponent> OfKinds(IReadOnlyCollection<ConfigurableComponentKind> kinds) =>
        Components.Where(c => kinds.Contains(c.Kind));

    /// <inheritdoc cref="Match(IReadOnlyCollection{ConfigurableComponentKind}, string)"/>
    public InventoryMatch Match(ConfigurableComponentKind kind, string name) => Match([kind], name);

    /// <summary>
    /// Finds the single component a declared name addresses, searching the given classes.
    /// </summary>
    /// <remarks>
    /// Dataverse enforces uniqueness on none of these name columns, so two matches is reachable in normal
    /// use, and searching several classes at once makes it more so — a cloud flow and a classic workflow
    /// can share a unique name. Neither an arbitrary pick nor a silent first-match is acceptable there: a
    /// wrong flow switched on in production is worse than a run that stops and names both candidates
    /// (KTD9). The caller reports the candidates, which is why their classes travel with them.
    /// </remarks>
    public InventoryMatch Match(IReadOnlyCollection<ConfigurableComponentKind> kinds, string name)
    {
        var matches = OfKinds(kinds)
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

    /// <summary><c>workflow.category</c> for a business rule.</summary>
    public const int WorkflowCategoryBusinessRule = 2;

    /// <summary><c>workflow.category</c> for a Dataverse custom process action.</summary>
    public const int WorkflowCategoryAction = 3;

    /// <summary><c>workflow.category</c> for a business process flow.</summary>
    public const int WorkflowCategoryBusinessProcessFlow = 4;

    /// <summary>The Process categories the inventory reads.</summary>
    /// <remarks>
    /// <c>workflow.category</c>: 0 Workflow, 1 Dialog, 2 Business Rule, 3 Action, 4 Business Process Flow,
    /// 5 Modern Flow, 6 Desktop Flow, 7 AI Flow (Dynamics 365 adds 9000, Web Client API Flow).
    /// <see href="https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/workflow"/>
    ///
    /// All of them are <c>workflow</c> rows carrying a <c>statecode</c>, which is what makes them all
    /// switchable and what made this list dangerous to widen: a capture that wrote business rules into the
    /// settings file would have the next apply deactivate them. The guard is no longer this filter but
    /// <see cref="ConfigurableComponentKinds.FileManaged"/>, which is what the file paths honour (KTD25).
    ///
    /// Dialog (1) is absent because Microsoft deprecated it and its own docs say to replace it. Desktop
    /// Flow (6) and AI Flow (7) are absent because their activation semantics are not confirmed to be the
    /// same on and off; add them once they are, and nothing else has to change.
    /// </remarks>
    public static readonly int[] ReadWorkflowCategories =
    [
        WorkflowCategoryClassic,
        WorkflowCategoryBusinessRule,
        WorkflowCategoryAction,
        WorkflowCategoryBusinessProcessFlow,
        WorkflowCategoryModernFlow,
    ];

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
        query.Criteria.AddCondition("category", ConditionOperator.In, ReadWorkflowCategories.Cast<object>().ToArray());

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        return entities
            // Checked again on the way out, not because the query is unreliable, but because a category
            // this build does not know about has no honest kind to become. Dropping it here is what keeps
            // a future category out of the inventory until someone decides what it is.
            .Where(e => ReadWorkflowCategories.Contains(
                e.GetAttributeValue<OptionSetValue>("category")?.Value ?? -1))
            .Select(e => new InventoryComponent(
                KindOf(e),
                AddressableName(e),
                e.Id,
                IsWorkflowActive(e),
                Suspended: IsWorkflowSuspended(e)))
            .Where(c => c.Name.Length > 0)
            .ToList();
    }

    /// <summary>The name a Process row is addressed by.</summary>
    /// <remarks>
    /// <c>uniquename</c> normally, because it is stable across renames and is what a settings file keys
    /// on; <c>name</c> is the renameable display label and is otherwise only a fallback for a row that
    /// has no unique name.
    ///
    /// A business process flow is the exception. Dataverse generates its unique name from the backing
    /// entity it creates, so it arrives as <c>msdyn_bpf_d3d97bac8c294105840e99e37a9d1c39</c>: unreadable
    /// in a list and untypeable at a prompt. Nothing is lost by preferring the display name there,
    /// because no settings file has a section for this class (KTD25a), so its unique name is a contract
    /// with nobody. Two flows sharing a display name resolve the way any other ambiguous name does.
    /// </remarks>
    static string AddressableName(Entity workflow)
    {
        var unique = workflow.GetAttributeValue<string>("uniquename");
        var display = workflow.GetAttributeValue<string>("name");

        if (KindOf(workflow) == ConfigurableComponentKind.BusinessProcessFlow)
            return display ?? unique ?? string.Empty;

        return unique ?? display ?? string.Empty;
    }

    /// <summary>Which class a Process row belongs to, by its category.</summary>
    /// <remarks>
    /// A map rather than "anything that is not a modern flow is a classic workflow", which was only true
    /// while the query read two categories. The default arm is unreachable, because the query and the
    /// filter above both restrict the set, and it exists so a category added to one and not the other
    /// fails loudly rather than arriving mislabelled as a classic workflow.
    /// </remarks>
    static ConfigurableComponentKind KindOf(Entity workflow) =>
        workflow.GetAttributeValue<OptionSetValue>("category")?.Value switch
        {
            WorkflowCategoryModernFlow => ConfigurableComponentKind.CloudFlow,
            WorkflowCategoryClassic => ConfigurableComponentKind.Workflow,
            WorkflowCategoryBusinessRule => ConfigurableComponentKind.BusinessRule,
            WorkflowCategoryAction => ConfigurableComponentKind.Action,
            WorkflowCategoryBusinessProcessFlow => ConfigurableComponentKind.BusinessProcessFlow,
            var category => throw new ArgumentOutOfRangeException(
                nameof(workflow), category, "Process category is read but has no component kind."),
        };

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
