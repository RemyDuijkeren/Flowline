namespace Flowline.Core.Configure;

/// <summary>Which PAC-owned section a gap sits in.</summary>
public enum SettingsGapKind
{
    /// <summary>An environment variable declared with no value.</summary>
    EnvironmentVariable,

    /// <summary>A connection reference declared with no connection bound.</summary>
    ConnectionReference,
}

/// <summary>One entry a settings file declares but leaves empty (R19).</summary>
/// <param name="Kind">Which section it came from.</param>
/// <param name="Name">Its schema name, or logical name for a connection reference.</param>
/// <param name="ConnectorId">
/// For a connection reference, the connector it is for, which is what narrows a list of the environment's
/// connections to the ones that could bind. <c>null</c> for an environment variable, and for a reference
/// whose entry carries no connector.
/// </param>
public sealed record SettingsGap(SettingsGapKind Kind, string Name, string? ConnectorId = null);

/// <summary>
/// Finds and fills the entries a settings file declares without a value (R19, KTD24).
/// </summary>
/// <remarks>
/// <c>pac solution create-settings</c> emits a skeleton with every value blank, and a capture leaves an
/// entry blank when the environment has nothing to read. Those blanks are the whole reason a settings file
/// needs hand-editing, and they are what a guided fill walks.
///
/// A blank is not a defect. The apply path skips an empty declared value on purpose, so a file full of
/// blanks is inert rather than destructive. This only finds what someone could usefully be asked about.
///
/// Lives in Core beside the reader because it reaches into the same pass-through JSON the PAC sections
/// are held in, and a second copy of that shape in the command project would be free to drift from the
/// one the apply path reads.
/// </remarks>
public static class SettingsFileGaps
{
    /// <summary>Every declared entry with nothing filled in, in file order.</summary>
    /// <remarks>
    /// A secret placeholder is deliberately not a gap. The capture writes one for a variable whose value
    /// Flowline will not read, and prompting for it would take a secret typed at a terminal and put it in
    /// a file the team commits. That entry keeps the warning the capture already prints.
    /// </remarks>
    public static IReadOnlyList<SettingsGap> Find(SettingsDocument document)
    {
        var gaps = new List<SettingsGap>();

        foreach (var entry in SettingsSectionEntries.In(document, SettingsSectionEntries.EnvironmentVariables))
        {
            var name = entry["SchemaName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var value = entry["Value"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value)) continue;

            gaps.Add(new SettingsGap(SettingsGapKind.EnvironmentVariable, name));
        }

        foreach (var entry in SettingsSectionEntries.In(document, SettingsSectionEntries.ConnectionReferences))
        {
            var name = entry["LogicalName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var value = entry["ConnectionId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value)) continue;

            gaps.Add(new SettingsGap(
                SettingsGapKind.ConnectionReference, name, entry["ConnectorId"]?.GetValue<string>()));
        }

        return gaps;
    }

    /// <summary>Writes an answer into the entry it belongs to.</summary>
    /// <remarks>
    /// Sets the value in place rather than rebuilding the section, so PAC's own property order and every
    /// property Flowline does not know about survive untouched — the same pass-through contract the rest
    /// of the file honours (R12a).
    /// </remarks>
    /// <returns><c>true</c> when the entry was found and set.</returns>
    public static bool Fill(SettingsDocument document, SettingsGap gap, string value)
    {
        var (section, nameProperty, valueProperty) = gap.Kind == SettingsGapKind.EnvironmentVariable
            ? (SettingsSectionEntries.EnvironmentVariables, "SchemaName", "Value")
            : (SettingsSectionEntries.ConnectionReferences, "LogicalName", "ConnectionId");

        foreach (var entry in SettingsSectionEntries.In(document, section))
        {
            if (!string.Equals(entry[nameProperty]?.GetValue<string>(), gap.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            entry[valueProperty] = value;
            return true;
        }

        return false;
    }
}
