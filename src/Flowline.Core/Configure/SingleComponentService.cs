using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Configure;

/// <summary>What happened when one component was addressed directly by kind and name, rather than through a settings file (KTD8).</summary>
public enum SingleComponentActionKind
{
    /// <summary>No target state or value was given — the current one was read and nothing was written.</summary>
    Read,

    /// <summary>The requested state or value was written.</summary>
    Applied,

    /// <summary>Already in the requested state or value. Nothing was written.</summary>
    Unchanged,

    /// <summary>The write was refused before it reached Dataverse — an empty value, a pull placeholder, or an unreadable secret.</summary>
    Skipped,

    /// <summary>The write was attempted and Dataverse refused it.</summary>
    Failed,
}

/// <summary>The result of addressing one component directly (R8, R10, R11, KTD8).</summary>
/// <param name="Component">The component that was addressed.</param>
/// <param name="Action">What happened.</param>
/// <param name="PriorEnabled">
/// For a state kind, its state before this call — what a read found, or what a write changed from.
/// <c>null</c> for a value kind.
/// </param>
/// <param name="PriorValue">
/// For a value kind, its value before this call — what a read found, or what a write changed from.
/// <c>null</c> for a state kind.
/// </param>
/// <param name="Detail">Why, for a skip or a failure. Never carries a component value (R10a).</param>
/// <param name="WasSuspended">
/// For a cloud flow or classic workflow, whether it was Suspended: on a write, before the write (KTD7); on a
/// read, right now — <see cref="PriorEnabled"/> alone flattens Suspended to not-enabled, so this is the only
/// way a read can tell a stopped-itself flow from one that was never started. Always <c>false</c> for a
/// plugin step or a value kind.
/// </param>
public sealed record SingleComponentOutcome(
    InventoryComponent Component,
    SingleComponentActionKind Action,
    bool? PriorEnabled = null,
    string? PriorValue = null,
    string? Detail = null,
    bool WasSuspended = false);

/// <summary>
/// Resolves one component by kind and name, reads its current state or value, or writes a new one (R6, R8,
/// R10, R11).
/// </summary>
/// <remarks>
/// Exactly one component per call: addressing goes through <see cref="SolutionInventory.Match"/>, the same
/// case-insensitive name match the file-apply path uses (KTD9), and a state or value write goes through the
/// same guarded writers <see cref="ConfigureApplyService"/> calls, so this path and the file-apply path can
/// never disagree about what a name resolves to or what a write is allowed to do.
///
/// This is a read-or-write for one named component, not a reconciliation: unlike <see cref="ApplyOutcome"/>,
/// there is no undeclared-component report and no all-skipped exit mapping, because a hand-typed name is
/// either the exact one component being changed or an error — never a partial declaration (KTD8).
/// </remarks>
public static class SingleComponentService
{
    /// <summary>Reads or writes a cloud flow's, classic workflow's, or plugin step's state.</summary>
    /// <param name="desiredEnabled">The target state, or <c>null</c> to read the current one instead of writing.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when no component of this kind matches <paramref name="name"/>;
    /// <see cref="ExitCode.ValidationFailed"/> when more than one does (R6, AE3).
    /// </exception>
    public static async Task<SingleComponentOutcome> ReadOrWriteStateAsync(
        IOrganizationServiceAsync2 service,
        SolutionInventory inventory,
        ConfigurableComponentKind kind,
        string name,
        bool? desiredEnabled,
        RunMode mode,
        CancellationToken ct)
    {
        var component = Resolve(inventory, kind, name);

        if (desiredEnabled is null)
            return new SingleComponentOutcome(component, SingleComponentActionKind.Read,
                PriorEnabled: component.Enabled, WasSuspended: component.Suspended);

        var write = await ComponentStateWriter.ApplyAsync(
            service, component, desiredEnabled.Value, mode, ct, currentlySuspended: component.Suspended)
            .ConfigureAwait(false);

        return FromWrite(write, component, priorEnabled: component.Enabled);
    }

    /// <summary>Reads or writes an environment variable's or a connection reference's value.</summary>
    /// <param name="desiredValue">The target value, or <c>null</c> to read the current one instead of writing.</param>
    /// <remarks>
    /// An environment variable's value lives in its own row rather than on the inventory component, which
    /// deliberately leaves it unset (<see cref="SolutionComponentInventory"/>), so both the read path and the
    /// write path's prior-value report need one further lookup for that kind. A connection reference's bound
    /// connection is already on the inventory row.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when no component of this kind matches <paramref name="name"/>;
    /// <see cref="ExitCode.ValidationFailed"/> when more than one does (R6, AE3).
    /// </exception>
    public static async Task<SingleComponentOutcome> ReadOrWriteValueAsync(
        IOrganizationServiceAsync2 service,
        SolutionInventory inventory,
        ConfigurableComponentKind kind,
        string name,
        string? desiredValue,
        RunMode mode,
        CancellationToken ct)
    {
        var component = Resolve(inventory, kind, name);
        var priorValue = await CurrentValueAsync(service, component, ct).ConfigureAwait(false);

        if (desiredValue is null)
            return new SingleComponentOutcome(component, SingleComponentActionKind.Read, PriorValue: priorValue);

        // KTD9: --value is a flag, so an empty one is a deliberate request rather than an omission. The
        // file-apply path silently skips an empty declared value so a captured file re-applies as a no-op;
        // a hand-typed empty value gets an explicit refusal instead, naming the deferred clear capability.
        if (desiredValue.Length == 0)
            return new SingleComponentOutcome(component, SingleComponentActionKind.Skipped, PriorValue: priorValue,
                Detail: $"'{component.Name}' can't be set to an empty value. Clearing a value isn't supported yet.");

        var write = component.Kind == ConfigurableComponentKind.EnvironmentVariable
            ? await ComponentValueWriter.ApplyEnvironmentVariableAsync(service, component, desiredValue, mode, ct)
                .ConfigureAwait(false)
            : await ComponentValueWriter.ApplyConnectionReferenceAsync(service, component, desiredValue, mode, ct)
                .ConfigureAwait(false);

        return FromWrite(write, component, priorValue: priorValue);
    }

    static async Task<string?> CurrentValueAsync(
        IOrganizationServiceAsync2 service, InventoryComponent component, CancellationToken ct)
    {
        if (component.Kind == ConfigurableComponentKind.ConnectionReference)
            return component.CurrentValue;

        var row = await ComponentValueWriter.FindValueRowAsync(service, component.Id, ct).ConfigureAwait(false);
        return row?.GetAttributeValue<string>("value");
    }

    /// <summary>
    /// Resolves the one component a name addresses, or fails naming what was searched (R6, R11, KTD9).
    /// </summary>
    static InventoryComponent Resolve(SolutionInventory inventory, ConfigurableComponentKind kind, string name)
    {
        var match = inventory.Match(kind, name);

        if (match.Ambiguous.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{name}' matches {match.Ambiguous.Count} components in this solution: " +
                string.Join(", ", match.Ambiguous.Select(c => $"'{c.Name}'")) +
                ". Rename one in Dataverse, then re-run.");

        if (match.NotFound)
            throw new FlowlineException(ExitCode.NotFound,
                $"No {Label(kind)} named '{name}' in this environment's copy of the solution. " +
                $"Names are matched on {AddressingKey(kind)}, not the display name.");

        return match.Component!;
    }

    /// <summary>What a kind is called in a sentence, rather than in the enum.</summary>
    static string Label(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "cloud flow",
        ConfigurableComponentKind.Workflow => "classic workflow",
        ConfigurableComponentKind.PluginStep => "plugin step",
        ConfigurableComponentKind.EnvironmentVariable => "environment variable",
        ConfigurableComponentKind.ConnectionReference => "connection reference",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Which column a name is matched against, so a not-found message says what was searched (R11, KTD9).
    /// </summary>
    /// <remarks>
    /// Display names are not addressable, and they are what an operator sees in the maker portal — so
    /// without this the obvious next move after a not-found is to retype the same display name.
    /// </remarks>
    static string AddressingKey(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.EnvironmentVariable => "the schema name",
        ConfigurableComponentKind.ConnectionReference => "the logical name",
        _ => "the unique name",
    };

    static SingleComponentOutcome FromWrite(
        ComponentOutcome write, InventoryComponent component, bool? priorEnabled = null, string? priorValue = null)
    {
        var action = write.Outcome switch
        {
            ComponentOutcomeKind.Applied => SingleComponentActionKind.Applied,
            ComponentOutcomeKind.Unchanged => SingleComponentActionKind.Unchanged,
            ComponentOutcomeKind.Skipped => SingleComponentActionKind.Skipped,
            ComponentOutcomeKind.Failed => SingleComponentActionKind.Failed,
            _ => SingleComponentActionKind.Failed,
        };

        return new SingleComponentOutcome(component, action, priorEnabled, priorValue, write.Detail, write.WasSuspended);
    }
}
