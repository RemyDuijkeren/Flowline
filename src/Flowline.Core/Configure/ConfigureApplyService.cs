using System.Text.Json.Nodes;
using Flowline.Core.Models;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Configure;

/// <summary>One declared value from a PAC-native section.</summary>
public sealed record DeclaredValue(string Name, string Value);

/// <summary>Applies a settings file to an environment (R3, R6, R7, R8, R9, R11).</summary>
/// <remarks>
/// <b>Tier order, not dependency order.</b> Values and connection references apply before flow and step
/// state, regardless of the order the file lists them, because a flow activated before its connection
/// reference is bound fails for a reason the file did not cause. No finer dependency is inferred: which flow
/// consumes which connection reference is not knowable from what the inventory returns, so a component whose
/// prerequisite failed is still attempted and fails on its own rather than being suppressed by a guess.
///
/// <b>The file is a partial declaration.</b> Only components the file names are touched; everything else is
/// left alone and reported as undeclared so a reader can see how much of the environment the file governs.
/// </remarks>
public sealed class ConfigureApplyService
{
    const string EnvironmentVariablesSection = "EnvironmentVariables";
    const string ConnectionReferencesSection = "ConnectionReferences";

    /// <summary>Applies every component the file declares, in tier order.</summary>
    public async Task<ApplyOutcome> ApplyAsync(
        IOrganizationServiceAsync2 service,
        SettingsDocument document,
        SolutionInventory inventory,
        RunMode mode,
        CancellationToken ct)
    {
        var outcomes = new List<ComponentOutcome>();
        var declaredNames = new List<(ConfigurableComponentKind Kind, string Name)>();

        try
        {
            await ApplyTiersAsync(service, document, inventory, mode, outcomes, declaredNames, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // An interrupted run has already written to a live environment. Returning what happened so far
            // is the only way the operator learns which components those were; throwing here would leave
            // them to find out by re-reading the environment.
            return new ApplyOutcome(outcomes, Undeclared(inventory, declaredNames), Cancelled: true);
        }

        return new ApplyOutcome(outcomes, Undeclared(inventory, declaredNames));
    }

    async Task ApplyTiersAsync(
        IOrganizationServiceAsync2 service,
        SettingsDocument document,
        SolutionInventory inventory,
        RunMode mode,
        List<ComponentOutcome> outcomes,
        List<(ConfigurableComponentKind Kind, string Name)> declaredNames,
        CancellationToken ct)
    {
        // Tier 1 — values and connection references.
        foreach (var declared in ReadValues(document, EnvironmentVariablesSection, "SchemaName", "Value"))
        {
            // Same rule as the connection references below, for the same reason: `pac solution
            // create-settings` writes an empty Value for a variable nobody has filled in, and a pull writes
            // one for a variable with no live value row. Creating an empty value row from that would be a
            // write on a component the file is really saying nothing about, and it would break AE1 — a file
            // pulled and applied straight back is supposed to change nothing.
            if (string.IsNullOrWhiteSpace(declared.Value))
                continue;

            declaredNames.Add((ConfigurableComponentKind.EnvironmentVariable, declared.Name));
            outcomes.Add(await ApplyOneAsync(inventory, ConfigurableComponentKind.EnvironmentVariable,
                declared.Name,
                component => ComponentValueWriter.ApplyEnvironmentVariableAsync(
                    service, component, declared.Value, mode, ct))
                .ConfigureAwait(false));
        }

        foreach (var declared in ReadValues(document, ConnectionReferencesSection, "LogicalName", "ConnectionId"))
        {
            // An empty ConnectionId is what `pac solution create-settings` emits for a reference nobody has
            // filled in yet. Binding to nothing would clear a working binding, so it is left alone.
            if (string.IsNullOrWhiteSpace(declared.Value))
                continue;

            declaredNames.Add((ConfigurableComponentKind.ConnectionReference, declared.Name));
            outcomes.Add(await ApplyOneAsync(inventory, ConfigurableComponentKind.ConnectionReference,
                declared.Name,
                component => ComponentValueWriter.ApplyConnectionReferenceAsync(
                    service, component, declared.Value, mode, ct))
                .ConfigureAwait(false));
        }

        // Tier 2 — cloud flow and classic workflow state.
        foreach (var (kind, entries) in new[]
                 {
                     (ConfigurableComponentKind.CloudFlow, document.CloudFlows),
                     (ConfigurableComponentKind.Workflow, document.Workflows),
                 })
        {
            foreach (var entry in entries)
            {
                declaredNames.Add((kind, entry.Name));
                outcomes.Add(await ApplyOneAsync(inventory, kind, entry.Name,
                    // Suspended is neither on nor off, so the writer needs to be told: a suspended flow the
                    // file declares off is not already off, and has to reach Draft (KTD8).
                    component => ComponentStateWriter.ApplyAsync(service, component, entry.Enabled, mode, ct,
                        currentlySuspended: component.Suspended))
                    .ConfigureAwait(false));
            }
        }

        // Tier 3 — plugin step state.
        foreach (var entry in document.PluginSteps)
        {
            declaredNames.Add((ConfigurableComponentKind.PluginStep, entry.Name));
            outcomes.Add(await ApplyOneAsync(inventory, ConfigurableComponentKind.PluginStep, entry.Name,
                // A plugin step has no third state, so its caller always passes false (KTD7).
                component => ComponentStateWriter.ApplyAsync(service, component, entry.Enabled, mode, ct,
                    currentlySuspended: false))
                .ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Resolves one declared name against the inventory, then applies it — or reports why it could not be.
    /// </summary>
    static async Task<ComponentOutcome> ApplyOneAsync(
        SolutionInventory inventory,
        ConfigurableComponentKind kind,
        string name,
        Func<InventoryComponent, Task<ComponentOutcome>> apply)
    {
        var match = inventory.Match(kind, name);

        if (match.Ambiguous.Count > 0)
            return new ComponentOutcome(kind, name, ComponentOutcomeKind.Skipped,
                // Only one remedy exists, so only one is offered: names are the whole addressing scheme
                // (KTD9) and the file has no qualifier to disambiguate with.
                $"'{name}' matches {match.Ambiguous.Count} components in this solution — rename one in Dataverse, then re-run.");

        if (match.NotFound)
            return new ComponentOutcome(kind, name, ComponentOutcomeKind.Skipped,
                $"'{name}' isn't in this environment's copy of the solution.");

        // No separate preview branch: every writer takes the mode and stops after its own comparison, so
        // a dry run reports the same verdicts a real run produces instead of a second, weaker guess at them.
        return await apply(match.Component!).ConfigureAwait(false);
    }

    /// <summary>Solution components in the covered classes the file does not name (R9).</summary>
    static IReadOnlyList<string> Undeclared(
        SolutionInventory inventory,
        IReadOnlyList<(ConfigurableComponentKind Kind, string Name)> declared)
    {
        var declaredSet = declared
            .Select(d => (d.Kind, Name: d.Name.ToLowerInvariant()))
            .ToHashSet();

        return inventory.Components
            .Where(c => !declaredSet.Contains((c.Kind, c.Name.ToLowerInvariant())))
            .Select(c => $"{c.Kind}: {c.Name}")
            .ToList();
    }

    /// <summary>
    /// Reads a PAC-native section out of the pass-through bag without moving it.
    /// </summary>
    /// <remarks>
    /// Projection, not extraction: PAC owns these sections and a write must return them byte-identical, so
    /// they stay in the bag and are only read from here.
    /// </remarks>
    /// <summary>
    /// Whether applying this document would actually act on one named component (KTD12).
    /// </summary>
    /// <remarks>
    /// The inline surface warns that the next push will override a change it just made, and the honest
    /// question is not whether the file mentions the component but whether a push would touch it. An empty
    /// value is skipped by the tiers above, so a file that names a variable with no value would never
    /// override anything — warning about it would train an operator to ignore the warning.
    ///
    /// Lives here rather than in the command because the answer is this class's own skip rule. The command
    /// project cannot see <see cref="ReadValues"/>, and a second copy of the parse would be free to drift
    /// from the behaviour it is supposed to predict.
    /// </remarks>
    public static bool WouldApply(SettingsDocument document, ConfigurableComponentKind kind, string name)
    {
        bool NamedInValues(string section, string nameProperty, string valueProperty) =>
            ReadValues(document, section, nameProperty, valueProperty)
                .Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(v.Value));

        bool NamedInStates(IEnumerable<ComponentStateEntry> entries) =>
            entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        return kind switch
        {
            ConfigurableComponentKind.EnvironmentVariable =>
                NamedInValues(EnvironmentVariablesSection, "SchemaName", "Value"),
            ConfigurableComponentKind.ConnectionReference =>
                NamedInValues(ConnectionReferencesSection, "LogicalName", "ConnectionId"),
            ConfigurableComponentKind.CloudFlow => NamedInStates(document.CloudFlows),
            ConfigurableComponentKind.Workflow => NamedInStates(document.Workflows),
            ConfigurableComponentKind.PluginStep => NamedInStates(document.PluginSteps),
            _ => false,
        };
    }

    internal static IReadOnlyList<DeclaredValue> ReadValues(
        SettingsDocument document, string section, string nameProperty, string valueProperty)
    {
        if (!document.PassThrough.TryGetValue(section, out var node) || node is not JsonArray array)
            return [];

        var values = new List<DeclaredValue>();

        foreach (var element in array)
        {
            if (element is not JsonObject entry) continue;

            var name = entry[nameProperty]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            values.Add(new DeclaredValue(name, entry[valueProperty]?.GetValue<string>() ?? string.Empty));
        }

        return values;
    }
}
