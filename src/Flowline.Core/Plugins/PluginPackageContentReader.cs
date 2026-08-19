using System.Xml.Linq;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Plugins;

// U5/KTD7: the pluginpackages/<uniquename>/package/*.nupkg walk PluginPackageAssemblyCheckService
// already performs (KTD2/KTD4), lifted out so the orphan classifier can read the identical per-package
// reflected-assembly identity without a second reflection pass per candidate. Package content, not
// Solution.xml's manifest, is the authority both consumers need: PluginPackageAssemblyCheckService
// checks it against the target's registrations, PluginAssemblyFamilyHandler checks it against a
// package-owned assembly's orphan candidacy.
public static class PluginPackageContentReader
{
    // KTD4: neither caller wants this reader's push-time "analyzed" line or the type scanner's own
    // warnings landing in deploy/orphan-cleanup output.
    static readonly IAnsiConsole DiscardConsole =
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) });

    // Verified 2026-08-19 against a real export: the unique name lives on the root element as a
    // uniquename attribute (and again as a <name> child) — read the XML rather than trusting the
    // containing directory's own name.
    public static string ReadPackageUniqueName(string packageDir)
    {
        var xmlPath = Path.Combine(packageDir, "pluginpackage.xml");
        var doc = XDocument.Load(xmlPath);
        var uniqueName = doc.Root?.Attribute("uniquename")?.Value;
        if (string.IsNullOrWhiteSpace(uniqueName))
            throw new InvalidOperationException($"pluginpackage.xml has no uniquename attribute ({xmlPath}).");
        return uniqueName;
    }

    // Locates <packageDir>/package/*.nupkg and reflects it with the given reader (KTD4). Returns null
    // when no .nupkg is present under the unpacked package folder — callers decide what that means for
    // them (PluginPackageAssemblyCheckService warns per-package; ScanReflectedAssemblyNamesByPackage
    // below just omits the package). Propagates AnalyzePackage's own exceptions (a workflow activity
    // type, a malformed .nupkg) uncaught — same as before this was lifted out of the check service.
    public static List<PluginAssemblyMetadata>? ReflectPackageContent(string packageDir, PluginAssemblyReader reader)
    {
        var nupkgDir = Path.Combine(packageDir, "package");
        var nupkgPath = Directory.Exists(nupkgDir)
            ? Directory.EnumerateFiles(nupkgDir, "*.nupkg").FirstOrDefault()
            : null;
        return nupkgPath == null ? null : reader.AnalyzePackage(nupkgPath);
    }

    // U5/KTD7: for every plug-in package the imported solution still carries (a pluginpackages/<name>
    // directory exists under dataverseSolutionSrcRoot), the simple names of its reflected plugin-bearing
    // assemblies — the content-based authority PluginAssemblyFamilyHandler's manifest-based test (the
    // type-91 portable-name match in ComponentClassifier) can't see on its own. Non-throwing per package:
    // AnalyzePackage can throw (a workflow activity type, a malformed .nupkg), and a package this can't
    // reflect must not silently widen the exclusion — it's simply absent from the result, the same as a
    // package the imported solution doesn't carry at all (R12). Computed once per caller invocation, not
    // once per candidate — multiple orphan candidates under the same package share one reflection pass.
    public static Dictionary<string, HashSet<string>> ScanReflectedAssemblyNamesByPackage(string dataverseSolutionSrcRoot)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var packagesRoot = Path.Combine(dataverseSolutionSrcRoot, "pluginpackages");
        if (!Directory.Exists(packagesRoot)) return result;

        var reader = new PluginAssemblyReader(DiscardConsole);

        foreach (var packageDir in Directory.GetDirectories(packagesRoot))
        {
            try
            {
                var uniqueName = ReadPackageUniqueName(packageDir);
                var reflected = ReflectPackageContent(packageDir, reader);
                if (reflected == null) continue;

                result[uniqueName] = reflected.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Non-throwing (KTD7): a package this can't read or reflect contributes nothing.
            }
        }

        return result;
    }
}
