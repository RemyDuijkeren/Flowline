using System.Xml.Linq;

namespace Flowline.Utils;

public static class NamespaceDeriver
{
    /// <summary>
    /// Derives the model namespace from the Plugins project, using the fallback chain:
    /// (1) &lt;RootNamespace&gt; + ".Models"
    /// (2) &lt;PackageId&gt; + ".Models"
    /// (3) csproj filename without extension + ".Models"
    /// (4) &lt;solutionName&gt; + ".Models" when csproj is absent
    /// </summary>
    public static string Derive(string slnFolder, string solutionName)
    {
        var csprojPath = Path.Combine(slnFolder, "Plugins", "Plugins.csproj");

        if (!File.Exists(csprojPath))
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
}
