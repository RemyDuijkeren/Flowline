using System.Text.Json.Nodes;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Configure;

/// <summary>What a pull produced, and what the run should say about it.</summary>
/// <param name="Document">The merged document, ready to write.</param>
/// <param name="Added">Components that appeared since the existing file was written (R12b).</param>
/// <param name="Vanished">
/// Entries the existing file declares whose component the solution no longer holds. Reported, never dropped
/// (R12b) — a name that disappeared from one environment may still matter in another, and silently deleting
/// a line someone wrote is worse than carrying one that no longer resolves.
/// </param>
/// <param name="Placeholders">
/// Secret-type variables whose value was deliberately not read (R13), each named so the run can say which
/// ones still need filling in by hand.
/// </param>
public sealed record PullResult(
    SettingsDocument Document,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Vanished,
    IReadOnlyList<string> Placeholders);

/// <summary>Builds a settings file from a live environment (R12, R12a, R12b, R12c, R13).</summary>
/// <remarks>
/// <b>Three inputs, each with one job.</b> The <i>skeleton</i> is what `pac solution create-settings` wrote:
/// it owns the shape of the PAC-native sections, including fields Flowline does not model such as
/// <c>ConnectorId</c>, and it is why a section Microsoft adds later appears without a Flowline change (R12a).
/// The <i>existing</i> file, when there is one, owns every value a human already put there. The <i>inventory</i>
/// owns what the environment actually holds right now.
///
/// <b>An existing value wins over the live one.</b> R12 asks a pull to write live values and R12b asks it to
/// preserve what the file already has; the two only conflict for an entry that has both, and the file wins.
/// A pull fills in blanks rather than overwriting decisions — someone who pinned a value in the file meant it,
/// and the alternative silently reverts it on the next pull. It also makes a second pull byte-identical to the
/// first (R12c) and keeps AE1 honest.
///
/// <b>No `pac` here.</b> This runs on the SDK and two parsed documents, so the whole merge is testable without
/// a `pac` on the path or a live environment. The command invokes `pac`, hands the result over, and writes what
/// comes back.
/// </remarks>
public sealed class ConfigurePullService
{

    /// <summary><c>environmentvariabledefinition.type</c> for a Secret.</summary>
    public const int SecretType = 100000005;

    /// <summary><c>environmentvariabledefinition.secretstore</c> for Azure Key Vault.</summary>
    public const int KeyVaultSecretStore = 0;

    /// <summary>Written in place of a secret this command refuses to read.</summary>
    /// <remarks>
    /// Deliberately not a plausible value: it has to fail loudly if it ever reaches an environment, rather
    /// than binding something to an empty string that looks configured.
    /// </remarks>
    public const string SecretPlaceholder = "<set-this-secret>";

    /// <summary>Merges the PAC skeleton, the existing file, and the live environment into one document.</summary>
    public async Task<PullResult> BuildAsync(
        IOrganizationServiceAsync2 service,
        SettingsDocument skeleton,
        SettingsDocument? existing,
        SolutionInventory inventory,
        CancellationToken ct)
    {
        var added = new List<string>();
        var vanished = new List<string>();
        var placeholders = new List<string>();

        var document = new SettingsDocument
        {
            // The existing file's line endings survive a pull, so re-pulling a committed file does not
            // rewrite every line of it (R12c).
            NewLine = existing?.NewLine ?? skeleton.NewLine,
        };

        foreach (var section in skeleton.PassThrough)
        {
            document.PassThrough[section.Key] = section.Value?.DeepClone();
        }

        await FillEnvironmentVariablesAsync(service, document, existing, inventory, added, placeholders, ct)
            .ConfigureAwait(false);

        FillConnectionReferences(document, existing, inventory, added);

        SettingsSectionEntries.CarryVanished(document, existing, SettingsSectionEntries.EnvironmentVariables, "SchemaName", vanished);
        SettingsSectionEntries.CarryVanished(document, existing, SettingsSectionEntries.ConnectionReferences, "LogicalName", vanished);

        WriteStateSections(document, existing, inventory, added, vanished);

        return new PullResult(document, added, vanished, placeholders);
    }

    async Task FillEnvironmentVariablesAsync(
        IOrganizationServiceAsync2 service,
        SettingsDocument document,
        SettingsDocument? existing,
        SolutionInventory inventory,
        List<string> added,
        List<string> placeholders,
        CancellationToken ct)
    {
        foreach (var entry in SettingsSectionEntries.In(document, SettingsSectionEntries.EnvironmentVariables))
        {
            var name = entry["SchemaName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var declared = SettingsSectionEntries.ExistingValue(existing, SettingsSectionEntries.EnvironmentVariables, "SchemaName", name, "Value");
            if (!string.IsNullOrWhiteSpace(declared))
            {
                entry["Value"] = declared;
                continue;
            }

            if (!SettingsSectionEntries.Declares(existing, SettingsSectionEntries.EnvironmentVariables, "SchemaName", name))
                added.Add($"EnvironmentVariable: {name}");

            var match = inventory.Match(ConfigurableComponentKind.EnvironmentVariable, name);
            if (match.Component is null)
            {
                entry["Value"] = string.Empty;
                continue;
            }

            if (IsUnreadableSecret(match.Component))
            {
                // R13 fails closed: only a Key Vault-backed Secret stores a reference rather than the secret
                // itself, so every other store — Microsoft Dataverse, and anything Microsoft adds later —
                // is refused rather than read. The value row is never queried, so there is no window where
                // the secret is in memory at all.
                entry["Value"] = SecretPlaceholder;
                placeholders.Add(name);
                continue;
            }

            var live = await ComponentValueWriter.FindValueRowAsync(service, match.Component.Id, ct).ConfigureAwait(false);
            entry["Value"] = live?.GetAttributeValue<string>("value") ?? string.Empty;
        }
    }

    /// <summary>
    /// True for a Secret whose store is anything but Key Vault, including a store this build does not know.
    /// </summary>
    /// <remarks>
    /// The unknown case is the reason this reads as an allow-list rather than a deny-list: a store Microsoft
    /// adds after this ships must be refused by default, not read because it failed to match "Dataverse".
    /// </remarks>
    internal static bool IsUnreadableSecret(InventoryComponent component) =>
        component.Type == SecretType && component.SecretStore != KeyVaultSecretStore;

    void FillConnectionReferences(
        SettingsDocument document,
        SettingsDocument? existing,
        SolutionInventory inventory,
        List<string> added)
    {
        foreach (var entry in SettingsSectionEntries.In(document, SettingsSectionEntries.ConnectionReferences))
        {
            var name = entry["LogicalName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var declared = SettingsSectionEntries.ExistingValue(existing, SettingsSectionEntries.ConnectionReferences, "LogicalName", name, "ConnectionId");
            if (!string.IsNullOrWhiteSpace(declared))
            {
                entry["ConnectionId"] = declared;
                continue;
            }

            if (!SettingsSectionEntries.Declares(existing, SettingsSectionEntries.ConnectionReferences, "LogicalName", name))
                added.Add($"ConnectionReference: {name}");

            var match = inventory.Match(ConfigurableComponentKind.ConnectionReference, name);
            entry["ConnectionId"] = match.Component?.CurrentValue ?? string.Empty;
        }
    }

    /// <summary>
    /// Writes the state classes, listing only the components that are currently off (R12).
    /// </summary>
    /// <remarks>
    /// Absence means untouched, and a deploy already activates what it imports (`--activate-plugins`), so an
    /// entry saying a component is on describes what would have happened anyway. Listing the whole inventory
    /// would bury the handful of deliberate exceptions — which is the only thing this section is for — in a
    /// list that grows with the solution.
    ///
    /// A component the existing file already declares keeps its declared value even when it is currently on:
    /// someone wrote that line on purpose, and a pull that deleted every `"Enabled": true` would erase the
    /// record of a deliberate re-enable.
    /// </remarks>
    static void WriteStateSections(
        SettingsDocument document,
        SettingsDocument? existing,
        SolutionInventory inventory,
        List<string> added,
        List<string> vanished)
    {
        AppendState(document.CloudFlows, existing?.CloudFlows, inventory,
            ConfigurableComponentKind.CloudFlow, added, vanished);
        AppendState(document.Workflows, existing?.Workflows, inventory,
            ConfigurableComponentKind.Workflow, added, vanished);
        AppendState(document.PluginSteps, existing?.PluginSteps, inventory,
            ConfigurableComponentKind.PluginStep, added, vanished);
    }

    /// <summary>
    /// Merges one state section: adds the components that are off, reports the entries that no longer resolve.
    /// </summary>
    /// <remarks>
    /// The merge rule lives in <see cref="SettingsFileMerger"/> so all four classes share one, rather than the
    /// state classes keeping a second copy that can drift from it.
    ///
    /// The two sets it is given differ on purpose. The candidates to add are the components that are off; the
    /// presence set is every component of the class, so a flow someone switched back on is not mistaken for
    /// one that left the solution.
    /// </remarks>
    static void AppendState(
        IList<ComponentStateEntry> into,
        IList<ComponentStateEntry>? existing,
        SolutionInventory inventory,
        ConfigurableComponentKind kind,
        List<string> added,
        List<string> vanished)
    {
        var present = inventory.OfKind(kind).ToList();

        var result = SettingsFileMerger.MergeStates(
            existing ?? [],
            present.Where(c => c.Enabled == false).Select(c => new ComponentStateEntry(c.Name, false)),
            present.Select(c => c.Name));

        // Declared entries keep their position, so a pull does not reshuffle a section someone reads in a
        // diff (R12c); MergeStates preserves that order and appends the rest.
        foreach (var entry in result.Merged)
            into.Add(entry);

        foreach (var name in result.Added)
            added.Add($"{kind}: {name}");

        foreach (var name in result.Vanished)
            vanished.Add($"{kind}: {name}");
    }
}
