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
    // warnings landing in deploy/orphan-cleanup output. Shared, so no consumer declares its own.
    public static readonly IAnsiConsole DiscardConsole =
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) });

    // Fix 5: both consumers run at different points of one deploy against the same unpacked directory,
    // so reflecting a package twice is pure waste (extract to temp + MetadataLoadContext, per package).
    // Keyed by the package directory's full path — the finest key that still makes both consumers hit,
    // since the solution src root is the only thing above it that varies.
    // ponytail: unbounded and process-lifetime, which is right for a CLI that deploys once per process;
    // a long-lived host would want eviction or a per-run instance instead.
    static readonly Dictionary<string, List<PluginAssemblyMetadata>?> ReflectionCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Tests reuse one process across cases and xUnit parallelizes classes — a fixture rewriting the same
    // directory must not read a previous case's reflection.
    internal static void ClearCache()
    {
        lock (ReflectionCache) ReflectionCache.Clear();
    }

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
    // below reports it as a failed package). Propagates AnalyzePackage's own exceptions (a workflow
    // activity type, a malformed .nupkg) uncaught — same as before this was lifted out of the check
    // service. A throwing package isn't cached: it's rare, and re-throwing costs what caching would.
    public static List<PluginAssemblyMetadata>? ReflectPackageContent(string packageDir, PluginAssemblyReader reader)
    {
        var key = Path.GetFullPath(packageDir);
        lock (ReflectionCache)
            if (ReflectionCache.TryGetValue(key, out var cached)) return cached;

        var nupkgDir = Path.Combine(packageDir, "package");
        var nupkgPath = Directory.Exists(nupkgDir)
            ? Directory.EnumerateFiles(nupkgDir, "*.nupkg").FirstOrDefault()
            : null;
        var result = nupkgPath == null ? null : reader.AnalyzePackage(nupkgPath);

        lock (ReflectionCache) ReflectionCache[key] = result;
        return result;
    }

    // U5/KTD7: for every plug-in package the imported solution still carries (a pluginpackages/<name>
    // directory exists under dataverseSolutionSrcRoot), the simple names of its reflected plugin-bearing
    // assemblies — the content-based authority PluginAssemblyFamilyHandler's manifest-based test (the
    // type-91 portable-name match in ComponentClassifier) can't see on its own. Computed once per caller
    // invocation, not once per candidate.
    //
    // Fix 1: a package directory this can't read or reflect is reported through Failures, never
    // swallowed. "Absent from the map" and "couldn't be read" are opposite facts for a caller deciding
    // whether to delete a package, and the bare catch this replaces made them indistinguishable.
    // A directory with no .nupkg inside counts as a failure too, not as an empty package — the same
    // reading PluginPackageAssemblyCheckService already gives that state. It's the wider of the two
    // choices, since one such directory defers every package delete in the run, and it's the one that
    // can't destroy anything.
    public static (Dictionary<string, HashSet<string>> ByPackage, List<(string PackageDir, Exception Error)> Failures)
        ScanReflectedAssemblyNamesByPackage(string dataverseSolutionSrcRoot)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<(string PackageDir, Exception Error)>();

        var packagesRoot = Path.Combine(dataverseSolutionSrcRoot, "pluginpackages");
        if (!Directory.Exists(packagesRoot)) return (result, failures);

        var reader = new PluginAssemblyReader(DiscardConsole);

        foreach (var packageDir in Directory.GetDirectories(packagesRoot))
        {
            try
            {
                var uniqueName = ReadPackageUniqueName(packageDir);
                var reflected = ReflectPackageContent(packageDir, reader)
                    ?? throw new InvalidOperationException("no .nupkg under its unpacked package folder");

                result[uniqueName] = reflected.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                failures.Add((Path.GetFileName(packageDir), ex));
            }
        }

        return (result, failures);
    }
}
