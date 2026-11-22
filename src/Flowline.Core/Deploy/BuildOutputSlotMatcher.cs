using System.Xml;
using System.Xml.Linq;
using Flowline.Core.Plugins;
using Flowline.Core.WebResources;

namespace Flowline.Core.Deploy;

public enum SlotKind { PluginAssembly, PluginPackage, WebResource }

/// <summary>A binary's place in an unpacked solution; <paramref name="BinaryRelativePath"/> is relative to the solution src folder, '/' separated.</summary>
public sealed record SolutionSlot(SlotKind Kind, string Key, string BinaryRelativePath);

/// <summary>A built plugin assembly (.dll) or package (.nupkg); <paramref name="AssemblyName"/> is the simple assembly name.</summary>
public sealed record PluginBuildOutput(string AssemblyName, string FilePath, bool IsPackage);

/// <summary>A built web resource file; <paramref name="RelativePath"/> is relative to the WebResources dist folder, '/' separated.</summary>
public sealed record WebResourceBuildOutput(string RelativePath, string FilePath);

public sealed record SlotCopy(string SourcePath, string SlotRelativePath);

public sealed record SlotMatchResult(
    IReadOnlyList<SlotCopy> Copies,
    IReadOnlyList<string> UnmatchedOutputs,
    IReadOnlyList<SolutionSlot> SlotsWithoutOutput)
{
    /// <summary>Two build outputs that map to one slot, each as "first and second (slot key)"; only the first is copied.</summary>
    public IReadOnlyList<string> DuplicateOutputs { get; init; } = [];
}

/// <summary>
/// Maps build outputs to the binary slots of an unpacked solution. Slots are found from their metadata
/// files, never from an existing binary, because a solution that gitignores binaries only holds metadata.
/// </summary>
public static class BuildOutputSlotMatcher
{
    public static IReadOnlyList<SolutionSlot> FindSlots(string solutionSrcFolder)
    {
        var slots = new List<SolutionSlot>();

        var assemblies = Path.Combine(solutionSrcFolder, "PluginAssemblies");
        if (Directory.Exists(assemblies))
            foreach (var dataXml in Directory.EnumerateFiles(assemblies, "*.dll.data.xml", SearchOption.AllDirectories))
            {
                var binary = dataXml[..^".data.xml".Length];
                var key = PluginWebResourceDriftChecker.ReadAssemblySimpleName(dataXml)
                          ?? Path.GetFileNameWithoutExtension(binary);
                slots.Add(new SolutionSlot(SlotKind.PluginAssembly, key, Relative(solutionSrcFolder, binary)));
            }

        var packages = Path.Combine(solutionSrcFolder, "pluginpackages");
        if (Directory.Exists(packages))
            foreach (var dir in Directory.EnumerateDirectories(packages)
                         .Where(d => File.Exists(Path.Combine(d, "pluginpackage.xml"))))
            {
                var uniqueName = PluginPackageContentReader.ReadPackageUniqueName(dir);
                var packageDir = Path.Combine(dir, "package");
                var nupkg = Directory.Exists(packageDir)
                    ? Directory.EnumerateFiles(packageDir, "*.nupkg").Order().FirstOrDefault()
                    : null;
                slots.Add(new SolutionSlot(SlotKind.PluginPackage, uniqueName,
                    Relative(solutionSrcFolder, nupkg ?? Path.Combine(packageDir, uniqueName + ".nupkg"))));
            }

        var webResources = Path.Combine(solutionSrcFolder, "WebResources");
        if (Directory.Exists(webResources))
            foreach (var dataXml in Directory.EnumerateFiles(webResources, "*.data.xml", SearchOption.AllDirectories))
                if (ReadWebResourceName(dataXml) is { } name)
                    slots.Add(new SolutionSlot(SlotKind.WebResource, name,
                        Relative(solutionSrcFolder, dataXml[..^".data.xml".Length])));

        return slots;
    }

    public static SlotMatchResult Match(
        IReadOnlyList<SolutionSlot> slots,
        IReadOnlyList<PluginBuildOutput> plugins,
        IReadOnlyList<WebResourceBuildOutput> webResources,
        string solutionUniqueName)
    {
        var copies = new List<SlotCopy>();
        var unmatched = new List<string>();
        var duplicates = new List<string>();
        var matched = new Dictionary<SolutionSlot, string>();

        void Resolve(string label, string sourcePath, SolutionSlot? slot)
        {
            if (slot is null) { unmatched.Add(label); return; }
            // Case-insensitive names (main.js and MAIN.js) or two projects building one assembly would
            // otherwise both copy to one slot, and whichever copied last would ship.
            if (matched.TryGetValue(slot, out var first)) { duplicates.Add($"{first} and {label} ({slot.Key})"); return; }
            copies.Add(new SlotCopy(sourcePath, slot.BinaryRelativePath));
            matched.Add(slot, label);
        }

        foreach (var p in plugins)
        {
            var slot = p.IsPackage
                ? slots.FirstOrDefault(s => s.Kind == SlotKind.PluginPackage
                    && (Eq(s.Key, p.AssemblyName) || s.Key.EndsWith("_" + p.AssemblyName, StringComparison.OrdinalIgnoreCase)))
                : slots.FirstOrDefault(s => s.Kind == SlotKind.PluginAssembly && Eq(s.Key, p.AssemblyName));
            Resolve(p.AssemblyName, p.FilePath, slot);
        }

        var prefix = FindPrefix(slots, solutionUniqueName);
        foreach (var w in webResources)
        {
            var name = WebResourceReader.IsVerbatimPath(w.RelativePath) ? w.RelativePath
                : prefix is null ? null
                : $"{prefix}_{solutionUniqueName}/{w.RelativePath}";
            var slot = name is null ? null : slots.FirstOrDefault(s => s.Kind == SlotKind.WebResource && Eq(s.Key, name));
            Resolve(w.RelativePath, w.FilePath, slot);
        }

        return new SlotMatchResult(copies, unmatched, slots.Where(s => !matched.ContainsKey(s)).ToList()) { DuplicateOutputs = duplicates };
    }

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    // The publisher prefix is whatever precedes "_<solutionUniqueName>/" in an existing web resource name.
    static string? FindPrefix(IReadOnlyList<SolutionSlot> slots, string solutionUniqueName)
    {
        var marker = "_" + solutionUniqueName + "/";
        foreach (var s in slots.Where(s => s.Kind == SlotKind.WebResource))
        {
            var i = s.Key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && WebResourceReader.IsVerbatimPath(s.Key[..(i + 1)])) return s.Key[..i];
        }
        return null;
    }

    static string? ReadWebResourceName(string dataXmlPath)
    {
        try
        {
            var name = XDocument.Load(dataXmlPath).Descendants("Name").FirstOrDefault()?.Value;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
