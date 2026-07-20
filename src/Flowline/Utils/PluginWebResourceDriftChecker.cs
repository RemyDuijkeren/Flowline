using System.Security.Cryptography;
using Flowline.Core.Plugins;

namespace Flowline.Utils;

public record DriftWarning(DriftCategory Category, string RelativePath);

public enum DriftCategory { ContentDiffers, NewInDataverse, OnlyLocal, PluginSizeMismatch, OrphanAssembly }

public static class PluginWebResourceDriftChecker
{
    private const long PluginSizeThresholdBytes = 10 * 1024; // 10 KB

    /// <summary>
    /// Compares local build output against the unpacked solution under <paramref name="packageFolder"/>.
    /// </summary>
    /// <remarks>
    /// Async only because plugin build output is located through the solution file now, not a fixed
    /// <c>Plugins/bin/Release</c> — every plugin project the solution references gets checked, whatever it
    /// is named. With no solution file, <see cref="PluginProjectResolver.DiscoverAsync"/> hands back the
    /// conventional folder, so a partially-set-up repo drifts exactly as it did before.
    /// </remarks>
    public static async Task<List<DriftWarning>> CheckAsync(string slnFolder, string packageFolder, string? publisherPrefix = null, CancellationToken cancellationToken = default)
    {
        var releaseFolders = (await PluginProjectResolver.DiscoverAsync(slnFolder, SkipMissingProject, cancellationToken).ConfigureAwait(false))
                             .Select(c => c.BuildOutputRoot)
                             .Where(Directory.Exists)
                             .ToList();

        var warnings = new List<DriftWarning>();
        warnings.AddRange(CheckWebResources(slnFolder, packageFolder, publisherPrefix, cancellationToken));
        warnings.AddRange(CheckPlugins(releaseFolders, packageFolder));
        warnings.AddRange(CheckOrphanAssemblies(releaseFolders, packageFolder));
        return warnings;
    }

    static IEnumerable<DriftWarning> CheckWebResources(string slnFolder, string packageFolder, string? publisherPrefix, CancellationToken cancellationToken = default)
    {
        var distFolder = Path.Combine(slnFolder, "WebResources", "dist");
        var srcWebFolder = Path.Combine(packageFolder, "src", "WebResources");

        if (!Directory.Exists(distFolder) || !Directory.EnumerateFiles(distFolder, "*.*", SearchOption.AllDirectories).Any())
            yield break;

        var distHashes = GetFileHashes(distFolder, cancellationToken);
        var srcHashes = Directory.Exists(srcWebFolder) ? GetWebResourceSrcHashes(srcWebFolder, slnFolder, publisherPrefix, cancellationToken) : new Dictionary<string, byte[]>();

        foreach (var (relPath, srcHash) in srcHashes)
        {
            if (!distHashes.TryGetValue(relPath, out var distHash))
                yield return new DriftWarning(DriftCategory.NewInDataverse, relPath);
            else if (!srcHash.SequenceEqual(distHash))
                yield return new DriftWarning(DriftCategory.ContentDiffers, relPath);
        }

        foreach (var relPath in distHashes.Keys.Where(k => !srcHashes.ContainsKey(k)))
            yield return new DriftWarning(DriftCategory.OnlyLocal, relPath);
    }

    static Dictionary<string, byte[]> GetWebResourceSrcHashes(string srcWebFolder, string slnFolder, string? publisherPrefix, CancellationToken cancellationToken = default)
    {
        var solutionName = Path.GetFileName(slnFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var pattern = publisherPrefix != null ? $"{publisherPrefix}_{solutionName}" : $"*_{solutionName}";
        var publisherRoot = Directory.EnumerateDirectories(srcWebFolder, pattern, SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(srcWebFolder, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)) continue;

            var sourceRoot = publisherRoot != null && file.StartsWith(publisherRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? publisherRoot
                : srcWebFolder;

            var relPath = Path.GetRelativePath(sourceRoot, file);
            using var fs = File.OpenRead(file);
            result[relPath] = SHA256.HashData(fs);
        }
        return result;
    }

    static IEnumerable<DriftWarning> CheckPlugins(IReadOnlyList<string> releaseFolders, string packageFolder)
    {
        var pluginAssembliesFolder = Path.Combine(packageFolder, "src", "PluginAssemblies");

        if (releaseFolders.Count == 0 || !Directory.Exists(pluginAssembliesFolder))
            yield break;

        var srcDlls = Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

        foreach (var releaseDll in EnumerateReleaseDlls(releaseFolders))
        {
            var name = Path.GetFileName(releaseDll);
            if (!srcDlls.TryGetValue(name, out var srcSize))
                continue;

            var releaseSize = new FileInfo(releaseDll).Length;
            if (Math.Abs(releaseSize - srcSize) > PluginSizeThresholdBytes)
                yield return new DriftWarning(DriftCategory.PluginSizeMismatch, name);
        }
    }

    static IEnumerable<DriftWarning> CheckOrphanAssemblies(IReadOnlyList<string> releaseFolders, string packageFolder)
    {
        var pluginAssembliesFolder = Path.Combine(packageFolder, "src", "PluginAssemblies");

        if (!Directory.Exists(pluginAssembliesFolder) || releaseFolders.Count == 0)
            yield break;

        // Every discovered project's output, not one folder's — an assembly is only an orphan when no
        // plugin project in the solution produces it. The old code also exempted the literal name
        // "Plugins.dll" from ever being reported; that exemption is gone, because the assembly a project
        // actually builds is now known. It was the wrong shape twice over: it exempted nothing for a
        // project with its own <AssemblyName>, and it hid the one case worth reporting — a stale
        // Plugins.dll left in the unpacked solution after the project was renamed.
        var releaseDlls = EnumerateReleaseDlls(releaseFolders)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dll);
            if (!releaseDlls.Contains(name))
                yield return new DriftWarning(DriftCategory.OrphanAssembly, name);
        }
    }

    static IEnumerable<string> EnumerateReleaseDlls(IReadOnlyList<string> releaseFolders) =>
        releaseFolders.SelectMany(f => Directory.EnumerateFiles(f, "*.dll", SearchOption.AllDirectories));

    static Dictionary<string, byte[]> GetFileHashes(string baseFolder, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(baseFolder, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(baseFolder, file);
            using var fs = File.OpenRead(file);
            result[relPath] = SHA256.HashData(fs);
        }
        return result;
    }

    /// <summary>Ignore a solution entry whose project is gone, rather than failing this path over it.</summary>
    /// <remarks>
    /// Scoping and advisory work only — none of it builds the project. `push` keeps the throw, so a broken
    /// solution file is still reported loudly by the command that actually cares.
    /// </remarks>
    static void SkipMissingProject(string _) { }

}
