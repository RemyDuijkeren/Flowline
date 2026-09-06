using System.ServiceModel;
using Flowline.Core.Services;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Configure;

/// <summary>Writes the two component classes that carry a value rather than a state (R7, R11a).</summary>
/// <remarks>
/// <b>An environment variable is two tables.</b> <c>environmentvariabledefinition</c> holds the schema name,
/// the type and a default; <c>environmentvariablevalue</c> holds the per-environment override and a lookup
/// back to the definition. Setting a value creates or updates the value row and never writes the definition,
/// because the default belongs to the solution and the override belongs to the environment.
///
/// <b>A value row is found by its definition lookup, never by name.</b> Confirmed against a live environment:
/// <c>environmentvariablevalue.schemaname</c> holds a GUID, not the definition's schema name, so matching on
/// it would find nothing and silently create a duplicate row on every run.
///
/// <b>No propagation wait.</b> Re-reading a row Flowline just wrote proves the write landed, not that the
/// platform propagated a binding to the flow runtime, and no observable signal distinguishes the two (KTD6).
/// A flow activation that fails on an unpropagated binding is reported like any other failure, with the
/// message saying a re-run will succeed.
/// </remarks>
public static class ComponentValueWriter
{
    /// <summary>Sets an environment variable's value for this environment.</summary>
    /// <param name="definitionId">The <c>environmentvariabledefinition</c> row the value belongs to.</param>
    public static async Task<ComponentOutcome> ApplyEnvironmentVariableAsync(
        IOrganizationServiceAsync2 service,
        InventoryComponent component,
        string declaredValue,
        CancellationToken ct)
    {
        try
        {
            var existing = await FindValueRowAsync(service, component.Id, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                if (string.Equals(existing.GetAttributeValue<string>("value"), declaredValue, StringComparison.Ordinal))
                    return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Unchanged);

                await service.UpdateAsync(new Entity("environmentvariablevalue", existing.Id)
                {
                    ["value"] = declaredValue,
                }, ct).ConfigureAwait(false);

                return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied);
            }

            // No value row yet, so the definition's default is what is currently in effect. A declared value
            // equal to that default still needs a row: the default belongs to the solution and would move
            // with the next import, while the override is this environment's own statement.
            await service.CreateAsync(new Entity("environmentvariablevalue")
            {
                ["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", component.Id),
                ["value"] = declaredValue,
            }, ct).ConfigureAwait(false);

            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied);
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Failed,
                ComponentStateWriter.DescribeFault(component, ex));
        }
    }

    /// <summary>Binds a connection reference to a connection.</summary>
    public static async Task<ComponentOutcome> ApplyConnectionReferenceAsync(
        IOrganizationServiceAsync2 service,
        InventoryComponent component,
        string connectionId,
        string? currentConnectionId,
        CancellationToken ct)
    {
        if (string.Equals(currentConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Unchanged);

        try
        {
            await service.UpdateAsync(new Entity("connectionreference", component.Id)
            {
                ["connectionid"] = connectionId,
            }, ct).ConfigureAwait(false);

            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied);
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Failed,
                DescribeBindingFault(component, ex));
        }
    }

    /// <summary>Finds the value row for a definition, or <c>null</c> when the default is still in effect.</summary>
    internal static async Task<Entity?> FindValueRowAsync(
        IOrganizationServiceAsync2 service, Guid definitionId, CancellationToken ct)
    {
        var query = new QueryExpression("environmentvariablevalue")
        {
            ColumnSet = new ColumnSet("environmentvariablevalueid", "value"),
            NoLock = true,
        };
        query.Criteria.AddCondition("environmentvariabledefinitionid", ConditionOperator.Equal, definitionId);

        var rows = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    /// <summary>
    /// A binding failure names the connection and the sharing fix.
    /// </summary>
    /// <remarks>
    /// This is the likeliest real-world failure on the CI path: a connection reference binds a connection
    /// owned by a specific principal, and an unattended identity often owns none of them. A human sees
    /// ownership in the maker portal; an agent only ever sees this message, so it carries the remedy.
    /// </remarks>
    internal static string DescribeBindingFault(InventoryComponent component, FaultException<OrganizationServiceFault> ex)
    {
        if (ComponentStateWriter.IsDependencyFault(ex))
            return $"Connection reference '{component.Name}' is blocked by a dependency — re-run once it clears.";

        var code = ex.Detail?.ErrorCode ?? 0;
        return $"Couldn't bind connection reference '{component.Name}' (error 0x{code:X8}). " +
               "Check the connection exists in this environment and is owned by, or shared with, the identity running Flowline.";
    }
}
