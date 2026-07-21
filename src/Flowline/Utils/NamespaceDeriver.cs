using System.Xml.Linq;
using Flowline.Core.Services;

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
    /// and silently generate models under the wrong namespace. A repo with no solution file has nothing to
    /// discover from and throws (R6) rather than falling back to the conventional project.
    ///
    /// With several candidates, the one that declares a namespace wins over the one that sorts first.
    /// Discovery here is pre-filter-only — <c>generate</c> does not build, so reflection cannot confirm
    /// which candidate is really the plugin project, and the pre-filter deliberately lets a project
    /// through when a <c>Directory.Build.props</c> could be supplying its SDK reference. That means a
    /// plain library can be a candidate, and <c>Common/</c> sorts ahead of <c>Plugins/</c>. Declaring
    /// <c>&lt;RootNamespace&gt;</c> or <c>&lt;PackageId&gt;</c> is the available evidence of intent —
    /// <c>pac plugin init</c> always writes <c>PackageId</c>, and a library usually declares neither —
    /// so it beats alphabetical order, which is evidence of nothing.
    /// </remarks>
    public static async Task<string> DeriveAsync(string slnFolder, string solutionName, CancellationToken cancellationToken = default)
    {
        var csprojPath = await ResolvePrimaryProjectAsync(slnFolder, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// The plugin project the generated models follow — the one declaring a namespace, else the first by
    /// path — or <c>null</c> when the folder has no plugin project on disk.
    /// </summary>
    /// <remarks>
    /// Shared by namespace derivation and <c>generate</c>'s default output folder so both land on the same
    /// project. Deriving the namespace from a relocated <c>src/Plugins/</c> while writing models to a
    /// composed <c>Plugins/Models/</c> would split the two apart — the models would carry the right
    /// namespace and sit in a folder nothing compiles.
    /// </remarks>
    public static async Task<string?> ResolvePrimaryProjectAsync(string slnFolder, CancellationToken cancellationToken = default)
    {
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken).ConfigureAwait(false);
        var present = layout.PluginProjects.Select(c => c.ProjectPath).Where(File.Exists).ToList();
        return present.FirstOrDefault(DeclaresANamespace) ?? present.FirstOrDefault();
    }

    /// <summary>True when the project states a namespace of its own, rather than leaving it to its filename.</summary>
    /// <remarks>
    /// Deliberately reads only the project's own file. Resolving an inherited <c>RootNamespace</c> would
    /// mean walking the <c>Directory.Build.props</c> chain — the MSBuild evaluation discovery exists to
    /// avoid — and an inherited value is shared by every project under it, so it says nothing about which
    /// candidate is the plugin project. A parse failure is not a namespace declaration; the caller's own
    /// try/catch reports it if that project ends up being the one chosen.
    /// </remarks>
    static bool DeclaresANamespace(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            return !string.IsNullOrWhiteSpace(doc.Descendants("RootNamespace").FirstOrDefault()?.Value)
                || !string.IsNullOrWhiteSpace(doc.Descendants("PackageId").FirstOrDefault()?.Value);
        }
        catch
        {
            return false;
        }
    }
}
