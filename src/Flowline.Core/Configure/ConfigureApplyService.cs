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
            return new ApplyOutcome(outcomes, Undeclared(inventory, declaredNames), Cancelled: true,
                ReportOnly: mode.IsReportOnly());
        }

        var applied = new ApplyOutcome(outcomes, Undeclared(inventory, declaredNames),
            ReportOnly: mode.IsReportOnly());

        // After the writes, never instead of them: a form whose state changed is invisible until its
        // table is published, and a failure here leaves a change that did happen rather than undoing one.
        if (applied.TablesNeedingPublish.Count == 0) return applied;

        var failures = await CustomizationPublisher
            .PublishTablesAsync(service, applied.TablesNeedingPublish, ct)
            .ConfigureAwait(false);

        return applied with { PublishFailures = failures };
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
        foreach (var declared in ReadValues(document, SettingsSectionEntries.EnvironmentVariables, "SchemaName", "Value"))
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

        foreach (var declared in ReadValues(document, SettingsSectionEntries.ConnectionReferences, "LogicalName", "ConnectionId"))
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

        // Tier 2 — every class whose state the file declares.
        //
        // Activations before deactivations, across the whole tier rather than per section (R20). Some
        // components cannot be switched off while they are the last one of their kind still on, so a
        // release that swaps one for another has to raise the new one before lowering the old. Ordering it
        // here makes that swap declarable: the file says true for the new and false for the old, and one
        // push does them in an order that works. It costs nothing when no such constraint exists.
        var stateTier = new[]
            {
                (Kind: ConfigurableComponentKind.CloudFlow, Entries: document.CloudFlows),
                (Kind: ConfigurableComponentKind.Workflow, Entries: document.Workflows),
                (Kind: ConfigurableComponentKind.BusinessRule, Entries: document.BusinessRules),
                (Kind: ConfigurableComponentKind.BusinessProcessFlow, Entries: document.BusinessProcessFlows),
                (Kind: ConfigurableComponentKind.Action, Entries: document.Actions),
                (Kind: ConfigurableComponentKind.Form, Entries: document.Forms),
                (Kind: ConfigurableComponentKind.View, Entries: document.Views),
                (Kind: ConfigurableComponentKind.PluginStep, Entries: document.PluginSteps),
            }
            .SelectMany(section => section.Entries.Select(entry => (section.Kind, Entry: entry)))
            .OrderByDescending(x => x.Entry.Enabled)
            .ToArray();

        foreach (var (kind, entry) in stateTier)
        {
            declaredNames.Add((kind, entry.Name));
            outcomes.Add(await ApplyOneAsync(inventory, kind, entry.Name,
                // Suspended is neither on nor off, so the writer needs to be told: a suspended flow the
                // file declares off is not already off, and has to reach Draft (KTD8). A plugin step has no
                // third state, and its component reports Suspended as false (KTD7), so one call serves both.
                component => ComponentStateWriter.ApplyAsync(service, component, entry.Enabled, mode, ct,
                    currentlySuspended: component.Suspended))
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

    /// <summary>Components this environment would not get back from a fresh deploy (R9, KTD33).</summary>
    /// <remarks>
    /// Not every component the file omits. A settings file is a partial declaration by design, so most of
    /// a solution being absent from it is the normal state, and a line reporting the normal state on every
    /// run is one an operator learns to skip. That is what this used to do: forty-odd components on a
    /// solution whose file declared seven, identical run after run.
    ///
    /// What is left is the set where absence actually costs something, which differs by what the class
    /// carries.
    ///
    /// A <b>state</b> class counts only when the component is <i>off</i>. Off and undeclared means someone
    /// turned it off here and nothing records that decision, so a deploy into a fresh environment
    /// activates it on import and nobody notices. Undeclared and on needs no line: on is what an import
    /// produces anyway, so the file and the environment already agree.
    ///
    /// A <b>value</b> class counts whenever it is absent, because a value has no such default. Nothing has
    /// said what the variable or the binding should be, so a fresh environment gets neither.
    ///
    /// Both halves are the same question — what would this environment not get back — and the answer is
    /// exactly the set <c>settings pull</c> would add, which is why that is the remedy named.
    ///
    /// Forms and views are excluded outright by <see cref="ConfigurableComponentKinds.IsCaptured"/>
    /// (KTD29).
    /// </remarks>
    static IReadOnlyList<string> Undeclared(
        SolutionInventory inventory,
        IReadOnlyList<(ConfigurableComponentKind Kind, string Name)> declared)
    {
        var declaredSet = declared
            .Select(d => (d.Kind, Name: d.Name.ToLowerInvariant()))
            .ToHashSet();

        return inventory.Components
            .Where(c => ConfigurableComponentKinds.IsCaptured(c.Kind))
            // Covers both halves: a state class that is off, and a value class, whose Enabled is null
            // because it has no state to be on.
            .Where(c => c.Enabled != true)
            .Where(c => !declaredSet.Contains((c.Kind, c.Name.ToLowerInvariant())))
            .Select(c => $"{c.Kind}: {c.Name}")
            .ToList();
    }

    /// <summary>
    /// Whether the next apply of this document would change one named component back (KTD12).
    /// </summary>
    /// <remarks>
    /// The inline surface warns that the next push will undo the change it just made, and the honest
    /// question is not whether the file mentions the component but whether a push would move it. Two
    /// things make it not.
    ///
    /// An empty value is skipped by the tiers above, so a file naming a variable with no value would
    /// override nothing. And a file that already declares what was just written would apply the same
    /// thing again, which changes nothing either. Warning in either case teaches an operator to ignore
    /// the warning, which costs them the one case that matters.
    ///
    /// Lives here rather than in the command because the answer is this class's own apply rule. The
    /// command project cannot see <see cref="ReadValues"/>, and a second copy of the parse would be free
    /// to drift from the behaviour it is supposed to predict.
    /// </remarks>
    /// <param name="writtenEnabled">The state just written, for a state kind; <c>null</c> for a value kind.</param>
    /// <param name="writtenValue">The value just written, for a value kind; <c>null</c> for a state kind.</param>
    public static bool WouldOverride(
        SettingsDocument document,
        ConfigurableComponentKind kind,
        string name,
        bool? writtenEnabled = null,
        string? writtenValue = null)
    {
        bool DisagreesOnValue(string section, string nameProperty, string valueProperty) =>
            ReadValues(document, section, nameProperty, valueProperty)
                .Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(v.Value)
                          && !string.Equals(v.Value, writtenValue, StringComparison.Ordinal));

        bool DisagreesOnState(IEnumerable<ComponentStateEntry> entries) =>
            entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                             && e.Enabled != writtenEnabled);

        return kind switch
        {
            ConfigurableComponentKind.EnvironmentVariable =>
                DisagreesOnValue(SettingsSectionEntries.EnvironmentVariables, "SchemaName", "Value"),
            ConfigurableComponentKind.ConnectionReference =>
                DisagreesOnValue(SettingsSectionEntries.ConnectionReferences, "LogicalName", "ConnectionId"),
            // Every state class, through the document's own map. Listing them here is what went wrong
            // before: five classes were missing and the default arm answered "the file says nothing",
            // so a run that contradicted the file never offered to fix it.
            _ => document.StateSection(kind) is { } section && DisagreesOnState(section),
        };
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
