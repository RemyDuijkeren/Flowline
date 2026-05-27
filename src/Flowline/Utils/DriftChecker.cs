using System.Security.Cryptography;

namespace Flowline.Utils;

public record DriftWarning(DriftCategory Category, string RelativePath);

public enum DriftCategory { ContentDiffers, NewInDataverse, OnlyLocal, PluginSizeMismatch, OrphanAssembly }

public static class DriftChecker
{
    private const long PluginSizeThresholdBytes = 10 * 1024; // 10 KB

    public static List<DriftWarning> Check(string slnFolder, string? publisherPrefix = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<DriftWarning>();
        warnings.AddRange(CheckWebResources(slnFolder, publisherPrefix, cancellationToken));
        warnings.AddRange(CheckPlugins(slnFolder));
        warnings.AddRange(CheckOrphanAssemblies(slnFolder));
        return warnings;
    }

    static IEnumerable<DriftWarning> CheckWebResources(string slnFolder, string? publisherPrefix, CancellationToken cancellationToken = default)
    {
        var distFolder = Path.Combine(slnFolder, "WebResources", "dist");
        var srcWebFolder = Path.Combine(slnFolder, "src", "WebResources");

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

    static IEnumerable<DriftWarning> CheckPlugins(string slnFolder)
    {
        var releaseFolder = Path.Combine(slnFolder, "Plugins", "bin", "Release");
        var pluginAssembliesFolder = Path.Combine(slnFolder, "src", "PluginAssemblies");

        if (!Directory.Exists(releaseFolder) || !Directory.Exists(pluginAssembliesFolder))
            yield break;

        var srcDlls = Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

        foreach (var releaseDll in Directory.EnumerateFiles(releaseFolder, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(releaseDll);
            if (!srcDlls.TryGetValue(name, out var srcSize))
                continue;

            var releaseSize = new FileInfo(releaseDll).Length;
            if (Math.Abs(releaseSize - srcSize) > PluginSizeThresholdBytes)
                yield return new DriftWarning(DriftCategory.PluginSizeMismatch, name);
        }
    }

    static IEnumerable<DriftWarning> CheckOrphanAssemblies(string slnFolder)
    {
        var pluginAssembliesFolder = Path.Combine(slnFolder, "src", "PluginAssemblies");
        var releaseFolder = Path.Combine(slnFolder, "Plugins", "bin", "Release");

        if (!Directory.Exists(pluginAssembliesFolder) || !Directory.Exists(releaseFolder))
            yield break;

        var releaseDlls = Directory.EnumerateFiles(releaseFolder, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dll);
            if (!name.Equals("Plugins.dll", StringComparison.OrdinalIgnoreCase) && !releaseDlls.Contains(name))
                yield return new DriftWarning(DriftCategory.OrphanAssembly, name);
        }
    }

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
}
