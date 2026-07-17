using System.Text.RegularExpressions;

namespace Flowline.Core.Services.WebResources;

public static class WebResourceAnnotationParser
{
    // Matches "// flowline:depends x", "//! flowline:depends x" (the "!" is the industry-standard
    // "legal comment" marker Terser/esbuild/SWC preserve by default when minifying), and the
    // single-line block form "/*! flowline:depends x */".
    static readonly Regex AnnotationRegex = new(
        @"^(?://!?|/\*!)\s*flowline:depends\s+(?<name>.+?)\s*(?:\*/)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads flowline:depends annotation lines from anywhere in a JS file — not just the leading
    /// comment block, since a bundler-injected banner (e.g. Rollup's "banner" option, often a
    /// "/**" block comment) can precede the annotation without being a "//" line comment itself,
    /// which would otherwise stop a leading-block-only scan before it ever reaches the annotation.
    /// </summary>
    public static IReadOnlyList<string> ParseAnnotations(string filePath)
    {
        List<string>? result = null;
        HashSet<string>? seen = null;
        foreach (var line in File.ReadLines(filePath))
        {
            var match = AnnotationRegex.Match(line.Trim());
            if (!match.Success) continue;

            var name = match.Groups["name"].Value;
            if (!string.IsNullOrEmpty(name) &&
                (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name))
                (result ??= []).Add(name);
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
