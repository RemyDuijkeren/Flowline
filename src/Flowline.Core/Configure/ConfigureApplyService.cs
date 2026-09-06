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
            outcomes.Add(await ApplyOneAsync(service, inventory, ConfigurableComponentKind.EnvironmentVariable,
                declared.Name, mode,
                component => ComponentValueWriter.ApplyEnvironmentVariableAsync(service, component, declared.Value, ct))
                .ConfigureAwait(false));
        }

        foreach (var declared in ReadValues(document, ConnectionReferencesSection, "LogicalName", "ConnectionId"))
        {
            // An empty ConnectionId is what `pac solution create-settings` emits for a reference nobody has
            // filled in yet. Binding to nothing would clear a working binding, so it is left alone.
            if (string.IsNullOrWhiteSpace(declared.Value))
                continue;

            declaredNames.Add((ConfigurableComponentKind.ConnectionReference, declared.Name));
            outcomes.Add(await ApplyOneAsync(service, inventory, ConfigurableComponentKind.ConnectionReference,
                declared.Name, mode,
                component => ComponentValueWriter.ApplyConnectionReferenceAsync(
                    service, component, declared.Value, currentConnectionId: null, ct))
                .ConfigureAwait(false));
        }

        // Tier 2 — flow and workflow state.
        foreach (var entry in document.Flows)
        {
            declaredNames.Add((ConfigurableComponentKind.Flow, entry.Name));
            outcomes.Add(await ApplyOneAsync(service, inventory, ConfigurableComponentKind.Flow, entry.Name, mode,
                component => ComponentStateWriter.ApplyAsync(service, component, entry.Enabled, ct))
                .ConfigureAwait(false));
        }

        // Tier 3 — plugin step state.
        foreach (var entry in document.PluginSteps)
        {
            declaredNames.Add((ConfigurableComponentKind.PluginStep, entry.Name));
            outcomes.Add(await ApplyOneAsync(service, inventory, ConfigurableComponentKind.PluginStep, entry.Name, mode,
                component => ComponentStateWriter.ApplyAsync(service, component, entry.Enabled, ct))
                .ConfigureAwait(false));
        }

        return new ApplyOutcome(outcomes, Undeclared(inventory, declaredNames));
    }

    /// <summary>
    /// Resolves one declared name against the inventory, then applies it — or reports why it could not be.
    /// </summary>
    static async Task<ComponentOutcome> ApplyOneAsync(
        IOrganizationServiceAsync2 service,
        SolutionInventory inventory,
        ConfigurableComponentKind kind,
        string name,
        RunMode mode,
        Func<InventoryComponent, Task<ComponentOutcome>> apply)
    {
        var match = inventory.Match(kind, name);

        if (match.Ambiguous.Count > 0)
            return new ComponentOutcome(kind, name, ComponentOutcomeKind.Skipped,
                $"'{name}' matches {match.Ambiguous.Count} components in this solution — rename one, or address it more precisely.");

        if (match.NotFound)
            return new ComponentOutcome(kind, name, ComponentOutcomeKind.Skipped,
                $"'{name}' isn't in this environment's copy of the solution.");

        if (mode.IsReportOnly())
            return await PreviewAsync(match.Component!, apply).ConfigureAwait(false);

        return await apply(match.Component!).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports what a real run would do without writing.
    /// </summary>
    /// <remarks>
    /// Dry-run is decided here rather than inside each writer so there is one place a write can leak from,
    /// and so the reported change set is derived from the same resolution a real run uses.
    /// </remarks>
    static Task<ComponentOutcome> PreviewAsync(
        InventoryComponent component,
        Func<InventoryComponent, Task<ComponentOutcome>> apply)
    {
        _ = apply;
        return Task.FromResult(new ComponentOutcome(component.Kind, component.Name, ComponentOutcomeKind.Applied,
            "would change"));
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
