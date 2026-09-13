using Flowline.Core.Models;

namespace Flowline.Core.Configure;

/// <summary>
/// Reads and changes what a settings file declares about one component (R21, KTD27).
/// </summary>
/// <remarks>
/// A component command changes the environment and leaves the file saying something else, which the run
/// already warns about. This is what lets it offer to fix that instead of only reporting it.
///
/// Lives beside the reader rather than in the command, because the sections it edits are the same ones
/// the apply path reads, and a second idea of where a declaration lives would let the two disagree about
/// what a file says.
/// </remarks>
public static class SettingsFileDeclaration
{
    /// <summary>What the file says this component's state should be, or <c>null</c> if it is silent.</summary>
    public static bool? DeclaredState(SettingsDocument document, ConfigurableComponentKind kind, string name) =>
        SectionFor(document, kind)?
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?
            .Enabled;

    /// <summary>What the file says this component's value should be, or <c>null</c> if it is silent.</summary>
    public static string? DeclaredValue(SettingsDocument document, ConfigurableComponentKind kind, string name) =>
        ValueSectionFor(kind) is { } section
            ? ConfigureApplyService.ReadValues(document, section.Section, section.NameProperty, section.ValueProperty)
                .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
            : null;

    /// <summary>
    /// Makes the file declare this state, adding the entry when it is not there.
    /// </summary>
    /// <remarks>
    /// A declared <c>true</c> is not redundant with leaving the component out, which is why this is offered
    /// for a component the file does not yet name. Absence means the apply ignores it; <c>true</c> means
    /// every apply asserts it. A solution import turns flows off and on again and does not restore the
    /// state of one that already existed, so "make sure this is on" is a thing a file needs to be able to
    /// say.
    /// </remarks>
    public static bool DeclareState(SettingsDocument document, ConfigurableComponentKind kind, string name, bool enabled)
    {
        if (SectionFor(document, kind) is not { } section) return false;

        for (var i = 0; i < section.Count; i++)
        {
            if (!string.Equals(section[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            // In place, so an entry keeps the position it has in a file someone reads as a diff (R12c).
            section[i] = section[i] with { Enabled = enabled };
            return true;
        }

        section.Add(new ComponentStateEntry(name, enabled));
        return true;
    }

    /// <summary>Stops the file declaring anything about this component's state.</summary>
    /// <remarks>
    /// Removing is not the same as declaring the opposite: it hands the component back to whatever the
    /// environment and the next deploy make of it.
    /// </remarks>
    public static bool RemoveState(SettingsDocument document, ConfigurableComponentKind kind, string name)
    {
        if (SectionFor(document, kind) is not { } section) return false;

        var found = section.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        return found is not null && section.Remove(found);
    }

    /// <summary>Makes the file declare this value, adding the entry when it is not there.</summary>
    public static bool DeclareValue(
        SettingsDocument document, ConfigurableComponentKind kind, string name, string value) =>
        SettingsFileGaps.Fill(document, new SettingsGap(GapKindFor(kind), name), value);

    /// <summary>The typed state section for a class, or <c>null</c> when the file has none.</summary>
    static IList<ComponentStateEntry>? SectionFor(SettingsDocument document, ConfigurableComponentKind kind) =>
        document.StateSection(kind);

    static (string Section, string NameProperty, string ValueProperty)? ValueSectionFor(ConfigurableComponentKind kind) =>
        kind switch
        {
            ConfigurableComponentKind.EnvironmentVariable =>
                (SettingsSectionEntries.EnvironmentVariables, "SchemaName", "Value"),
            ConfigurableComponentKind.ConnectionReference =>
                (SettingsSectionEntries.ConnectionReferences, "LogicalName", "ConnectionId"),
            _ => null,
        };

    static SettingsGapKind GapKindFor(ConfigurableComponentKind kind) =>
        kind == ConfigurableComponentKind.ConnectionReference
            ? SettingsGapKind.ConnectionReference
            : SettingsGapKind.EnvironmentVariable;
}
