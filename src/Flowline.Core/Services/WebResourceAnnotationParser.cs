namespace Flowline.Core.Services;

public static class WebResourceAnnotationParser
{
    const string DependsPrefix = "// flowline:depends ";

    /// <summary>
    /// Reads raw flowline:depends annotation lines from the top of a JS file.
    /// Stops at the first non-comment, non-blank line.
    /// </summary>
    public static IReadOnlyList<string> ParseAnnotations(string filePath)
    {
        List<string>? result = null;
        HashSet<string>? seen = null;
        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.StartsWith(DependsPrefix))
            {
                var name = trimmed[DependsPrefix.Length..].Trim();
                if (!string.IsNullOrEmpty(name) &&
                    (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name))
                    (result ??= []).Add(name);
            }
            else if (trimmed.StartsWith("//"))
                continue;
            else
                break;
        }
        return result?.AsReadOnly() ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Collects all raw flowline:depends references across JS files in a directory.
    /// Used by OrphanCleanupService to determine exemption set.
    /// </summary>
    public static IReadOnlySet<string> CollectAllReferences(string webresourceRoot)
    {
        if (!Directory.Exists(webresourceRoot))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(webresourceRoot, "*.js", SearchOption.AllDirectories))
        {
            foreach (var name in ParseAnnotations(file))
                result.Add(name);
        }
        return result;
    }
}
