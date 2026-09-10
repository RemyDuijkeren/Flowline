using System.ServiceModel;
using Flowline.Core.Models;
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
    /// <summary>Sets an environment variable's value for this environment, unless doing so would write over a secret (R13).</summary>
    /// <param name="definitionId">The <c>environmentvariabledefinition</c> row the value belongs to.</param>
    /// <remarks>
    /// The pull side refuses to read a secret it cannot safely round-trip and writes a placeholder in its
    /// place. Without the matching refusal here, that placeholder is just a non-empty value: a pull followed
    /// by an apply would overwrite the real secret with the literal text <c>&lt;set-this-secret&gt;</c>. A
    /// fail-closed pull and a fail-open apply is the same as no protection at all.
    ///
    /// Both halves are checked. The placeholder catches a file that has been through a pull; the definition's
    /// own type and store catch a value typed in by hand against a variable Flowline will not manage, which
    /// no amount of care with the file could otherwise prevent.
    /// </remarks>
    public static async Task<ComponentOutcome> ApplyEnvironmentVariableAsync(
        IOrganizationServiceAsync2 service,
        InventoryComponent component,
        string declaredValue,
        RunMode mode,
        CancellationToken ct)
    {
        if (string.Equals(declaredValue, ConfigurePullService.SecretPlaceholder, StringComparison.Ordinal))
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Skipped,
                $"'{component.Name}' still holds the pull placeholder — set its real value in the settings file, or in the maker portal.");

        if (ConfigurePullService.IsUnreadableSecret(component))
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Skipped,
                $"'{component.Name}' is a secret Flowline won't write — set it in the maker portal.");

        try
        {
            var existing = await FindValueRowAsync(service, component.Id, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                if (string.Equals(existing.GetAttributeValue<string>("value"), declaredValue, StringComparison.Ordinal))
                    return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Unchanged);

                if (mode.IsReportOnly())
                    return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied, "would change");

                await service.UpdateAsync(new Entity("environmentvariablevalue", existing.Id)
                {
                    ["value"] = declaredValue,
                }, ct).ConfigureAwait(false);

                return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied);
            }

            // No value row yet, so the definition's default is what is currently in effect. A declared value
            // equal to that default still needs a row: the default belongs to the solution and would move
            // with the next import, while the override is this environment's own statement.
            if (mode.IsReportOnly())
                return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied, "would change");

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
    /// <remarks>
    /// The current binding comes off the component rather than a parameter: the inventory read already has
    /// it, and a caller with nothing to pass silently turned every apply into a write — which is what shipped
    /// first, and what made a file applied straight back to the environment it came from report Applied
    /// instead of Unchanged (AE1).
    ///
    /// Carries the same placeholder and unreadable-secret checks as the environment-variable writer above
    /// (KTD6), so the two value writers cannot drift apart even though a connection reference carries a
    /// connection identifier rather than a credential and never actually trips the secret check.
    /// </remarks>
    public static async Task<ComponentOutcome> ApplyConnectionReferenceAsync(
        IOrganizationServiceAsync2 service,
        InventoryComponent component,
        string connectionId,
        RunMode mode,
        CancellationToken ct)
    {
        if (string.Equals(connectionId, ConfigurePullService.SecretPlaceholder, StringComparison.Ordinal))
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Skipped,
                $"'{component.Name}' still holds the pull placeholder — set its real value in the settings file, or in the maker portal.");

        if (ConfigurePullService.IsUnreadableSecret(component))
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Skipped,
                $"'{component.Name}' is a secret Flowline won't write — set it in the maker portal.");

        if (string.Equals(component.CurrentValue, connectionId, StringComparison.OrdinalIgnoreCase))
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Unchanged);

        if (mode.IsReportOnly())
            return new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied, "would change");

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
