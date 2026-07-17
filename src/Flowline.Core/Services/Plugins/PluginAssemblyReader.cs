using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services.Plugins;

public class PluginAssemblyReader(IAnsiConsole console)
{
    public PluginAssemblyMetadata Analyze(string dllPath)
    {
        var resolver = new PathAssemblyResolver(BuildResolverPaths(dllPath).Distinct());
        using var mlc = new MetadataLoadContext(resolver);

        var assembly = mlc.LoadFromAssemblyPath(dllPath);
        var content = File.ReadAllBytes(dllPath);
        var metadata = BuildAssemblyMetadata(assembly, content, new PluginTypeMetadataScanner(console).ScanPluginTypes(assembly));

        console.Info($"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) analyzed");

        return metadata;
    }

    // Reflects every DLL in a .nupkg's lib/<tfm>/ folder (a .nupkg is a standard OPC zip package),
    // returning one PluginAssemblyMetadata per DLL that contains at least one class implementing
    // IPlugin — mirroring the exact filter Dataverse itself applies when auto-creating pluginassembly
    // records (KD5, KTD8). DLLs with no IPlugin-derived type (pure dependencies, e.g. Newtonsoft.Json)
    // are skipped entirely — no separate classifier needed. Zero plugin-bearing DLLs is not rejected
    // here (R3a) — that's the caller's responsibility.
    public List<PluginAssemblyMetadata> AnalyzePackage(string nupkgPath)
    {
        var tempDir = Directory.CreateTempSubdirectory("flowline-nupkg-").FullName;
        try
        {
            ExtractLibDlls(nupkgPath, tempDir);

            // The plugins project's SDK reference (Microsoft.Xrm.Sdk et al) is typically excluded from
            // the .nupkg's own lib/<tfm>/ content (PrivateAssets="All" — that's what keeps a redundant
            // SDK copy out of the package) but is still copy-local in the build output, since that
            // exclusion only affects packaging, not the local build output. A real `dotnet pack` build
            // drops the .nupkg directly in bin/Release/ while the copy-local DLLs live one level down
            // (e.g. bin/Release/net462/, plus its own net462/publish/ sub-copy) — never flat alongside
            // the .nupkg itself — so the search must recurse under the .nupkg's directory, not just
            // scan it. Recursing can surface the same assembly under both net462/ and net462/publish/,
            // which would make PathAssemblyResolver throw on a duplicate simple name — dedup by filename
            // first. Widening the resolver this way lets MetadataLoadContext resolve those types without
            // needing them bundled inside the package.
            var nupkgDir = Path.GetDirectoryName(Path.GetFullPath(nupkgPath));
            var siblingDlls = nupkgDir != null && Directory.Exists(nupkgDir)
                ? DedupeSiblingDlls(Directory.EnumerateFiles(nupkgDir, "*.dll", SearchOption.AllDirectories))
                : [];

            var results = new List<PluginAssemblyMetadata>();
            var codeActivityFindings = new List<string>();
            var scanner = new PluginTypeMetadataScanner(console);

            foreach (var dllPath in Directory.EnumerateFiles(tempDir, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                // BuildResolverPaths already searches the whole directory containing dllPath — since
                // every DLL from lib/<tfm>/ is extracted into that same directory, cross-DLL type
                // references within the package resolve without any extra widening (KD5).
                //
                // A packing tool can bundle a PrivateAssets="All" dependency (e.g. Flowline.Attributes)
                // into the .nupkg's own lib/<tfm>/ content anyway — PrivateAssets governs NuGet dependency
                // metadata, not which files a custom pack target physically copies into lib/. When that
                // happens, the same assembly identity is registered twice: once from tempDir (the
                // package's own extracted copy) and once from siblingDlls (the copy-local build-output
                // copy, already deduped and drift-checked by DedupeSiblingDlls) — MetadataLoadContext
                // throws "already been loaded" on the second load, since Distinct() only dedupes by exact
                // path string, not by filename/identity. Dedup the combined list by filename, keeping
                // tempDir's entry (GroupBy preserves Concat's order, so a name collision resolves to what
                // the package itself actually carries).
                var resolver = new PathAssemblyResolver(
                    BuildResolverPaths(dllPath).Concat(siblingDlls)
                        .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First()));
                using var mlc = new MetadataLoadContext(resolver);
                var assembly = mlc.LoadFromAssemblyPath(dllPath);
                var pluginTypes = scanner.ScanPluginTypes(assembly);

                // Skip DLLs with no class implementing IPlugin — this is what filters out pure-dependency
                // DLLs without any new classifier logic (KD5, KTD8). ScanPluginTypes only ever adds an
                // entry for a type that is IPlugin-derived or CodeActivity-derived (or both would be
                // impossible per its own guard), so "any non-workflow entry" means "any real IPlugin type".
                if (!pluginTypes.Any(p => !p.IsWorkflow)) continue;

                var content = File.ReadAllBytes(dllPath);
                var metadata = BuildAssemblyMetadata(assembly, content, pluginTypes);

                console.Info($"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) analyzed");

                foreach (var workflowType in pluginTypes.Where(p => p.IsWorkflow))
                    codeActivityFindings.Add($"{workflowType.FullName} (in {Path.GetFileName(dllPath)})");

                results.Add(metadata);
            }

            if (codeActivityFindings.Count > 0)
                throw new InvalidOperationException(
                    $"Plugin package contains workflow activity type(s), which are not supported for pluginpackage " +
                    $"deployment — move them to a separate project for classic DLL deployment: {string.Join(", ", codeActivityFindings)}.");

            return results;
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // dotnet publish copies the whole dependency closure into net462/publish/, duplicating filenames
    // already present in net462/ itself — an expected, common occurrence, not an anomaly, so a bare
    // filename collision must not fail loudly. But a stale prior build can leave net462/ and
    // net462/publish/ holding genuinely different content under the same filename (version drift);
    // picking an arbitrary one (enumeration order is unspecified) would then silently resolve some
    // packaged DLL's dependencies against the wrong version. Only the narrower drift case fails loud,
    // mirroring PushCommand.ResolvePluginPushPath's ambiguous-.nupkg guard; identical duplicates dedupe
    // silently. Also fixes the previous double-dedup: this runs once against a materialized list,
    // instead of re-walking and re-deduping siblingDlls on every DLL in AnalyzePackage's loop.
    private static List<string> DedupeSiblingDlls(IEnumerable<string> dllPaths)
    {
        var result = new List<string>();
        foreach (var group in dllPaths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var paths = group.ToList();
            if (paths.Count == 1)
            {
                result.Add(paths[0]);
                continue;
            }

            var distinctHashes = paths
                .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
                .Distinct()
                .ToList();
            if (distinctHashes.Count > 1)
                throw new InvalidOperationException(
                    $"Found {paths.Count} copies of '{group.Key}' with different content under the build output " +
                    $"({string.Join(", ", paths)}) — run a clean build so only one version of each dependency remains.");

            result.Add(paths[0]);
        }
        return result;
    }

    // Extracts every *.dll under lib/ from a .nupkg (OPC zip) into destinationDir, preserving the
    // lib/<tfm>/ subfolder structure so DLLs from the same tfm land in the same directory.
    private static void ExtractLibDlls(string nupkgPath, string destinationDir)
    {
        using var archive = ZipFile.OpenRead(nupkgPath);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) ||
                !entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var destPath = Path.Combine(destinationDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static PluginAssemblyMetadata BuildAssemblyMetadata(Assembly assembly, byte[] content, List<PluginTypeMetadata> pluginTypes)
    {
        var assemblyName = assembly.GetName();
        var hash = Convert.ToHexString(SHA256.HashData(content));

        var pktBytes = assemblyName.GetPublicKeyToken();
        var publicKeyToken = pktBytes is { Length: > 0 }
            ? Convert.ToHexString(pktBytes).ToLowerInvariant()
            : null;
        var culture = string.IsNullOrEmpty(assemblyName.CultureName) ? "neutral" : assemblyName.CultureName;

        return new PluginAssemblyMetadata(
            assemblyName.Name!,
            assemblyName.FullName,
            content,
            hash,
            assemblyName.Version!.ToString(),
            publicKeyToken,
            culture,
            pluginTypes);
    }

    private static List<string> BuildResolverPaths(string dllPath)
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var paths = new List<string>(Directory.GetFiles(runtimeDir, "*.dll"));

        var assemblyDir = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(assemblyDir) && Directory.Exists(assemblyDir))
        {
            paths.AddRange(Directory.EnumerateFiles(assemblyDir, "*.dll", SearchOption.AllDirectories));

            var parentDir = Directory.GetParent(assemblyDir)?.FullName;
            if (parentDir != null && Directory.Exists(parentDir))
                paths.AddRange(Directory.EnumerateFiles(parentDir, "*.dll", SearchOption.TopDirectoryOnly));

            // Look for a 'lib' or 'ref' folder in the assembly directory or its parent
            // This is useful for providing .NET Framework reference assemblies on Linux.
            string[] libFolderNames = ["lib", "ref", "external"];
            var searchDirs = new List<string> { assemblyDir };
            if (parentDir != null) searchDirs.Add(parentDir);

            foreach (var searchDir in searchDirs)
                foreach (var libName in libFolderNames)
                {
                    var libPath = Path.Combine(searchDir, libName);
                    if (Directory.Exists(libPath))
                        paths.AddRange(Directory.EnumerateFiles(libPath, "*.dll", SearchOption.AllDirectories));
                }
        }

        // Include GAC (Global Assembly Cache) for .NET Framework assemblies like System.Activities
        // This only works on Windows where .NET Framework is installed.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var windir = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(windir))
            {
                var gacPath = Path.Combine(windir, "Microsoft.NET", "assembly");
                if (Directory.Exists(gacPath))
                {
                    // We don't want to load every single DLL in the GAC as it's huge.
                    // But we can add common dependencies if they are not already in paths.
                    string[] commonGacAssemblies = ["System.Activities.dll", "System.Runtime.Serialization.dll", "System.ServiceModel.dll"];
                    foreach (var gacAssemblyName in commonGacAssemblies)
                    {
                        if (paths.All(p => !p.Contains(gacAssemblyName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var foundPath = Directory.EnumerateFiles(gacPath, gacAssemblyName, SearchOption.AllDirectories).FirstOrDefault();
                            if (foundPath != null)
                                paths.Add(foundPath);
                        }
                    }
                }
            }
        }

        paths.Add(dllPath);
        return paths;
    }
}
