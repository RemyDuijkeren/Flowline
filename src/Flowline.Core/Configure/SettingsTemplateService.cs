namespace Flowline.Core.Configure;

/// <summary>What a template refresh produced, and what the run should say about it.</summary>
/// <param name="Document">The merged document, ready to write.</param>
/// <param name="Added">Keys that appeared since the existing template was written (R17).</param>
/// <param name="Vanished">
/// Entries the existing template declares whose component the solution no longer holds. Reported, never
/// dropped — a key that left this solution's declaration may still matter to whoever reads the file, and
/// silently deleting a line someone filled in is worse than carrying one that no longer resolves.
/// </param>
public sealed record TemplateMergeResult(
    SettingsDocument Document,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Vanished);

/// <summary>
/// Builds the shared, un-suffixed settings template from the unpacked solution alone (R17, KTD14).
/// </summary>
/// <remarks>
/// This is <see cref="ConfigurePullService"/>'s merge with the live-state input removed: same skeleton, same
/// "existing file wins" rule, but no service, no inventory and no connection — the key list comes entirely
/// from the unpacked solution `pac solution create-settings` already read, and every value stays empty
/// unless the existing file already filled it in. That is what makes a shared file any environment can fall
/// back to safe to write automatically (KTD14): the apply path skips an empty value for both value classes,
/// so an unfilled key is a no-op wherever it is applied.
///
/// The three Flowline-owned state sections never enter the merged document — this only ever touches the
/// EnvironmentVariables and ConnectionReferences entries in <see cref="SettingsDocument.PassThrough"/>, and
/// the new document's typed CloudFlows/Workflows/PluginSteps lists stay at their empty default, so
/// <see cref="SettingsFileReader.Write"/> omits them. A flow's state is a boolean with no inert form, so
/// listing one at all would declare it, and DEV is exactly where flows are switched off. Every other
/// pass-through section — including one PAC adds later, such as CopilotAgents — is carried through
/// untouched, same as the capture path.
/// </remarks>
public static class SettingsTemplateService
{
    const string EnvironmentVariablesSection = "EnvironmentVariables";
    const string ConnectionReferencesSection = "ConnectionReferences";

    /// <summary>Merges the PAC skeleton into the existing shared template, with no live state.</summary>
    public static TemplateMergeResult Merge(SettingsDocument skeleton, SettingsDocument? existing)
    {
        var added = new List<string>();
        var vanished = new List<string>();

        var document = new SettingsDocument
        {
            // Inherited like the capture path (R12c): a re-run against an unchanged solution should not
            // rewrite every line of a file someone already committed.
            NewLine = existing?.NewLine ?? skeleton.NewLine,
        };

        foreach (var section in skeleton.PassThrough)
            document.PassThrough[section.Key] = section.Value?.DeepClone();

        FillSection(document, existing, EnvironmentVariablesSection, "SchemaName", "Value", added);
        FillSection(document, existing, ConnectionReferencesSection, "LogicalName", "ConnectionId", added);

        SettingsSectionEntries.CarryVanished(document, existing, EnvironmentVariablesSection, "SchemaName", vanished);
        SettingsSectionEntries.CarryVanished(document, existing, ConnectionReferencesSection, "LogicalName", vanished);

        return new TemplateMergeResult(document, added, vanished);
    }

    static void FillSection(
        SettingsDocument document, SettingsDocument? existing, string section, string nameProperty, string valueProperty,
        List<string> added)
    {
        foreach (var entry in SettingsSectionEntries.In(document, section))
        {
            var name = entry[nameProperty]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var declared = SettingsSectionEntries.ExistingValue(existing, section, nameProperty, name, valueProperty);
            if (!string.IsNullOrWhiteSpace(declared))
            {
                entry[valueProperty] = declared;
                continue;
            }

            entry[valueProperty] = string.Empty;

            if (!SettingsSectionEntries.Declares(existing, section, nameProperty, name))
                added.Add($"{section}: {name}");
        }
    }
}
