using Flowline.Core.Configure;
using Flowline.Core.Environments;

namespace Flowline.Core.ConnectionReferences;

/// <summary>What one role's settings file says about connection references.</summary>
/// <param name="Role">The role the file belongs to.</param>
/// <param name="HasFile">Whether <c>deploymentSettings.&lt;role&gt;.json</c> exists. No file is no evidence.</param>
/// <param name="ConnectionIds">Logical name to connection id, case-insensitive. Entries with an empty id are left out, which makes them undeclared.</param>
public sealed record RoleEvidence(EnvironmentRole Role, bool HasFile, IReadOnlyDictionary<string, string> ConnectionIds);

/// <summary>Reads the role settings files that show whether connection references are bound alike in each environment.</summary>
public static class SettingsEvidence
{
    /// <summary>
    /// One entry per role except DEV, from the role files beside the cdsproj.
    /// </summary>
    /// <remarks>
    /// The shared <c>deploymentSettings.json</c> is not read: its connection ids are empty by design, so it
    /// proves nothing. A malformed role file throws the existing config error rather than being skipped.
    /// </remarks>
    public static IReadOnlyList<RoleEvidence> Read(string solutionFolder)
    {
        var result = new List<RoleEvidence>();
        foreach (var role in EnvironmentRoles.All.Where(r => r != EnvironmentRole.Dev))
        {
            var path = Path.Combine(solutionFolder, SettingsFileLocator.FileNameForRole(role.Keyword()));
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
            {
                result.Add(new RoleEvidence(role, false, ids));
                continue;
            }

            var document = SettingsFileReader.Read(path);
            foreach (var entry in SettingsSectionEntries.In(document, SettingsSectionEntries.ConnectionReferences))
            {
                var name = entry["LogicalName"]?.GetValue<string>();
                var id = entry["ConnectionId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) ids[name] = id;
            }

            result.Add(new RoleEvidence(role, true, ids));
        }

        return result;
    }
}
