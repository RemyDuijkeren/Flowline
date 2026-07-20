using System.Xml.Linq;
using Flowline.Core.Plugins;

namespace Flowline.Utils;

public static class NamespaceDeriver
{
    /// <summary>
    /// Derives the model namespace from the plugin project, using the fallback chain:
    /// (1) &lt;RootNamespace&gt; + ".Models"
    /// (2) &lt;PackageId&gt; + ".Models"
    /// (3) csproj filename without extension + ".Models"
    /// (4) &lt;solutionName&gt; + ".Models" when no plugin project is there
    /// </summary>
    /// <remarks>
    /// Which project supplies the namespace comes from solution-file discovery, not a fixed
    /// <c>Plugins/Plugins.csproj</c> — a project with any other name used to fall straight through to (4)
    /// and silently generate models under the wrong namespace. A solution with several plugin projects
    /// takes the first in path order: generated early-bound models are one shared set, so there is one
    /// namespace to pick, and picking deterministically beats picking by folder name. With no solution
    /// file the conventional project is still the one read, so an unscaffolded repo derives as before.
    /// </remarks>
    public static async Task<string> DeriveAsync(string slnFolder, string solutionName, CancellationToken cancellationToken = default)
    {
        var candidates = await PluginProjectResolver.DiscoverAsync(slnFolder, SkipMissingProject, cancellationToken).ConfigureAwait(false);
        var csprojPath = candidates.Select(c => c.ProjectPath).FirstOrDefault(File.Exists);

        if (csprojPath == null)
            return $"{solutionName}.Models";

        try
        {
            var doc = XDocument.Load(csprojPath);

            // (1) <RootNamespace>
            var rootNs = doc.Descendants("RootNamespace").FirstOrDefault()?.Value?.Trim();
            if (!string.IsNullOrEmpty(rootNs))
                return $"{rootNs}.Models";

            // (2) <PackageId> — set by 'pac plugin init --name'
            var packageId = doc.Descendants("PackageId").FirstOrDefault()?.Value?.Trim();
            if (!string.IsNullOrEmpty(packageId))
                return $"{packageId}.Models";

            // (3) csproj filename without extension
            var name = Path.GetFileNameWithoutExtension(csprojPath);
            return $"{name}.Models";
        }
        catch
        {
            // XML parse failure → fall back to solution name
            return $"{solutionName}.Models";
        }
    }

    /// <summary>Ignore a solution entry whose project is gone, rather than failing this path over it.</summary>
    /// <remarks>
    /// Scoping and advisory work only — none of it builds the project. `push` keeps the throw, so a broken
    /// solution file is still reported loudly by the command that actually cares.
    /// </remarks>
    static void SkipMissingProject(string _) { }

}
