namespace Flowline.Core.Configure;

/// <summary>What a merge changed, so the caller can report it without re-deriving the diff.</summary>
public sealed record StateMergeResult(
    IReadOnlyList<ComponentStateEntry> Merged,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Vanished);

/// <summary>Merges live component state into an existing settings file (R12b).</summary>
/// <remarks>
/// A pull merges rather than regenerating. `pac solution create-settings` emits a blank skeleton, so
/// regenerating would wipe every value the team filled in — the well-known annoyance with that command, and
/// the reason R12b exists.
///
/// The file wins on any component it already declares. It states intent; the environment states fact, and a
/// merge that took the fact would erase the intent the file was written to hold.
/// </remarks>
public static class SettingsFileMerger
{
    /// <summary>
    /// Merges a live component set into the entries a settings file already declares.
    /// </summary>
    /// <param name="existing">Entries the file already carries, in file order.</param>
    /// <param name="live">Components found in the target environment.</param>
    /// <remarks>
    /// Existing entries keep their position and their declared value; components the file does not name are
    /// appended in the order the live set supplied them. An entry whose component is no longer in the
    /// solution is kept and reported — dropping it would silently discard a declaration the author may still
    /// want, for a component that could simply be absent from this one environment.
    /// </remarks>
    public static StateMergeResult MergeStates(
        IEnumerable<ComponentStateEntry> existing,
        IEnumerable<ComponentStateEntry> live)
    {
        var existingList = existing.ToList();
        var liveNames = live.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredNames = existingList.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new List<ComponentStateEntry>(existingList);
        var added = new List<string>();

        foreach (var candidate in live.Where(c => !declaredNames.Contains(c.Name)))
        {
            merged.Add(candidate);
            added.Add(candidate.Name);
        }

        var vanished = existingList
            .Where(e => !liveNames.Contains(e.Name))
            .Select(e => e.Name)
            .ToList();

        return new StateMergeResult(merged, added, vanished);
    }
}
