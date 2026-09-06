namespace Flowline.Core.Configure;

/// <summary>Where a settings file was found, and by which rule (R4a).</summary>
/// <param name="Path">Absolute path to the file, whether or not it exists yet.</param>
/// <param name="Source">How the path was chosen — reported before the first write so a run says which file it picked.</param>
/// <param name="Exists">Whether the file is on disk.</param>
public sealed record SettingsFileLocation(string Path, SettingsFileSource Source, bool Exists);

/// <summary>How a settings-file path was chosen.</summary>
public enum SettingsFileSource
{
    /// <summary>The caller passed <c>--settings-file</c>.</summary>
    Explicit,

    /// <summary>The role-named convention file, <c>deploymentSettings.&lt;role&gt;.json</c>.</summary>
    RoleConvention,

    /// <summary>The un-suffixed fallback, <c>deploymentSettings.json</c>.</summary>
    SharedFallback,
}

/// <summary>Resolves which settings file a run reads or writes (R4a).</summary>
/// <remarks>
/// Settings files live beside the <c>.cdsproj</c> because the file is solution-scoped data and that keeps it
/// working for a repo holding more than one solution. PAC's filename stem is kept so a copy of the file is
/// still recognisable as a deployment settings file wherever it lands.
///
/// No Microsoft convention exists for this location in a <c>.cdsproj</c> repo — ALM Accelerator's <c>config/</c>
/// folder is the only precedent and it is deprecated — so this is Flowline's.
/// </remarks>
public static class SettingsFileLocator
{
    const string Stem = "deploymentSettings";

    /// <summary>Filename for a role-named settings file.</summary>
    public static string FileNameForRole(string role) => $"{Stem}.{role.ToLowerInvariant()}.json";

    /// <summary>Filename for the shared, un-suffixed settings file.</summary>
    public static string SharedFileName => $"{Stem}.json";

    /// <summary>
    /// Resolves the settings file for a run.
    /// </summary>
    /// <param name="solutionFolder">The folder holding the <c>.cdsproj</c>, from <c>SolutionFileLayout</c>.</param>
    /// <param name="role">The environment role, or <c>null</c> when the target was a URL.</param>
    /// <param name="explicitPath">A path the caller supplied, which overrides discovery.</param>
    /// <remarks>
    /// A URL target has no role name to build a filename from, so it can only reach the shared fallback or an
    /// explicit path. The role-named file wins when it exists; otherwise the shared file is used, which is
    /// safe for state declarations but carries environment-local values into the wrong environment if it
    /// declares connection references or most environment variable values.
    /// </remarks>
    public static SettingsFileLocation Locate(string solutionFolder, string? role, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var resolved = Path.GetFullPath(explicitPath);
            return new SettingsFileLocation(resolved, SettingsFileSource.Explicit, File.Exists(resolved));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var roleFile = Path.Combine(solutionFolder, FileNameForRole(role));
            if (File.Exists(roleFile))
                return new SettingsFileLocation(roleFile, SettingsFileSource.RoleConvention, true);
        }

        var shared = Path.Combine(solutionFolder, SharedFileName);
        if (File.Exists(shared))
            return new SettingsFileLocation(shared, SettingsFileSource.SharedFallback, true);

        // Nothing on disk yet. A pull needs somewhere to write, and the role-named file is the right target
        // when a role is known — a first pull should not create the shared file and quietly make one
        // environment's values look like every environment's.
        return string.IsNullOrWhiteSpace(role)
            ? new SettingsFileLocation(shared, SettingsFileSource.SharedFallback, false)
            : new SettingsFileLocation(Path.Combine(solutionFolder, FileNameForRole(role)), SettingsFileSource.RoleConvention, false);
    }
}
