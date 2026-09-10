using System.Text.Json.Nodes;

namespace Flowline.Core.Configure;

/// <summary>
/// Reading and carrying entries in the PAC-owned sections of a settings file.
/// </summary>
/// <remarks>
/// PAC's sections are a pass-through bag rather than a typed model (<see cref="SettingsDocument"/>), so
/// both the paths that build a settings file — the capture in <see cref="ConfigurePullService"/> and the
/// template refresh in <see cref="SettingsTemplateService"/> — have to reach into the same JSON shape by
/// property name.
///
/// One copy, because the two must agree. They ask the same questions of the same document about the same
/// file, and a second copy of "does this section already declare this name" would be free to answer
/// differently — which would show up as a capture and a refresh disagreeing about what is new.
/// </remarks>
internal static class SettingsSectionEntries
{
    /// <summary>Every object entry in one pass-through section, or nothing when the section is absent.</summary>
    public static IEnumerable<JsonObject> In(SettingsDocument document, string section) =>
        document.PassThrough.TryGetValue(section, out var node) && node is JsonArray array
            ? array.OfType<JsonObject>()
            : [];

    /// <summary>Whether a document already names this entry in this section.</summary>
    public static bool Declares(SettingsDocument? existing, string section, string nameProperty, string name) =>
        existing is not null && In(existing, section)
            .Any(e => string.Equals(e[nameProperty]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value a document already carries for this entry, or <c>null</c>.</summary>
    public static string? ExistingValue(
        SettingsDocument? existing, string section, string nameProperty, string name, string valueProperty)
    {
        if (existing is null) return null;

        return In(existing, section)
            .Where(e => string.Equals(e[nameProperty]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e[valueProperty]?.GetValue<string>())
            .FirstOrDefault();
    }

    /// <summary>
    /// Appends entries the existing file declares that the regenerated skeleton no longer has, and names
    /// them.
    /// </summary>
    /// <remarks>
    /// Reported, never dropped: an entry whose component left the solution may still matter to whoever
    /// filled it in, and silently deleting their line is worse than carrying one that no longer resolves.
    ///
    /// Appended rather than merged in place, so the skeleton's own order — what a diff against the previous
    /// write compares to — is untouched.
    /// </remarks>
    public static void CarryVanished(
        SettingsDocument document, SettingsDocument? existing, string section, string nameProperty,
        List<string> vanished)
    {
        if (existing is null) return;

        var present = In(document, section)
            .Select(e => e[nameProperty]?.GetValue<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!document.PassThrough.TryGetValue(section, out var node) || node is not JsonArray target) return;

        foreach (var entry in In(existing, section))
        {
            var name = entry[nameProperty]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || present.Contains(name)) continue;

            target.Add(entry.DeepClone());
            vanished.Add($"{section}: {name}");
        }
    }
}
