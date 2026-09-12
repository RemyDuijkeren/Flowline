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
    /// <param name="kinds">The classes to search. More than one when the caller did not narrow by type.</param>
    /// <param name="desiredEnabled">The target state, or <c>null</c> to read the current one instead of writing.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when nothing in these classes matches <paramref name="name"/>;
    /// <see cref="ExitCode.ValidationFailed"/> when more than one does (R6, AE3).
    /// </exception>
    public static async Task<SingleComponentOutcome> ReadOrWriteStateAsync(
        IOrganizationServiceAsync2 service,
        SolutionInventory inventory,
        IReadOnlyCollection<ConfigurableComponentKind> kinds,
        string name,
        bool? desiredEnabled,
        RunMode mode,
        CancellationToken ct)
    {
        var component = Resolve(inventory, kinds, name);

        if (desiredEnabled is null)
            return new SingleComponentOutcome(component, SingleComponentActionKind.Read,
                PriorEnabled: component.Enabled, WasSuspended: component.Suspended);

        var write = await ComponentStateWriter.ApplyAsync(
            service, component, desiredEnabled.Value, mode, ct, currentlySuspended: component.Suspended)
            .ConfigureAwait(false);

        return FromWrite(write, component, priorEnabled: component.Enabled);
    }

    /// <summary>Reads or writes an environment variable's or a connection reference's value.</summary>
    /// <param name="kinds">The classes to search. More than one when the caller did not narrow by type.</param>
    /// <param name="desiredValue">The target value, or <c>null</c> to read the current one instead of writing.</param>
    /// <remarks>
    /// An environment variable's value lives in its own row rather than on the inventory component, which
    /// deliberately leaves it unset (<see cref="SolutionComponentInventory"/>), so a read of that kind costs
    /// one further Dataverse query. A connection reference's bound connection is already on the inventory
    /// row, so reading one costs nothing.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when nothing in these classes matches <paramref name="name"/>;
    /// <see cref="ExitCode.ValidationFailed"/> when more than one does (R6, AE3).
    /// </exception>
    public static async Task<SingleComponentOutcome> ReadOrWriteValueAsync(
        IOrganizationServiceAsync2 service,
        SolutionInventory inventory,
        IReadOnlyCollection<ConfigurableComponentKind> kinds,
        string name,
        string? desiredValue,
        RunMode mode,
        CancellationToken ct)
    {
        var component = Resolve(inventory, kinds, name);

        // Read only on the path that reports it. An environment variable's value costs a Dataverse query,
        // and only a read prints it — a write reports what it did, not what was there, and the writer runs
        // its own comparison query anyway. Fetching it up front billed every write for two round trips
        // where one does.
        if (desiredValue is null)
            return new SingleComponentOutcome(component, SingleComponentActionKind.Read,
                PriorValue: await CurrentValueAsync(service, component, ct).ConfigureAwait(false));

        // KTD9: --value is a flag, so an empty one is a deliberate request rather than an omission. The
        // file-apply path silently skips an empty declared value so a captured file re-applies as a no-op;
        // a hand-typed empty value gets an explicit refusal instead, naming the deferred clear capability.
        if (desiredValue.Length == 0)
            return new SingleComponentOutcome(component, SingleComponentActionKind.Skipped,
                Detail: $"'{component.Name}' can't be set to an empty value. Clearing a value isn't supported yet.");

        var write = component.Kind == ConfigurableComponentKind.EnvironmentVariable
            ? await ComponentValueWriter.ApplyEnvironmentVariableAsync(service, component, desiredValue, mode, ct)
                .ConfigureAwait(false)
            : await ComponentValueWriter.ApplyConnectionReferenceAsync(service, component, desiredValue, mode, ct)
                .ConfigureAwait(false);

        return FromWrite(write, component);
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
    /// <remarks>
    /// Searching several classes at once makes a collision reachable that could not happen before: a
    /// cloud flow and a classic workflow can share a unique name, and until one command spanned both,
    /// nothing would have looked at the two together. So an ambiguous match names each candidate's class,
    /// and the remedy offered is the type filter rather than renaming something in Dataverse.
    /// </remarks>
    static InventoryComponent Resolve(
        SolutionInventory inventory, IReadOnlyCollection<ConfigurableComponentKind> kinds, string name)
    {
        var match = inventory.Match(kinds, name);

        if (match.Ambiguous.Count > 0)
        {
            var acrossClasses = match.Ambiguous.Select(c => c.Kind).Distinct().Count() > 1;

            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{name}' matches {match.Ambiguous.Count} components in this solution: " +
                string.Join(", ", match.Ambiguous.Select(c => $"'{c.Name}' ({Label(c.Kind)})")) +
                (acrossClasses
                    ? ". Narrow it with --type, or rename one in Dataverse."
                    : ". Rename one in Dataverse, then re-run."));
        }

        if (match.NotFound)
            throw new FlowlineException(ExitCode.NotFound,
                $"No {Labels(kinds)} named '{name}' in this environment's copy of the solution. " +
                $"Names are matched on {AddressingKeys(kinds)}, not the display name.");

        return match.Component!;
    }

    /// <summary>The classes searched, as a list a sentence can carry.</summary>
    static string Labels(IReadOnlyCollection<ConfigurableComponentKind> kinds)
    {
        var labels = kinds.Select(Label).ToArray();

        return labels.Length == 1
            ? labels[0]
            : string.Join(", ", labels[..^1]) + " or " + labels[^1];
    }

    /// <summary>
    /// Which columns a name is matched against across the classes searched.
    /// </summary>
    /// <remarks>
    /// The three state classes all address on the unique name, so that sentence stays short. The two
    /// value classes disagree with each other, and a caller that did not narrow by type has to be told
    /// both rather than a half-truth about whichever came first.
    /// </remarks>
    static string AddressingKeys(IReadOnlyCollection<ConfigurableComponentKind> kinds)
    {
        var keys = kinds.Select(AddressingKey).Distinct().ToArray();

        return keys.Length == 1 ? keys[0] : string.Join(" or ", keys);
    }

    /// <summary>What a kind is called in a sentence, rather than in the enum.</summary>
    static string Label(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "cloud flow",
        ConfigurableComponentKind.Workflow => "classic workflow",
        ConfigurableComponentKind.BusinessRule => "business rule",
        ConfigurableComponentKind.Action => "action",
        ConfigurableComponentKind.BusinessProcessFlow => "business process flow",
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
