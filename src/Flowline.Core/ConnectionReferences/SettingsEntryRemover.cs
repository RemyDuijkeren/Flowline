using Flowline.Core.Configure;

namespace Flowline.Core.ConnectionReferences;

/// <summary>One settings file that lost entries, and which ones.</summary>
public sealed record SettingsEntryRemoval(string Path, IReadOnlyList<string> Removed);

/// <summary>
/// Removes connection reference entries from the role settings files and the shared template beside the
/// cdsproj, leaving every other byte of each file as it was.
/// </summary>
/// <remarks>
/// Runs before the shared template refresh. The refresh carries entries the template declares but the
/// regenerated skeleton lacks, so an entry left in the template would come back after the reference is gone.
/// </remarks>
public static class SettingsEntryRemover
{
    /// <summary>Removes the named connection references, matched case-insensitively, from every settings file in the folder.</summary>
    /// <returns>Only the files that changed. A file declaring none of the names is not rewritten.</returns>
    /// <exception cref="FlowlineException"><see cref="ExitCode.ConfigInvalid"/> when a settings file is malformed.</exception>
    public static IReadOnlyList<SettingsEntryRemoval> Remove(string settingsFolder, IEnumerable<string> logicalNames)
    {
        var names = logicalNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new List<SettingsEntryRemoval>();
        if (names.Count == 0 || !Directory.Exists(settingsFolder)) return changed;

        var stem = Path.GetFileNameWithoutExtension(SettingsFileLocator.SharedFileName);
        var files = Directory.EnumerateFiles(settingsFolder, SettingsFileLocator.SharedFileName)
            .Concat(Directory.EnumerateFiles(settingsFolder, $"{stem}.*.json"))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var document = SettingsFileReader.Read(file);
            var removed = SettingsSectionEntries.Remove(
                document, SettingsSectionEntries.ConnectionReferences, "LogicalName", names);
            if (removed.Count == 0) continue;

            SettingsFileReader.Save(document, file);
            changed.Add(new SettingsEntryRemoval(file, removed));
        }

        return changed;
    }
}
